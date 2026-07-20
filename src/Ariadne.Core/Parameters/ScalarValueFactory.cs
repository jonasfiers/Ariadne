using System;
using Neo4j.Driver;

namespace Ariadne.Core.Parameters;

/// <summary>
/// The single, shared place that constructs a <c>Neo4j.Driver</c> temporal value from already-extracted
/// primitive parts (a CLR <see cref="DateTime"/> plus, for a zoned value, a zone id or fixed offset).
/// </summary>
/// <remarks>
/// <para>
/// This exists so that <b>both</b> the scalar/List/Map path (<see cref="CypherParameterMapper"/>, which
/// reads a <see cref="DateTime"/> from a carrier) and the recursive <c>Json</c> escape-hatch path
/// (<see cref="TypedJsonBuilder"/>, which parses a <see cref="DateTime"/> from an ISO-8601 string) build
/// the <em>identical</em> driver temporal — same sub-second (100&#8209;ns) precision, the same
/// <see cref="DateTime.Kind"/>&#8209;independence for zoned values, and the same <c>offset = minutes&#215;60</c>
/// arithmetic. A <c>Json</c> <c>Date</c>/<c>Time</c>/<c>DateTime</c>/<c>ZonedDateTime</c> node is therefore
/// byte-identical to the scalar-path value, which cross-consistency tests assert directly.
/// </para>
/// <para>
/// The temporal-construction logic lives here and nowhere else — it is deliberately NOT duplicated in the
/// JSON builder.
/// </para>
/// </remarks>
internal static class ScalarValueFactory
{
    /// <summary>Maximum magnitude of a UTC offset in minutes (±18:00), matching the ISO/driver bound.</summary>
    private const int MaxOffsetMinutes = 18 * 60;


    /// <summary>Builds a driver <see cref="LocalDate"/> from the date part of <paramref name="dt"/> (time ignored).</summary>
    public static LocalDate BuildDate(DateTime dt)
        => new LocalDate(dt.Year, dt.Month, dt.Day);

    /// <summary>Builds a driver <see cref="LocalTime"/> from the time part of <paramref name="dt"/> (date ignored), preserving 100&#8209;ns precision.</summary>
    public static LocalTime BuildTime(DateTime dt)
        => new LocalTime(dt.Hour, dt.Minute, dt.Second, NanosecondOfSecond(dt));

    /// <summary>Builds a zoneless driver <see cref="LocalDateTime"/> (Decision A) from <paramref name="dt"/>, preserving 100&#8209;ns precision.</summary>
    public static LocalDateTime BuildLocalDateTime(DateTime dt)
        => new LocalDateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, NanosecondOfSecond(dt));

    /// <summary>
    /// Builds a driver <see cref="ZonedDateTime"/> from <paramref name="dt"/> plus a supplied zone id
    /// (preferred) or fixed <paramref name="offsetMinutes"/> — never fabricating a zone.
    /// </summary>
    /// <param name="dt">The wall-clock value. Its <see cref="DateTime.Kind"/> is normalized away (see remarks).</param>
    /// <param name="zoneId">IANA zone id (e.g. <c>Europe/Brussels</c>); preferred when non-empty.</param>
    /// <param name="offsetMinutes">Fixed UTC offset in minutes, used only when <paramref name="zoneId"/> is empty.</param>
    /// <param name="context">Human-readable descriptor of the offender, used verbatim in error messages.</param>
    /// <exception cref="CypherParameterException">
    /// Neither a zone id nor an offset was supplied (a zone is never invented), or the zone id is invalid.
    /// </exception>
    /// <remarks>
    /// The <see cref="DateTime.Kind"/> is forced to <see cref="DateTimeKind.Unspecified"/> so the wall-clock
    /// is interpreted literally in the supplied zone — never UTC-shifted (which <c>Kind=Utc</c> would cause,
    /// a silent miscompute) and never offset-validated-and-rejected (which <c>Kind=Local</c> could cause).
    /// Behaviour must not depend on the hidden Kind flag (design principle 5).
    /// </remarks>
    public static ZonedDateTime BuildZoned(DateTime dt, string? zoneId, int? offsetMinutes, string context)
    {
        var normalized = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);

        // A zone is only ever produced when the caller supplied one.
        if (!string.IsNullOrEmpty(zoneId))
        {
            try
            {
                return new ZonedDateTime(normalized, zoneId);
            }
            catch (Exception ex)
            {
                throw new CypherParameterException(
                    $"{context} has an invalid ZoneId '{zoneId}'.", ex);
            }
        }

        if (offsetMinutes is int minutes)
        {
            // A UTC offset is bounded to ±18:00; reject absurd values loudly rather than let
            // minutes×60 overflow into a silently wrong offset.
            if (minutes < -MaxOffsetMinutes || minutes > MaxOffsetMinutes)
                throw new CypherParameterException(
                    $"{context} has an OffsetMinutes of {minutes}, outside the valid range ±{MaxOffsetMinutes} (±18:00).");

            // The driver takes offset seconds.
            return new ZonedDateTime(normalized, minutes * 60);
        }

        throw new CypherParameterException(
            $"{context} is ZonedDateTime but supplies neither ZoneId nor OffsetMinutes " +
            "(a zone is never fabricated).");
    }

    /// <summary>Nanosecond component within the second, from the DateTime's 100-ns ticks (0..999,999,900).</summary>
    internal static int NanosecondOfSecond(DateTime dt)
        => (int)(dt.Ticks % TimeSpan.TicksPerSecond) * 100;
}
