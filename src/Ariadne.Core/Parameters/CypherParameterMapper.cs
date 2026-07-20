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
/// (<c>List</c>/<c>Map</c>/<c>Json</c>) and deferred tags (<c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>)
/// intentionally hit the fail-loud default and throw <see cref="CypherParameterException"/>.
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
        // Case-insensitive tag matching per the spec.
        switch ((p.Type ?? string.Empty).ToUpperInvariant())
        {
            case "STRING":
                return Required(p, p.StringValue, "StringValue");

            case "INTEGER":
                // Neo4j Integer is Int64; the carrier is already long.
                return Required(p, p.IntegerValue, "IntegerValue");

            case "BOOLEAN":
                return Required(p, p.BooleanValue, "BooleanValue");

            case "FLOAT":
                // Neo4j has no decimal type; Float (IEEE-754 double) is the only target.
                // (double)decimal is always finite and in range, so the "reject non-finite" rule
                // cannot trigger from this carrier. Precision loss is expected and documented.
                return (double)Required(p, p.FloatValue, "FloatValue");

            case "DATE":
            {
                var dt = RequiredDateTime(p);
                return new LocalDate(dt.Year, dt.Month, dt.Day);
            }

            case "TIME":
            {
                var dt = RequiredDateTime(p);
                return new LocalTime(dt.Hour, dt.Minute, dt.Second, NanosecondOfSecond(dt));
            }

            case "DATETIME":
                // Decision A: zoneless. Never fabricate a zone.
            {
                var dt = RequiredDateTime(p);
                return new LocalDateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second,
                    NanosecondOfSecond(dt));
            }

            case "ZONEDDATETIME":
                return BuildZoned(p);

            case "BYTES":
                return Required(p, p.BytesValue, "BytesValue");

            case "NULL":
                return null;

            default:
                // Unknown, composite (List/Map/Json), or deferred (Duration/Point/OffsetTime) tag.
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' has unsupported type '{p.Type}'.");
        }
    }

    private static ZonedDateTime BuildZoned(CypherParameter p)
    {
        var dt = RequiredDateTime(p);

        // A zone is only ever produced when the caller supplied one — never invented.
        if (!string.IsNullOrEmpty(p.ZoneId))
        {
            try
            {
                return new ZonedDateTime(dt, p.ZoneId);
            }
            catch (Exception ex)
            {
                throw new CypherParameterException(
                    $"Cypher parameter '{p.Name}' has an invalid ZoneId '{p.ZoneId}'.", ex);
            }
        }

        if (p.OffsetMinutes is int minutes)
        {
            // The driver takes offset seconds.
            return new ZonedDateTime(dt, minutes * 60);
        }

        throw new CypherParameterException(
            $"Cypher parameter '{p.Name}' is ZonedDateTime but supplies neither ZoneId nor OffsetMinutes " +
            "(a zone is never fabricated).");
    }

    /// <summary>Nanosecond component within the second, from the DateTime's 100-ns ticks (0..999,999,900).</summary>
    private static int NanosecondOfSecond(DateTime dt)
        => (int)(dt.Ticks % TimeSpan.TicksPerSecond) * 100;

    private static DateTime RequiredDateTime(CypherParameter p)
    {
        if (p.DateTimeValue is DateTime dt) return dt;
        throw new CypherParameterException(
            $"Cypher parameter '{p.Name}' (type '{p.Type}') is missing its DateTimeValue.");
    }

    private static T Required<T>(CypherParameter p, T? value, string carrier) where T : struct
    {
        if (value is T v) return v;
        throw new CypherParameterException(
            $"Cypher parameter '{p.Name}' (type '{p.Type}') is missing its {carrier}.");
    }

    private static T Required<T>(CypherParameter p, T? value, string carrier) where T : class
    {
        if (value is T v) return v;
        throw new CypherParameterException(
            $"Cypher parameter '{p.Name}' (type '{p.Type}') is missing its {carrier}.");
    }
}
