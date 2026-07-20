using System;

namespace Ariadne.Core.Parameters;

/// <summary>
/// Thrown when a <see cref="CypherParameter"/> cannot be mapped to a Neo4j driver value:
/// an unknown, deferred, or composite <see cref="CypherParameter.Type"/> tag; an invalid
/// or duplicate <see cref="CypherParameter.Name"/>; a missing required value carrier; or a
/// <c>ZonedDateTime</c> that supplies neither a zone id nor an offset.
/// </summary>
/// <remarks>
/// This is the single, named failure signal for the parameter layer. The design philosophy
/// (inherited from PICASSO) is <em>fail loud, never silently miscompute</em>: every unmappable
/// input throws this exception with a message that names the offending parameter, rather than
/// being skipped or guessed.
/// </remarks>
public sealed class CypherParameterException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    /// <param name="message">Human-readable detail; should name the offending parameter.</param>
    public CypherParameterException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an explanatory message and an inner cause.</summary>
    /// <param name="message">Human-readable detail; should name the offending parameter.</param>
    /// <param name="innerException">The underlying cause (e.g. an invalid zone id from the driver).</param>
    public CypherParameterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
