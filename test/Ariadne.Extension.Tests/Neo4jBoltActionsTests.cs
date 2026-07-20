using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Ariadne.Core.Connection;
using Ariadne.Core.Execution;
using Ariadne.Core.Parameters;
using Ariadne.Core.Results;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Extension.Tests;

/// <summary>
/// Validates the Integration Studio action boundary the way the generated extension will call it — plain C#,
/// no OutSystems runtime. Two things matter here that the Core tests do not cover: that a successful Core
/// call is mapped cleanly onto the <c>bool</c> + <c>out</c> shape, and that <b>every</b> Core exception is
/// swallowed into <c>false</c> + a credential-free <c>errorMessage</c> with no throw crossing the boundary.
/// The "fake executor/cache" is a real executor/cache wired to a fake <see cref="IDriverFactory"/>.
/// </summary>
public class Neo4jBoltActionsTests
{
    // A distinctive credential so a leak into any errorMessage is unambiguous.
    private const string Secret = "SUPER-SECRET-PW-4826-xyzzy";

    private static ConnConfig Config() => new ConnConfig
    {
        Uri = "bolt://localhost:7687",
        AuthScheme = "Basic",
        Username = "neo4j",
        Password = Secret,
    };

    private static Neo4jBoltActions Build(IDriverFactory factory)
    {
        var cache = new DriverCache(factory);
        var executor = new CypherExecutor(cache);
        return new Neo4jBoltActions(executor, cache);
    }

    /// <summary>A fake driver whose one row is <c>{ "value": 42 }</c> and whose summary reports NodesCreated=1.</summary>
    private static FakeExtDriver SuccessDriver()
    {
        var summary = new FakeExtSummary
        {
            QueryType = QueryType.ReadWrite,
            Counters = new FakeExtCounters { NodesCreated = 1, PropertiesSet = 2, ContainsUpdates = true },
        };
        var records = new List<IRecord> { new FakeExtRecord().With("value", 42L) };
        var cursor = new FakeExtCursor(new[] { "value" }, records, summary);
        return new FakeExtDriver(cursor: cursor);
    }

    // ---- success mapping ----

    public static IEnumerable<object[]> RunActions => new[]
    {
        new object[] { "Read" },
        new object[] { "Write" },
        new object[] { "AutoCommit" },
    };

    private static bool Invoke(
        Neo4jBoltActions actions, string which, ConnConfig config, string query, CypherParameter[] parameters,
        out string recordsJson, out string[] columns, out CypherSummary summary, out string errorMessage) =>
        which switch
        {
            "Read" => actions.RunCypherRead(config, query, parameters, out recordsJson, out columns, out summary, out errorMessage),
            "Write" => actions.RunCypherWrite(config, query, parameters, out recordsJson, out columns, out summary, out errorMessage),
            "AutoCommit" => actions.RunCypherAutoCommit(config, query, parameters, out recordsJson, out columns, out summary, out errorMessage),
            _ => throw new ArgumentOutOfRangeException(nameof(which)),
        };

    [Theory]
    [MemberData(nameof(RunActions))]
    public void RunCypher_success_returns_true_and_maps_all_outputs(string which)
    {
        var actions = Build(FakeExtDriverFactory.Returning(SuccessDriver()));

        bool ok = Invoke(
            actions, which, Config(), "RETURN 42 AS value", Array.Empty<CypherParameter>(),
            out var recordsJson, out var columns, out var summary, out var error);

        Assert.True(ok, error);
        Assert.Equal("", error);

        // recordsJson is the canonical envelope; assert the value survived rather than just non-empty.
        using var doc = JsonDocument.Parse(recordsJson);
        Assert.Equal(42, doc.RootElement[0].GetProperty("value").GetInt32());

        Assert.Equal(new[] { "value" }, columns);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.NodesCreated);
        Assert.Equal(2, summary.PropertiesSet);
    }

    [Fact]
    public void RunCypher_null_parameter_list_is_treated_as_no_parameters()
    {
        var actions = Build(FakeExtDriverFactory.Returning(SuccessDriver()));

        bool ok = actions.RunCypherRead(
            Config(), "RETURN 42 AS value", null!,
            out var recordsJson, out _, out _, out var error);

        Assert.True(ok, error);
        Assert.False(string.IsNullOrEmpty(recordsJson));
    }

    // ---- exception swallowing: every Core exception → false + credential-free message, no throw ----

    [Theory]
    [MemberData(nameof(RunActions))]
    public void Developer_error_returns_false_with_safe_defaults_and_no_password(string which)
    {
        // A ClientException WITH a Neo4j code maps to a developer-classified CypherExecutionException.
        var driver = new FakeExtDriver(
            throwOnRun: new ClientException("Neo.ClientError.Statement.SyntaxError", "Invalid input 'RETRUN'"));
        var actions = Build(FakeExtDriverFactory.Returning(driver));

        var ex = Record.Exception(() => Invoke(
            actions, which, Config(), "RETRUN 1", Array.Empty<CypherParameter>(),
            out var recordsJson, out var columns, out var summary, out var error));

        Assert.Null(ex); // nothing crosses the boundary
    }

    [Fact]
    public void Developer_error_message_is_shaped_and_credential_free()
    {
        var driver = new FakeExtDriver(
            throwOnRun: new ClientException("Neo.ClientError.Statement.SyntaxError", "Invalid input 'RETRUN'"));
        var actions = Build(FakeExtDriverFactory.Returning(driver));

        bool ok = actions.RunCypherRead(
            Config(), "RETRUN 1", Array.Empty<CypherParameter>(),
            out var recordsJson, out var columns, out var summary, out var error);

        Assert.False(ok);
        Assert.Equal("", recordsJson);
        Assert.Empty(columns);
        Assert.Null(summary);
        Assert.Contains("Query error", error);
        Assert.Contains("SyntaxError", error);
        Assert.DoesNotContain(Secret, error);
    }

    [Fact]
    public void Operational_error_returns_false_credential_free()
    {
        var driver = new FakeExtDriver(throwOnRun: new ServiceUnavailableException("connection refused"));
        var actions = Build(FakeExtDriverFactory.Returning(driver));

        bool ok = actions.RunCypherWrite(
            Config(), "CREATE (:X)", Array.Empty<CypherParameter>(),
            out var recordsJson, out var columns, out var summary, out var error);

        Assert.False(ok);
        Assert.Equal("", recordsJson);
        Assert.Empty(columns);
        Assert.Null(summary);
        Assert.Contains("Connection error", error);
        Assert.DoesNotContain(Secret, error);
    }

    [Fact]
    public void Bad_parameter_returns_false_before_touching_the_driver()
    {
        // An unknown Type tag makes the parameter mapper throw CypherParameterException — before any GetDriver.
        var actions = Build(FakeExtDriverFactory.Returning(SuccessDriver()));
        var bad = new[] { new CypherParameter { Name = "p", Type = "NoSuchType" } };

        bool ok = actions.RunCypherRead(
            Config(), "RETURN $p", bad,
            out var recordsJson, out var columns, out var summary, out var error);

        Assert.False(ok);
        Assert.Equal("", recordsJson);
        Assert.Empty(columns);
        Assert.Null(summary);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain(Secret, error);
    }

    [Fact]
    public void Connection_config_error_is_caught_not_thrown()
    {
        // The real production factory validates the auth scheme first (no network): an unsupported scheme
        // throws ConnectionException out of GetDriver, which the boundary must catch.
        var actions = Build(new GraphDatabaseDriverFactory());
        var config = Config();
        config.AuthScheme = "Kerberos"; // out of scope for v1 → ConnectionException

        var ex = Record.Exception(() =>
        {
            bool ok = actions.RunCypherRead(
                config, "RETURN 1", Array.Empty<CypherParameter>(),
                out var recordsJson, out var columns, out var summary, out var error);

            Assert.False(ok);
            Assert.Equal("", recordsJson);
            Assert.Null(summary);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.DoesNotContain(Secret, error);
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Unexpected_exception_is_caught_not_thrown()
    {
        // Anything non-Neo4j (here from Create) is not part of the §9 mapping and would otherwise propagate.
        var actions = Build(FakeExtDriverFactory.ThrowingOnCreate(new InvalidOperationException("kaboom")));

        bool ok = actions.RunCypherRead(
            Config(), "RETURN 1", Array.Empty<CypherParameter>(),
            out var recordsJson, out var columns, out var summary, out var error);

        Assert.False(ok);
        Assert.Equal("kaboom", error);
        Assert.Null(summary);
    }

    // ---- VerifyConnectivity ----

    [Fact]
    public void VerifyConnectivity_good_server_returns_true()
    {
        var actions = Build(FakeExtDriverFactory.Returning(new FakeExtDriver()));

        bool result = actions.VerifyConnectivity(Config(), out bool ok, out var error);

        Assert.True(result);
        Assert.True(ok);
        Assert.Equal("", error);
    }

    [Fact]
    public void VerifyConnectivity_unreachable_returns_false_credential_free()
    {
        var driver = new FakeExtDriver(connectivityError: new AuthenticationException("auth failed"));
        var actions = Build(FakeExtDriverFactory.Returning(driver));

        bool result = actions.VerifyConnectivity(Config(), out bool ok, out var error);

        Assert.False(result);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain(Secret, error);
    }

    [Fact]
    public void VerifyConnectivity_bad_config_is_caught_not_thrown()
    {
        var actions = Build(new GraphDatabaseDriverFactory());
        var config = Config();
        config.AuthScheme = "Kerberos";

        var ex = Record.Exception(() =>
        {
            bool result = actions.VerifyConnectivity(config, out bool ok, out var error);
            Assert.False(result);
            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
            Assert.DoesNotContain(Secret, error);
        });

        Assert.Null(ex);
    }

    // ---- ResetDriver ----

    [Fact]
    public void ResetDriver_is_a_noop_success_when_nothing_cached()
    {
        var actions = Build(FakeExtDriverFactory.Returning(new FakeExtDriver()));

        bool ok = actions.ResetDriver(Config(), out var error);

        Assert.True(ok);
        Assert.Equal("", error);
    }

    [Fact]
    public void ResetDriver_null_config_is_caught_not_thrown()
    {
        var actions = Build(FakeExtDriverFactory.Returning(new FakeExtDriver()));

        var ex = Record.Exception(() =>
        {
            bool ok = actions.ResetDriver(null!, out var error);
            Assert.False(ok);
            Assert.False(string.IsNullOrEmpty(error));
        });

        Assert.Null(ex);
    }

    // ---- process-lifetime singletons ----

    [Fact]
    public void Two_default_instances_share_one_static_cache_and_executor()
    {
        var a = new Neo4jBoltActions();
        var b = new Neo4jBoltActions();

        object cacheA = Field(a, "_cache");
        object cacheB = Field(b, "_cache");
        object execA = Field(a, "_executor");
        object execB = Field(b, "_executor");

        Assert.Same(cacheA, cacheB);
        Assert.Same(execA, execB);
    }

    private static object Field(Neo4jBoltActions actions, string name)
    {
        FieldInfo f = typeof(Neo4jBoltActions).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} not found.");
        return f.GetValue(actions)!;
    }

    // ---- env-gated live integration (skips without a server) ----

    // Only reached from [RequiresNeo4jFact] tests, which run only when the env vars are set (non-null).
    private static ConnConfig LiveConfig() => new ConnConfig
    {
        Uri = Neo4jTestEnvironment.Uri!,
        AuthScheme = "Basic",
        Username = Neo4jTestEnvironment.User,
        Password = Neo4jTestEnvironment.Password,
        Database = Neo4jTestEnvironment.Database,
    };

    [RequiresNeo4jFact]
    public void Live_write_then_read_back_through_the_action_surface()
    {
        var actions = new Neo4jBoltActions();
        var config = LiveConfig();
        string tag = "ext-" + Guid.NewGuid().ToString("N");

        try
        {
            bool wrote = actions.RunCypherWrite(
                config,
                "CREATE (n:AriadneOracleTest {tag: $tag, n: 7}) RETURN n.tag AS tag",
                new[] { new CypherParameter { Name = "tag", Type = "String", StringValue = tag } },
                out var writeJson, out var writeCols, out var writeSummary, out var writeError);

            Assert.True(wrote, writeError);
            Assert.Equal(1, writeSummary.NodesCreated);
            Assert.Contains(tag, writeJson);
            Assert.Equal(new[] { "tag" }, writeCols);

            bool read = actions.RunCypherRead(
                config,
                "MATCH (n:AriadneOracleTest {tag: $tag}) RETURN n.n AS n",
                new[] { new CypherParameter { Name = "tag", Type = "String", StringValue = tag } },
                out var readJson, out var readCols, out var readSummary, out var readError);

            Assert.True(read, readError);
            Assert.Equal(new[] { "n" }, readCols);
            using var doc = JsonDocument.Parse(readJson);
            Assert.Equal(7, doc.RootElement[0].GetProperty("n").GetInt32());
        }
        finally
        {
            actions.RunCypherWrite(
                config,
                "MATCH (n:AriadneOracleTest {tag: $tag}) DETACH DELETE n",
                new[] { new CypherParameter { Name = "tag", Type = "String", StringValue = tag } },
                out _, out _, out _, out _);
        }
    }

    [RequiresNeo4jFact]
    public void Live_verify_connectivity_good()
    {
        var actions = new Neo4jBoltActions();

        bool result = actions.VerifyConnectivity(LiveConfig(), out bool ok, out var error);

        Assert.True(result, error);
        Assert.True(ok);
        Assert.Equal("", error);
    }

    [RequiresNeo4jFact]
    public void Live_verify_connectivity_bad_password_is_false_and_credential_free()
    {
        var actions = new Neo4jBoltActions();
        var config = LiveConfig();
        const string wrongPw = "definitely-not-the-password-9182";
        config.Password = wrongPw;

        bool result = actions.VerifyConnectivity(config, out bool ok, out var error);

        Assert.False(result);
        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.DoesNotContain(wrongPw, error);
    }
}
