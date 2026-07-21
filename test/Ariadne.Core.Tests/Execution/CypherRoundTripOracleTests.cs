using System;
using System.Collections.Generic;
using System.Text.Json;
using Ariadne.Core.Execution;
using Ariadne.Core.Parameters;
using Xunit;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// <b>The round-trip oracle</b> — the differentiator proof, the PICASSO-GnuCOBOL analogue for this connector.
/// For <em>every</em> supported type it drives the <b>whole stack end-to-end</b> against the <b>live Neo4j</b>
/// (<see cref="CypherExecutor.RunCypherRead"/> of <c>RETURN $p AS p</c> with a typed
/// <see cref="CypherParameter"/>) and asserts <see cref="CypherExecutionResult.RecordsJson"/> equals the
/// documented canonical JSON. That single loop proves the <b>parameter mapper</b> (Features 01–03), the
/// <b>real Neo4j server</b>, and the <b>result serializer</b> (Features 04–06) all agree — a mocked test
/// cannot. Graph types (which can't be bound as parameters) are proven by create-then-read-back, asserting
/// the envelope structure + labels + properties (never the non-deterministic <c>elementId</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Env-gated + skippable</b> (<see cref="RequiresNeo4jFactAttribute"/>): connection comes only from
/// <c>NEO4J_TEST_URI</c>/<c>_USER</c>/<c>_PASSWORD</c>; every test skips cleanly when they are unset, so CI
/// without a server stays green. <b>Isolation:</b> the graph tests create only <c>:AriadneOracleTest</c> data
/// and delete it; the shared <see cref="Neo4jLiveOracleCollection"/> fixture serializes the live classes and
/// tears the label down once at the end.
/// </para>
/// <para>
/// <b>Coverage of the §3 parameter map</b> (design/01-parameters.md): String, Integer, Float (incl. the
/// documented <c>Decimal→Float</c> loss), Boolean, Null, Date, Time (incl. the 100-ns fractional boundary),
/// DateTime, ZonedDateTime (named zone <b>and</b> fixed offset), Bytes, List, Map, Json (nested) — all
/// covered. The deferred tags <c>Duration</c>/<c>Point</c>/<c>OffsetTime</c> are deliberately NOT covered:
/// they fail loud by design (not supported), so there is nothing to round-trip.
/// </para>
/// <para>
/// <b>Coverage of the §3 result map</b> (design/02-results.md): Null, Boolean, Integer, Float, String, Bytes,
/// Date, LocalTime, LocalDateTime, ZonedDateTime, List, Map, Node, Relationship, Path — all covered. Deferred
/// Duration/Point/OffsetTime again out of scope (fail-loud, unit-tested elsewhere). The §5 null-collapse and
/// §7 type-stability behaviours are documentation of a platform limitation / a sampler concern, not a
/// serializer output shape, so they are out of this oracle's remit.
/// </para>
/// </remarks>
[Collection(Neo4jLiveOracleCollection.Name)]
public sealed class CypherRoundTripOracleTests
{
    private readonly CypherExecutorIntegrationTests.OracleFixture _fx;

    public CypherRoundTripOracleTests(CypherExecutorIntegrationTests.OracleFixture fx) => _fx = fx;

    private static IReadOnlyList<CypherParameter> Bind(CypherParameter p) => new[] { p };
    private static IReadOnlyList<CypherParameter> NoParams() => Array.Empty<CypherParameter>();

    /// <summary>Runs <c>RETURN $p AS p</c> binding <paramref name="p"/> and asserts the exact records JSON.</summary>
    private void AssertReturn(CypherParameter p, string expectedRecordsJson)
    {
        var result = _fx.Executor.RunCypherRead(_fx.Config, "RETURN $p AS p", Bind(p));
        Assert.Equal(new[] { "p" }, result.Columns);
        Assert.Equal(expectedRecordsJson, result.RecordsJson);
    }

    // ---------------------------------------------------------------------------------------------
    // Scalar leaf types — exact canonical JSON, whole stack, live server.
    // ---------------------------------------------------------------------------------------------

    [RequiresNeo4jFact]
    public void String_round_trips() =>
        AssertReturn(new CypherParameter { Name = "p", Type = "String", StringValue = "hi" }, "[{\"p\":\"hi\"}]");

    [RequiresNeo4jFact]
    public void Integer_round_trips() =>
        AssertReturn(new CypherParameter { Name = "p", Type = "Integer", IntegerValue = 42 }, "[{\"p\":42}]");

    [RequiresNeo4jFact]
    public void Float_round_trips() =>
        AssertReturn(new CypherParameter { Name = "p", Type = "Float", FloatValue = 3.5m }, "[{\"p\":3.5}]");

    /// <summary>
    /// <b>Documented loss — Decimal→Float (spec §5).</b> Neo4j has no decimal type, so a
    /// <see cref="decimal"/> maps to IEEE-754 <c>double</c>. The oracle asserts the <em>lossy</em> result
    /// (the round-tripped value is NOT the original 20-significant-digit decimal): the low-order digits
    /// <c>…7890</c> are gone — <c>1.2345678901234567890</c> comes back as <c>1.2345678901234567</c>. This is
    /// the stated downgrade, not a bug (String is the lossless alternative for exact decimals).
    /// </summary>
    [RequiresNeo4jFact]
    public void Float_from_high_precision_decimal_is_documented_lossy() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Float", FloatValue = 1.2345678901234567890m },
            "[{\"p\":1.2345678901234567}]");

    /// <summary>
    /// <b>Documented loss — Decimal→Float (spec §5), large magnitude.</b> A large exact integer-valued
    /// decimal likewise downgrades to <c>double</c> and returns in scientific notation with a rounded final
    /// digit (<c>…5678</c> → <c>…5680</c>, rendered <c>1.2345678901234568E+17</c>) — again the documented
    /// downgrade, asserted exactly as the loss it is.
    /// </summary>
    [RequiresNeo4jFact]
    public void Float_from_large_decimal_is_documented_lossy() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Float", FloatValue = 123456789012345678m },
            "[{\"p\":1.2345678901234568E+17}]");

    [RequiresNeo4jFact]
    public void Boolean_round_trips() =>
        AssertReturn(new CypherParameter { Name = "p", Type = "Boolean", BooleanValue = true }, "[{\"p\":true}]");

    /// <summary>Explicit Null → JSON <c>null</c> (spec §5: emitted, never omitted).</summary>
    [RequiresNeo4jFact]
    public void Null_round_trips() =>
        AssertReturn(new CypherParameter { Name = "p", Type = "Null" }, "[{\"p\":null}]");

    // ---------------------------------------------------------------------------------------------
    // Temporal leaf types.
    // ---------------------------------------------------------------------------------------------

    [RequiresNeo4jFact]
    public void Date_round_trips() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Date", DateTimeValue = new DateTime(2024, 9, 1) },
            "[{\"p\":\"2024-09-01\"}]");

    [RequiresNeo4jFact]
    public void Time_round_trips() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Time", DateTimeValue = new DateTime(2020, 1, 1, 10, 30, 0) },
            "[{\"p\":\"10:30:00\"}]");

    /// <summary>
    /// <b>Temporal 100-ns boundary (documented precision limit).</b> A CLR <see cref="DateTime"/>'s finest
    /// resolution is one 100-ns tick; that maps to Neo4j's nanosecond field as a multiple of 100 and comes
    /// back <em>exactly</em> (<c>1234567</c> ticks → <c>.1234567</c>). The documented loss is the mirror
    /// direction: a Neo4j value carrying genuine sub-100-ns precision cannot be produced from CLR here and
    /// fails loud on read (asserted in the serializer unit tests). So this round-trip is lossless up to the
    /// pinned 7-digit boundary — the boundary itself, proven live.
    /// </summary>
    [RequiresNeo4jFact]
    public void Time_with_100ns_fraction_round_trips_at_the_boundary() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Time", DateTimeValue = new DateTime(2020, 1, 1, 10, 30, 0).AddTicks(1234567) },
            "[{\"p\":\"10:30:00.1234567\"}]");

    [RequiresNeo4jFact]
    public void DateTime_round_trips_zoneless() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "DateTime", DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0) },
            "[{\"p\":\"2024-09-01T10:30:00\"}]");

    /// <summary>ZonedDateTime with a <b>named</b> IANA zone → the <c>{value,zone}</c> shape, zone preserved.</summary>
    [RequiresNeo4jFact]
    public void ZonedDateTime_with_named_zone_round_trips() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "ZonedDateTime", DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0), ZoneId = "Europe/Brussels" },
            "[{\"p\":{\"value\":\"2024-09-01T10:30:00\",\"zone\":\"Europe/Brussels\"}}]");

    /// <summary>ZonedDateTime with a <b>fixed offset</b> (+120 min) → zone rendered as <c>+02:00</c>.</summary>
    [RequiresNeo4jFact]
    public void ZonedDateTime_with_fixed_offset_round_trips() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "ZonedDateTime", DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0), OffsetMinutes = 120 },
            "[{\"p\":{\"value\":\"2024-09-01T10:30:00\",\"zone\":\"+02:00\"}}]");

    // ---------------------------------------------------------------------------------------------
    // Bytes + composites.
    // ---------------------------------------------------------------------------------------------

    [RequiresNeo4jFact]
    public void Bytes_round_trips_as_base64() =>
        AssertReturn(
            new CypherParameter { Name = "p", Type = "Bytes", BytesValue = new byte[] { 1, 2, 3, 4 } },
            "[{\"p\":\"AQIDBA==\"}]");

    /// <summary>A flat list of heterogeneous scalars → an ordered JSON array (element order is preserved live).</summary>
    [RequiresNeo4jFact]
    public void List_of_scalars_round_trips() =>
        AssertReturn(
            new CypherParameter
            {
                Name = "p",
                Type = "List",
                ListElements = new CypherListElement[]
                {
                    new CypherListElement { Type = "Integer", IntegerValue = 1 },
                    new CypherListElement { Type = "String", StringValue = "two" },
                    new CypherListElement { Type = "Boolean", BooleanValue = true },
                },
            },
            "[{\"p\":[1,\"two\",true]}]");

    /// <summary>
    /// A flat map of scalars → a JSON object. <b>Oracle finding (not a discrepancy):</b> Neo4j returns map
    /// keys in its own <em>server-determined</em> order (here <c>gamma, alpha, beta</c> — neither insertion
    /// nor alphabetical), and our serializer faithfully passes that order through. The order is stable for a
    /// given server, so the exact assertion holds; a caller must not assume map-key order (documented).
    /// </summary>
    [RequiresNeo4jFact]
    public void Map_of_scalars_round_trips_in_server_key_order() =>
        AssertReturn(
            new CypherParameter
            {
                Name = "p",
                Type = "Map",
                MapEntries = new CypherMapEntry[]
                {
                    new CypherMapEntry { Key = "alpha", Type = "Integer", IntegerValue = 1 },
                    new CypherMapEntry { Key = "beta", Type = "String", StringValue = "x" },
                    new CypherMapEntry { Key = "gamma", Type = "Boolean", BooleanValue = false },
                },
            },
            "[{\"p\":{\"gamma\":false,\"alpha\":1,\"beta\":\"x\"}}]");

    /// <summary>
    /// The <c>Json</c> escape hatch — arbitrary nesting (list-of-maps, each map holding a nested list) that the
    /// flat List/Map cannot express — round-trips through the recursive typed-JSON builder and comes back as
    /// the nested JSON, proving depth is preserved end to end.
    /// </summary>
    [RequiresNeo4jFact]
    public void Json_nested_structure_round_trips() =>
        AssertReturn(
            new CypherParameter
            {
                Name = "p",
                Type = "Json",
                JsonValue =
                    "{\"$type\":\"List\",\"$value\":[" +
                    "{\"$type\":\"Map\",\"$value\":{\"id\":{\"$type\":\"Integer\",\"$value\":1}," +
                    "\"tags\":{\"$type\":\"List\",\"$value\":[{\"$type\":\"String\",\"$value\":\"a\"},{\"$type\":\"String\",\"$value\":\"b\"}]}}}," +
                    "{\"$type\":\"Map\",\"$value\":{\"id\":{\"$type\":\"Integer\",\"$value\":2}," +
                    "\"tags\":{\"$type\":\"List\",\"$value\":[]}}}" +
                    "]}",
            },
            "[{\"p\":[{\"id\":1,\"tags\":[\"a\",\"b\"]},{\"id\":2,\"tags\":[]}]}]");

    // ---------------------------------------------------------------------------------------------
    // Multi-record / multi-column envelope.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A 2-record, 2-column result — proves the record-array envelope and the column ordering live: the JSON
    /// is an array of per-record objects keyed by column name, in column order.
    /// </summary>
    [RequiresNeo4jFact]
    public void Multi_record_multi_column_envelope()
    {
        var result = _fx.Executor.RunCypherRead(
            _fx.Config,
            "UNWIND [{a:1,b:'x'},{a:2,b:'y'}] AS row RETURN row.a AS a, row.b AS b",
            NoParams());

        Assert.Equal(new[] { "a", "b" }, result.Columns);
        Assert.Equal("[{\"a\":1,\"b\":\"x\"},{\"a\":2,\"b\":\"y\"}]", result.RecordsJson);
    }

    // ---------------------------------------------------------------------------------------------
    // Graph types — create then read back; assert envelope structure + labels + properties, never the
    // non-deterministic elementId (spec §4).
    // ---------------------------------------------------------------------------------------------

    /// <summary>Node envelope: <c>{elementId, labels, properties}</c> — id present but not value-asserted.</summary>
    [RequiresNeo4jFact]
    public void Node_reads_back_as_the_canonical_envelope()
    {
        ClearOracleData();
        _fx.Executor.RunCypherWrite(
            _fx.Config, "CREATE (n:AriadneOracleTest {name:'Alice', born:1990})", NoParams());

        var read = _fx.Executor.RunCypherRead(
            _fx.Config, "MATCH (n:AriadneOracleTest {name:'Alice'}) RETURN n", NoParams());

        Assert.Equal(new[] { "n" }, read.Columns);
        JsonElement node = SingleRecordColumn(read.RecordsJson, "n");

        AssertNonEmptyElementId(node);
        Assert.Equal(new[] { "AriadneOracleTest" }, StringArray(node, "labels"));

        JsonElement props = node.GetProperty("properties");
        Assert.Equal("Alice", props.GetProperty("name").GetString());
        Assert.Equal(1990, props.GetProperty("born").GetInt64());

        ClearOracleData();
    }

    /// <summary>
    /// Relationship envelope: <c>{elementId, type, startNodeElementId, endNodeElementId, properties}</c> —
    /// the three ids are asserted present (non-empty) but not by value; type + properties are exact.
    /// </summary>
    [RequiresNeo4jFact]
    public void Relationship_reads_back_as_the_canonical_envelope()
    {
        ClearOracleData();
        _fx.Executor.RunCypherWrite(
            _fx.Config,
            "CREATE (:AriadneOracleTest {name:'Alice'})-[:KNOWS {since:2020}]->(:AriadneOracleTest {name:'Bob'})",
            NoParams());

        var read = _fx.Executor.RunCypherRead(
            _fx.Config,
            "MATCH (:AriadneOracleTest {name:'Alice'})-[r:KNOWS]->(:AriadneOracleTest {name:'Bob'}) RETURN r",
            NoParams());

        Assert.Equal(new[] { "r" }, read.Columns);
        JsonElement rel = SingleRecordColumn(read.RecordsJson, "r");

        AssertNonEmptyElementId(rel);
        Assert.Equal("KNOWS", rel.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(rel.GetProperty("startNodeElementId").GetString()));
        Assert.False(string.IsNullOrEmpty(rel.GetProperty("endNodeElementId").GetString()));
        Assert.Equal(2020, rel.GetProperty("properties").GetProperty("since").GetInt64());

        ClearOracleData();
    }

    /// <summary>
    /// Path envelope: <c>{nodes:[…], relationships:[…]}</c> in traversal order — 2 nodes, 1 relationship,
    /// each a canonical sub-envelope; labels/type/properties asserted, ids only for presence.
    /// </summary>
    [RequiresNeo4jFact]
    public void Path_reads_back_as_the_canonical_envelope()
    {
        ClearOracleData();
        _fx.Executor.RunCypherWrite(
            _fx.Config,
            "CREATE (:AriadneOracleTest {name:'Alice'})-[:KNOWS {since:2020}]->(:AriadneOracleTest {name:'Bob'})",
            NoParams());

        var read = _fx.Executor.RunCypherRead(
            _fx.Config,
            "MATCH path=(:AriadneOracleTest {name:'Alice'})-[:KNOWS]->(:AriadneOracleTest {name:'Bob'}) RETURN path",
            NoParams());

        Assert.Equal(new[] { "path" }, read.Columns);
        JsonElement path = SingleRecordColumn(read.RecordsJson, "path");

        JsonElement nodes = path.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal("Alice", nodes[0].GetProperty("properties").GetProperty("name").GetString());
        Assert.Equal("Bob", nodes[1].GetProperty("properties").GetProperty("name").GetString());
        foreach (JsonElement n in nodes.EnumerateArray())
        {
            AssertNonEmptyElementId(n);
            Assert.Equal(new[] { "AriadneOracleTest" }, StringArray(n, "labels"));
        }

        JsonElement rels = path.GetProperty("relationships");
        Assert.Equal(1, rels.GetArrayLength());
        Assert.Equal("KNOWS", rels[0].GetProperty("type").GetString());
        AssertNonEmptyElementId(rels[0]);
        Assert.Equal(2020, rels[0].GetProperty("properties").GetProperty("since").GetInt64());

        ClearOracleData();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private void ClearOracleData() =>
        _fx.Executor.RunCypherWrite(_fx.Config, "MATCH (n:AriadneOracleTest) DETACH DELETE n", NoParams());

    /// <summary>Parses a single-record <c>RecordsJson</c> array and returns the named column's element.</summary>
    private static JsonElement SingleRecordColumn(string recordsJson, string column)
    {
        using var doc = JsonDocument.Parse(recordsJson);
        JsonElement records = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, records.ValueKind);
        Assert.Equal(1, records.GetArrayLength());
        // Clone so the value survives disposal of the JsonDocument.
        return records[0].GetProperty(column).Clone();
    }

    private static void AssertNonEmptyElementId(JsonElement envelope)
    {
        Assert.True(envelope.TryGetProperty("elementId", out JsonElement id), "envelope has no elementId");
        Assert.Equal(JsonValueKind.String, id.ValueKind);
        Assert.False(string.IsNullOrEmpty(id.GetString()), "elementId is empty");
    }

    private static string[] StringArray(JsonElement obj, string property)
    {
        var list = new List<string>();
        foreach (JsonElement e in obj.GetProperty(property).EnumerateArray())
            list.Add(e.GetString()!);
        return list.ToArray();
    }
}
