using System;
using System.Collections.Generic;
using System.Linq;
using Ariadne.Core.Parameters;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Parameters;

/// <summary>
/// Unit tests for <see cref="CypherParameterMapper.BuildParameters"/>. Pure logic — no server,
/// no driver/session, no mocking libraries. Each scalar tag is asserted for driver type and value;
/// every fail-loud rule is exercised.
/// </summary>
public class CypherParameterMapperTests
{
    private static object? MapOne(CypherParameter p)
        => CypherParameterMapper.BuildParameters(new[] { p })[p.Name];

    // ---- scalar happy paths: type AND value ----

    [Fact]
    public void String_maps_to_string()
    {
        var v = MapOne(new CypherParameter { Name = "s", Type = "String", StringValue = "hello" });
        Assert.IsType<string>(v);
        Assert.Equal("hello", v);
    }

    [Fact]
    public void Integer_maps_to_long()
    {
        var v = MapOne(new CypherParameter { Name = "i", Type = "Integer", IntegerValue = 9_000_000_000L });
        Assert.IsType<long>(v);
        Assert.Equal(9_000_000_000L, v);
    }

    [Fact]
    public void Float_maps_decimal_to_double()
    {
        var v = MapOne(new CypherParameter { Name = "f", Type = "Float", FloatValue = 3.5m });
        Assert.IsType<double>(v);
        Assert.Equal(3.5d, (double)v!);
    }

    [Fact]
    public void Boolean_maps_to_bool()
    {
        var v = MapOne(new CypherParameter { Name = "b", Type = "Boolean", BooleanValue = true });
        Assert.IsType<bool>(v);
        Assert.Equal(true, v);
    }

    [Fact]
    public void Date_maps_to_LocalDate_date_part_only()
    {
        var dt = new DateTime(2024, 9, 1, 13, 45, 30);
        var v = MapOne(new CypherParameter { Name = "d", Type = "Date", DateTimeValue = dt });
        var ld = Assert.IsType<LocalDate>(v);
        Assert.Equal(2024, ld.Year);
        Assert.Equal(9, ld.Month);
        Assert.Equal(1, ld.Day);
    }

    [Fact]
    public void Time_maps_to_LocalTime_time_part_only()
    {
        var dt = new DateTime(2024, 9, 1, 13, 45, 30);
        var v = MapOne(new CypherParameter { Name = "t", Type = "Time", DateTimeValue = dt });
        var lt = Assert.IsType<LocalTime>(v);
        Assert.Equal(13, lt.Hour);
        Assert.Equal(45, lt.Minute);
        Assert.Equal(30, lt.Second);
    }

    [Fact]
    public void DateTime_maps_to_LocalDateTime_all_components()
    {
        var dt = new DateTime(2024, 9, 1, 13, 45, 30);
        var v = MapOne(new CypherParameter { Name = "dt", Type = "DateTime", DateTimeValue = dt });
        var ldt = Assert.IsType<LocalDateTime>(v);
        Assert.Equal(2024, ldt.Year);
        Assert.Equal(9, ldt.Month);
        Assert.Equal(1, ldt.Day);
        Assert.Equal(13, ldt.Hour);
        Assert.Equal(45, ldt.Minute);
        Assert.Equal(30, ldt.Second);
    }

    [Fact]
    public void DateTime_maps_to_zoneless_LocalDateTime_type()
    {
        // Decision A: DateTime is zoneless. The produced type must be LocalDateTime, never ZonedDateTime.
        var v = MapOne(new CypherParameter
        {
            Name = "dt",
            Type = "DateTime",
            DateTimeValue = new DateTime(2024, 9, 1, 10, 0, 0, DateTimeKind.Utc) // Kind must not promote it
        });
        Assert.IsType<LocalDateTime>(v);
        Assert.IsNotType<ZonedDateTime>(v);
    }

    [Fact]
    public void Bytes_maps_to_byte_array()
    {
        var bytes = new byte[] { 1, 2, 3, 250 };
        var v = MapOne(new CypherParameter { Name = "by", Type = "Bytes", BytesValue = bytes });
        var arr = Assert.IsType<byte[]>(v);
        Assert.Equal(bytes, arr);
    }

    // ---- Null ----

    [Fact]
    public void Null_maps_to_null_in_dict()
    {
        var dict = CypherParameterMapper.BuildParameters(new[]
        {
            new CypherParameter { Name = "n", Type = "Null" }
        });
        Assert.True(dict.ContainsKey("n"));
        Assert.Null(dict["n"]);
    }

    [Fact]
    public void Null_is_not_empty_string()
    {
        var nullVal = MapOne(new CypherParameter { Name = "n", Type = "Null" });
        var emptyStr = MapOne(new CypherParameter { Name = "s", Type = "String", StringValue = "" });
        Assert.Null(nullVal);
        Assert.NotNull(emptyStr);
        Assert.Equal("", emptyStr);
    }

    // ---- ZonedDateTime ----

    [Fact]
    public void ZonedDateTime_from_ZoneId()
    {
        var dt = new DateTime(2024, 9, 1, 10, 0, 0);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt, ZoneId = "Europe/Brussels"
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal(2024, zdt.Year);
        Assert.Equal(10, zdt.Hour);
        Assert.Equal("Europe/Brussels", ((ZoneId)zdt.Zone).Id);
    }

    [Fact]
    public void ZonedDateTime_from_OffsetMinutes()
    {
        var dt = new DateTime(2024, 9, 1, 10, 0, 0);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt, OffsetMinutes = 120
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal(120 * 60, zdt.OffsetSeconds);
    }

    [Fact]
    public void ZonedDateTime_prefers_ZoneId_when_both_supplied()
    {
        var dt = new DateTime(2024, 9, 1, 10, 0, 0);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt,
            ZoneId = "Europe/Brussels", OffsetMinutes = 999
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal("Europe/Brussels", ((ZoneId)zdt.Zone).Id);
    }

    [Fact]
    public void ZonedDateTime_with_neither_zone_nor_offset_throws()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = new DateTime(2024, 9, 1)
        }));
        Assert.Contains("z", ex.Message);
        Assert.Contains("ZoneId", ex.Message);
    }

    [Fact]
    public void ZonedDateTime_with_invalid_ZoneId_throws()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime",
            DateTimeValue = new DateTime(2024, 9, 1), ZoneId = "Not/AZone"
        }));
        Assert.Contains("Not/AZone", ex.Message);
    }

    // Regression (reviewer finding): the produced wall-clock must not depend on DateTime.Kind.
    // Kind=Utc must NOT shift 10:00 -> 12:00, and Kind=Local must NOT throw. All Kinds -> 10:00.
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void ZonedDateTime_wall_clock_is_independent_of_DateTimeKind_via_ZoneId(DateTimeKind kind)
    {
        var dt = DateTime.SpecifyKind(new DateTime(2024, 9, 1, 10, 0, 0), kind);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt, ZoneId = "Europe/Brussels"
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal(10, zdt.Hour); // literal wall-clock, never Kind-shifted
        Assert.Equal("Europe/Brussels", ((ZoneId)zdt.Zone).Id);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void ZonedDateTime_wall_clock_is_independent_of_DateTimeKind_via_OffsetMinutes(DateTimeKind kind)
    {
        var dt = DateTime.SpecifyKind(new DateTime(2024, 9, 1, 10, 0, 0), kind);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt, OffsetMinutes = 120
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal(10, zdt.Hour);
        Assert.Equal(120 * 60, zdt.OffsetSeconds);
    }

    // ---- sub-second precision ----

    [Fact]
    public void Subsecond_precision_preserved_through_LocalTime()
    {
        // 123456700 ns == 1_234_567 ticks-of-100ns.
        var dt = new DateTime(2024, 1, 1, 0, 0, 0).AddTicks(1_234_567);
        var v = MapOne(new CypherParameter { Name = "t", Type = "Time", DateTimeValue = dt });
        var lt = Assert.IsType<LocalTime>(v);
        Assert.Equal(123_456_700, lt.Nanosecond);
    }

    [Fact]
    public void Subsecond_precision_preserved_through_LocalDateTime()
    {
        var dt = new DateTime(2024, 9, 1, 13, 45, 30).AddTicks(9_876_543);
        var v = MapOne(new CypherParameter { Name = "dt", Type = "DateTime", DateTimeValue = dt });
        var ldt = Assert.IsType<LocalDateTime>(v);
        Assert.Equal(987_654_300, ldt.Nanosecond);
    }

    [Fact]
    public void Subsecond_precision_preserved_through_ZonedDateTime()
    {
        var dt = new DateTime(2024, 9, 1, 13, 45, 30).AddTicks(1_234_567);
        var v = MapOne(new CypherParameter
        {
            Name = "z", Type = "ZonedDateTime", DateTimeValue = dt, OffsetMinutes = 60
        });
        var zdt = Assert.IsType<ZonedDateTime>(v);
        Assert.Equal(123_456_700, zdt.Nanosecond);
    }

    // ---- case-insensitive tags ----

    [Theory]
    [InlineData("string")]
    [InlineData("STRING")]
    [InlineData("StRiNg")]
    public void Tag_matching_is_case_insensitive(string tag)
    {
        var v = MapOne(new CypherParameter { Name = "s", Type = tag, StringValue = "x" });
        Assert.Equal("x", v);
    }

    // ---- deferred / composite / garbage tags all fail loud ----

    [Theory]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Json")]
    [InlineData("Duration")]
    [InlineData("Point")]
    [InlineData("OffsetTime")]
    [InlineData("Wibble")]
    [InlineData("")]
    public void Unsupported_tags_throw_named_exception_with_name_and_type(string tag)
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "p", Type = tag
        }));
        Assert.Contains("p", ex.Message);
        Assert.Contains(tag, ex.Message);
    }

    // ---- name validation ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_or_whitespace_name_throws(string? name)
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = name!, Type = "String", StringValue = "x"
        }));
    }

    [Theory]
    [InlineData("$id")]
    [InlineData("id$")]
    [InlineData("na$me")]
    public void Name_containing_dollar_throws(string name)
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = name, Type = "String", StringValue = "x"
        }));
    }

    [Theory]
    [InlineData("my name")]
    [InlineData("my\tname")]
    [InlineData("name\n")]
    public void Name_containing_whitespace_throws(string name)
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = name, Type = "String", StringValue = "x"
        }));
    }

    [Fact]
    public void Duplicate_name_throws()
    {
        var ex = Assert.Throws<CypherParameterException>(() => CypherParameterMapper.BuildParameters(new[]
        {
            new CypherParameter { Name = "dup", Type = "String", StringValue = "a" },
            new CypherParameter { Name = "dup", Type = "Integer", IntegerValue = 1 }
        }));
        Assert.Contains("dup", ex.Message);
    }

    // ---- missing required carriers fail loud ----

    [Fact]
    public void String_with_null_carrier_throws()
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "s", Type = "String", StringValue = null
        }));
    }

    [Fact]
    public void Integer_with_null_carrier_throws()
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "i", Type = "Integer", IntegerValue = null
        }));
    }

    [Fact]
    public void Date_with_null_datetime_throws()
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "d", Type = "Date", DateTimeValue = null
        }));
    }

    [Fact]
    public void Bytes_with_null_carrier_throws()
    {
        Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "b", Type = "Bytes", BytesValue = null
        }));
    }

    // ---- aggregate / argument behaviour ----

    [Fact]
    public void BuildParameters_maps_multiple_entries_preserving_names()
    {
        var dict = CypherParameterMapper.BuildParameters(new[]
        {
            new CypherParameter { Name = "s", Type = "String", StringValue = "x" },
            new CypherParameter { Name = "i", Type = "Integer", IntegerValue = 7 },
            new CypherParameter { Name = "n", Type = "Null" }
        });
        Assert.Equal(3, dict.Count);
        Assert.Equal("x", dict["s"]);
        Assert.Equal(7L, dict["i"]);
        Assert.Null(dict["n"]);
    }

    [Fact]
    public void Names_are_case_sensitive_distinct_keys()
    {
        var dict = CypherParameterMapper.BuildParameters(new[]
        {
            new CypherParameter { Name = "id", Type = "Integer", IntegerValue = 1 },
            new CypherParameter { Name = "ID", Type = "Integer", IntegerValue = 2 }
        });
        Assert.Equal(2, dict.Count);
        Assert.Equal(1L, dict["id"]);
        Assert.Equal(2L, dict["ID"]);
    }

    [Fact]
    public void Null_parameters_argument_throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => CypherParameterMapper.BuildParameters(null!));
    }

    [Fact]
    public void Empty_input_yields_empty_dictionary()
    {
        var dict = CypherParameterMapper.BuildParameters(Enumerable.Empty<CypherParameter>());
        Assert.Empty(dict);
    }
}
