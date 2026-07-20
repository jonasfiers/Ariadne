using System;
using System.Globalization;
using System.Text;
using Neo4j.Driver;

namespace Ariadne.Core.Results;

/// <summary>
/// The single, shared place that renders a <c>Neo4j.Driver</c> temporal into its canonical, strict,
/// culture-invariant ISO-8601 result string. All four temporal leaf types (<see cref="LocalDate"/>,
/// <see cref="LocalTime"/>, <see cref="LocalDateTime"/>, <see cref="ZonedDateTime"/>) route through here so
/// the date/time/fraction/offset rendering lives in exactly one place.
/// </summary>
/// <remarks>
/// <para>
/// Rendering reads the driver's stored wall-clock <b>components</b> (<c>Year</c>, <c>Month</c>, …,
/// <c>Nanosecond</c>) directly — never a converted <see cref="DateTime"/> — so the emitted string is the
/// literal local time and cannot depend on the host timezone (verified empirically against 5.28.3).
/// Every numeric part is formatted with <see cref="CultureInfo.InvariantCulture"/>.
/// </para>
/// <para>
/// Fractional seconds are emitted only when non-zero, at up to 7 digits (100&#8209;ns / one CLR tick), with
/// trailing zeros trimmed. A value carrying genuine sub-100-ns precision (a nanosecond not a multiple of
/// 100) cannot be represented in the pinned ≤7-digit format and throws <see cref="CypherResultException"/>
/// rather than being silently truncated.
/// </para>
/// </remarks>
internal static class TemporalFormat
{
    /// <summary>Renders a <see cref="LocalDate"/> as <c>yyyy-MM-dd</c>.</summary>
    public static string Date(LocalDate d)
        => DatePart(d.Year, d.Month, d.Day);

    /// <summary>Renders a <see cref="LocalTime"/> as <c>HH:mm:ss</c> or <c>HH:mm:ss.fffffff</c> (fraction only if non-zero).</summary>
    public static string Time(LocalTime t)
        => TimePart(t.Hour, t.Minute, t.Second, t.Nanosecond);

    /// <summary>Renders a zoneless <see cref="LocalDateTime"/> as <c>yyyy-MM-ddTHH:mm:ss[.fffffff]</c>.</summary>
    public static string DateTime(LocalDateTime dt)
        => DatePart(dt.Year, dt.Month, dt.Day) + "T" + TimePart(dt.Hour, dt.Minute, dt.Second, dt.Nanosecond);

    /// <summary>
    /// Renders the zoneless wall-clock of a <see cref="ZonedDateTime"/> as <c>yyyy-MM-ddTHH:mm:ss[.fffffff]</c>
    /// (the <c>value</c> field of the pinned <c>{ value, zone }</c> shape). The zone is rendered separately by
    /// <see cref="Zone"/>.
    /// </summary>
    public static string WallClock(ZonedDateTime z)
        => DatePart(z.Year, z.Month, z.Day) + "T" + TimePart(z.Hour, z.Minute, z.Second, z.Nanosecond);

    /// <summary>
    /// Renders the <c>zone</c> field of a <see cref="ZonedDateTime"/>: the named zone id when the zone is a
    /// <see cref="ZoneId"/>, otherwise the fixed offset as <c>±HH:MM</c> when it is a <see cref="ZoneOffset"/>.
    /// </summary>
    /// <exception cref="CypherResultException">The zone is an unrecognized <see cref="Zone"/> subtype.</exception>
    public static string Zone(ZonedDateTime z)
    {
        switch (z.Zone)
        {
            case ZoneId zoneId:
                // A named zone (e.g. "Europe/Brussels") — the lossless, full-fidelity identity.
                return zoneId.Id;
            case ZoneOffset zoneOffset:
                // A fixed offset with no named zone — rendered as ±HH:MM.
                return FormatOffset(zoneOffset.OffsetSeconds);
            default:
                throw new CypherResultException(
                    $"Cannot serialize ZonedDateTime: unrecognized Zone type '{z.Zone?.GetType().FullName ?? "null"}'.");
        }
    }

    private static string DatePart(int year, int month, int day)
        => Pad(year, 4) + "-" + Pad(month, 2) + "-" + Pad(day, 2);

    private static string TimePart(int hour, int minute, int second, int nanosecond)
        => Pad(hour, 2) + ":" + Pad(minute, 2) + ":" + Pad(second, 2) + Fraction(nanosecond);

    /// <summary>
    /// The fractional-second suffix (including the leading <c>.</c>) for <paramref name="nanosecond"/>
    /// (0..999,999,999), or the empty string when it is zero. Trailing zeros are trimmed.
    /// </summary>
    /// <exception cref="CypherResultException">
    /// <paramref name="nanosecond"/> is not a multiple of 100 (genuine sub-100-ns precision), which the
    /// pinned ≤7-digit result format cannot represent.
    /// </exception>
    private static string Fraction(int nanosecond)
    {
        if (nanosecond == 0)
            return string.Empty;

        if (nanosecond % 100 != 0)
            throw new CypherResultException(
                $"Cannot serialize temporal value: nanosecond component {nanosecond} carries sub-100-nanosecond " +
                "precision that the pinned 7-digit (100 ns) ISO result format cannot represent without silent loss.");

        // 100-ns ticks within the second (1..9,999,999): a 7-digit fixed field with trailing zeros trimmed.
        int ticks = nanosecond / 100;
        string digits = ticks.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        return "." + digits;
    }

    /// <summary>Formats a fixed UTC offset (in seconds) as <c>±HH:MM</c>; zero renders as <c>+00:00</c>.</summary>
    private static string FormatOffset(int offsetSeconds)
    {
        char sign = offsetSeconds < 0 ? '-' : '+';
        int abs = Math.Abs(offsetSeconds);
        int hours = abs / 3600;
        int minutes = (abs % 3600) / 60;

        var sb = new StringBuilder(6);
        sb.Append(sign).Append(Pad(hours, 2)).Append(':').Append(Pad(minutes, 2));

        // A sub-minute offset (e.g. a historical LMT) cannot fit the pinned ±HH:MM form; fail loud rather
        // than silently drop the residual seconds. No modern fixed offset has one.
        int residualSeconds = abs % 60;
        if (residualSeconds != 0)
            throw new CypherResultException(
                $"Cannot serialize ZonedDateTime: fixed offset of {offsetSeconds} seconds is not a whole number " +
                "of minutes and cannot be represented in the pinned ±HH:MM form without silent loss.");

        return sb.ToString();
    }

    /// <summary>Zero-pads a non-negative integer to <paramref name="width"/> digits, invariant-culture.</summary>
    private static string Pad(int value, int width)
        => value.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
