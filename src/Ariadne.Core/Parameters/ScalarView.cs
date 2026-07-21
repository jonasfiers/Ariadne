using System;

namespace Ariadne.Core.Parameters;

/// <summary>
/// A read-only view over the scalar carrier properties shared by <see cref="CypherParameter"/>,
/// <see cref="CypherListElement"/> and <see cref="CypherMapEntry"/>, so the mapper can read any of
/// them through one shape.
/// </summary>
/// <remarks>
/// <para>
/// This replaces what used to be an <c>internal interface IScalarCarrier</c> implemented by the three
/// public model classes. That arrangement broke the Integration Studio import: a public type that
/// implements an <em>internal</em> interface still advertises it through reflection, and Integration
/// Studio's .NET import wizard silently drops any type whose interfaces it cannot access — and with
/// it, every action whose signature mentions that type. All three <c>RunCypher*</c> actions vanished
/// from the wizard while <c>VerifyConnectivity</c> and <c>ResetDriver</c> (which only touch
/// <see cref="Connection.ConnConfig"/>, implementing nothing) imported fine.
/// </para>
/// <para>
/// So the rule for anything on the OutSystems-facing surface: <b>public model classes implement no
/// interfaces and inherit nothing.</b> They are plain property bags mirroring OutSystems Structures,
/// which cannot inherit either. Shared behaviour lives here instead, in an internal type that never
/// appears in a public signature.
/// </para>
/// </remarks>
internal readonly struct ScalarView
{
    private ScalarView(
        string? stringValue,
        long? integerValue,
        decimal? floatValue,
        bool? booleanValue,
        DateTime? dateTimeValue,
        byte[]? bytesValue,
        string? zoneId,
        int? offsetMinutes)
    {
        StringValue = stringValue;
        IntegerValue = integerValue;
        FloatValue = floatValue;
        BooleanValue = booleanValue;
        DateTimeValue = dateTimeValue;
        BytesValue = bytesValue;
        ZoneId = zoneId;
        OffsetMinutes = offsetMinutes;
    }

    /// <summary>Carrier read for the <c>String</c> tag.</summary>
    public string? StringValue { get; }

    /// <summary>Carrier read for the <c>Integer</c> tag.</summary>
    public long? IntegerValue { get; }

    /// <summary>Carrier read for the <c>Float</c> tag.</summary>
    public decimal? FloatValue { get; }

    /// <summary>Carrier read for the <c>Boolean</c> tag.</summary>
    public bool? BooleanValue { get; }

    /// <summary>Carrier read for every temporal tag.</summary>
    public DateTime? DateTimeValue { get; }

    /// <summary>Carrier read for the <c>Bytes</c> tag.</summary>
    public byte[]? BytesValue { get; }

    /// <summary>Zone identifier for the <c>ZonedDateTime</c> tag.</summary>
    public string? ZoneId { get; }

    /// <summary>Offset in minutes for zoned/offset temporal tags.</summary>
    public int? OffsetMinutes { get; }

    public static ScalarView Of(CypherParameter p) => new(
        p.StringValue, p.IntegerValue, p.FloatValue, p.BooleanValue,
        p.DateTimeValue, p.BytesValue, p.ZoneId, p.OffsetMinutes);

    public static ScalarView Of(CypherListElement e) => new(
        e.StringValue, e.IntegerValue, e.FloatValue, e.BooleanValue,
        e.DateTimeValue, e.BytesValue, e.ZoneId, e.OffsetMinutes);

    public static ScalarView Of(CypherMapEntry m) => new(
        m.StringValue, m.IntegerValue, m.FloatValue, m.BooleanValue,
        m.DateTimeValue, m.BytesValue, m.ZoneId, m.OffsetMinutes);
}
