using System;
using System.Collections.Generic;
using Ariadne.Core.Results;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Results;

/// <summary>
/// Unit tests for the Feature 07 summary mapper (<see cref="CypherSummaryMapper"/>): projecting a driver
/// <see cref="IResultSummary"/> into the static, typed <see cref="CypherSummary"/> (result spec §2). Pure
/// logic — <see cref="IResultSummary"/>, <see cref="ICounters"/> and <see cref="IDatabaseInfo"/> are
/// satisfied by small hand-rolled fakes (no server, no session, no mocking libraries). Each counter is
/// given a distinct value so a swapped field is caught; timings, the <c>-1 ms</c> "unavailable" sentinel,
/// every <see cref="QueryType"/> code, and the fail-loud paths are all asserted.
/// </summary>
public class CypherSummaryMapperTests
{
    // ================================ counters map 1:1 ================================

    [Fact]
    public void Every_counter_maps_to_its_own_field()
    {
        // Distinct primes so a swapped assignment can never coincidentally pass.
        var counters = new FakeCounters
        {
            NodesCreated = 2,
            NodesDeleted = 3,
            RelationshipsCreated = 5,
            RelationshipsDeleted = 7,
            PropertiesSet = 11,
            LabelsAdded = 13,
            LabelsRemoved = 17,
            IndexesAdded = 19,
            IndexesRemoved = 23,
            ConstraintsAdded = 29,
            ConstraintsRemoved = 31,
            SystemUpdates = 37,
        };

        var s = CypherSummaryMapper.Map(new FakeSummary { Counters = counters });

        Assert.Equal(2, s.NodesCreated);
        Assert.Equal(3, s.NodesDeleted);
        Assert.Equal(5, s.RelationshipsCreated);
        Assert.Equal(7, s.RelationshipsDeleted);
        Assert.Equal(11, s.PropertiesSet);
        Assert.Equal(13, s.LabelsAdded);
        Assert.Equal(17, s.LabelsRemoved);
        Assert.Equal(19, s.IndexesAdded);
        Assert.Equal(23, s.IndexesRemoved);
        Assert.Equal(29, s.ConstraintsAdded);
        Assert.Equal(31, s.ConstraintsRemoved);
        Assert.Equal(37, s.SystemUpdates);
    }

    [Fact]
    public void Counter_fields_are_long_and_accept_values_beyond_int_semantics()
    {
        // The driver exposes int counters; the POCO widens to long. A max-int driver value stays exact.
        var counters = new FakeCounters { PropertiesSet = int.MaxValue };
        var s = CypherSummaryMapper.Map(new FakeSummary { Counters = counters });
        Assert.Equal(2147483647L, s.PropertiesSet);
        Assert.IsType<long>(s.PropertiesSet);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContainsUpdates_and_ContainsSystemUpdates_map(bool flag)
    {
        var counters = new FakeCounters { ContainsUpdates = flag, ContainsSystemUpdates = !flag };
        var s = CypherSummaryMapper.Map(new FakeSummary { Counters = counters });
        Assert.Equal(flag, s.ContainsUpdates);
        Assert.Equal(!flag, s.ContainsSystemUpdates);
    }

    // ================================ timings ================================

    [Fact]
    public void Timings_convert_TimeSpan_to_whole_milliseconds()
    {
        var s = CypherSummaryMapper.Map(new FakeSummary
        {
            ResultAvailableAfter = TimeSpan.FromMilliseconds(42),
            ResultConsumedAfter = TimeSpan.FromMilliseconds(1234),
        });
        Assert.Equal(42, s.ResultAvailableAfterMs);
        Assert.Equal(1234, s.ResultConsumedAfterMs);
    }

    [Fact]
    public void Sub_millisecond_timing_truncates_toward_zero_not_rounds()
    {
        // (long)TotalMilliseconds truncates; document that 12.9 ms -> 12, not 13.
        var s = CypherSummaryMapper.Map(new FakeSummary
        {
            ResultAvailableAfter = TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond * 12 + 9000), // 12.9 ms
            ResultConsumedAfter = TimeSpan.FromMilliseconds(0),
        });
        Assert.Equal(12, s.ResultAvailableAfterMs);
        Assert.Equal(0, s.ResultConsumedAfterMs);
    }

    [Fact]
    public void Unavailable_timing_sentinel_minus_one_passes_through_not_zeroed()
    {
        // The driver reports an unavailable timing as -1 ms; it must survive as -1, distinguishable from 0.
        var s = CypherSummaryMapper.Map(new FakeSummary
        {
            ResultAvailableAfter = TimeSpan.FromMilliseconds(-1),
            ResultConsumedAfter = TimeSpan.FromMilliseconds(-1),
        });
        Assert.Equal(-1, s.ResultAvailableAfterMs);
        Assert.Equal(-1, s.ResultConsumedAfterMs);
    }

    // ================================ query type ================================

    [Theory]
    [InlineData(QueryType.ReadOnly, "r")]
    [InlineData(QueryType.ReadWrite, "rw")]
    [InlineData(QueryType.WriteOnly, "w")]
    [InlineData(QueryType.SchemaWrite, "s")]
    public void Known_query_types_map_to_short_codes(QueryType type, string code)
    {
        var s = CypherSummaryMapper.Map(new FakeSummary { QueryType = type });
        Assert.Equal(code, s.QueryType);
    }

    [Fact]
    public void Unknown_query_type_maps_to_unknown_and_does_not_discard_the_summary()
    {
        // QueryType.Unknown = the server didn't classify the query (a benign, representable state).
        // It must NOT throw away the whole summary — it maps to "unknown", and counters/timings survive.
        var s = CypherSummaryMapper.Map(new FakeSummary
        {
            QueryType = QueryType.Unknown,
            Counters = new FakeCounters { NodesCreated = 3 },
        });
        Assert.Equal("unknown", s.QueryType);
        Assert.Equal(3, s.NodesCreated); // the rest of the summary is intact
    }

    [Fact]
    public void Undefined_query_type_enum_value_fails_loud()
    {
        var ex = Assert.Throws<CypherResultException>(
            () => CypherSummaryMapper.Map(new FakeSummary { QueryType = (QueryType)99 }));
        Assert.Contains("99", ex.Message);
    }

    // ================================ database ================================

    [Fact]
    public void Database_name_passes_through()
    {
        var s = CypherSummaryMapper.Map(new FakeSummary { Database = new FakeDatabaseInfo { Name = "neo4j" } });
        Assert.Equal("neo4j", s.Database);
    }

    [Fact]
    public void Null_database_info_yields_null_database_not_fabricated()
    {
        var s = CypherSummaryMapper.Map(new FakeSummary { Database = null });
        Assert.Null(s.Database);
    }

    [Fact]
    public void Null_database_name_passes_through_as_null()
    {
        var s = CypherSummaryMapper.Map(new FakeSummary { Database = new FakeDatabaseInfo { Name = null! } });
        Assert.Null(s.Database);
    }

    // ================================ fail loud on null ================================

    [Fact]
    public void Map_null_summary_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => CypherSummaryMapper.Map(null!));
    }

    [Fact]
    public void Null_counters_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => CypherSummaryMapper.Map(new FakeSummary { Counters = null! }));
    }

    // ================================ default / zero record ================================

    [Fact]
    public void All_zero_read_only_summary_maps_cleanly()
    {
        // A pure read (no writes): every counter zero, ContainsUpdates false, QueryType "r".
        var s = CypherSummaryMapper.Map(new FakeSummary { QueryType = QueryType.ReadOnly });
        Assert.Equal(0, s.NodesCreated);
        Assert.False(s.ContainsUpdates);
        Assert.False(s.ContainsSystemUpdates);
        Assert.Equal("r", s.QueryType);
    }

    // ================================ hand-rolled fakes ================================

    /// <summary>Minimal <see cref="ICounters"/> fake — every member is a settable auto-property.</summary>
    private sealed class FakeCounters : ICounters
    {
        public bool ContainsUpdates { get; set; }
        public bool ContainsSystemUpdates { get; set; }
        public int NodesCreated { get; set; }
        public int NodesDeleted { get; set; }
        public int RelationshipsCreated { get; set; }
        public int RelationshipsDeleted { get; set; }
        public int PropertiesSet { get; set; }
        public int LabelsAdded { get; set; }
        public int LabelsRemoved { get; set; }
        public int IndexesAdded { get; set; }
        public int IndexesRemoved { get; set; }
        public int ConstraintsAdded { get; set; }
        public int ConstraintsRemoved { get; set; }
        public int SystemUpdates { get; set; }
    }

    /// <summary>Minimal <see cref="IDatabaseInfo"/> fake.</summary>
    private sealed class FakeDatabaseInfo : IDatabaseInfo
    {
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Minimal <see cref="IResultSummary"/> fake. Only the members the mapper reads
    /// (<see cref="Counters"/>, the two timings, <see cref="QueryType"/>, <see cref="Database"/>) carry
    /// meaningful values; the rest are inert defaults the mapper never touches.
    /// </summary>
    private sealed class FakeSummary : IResultSummary
    {
        public ICounters Counters { get; set; } = new FakeCounters();
        public TimeSpan ResultAvailableAfter { get; set; } = TimeSpan.Zero;
        public TimeSpan ResultConsumedAfter { get; set; } = TimeSpan.Zero;
        public QueryType QueryType { get; set; } = QueryType.ReadOnly;
        public IDatabaseInfo? Database { get; set; } = new FakeDatabaseInfo { Name = "neo4j" };

        // --- members the mapper never reads: inert ---
        public Query Query => new Query("");
        public bool HasPlan => false;
        public bool HasProfile => false;
        public IPlan Plan => null!;
        public IProfiledPlan Profile => null!;
        public IList<INotification> Notifications => Array.Empty<INotification>();
        public IList<IGqlStatusObject> GqlStatusObjects => Array.Empty<IGqlStatusObject>();
        public IServerInfo Server => null!;
        IDatabaseInfo IResultSummary.Database => Database!;
    }
}
