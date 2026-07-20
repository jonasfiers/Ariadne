using System;

namespace Ariadne.Core.Connection;

/// <summary>
/// The single, named failure signal for the connection layer — the mirror of
/// <c>CypherParameterException</c> (parameters) and <c>CypherResultException</c> (results). Thrown when a
/// connection cannot be constructed from its configuration: an unknown or out-of-scope authentication
/// scheme (anything other than <c>Basic</c>/<c>Bearer</c>/<c>None</c> — Kerberos and Custom are deliberately
/// not supported in v1).
/// </summary>
/// <remarks>
/// The design philosophy (inherited from PICASSO) is <em>fail loud, never silently miscompute</em>: an
/// unsupported scheme throws this exception with a message that names the offending value, rather than
/// falling back to a guessed default (which could silently authenticate — or fail to — in a way the caller
/// never asked for). Credentials are <b>never</b> echoed into the message.
/// </remarks>
public sealed class ConnectionException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    /// <param name="message">Human-readable detail; must not contain any credential value.</param>
    public ConnectionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an explanatory message and an inner cause.</summary>
    /// <param name="message">Human-readable detail; must not contain any credential value.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
