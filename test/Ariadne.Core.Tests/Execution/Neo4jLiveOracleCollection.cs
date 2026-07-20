using Xunit;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// The xunit collection that groups every <b>live-Neo4j</b> integration class so they share a single
/// <see cref="CypherExecutorIntegrationTests.OracleFixture"/> (one driver + one teardown) and — because
/// xunit never parallelizes tests within one collection — run <b>sequentially</b>. That sequencing is what
/// keeps the classes from colliding on the shared <c>:AriadneOracleTest</c> label: two independent class
/// fixtures each broad-deleting that label could otherwise delete each other's data mid-run. With one shared
/// collection fixture the teardown happens exactly once, after both classes finish.
/// </summary>
[CollectionDefinition(Name)]
public sealed class Neo4jLiveOracleCollection : ICollectionFixture<CypherExecutorIntegrationTests.OracleFixture>
{
    public const string Name = "Neo4jLiveOracle";
}
