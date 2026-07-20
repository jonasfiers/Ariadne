using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Neo4j.Driver;

namespace Ariadne.Core.Results;

/// <summary>
/// Serializes a single Cypher result value — leaf, composite, or graph type, as the <c>Neo4j.Driver</c>
/// hands it back — into the canonical result JSON defined by the result spec (§3 value type map, §4 graph
/// envelopes, §5 null handling, §6 arbitrary-key maps). This is the inverse of the parameter mapper and the
/// foundation the record envelope (Feature 06) builds on.
/// </summary>
/// <remarks>
/// <para>
/// The primitive entry point <see cref="Write(Utf8JsonWriter, object?)"/> writes one value into an open
/// <see cref="Utf8JsonWriter"/>; composites recurse through the <em>same</em> call — a list element, a map
/// value, or a node/relationship property is serialized by <see cref="Write(Utf8JsonWriter, object?)"/>
/// again — so arbitrary nesting works to any depth and every leaf rule (fail-loud, 100-ns precision) is
/// inherited unchanged. <see cref="Serialize(object?)"/> is a convenience wrapper returning the JSON string.
/// </para>
/// <para>
/// Supported leaf types: <see langword="null"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="double"/>
/// (finite only), <see cref="string"/>, <see cref="byte"/><c>[]</c> (base64), and the driver temporals
/// <see cref="LocalDate"/>, <see cref="LocalTime"/>, <see cref="LocalDateTime"/> and
/// <see cref="ZonedDateTime"/> (the pinned <c>{ "value": "&lt;zoneless ISO&gt;", "zone": "&lt;id or ±HH:MM&gt;" }</c>
/// shape). Temporal rendering is strict, culture-invariant ISO-8601 via <see cref="TemporalFormat"/>.
/// Supported composites/graph types: <see cref="System.Collections.IList"/> → JSON array,
/// <see cref="System.Collections.IDictionary"/> → JSON object (arbitrary keys verbatim, §6),
/// <see cref="INode"/>/<see cref="IRelationship"/>/<see cref="IPath"/> → the canonical §4 envelopes
/// (<c>elementId</c> only — the deprecated numeric <c>id</c> is never emitted).
/// </para>
/// <para>
/// <b>Fail loud (the result-side cardinal rule):</b> any value whose runtime type is not supported — a
/// deferred <c>Duration</c>/<c>Point</c>/<c>OffsetTime</c> or an unknown type — throws
/// <see cref="CypherResultException"/> naming the runtime type; a placeholder or guessed serialization is
/// never emitted. This applies <em>at any depth</em>: an unsupported value nested inside a list, map, or
/// property throws mid-tree and the exception <b>propagates</b>. Per BACKLOG N1 the serializer never
/// catches it nor tries to "close" the partial JSON — the writer is left holding an incomplete token and
/// the record layer (Feature 06) is responsible for abandoning that writer/buffer.
/// </para>
/// </remarks>
public static class CypherValueSerializer
{
    /// <summary>
    /// The canonical <see cref="Utf8JsonWriter"/> options for result JSON: relaxed escaping so data-safe
    /// characters (notably the <c>+</c> in an offset <c>zone</c>, and non-ASCII text) render literally
    /// rather than as <c>\uXXXX</c> — producing the clean, sample-friendly output the result spec's
    /// examples show. The output is still strictly valid JSON. Later features (the record envelope) create
    /// their top-level writer with these same options so every layer encodes identically.
    /// </summary>
    /// <remarks>
    /// "Relaxed" leaves the HTML-sensitive characters <c>&lt; &gt; &amp;</c> unescaped; this output is a
    /// JSON data payload consumed by <c>JSONDeserialize</c>, never injected into an HTML page, so that is
    /// safe here.
    /// </remarks>
    public static readonly JsonWriterOptions CanonicalWriterOptions = new JsonWriterOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Writes a single Cypher result value as JSON into <paramref name="writer"/>: a leaf (a literal, a
    /// string, or — for <see cref="ZonedDateTime"/> — a <c>{ value, zone }</c> object), or a composite/graph
    /// type (array, object, or §4 node/relationship/path envelope) whose children recurse through this same
    /// method.
    /// </summary>
    /// <param name="writer">The open writer to emit into. Must not be <see langword="null"/>.</param>
    /// <param name="value">The driver value to serialize (may be <see langword="null"/> → JSON <c>null</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="CypherResultException">
    /// The value's runtime type is unsupported (a deferred <c>Duration</c>/<c>Point</c>/<c>OffsetTime</c> or
    /// an unknown type) — <em>including nested inside a list/map/property, in which case the throw
    /// propagates and the writer is left mid-token</em> — or the value is a non-finite <see cref="double"/>,
    /// or a temporal carries sub-100-ns precision.
    /// </exception>
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        switch (value)
        {
            case null:
                // §5: emit an explicit JSON null (never omit) so the record shape stays stable for sampling.
                writer.WriteNullValue();
                return;

            case bool b:
                writer.WriteBooleanValue(b);
                return;

            case long l:
                // Neo4j Integer is always 64-bit; the driver hands back a long.
                writer.WriteNumberValue(l);
                return;

            case double d:
                if (!IsFinite(d))
                    // JSON has no NaN/Infinity literal — fail loud with a named type rather than let the
                    // writer throw a bare ArgumentException.
                    throw new CypherResultException(
                        $"Cannot serialize Cypher Float value '{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}': " +
                        "JSON cannot represent NaN or Infinity.");
                writer.WriteNumberValue(d);
                return;

            case string s:
                writer.WriteStringValue(s);
                return;

            case byte[] bytes:
                // Bytes → base64 JSON string (the driver's Bytes type is a CLR byte[]).
                writer.WriteBase64StringValue(bytes);
                return;

            case LocalDate localDate:
                writer.WriteStringValue(TemporalFormat.Date(localDate));
                return;

            case LocalTime localTime:
                writer.WriteStringValue(TemporalFormat.Time(localTime));
                return;

            case LocalDateTime localDateTime:
                writer.WriteStringValue(TemporalFormat.DateTime(localDateTime));
                return;

            case ZonedDateTime zonedDateTime:
                WriteZoned(writer, zonedDateTime);
                return;

            // ---- composites + graph types (Feature 05) — each recurses back through Write ----

            case INode node:
                WriteNode(writer, node);
                return;

            case IRelationship relationship:
                WriteRelationship(writer, relationship);
                return;

            case IPath path:
                WritePath(writer, path);
                return;

            // A driver map (a bare Cypher map, or a node/relationship `properties`) arrives as a concrete
            // Dictionary<string, object>, which implements the non-generic IDictionary. §6: keys are arbitrary
            // strings, emitted verbatim as JSON object keys (no Pattern-B name/value list). Placed before IList
            // because a dictionary is not a list; neither can also be a graph type, so ordering among these is safe.
            case IDictionary map:
                WriteMap(writer, map);
                return;

            // A driver list arrives as an IList (List<object>, or a CLR array). `byte[]` is handled by the leaf
            // case above, so it never reaches here despite also being an IList.
            case IList list:
                WriteList(writer, list);
                return;

            default:
                // Deferred (Duration/Point/OffsetTime) or anything unknown: fail loud, naming the runtime type.
                // Never a placeholder or a guess.
                throw new CypherResultException(
                    $"Cannot serialize Cypher result value of unsupported type '{value.GetType().FullName}'. " +
                    "Supported types are null, Boolean, Integer (Int64), Float (double), String, Bytes (byte[]), " +
                    "the temporals LocalDate/LocalTime/LocalDateTime/ZonedDateTime, and the composites " +
                    "list (IList) / map (IDictionary) / Node / Relationship / Path. The deferred " +
                    "Duration/Point/OffsetTime are not supported.");
        }
    }

    /// <summary>
    /// Serializes a single leaf Cypher result value to its canonical JSON text (UTF-8, unindented).
    /// A convenience wrapper over <see cref="Write(Utf8JsonWriter, object?)"/>.
    /// </summary>
    /// <param name="value">The driver value to serialize.</param>
    /// <returns>The value's JSON representation as a string.</returns>
    /// <exception cref="CypherResultException">As documented on <see cref="Write(Utf8JsonWriter, object?)"/>.</exception>
    public static string Serialize(object? value)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, CanonicalWriterOptions))
            {
                Write(writer, value);
                writer.Flush();
            }
            // ToArray() keeps this netstandard2.0-safe (no ReadOnlySpan<byte> GetString overload needed).
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    /// <summary>
    /// Writes a <see cref="ZonedDateTime"/> as the pinned full-fidelity object
    /// <c>{ "value": "&lt;zoneless ISO wall-clock&gt;", "zone": "&lt;named id or ±HH:MM offset&gt;" }</c>.
    /// The wall-clock <c>value</c> is host-timezone independent; the zone is never fabricated (it is the
    /// driver's own <see cref="ZoneId"/> or <see cref="ZoneOffset"/>).
    /// </summary>
    private static void WriteZoned(Utf8JsonWriter writer, ZonedDateTime zonedDateTime)
    {
        writer.WriteStartObject();
        writer.WriteString("value", TemporalFormat.WallClock(zonedDateTime));
        writer.WriteString("zone", TemporalFormat.Zone(zonedDateTime));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a driver list (<see cref="IList"/>) as a JSON array — each element serialized by recursing
    /// through <see cref="Write(Utf8JsonWriter, object?)"/>, so nesting works to any depth and every leaf
    /// fail-loud rule is inherited (an unsupported element throws mid-array and the exception propagates —
    /// the writer is left with a partial token by design; see the class remarks / BACKLOG N1).
    /// </summary>
    private static void WriteList(Utf8JsonWriter writer, IList list)
    {
        writer.WriteStartArray();
        foreach (var element in list)
            Write(writer, element);
        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a driver map (<see cref="IDictionary"/> — a bare Cypher map) as a JSON object. Keys are
    /// arbitrary strings preserved <b>verbatim</b> (result spec §6 — no Pattern-B name/value list); a
    /// non-string key (which a Neo4j map never produces) fails loud rather than being coerced. Each value
    /// recurses through <see cref="Write(Utf8JsonWriter, object?)"/>.
    /// </summary>
    private static void WriteMap(Utf8JsonWriter writer, IDictionary map)
    {
        writer.WriteStartObject();
        foreach (DictionaryEntry entry in map)
        {
            writer.WritePropertyName(RequireStringKey(entry.Key));
            Write(writer, entry.Value);
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a graph entity's <c>properties</c> map (the driver's strongly-typed
    /// <see cref="IReadOnlyDictionary{TKey, TValue}"/> of <see cref="string"/>→<see cref="object"/>) as a
    /// JSON object — the same shape as <see cref="WriteMap"/>, but read from the graph type's typed member
    /// so it never depends on the concrete map class. Keys verbatim (§6); each value recurses through
    /// <see cref="Write(Utf8JsonWriter, object?)"/>. A <see langword="null"/> map (defensive; the driver
    /// supplies an empty map, never null) renders as <c>{}</c>.
    /// </summary>
    private static void WriteProperties(Utf8JsonWriter writer, IReadOnlyDictionary<string, object>? properties)
    {
        writer.WriteStartObject();
        if (properties != null)
        {
            foreach (var kv in properties)
            {
                writer.WritePropertyName(kv.Key);
                Write(writer, kv.Value);
            }
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an <see cref="INode"/> as the canonical node envelope
    /// <c>{ "elementId", "labels": [...], "properties": { ... } }</c> (result spec §4). The pre-5.0 numeric
    /// <c>id</c> is deprecated and deliberately not emitted (decision R2 — <c>elementId</c> only).
    /// </summary>
    private static void WriteNode(Utf8JsonWriter writer, INode node)
    {
        writer.WriteStartObject();

        writer.WriteString("elementId", node.ElementId);

        writer.WritePropertyName("labels");
        writer.WriteStartArray();
        if (node.Labels != null)
        {
            foreach (var label in node.Labels)
                writer.WriteStringValue(label);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("properties");
        WriteProperties(writer, node.Properties);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an <see cref="IRelationship"/> as the canonical relationship envelope
    /// <c>{ "elementId", "type", "startNodeElementId", "endNodeElementId", "properties": { ... } }</c>
    /// (result spec §4). Endpoints use their string <c>elementId</c>s; the deprecated numeric ids are not
    /// emitted (decision R2).
    /// </summary>
    private static void WriteRelationship(Utf8JsonWriter writer, IRelationship relationship)
    {
        writer.WriteStartObject();

        writer.WriteString("elementId", relationship.ElementId);
        writer.WriteString("type", relationship.Type);
        writer.WriteString("startNodeElementId", relationship.StartNodeElementId);
        writer.WriteString("endNodeElementId", relationship.EndNodeElementId);

        writer.WritePropertyName("properties");
        WriteProperties(writer, relationship.Properties);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an <see cref="IPath"/> as <c>{ "nodes": [ &lt;node&gt;, ... ], "relationships": [ &lt;rel&gt;, ... ] }</c>
    /// (result spec §4), the driver's traversal order preserved. Each node/relationship recurses through
    /// <see cref="Write(Utf8JsonWriter, object?)"/>, reusing the exact node/relationship envelope writers.
    /// </summary>
    private static void WritePath(Utf8JsonWriter writer, IPath path)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("nodes");
        writer.WriteStartArray();
        if (path.Nodes != null)
        {
            foreach (var node in path.Nodes)
                Write(writer, node);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("relationships");
        writer.WriteStartArray();
        if (path.Relationships != null)
        {
            foreach (var relationship in path.Relationships)
                Write(writer, relationship);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// A JSON object key must be a string. Neo4j map keys always are; a non-string key (never produced by
    /// the driver) fails loud rather than being silently coerced via <c>ToString()</c>.
    /// </summary>
    private static string RequireStringKey(object? key)
    {
        if (key is string s) return s;
        throw new CypherResultException(
            $"Cannot serialize a Cypher map with a non-string key of type " +
            $"'{key?.GetType().FullName ?? "null"}'; JSON object keys must be strings.");
    }

    /// <summary>double.IsFinite is unavailable on netstandard2.0; this is the equivalent.</summary>
    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
