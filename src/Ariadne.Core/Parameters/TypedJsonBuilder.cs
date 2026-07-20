using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Ariadne.Core.Parameters;

/// <summary>
/// The recursive builder for the <c>Json</c> escape-hatch parameter (Decision B): it parses a
/// caller-supplied typed-JSON string with <c>System.Text.Json</c> and walks it, producing the exact
/// <c>Neo4j.Driver</c> value types — scalars plus nested <see cref="IList{T}"/> /
/// <see cref="IDictionary{TKey,TValue}"/> of <c>object?</c> to arbitrary depth.
/// </summary>
/// <remarks>
/// <para>
/// Every node is a JSON object carrying an explicit <c>$type</c> tag and (except <c>Null</c>) a
/// <c>$value</c>. Because the type is <em>declared</em>, the mapping stays lossless and fail-loud — a raw
/// JSON primitive is never guessed into a driver type. Temporal construction is <b>not</b> re-implemented
/// here: this builder parses the primitive parts (a <see cref="DateTime"/>, a zone id / offset) and hands
/// them to <see cref="ScalarValueFactory"/>, the same code the scalar/List/Map path uses, so a
/// <c>Json</c> temporal node is byte-identical to its scalar-path equivalent.
/// </para>
/// <para>
/// Every failure throws <see cref="CypherParameterException"/> with a JSON path to the offending node
/// (e.g. <c>$[2].props</c>): invalid JSON, a node that is not an object, a node missing its <c>$type</c>,
/// an unknown or deferred (<c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>) <c>$type</c>, a nested
/// <c>Json</c> <c>$type</c> (valid at the top level only), a <c>$value</c> that does not parse for its
/// declared type, a <c>ZonedDateTime</c> supplying neither <c>$zone</c> nor <c>$offsetMinutes</c>, or a
/// <c>Map</c> node with a duplicate or empty key.
/// </para>
/// </remarks>
internal static class TypedJsonBuilder
{
    private const string TypeKey = "$type";
    private const string ValueKey = "$value";
    private const string ZoneKey = "$zone";
    private const string OffsetKey = "$offsetMinutes";

    /// <summary>
    /// Parses and builds the driver value tree from the top-level typed-JSON payload of a <c>Json</c>
    /// parameter. The root is itself one node (usually a <c>List</c> or <c>Map</c>).
    /// </summary>
    /// <param name="jsonValue">The raw typed-JSON string (the <c>JsonValue</c> carrier).</param>
    /// <param name="parameterContext">Descriptor of the owning parameter, used in the missing/invalid messages.</param>
    /// <returns>The driver value: a scalar, or a nested <see cref="IList{T}"/>/<see cref="IDictionary{TKey,TValue}"/>.</returns>
    /// <exception cref="CypherParameterException">The payload is null, not valid JSON, or contains an invalid node.</exception>
    public static object? Build(string? jsonValue, string parameterContext)
    {
        if (jsonValue is null)
            throw new CypherParameterException(
                $"{parameterContext} (type 'Json') is missing its JsonValue.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonValue);
        }
        catch (JsonException ex)
        {
            throw new CypherParameterException(
                $"{parameterContext} (type 'Json') is not valid JSON: {ex.Message}", ex);
        }

        // Everything BuildNode returns is a fully materialized value (driver scalar, boxed primitive,
        // byte[], List<object?>, Dictionary<string,object?>) — no JsonElement escapes — so it is safe to
        // dispose the document once the walk completes.
        using (doc)
        {
            return BuildNode(doc.RootElement, "$");
        }
    }

    private static object? BuildNode(JsonElement node, string path)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new CypherParameterException(
                $"Typed-JSON node at {path} must be a JSON object with a \"$type\" tag, but was {node.ValueKind}.");

        if (!node.TryGetProperty(TypeKey, out var typeEl))
            throw new CypherParameterException(
                $"Typed-JSON node at {path} is missing its \"$type\" tag (an untagged node is never guessed).");

        if (typeEl.ValueKind != JsonValueKind.String)
            throw new CypherParameterException(
                $"Typed-JSON node at {path} has a non-string \"$type\".");

        var type = typeEl.GetString()!;
        switch (type.ToUpperInvariant())
        {
            case "STRING":
            {
                var v = RequireValue(node, path, type);
                if (v.ValueKind != JsonValueKind.String)
                    throw WrongValue(path, type, "a JSON string", v.ValueKind);
                return v.GetString();
            }

            case "INTEGER":
            {
                var v = RequireValue(node, path, type);
                // Must be a JSON number that is an exact 64-bit integer. A string ("abc"), a fractional
                // number (3.5), or an out-of-range value all fail loud — never coerced.
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var l))
                    throw WrongValue(path, type, "a 64-bit integer JSON number", v.ValueKind);
                return l;
            }

            case "FLOAT":
            {
                var v = RequireValue(node, path, type);
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d))
                    throw WrongValue(path, type, "a JSON number", v.ValueKind);
                // JSON has no Infinity/NaN literal, so a parsed double is always finite here.
                return d;
            }

            case "BOOLEAN":
            {
                var v = RequireValue(node, path, type);
                if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
                    throw WrongValue(path, type, "a JSON boolean", v.ValueKind);
                return v.GetBoolean();
            }

            case "NULL":
                // Null carries no $value (a present $value, if any, is irrelevant).
                return null;

            case "DATE":
                return ScalarValueFactory.BuildDate(ParseDateTime(node, path, type));

            case "TIME":
                return ScalarValueFactory.BuildTime(ParseDateTime(node, path, type));

            case "DATETIME":
                return ScalarValueFactory.BuildLocalDateTime(ParseDateTime(node, path, type));

            case "ZONEDDATETIME":
                return BuildZoned(node, path, type);

            case "BYTES":
            {
                var v = RequireValue(node, path, type);
                if (v.ValueKind != JsonValueKind.String)
                    throw WrongValue(path, type, "a base64 JSON string", v.ValueKind);
                try
                {
                    return v.GetBytesFromBase64();
                }
                catch (FormatException ex)
                {
                    throw new CypherParameterException(
                        $"Typed-JSON node at {path} (type '{type}') has a \"$value\" that is not valid base64.", ex);
                }
            }

            case "LIST":
                return BuildList(node, path, type);

            case "MAP":
                return BuildMap(node, path, type);

            case "JSON":
                // `Json` is only valid as the top-level parameter type; a nested structure uses nested
                // List/Map nodes, never a Json-in-Json.
                throw new CypherParameterException(
                    $"Typed-JSON node at {path} has \"$type\" 'Json', which is only valid at the top level — " +
                    "use nested List/Map nodes for nesting, not a Json node inside the tree.");

            case "DURATION":
            case "POINT":
            case "OFFSETTIME":
                // Deferred types are unsupported even inside Json — they fail loud.
                throw new CypherParameterException(
                    $"Typed-JSON node at {path} has deferred/unsupported \"$type\" '{type}'.");

            default:
                throw new CypherParameterException(
                    $"Typed-JSON node at {path} has unknown \"$type\" '{type}'.");
        }
    }

    private static IList<object?> BuildList(JsonElement node, string path, string type)
    {
        var v = RequireValue(node, path, type);
        if (v.ValueKind != JsonValueKind.Array)
            throw WrongValue(path, type, "a JSON array", v.ValueKind);

        var result = new List<object?>(v.GetArrayLength());
        var i = 0;
        foreach (var element in v.EnumerateArray())
        {
            result.Add(BuildNode(element, $"{path}[{i}]"));
            i++;
        }
        return result;
    }

    private static IDictionary<string, object?> BuildMap(JsonElement node, string path, string type)
    {
        var v = RequireValue(node, path, type);
        if (v.ValueKind != JsonValueKind.Object)
            throw WrongValue(path, type, "a JSON object", v.ValueKind);

        // Ordinal/case-sensitive keys, matching the flat Map path (Cypher map keys are case-sensitive).
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in v.EnumerateObject())
        {
            var key = member.Name;
            if (string.IsNullOrWhiteSpace(key))
                throw new CypherParameterException(
                    $"Typed-JSON Map node at {path} has an empty or whitespace key.");

            // System.Text.Json preserves duplicate JSON keys; last-write-wins would silently drop a value.
            if (result.ContainsKey(key))
                throw new CypherParameterException(
                    $"Typed-JSON Map node at {path} has a duplicate key '{key}' " +
                    "(Cypher map keys are case-sensitive; last-write-wins would silently drop a value).");

            result[key] = BuildNode(member.Value, $"{path}.{key}");
        }
        return result;
    }

    private static object BuildZoned(JsonElement node, string path, string type)
    {
        var dt = ParseDateTime(node, path, type);

        string? zoneId = null;
        if (node.TryGetProperty(ZoneKey, out var zoneEl))
        {
            if (zoneEl.ValueKind != JsonValueKind.String)
                throw new CypherParameterException(
                    $"Typed-JSON node at {path} (type '{type}') has a non-string \"$zone\".");
            zoneId = zoneEl.GetString();
        }

        int? offsetMinutes = null;
        if (node.TryGetProperty(OffsetKey, out var offsetEl))
        {
            if (offsetEl.ValueKind != JsonValueKind.Number || !offsetEl.TryGetInt32(out var m))
                throw new CypherParameterException(
                    $"Typed-JSON node at {path} (type '{type}') has a \"$offsetMinutes\" that is not a 32-bit integer.");
            offsetMinutes = m;
        }

        // ScalarValueFactory.BuildZoned throws (with this path in the message) when neither is supplied,
        // and normalizes DateTime.Kind exactly as the scalar path does.
        return ScalarValueFactory.BuildZoned(dt, zoneId, offsetMinutes, $"Typed-JSON node at {path}");
    }

    /// <summary>
    /// Reads and ISO-8601-parses the <c>$value</c> of a temporal node into a zoneless
    /// <see cref="DateTime"/> (each temporal builder then takes the part it needs). The string is parsed
    /// with the invariant culture and no styles, so a zoneless wall-clock stays <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    private static DateTime ParseDateTime(JsonElement node, string path, string type)
    {
        var v = RequireValue(node, path, type);
        if (v.ValueKind != JsonValueKind.String)
            throw WrongValue(path, type, "an ISO-8601 string", v.ValueKind);

        var s = v.GetString();
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            throw new CypherParameterException(
                $"Typed-JSON node at {path} (type '{type}') has a \"$value\" '{s}' that is not a valid ISO-8601 {type}.");
        return dt;
    }

    private static JsonElement RequireValue(JsonElement node, string path, string type)
    {
        if (!node.TryGetProperty(ValueKey, out var value))
            throw new CypherParameterException(
                $"Typed-JSON node at {path} (type '{type}') is missing its \"$value\".");
        return value;
    }

    private static CypherParameterException WrongValue(string path, string type, string expected, JsonValueKind actual)
        => new CypherParameterException(
            $"Typed-JSON node at {path} (type '{type}') expects {expected} in \"$value\", but was {actual}.");
}
