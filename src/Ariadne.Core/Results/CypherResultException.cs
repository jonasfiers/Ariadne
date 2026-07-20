using System;

namespace Ariadne.Core.Results;

/// <summary>
/// Thrown when a Cypher result value cannot be serialized to the canonical result JSON: a driver
/// value whose runtime type is not in the supported leaf set (a deferred <c>Duration</c>/<c>Point</c>/
/// <c>OffsetTime</c>, a composite <c>Node</c>/<c>Relationship</c>/<c>Path</c>/list/map — Feature 05 — or
/// any unknown type), a non-finite <see cref="double"/> (<c>NaN</c>/±∞, which JSON cannot represent), or
/// a temporal carrying sub-100-nanosecond precision the pinned ≤7-digit result format cannot hold.
/// </summary>
/// <remarks>
/// This is the single, named failure signal for the result layer — the mirror of
/// <c>CypherParameterException</c> on the parameter side. The design philosophy (inherited from PICASSO)
/// is <em>fail loud, never silently miscompute</em>: an unserializable value throws this exception with a
/// message that names the offending runtime type, rather than being emitted as a placeholder or a guessed
/// serialization. Feature 05 will replace the composite/graph throws with real handling.
/// </remarks>
public sealed class CypherResultException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    /// <param name="message">Human-readable detail; should name the offending runtime type.</param>
    public CypherResultException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an explanatory message and an inner cause.</summary>
    /// <param name="message">Human-readable detail; should name the offending runtime type.</param>
    /// <param name="innerException">The underlying cause.</param>
    public CypherResultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
