using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Ariadne.Core.Results;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Results;

/// <summary>
/// Unit tests for the Feature 06 record-envelope builder (<see cref="RecordsJsonBuilder"/>): turning a sequence
/// of driver <see cref="IRecord"/>s into the canonical <c>RecordsJson</c> array (result spec §2), routing every
/// value through <see cref="CypherValueSerializer"/>, exposing the columns, and — the BACKLOG N1 responsibility
/// this feature owns — abandoning the whole result (no partial JSON) when a value fails loud mid-result. Pure
/// logic: <see cref="IRecord"/> and <see cref="INode"/> are satisfied by small hand-rolled fakes (no server, no
/// session, no mocking libraries).
/// </summary>
public class RecordsJsonBuilderTests
{
    // ============================ shape: array of per-record objects ============================

    [Fact]
    public void Empty_sequence_yields_empty_array_and_no_columns()
    {
        var result = RecordsJsonBuilder.Build(Array.Empty<IRecord>());
        Assert.Equal("[]", result.Json);
        Assert.Empty(result.Columns);
    }

    [Fact]
    public void Record_with_zero_columns_yields_empty_object()
    {
        var records = new[] { new FakeRecord() };
        Assert.Equal("[{}]", RecordsJsonBuilder.BuildRecordsJson(records));
    }

    [Fact]
    public void Single_record_columns_in_keys_order_with_leaf_values()
    {
        var record = new FakeRecord()
            .With("name", "Jonas")
            .With("age", 34L)
            .With("active", true);
        Assert.Equal(
            "[{\"name\":\"Jonas\",\"age\":34,\"active\":true}]",
            RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
    }

    [Fact]
    public void Column_order_follows_keys_not_alphabetical()
    {
        var record = new FakeRecord()
            .With("z", 1L)
            .With("a", 2L)
            .With("m", 3L);
        Assert.Equal("[{\"z\":1,\"a\":2,\"m\":3}]", RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
    }

    [Fact]
    public void Multiple_records_preserve_sequence_order()
    {
        var records = new[]
        {
            new FakeRecord().With("n", 1L),
            new FakeRecord().With("n", 2L),
            new FakeRecord().With("n", 3L),
        };
        Assert.Equal("[{\"n\":1},{\"n\":2},{\"n\":3}]", RecordsJsonBuilder.BuildRecordsJson(records));
    }

    [Fact]
    public void Single_record_with_mixed_columns_scalar_node_list_emits_exact_object()
    {
        var record = new FakeRecord()
            .With("id", 7L)
            .With("p", new FakeNode
            {
                ElementId = "4:abc:7",
                Labels = new[] { "Person" },
                Properties = new Dictionary<string, object> { ["name"] = "Jonas" },
            })
            .With("tags", new List<object?> { "a", "b" });
        Assert.Equal(
            "[{\"id\":7," +
            "\"p\":{\"elementId\":\"4:abc:7\",\"labels\":[\"Person\"],\"properties\":{\"name\":\"Jonas\"}}," +
            "\"tags\":[\"a\",\"b\"]}]",
            RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
    }

    // ============================ columns exposure ============================

    [Fact]
    public void Columns_come_from_first_record_keys_in_order()
    {
        var records = new[]
        {
            new FakeRecord().With("p", 1L).With("since", 2L),
            new FakeRecord().With("p", 3L).With("since", 4L),
        };
        var result = RecordsJsonBuilder.Build(records);
        Assert.Equal(new[] { "p", "since" }, result.Columns);
    }

    [Fact]
    public void Build_json_and_columns_produced_in_one_pass_over_a_lazy_sequence()
    {
        // A single-enumeration guard: Build must not walk the sequence twice (columns + json in one pass).
        int enumerations = 0;
        IEnumerable<IRecord> Lazy()
        {
            enumerations++;
            yield return new FakeRecord().With("n", 1L);
            yield return new FakeRecord().With("n", 2L);
        }

        var result = RecordsJsonBuilder.Build(Lazy());
        Assert.Equal("[{\"n\":1},{\"n\":2}]", result.Json);
        Assert.Equal(new[] { "n" }, result.Columns);
        Assert.Equal(1, enumerations);
    }

    // ============================ value routing through CypherValueSerializer ============================

    [Fact]
    public void Zoned_date_time_value_routes_through_serializer_as_value_zone_object()
    {
        var z = new ZonedDateTime(new DateTime(2024, 9, 1, 10, 30, 0, DateTimeKind.Unspecified), "Europe/Brussels");
        var record = new FakeRecord().With("at", z);

        var root = JsonDocument.Parse(RecordsJsonBuilder.BuildRecordsJson(new[] { record })).RootElement;
        var at = root[0].GetProperty("at");
        Assert.Equal("2024-09-01T10:30:00", at.GetProperty("value").GetString());
        Assert.Equal("Europe/Brussels", at.GetProperty("zone").GetString());
    }

    [Fact]
    public void Node_value_routes_through_serializer_as_canonical_envelope()
    {
        var record = new FakeRecord().With("p", new FakeNode
        {
            ElementId = "4:x:1",
            Labels = new[] { "Person", "Employee" },
            Properties = new Dictionary<string, object> { ["name"] = "Jonas", ["age"] = 34L },
        });

        var root = JsonDocument.Parse(RecordsJsonBuilder.BuildRecordsJson(new[] { record })).RootElement;
        var p = root[0].GetProperty("p");
        Assert.Equal("4:x:1", p.GetProperty("elementId").GetString());
        Assert.Equal(2, p.GetProperty("labels").GetArrayLength());
        Assert.Equal("Jonas", p.GetProperty("properties").GetProperty("name").GetString());
        Assert.False(p.TryGetProperty("id", out _)); // elementId only
    }

    [Fact]
    public void Null_column_value_emits_explicit_json_null()
    {
        // §5: nulls are emitted explicitly, never omitted, so the record shape stays stable for sampling.
        var record = new FakeRecord().With("missing", null).With("present", 1L);
        Assert.Equal("[{\"missing\":null,\"present\":1}]", RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
    }

    // ============================ fail loud: null record / duplicate key ============================

    [Fact]
    public void Null_records_argument_throws_argument_null()
        => Assert.Throws<ArgumentNullException>(() => RecordsJsonBuilder.Build(null!));

    [Fact]
    public void Null_record_in_sequence_fails_loud()
    {
        var records = new IRecord?[] { new FakeRecord().With("n", 1L), null };
        var ex = Assert.Throws<CypherResultException>(() => RecordsJsonBuilder.BuildRecordsJson(records!));
        Assert.Contains("null record", ex.Message);
    }

    [Fact]
    public void Duplicate_column_name_within_a_record_fails_loud()
    {
        // Bolt never produces this, but the decision is: fail loud rather than emit a duplicate JSON object key.
        var record = new FakeRecord().With("x", 1L).With("x", 2L);
        var ex = Assert.Throws<CypherResultException>(() => RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
        Assert.Contains("duplicate column name", ex.Message);
        Assert.Contains("'x'", ex.Message);
    }

    [Fact]
    public void Duplicate_column_key_is_case_sensitive_distinct_names_are_allowed()
    {
        // 'X' and 'x' are distinct JSON keys (ordinal); this is not a duplicate.
        var record = new FakeRecord().With("X", 1L).With("x", 2L);
        Assert.Equal("[{\"X\":1,\"x\":2}]", RecordsJsonBuilder.BuildRecordsJson(new[] { record }));
    }

    // ============================ BACKLOG N1: abandon the whole result on a mid-result throw ============================

    [Fact]
    public void Unsupported_value_in_a_later_record_throws_and_returns_no_partial_json()
    {
        // The N1 proof for the public string API: a good 1st record, then a 2nd record whose value is an
        // unsupported Duration. BuildRecordsJson must THROW — it returns a string only on full success, so no
        // truncated array and no half-record can ever reach the caller.
        var records = new[]
        {
            new FakeRecord().With("n", 1L),
            new FakeRecord().With("n", new Duration(1, 2, 3, 4)),
            new FakeRecord().With("n", 3L),
        };

        var ex = Assert.Throws<CypherResultException>(() => RecordsJsonBuilder.BuildRecordsJson(records));
        Assert.Contains("Duration", ex.Message);
    }

    [Fact]
    public void Unsupported_value_mid_record_throws_before_any_string_is_produced()
    {
        // Reinforces N1: the throw happens inside Build, so there is no return value at all — nothing partial is
        // observable. We capture the result reference and confirm it was never assigned.
        var records = new[]
        {
            new FakeRecord().With("a", 1L).With("b", new Point(7203, 1.0, 2.0)),
        };

        RecordsJsonResult captured = default;
        bool assigned = false;
        Assert.Throws<CypherResultException>(() =>
        {
            captured = RecordsJsonBuilder.Build(records);
            assigned = true;
        });
        Assert.False(assigned);
        Assert.Null(captured.Json); // default struct — never populated with partial content
    }

    [Fact]
    public void A_failing_build_does_not_corrupt_a_subsequent_successful_build()
    {
        // No leaked/reused buffer state: after a throw, a fresh clean call returns exactly the right JSON.
        var bad = new[] { new FakeRecord().With("n", new Duration(1, 2, 3, 4)) };
        Assert.Throws<CypherResultException>(() => RecordsJsonBuilder.BuildRecordsJson(bad));

        var good = new[] { new FakeRecord().With("n", 1L), new FakeRecord().With("n", 2L) };
        Assert.Equal("[{\"n\":1},{\"n\":2}]", RecordsJsonBuilder.BuildRecordsJson(good));
    }

    [Fact]
    public void WriteRecords_overload_leaves_writer_mid_array_on_throw_caller_abandons_it()
    {
        // The composing overload propagates the throw with the writer left holding a partial, invalid-JSON
        // fragment — by design; the caller (future execution layer) abandons it rather than flushing partial
        // JSON. Mirrors the Feature 05 N1 proof, one level up.
        using var stream = new MemoryStream();
        var records = new[]
        {
            new FakeRecord().With("n", 1L),
            new FakeRecord().With("n", new Duration(1, 2, 3, 4)),
        };

        CypherResultException? thrown = null;
        using (var writer = new Utf8JsonWriter(stream, CypherValueSerializer.CanonicalWriterOptions))
        {
            try
            {
                RecordsJsonBuilder.WriteRecords(writer, records);
                writer.Flush();
            }
            catch (CypherResultException ex)
            {
                thrown = ex;
                writer.Flush(); // flush the partial tokens; do NOT close the structures
            }
        }

        Assert.NotNull(thrown);
        Assert.Contains("Duration", thrown!.Message);

        var partial = Encoding.UTF8.GetString(stream.ToArray());
        // The array + first record were written, the 2nd record's object opened and its property name emitted,
        // then the throw stopped emission. It is NOT closed/valid JSON.
        Assert.Equal("[{\"n\":1},{\"n\":", partial);
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(partial));
    }

    [Fact]
    public void WriteRecords_into_shared_writer_composes_and_returns_columns()
    {
        // The overload is meant to compose into a larger writer; confirm it writes a valid array and returns the
        // columns for the caller to surface separately.
        using var stream = new MemoryStream();
        var records = new[]
        {
            new FakeRecord().With("p", 1L).With("q", "x"),
            new FakeRecord().With("p", 2L).With("q", "y"),
        };

        IReadOnlyList<string> columns;
        using (var writer = new Utf8JsonWriter(stream, CypherValueSerializer.CanonicalWriterOptions))
        {
            columns = RecordsJsonBuilder.WriteRecords(writer, records);
            writer.Flush();
        }

        Assert.Equal(new[] { "p", "q" }, columns);
        Assert.Equal("[{\"p\":1,\"q\":\"x\"},{\"p\":2,\"q\":\"y\"}]", Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ============================ hand-rolled driver fakes (no Moq) ============================

    /// <summary>
    /// A minimal <see cref="IRecord"/> backed by ordered (key,value) pairs — so both column order and a
    /// duplicate key can be represented. <see cref="IRecord"/> extends
    /// <see cref="IReadOnlyDictionary{TKey, TValue}"/>; only the members the builder actually uses
    /// (<see cref="Keys"/> and the integer indexer) carry real behaviour, the rest are present to satisfy the
    /// interface.
    /// </summary>
    private sealed class FakeRecord : IRecord
    {
        private readonly List<string> _keys = new List<string>();
        private readonly List<object?> _values = new List<object?>();

        /// <summary>Appends a column; returns <c>this</c> for fluent construction.</summary>
        public FakeRecord With(string key, object? value)
        {
            _keys.Add(key);
            _values.Add(value);
            return this;
        }

        // --- the two members RecordsJsonBuilder relies on ---
        public IReadOnlyList<string> Keys => _keys;
        public object this[int index] => _values[index]!;

        // --- IRecord.Values (typed dictionary) ---
        public IReadOnlyDictionary<string, object> Values
        {
            get
            {
                var d = new Dictionary<string, object>();
                for (int i = 0; i < _keys.Count; i++) d[_keys[i]] = _values[i]!;
                return d;
            }
        }

        // --- IRecord string-keyed accessors (unused by the builder) ---
        public object this[string key] => _values[_keys.IndexOf(key)]!;
        public T Get<T>(string key) => (T)this[key];
        public bool TryGet<T>(string key, out T value)
        {
            int i = _keys.IndexOf(key);
            if (i >= 0) { value = (T)_values[i]!; return true; }
            value = default!;
            return false;
        }
        public T GetCaseInsensitive<T>(string key) => Get<T>(key);
        public bool TryGetCaseInsensitive<T>(string key, out T value) => TryGet(key, out value);

        // --- IReadOnlyDictionary<string, object> surface (unused by the builder) ---
        IEnumerable<string> IReadOnlyDictionary<string, object>.Keys => _keys;
        IEnumerable<object> IReadOnlyDictionary<string, object>.Values
        {
            get { foreach (var v in _values) yield return v!; }
        }
        int IReadOnlyCollection<KeyValuePair<string, object>>.Count => _keys.Count;
        bool IReadOnlyDictionary<string, object>.ContainsKey(string key) => _keys.Contains(key);
        bool IReadOnlyDictionary<string, object>.TryGetValue(string key, out object value)
        {
            int i = _keys.IndexOf(key);
            if (i >= 0) { value = _values[i]!; return true; }
            value = null!;
            return false;
        }
        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
        {
            for (int i = 0; i < _keys.Count; i++)
                yield return new KeyValuePair<string, object>(_keys[i], _values[i]!);
        }
        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable<KeyValuePair<string, object>>)this).GetEnumerator();
    }

    /// <summary>Minimal <see cref="INode"/> fake for the value-routing spot-checks.</summary>
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
}
