using System;

namespace Ariadne.Core.Parameters;

/// <summary>
/// One caller-supplied, explicitly-typed query parameter (<c>$name</c>) bound into a Cypher
/// statement. This is a plain-C# mirror of the OutSystems <c>CypherParameter</c> structure:
/// a tagged union where <see cref="Type"/> selects which single value carrier is read.
/// </summary>
/// <remarks>
/// The caller always knows a parameter's type when they build it, so the fat-struct-with-one-carrier
/// shape is deliberate and correct for static, known-typed input. Only the <b>scalar</b> subset is
/// mapped by <see cref="CypherParameterMapper.BuildParameters"/> in this feature; composite
/// (<c>List</c>/<c>Map</c>/<c>Json</c>) and deferred (<c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>)
/// tags exist in the spec but currently fail loud.
/// </remarks>
public sealed class CypherParameter
{
    /// <summary>The Cypher identifier this parameter binds to, without the leading <c>$</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The type tag selecting the mapping and the value carrier (case-insensitive). One of the tags
    /// in the parameter spec, e.g. <c>String</c>, <c>Integer</c>, <c>Float</c>, <c>Boolean</c>,
    /// <c>Date</c>, <c>Time</c>, <c>DateTime</c>, <c>ZonedDateTime</c>, <c>Bytes</c>, <c>Null</c>.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Value carrier read when <see cref="Type"/> is <c>String</c>.</summary>
    public string? StringValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Integer</c> (Neo4j Integer is Int64).</summary>
    public long? IntegerValue { get; set; }

    /// <summary>
    /// Value carrier read when <see cref="Type"/> is <c>Float</c>. Carried as <see cref="decimal"/>
    /// (OutSystems Decimal); mapped to <see cref="double"/> — lossy past ~15–17 significant digits,
    /// because Neo4j has no decimal type. See the parameter spec's Decimal section.
    /// </summary>
    public decimal? FloatValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Boolean</c>.</summary>
    public bool? BooleanValue { get; set; }

    /// <summary>
    /// Value carrier read for the temporal tags (<c>Date</c>, <c>Time</c>, <c>DateTime</c>,
    /// <c>ZonedDateTime</c>). Only the relevant part is used (date part for <c>Date</c>, time part
    /// for <c>Time</c>). Its 100-ns tick precision is carried into the driver temporal's nanoseconds.
    /// </summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Bytes</c>.</summary>
    public byte[]? BytesValue { get; set; }

    /// <summary>
    /// IANA zone id (e.g. <c>Europe/Brussels</c>), read only for <c>ZonedDateTime</c>. Preferred over
    /// <see cref="OffsetMinutes"/> when both are set. A zone is never fabricated — the caller supplies it.
    /// </summary>
    public string? ZoneId { get; set; }

    /// <summary>
    /// Fixed offset from UTC in minutes (e.g. <c>+120</c>), read only for <c>ZonedDateTime</c> as the
    /// fallback when <see cref="ZoneId"/> is not set. Converted to offset-seconds for the driver.
    /// </summary>
    public int? OffsetMinutes { get; set; }
}
