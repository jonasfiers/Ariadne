using System;
using System.Collections.Generic;
using Ariadne.Core.Connection;
using Ariadne.Core.Execution;
using Ariadne.Core.Parameters;
using Xunit;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// <b>Live</b> integration tests for <see cref="CypherExecutor"/> against a real Neo4j — the round-trip oracle
/// (the PICASSO-GnuCOBOL analogue). They connect, run real Cypher, and assert on the real records + summary,
/// exercising the full wiring that the mocked unit tests cannot: the real driver, session, managed-tx routing,
/// cursor materialization, and the actual server's error codes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Public-safe and env-gated.</b> Connection details come <b>only</b> from environment variables
/// (<c>NEO4J_TEST_URI</c>, <c>NEO4J_TEST_USER</c>, <c>NEO4J_TEST_PASSWORD</c>) — never literals in the repo —
/// and every test <b>skips cleanly</b> (via <see cref="RequiresNeo4jFactAttribute"/>) when they are
/// unset, so CI and other machines without a Neo4j do not fail. The tests create and delete only
/// <c>:AriadneOracleTest</c>-labelled nodes and never assume a clean database.
/// </para>
/// </remarks>
[Collection(Neo4jLiveOracleCollection.Name)]
public sealed class CypherExecutorIntegrationTests
{
    private readonly OracleFixture _fx;

    public CypherExecutorIntegrationTests(OracleFixture fx) => _fx = fx;

    private static IReadOnlyList<CypherParameter> Params(params CypherParameter[] p) => p;
    private static IReadOnlyList<CypherParameter> NoParams() => Array.Empty<CypherParameter>();

    [RequiresNeo4jFact]
    public void Read_returns_a_bound_scalar_parameter()
    {
        var result = _fx.Executor.RunCypherRead(
            _fx.Config, "RETURN $p AS p",
            Params(new CypherParameter { Name = "p", Type = "Integer", IntegerValue = 7 }));

        Assert.Equal(new[] { "p" }, result.Columns);
        Assert.Equal("[{\"p\":7}]", result.RecordsJson);
        Assert.Equal("r", result.Summary.QueryType);
    }

    [RequiresNeo4jFact]
    public void Read_round_trips_a_bound_temporal_parameter()
    {
        var result = _fx.Executor.RunCypherRead(
            _fx.Config, "RETURN $d AS d",
            Params(new CypherParameter { Name = "d", Type = "Date", DateTimeValue = new DateTime(2020, 1, 15) }));

        Assert.Equal(new[] { "d" }, result.Columns);
        // The result serializer renders a Neo4j Date as ISO yyyy-MM-dd (Feature 04/05 + TemporalFormat).
        Assert.Equal("[{\"d\":\"2020-01-15\"}]", result.RecordsJson);
    }

    [RequiresNeo4jFact]
    public void Write_creates_a_node_and_reports_the_counter_then_reads_it_back()
    {
        string id = "oracle-" + Guid.NewGuid().ToString("N");

        var write = _fx.Executor.RunCypherWrite(
            _fx.Config, "CREATE (n:AriadneOracleTest {id: $id})",
            Params(new CypherParameter { Name = "id", Type = "String", StringValue = id }));

        Assert.Equal(1, write.Summary.NodesCreated);
        Assert.True(write.Summary.ContainsUpdates);

        var read = _fx.Executor.RunCypherRead(
            _fx.Config, "MATCH (n:AriadneOracleTest {id: $id}) RETURN n.id AS id",
            Params(new CypherParameter { Name = "id", Type = "String", StringValue = id }));

        Assert.Equal("[{\"id\":\"" + id + "\"}]", read.RecordsJson);
    }

    [RequiresNeo4jFact]
    public void AutoCommit_runs_a_simple_return()
    {
        var result = _fx.Executor.RunCypherAutoCommit(_fx.Config, "RETURN 1 AS one", NoParams());

        Assert.Equal("[{\"one\":1}]", result.RecordsJson);
    }

    [RequiresNeo4jFact]
    public void Empty_result_still_reports_its_projected_columns()
    {
        var result = _fx.Executor.RunCypherRead(
            _fx.Config, "MATCH (n:AriadneOracleTest {id:'no-such-node-ever'}) RETURN n.id AS id, n.x AS x", NoParams());

        Assert.Equal("[]", result.RecordsJson);
        Assert.Equal(new[] { "id", "x" }, result.Columns);
    }

    [RequiresNeo4jFact]
    public void Bad_cypher_surfaces_a_developer_error_with_the_server_syntax_message()
    {
        var ex = Assert.Throws<CypherExecutionException>(
            () => _fx.Executor.RunCypherRead(_fx.Config, "RETRUN 1", NoParams()));

        Assert.True(ex.IsDeveloperError);
        Assert.Equal(ExecutionErrorClassification.Developer, ex.Classification);
        // The real server's syntax-error code + message are surfaced verbatim.
        Assert.Contains("SyntaxError", ex.Neo4jCode ?? string.Empty);
        Assert.Contains("Neo.ClientError.Statement.SyntaxError", ex.Message);
    }

    /// <summary>
    /// Shared live-connection fixture: reads the env-var connection once, builds one real driver cache +
    /// executor, and on disposal deletes every <c>:AriadneOracleTest</c> node the suite created. When the env
    /// vars are unset it reports <see cref="Available"/> = false and the tests skip.
    /// </summary>
    public sealed class OracleFixture : IDisposable
    {
        private readonly DriverCache? _cache;

        public OracleFixture()
        {
            if (!Neo4jTestEnvironment.IsConfigured)
            {
                Available = false;
                return;
            }

            Available = true;
            Config = new ConnConfig
            {
                Uri = Neo4jTestEnvironment.Uri!,
                AuthScheme = "Basic",
                Username = Neo4jTestEnvironment.User,
                Password = Neo4jTestEnvironment.Password,
                Database = Neo4jTestEnvironment.Database, // optional; null ⇒ driver default
            };
            _cache = new DriverCache(new GraphDatabaseDriverFactory());
            Executor = new CypherExecutor(_cache);
        }

        /// <summary>Whether a live server is configured; false ⇒ every test skips.</summary>
        public bool Available { get; }

        /// <summary>The live connection config (only meaningful when <see cref="Available"/>).</summary>
        public ConnConfig Config { get; } = new ConnConfig();

        /// <summary>The executor over the live driver (only meaningful when <see cref="Available"/>).</summary>
        public CypherExecutor Executor { get; } = null!;

        public void Dispose()
        {
            if (Available)
            {
                try
                {
                    Executor.RunCypherWrite(
                        Config, "MATCH (n:AriadneOracleTest) DETACH DELETE n", Array.Empty<CypherParameter>());
                }
                catch
                {
                    // Best-effort teardown; never fail the suite on cleanup.
                }
            }

            _cache?.Dispose();
        }
    }
}
