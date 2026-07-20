using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Ariadne.Core.Results;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Results;

/// <summary>
/// Unit tests for the Feature 05 composite + graph serialization added to <see cref="CypherValueSerializer"/>:
/// <see cref="System.Collections.IList"/> → JSON array, <see cref="System.Collections.IDictionary"/> → JSON
/// object (arbitrary keys verbatim, §6), and the <see cref="INode"/>/<see cref="IRelationship"/>/<see cref="IPath"/>
/// envelopes (§4). Pure logic — the driver graph interfaces are satisfied by small hand-rolled fakes (no server,
/// no session, no mocking libraries). Nesting, traversal order, `elementId`-only identity, and — the BACKLOG N1
/// concern — that a mid-tree throw propagates and does <b>not</b> emit partial/valid JSON are all asserted.
/// </summary>
public class CypherCompositeSerializerTests
{
    // ============================ lists ============================

    [Fact]
    public void Empty_list_serializes_to_empty_array()
        => Assert.Equal("[]", CypherValueSerializer.Serialize(new List<object?>()));

    [Fact]
    public void List_of_mixed_scalars_serializes_in_order_with_leaf_types()
    {
        var list = new List<object?> { 1L, "two", true, null, 3.5, new LocalDate(2024, 9, 1) };
        Assert.Equal("[1,\"two\",true,null,3.5,\"2024-09-01\"]", CypherValueSerializer.Serialize(list));
    }

    [Fact]
    public void Nested_list_of_lists_recurses()
    {
        var list = new List<object?> { new List<object?> { 1L, 2L }, new List<object?> { new List<object?> { 3L } } };
        Assert.Equal("[[1,2],[[3]]]", CypherValueSerializer.Serialize(list));
    }

    [Fact]
    public void Clr_array_is_also_serialized_as_a_json_array()
        // A driver list can arrive as a CLR array; System.Array implements the non-generic IList.
        => Assert.Equal("[1,2,3]", CypherValueSerializer.Serialize(new object[] { 1L, 2L, 3L }));

    // ============================ maps ============================

    [Fact]
    public void Empty_map_serializes_to_empty_object()
        => Assert.Equal("{}", CypherValueSerializer.Serialize(new Dictionary<string, object?>()));

    [Fact]
    public void Map_of_scalars_serializes_keys_and_recursed_values()
    {
        var map = new Dictionary<string, object?> { ["a"] = 1L, ["b"] = "x", ["c"] = null };
        Assert.Equal("{\"a\":1,\"b\":\"x\",\"c\":null}", CypherValueSerializer.Serialize(map));
    }

    [Fact]
    public void Arbitrary_map_keys_are_preserved_verbatim_no_pattern_b()
    {
        // §6: keys with spaces/dots/$ stay exactly as-is — never a name/value list.
        var map = new Dictionary<string, object?>
        {
            ["with space"] = 1L,
            ["dotted.key"] = 2L,
            ["$dollar"] = 3L,
        };
        var root = JsonDocument.Parse(CypherValueSerializer.Serialize(map)).RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(1L, root.GetProperty("with space").GetInt64());
        Assert.Equal(2L, root.GetProperty("dotted.key").GetInt64());
        Assert.Equal(3L, root.GetProperty("$dollar").GetInt64());
    }

    [Fact]
    public void Map_whose_values_are_lists_and_nodes_recurses()
    {
        var map = new Dictionary<string, object?>
        {
            ["nums"] = new List<object?> { 1L, 2L },
            ["node"] = new FakeNode { ElementId = "4:x:1", Labels = new[] { "L" } },
        };
        Assert.Equal(
            "{\"nums\":[1,2],\"node\":{\"elementId\":\"4:x:1\",\"labels\":[\"L\"],\"properties\":{}}}",
            CypherValueSerializer.Serialize(map));
    }

    [Fact]
    public void Non_string_map_key_fails_loud()
    {
        // A JSON object key must be a string; a non-string key (never produced by Neo4j) is not coerced.
        var map = new System.Collections.Hashtable { [42] = "v" };
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(map));
        Assert.Contains("non-string key", ex.Message);
    }

    // ============================ node envelope ============================

    [Fact]
    public void Node_with_labels_and_mixed_props_emits_exact_envelope()
    {
        var node = new FakeNode
        {
            ElementId = "4:abc:7",
            Labels = new[] { "Person", "Employee" },
            Properties = new Dictionary<string, object>
            {
                ["name"] = "Jonas",
                ["age"] = 34L,
                ["active"] = true,
            },
        };
        Assert.Equal(
            "{\"elementId\":\"4:abc:7\",\"labels\":[\"Person\",\"Employee\"]," +
            "\"properties\":{\"name\":\"Jonas\",\"age\":34,\"active\":true}}",
            CypherValueSerializer.Serialize(node));
    }

    [Fact]
    public void Node_with_empty_labels_emits_empty_array()
    {
        var node = new FakeNode { ElementId = "4:x:1", Labels = Array.Empty<string>() };
        Assert.Equal("{\"elementId\":\"4:x:1\",\"labels\":[],\"properties\":{}}",
            CypherValueSerializer.Serialize(node));
    }

    [Fact]
    public void Node_with_empty_properties_emits_empty_object()
    {
        var node = new FakeNode { ElementId = "4:x:1", Labels = new[] { "L" } };
        Assert.Equal("{\"elementId\":\"4:x:1\",\"labels\":[\"L\"],\"properties\":{}}",
            CypherValueSerializer.Serialize(node));
    }

    [Fact]
    public void Node_does_not_emit_deprecated_numeric_id()
    {
        var node = new FakeNode { ElementId = "4:x:1", Labels = new[] { "L" } };
        var root = JsonDocument.Parse(CypherValueSerializer.Serialize(node)).RootElement;
        Assert.False(root.TryGetProperty("id", out _), "elementId only — the deprecated numeric id must not be emitted.");
        Assert.Equal("4:x:1", root.GetProperty("elementId").GetString());
    }

    [Fact]
    public void Node_property_that_is_a_list_nests_correctly()
    {
        var node = new FakeNode
        {
            ElementId = "4:x:1",
            Labels = new[] { "Tagged" },
            Properties = new Dictionary<string, object> { ["tags"] = new List<object?> { "a", "b" } },
        };
        Assert.Equal(
            "{\"elementId\":\"4:x:1\",\"labels\":[\"Tagged\"],\"properties\":{\"tags\":[\"a\",\"b\"]}}",
            CypherValueSerializer.Serialize(node));
    }

    // ============================ relationship envelope ============================

    [Fact]
    public void Relationship_emits_exact_envelope_with_endpoint_element_ids()
    {
        var rel = new FakeRelationship
        {
            ElementId = "5:abc:11",
            Type = "WORKS_AT",
            StartNodeElementId = "4:abc:7",
            EndNodeElementId = "4:abc:9",
            Properties = new Dictionary<string, object> { ["since"] = new LocalDate(2024, 9, 1) },
        };
        Assert.Equal(
            "{\"elementId\":\"5:abc:11\",\"type\":\"WORKS_AT\"," +
            "\"startNodeElementId\":\"4:abc:7\",\"endNodeElementId\":\"4:abc:9\"," +
            "\"properties\":{\"since\":\"2024-09-01\"}}",
            CypherValueSerializer.Serialize(rel));
    }

    [Fact]
    public void Relationship_does_not_emit_deprecated_numeric_ids()
    {
        var rel = new FakeRelationship
        {
            ElementId = "5:x:1",
            Type = "R",
            StartNodeElementId = "4:x:1",
            EndNodeElementId = "4:x:2",
        };
        var root = JsonDocument.Parse(CypherValueSerializer.Serialize(rel)).RootElement;
        Assert.False(root.TryGetProperty("id", out _));
        Assert.False(root.TryGetProperty("startNodeId", out _));
        Assert.False(root.TryGetProperty("endNodeId", out _));
    }

    // ============================ path envelope ============================

    [Fact]
    public void Path_of_n_nodes_and_n_minus_one_rels_preserves_traversal_order()
    {
        var n1 = new FakeNode { ElementId = "4:x:1", Labels = new[] { "A" } };
        var n2 = new FakeNode { ElementId = "4:x:2", Labels = new[] { "B" } };
        var n3 = new FakeNode { ElementId = "4:x:3", Labels = new[] { "C" } };
        var r1 = new FakeRelationship { ElementId = "5:x:1", Type = "T1", StartNodeElementId = "4:x:1", EndNodeElementId = "4:x:2" };
        var r2 = new FakeRelationship { ElementId = "5:x:2", Type = "T2", StartNodeElementId = "4:x:2", EndNodeElementId = "4:x:3" };
        var path = new FakePath
        {
            Start = n1,
            End = n3,
            Nodes = new[] { n1, n2, n3 },
            Relationships = new[] { r1, r2 },
        };

        var root = JsonDocument.Parse(CypherValueSerializer.Serialize(path)).RootElement;
        var nodes = root.GetProperty("nodes");
        var rels = root.GetProperty("relationships");
        Assert.Equal(3, nodes.GetArrayLength());
        Assert.Equal(2, rels.GetArrayLength());
        // traversal order preserved
        Assert.Equal("4:x:1", nodes[0].GetProperty("elementId").GetString());
        Assert.Equal("4:x:2", nodes[1].GetProperty("elementId").GetString());
        Assert.Equal("4:x:3", nodes[2].GetProperty("elementId").GetString());
        Assert.Equal("T1", rels[0].GetProperty("type").GetString());
        Assert.Equal("T2", rels[1].GetProperty("type").GetString());
    }

    [Fact]
    public void Empty_path_emits_empty_node_and_rel_arrays()
    {
        var path = new FakePath();
        Assert.Equal("{\"nodes\":[],\"relationships\":[]}", CypherValueSerializer.Serialize(path));
    }

    // ============================ deep nesting ============================

    [Fact]
    public void List_containing_nodes_nests()
    {
        var list = new List<object?>
        {
            new FakeNode { ElementId = "4:x:1", Labels = new[] { "A" } },
            new FakeNode { ElementId = "4:x:2", Labels = Array.Empty<string>() },
        };
        Assert.Equal(
            "[{\"elementId\":\"4:x:1\",\"labels\":[\"A\"],\"properties\":{}}," +
            "{\"elementId\":\"4:x:2\",\"labels\":[],\"properties\":{}}]",
            CypherValueSerializer.Serialize(list));
    }

    [Fact]
    public void Deeply_mixed_tree_map_of_list_of_node_with_map_property_recurses()
    {
        var inner = new FakeNode
        {
            ElementId = "4:x:9",
            Labels = new[] { "Leaf" },
            Properties = new Dictionary<string, object> { ["meta"] = new Dictionary<string, object?> { ["k"] = 1L } },
        };
        var tree = new Dictionary<string, object?> { ["items"] = new List<object?> { inner } };
        Assert.Equal(
            "{\"items\":[{\"elementId\":\"4:x:9\",\"labels\":[\"Leaf\"]," +
            "\"properties\":{\"meta\":{\"k\":1}}}]}",
            CypherValueSerializer.Serialize(tree));
    }

    // ============================ fail loud: mid-tree throws propagate (BACKLOG N1) ============================

    [Fact]
    public void Unsupported_value_nested_in_list_propagates_via_serialize()
    {
        var list = new List<object?> { 1L, new Duration(1, 2, 3, 4) };
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(list));
        Assert.Contains("Duration", ex.Message);
    }

    [Fact]
    public void Unsupported_value_nested_in_map_propagates_via_serialize()
    {
        var map = new Dictionary<string, object?> { ["ok"] = 1L, ["bad"] = new Point(7203, 1.0, 2.0) };
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(map));
        Assert.Contains("Point", ex.Message);
    }

    [Fact]
    public void Unsupported_value_nested_in_node_property_propagates_via_serialize()
    {
        var node = new FakeNode
        {
            ElementId = "4:x:1",
            Labels = new[] { "L" },
            Properties = new Dictionary<string, object> { ["t"] = new OffsetTime(10, 30, 0, 2 * 3600) },
        };
        var ex = Assert.Throws<CypherResultException>(() => CypherValueSerializer.Serialize(node));
        Assert.Contains("OffsetTime", ex.Message);
    }

    [Fact]
    public void Mid_tree_throw_leaves_partial_not_valid_json_and_is_never_closed()
    {
        // The N1 proof: drive Write directly on a shared writer, let the nested Duration throw, and show the
        // flushed buffer is an UNCLOSED, invalid-JSON fragment — the serializer never caught the throw nor
        // tried to "close" the array. Feature 06's record layer is what abandons this writer.
        using var stream = new MemoryStream();
        var list = new List<object?> { 1L, "ok", new Duration(1, 2, 3, 4), 99L };

        CypherResultException? thrown = null;
        using (var writer = new Utf8JsonWriter(stream, CypherValueSerializer.CanonicalWriterOptions))
        {
            try
            {
                CypherValueSerializer.Write(writer, list);
                writer.Flush();
            }
            catch (CypherResultException ex)
            {
                thrown = ex;
                writer.Flush(); // flush whatever partial tokens were already written; do NOT close the array
            }
        }

        Assert.NotNull(thrown);
        Assert.Contains("Duration", thrown!.Message);

        var partial = Encoding.UTF8.GetString(stream.ToArray());
        // The array opened and the two valid leading elements were written; the throw stopped emission there.
        Assert.Equal("[1,\"ok\"", partial);
        // It is NOT valid JSON — proving nothing closed the structure into a (misleadingly) parseable document.
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(partial));
    }

    // ============================ hand-rolled driver fakes (no Moq) ============================

    private sealed class FakeNode : INode
    {
        public string ElementId { get; set; } = "";
        public IReadOnlyList<string> Labels { get; set; } = Array.Empty<string>();
        public IReadOnlyDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public long Id => throw new NotSupportedException("Deprecated numeric id is not used by the serializer.");
        public object this[string key] => Properties[key];
        public T Get<T>(string key) => (T)Properties[key];
        public bool TryGet<T>(string key, out T value)
        {
            if (Properties.TryGetValue(key, out var v)) { value = (T)v!; return true; }
            value = default!;
            return false;
        }
        public bool Equals(INode? other) => ReferenceEquals(this, other);
    }

    private sealed class FakeRelationship : IRelationship
    {
        public string ElementId { get; set; } = "";
        public string Type { get; set; } = "";
        public string StartNodeElementId { get; set; } = "";
        public string EndNodeElementId { get; set; } = "";
        public IReadOnlyDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public long Id => throw new NotSupportedException("Deprecated numeric id is not used by the serializer.");
        public long StartNodeId => throw new NotSupportedException("Deprecated numeric id is not used by the serializer.");
        public long EndNodeId => throw new NotSupportedException("Deprecated numeric id is not used by the serializer.");
        public object this[string key] => Properties[key];
        public T Get<T>(string key) => (T)Properties[key];
        public bool TryGet<T>(string key, out T value)
        {
            if (Properties.TryGetValue(key, out var v)) { value = (T)v!; return true; }
            value = default!;
            return false;
        }
        public bool Equals(IRelationship? other) => ReferenceEquals(this, other);
    }

    private sealed class FakePath : IPath
    {
        public INode Start { get; set; } = null!;
        public INode End { get; set; } = null!;
        public IReadOnlyList<INode> Nodes { get; set; } = Array.Empty<INode>();
        public IReadOnlyList<IRelationship> Relationships { get; set; } = Array.Empty<IRelationship>();
        public bool Equals(IPath? other) => ReferenceEquals(this, other);
    }
}
