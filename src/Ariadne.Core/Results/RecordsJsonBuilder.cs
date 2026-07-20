using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Neo4j.Driver;

namespace Ariadne.Core.Results;

/// <summary>
/// Assembles a whole Cypher query result — a sequence of driver <see cref="IRecord"/>s — into the canonical
/// <c>RecordsJson</c> envelope (result spec §2): a JSON <b>array</b> whose every element is a per-record JSON
/// <b>object</b> keyed by column name (in the record's <see cref="IRecord.Keys"/> order), each value serialized
/// by <see cref="CypherValueSerializer.Write(Utf8JsonWriter, object?)"/>. This is the last pure-serialization
/// layer before the connection/execution layer wires real queries in; it is pure over an already-materialized
/// record sequence and never opens a session, runs a query, or touches a cursor's async API.
/// </summary>
/// <remarks>
/// <para>
/// Every value goes through the Feature 04/05 <see cref="CypherValueSerializer"/> unchanged, so all leaf,
/// composite, and graph rules — and the fail-loud behaviour on an unsupported/unrepresentable value — are
/// inherited exactly. The column names for the result are exposed alongside the JSON (taken from the first
/// record's <see cref="IRecord.Keys"/>; an empty sequence yields <c>[]</c> and no columns). At execution time a
/// real result's columns come from the cursor's <c>Keys</c> even for an empty result — that wiring is a later
/// feature; here the columns come from whatever the records themselves carry.
/// </para>
/// <para>
/// <b>Writer integrity (BACKLOG N1 — this layer owns it).</b> A <see cref="CypherResultException"/> thrown by
/// the value serializer mid-record must abandon the <em>whole</em> result: the public string API throws and
/// returns <b>no</b> output — not a truncated array, not a half-record, and nothing leaked from a reused buffer.
/// This is structural, not defensive: <see cref="BuildRecordsJson(IEnumerable{IRecord})"/> serializes into a
/// fresh, method-local <see cref="MemoryStream"/> and only ever materializes the string from that stream
/// <em>after</em> the array has been closed and flushed in full. A throw propagates out before the string is
/// ever built, so a partially-written buffer can never reach the caller (proven by test).
/// </para>
/// <para>
/// Values are read by the integer indexer <see cref="IRecord.this[int]"/>, which is aligned 1:1 with
/// <see cref="IRecord.Keys"/>, rather than by the string indexer — so a (Bolt-impossible) duplicate column name
/// is detected and <see cref="CypherResultException"/> thrown loudly rather than silently emitting a duplicate
/// JSON object key.
/// </para>
/// </remarks>
public static class RecordsJsonBuilder
{
    /// <summary>
    /// Builds the full <c>RecordsJson</c> envelope from a materialized record sequence: the JSON array text and
    /// the ordered column names, produced in a single pass over the sequence.
    /// </summary>
    /// <param name="records">The already-materialized records. Must not be <see langword="null"/>; individual
    /// records must not be <see langword="null"/>.</param>
    /// <returns>The <see cref="RecordsJsonResult"/> carrying the JSON array text and the column names (the first
    /// record's <see cref="IRecord.Keys"/>, or empty for an empty sequence).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="CypherResultException">A record is <see langword="null"/>, a record has a duplicate
    /// column name, or a value is unserializable (fail-loud, inherited from
    /// <see cref="CypherValueSerializer"/>). On any throw no partial JSON is produced.</exception>
    public static RecordsJsonResult Build(IEnumerable<IRecord> records)
    {
        if (records is null) throw new ArgumentNullException(nameof(records));

        // A fresh, method-local buffer — never pooled or reused across calls — so a throw cannot leak a
        // partially-written result: the string below is materialized only after the array is fully closed.
        using (var stream = new MemoryStream())
        {
            IReadOnlyList<string> columns;
            using (var writer = new Utf8JsonWriter(stream, CypherValueSerializer.CanonicalWriterOptions))
            {
                columns = WriteRecords(writer, records);
                writer.Flush();
            }
            // Reached only when every record serialized cleanly and the array was closed. ToArray keeps this
            // netstandard2.0-safe.
            var json = Encoding.UTF8.GetString(stream.ToArray());
            return new RecordsJsonResult(json, columns);
        }
    }

    /// <summary>
    /// Builds just the <c>RecordsJson</c> array text (a convenience over <see cref="Build(IEnumerable{IRecord})"/>
    /// for callers that only need the JSON).
    /// </summary>
    /// <param name="records">The already-materialized records.</param>
    /// <returns>The JSON array of per-record objects.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is <see langword="null"/>.</exception>
    /// <exception cref="CypherResultException">As documented on <see cref="Build(IEnumerable{IRecord})"/>; on any
    /// throw no partial JSON is returned.</exception>
    public static string BuildRecordsJson(IEnumerable<IRecord> records) => Build(records).Json;

    /// <summary>
    /// Writes the <c>RecordsJson</c> array into an already-open <see cref="Utf8JsonWriter"/> and returns the
    /// ordered column names. This overload lets the future execution layer compose the records array into a
    /// larger writer without an intermediate string; the caller owns the writer and — per BACKLOG N1 — must
    /// abandon it (never flush partial JSON to a consumer) if this method throws.
    /// </summary>
    /// <param name="writer">The open writer to emit the array into. Must not be <see langword="null"/>.</param>
    /// <param name="records">The already-materialized records. Must not be <see langword="null"/>.</param>
    /// <returns>The column names — the first record's <see cref="IRecord.Keys"/>, or an empty list for an empty
    /// sequence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> or <paramref name="records"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="CypherResultException">A record is <see langword="null"/>, a record has a duplicate
    /// column name, or a value is unserializable. The exception propagates with the writer left mid-array (by
    /// design — the caller abandons it).</exception>
    public static IReadOnlyList<string> WriteRecords(Utf8JsonWriter writer, IEnumerable<IRecord> records)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        if (records is null) throw new ArgumentNullException(nameof(records));

        IReadOnlyList<string>? columns = null;

        writer.WriteStartArray();
        foreach (var record in records)
        {
            if (record is null)
                throw new CypherResultException(
                    "Cannot build RecordsJson: the record sequence contains a null record. " +
                    "A materialized Cypher result never yields a null record.");

            // Columns for the whole result come from the first record's Keys (result spec §2).
            columns ??= record.Keys;

            WriteRecord(writer, record);
        }
        writer.WriteEndArray();

        return columns ?? Array.Empty<string>();
    }

    /// <summary>
    /// Writes one record as a JSON object: each column name (in <see cref="IRecord.Keys"/> order) mapped to its
    /// value serialized by <see cref="CypherValueSerializer.Write(Utf8JsonWriter, object?)"/>. Values are read by
    /// the integer indexer, which is aligned with <c>Keys</c>; a duplicate column name fails loud rather than
    /// emitting a duplicate JSON object key.
    /// </summary>
    private static void WriteRecord(Utf8JsonWriter writer, IRecord record)
    {
        var keys = record.Keys;

        writer.WriteStartObject();

        // Guard against a (Bolt-impossible) duplicate column name. A single-column record can't collide, so the
        // set is allocated only when there is more than one column.
        HashSet<string>? seen = keys.Count > 1 ? new HashSet<string>(StringComparer.Ordinal) : null;
        for (int i = 0; i < keys.Count; i++)
        {
            var key = keys[i];

            if (seen != null && !seen.Add(key))
                throw new CypherResultException(
                    $"Cannot build RecordsJson: record has a duplicate column name '{key}'. " +
                    "A JSON object cannot have two members with the same name; a well-formed Cypher " +
                    "result never returns duplicate column names.");

            writer.WritePropertyName(key);
            // Values are read by the integer indexer (aligned with Keys), not the string indexer — so a
            // duplicate name never silently resolves to one column's value.
            CypherValueSerializer.Write(writer, record[i]);
        }

        writer.WriteEndObject();
    }
}
