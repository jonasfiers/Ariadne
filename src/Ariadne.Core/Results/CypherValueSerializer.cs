using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Neo4j.Driver;

namespace Ariadne.Core.Results;

/// <summary>
/// Serializes a single <b>leaf</b> Cypher result value — as the <c>Neo4j.Driver</c> hands it back — into
/// the canonical result JSON defined by the result spec (§3 value type map, §5 null handling). This is the
/// inverse of the parameter mapper and the foundation the graph/composite serializer (Feature 05) and the
/// record envelope (Feature 06) build on.
/// </summary>
/// <remarks>
/// <para>
/// The primitive entry point <see cref="Write(Utf8JsonWriter, object?)"/> writes one value into an open
/// <see cref="Utf8JsonWriter"/>; this composes cleanly for the later nested graph/list work (an element of
/// a list or a node property is serialized by the same call, recursively). <see cref="Serialize(object?)"/>
/// is a convenience wrapper returning the value's JSON as a string.
/// </para>
/// <para>
/// Supported leaf types: <see langword="null"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="double"/>
/// (finite only), <see cref="string"/>, <see cref="byte"/><c>[]</c> (base64), and the driver temporals
/// <see cref="LocalDate"/>, <see cref="LocalTime"/>, <see cref="LocalDateTime"/> and
/// <see cref="ZonedDateTime"/> (the pinned <c>{ "value": "&lt;zoneless ISO&gt;", "zone": "&lt;id or ±HH:MM&gt;" }</c>
/// shape). Temporal rendering is strict, culture-invariant ISO-8601 via <see cref="TemporalFormat"/>.
/// </para>
/// <para>
/// <b>Fail loud (the result-side cardinal rule):</b> any value whose runtime type is not in the supported
/// leaf set — a deferred <c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>, a composite <c>Node</c>/
/// <c>Relationship</c>/<c>Path</c>/<see cref="System.Collections.IList"/>/<see cref="System.Collections.IDictionary"/>
/// (Feature 05), or an unknown type — throws <see cref="CypherResultException"/> naming the runtime type.
/// A placeholder or guessed serialization is never emitted. Feature 05 will replace the composite throws
/// with real handling.
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
    /// Writes a single leaf Cypher result value as JSON into <paramref name="writer"/> (a bare JSON value:
    /// a literal, a string, or — for <see cref="ZonedDateTime"/> — a <c>{ value, zone }</c> object).
    /// </summary>
    /// <param name="writer">The open writer to emit into. Must not be <see langword="null"/>.</param>
    /// <param name="value">The driver value to serialize (may be <see langword="null"/> → JSON <c>null</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="CypherResultException">
    /// The value's runtime type is not a supported leaf (deferred/composite/unknown), the value is a
    /// non-finite <see cref="double"/>, or a temporal carries sub-100-ns precision.
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

            default:
                // Deferred (Duration/Point/OffsetTime), composite (Node/Relationship/Path/list/map — Feature 05),
                // or anything unknown: fail loud, naming the runtime type. Never a placeholder or a guess.
                throw new CypherResultException(
                    $"Cannot serialize Cypher result value of unsupported type '{value.GetType().FullName}'. " +
                    "Supported leaf types are null, Boolean, Integer (Int64), Float (double), String, Bytes (byte[]), " +
                    "and the temporals LocalDate/LocalTime/LocalDateTime/ZonedDateTime. Composites " +
                    "(Node/Relationship/Path/list/map) and the deferred Duration/Point/OffsetTime are not yet supported.");
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

    /// <summary>double.IsFinite is unavailable on netstandard2.0; this is the equivalent.</summary>
    private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
}
