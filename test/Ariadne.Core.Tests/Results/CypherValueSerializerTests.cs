using System;
using System.Collections.Generic;
using System.Text.Json;
using Ariadne.Core.Results;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Results;

/// <summary>
/// Unit tests for <see cref="CypherValueSerializer"/> — the result-side leaf serializer. Pure logic:
/// real driver values are constructed directly (no server, no session, no mocking libraries) and the
/// exact emitted JSON is asserted. Temporals are verified to be host-timezone independent, fractions are
/// verified to appear only when non-zero at 100-ns precision, and every unsupported type is verified to
/// throw the named <see cref="CypherResultException"/>.
/// </summary>
public class CypherValueSerializerTests
{
    // ---- scalar leaves ----

    [Fact]
    public void Null_serializes_to_json_null()
        => Assert.Equal("null", CypherValueSerializer.Serialize(null));

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Bool_serializes_to_json_bool(bool value, string expected)
        => Assert.Equal(expected, CypherValueSerializer.Serialize(value));

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(42L, "42")]
    [InlineData(-7L, "-7")]
    [InlineData(9_000_000_000L, "9000000000")]     // beyond Int32 — Neo4j Integer is 64-bit
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    public void Long_serializes_to_json_integer(long value, string expected)
        => Assert.Equal(expected, CypherValueSerializer.Serialize(value));

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-3.25, "-3.25")]
    public void Double_serializes_to_json_number(double value, string expected)
        => Assert.Equal(expected, CypherValueSerializer.Serialize(value));

    [Fact]
    public void Double_is_distinct_from_long_it_uses_the_double_carrier()
    {
        // A whole-valued double still travels the double path; System.Text.Json renders it without a
        // fractional part but it is dispatched as a Float, not an Integer.
        Assert.Equal("5", CypherValueSerializer.Serialize(5.0));
    }

    [Theory]
    [InlineData("hello", "\"hello\"")]
    [InlineData("", "\"\"")]
    public void String_serializes_to_json_string(string value, string expected)
        => Assert.Equal(expected, CypherValueSerializer.Serialize(value));

    [Fact]
    public void String_with_special_chars_is_json_escaped()
    {
        var json = CypherValueSerializer.Serialize("a\"b\\c");
        // Round-trips back to the original through a real JSON reader.
        Assert.Equal("a\"b\\c", JsonDocument.Parse(json).RootElement.GetString());
    }

    [Fact]
    public void Bytes_serialize_to_base64_string()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFF, 0x10 };
        var json = CypherValueSerializer.Serialize(bytes);
        Assert.Equal("\"" + Convert.ToBase64String(bytes) + "\"", json);
        // And it deserializes back to the same bytes.
        Assert.Equal(bytes, JsonDocument.Parse(json).RootElement.GetBytesFromBase64());
    }

    [Fact]
    public void Empty_bytes_serialize_to_empty_base64_string()
        => Assert.Equal("\"\"", CypherValueSerializer.Serialize(Array.Empty<byte>()));

    // ---- non-finite doubles fail loud (JSON has no NaN/Infinity) ----

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_double_throws(double value)
    {
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(value));
        Assert.Contains("Float", ex.Message);
    }

    // ---- LocalDate ----

    [Fact]
    public void LocalDate_serializes_iso()
        => Assert.Equal("\"2024-09-01\"", CypherValueSerializer.Serialize(new LocalDate(2024, 9, 1)));

    [Fact]
    public void LocalDate_pads_single_digit_month_and_day()
        => Assert.Equal("\"2024-03-07\"", CypherValueSerializer.Serialize(new LocalDate(2024, 3, 7)));

    // ---- LocalTime (fraction only when non-zero) ----

    [Fact]
    public void LocalTime_without_fraction_omits_it()
        => Assert.Equal("\"10:30:00\"", CypherValueSerializer.Serialize(new LocalTime(10, 30, 0)));

    [Fact]
    public void LocalTime_with_fraction_trims_trailing_zeros()
        // 123456700 ns = .1234567 (7 sig digits, no trailing zero); 100 ns = .0000001
        => Assert.Equal("\"10:30:15.1234567\"",
            CypherValueSerializer.Serialize(new LocalTime(10, 30, 15, 123456700)));

    [Fact]
    public void LocalTime_with_short_fraction()
        // 500,000,000 ns = half a second → ".5" after trimming
        => Assert.Equal("\"10:30:15.5\"",
            CypherValueSerializer.Serialize(new LocalTime(10, 30, 15, 500_000_000)));

    [Fact]
    public void LocalTime_one_tick_fraction()
        // 100 ns = one CLR tick = the finest representable fraction → ".0000001"
        => Assert.Equal("\"00:00:00.0000001\"",
            CypherValueSerializer.Serialize(new LocalTime(0, 0, 0, 100)));

    [Fact]
    public void LocalTime_sub_100ns_precision_throws()
    {
        // 123456789 ns is not a multiple of 100 → cannot be represented in ≤7 digits → fail loud.
        var ex = Assert.Throws<CypherResultException>(
            () => CypherValueSerializer.Serialize(new LocalTime(10, 30, 15, 123456789)));
        Assert.Contains("sub-100-nanosecond", ex.Message);
    }

    // ---- LocalDateTime ----

    [Fact]
    public void LocalDateTime_serializes_zoneless_iso()
        => Assert.Equal("\"2024-09-01T10:30:00\"",
            CypherValueSerializer.Serialize(new LocalDateTime(2024, 9, 1, 10, 30, 0)));

    [Fact]
    public void LocalDateTime_with_fraction()
        => Assert.Equal("\"2024-09-01T10:30:00.001\"",
            CypherValueSerializer.Serialize(new LocalDateTime(2024, 9, 1, 10, 30, 0, 1_000_000)));

    // ---- ZonedDateTime: the pinned { value, zone } shape ----

    [Fact]
    public void ZonedDateTime_named_zone_emits_value_and_zone_id()
    {
        var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified), "Europe/Brussels");
        var json = CypherValueSerializer.Serialize(z);
        Assert.Equal("{\"value\":\"2024-09-01T10:30:00\",\"zone\":\"Europe/Brussels\"}", json);
    }

    [Fact]
    public void ZonedDateTime_fixed_offset_emits_offset_as_zone()
    {
        var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified), 2 * 3600);
        var json = CypherValueSerializer.Serialize(z);
        Assert.Equal("{\"value\":\"2024-09-01T10:30:00\",\"zone\":\"+02:00\"}", json);
    }

    [Fact]
    public void ZonedDateTime_negative_half_hour_offset()
    {
        // -5:30 offset (India-like magnitude, negative) → "-05:30"
        var z = new ZonedDateTime(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), -(5 * 3600 + 30 * 60));
        var json = CypherValueSerializer.Serialize(z);
        Assert.Equal("{\"value\":\"2024-01-01T00:00:00\",\"zone\":\"-05:30\"}", json);
    }

    [Fact]
    public void ZonedDateTime_zero_offset_renders_plus_zero()
    {
        var z = new ZonedDateTime(new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Unspecified), 0);
        var json = CypherValueSerializer.Serialize(z);
        Assert.Equal("{\"value\":\"2024-06-15T12:00:00\",\"zone\":\"+00:00\"}", json);
    }

    [Fact]
    public void ZonedDateTime_value_carries_fraction()
    {
        var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified).AddTicks(1), 2 * 3600);
        var json = CypherValueSerializer.Serialize(z);
        Assert.Equal("{\"value\":\"2024-09-01T10:30:00.0000001\",\"zone\":\"+02:00\"}", json);
    }

    [Fact]
    public void ZonedDateTime_shape_is_deserializable_object()
    {
        var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified), "Europe/Brussels");
        var root = JsonDocument.Parse(CypherValueSerializer.Serialize(z)).RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("2024-09-01T10:30:00", root.GetProperty("value").GetString());
        Assert.Equal("Europe/Brussels", root.GetProperty("zone").GetString());
    }

    // ---- host-timezone independence (design principle: the wall-clock must not shift with host TZ) ----

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]  // UTC-8/-7
    [InlineData("Asia/Tokyo")]           // UTC+9
    public void ZonedDateTime_wall_clock_does_not_depend_on_host_timezone(string tzId)
    {
        var original = TimeZoneInfo.Local;
        try
        {
            SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById(tzId));
            // Prove the override actually took effect, so the assertion below is not vacuous.
            Assert.Equal(tzId, TimeZoneInfo.Local.Id);

            var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified), "Europe/Brussels");
            var json = CypherValueSerializer.Serialize(z);

            // The literal local wall-clock is emitted verbatim, regardless of the host zone.
            Assert.Equal("{\"value\":\"2024-09-01T10:30:00\",\"zone\":\"Europe/Brussels\"}", json);
        }
        finally
        {
            SetLocalTimeZone(original);
        }
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Asia/Tokyo")]
    public void LocalDateTime_does_not_depend_on_host_timezone(string tzId)
    {
        var original = TimeZoneInfo.Local;
        try
        {
            SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById(tzId));
            Assert.Equal(tzId, TimeZoneInfo.Local.Id);
            Assert.Equal("\"2024-09-01T10:30:00\"",
                CypherValueSerializer.Serialize(new LocalDateTime(2024, 9, 1, 10, 30, 0)));
        }
        finally
        {
            SetLocalTimeZone(original);
        }
    }

    // ---- fail loud: unsupported leaf types name the runtime type ----

    [Fact]
    public void Duration_throws_naming_type()
        => AssertUnsupported(new Duration(1, 2, 3, 4), "Duration");

    [Fact]
    public void Point_throws_naming_type()
        => AssertUnsupported(new Point(7203, 1.0, 2.0), "Point");

    [Fact]
    public void OffsetTime_throws_naming_type()
        => AssertUnsupported(new OffsetTime(10, 30, 0, 2 * 3600), "OffsetTime");

    [Fact]
    public void List_stand_in_throws_naming_type()
        => AssertUnsupported(new List<object?> { 1L, 2L }, "List");

    [Fact]
    public void Dictionary_stand_in_throws_naming_type()
        => AssertUnsupported(new Dictionary<string, object?> { ["a"] = 1L }, "Dictionary");

    [Fact]
    public void Unknown_arbitrary_type_throws_naming_type()
        => AssertUnsupported(new Uri("https://example.org"), "Uri");

    [Fact]
    public void Int32_is_not_a_supported_leaf_and_throws()
        // Neo4j Integer arrives as long; a boxed int is NOT silently widened — it fails loud.
        => AssertUnsupported(5, "Int32");

    [Fact]
    public void Decimal_is_not_a_supported_leaf_and_throws()
        // Neo4j Float arrives as double; a boxed decimal is not accepted.
        => AssertUnsupported(1.5m, "Decimal");

    // ---- the Utf8JsonWriter entry point composes (used by Feature 05 for nested structures) ----

    [Fact]
    public void Write_entry_point_composes_inside_a_larger_document()
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            CypherValueSerializer.Write(writer, 1L);
            CypherValueSerializer.Write(writer, "two");
            CypherValueSerializer.Write(writer, new LocalDate(2024, 9, 1));
            CypherValueSerializer.Write(writer, null);
            writer.WriteEndArray();
        }
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal("[1,\"two\",\"2024-09-01\",null]", json);
    }

    [Fact]
    public void Write_null_writer_throws_argument_null()
        => Assert.Throws<ArgumentNullException>(() => CypherValueSerializer.Write(null!, 1L));

    // ---- helpers ----

    private static void AssertUnsupported(object value, string expectedTypeFragment)
    {
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(value));
        Assert.Contains(expectedTypeFragment, ex.Message);
    }

    /// <summary>
    /// Overrides <see cref="TimeZoneInfo.Local"/> for the duration of a host-TZ-independence test. The
    /// cached-data clear forces the runtime to re-read the override, so the assertion genuinely runs under
    /// the chosen zone rather than the machine default.
    /// </summary>
    private static void SetLocalTimeZone(TimeZoneInfo tz)
    {
        typeof(TimeZoneInfo)
            .GetField("s_cachedData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, null);
        var cachedDataType = typeof(TimeZoneInfo).Assembly.GetType("System.TimeZoneInfo+CachedData")!;
        var cachedData = Activator.CreateInstance(cachedDataType, nonPublic: true)!;
        cachedDataType.GetField("_localTimeZone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cachedData, tz);
        typeof(TimeZoneInfo)
            .GetField("s_cachedData", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, cachedData);
    }
}
