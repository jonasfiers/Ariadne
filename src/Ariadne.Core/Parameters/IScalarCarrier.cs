using System;

namespace Ariadne.Core.Parameters;

/// <summary>
/// The set of scalar value carriers shared by a top-level <see cref="CypherParameter"/>, a
/// <see cref="CypherListElement"/>, and a <see cref="CypherMapEntry"/>. It exists solely so the
/// mapper has <b>one</b> scalar-mapping path: the same <c>Type</c>-tag switch reads carriers through
/// this shape whether it is mapping a top-level scalar or an element of a flat <c>List</c>/<c>Map</c>.
/// </summary>
/// <remarks>
/// Internal by design — it is an implementation detail of the mapper, not part of the OutSystems-facing
/// surface. The three public model classes mirror distinct OutSystems structures (which do not inherit),
/// so each declares its own carrier properties; this interface unifies only the <em>reading</em> of them.
/// </remarks>
internal interface IScalarCarrier
{
    /// <summary>Carrier read for the <c>String</c> tag.</summary>
    string? StringValue { get; }

    /// <summary>Carrier read for the <c>Integer</c> tag (Neo4j Integer is Int64).</summary>
    long? IntegerValue { get; }

    /// <summary>Carrier read for the <c>Float</c> tag (OutSystems Decimal, mapped lossily to double).</summary>
    decimal? FloatValue { get; }

    /// <summary>Carrier read for the <c>Boolean</c> tag.</summary>
    bool? BooleanValue { get; }

    /// <summary>Carrier read for the temporal tags (<c>Date</c>/<c>Time</c>/<c>DateTime</c>/<c>ZonedDateTime</c>).</summary>
    DateTime? DateTimeValue { get; }

    /// <summary>Carrier read for the <c>Bytes</c> tag.</summary>
    byte[]? BytesValue { get; }

    /// <summary>IANA zone id read for the <c>ZonedDateTime</c> tag (preferred over <see cref="OffsetMinutes"/>).</summary>
    string? ZoneId { get; }

    /// <summary>Fixed UTC offset in minutes read for the <c>ZonedDateTime</c> tag when no <see cref="ZoneId"/> is set.</summary>
    int? OffsetMinutes { get; }
}
