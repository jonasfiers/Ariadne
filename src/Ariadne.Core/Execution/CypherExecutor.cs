using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Ariadne.Core.Parameters;
using Ariadne.Core.Results;
using Neo4j.Driver;

namespace Ariadne.Core.Execution;

/// <summary>
/// The execution layer — the part that actually runs a Cypher statement (connection spec §2/§3/§9). It wires
/// together everything the earlier features built: the process-lifetime driver singleton (Feature 08
/// <see cref="DriverCache"/>) → a per-call <see cref="IAsyncSession"/> → the right transaction mode → a
/// cursor → the <c>RecordsJson</c> envelope (Feature 06 <see cref="RecordsJsonBuilder"/>) + the typed
/// <c>CypherSummary</c> (Feature 07 <see cref="CypherSummaryMapper"/>), converting the caller's typed
/// parameters (Features 01-03 <see cref="CypherParameterMapper"/>) to the driver's dictionary, blocking the
/// async driver at the outermost boundary via the Feature 08 <see cref="AsyncBridge"/>, and mapping driver
/// exceptions to named, developer-vs-operational errors.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three execution modes (spec §3).</b> Reads and writes run through <b>managed transaction functions</b>
/// (<see cref="IAsyncSession.ExecuteReadAsync{T}(System.Func{IAsyncQueryRunner,System.Threading.Tasks.Task{T}},System.Action{TransactionConfigBuilder})"/>
/// / <c>ExecuteWriteAsync</c>), which add cluster routing (reads → replicas, writes → leader) and
/// <b>automatic retry</b> of transient errors up to the driver's <c>MaxTransactionRetryTime</c>. Auto-commit
/// (<see cref="IAsyncQueryRunner.RunAsync(string,System.Collections.Generic.IDictionary{string,object})"/> on
/// the session itself) gets neither — it exists only for statements that <i>cannot</i> run inside a managed
/// transaction (<c>CALL { … } IN TRANSACTIONS</c>, some admin commands).
/// </para>
/// <para>
/// <b>The cursor is fully drained inside the transaction function.</b> A managed transaction's cursor must be
/// consumed before the function returns, so <see cref="RunWorkAsync"/> materializes the records
/// (<c>ToListAsync</c>) and obtains the summary (<c>ConsumeAsync</c>) — and the columns (<c>KeysAsync</c>) —
/// inside the lambda, returning only already-materialized data out of it. The exact same lambda drives the
/// auto-commit path (a session <i>is</i> an <see cref="IAsyncQueryRunner"/>), so all three modes share one
/// cursor-handling path.
/// </para>
/// <para>
/// <b>Real driver API verified by reflection (Neo4j.Driver 5.28.3):</b>
/// <c>IAsyncSession.ExecuteReadAsync&lt;T&gt;(Func&lt;IAsyncQueryRunner,Task&lt;T&gt;&gt;)</c> /
/// <c>ExecuteWriteAsync&lt;T&gt;</c>; <c>IAsyncQueryRunner.RunAsync(string, IDictionary&lt;string,object&gt;)</c>
/// (the session inherits it for auto-commit); <c>ResultCursorExtensions.ToListAsync(IResultCursor)</c> →
/// <c>Task&lt;List&lt;IRecord&gt;&gt;</c>; <c>IResultCursor.ConsumeAsync()</c> → <c>Task&lt;IResultSummary&gt;</c>;
/// <c>IResultCursor.KeysAsync()</c> → <c>Task&lt;string[]&gt;</c>; <c>IDriver.AsyncSession(Action&lt;SessionConfigBuilder&gt;)</c>
/// with <c>SessionConfigBuilder.WithDatabase(string)</c>.
/// </para>
/// <para>
/// <b>Deliberately not offered:</b> developer-managed multi-call transactions (a <c>BeginTransaction</c> in
/// one action, <c>Commit</c> in another) — a documented non-goal (spec §4). Each method here is exactly one
/// transaction.
/// </para>
/// </remarks>
public sealed class CypherExecutor
{
    private readonly DriverCache _cache;

    /// <summary>Creates the executor over a shared driver cache.</summary>
    /// <param name="cache">The process-lifetime driver cache (Feature 08). Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is null.</exception>
    public CypherExecutor(DriverCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Runs a read query in a <b>managed read transaction</b> (<c>ExecuteReadAsync</c>): routes to followers /
    /// read replicas on a cluster and automatically retries transient errors. Use this for anything that only
    /// reads (<c>MATCH … RETURN</c>).
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <param name="query">The Cypher statement. Must not be null, empty, or whitespace.</param>
    /// <param name="parameters">The typed parameters to bind. Must not be null (may be empty).</param>
    /// <returns>The records JSON, the columns, and the typed summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/>, <paramref name="query"/>, or <paramref name="parameters"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is empty or whitespace.</exception>
    /// <exception cref="CypherParameterException">A parameter is invalid (fail-loud, from the parameter mapper).</exception>
    /// <exception cref="ConnectionException">The configuration is invalid (e.g. an unsupported auth scheme).</exception>
    /// <exception cref="CypherExecutionException">A driver error occurred, mapped to a named developer/operational error (spec §9).</exception>
    /// <exception cref="CypherResultException">A returned value is unserializable (fail-loud, from the records builder).</exception>
    public CypherExecutionResult RunCypherRead(ConnConfig config, string query, IEnumerable<CypherParameter> parameters) =>
        Execute(config, query, parameters, ExecutionMode.Read);

    /// <summary>
    /// Runs a write query in a <b>managed write transaction</b> (<c>ExecuteWriteAsync</c>): routes to the
    /// cluster leader and automatically retries transient errors. Use this for
    /// <c>CREATE</c>/<c>MERGE</c>/<c>SET</c>/<c>DELETE</c>.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <param name="query">The Cypher statement. Must not be null, empty, or whitespace.</param>
    /// <param name="parameters">The typed parameters to bind. Must not be null (may be empty).</param>
    /// <returns>The records JSON, the columns, and the typed summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/>, <paramref name="query"/>, or <paramref name="parameters"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is empty or whitespace.</exception>
    /// <exception cref="CypherParameterException">A parameter is invalid (fail-loud, from the parameter mapper).</exception>
    /// <exception cref="ConnectionException">The configuration is invalid (e.g. an unsupported auth scheme).</exception>
    /// <exception cref="CypherExecutionException">A driver error occurred, mapped to a named developer/operational error (spec §9).</exception>
    /// <exception cref="CypherResultException">A returned value is unserializable (fail-loud, from the records builder).</exception>
    public CypherExecutionResult RunCypherWrite(ConnConfig config, string query, IEnumerable<CypherParameter> parameters) =>
        Execute(config, query, parameters, ExecutionMode.Write);

    /// <summary>
    /// Runs a query as an <b>auto-commit (implicit) transaction</b> (<c>session.RunAsync</c>): <b>no routing
    /// benefit and no automatic retry</b>. Use this <i>only</i> for statements that cannot run inside a
    /// managed transaction — <c>CALL { … } IN TRANSACTIONS</c> (batched import) and certain administrative
    /// commands. Prefer <see cref="RunCypherRead"/>/<see cref="RunCypherWrite"/> for everything else.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <param name="query">The Cypher statement. Must not be null, empty, or whitespace.</param>
    /// <param name="parameters">The typed parameters to bind. Must not be null (may be empty).</param>
    /// <returns>The records JSON, the columns, and the typed summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/>, <paramref name="query"/>, or <paramref name="parameters"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="query"/> is empty or whitespace.</exception>
    /// <exception cref="CypherParameterException">A parameter is invalid (fail-loud, from the parameter mapper).</exception>
    /// <exception cref="ConnectionException">The configuration is invalid (e.g. an unsupported auth scheme).</exception>
    /// <exception cref="CypherExecutionException">A driver error occurred, mapped to a named developer/operational error (spec §9).</exception>
    /// <exception cref="CypherResultException">A returned value is unserializable (fail-loud, from the records builder).</exception>
    public CypherExecutionResult RunCypherAutoCommit(ConnConfig config, string query, IEnumerable<CypherParameter> parameters) =>
        Execute(config, query, parameters, ExecutionMode.AutoCommit);

    private CypherExecutionResult Execute(
        ConnConfig config, string query, IEnumerable<CypherParameter> parameters, ExecutionMode mode)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Cypher query must not be empty or whitespace.", nameof(query));
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));

        // Fail-loud parameter mapping. A bad parameter is the caller's error and surfaces as
        // CypherParameterException (unwrapped) — it never reaches the network.
        IDictionary<string, object?> driverParameters = CypherParameterMapper.BuildParameters(parameters);

        // A configuration error (unsupported auth scheme) surfaces loudly as ConnectionException, outside the
        // driver-exception mapping below — it is a programmer error, not an execution failure.
        IDriver driver = _cache.GetDriver(config);

        try
        {
            // One blocking hop at the outermost boundary (spec §2). AsyncBridge re-throws the ORIGINAL driver
            // exception (never an AggregateException), which the §9 mapping below relies on.
            return AsyncBridge.RunSync(ExecuteAsync(driver, config, query, driverParameters, mode));
        }
        catch (Neo4jException ex)
        {
            throw MapDriverException(ex, config.Uri, mode);
        }
    }

    private static async Task<CypherExecutionResult> ExecuteAsync(
        IDriver driver, ConnConfig config, string query, IDictionary<string, object?> parameters, ExecutionMode mode)
    {
        // Session per unit of work (spec §1), disposed at the end of this call. WithDatabase only when a
        // database is configured; otherwise the driver's default database is used.
        //
        // Synchronous `using` (not `await using`) is deliberate: on netstandard2.0 the `await using` lowering
        // hard-references Microsoft.Bcl.AsyncInterfaces, which the net10 test host omits as "in-box" (breaking
        // the fakes at runtime). The driver's own IDisposable.Dispose() closes the session by blocking on
        // CloseAsync().GetAwaiter().GetResult() — the identical sync-over-async pattern the connector already
        // uses via AsyncBridge (§2), so cleanup is equivalent and no extra package is dragged in.
        using IAsyncSession session = driver.AsyncSession(o =>
        {
            if (config.Database != null)
            {
                o.WithDatabase(config.Database);
            }
        });

        // The single cursor-handling path, shared by all three modes: run the query, then drain the cursor
        // (keys, records, summary) BEFORE returning — mandatory inside a managed transaction function, and
        // harmless for auto-commit.
        Func<IAsyncQueryRunner, Task<TxOutput>> work = runner => RunWorkAsync(runner, query, parameters);

        TxOutput output;
        switch (mode)
        {
            case ExecutionMode.Read:
                output = await session.ExecuteReadAsync(work).ConfigureAwait(false);
                break;
            case ExecutionMode.Write:
                output = await session.ExecuteWriteAsync(work).ConfigureAwait(false);
                break;
            case ExecutionMode.AutoCommit:
                // A session IS an IAsyncQueryRunner; calling it directly is the implicit/auto-commit tx
                // (no managed-tx wrapper → no routing, no retry).
                output = await work(session).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown execution mode.");
        }

        // Pure serialization (Features 06/07), over already-materialized data — no cursor/session touched here.
        string json = RecordsJsonBuilder.BuildRecordsJson(output.Records);
        CypherSummary summary = CypherSummaryMapper.Map(output.Summary);
        return new CypherExecutionResult(json, output.Keys, summary);
    }

    private static async Task<TxOutput> RunWorkAsync(
        IAsyncQueryRunner runner, string query, IDictionary<string, object?> parameters)
    {
        // The driver's parameter dictionary is IDictionary<string, object>; object? and object are the same CLR
        // type (nullable annotations are erased), and the mapper already produced driver-legal values.
        IResultCursor cursor = await runner
            .RunAsync(query, (IDictionary<string, object>)parameters!)
            .ConfigureAwait(false);

        // Columns come from the cursor's Keys — authoritative even for a zero-row result (a projection that
        // matched nothing still reports its column names).
        string[] keys = await cursor.KeysAsync().ConfigureAwait(false);

        // Materialize all records, THEN consume for the summary — the required order (ToListAsync drains the
        // stream; ConsumeAsync must follow). Both happen before this function returns.
        List<IRecord> records = await cursor.ToListAsync().ConfigureAwait(false);
        IResultSummary summary = await cursor.ConsumeAsync().ConfigureAwait(false);

        return new TxOutput(records, keys, summary);
    }

    /// <summary>
    /// Maps a driver <see cref="Neo4jException"/> to a named, classified <see cref="CypherExecutionException"/>
    /// (spec §9). Developer errors (bad Cypher / constraint = <see cref="ClientException"/>) surface the Neo4j
    /// code and message <b>verbatim</b>; operational errors surface a friendly, <b>credential-free</b> message
    /// built only from the URI and the Neo4j status code. By the time a transient error reaches here the
    /// managed-transaction retries were <i>exhausted</i> (for read/write); the auto-commit path never retried —
    /// the message says which.
    /// </summary>
    private static CypherExecutionException MapDriverException(Neo4jException ex, string? uri, ExecutionMode mode)
    {
        string retried = mode == ExecutionMode.AutoCommit
            ? "in an auto-commit statement (no retry)"
            : "after retries were exhausted";

        switch (ex)
        {
            // A ClientException with NO Neo.ClientError.* code isn't a Cypher/constraint error — it's a
            // client-side OPERATIONAL condition (e.g. connection-acquisition timeout / pool exhaustion,
            // which the driver reports as a bare ClientException). Real server developer errors always
            // carry a Neo.ClientError.* code, so an empty code is a safe discriminator. (§9: acquisition
            // timeout is operational.) Must precede the general ClientException arm.
            case ClientException client when string.IsNullOrEmpty(client.Code):
                return CypherExecutionException.Operational(
                    $"Neo4j client-side error: {client.Message}", client.Code, client);

            // ClientException with a code: a developer error; several client-error subtypes exist, so catching
            // the base here surfaces them all verbatim. (Bad Cypher, constraint violation, argument errors.)
            case ClientException client:
                return CypherExecutionException.Developer(
                    $"{client.Code}: {client.Message}", client.Code, client);

            // AuthenticationException is a SecurityException; match it before the generic fallback so a bad
            // credential yields a clean operational message with NO hint about which field was wrong.
            case AuthenticationException auth:
                return CypherExecutionException.Operational(
                    "Neo4j authentication failed.", auth.Code, auth);

            case ServiceUnavailableException svc:
                return CypherExecutionException.Operational(
                    $"Cannot reach Neo4j at {uri}.", svc.Code, svc);

            case SessionExpiredException expired:
                return CypherExecutionException.Operational(
                    $"Neo4j routing/session error ({retried}).", expired.Code, expired);

            case TransientException transient:
                return CypherExecutionException.Operational(
                    $"Neo4j transient error persisted ({retried}): {transient.Code}.", transient.Code, transient);

            case ConnectionReadTimeoutException timeout:
                return CypherExecutionException.Operational(
                    "Neo4j connection timed out (read timeout).", timeout.Code, timeout);

            case DatabaseException db:
                return CypherExecutionException.Operational(
                    $"Neo4j server error: {db.Code}.", db.Code, db);

            default:
                // Any other Neo4jException — named and operational, code only, never a credential.
                return CypherExecutionException.Operational(
                    $"Neo4j error: {ex.Code}.", ex.Code, ex);
        }
    }

    private enum ExecutionMode
    {
        Read,
        Write,
        AutoCommit,
    }

        /// <summary>The already-materialized outputs handed back out of the transaction function.</summary>
    private readonly struct TxOutput
    {
        public TxOutput(IReadOnlyList<IRecord> records, string[] keys, IResultSummary summary)
        {
            Records = records;
            Keys = keys;
            Summary = summary;
        }

        public IReadOnlyList<IRecord> Records { get; }

        public string[] Keys { get; }

        public IResultSummary Summary { get; }
    }
}
