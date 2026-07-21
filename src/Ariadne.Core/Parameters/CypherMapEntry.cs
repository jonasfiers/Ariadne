using System;

namespace Ariadne.Core.Parameters;

/// <summary>
/// One scalar entry (key + value) of a flat Cypher <c>Map</c> parameter (e.g. a property in
/// <c>SET n += $props</c>). A plain-C# mirror of the OutSystems <c>CypherMapEntry</c> structure: a
/// tagged union like <see cref="CypherParameter"/> but keyed by <see cref="Key"/> and — deliberately —
/// with <b>no recursion</b>.
/// </summary>
/// <remarks>
/// <see cref="Key"/> must be non-empty; keys are compared <b>case-sensitively</b> (<c>A</c> and <c>a</c>
/// are distinct, matching the parameter-name rule). The entry's <see cref="Type"/> must be a <b>scalar</b>
/// tag — a composite (<c>List</c>/<c>Map</c>/<c>Json</c>) or deferred (<c>Duration</c>/<c>Point</c>/<c>OffsetTime</c>)
/// tag fails loud: flat maps cannot nest — nested structures use the <c>Json</c> parameter (see Decision B).
/// </remarks>
public sealed class CypherMapEntry
{
    /// <summary>The map key. Must be non-empty/non-whitespace and unique within the map (case-sensitive).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The scalar type tag selecting the value carrier (case-insensitive). Composite/deferred tags throw.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Value carrier read when <see cref="Type"/> is <c>String</c>.</summary>
    public string? StringValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Integer</c> (Neo4j Integer is Int64).</summary>
    public long? IntegerValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Float</c> (Decimal, mapped lossily to double).</summary>
    public decimal? FloatValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Boolean</c>.</summary>
    public bool? BooleanValue { get; set; }

    /// <summary>Value carrier read for the temporal tags (<c>Date</c>/<c>Time</c>/<c>DateTime</c>/<c>ZonedDateTime</c>).</summary>
    public DateTime? DateTimeValue { get; set; }

    /// <summary>Value carrier read when <see cref="Type"/> is <c>Bytes</c>.</summary>
    public byte[]? BytesValue { get; set; }

    /// <summary>IANA zone id read for <c>ZonedDateTime</c> (preferred over <see cref="OffsetMinutes"/>); never fabricated.</summary>
    public string? ZoneId { get; set; }

    /// <summary>Fixed UTC offset in minutes read for <c>ZonedDateTime</c> as the fallback when no <see cref="ZoneId"/> is set.</summary>
    public int? OffsetMinutes { get; set; }
}
