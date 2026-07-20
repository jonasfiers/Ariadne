using System;
using System.Collections.Generic;
using Ariadne.Core.Parameters;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Parameters;

/// <summary>
/// Unit tests for Feature 03 — the recursive <c>Json</c> typed-escape-hatch parameter. Pure logic — no
/// server, no driver/session, no mocking libraries. Covers: arbitrary nesting producing the correct
/// nested <see cref="IList{T}"/>/<see cref="IDictionary{TKey,TValue}"/> of driver types;
/// <b>cross-consistency</b> with the Feature 01 scalar path (a <c>Json</c> scalar node must equal the
/// scalar-path value, byte-for-byte, incl. <c>DateTime.Kind</c>-independence and sub-second precision);
/// and every fail-loud rule with a JSON path in the message.
/// </summary>
public class CypherJsonParameterTests
{
    private static object? MapJson(string? json)
        => CypherParameterMapper.BuildParameters(new[]
        {
            new CypherParameter { Name = "j", Type = "Json", JsonValue = json }
        })["j"];

    private static object? MapScalar(CypherParameter p)
        => CypherParameterMapper.BuildParameters(new[] { p })[p.Name];

    private static CypherParameterException Throws(string? json)
        => Assert.Throws<CypherParameterException>(() => MapJson(json));

    // ---------- scalar $type happy paths (type AND value) ----------

    [Fact]
    public void Json_String_maps_to_string()
    {
        var v = MapJson("""{"$type":"String","$value":"hi"}""");
        Assert.Equal("hi", Assert.IsType<string>(v));
    }

    [Fact]
    public void Json_Integer_maps_to_long()
    {
        var v = MapJson("""{"$type":"Integer","$value":9000000000}""");
        Assert.Equal(9_000_000_000L, Assert.IsType<long>(v));
    }

    [Fact]
    public void Json_Float_maps_to_double()
    {
        var v = MapJson("""{"$type":"Float","$value":3.5}""");
        Assert.Equal(3.5d, Assert.IsType<double>(v));
    }

    [Fact]
    public void Json_Float_accepts_integral_json_number()
    {
        var v = MapJson("""{"$type":"Float","$value":4}""");
        Assert.Equal(4.0d, Assert.IsType<double>(v));
    }

    [Fact]
    public void Json_Boolean_maps_to_bool()
    {
        var v = MapJson("""{"$type":"Boolean","$value":true}""");
        Assert.True(Assert.IsType<bool>(v));
    }

    [Fact]
    public void Json_Null_maps_to_null()
    {
        Assert.Null(MapJson("""{"$type":"Null"}"""));
    }

    [Fact]
    public void Json_Bytes_maps_to_byte_array()
    {
        var bytes = new byte[] { 1, 2, 3, 250 };
        var b64 = Convert.ToBase64String(bytes);
        var v = MapJson($$"""{"$type":"Bytes","$value":"{{b64}}"}""");
        Assert.Equal(bytes, Assert.IsType<byte[]>(v));
    }

    // ---------- cross-consistency: Json scalar == Feature 01 scalar ----------

    [Fact]
    public void CrossConsistency_Date()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "d", Type = "Date", DateTimeValue = new DateTime(2024, 9, 1, 13, 45, 30)
        });
        var json = MapJson("""{"$type":"Date","$value":"2024-09-01"}""");
        Assert.IsType<LocalDate>(json);
        Assert.Equal(scalar, json);
    }

    [Fact]
    public void CrossConsistency_Time()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "t", Type = "Time", DateTimeValue = new DateTime(2024, 1, 1, 10, 30, 0)
        });
        var json = MapJson("""{"$type":"Time","$value":"10:30:00"}""");
        Assert.IsType<LocalTime>(json);
        Assert.Equal(scalar, json);
    }

    [Fact]
    public void CrossConsistency_DateTime()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "dt", Type = "DateTime", DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0)
        });
        var json = MapJson("""{"$type":"DateTime","$value":"2024-09-01T10:30:00"}""");
        Assert.IsType<LocalDateTime>(json);
        Assert.Equal(scalar, json);
    }

    [Fact]
    public void CrossConsistency_ZonedDateTime_from_zone()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime",
            DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0), ZoneId = "Europe/Brussels"
        });
        var json = MapJson(
            """{"$type":"ZonedDateTime","$value":"2024-09-01T10:30:00","$zone":"Europe/Brussels"}""");
        var zdt = Assert.IsType<ZonedDateTime>(json);
        Assert.Equal(scalar, json);
        Assert.Equal("Europe/Brussels", ((ZoneId)zdt.Zone).Id);
    }

    [Fact]
    public void CrossConsistency_ZonedDateTime_from_offset()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime",
            DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0), OffsetMinutes = 120
        });
        var json = MapJson(
            """{"$type":"ZonedDateTime","$value":"2024-09-01T10:30:00","$offsetMinutes":120}""");
        var zdt = Assert.IsType<ZonedDateTime>(json);
        Assert.Equal(scalar, json);
        Assert.Equal(120 * 60, zdt.OffsetSeconds);
    }

    [Fact]
    public void CrossConsistency_ZonedDateTime_is_Kind_independent()
    {
        // Scalar carrier is Kind=Utc; the Json string is zoneless (Kind=Unspecified). Both must normalize
        // to the same literal wall-clock — 10:30 in Europe/Brussels, never UTC-shifted.
        var scalar = MapScalar(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime",
            DateTimeValue = new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Utc),
            ZoneId = "Europe/Brussels"
        });
        var json = MapJson(
            """{"$type":"ZonedDateTime","$value":"2024-09-01T10:30:00","$zone":"Europe/Brussels"}""");
        var zdt = Assert.IsType<ZonedDateTime>(json);
        Assert.Equal(10, zdt.Hour);
        Assert.Equal(scalar, json);
    }

    [Fact]
    public void CrossConsistency_subsecond_precision_Time()
    {
        // 123456700 ns == 1_234_567 ticks-of-100ns.
        var scalar = MapScalar(new CypherParameter
        {
            Name = "t", Type = "Time",
            DateTimeValue = new DateTime(2024, 1, 1, 0, 0, 0).AddTicks(1_234_567)
        });
        var json = MapJson("""{"$type":"Time","$value":"00:00:00.123456700"}""");
        var lt = Assert.IsType<LocalTime>(json);
        Assert.Equal(123_456_700, lt.Nanosecond);
        Assert.Equal(scalar, json);
    }

    [Fact]
    public void CrossConsistency_subsecond_precision_ZonedDateTime()
    {
        var scalar = MapScalar(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime",
            DateTimeValue = new DateTime(2024, 9, 1, 13, 45, 30).AddTicks(1_234_567), OffsetMinutes = 60
        });
        var json = MapJson(
            """{"$type":"ZonedDateTime","$value":"2024-09-01T13:45:30.123456700","$offsetMinutes":60}""");
        var zdt = Assert.IsType<ZonedDateTime>(json);
        Assert.Equal(123_456_700, zdt.Nanosecond);
        Assert.Equal(scalar, json);
    }

    // ---------- nesting ----------

    [Fact]
    public void Json_flat_List_of_scalars()
    {
        var v = MapJson("""
            {"$type":"List","$value":[
                {"$type":"Integer","$value":1},
                {"$type":"Integer","$value":2},
                {"$type":"String","$value":"three"}
            ]}
            """);
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Equal(new object?[] { 1L, 2L, "three" }, list);
    }

    [Fact]
    public void Json_flat_Map_of_scalars()
    {
        var v = MapJson("""
            {"$type":"Map","$value":{
                "name":{"$type":"String","$value":"neo"},
                "level":{"$type":"Integer","$value":42}
            }}
            """);
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(v);
        Assert.Equal("neo", map["name"]);
        Assert.Equal(42L, map["level"]);
    }

    [Fact]
    public void Json_deeply_nested_list_of_maps_of_lists()
    {
        var v = MapJson("""
            {"$type":"List","$value":[
                {"$type":"Map","$value":{
                    "id":{"$type":"Integer","$value":7},
                    "tags":{"$type":"List","$value":[
                        {"$type":"String","$value":"a"},
                        {"$type":"List","$value":[
                            {"$type":"Boolean","$value":true},
                            {"$type":"Null"}
                        ]}
                    ]}
                }}
            ]}
            """);

        var outer = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Single(outer);
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(outer[0]);
        Assert.Equal(7L, map["id"]);
        var tags = Assert.IsAssignableFrom<IList<object?>>(map["tags"]);
        Assert.Equal("a", tags[0]);
        var innerList = Assert.IsAssignableFrom<IList<object?>>(tags[1]);
        Assert.Equal(true, innerList[0]);
        Assert.Null(innerList[1]);
    }

    [Fact]
    public void Json_empty_List_and_Map_are_valid()
    {
        Assert.Empty(Assert.IsAssignableFrom<IList<object?>>(MapJson("""{"$type":"List","$value":[]}""")));
        Assert.Empty(Assert.IsAssignableFrom<IDictionary<string, object?>>(
            MapJson("""{"$type":"Map","$value":{}}""")));
    }

    [Fact]
    public void Json_temporal_survives_nesting_and_stays_driver_type()
    {
        var v = MapJson("""
            {"$type":"List","$value":[
                {"$type":"Date","$value":"2024-09-01"}
            ]}
            """);
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.IsType<LocalDate>(list[0]);
    }

    [Fact]
    public void Json_case_insensitive_type_tags()
    {
        Assert.Equal(1L, MapJson("""{"$type":"integer","$value":1}"""));
        Assert.Equal(1L, MapJson("""{"$type":"INTEGER","$value":1}"""));
    }

    // ---------- fail-loud: structure ----------

    [Fact]
    public void Json_null_carrier_throws()
    {
        var ex = Throws(null);
        Assert.Contains("JsonValue", ex.Message);
    }

    [Fact]
    public void Json_invalid_json_throws()
    {
        var ex = Throws("{ not json ]");
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Json_non_object_node_throws()
    {
        var ex = Throws("42");
        Assert.Contains("$", ex.Message);
        Assert.Contains("must be a JSON object", ex.Message);
    }

    [Fact]
    public void Json_missing_type_throws_with_path()
    {
        var ex = Throws("""{"$value":5}""");
        Assert.Contains("$type", ex.Message);
        Assert.Contains("$", ex.Message);
    }

    [Fact]
    public void Json_non_string_type_throws()
    {
        var ex = Throws("""{"$type":5,"$value":5}""");
        Assert.Contains("non-string", ex.Message);
    }

    [Fact]
    public void Json_missing_value_throws()
    {
        var ex = Throws("""{"$type":"Integer"}""");
        Assert.Contains("$value", ex.Message);
    }

    // ---------- fail-loud: unknown / deferred / nested-Json ----------

    [Theory]
    [InlineData("Duration")]
    [InlineData("Point")]
    [InlineData("OffsetTime")]
    public void Json_deferred_types_throw(string type)
    {
        var ex = Throws($$"""{"$type":"{{type}}","$value":"x"}""");
        Assert.Contains(type, ex.Message);
    }

    [Fact]
    public void Json_unknown_type_throws()
    {
        var ex = Throws("""{"$type":"Wibble","$value":1}""");
        Assert.Contains("Wibble", ex.Message);
        Assert.Contains("unknown", ex.Message);
    }

    [Fact]
    public void Json_nested_Json_type_throws()
    {
        var ex = Throws("""{"$type":"Json","$value":"{}"}""");
        Assert.Contains("top level", ex.Message);
    }

    // ---------- fail-loud: wrong $value for $type, with path ----------

    [Fact]
    public void Json_Integer_with_string_value_throws()
    {
        var ex = Throws("""{"$type":"Integer","$value":"abc"}""");
        Assert.Contains("Integer", ex.Message);
    }

    [Fact]
    public void Json_Integer_with_fractional_value_throws()
    {
        var ex = Throws("""{"$type":"Integer","$value":3.5}""");
        Assert.Contains("Integer", ex.Message);
    }

    [Fact]
    public void Json_Boolean_with_number_value_throws()
    {
        Assert.Throws<CypherParameterException>(() => MapJson("""{"$type":"Boolean","$value":1}"""));
    }

    [Fact]
    public void Json_Date_with_bad_value_throws()
    {
        var ex = Throws("""{"$type":"Date","$value":"not-a-date"}""");
        Assert.Contains("ISO-8601", ex.Message);
    }

    [Fact]
    public void Json_Bytes_with_bad_base64_throws()
    {
        var ex = Throws("""{"$type":"Bytes","$value":"not base64!!"}""");
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void Json_List_value_not_array_throws()
    {
        var ex = Throws("""{"$type":"List","$value":{}}""");
        Assert.Contains("array", ex.Message);
    }

    [Fact]
    public void Json_Map_value_not_object_throws()
    {
        var ex = Throws("""{"$type":"Map","$value":[]}""");
        Assert.Contains("object", ex.Message);
    }

    // ---------- fail-loud: ZonedDateTime zone/offset ----------

    [Fact]
    public void Json_ZonedDateTime_without_zone_or_offset_throws()
    {
        var ex = Throws("""{"$type":"ZonedDateTime","$value":"2024-09-01T10:30:00"}""");
        Assert.Contains("neither ZoneId nor OffsetMinutes", ex.Message);
    }

    [Fact]
    public void Json_ZonedDateTime_invalid_zone_throws()
    {
        var ex = Throws(
            """{"$type":"ZonedDateTime","$value":"2024-09-01T10:30:00","$zone":"Not/AZone"}""");
        Assert.Contains("Not/AZone", ex.Message);
    }

    // ---------- fail-loud: Map keys ----------

    [Fact]
    public void Json_Map_empty_key_throws()
    {
        var ex = Throws("""{"$type":"Map","$value":{"":{"$type":"Integer","$value":1}}}""");
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void Json_Map_duplicate_key_throws()
    {
        var ex = Throws("""
            {"$type":"Map","$value":{
                "k":{"$type":"Integer","$value":1},
                "k":{"$type":"Integer","$value":2}
            }}
            """);
        Assert.Contains("duplicate", ex.Message);
    }

    // ---------- JSON path pinpoints the offender ----------

    [Fact]
    public void Json_error_path_points_into_nested_list_element()
    {
        // Offender is the untagged/second element of the top-level list.
        var ex = Throws("""
            {"$type":"List","$value":[
                {"$type":"Integer","$value":1},
                {"$type":"Integer","$value":"bad"}
            ]}
            """);
        Assert.Contains("$[1]", ex.Message);
    }

    [Fact]
    public void Json_error_path_points_into_nested_map_value()
    {
        var ex = Throws("""
            {"$type":"Map","$value":{
                "outer":{"$type":"Map","$value":{
                    "inner":{"$type":"Duration","$value":"x"}
                }}
            }}
            """);
        Assert.Contains("$.outer.inner", ex.Message);
    }
}
