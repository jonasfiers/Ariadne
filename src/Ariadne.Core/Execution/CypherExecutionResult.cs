using System.Collections.Generic;
using Ariadne.Core.Results;

namespace Ariadne.Core.Execution;

/// <summary>
/// The three outputs of running a Cypher statement, produced together by
/// <see cref="CypherExecutor"/>: the records serialized as the canonical <c>RecordsJson</c> envelope
/// (<see cref="RecordsJson"/>), the result's ordered column names (<see cref="Columns"/>), and the typed
/// execution <see cref="Summary"/> (counters / timings / classification). This is the plain-C# mirror of the
/// values the OutSystems <c>RunCypher…</c> actions return to the caller.
/// </summary>
/// <remarks>
/// <para>
/// An immutable value object: it merely pairs the already-built JSON, the columns, and the summary.
/// <see cref="Columns"/> comes from the cursor's <c>Keys</c> (not merely from the returned records), so a
/// query that returns <b>no rows</b> still reports its projected column names — never an empty column list
/// for a non-empty projection.
/// </para>
/// <para>
/// The pieces are produced by the composed Feature 06/07 builders (<see cref="RecordsJsonBuilder"/>,
/// <see cref="CypherSummaryMapper"/>) unchanged, so their serialization and fail-loud rules are inherited
/// exactly.
/// </para>
/// </remarks>
public sealed class CypherExecutionResult
{
    /// <summary>Creates the result triple.</summary>
    /// <param name="recordsJson">The records serialized as a JSON array of per-record objects.</param>
    /// <param name="columns">The result's ordered column names (the cursor's <c>Keys</c>).</param>
    /// <param name="summary">The typed execution summary.</param>
    public CypherExecutionResult(string recordsJson, IReadOnlyList<string> columns, CypherSummary summary)
    {
        RecordsJson = recordsJson;
        Columns = columns;
        Summary = summary;
    }

    /// <summary>The records as a JSON <b>array</b> of per-record objects (<c>[ { "col": value, … }, … ]</c>).</summary>
    public string RecordsJson { get; }

    /// <summary>
    /// The result's column names in order — taken from the cursor's <c>Keys</c>, so they are present even for
    /// an empty (zero-row) result.
    /// </summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The typed execution summary: write counters, timings, query classification, and database.</summary>
    public CypherSummary Summary { get; }
}
