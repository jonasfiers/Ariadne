using System;
using System.Collections.Generic;
using Ariadne.Core.Parameters;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Parameters;

/// <summary>
/// Unit tests for Feature 02 — flat, one-level <c>List</c> and <c>Map</c> composite parameters.
/// Pure logic — no server, no driver/session, no mocking libraries. Each composite is asserted for
/// driver container type, element type/value, and every fail-loud rule (nesting, bad keys, missing or
/// null carriers). The scalar element path is the SAME one Feature 01's scalars use, so the temporal
/// <c>Kind</c>-normalization is re-proven here to apply to elements too.
/// </summary>
public class CypherCompositeParameterTests
{
    private static object? MapOne(CypherParameter p)
        => CypherParameterMapper.BuildParameters(new[] { p })[p.Name];

    // ---- List happy paths: container, element types & values ----

    [Fact]
    public void List_maps_to_IList_of_object()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "ids",
            Type = "List",
            ListElements = new List<CypherListElement>
            {
                new() { Type = "Integer", IntegerValue = 1 },
                new() { Type = "Integer", IntegerValue = 2 },
                new() { Type = "Integer", IntegerValue = 3 }
            }
        });
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Equal(3, list.Count);
        Assert.Equal(new object?[] { 1L, 2L, 3L }, list);
    }

    [Fact]
    public void List_of_each_scalar_type_maps_to_correct_driver_types()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var v = MapOne(new CypherParameter
        {
            Name = "mixed",
            Type = "List",
            ListElements = new List<CypherListElement>
            {
                new() { Type = "String", StringValue = "hi" },
                new() { Type = "Integer", IntegerValue = 42 },
                new() { Type = "Float", FloatValue = 3.5m },
                new() { Type = "Boolean", BooleanValue = true },
                new() { Type = "Bytes", BytesValue = bytes },
                new() { Type = "Date", DateTimeValue = new DateTime(2024, 9, 1, 13, 45, 30) },
                new() { Type = "Time", DateTimeValue = new DateTime(2024, 9, 1, 13, 45, 30) },
                new() { Type = "DateTime", DateTimeValue = new DateTime(2024, 9, 1, 13, 45, 30) },
                new() { Type = "Null" }
            }
        });
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Equal(9, list.Count);
        Assert.Equal("hi", Assert.IsType<string>(list[0]));
        Assert.Equal(42L, Assert.IsType<long>(list[1]));
        Assert.Equal(3.5d, Assert.IsType<double>(list[2]));
        Assert.True(Assert.IsType<bool>(list[3]));
        Assert.Equal(bytes, Assert.IsType<byte[]>(list[4]));
        Assert.IsType<LocalDate>(list[5]);
        Assert.IsType<LocalTime>(list[6]);
        Assert.IsType<LocalDateTime>(list[7]);
        Assert.Null(list[8]);
    }

    [Fact]
    public void Empty_list_maps_to_empty_IList()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "ids", Type = "List", ListElements = new List<CypherListElement>()
        });
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Empty(list);
    }

    // ---- Map happy paths ----

    [Fact]
    public void Map_of_mixed_scalars_maps_to_IDictionary_with_correct_keys()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry>
            {
                new() { Key = "name", Type = "String", StringValue = "Ada" },
                new() { Key = "age", Type = "Integer", IntegerValue = 36 },
                new() { Key = "active", Type = "Boolean", BooleanValue = true },
                new() { Key = "score", Type = "Float", FloatValue = 9.5m },
                new() { Key = "note", Type = "Null" }
            }
        });
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(v);
        Assert.Equal(5, map.Count);
        Assert.Equal("Ada", map["name"]);
        Assert.Equal(36L, map["age"]);
        Assert.Equal(true, map["active"]);
        Assert.Equal(9.5d, map["score"]);
        Assert.Null(map["note"]);
    }

    [Fact]
    public void Empty_map_maps_to_empty_IDictionary()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "props", Type = "Map", MapEntries = new List<CypherMapEntry>()
        });
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(v);
        Assert.Empty(map);
    }

    [Fact]
    public void Map_keys_are_case_sensitive_distinct()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry>
            {
                new() { Key = "A", Type = "Integer", IntegerValue = 1 },
                new() { Key = "a", Type = "Integer", IntegerValue = 2 }
            }
        });
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(v);
        Assert.Equal(2, map.Count);
        Assert.Equal(1L, map["A"]);
        Assert.Equal(2L, map["a"]);
    }

    // ---- temporal Kind normalization applies to elements too (the load-bearing check) ----

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void List_ZonedDateTime_element_wall_clock_is_independent_of_Kind(DateTimeKind kind)
    {
        var dt = DateTime.SpecifyKind(new DateTime(2024, 9, 1, 10, 0, 0), kind);
        var v = MapOne(new CypherParameter
        {
            Name = "times",
            Type = "List",
            ListElements = new List<CypherListElement>
            {
                new() { Type = "ZonedDateTime", DateTimeValue = dt, ZoneId = "Europe/Brussels" }
            }
        });
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        var zdt = Assert.IsType<ZonedDateTime>(list[0]);
        Assert.Equal(10, zdt.Hour); // Kind=Utc must NOT shift 10:00 -> 12:00
        Assert.Equal("Europe/Brussels", ((ZoneId)zdt.Zone).Id);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void Map_ZonedDateTime_entry_wall_clock_is_independent_of_Kind(DateTimeKind kind)
    {
        var dt = DateTime.SpecifyKind(new DateTime(2024, 9, 1, 10, 0, 0), kind);
        var v = MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry>
            {
                new() { Key = "when", Type = "ZonedDateTime", DateTimeValue = dt, OffsetMinutes = 120 }
            }
        });
        var map = Assert.IsAssignableFrom<IDictionary<string, object?>>(v);
        var zdt = Assert.IsType<ZonedDateTime>(map["when"]);
        Assert.Equal(10, zdt.Hour);
        Assert.Equal(120 * 60, zdt.OffsetSeconds);
    }

    // ---- nesting / composite / deferred elements fail loud, pointing to Json ----

    [Theory]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Json")]
    public void List_element_with_composite_type_throws_pointing_to_Json(string tag)
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "outer",
            Type = "List",
            ListElements = new List<CypherListElement> { new() { Type = tag } }
        }));
        Assert.Contains("outer", ex.Message);
        Assert.Contains("Json", ex.Message);
    }

    [Theory]
    [InlineData("Duration")]
    [InlineData("Point")]
    [InlineData("OffsetTime")]
    [InlineData("Wibble")]
    public void List_element_with_deferred_or_unknown_type_throws(string tag)
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "outer",
            Type = "List",
            ListElements = new List<CypherListElement> { new() { Type = tag } }
        }));
        Assert.Contains("outer", ex.Message);
        Assert.Contains(tag, ex.Message);
    }

    [Theory]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Json")]
    public void Map_entry_with_composite_type_throws_pointing_to_Json_and_naming_Key(string tag)
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "outer",
            Type = "Map",
            MapEntries = new List<CypherMapEntry> { new() { Key = "child", Type = tag } }
        }));
        Assert.Contains("outer", ex.Message);
        Assert.Contains("child", ex.Message); // the offending Key is named
        Assert.Contains("Json", ex.Message);
    }

    // ---- map key validation ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Map_entry_with_empty_or_whitespace_key_throws(string? key)
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry> { new() { Key = key!, Type = "String", StringValue = "x" } }
        }));
        Assert.Contains("props", ex.Message);
    }

    [Fact]
    public void Map_with_duplicate_key_throws_naming_the_key()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry>
            {
                new() { Key = "dup", Type = "Integer", IntegerValue = 1 },
                new() { Key = "dup", Type = "Integer", IntegerValue = 2 }
            }
        }));
        Assert.Contains("props", ex.Message);
        Assert.Contains("dup", ex.Message);
    }

    // ---- missing / null carriers fail loud ----

    [Fact]
    public void List_tag_with_null_carrier_throws_missing_carrier()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "ids", Type = "List", ListElements = null
        }));
        Assert.Contains("ids", ex.Message);
        Assert.Contains("List", ex.Message);
    }

    [Fact]
    public void Map_tag_with_null_carrier_throws_missing_carrier()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "props", Type = "Map", MapEntries = null
        }));
        Assert.Contains("props", ex.Message);
        Assert.Contains("Map", ex.Message);
    }

    [Fact]
    public void Null_list_element_throws_not_silently_dropped()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "ids",
            Type = "List",
            ListElements = new List<CypherListElement> { new() { Type = "Integer", IntegerValue = 1 }, null! }
        }));
        Assert.Contains("ids", ex.Message);
    }

    [Fact]
    public void Null_map_entry_throws_not_silently_dropped()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "props",
            Type = "Map",
            MapEntries = new List<CypherMapEntry> { new() { Key = "a", Type = "Integer", IntegerValue = 1 }, null! }
        }));
        Assert.Contains("props", ex.Message);
    }

    [Fact]
    public void List_element_missing_its_value_carrier_throws()
    {
        var ex = Assert.Throws<CypherParameterException>(() => MapOne(new CypherParameter
        {
            Name = "ids",
            Type = "List",
            ListElements = new List<CypherListElement> { new() { Type = "Integer", IntegerValue = null } }
        }));
        Assert.Contains("ids", ex.Message);
    }

    // ---- element tag case-insensitivity (shared scalar path) ----

    [Fact]
    public void List_element_tag_matching_is_case_insensitive()
    {
        var v = MapOne(new CypherParameter
        {
            Name = "ids",
            Type = "List",
            ListElements = new List<CypherListElement> { new() { Type = "iNtEgEr", IntegerValue = 5 } }
        });
        var list = Assert.IsAssignableFrom<IList<object?>>(v);
        Assert.Equal(5L, list[0]);
    }

    [Fact]
    public void List_and_Map_tags_are_case_insensitive()
    {
        var l = MapOne(new CypherParameter
        {
            Name = "ids", Type = "lIsT", ListElements = new List<CypherListElement>()
        });
        Assert.IsAssignableFrom<IList<object?>>(l);

        var m = MapOne(new CypherParameter
        {
            Name = "props", Type = "mAp", MapEntries = new List<CypherMapEntry>()
        });
        Assert.IsAssignableFrom<IDictionary<string, object?>>(m);
    }
}
