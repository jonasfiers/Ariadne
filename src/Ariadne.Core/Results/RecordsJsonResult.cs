using System.Collections.Generic;

namespace Ariadne.Core.Results;

/// <summary>
/// The two dynamic outputs of the result envelope (result spec §2), produced together in one pass by
/// <see cref="RecordsJsonBuilder.Build(IEnumerable{Neo4j.Driver.IRecord})"/>: the records serialized as a JSON
/// array (<see cref="Json"/>) and the ordered column names (<see cref="Columns"/>). The third output —
/// <c>Summary</c> (the typed counters) — is a separate feature and deliberately not part of this type.
/// </summary>
/// <remarks>
/// A value type: it merely pairs the already-built JSON string with the column list. <see cref="Columns"/> is
/// the first record's <see cref="Neo4j.Driver.IRecord.Keys"/> (or empty for an empty result); at execution time
/// a real result's columns come from the cursor even when no records are returned — that wiring is a later
/// feature.
/// </remarks>
public readonly struct RecordsJsonResult
{
    /// <summary>Creates the result pairing.</summary>
    /// <param name="json">The records serialized as a JSON array of per-record objects.</param>
    /// <param name="columns">The ordered column names.</param>
    public RecordsJsonResult(string json, IReadOnlyList<string> columns)
    {
        Json = json;
        Columns = columns;
    }

    /// <summary>The records as a JSON <b>array</b> of per-record objects (<c>[ { "col": value, ... }, ... ]</c>).</summary>
    public string Json { get; }

    /// <summary>The result's column names in order (the first record's <c>Keys</c>; empty for an empty result).</summary>
    public IReadOnlyList<string> Columns { get; }
}
