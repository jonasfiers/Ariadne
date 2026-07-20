using System;
using Neo4j.Driver;

namespace Ariadne.Core.Results;

/// <summary>
/// Projects a driver <see cref="IResultSummary"/> into the static, typed <see cref="CypherSummary"/>
/// (result spec §2). A <b>pure projection</b>: it reads an already-obtained summary and does not run a
/// query, open a session, or touch the network.
/// </summary>
/// <remarks>
/// <para>
/// Verified against the real Neo4j.Driver 5.28.3 API by reflection: the counters come from
/// <see cref="IResultSummary.Counters"/> (an <see cref="ICounters"/> whose fields are <see cref="int"/>,
/// widened here to <see cref="long"/> to match the OutSystems Long Integer contract); timings come from
/// <see cref="IResultSummary.ResultAvailableAfter"/> / <see cref="IResultSummary.ResultConsumedAfter"/>
/// (each a <see cref="TimeSpan"/>); the classification from <see cref="IResultSummary.QueryType"/>; and the
/// database name from <see cref="IResultSummary.Database"/>'s <c>Name</c>.
/// </para>
/// <para>
/// <b>Timing "unavailable" sentinel.</b> The driver reports an unavailable timing as
/// <c>TimeSpan.FromMilliseconds(-1)</c>. It is converted with <c>(long)TimeSpan.TotalMilliseconds</c> and
/// therefore <b>passes through as <c>-1</c></b> — a documented sentinel the caller can detect, never
/// silently collapsed to <c>0</c> (which would be indistinguishable from a genuine sub-millisecond timing).
/// </para>
/// <para>
/// <b>Fail loud.</b> A <see langword="null"/> summary throws <see cref="ArgumentNullException"/>. The
/// driver's <see cref="QueryType.Unknown"/> classification and any undefined enum value have no defined
/// short code, so they throw <see cref="CypherResultException"/> rather than emit a blank or guessed code.
/// </para>
/// </remarks>
public static class CypherSummaryMapper
{
    /// <summary>
    /// Maps a driver <see cref="IResultSummary"/> to a typed <see cref="CypherSummary"/>.
    /// </summary>
    /// <param name="summary">The already-obtained driver summary. Must not be <see langword="null"/>.</param>
    /// <returns>The projected, statically-typed summary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="summary"/> (or its <c>Counters</c>) is null.</exception>
    /// <exception cref="CypherResultException">
    /// The summary's <see cref="QueryType"/> is <see cref="QueryType.Unknown"/> or an unrecognized value —
    /// there is no short code to emit, so the mapper fails loud rather than guess.
    /// </exception>
    public static CypherSummary Map(IResultSummary summary)
    {
        if (summary is null)
        {
            throw new ArgumentNullException(nameof(summary));
        }

        ICounters counters = summary.Counters
            ?? throw new ArgumentNullException(nameof(summary), "IResultSummary.Counters was null.");

        return new CypherSummary
        {
            NodesCreated = counters.NodesCreated,
            NodesDeleted = counters.NodesDeleted,
            RelationshipsCreated = counters.RelationshipsCreated,
            RelationshipsDeleted = counters.RelationshipsDeleted,
            PropertiesSet = counters.PropertiesSet,
            LabelsAdded = counters.LabelsAdded,
            LabelsRemoved = counters.LabelsRemoved,
            IndexesAdded = counters.IndexesAdded,
            IndexesRemoved = counters.IndexesRemoved,
            ConstraintsAdded = counters.ConstraintsAdded,
            ConstraintsRemoved = counters.ConstraintsRemoved,
            SystemUpdates = counters.SystemUpdates,
            ContainsUpdates = counters.ContainsUpdates,
            ContainsSystemUpdates = counters.ContainsSystemUpdates,

            // TimeSpan -> whole milliseconds. The driver's -1 ms "unavailable" sentinel passes through
            // unchanged (see the type-level remarks) — never silently zeroed.
            ResultAvailableAfterMs = (long)summary.ResultAvailableAfter.TotalMilliseconds,
            ResultConsumedAfterMs = (long)summary.ResultConsumedAfter.TotalMilliseconds,

            QueryType = ToShortCode(summary.QueryType),

            // Database info is absent on older servers; pass the name through as reported (may be null).
            Database = summary.Database?.Name,
        };
    }

    /// <summary>
    /// Maps the driver's <see cref="QueryType"/> enum to the spec §2 short code. The known productive set
    /// maps directly; <see cref="QueryType.Unknown"/> and any undefined value fail loud.
    /// </summary>
    private static string ToShortCode(QueryType queryType) => queryType switch
    {
        QueryType.ReadOnly => "r",
        QueryType.ReadWrite => "rw",
        QueryType.WriteOnly => "w",
        QueryType.SchemaWrite => "s",
        _ => throw new CypherResultException(
            $"Cypher result summary has an unmappable QueryType '{queryType}' (numeric {(int)queryType}): " +
            "there is no defined short code for it, so the mapper fails loud rather than emit a blank or " +
            "guessed classification."),
    };
}
