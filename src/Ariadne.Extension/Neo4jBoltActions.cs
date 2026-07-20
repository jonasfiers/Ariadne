using System;
using System.Collections.Generic;
using System.Linq;
using Ariadne.Core.Connection;
using Ariadne.Core.Execution;
using Ariadne.Core.Parameters;
using Ariadne.Core.Results;

namespace Ariadne.Extension;

/// <summary>
/// The Integration Studio action surface for the Neo4j Bolt connector. Every method is shaped the way
/// OutSystems expects an extension action to look: a Boolean success result, input parameters first, output
/// parameters as <c>out</c> arguments, and — the cardinal rule — <b>no exception ever crosses this
/// boundary</b>. A failure comes back as <c>false</c> plus a populated <c>errorMessage</c>, never as a thrown
/// exception. This is still "fail loud": the caller gets a clear, explicit error, never a wrong result and
/// never a raw throw.
///
/// <para>
/// <b>Process-lifetime singletons.</b> The driver cache and the executor over it are <c>static readonly</c>:
/// a Neo4j <c>IDriver</c> owns a heavyweight connection pool and must be built once per connection identity
/// and reused for the life of the app pool — a driver-per-call is the exact anti-pattern the connection
/// layer exists to prevent. Every <see cref="Neo4jBoltActions"/> instance the Integration Studio wiring
/// news up therefore shares the same cache and executor.
/// </para>
///
/// <para>
/// <b>Typed parameters in, dynamic results out.</b> Parameters arrive as a typed
/// <see cref="CypherParameter"/>[] — a Structure list, not a JSON string (the differentiator over a
/// stringly-typed connector). The record set is dynamic in shape, so it travels back as
/// <c>recordsJson</c> Text that the OutSystems app JSONDeserializes against its own Structures; the summary
/// is fixed in shape, so it comes back as the typed <see cref="CypherSummary"/>. This is the same
/// "complex/dynamic → JSON Text, fixed → typed" split the sibling PICASSO connector uses.
/// </para>
///
/// <para>
/// <b>Credentials never leak.</b> Core's exceptions are built only from non-secret facts (the URI, the
/// Neo4j status code) and never echo <see cref="ConnConfig.Password"/> / <see cref="ConnConfig.BearerToken"/>;
/// this surface preserves that discipline — nothing here folds a credential into <c>errorMessage</c>.
/// </para>
///
/// <para>
/// This is deliberately a plain class with no OutSystems base type: the generated base-class name is
/// Integration Studio-version specific, so the wiring step delegates to this from whatever IS generates
/// rather than guessing at it here.
/// </para>
/// </summary>
public sealed class Neo4jBoltActions
{
    // The process-lifetime singletons (spec §1). One driver cache, one executor over it, shared by every
    // instance — never per-call. The public parameterless ctor binds to these; the internal ctor lets a test
    // substitute an executor/cache wired to a fake driver factory (no network).
    private static readonly DriverCache SharedCache = new DriverCache(new GraphDatabaseDriverFactory());
    private static readonly CypherExecutor SharedExecutor = new CypherExecutor(SharedCache);

    private readonly CypherExecutor _executor;
    private readonly DriverCache _cache;

    /// <summary>
    /// The production constructor Integration Studio uses: binds to the process-lifetime shared driver cache
    /// and executor, so every action reuses one connection pool per connection identity.
    /// </summary>
    public Neo4jBoltActions()
        : this(SharedExecutor, SharedCache)
    {
    }

    /// <summary>
    /// Test seam: wires the surface to a caller-supplied executor and cache (each built over a fake
    /// <see cref="IDriverFactory"/>) so the boundary behaviour is exercised with zero network. Not part of
    /// the OutSystems surface.
    /// </summary>
    /// <param name="executor">The executor the <c>RunCypher…</c> actions delegate to.</param>
    /// <param name="cache">The cache <see cref="VerifyConnectivity"/> / <see cref="ResetDriver"/> act on.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    internal Neo4jBoltActions(CypherExecutor executor, DriverCache cache)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    // ---- Action 1: RunCypherRead ----

    /// <summary>
    /// Runs a read query in a managed read transaction (cluster routing to replicas + automatic retry of
    /// transient errors). Use for anything that only reads (<c>MATCH … RETURN</c>).
    /// </summary>
    /// <param name="connection">The connection configuration.</param>
    /// <param name="query">The Cypher statement.</param>
    /// <param name="parameters">The typed parameters to bind (may be empty; a null list is treated as empty).</param>
    /// <param name="recordsJson">Out: the records as a JSON array; <c>""</c> on failure.</param>
    /// <param name="columns">Out: the result's ordered column names; empty on failure.</param>
    /// <param name="summary">Out: the typed execution summary; null on failure.</param>
    /// <param name="errorMessage">Out: empty on success, a credential-free error on failure.</param>
    /// <returns><c>true</c> on success; <c>false</c> if anything went wrong (never throws).</returns>
    public bool RunCypherRead(
        ConnConfig connection,
        string query,
        CypherParameter[] parameters,
        out string recordsJson,
        out string[] columns,
        out CypherSummary summary,
        out string errorMessage) =>
        Run(() => _executor.RunCypherRead(connection, query, Params(parameters)),
            out recordsJson, out columns, out summary, out errorMessage);

    // ---- Action 2: RunCypherWrite ----

    /// <summary>
    /// Runs a write query in a managed write transaction (cluster routing to the leader + automatic retry of
    /// transient errors). Use for <c>CREATE</c>/<c>MERGE</c>/<c>SET</c>/<c>DELETE</c>.
    /// </summary>
    /// <inheritdoc cref="RunCypherRead"/>
    public bool RunCypherWrite(
        ConnConfig connection,
        string query,
        CypherParameter[] parameters,
        out string recordsJson,
        out string[] columns,
        out CypherSummary summary,
        out string errorMessage) =>
        Run(() => _executor.RunCypherWrite(connection, query, Params(parameters)),
            out recordsJson, out columns, out summary, out errorMessage);

    // ---- Action 3: RunCypherAutoCommit ----

    /// <summary>
    /// Runs a query as an auto-commit (implicit) transaction — <b>no routing benefit and no automatic
    /// retry</b>. Use only for statements that cannot run inside a managed transaction
    /// (<c>CALL { … } IN TRANSACTIONS</c>, certain admin commands); prefer
    /// <see cref="RunCypherRead"/>/<see cref="RunCypherWrite"/> otherwise.
    /// </summary>
    /// <inheritdoc cref="RunCypherRead"/>
    public bool RunCypherAutoCommit(
        ConnConfig connection,
        string query,
        CypherParameter[] parameters,
        out string recordsJson,
        out string[] columns,
        out CypherSummary summary,
        out string errorMessage) =>
        Run(() => _executor.RunCypherAutoCommit(connection, query, Params(parameters)),
            out recordsJson, out columns, out summary, out errorMessage);

    // ---- Action 4: VerifyConnectivity ----

    /// <summary>
    /// The "test connection" diagnostic: obtains (or builds) the cached driver for <paramref name="connection"/>
    /// and checks the server is reachable and accepts the handshake.
    /// <para>
    /// A <em>connectivity</em> failure (server down, bad credentials) is a normal answer, not an error: the
    /// action returns <c>false</c> with <paramref name="ok"/> <c>false</c> and a credential-free
    /// <paramref name="errorMessage"/>. A <em>configuration</em> error (an unsupported auth scheme) is also
    /// caught and returned the same way — nothing throws across the boundary.
    /// </para>
    /// </summary>
    /// <param name="connection">The connection configuration.</param>
    /// <param name="ok">Out: <c>true</c> when connectivity was verified; <c>false</c> otherwise.</param>
    /// <param name="errorMessage">Out: empty when <paramref name="ok"/>, otherwise a credential-free reason.</param>
    /// <returns>The same value as <paramref name="ok"/> (never throws).</returns>
    public bool VerifyConnectivity(ConnConfig connection, out bool ok, out string errorMessage)
    {
        ok = false;
        errorMessage = "";

        try
        {
            ConnectivityResult result = new ConnectivityVerifier(_cache).VerifyConnectivity(connection);
            ok = result.Ok;
            if (!result.Ok)
            {
                // ConnectivityResult carries only the driver exception's type name + message — for a
                // connectivity check neither contains a credential.
                errorMessage = string.IsNullOrEmpty(result.ErrorType)
                    ? (result.ErrorMessage ?? "Connectivity check failed.")
                    : $"{result.ErrorType}: {result.ErrorMessage}";
            }

            return ok;
        }
        catch (Exception ex)
        {
            // A configuration error (unsupported auth scheme → ConnectionException) or anything else: caught,
            // never thrown across the boundary.
            ok = false;
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- Action 5: ResetDriver ----

    /// <summary>
    /// Evicts and disposes the cached driver for <paramref name="connection"/>, so the next action rebuilds
    /// it. Use after rotating credentials or changing connection settings. A no-op if nothing is cached for
    /// that identity.
    /// </summary>
    /// <param name="connection">The connection configuration whose driver should be discarded.</param>
    /// <param name="errorMessage">Out: empty on success, the reason on failure.</param>
    /// <returns><c>true</c> on success (including the no-op case); <c>false</c> on failure (never throws).</returns>
    public bool ResetDriver(ConnConfig connection, out string errorMessage)
    {
        errorMessage = "";

        try
        {
            _cache.Reset(connection);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    // ---- boundary plumbing ----

    /// <summary>
    /// The shared body of the three <c>RunCypher…</c> actions: run the executor, map its result onto the out
    /// parameters, and turn <b>any</b> exception into <c>false</c> + a credential-free
    /// <paramref name="errorMessage"/> with the outputs reset to safe defaults. This is the single place the
    /// no-exception-crosses-the-boundary rule is enforced for query execution.
    /// </summary>
    private static bool Run(
        Func<CypherExecutionResult> execute,
        out string recordsJson,
        out string[] columns,
        out CypherSummary summary,
        out string errorMessage)
    {
        try
        {
            CypherExecutionResult result = execute();
            recordsJson = result.RecordsJson;
            // Columns come from the cursor Keys (a string[]); avoid a copy when it already is one.
            columns = result.Columns as string[] ?? result.Columns.ToArray();
            summary = result.Summary;
            errorMessage = "";
            return true;
        }
        catch (CypherExecutionException ex)
        {
            // Shape the message by fault (spec §9): a developer error is the caller's query/data to fix; an
            // operational one is an environment condition. Core built the text credential-free either way.
            SafeDefaults(out recordsJson, out columns, out summary);
            errorMessage = ex.IsDeveloperError
                ? $"Query error: {ex.Message}"
                : $"Connection error: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            // CypherParameterException (bad parameter, before the network), ConnectionException (bad config),
            // CypherResultException (unserializable value), or anything else — all caught, none re-thrown.
            SafeDefaults(out recordsJson, out columns, out summary);
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void SafeDefaults(out string recordsJson, out string[] columns, out CypherSummary summary)
    {
        recordsJson = "";
        columns = Array.Empty<string>();
        summary = null!;
    }

    /// <summary>
    /// Normalizes the incoming parameter array for the executor. OutSystems Structure lists are never null,
    /// but a defensive null collapses to "no parameters" rather than a spurious failure — an empty and an
    /// absent parameter list are indistinguishable at this surface.
    /// </summary>
    private static IEnumerable<CypherParameter> Params(CypherParameter[]? parameters) =>
        parameters ?? Array.Empty<CypherParameter>();
}
