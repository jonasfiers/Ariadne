using System;
using System.Collections.Generic;
using Ariadne.Core.Connection;
using Ariadne.Core.Execution;
using Ariadne.Core.Parameters;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// Unit tests for the Feature 09 execution layer (<see cref="CypherExecutor"/>) using hand-rolled fakes of the
/// driver's async surface — no live Neo4j, no mocking libraries. They pin the ORCHESTRATION that is hard to
/// trigger against a real server: which transaction mode each method routes to, that parameters flow through
/// the mapper, that the session is opened for the configured database and disposed, that columns come from the
/// cursor's keys even for an empty result, and the full §9 driver-exception → named/classified error mapping
/// (including that no operational message leaks a credential). Live round-trip behaviour is covered separately
/// by the env-gated integration tests.
/// </summary>
public class CypherExecutorTests
{
    private const string Secret = "sup3r-s3cr3t-p@ssw0rd";

    private static ConnConfig Config(string? database = "neo4j") => new ConnConfig
    {
        Uri = "bolt://neo4j.example.internal:7687",
        AuthScheme = "Basic",
        Username = "neo4j",
        Password = Secret,
        Database = database,
    };

    private static (CypherExecutor executor, ExecFakeDriver driver) Build(
        FakeCursor cursor, Exception? throwOnRun = null)
    {
        var driver = new ExecFakeDriver(cursor, throwOnRun);
        var cache = new DriverCache(new ExecFakeDriverFactory(driver));
        return (new CypherExecutor(cache), driver);
    }

    private static FakeCursor CursorWith(
        string[] keys, List<IRecord>? records = null, IResultSummary? summary = null) =>
        new FakeCursor(keys, records ?? new List<IRecord>(), summary ?? new ExecFakeSummary());

    private static IEnumerable<CypherParameter> NoParams() => Array.Empty<CypherParameter>();

    // ============================ routing: each method → its transaction mode ============================

    [Fact]
    public void RunCypherRead_uses_ExecuteReadAsync_only()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherRead(Config(), "MATCH (n) RETURN n", NoParams());

        var session = driver.LastSession!;
        Assert.Equal(1, session.ReadCalls);
        Assert.Equal(0, session.WriteCalls);
        Assert.Equal(0, session.AutoCommitCalls);
    }

    [Fact]
    public void RunCypherWrite_uses_ExecuteWriteAsync_only()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherWrite(Config(), "CREATE (n)", NoParams());

        var session = driver.LastSession!;
        Assert.Equal(0, session.ReadCalls);
        Assert.Equal(1, session.WriteCalls);
        Assert.Equal(0, session.AutoCommitCalls);
    }

    [Fact]
    public void RunCypherAutoCommit_uses_session_RunAsync_only()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherAutoCommit(Config(), "CALL {} IN TRANSACTIONS", NoParams());

        var session = driver.LastSession!;
        Assert.Equal(0, session.ReadCalls);
        Assert.Equal(0, session.WriteCalls);
        Assert.Equal(1, session.AutoCommitCalls);
    }

    // ============================ parameter flow + query pass-through ============================

    [Fact]
    public void Parameters_flow_through_BuildParameters_to_the_runner()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));
        var parameters = new[]
        {
            new CypherParameter { Name = "n", Type = "Integer", IntegerValue = 42 },
            new CypherParameter { Name = "s", Type = "String", StringValue = "hi" },
        };

        executor.RunCypherRead(Config(), "RETURN $n, $s", parameters);

        var session = driver.LastSession!;
        Assert.Equal("RETURN $n, $s", session.LastQuery);
        Assert.NotNull(session.LastParameters);
        Assert.Equal(42L, session.LastParameters!["n"]);   // Neo4j Integer is Int64
        Assert.Equal("hi", session.LastParameters!["s"]);
    }

    [Fact]
    public void Invalid_parameter_fails_loud_as_CypherParameterException_not_execution_error()
    {
        var (executor, _) = Build(CursorWith(Array.Empty<string>()));
        var dup = new[]
        {
            new CypherParameter { Name = "x", Type = "Integer", IntegerValue = 1 },
            new CypherParameter { Name = "x", Type = "Integer", IntegerValue = 2 },
        };

        Assert.Throws<CypherParameterException>(() => executor.RunCypherRead(Config(), "RETURN $x", dup));
    }

    // ============================ session lifecycle: database + disposal ============================

    [Fact]
    public void Session_is_opened_for_the_configured_database()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherRead(Config(database: "movies"), "RETURN 1", NoParams());

        Assert.Equal("movies", driver.LastSession!.Database);
    }

    [Fact]
    public void Null_database_leaves_the_driver_default()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherRead(Config(database: null), "RETURN 1", NoParams());

        Assert.Null(driver.LastSession!.Database);
    }

    [Fact]
    public void Session_is_disposed_after_the_call()
    {
        var (executor, driver) = Build(CursorWith(Array.Empty<string>()));

        executor.RunCypherRead(Config(), "RETURN 1", NoParams());

        Assert.True(driver.LastSession!.Disposed);
    }

    [Fact]
    public void Session_is_disposed_even_when_the_run_throws()
    {
        var (executor, driver) = Build(
            CursorWith(Array.Empty<string>()),
            throwOnRun: new ClientException("Neo.ClientError.Statement.SyntaxError", "boom"));

        Assert.Throws<CypherExecutionException>(() => executor.RunCypherRead(Config(), "RETRUN 1", NoParams()));
        Assert.True(driver.LastSession!.Disposed);
    }

    // ============================ result assembly: columns / records / summary ============================

    [Fact]
    public void Columns_come_from_cursor_keys_even_for_an_empty_result()
    {
        // Zero records, but the projection has columns — the cursor's keys must still surface.
        var (executor, _) = Build(CursorWith(new[] { "a", "b" }));

        var result = executor.RunCypherRead(Config(), "MATCH (n:None) RETURN n.a AS a, n.b AS b", NoParams());

        Assert.Equal(new[] { "a", "b" }, result.Columns);
        Assert.Equal("[]", result.RecordsJson);
    }

    [Fact]
    public void Records_are_serialized_and_summary_is_mapped()
    {
        var records = new List<IRecord> { new ExecFakeRecord().With("n", 1L) };
        var summary = new ExecFakeSummary
        {
            Counters = new ExecFakeCounters { NodesCreated = 1, ContainsUpdates = true },
            QueryType = QueryType.ReadWrite,
        };
        var (executor, _) = Build(CursorWith(new[] { "n" }, records, summary));

        var result = executor.RunCypherWrite(Config(), "CREATE (n) RETURN 1 AS n", NoParams());

        Assert.Equal("[{\"n\":1}]", result.RecordsJson);
        Assert.Equal(new[] { "n" }, result.Columns);
        Assert.Equal(1, result.Summary.NodesCreated);
        Assert.True(result.Summary.ContainsUpdates);
        Assert.Equal("rw", result.Summary.QueryType);
    }

    // ============================ §9 error mapping ============================

    [Fact]
    public void ClientException_is_a_developer_error_surfaced_verbatim()
    {
        var client = new ClientException("Neo.ClientError.Statement.SyntaxError", "Invalid input 'RETRUN'");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: client);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherRead(Config(), "RETRUN 1", NoParams()));

        Assert.True(ex.IsDeveloperError);
        Assert.Equal(ExecutionErrorClassification.Developer, ex.Classification);
        Assert.Equal("Neo.ClientError.Statement.SyntaxError", ex.Neo4jCode);
        Assert.Contains("Neo.ClientError.Statement.SyntaxError", ex.Message);
        Assert.Contains("Invalid input 'RETRUN'", ex.Message);
        Assert.Same(client, ex.InnerException);
    }

    [Fact]
    public void AuthenticationException_is_operational_and_leaks_no_field_or_credential()
    {
        var auth = new AuthenticationException("The client is unauthorized due to authentication failure.");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: auth);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherRead(Config(), "RETURN 1", NoParams()));

        Assert.False(ex.IsDeveloperError);
        Assert.Equal(ExecutionErrorClassification.Operational, ex.Classification);
        Assert.Equal("Neo4j authentication failed.", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain("neo4j", ex.Message);   // no username/field hint
    }

    [Fact]
    public void ServiceUnavailable_is_operational_and_names_only_the_uri()
    {
        var svc = new ServiceUnavailableException("connection refused");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: svc);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherRead(Config(), "RETURN 1", NoParams()));

        Assert.Equal(ExecutionErrorClassification.Operational, ex.Classification);
        Assert.Contains("bolt://neo4j.example.internal:7687", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public void TransientException_managed_mode_says_retries_exhausted()
    {
        var transient = new TransientException("Neo.TransientError.Transaction.DeadlockDetected", "deadlock");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: transient);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherWrite(Config(), "CREATE (n)", NoParams()));

        Assert.Equal(ExecutionErrorClassification.Operational, ex.Classification);
        Assert.Equal("Neo.TransientError.Transaction.DeadlockDetected", ex.Neo4jCode);
        Assert.Contains("Neo.TransientError.Transaction.DeadlockDetected", ex.Message);
        Assert.Contains("retries", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public void TransientException_autocommit_says_no_retry()
    {
        var transient = new TransientException("Neo.TransientError.General.X", "x");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: transient);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherAutoCommit(Config(), "CALL {} IN TRANSACTIONS", NoParams()));

        Assert.Contains("no retry", ex.Message);
    }

    [Fact]
    public void SessionExpired_is_operational()
    {
        var expired = new SessionExpiredException("leader switch");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: expired);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherWrite(Config(), "CREATE (n)", NoParams()));

        Assert.Equal(ExecutionErrorClassification.Operational, ex.Classification);
        Assert.Contains("routing/session", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public void DatabaseException_is_operational_with_code()
    {
        var db = new DatabaseException("Neo.DatabaseError.General.UnknownError", "internal");
        var (executor, _) = Build(CursorWith(Array.Empty<string>()), throwOnRun: db);

        var ex = Assert.Throws<CypherExecutionException>(() => executor.RunCypherRead(Config(), "RETURN 1", NoParams()));

        Assert.Equal(ExecutionErrorClassification.Operational, ex.Classification);
        Assert.Equal("Neo.DatabaseError.General.UnknownError", ex.Neo4jCode);
        Assert.Contains("Neo.DatabaseError.General.UnknownError", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    // ============================ argument validation ============================

    [Fact]
    public void Null_config_throws_ArgumentNullException()
    {
        var (executor, _) = Build(CursorWith(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => executor.RunCypherRead(null!, "RETURN 1", NoParams()));
    }

    [Fact]
    public void Null_query_throws_ArgumentNullException()
    {
        var (executor, _) = Build(CursorWith(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => executor.RunCypherRead(Config(), null!, NoParams()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_query_throws_ArgumentException(string query)
    {
        var (executor, _) = Build(CursorWith(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => executor.RunCypherRead(Config(), query, NoParams()));
    }

    [Fact]
    public void Null_parameters_throws_ArgumentNullException()
    {
        var (executor, _) = Build(CursorWith(Array.Empty<string>()));
        Assert.Throws<ArgumentNullException>(() => executor.RunCypherRead(Config(), "RETURN 1", null!));
    }

    [Fact]
    public void Ctor_null_cache_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CypherExecutor(null!));
    }
}
