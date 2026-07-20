namespace Ariadne.Core.Results;

/// <summary>
/// The <b>static</b>, already-typed third output of the result envelope (result spec §2): the write
/// counters, execution timings, query classification, and target database of a run Cypher statement.
/// Unlike the records (dynamic → JSON), the summary shape is known and fixed, so it is returned as a
/// plain typed structure with no JSON step. This is a plain-C# mirror of the OutSystems
/// <c>CypherSummary</c> structure.
/// </summary>
/// <remarks>
/// Produced by <see cref="CypherSummaryMapper.Map(Neo4j.Driver.IResultSummary)"/> as a pure projection over
/// the driver's <see cref="Neo4j.Driver.IResultSummary"/> — it does not run a query. The counter fields are
/// widened to <see cref="long"/> to match the OutSystems <b>Long Integer</b> attribute type of §2 (the driver
/// exposes them as <see cref="int"/> on <see cref="Neo4j.Driver.ICounters"/>; the widening is lossless). All
/// properties are settable so the mapper (and the OutSystems marshalling boundary) can populate them by
/// object initializer, matching the other POCOs in this project.
/// </remarks>
public sealed class CypherSummary
{
    /// <summary>Number of nodes created by the statement.</summary>
    public long NodesCreated { get; set; }

    /// <summary>Number of nodes deleted by the statement.</summary>
    public long NodesDeleted { get; set; }

    /// <summary>Number of relationships created by the statement.</summary>
    public long RelationshipsCreated { get; set; }

    /// <summary>Number of relationships deleted by the statement.</summary>
    public long RelationshipsDeleted { get; set; }

    /// <summary>Number of properties set by the statement.</summary>
    public long PropertiesSet { get; set; }

    /// <summary>Number of labels added to nodes by the statement.</summary>
    public long LabelsAdded { get; set; }

    /// <summary>Number of labels removed from nodes by the statement.</summary>
    public long LabelsRemoved { get; set; }

    /// <summary>Number of indexes added by the statement.</summary>
    public long IndexesAdded { get; set; }

    /// <summary>Number of indexes removed by the statement.</summary>
    public long IndexesRemoved { get; set; }

    /// <summary>Number of constraints added by the statement.</summary>
    public long ConstraintsAdded { get; set; }

    /// <summary>Number of constraints removed by the statement.</summary>
    public long ConstraintsRemoved { get; set; }

    /// <summary>Number of system (administrative/database-management) updates performed by the statement.</summary>
    public long SystemUpdates { get; set; }

    /// <summary>Whether the statement made any data (node/relationship/property/label/index/constraint) updates.</summary>
    public bool ContainsUpdates { get; set; }

    /// <summary>Whether the statement made any system (administrative) updates.</summary>
    public bool ContainsSystemUpdates { get; set; }

    /// <summary>
    /// Time in milliseconds from statement send until the result was available to begin streaming
    /// (the driver's <see cref="Neo4j.Driver.IResultSummary.ResultAvailableAfter"/> as whole milliseconds).
    /// A driver "not available" sentinel of <c>-1</c> ms is passed through unchanged (never silently zeroed);
    /// see <see cref="CypherSummaryMapper"/>.
    /// </summary>
    public long ResultAvailableAfterMs { get; set; }

    /// <summary>
    /// Time in milliseconds from the result becoming available until it was fully consumed
    /// (the driver's <see cref="Neo4j.Driver.IResultSummary.ResultConsumedAfter"/> as whole milliseconds).
    /// A driver "not available" sentinel of <c>-1</c> ms is passed through unchanged (never silently zeroed).
    /// </summary>
    public long ResultConsumedAfterMs { get; set; }

    /// <summary>
    /// The query classification as a short code: <c>"r"</c> (read-only), <c>"rw"</c> (read-write),
    /// <c>"w"</c> (write-only), or <c>"s"</c> (schema-write). The mapper throws rather than emit a blank or
    /// guessed code for the driver's <c>Unknown</c> classification or any unrecognized enum value.
    /// </summary>
    public string QueryType { get; set; } = string.Empty;

    /// <summary>
    /// The name of the database the statement ran against, or <see langword="null"/> when the driver did not
    /// report one (older servers / no database info). Passed through as reported — never fabricated.
    /// </summary>
    public string? Database { get; set; }
}
