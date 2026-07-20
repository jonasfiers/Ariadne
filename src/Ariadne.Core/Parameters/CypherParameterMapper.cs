using System;
using System.Collections.Generic;
using Neo4j.Driver;

namespace Ariadne.Core.Parameters;

/// <summary>
/// Maps caller-supplied, explicitly-typed <see cref="CypherParameter"/> values to the exact
/// <c>Neo4j.Driver</c> value types the driver expects for parameter binding.
/// </summary>
/// <remarks>
/// <para>
/// This is pure logic: it builds and returns a plain dictionary and opens no driver, session, or
/// connection. Only the <b>scalar</b> tag subset is implemented here. Composite tags
/// (<c>List</c>/<c>Map</c>) are mapped by reusing the scalar path for each element; the <c>Json</c>
/// escape hatch and the deferred tags (<c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>) intentionally
/// hit the fail-loud default and throw <see cref="CypherParameterException"/>.
/// </para>
/// <para>
/// Design philosophy (inherited from PICASSO): fail loud, never silently miscompute. Every
/// unmappable input throws a named exception naming the offending parameter — nothing is skipped
/// or guessed. A CLR <see cref="DateTime"/> is never handed to the driver directly (its implicit
/// temporal conversion keys on the hidden <see cref="DateTime.Kind"/> flag); the explicit driver
/// temporal type is constructed from the tag instead.
/// </para>
/// </remarks>
public static class CypherParameterMapper
{
    /// <summary>
    /// Builds the driver parameter dictionary from the supplied parameters, validating each name and
    /// mapping each value by its <see cref="CypherParameter.Type"/> tag (case-insensitive).
    /// </summary>
    /// <param name="parameters">The parameters to map. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A dictionary from parameter name to the driver value (a driver temporal, a primitive, a
    /// <see cref="byte"/> array, or <see langword="null"/> for the <c>Null</c> tag), ready to pass to
    /// the driver as query parameters.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="CypherParameterException">
    /// A name is empty/whitespace, contains <c>$</c> or whitespace, or is duplicated; a required value
    /// carrier is missing; a <c>ZonedDateTime</c> supplies neither zone id nor offset; or the type tag
    /// is unknown, composite, or deferred.
    /// </exception>
    public static IDictionary<string, object?> BuildParameters(IEnumerable<CypherParameter> parameters)
    {
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));

        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            ValidateName(p, dict);
            dict[p.Name] = BuildValue(p);
        }
        return dict;
    }

    private static void ValidateName(CypherParameter p, Dictionary<string, object?> dict)
    {
        var name = p.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new CypherParameterException("Cypher parameter name is empty or whitespace.");

        // A '$' or any whitespace inside the name would produce an invalid Cypher identifier.
        if (name.IndexOf('$') >= 0)
            throw new CypherParameterException(
                $"Cypher parameter name '{name}' must not contain '$' (the binding prefix is implicit).");

        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch))
                throw new CypherParameterException(
                    $"Cypher parameter name '{name}' must not contain whitespace.");
        }

        if (dict.ContainsKey(name))
            throw new CypherParameterException(
                $"Duplicate Cypher parameter name '{name}' (last-write-wins would silently drop a value).");
    }

    private static object? BuildValue(CypherParameter p)
    {
        // The two flat composites are handled here; everything else — every scalar tag, plus the
        // fail-loud rejection of Json/deferred/unknown tags — goes through the single shared scalar
        // path (MapScalar), which is the SAME path used for each List element and Map entry.
        switch ((p.Type ?? string.Empty).ToUpperInvariant())
        {
            case "LIST":
                return BuildList(p);

            case "MAP":
                return BuildMap(p);

            case "JSON":
                // The recursive typed-JSON escape hatch — top-level only. Nested `Json` nodes fail loud
                // inside the builder; a `Json` element inside a flat List/Map still fails loud in MapScalar.
                return TypedJsonBuilder.Build(p.JsonValue, $"Cypher parameter '{p.Name}'");

            default:
                return MapScalar(p.Type, p, $"Cypher parameter '{p.Name}'");
        }
    }

    /// <summary>
    /// The one and only scalar-mapping path. Maps a single scalar <paramref name="type"/> tag reading
    /// its value through <paramref name="carrier"/>, and is called identically for a top-level scalar
    /// parameter and for each element of a flat <c>List</c>/<c>Map</c>. Any composite
    /// (<c>List</c>/<c>Map</c>/<c>Json</c>), deferred, or unknown tag is rejected here, so a nested
    /// composite element fails loud in exactly one place.
    /// </summary>
    /// <param name="type">The scalar type tag (case-insensitive).</param>
    /// <param name="carrier">The value carrier to read the scalar from.</param>
    /// <param name="context">
    /// A human-readable descriptor of what is being mapped (e.g. <c>Cypher parameter 'foo'</c> or
    /// <c>Cypher parameter 'foo' list element [2]</c>), used verbatim in every error message.
    /// </param>
    private static object? MapScalar(string? type, IScalarCarrier carrier, string context)
    {
        // Case-insensitive tag matching per the spec.
        switch ((type ?? string.Empty).ToUpperInvariant())
        {
            case "STRING":
                return Required(carrier.StringValue, context, type, "StringValue");

            case "INTEGER":
                // Neo4j Integer is Int64; the carrier is already long.
                return Required(carrier.IntegerValue, context, type, "IntegerValue");

            case "BOOLEAN":
                return Required(carrier.BooleanValue, context, type, "BooleanValue");

            case "FLOAT":
                // Neo4j has no decimal type; Float (IEEE-754 double) is the only target.
                // (double)decimal is always finite and in range, so the "reject non-finite" rule
                // cannot trigger from this carrier. Precision loss is expected and documented.
                return (double)Required(carrier.FloatValue, context, type, "FloatValue");

            case "DATE":
                // Shared construction (ScalarValueFactory) — identical to the Json path.
                return ScalarValueFactory.BuildDate(RequiredDateTime(carrier, context, type));

            case "TIME":
                return ScalarValueFactory.BuildTime(RequiredDateTime(carrier, context, type));

            case "DATETIME":
                // Decision A: zoneless. Never fabricate a zone.
                return ScalarValueFactory.BuildLocalDateTime(RequiredDateTime(carrier, context, type));

            case "ZONEDDATETIME":
                return ScalarValueFactory.BuildZoned(
                    RequiredDateTime(carrier, context, type), carrier.ZoneId, carrier.OffsetMinutes, context);

            case "BYTES":
                return Required(carrier.BytesValue, context, type, "BytesValue");

            case "NULL":
                return null;

            case "LIST":
            case "MAP":
            case "JSON":
                // A composite tag where only a scalar is allowed — i.e. nesting. Flat List/Map elements
                // must be scalar; nested structures are the `Json` parameter's job (Decision B, rule 8).
                throw new CypherParameterException(
                    $"{context} has composite type '{type}'; flat List/Map elements must be scalar — " +
                    "use the `Json` parameter type for nested structures.");

            default:
                // Unknown or deferred (Duration/Point/OffsetTime) tag.
                throw new CypherParameterException(
                    $"{context} has unsupported type '{type}'.");
        }
    }

    private static IList<object?> BuildList(CypherParameter p)
    {
        var elements = p.ListElements;
        if (elements is null)
            throw new CypherParameterException(
                $"Cypher parameter '{p.Name}' (type 'List') is missing its ListElements.");

        // An empty list is valid — it maps to an empty driver list.
        var result = new List<object?>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (element is null)
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' list element [{i}] is null.");

            // Same scalar path as a top-level scalar — nesting fails loud inside MapScalar.
            result.Add(MapScalar(element.Type, element, $"Cypher parameter '{p.Name}' list element [{i}]"));
        }

        return result;
    }

    private static IDictionary<string, object?> BuildMap(CypherParameter p)
    {
        var entries = p.MapEntries;
        if (entries is null)
            throw new CypherParameterException(
                $"Cypher parameter '{p.Name}' (type 'Map') is missing its MapEntries.");

        // Cypher map keys are case-sensitive (A and a are distinct), matching the parameter-name rule.
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry is null)
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' map entry [{i}] is null.");

            var key = entry.Key;
            if (string.IsNullOrWhiteSpace(key))
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' has a map entry (index {i}) with an empty or whitespace Key.");

            if (result.ContainsKey(key))
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' has a duplicate map Key '{key}' " +
                    "(Cypher map keys are case-sensitive; last-write-wins would silently drop a value).");

            // Same scalar path as a top-level scalar — nesting fails loud inside MapScalar.
            result[key] = MapScalar(entry.Type, entry, $"Cypher parameter '{p.Name}' map entry with Key '{key}'");
        }

        return result;
    }

    private static DateTime RequiredDateTime(IScalarCarrier carrier, string context, string? type)
    {
        if (carrier.DateTimeValue is DateTime dt) return dt;
        throw new CypherParameterException(
            $"{context} (type '{type}') is missing its DateTimeValue.");
    }

    private static T Required<T>(T? value, string context, string? type, string carrier) where T : struct
    {
        if (value is T v) return v;
        throw new CypherParameterException(
            $"{context} (type '{type}') is missing its {carrier}.");
    }

    private static T Required<T>(T? value, string context, string? type, string carrier) where T : class
    {
        if (value is T v) return v;
        throw new CypherParameterException(
            $"{context} (type '{type}') is missing its {carrier}.");
    }
}
