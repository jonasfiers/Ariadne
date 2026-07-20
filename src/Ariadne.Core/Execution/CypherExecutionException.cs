using System;

namespace Ariadne.Core.Execution;

/// <summary>
/// Whose fault an execution error is (connection spec §9): a <see cref="Developer"/> error is caused by the
/// caller's query or data (a syntax error, a constraint violation) and is surfaced <b>verbatim</b> so they
/// can fix it; an <see cref="Operational"/> error is an environment/runtime condition (server unreachable,
/// authentication rejected, transient failure that outlived its retries) surfaced with a <b>friendly</b>
/// message that never leaks a credential.
/// </summary>
public enum ExecutionErrorClassification
{
    /// <summary>The caller's query or data is at fault; the Neo4j code + message are surfaced verbatim.</summary>
    Developer,

    /// <summary>An environment/runtime condition is at fault; a friendly, credential-free message is surfaced.</summary>
    Operational,
}

/// <summary>
/// The single, named failure signal for the execution layer — the mirror of the other layers' named
/// exceptions (<c>CypherParameterException</c>, <c>CypherResultException</c>, <c>ConnectionException</c>).
/// Every driver <see cref="Neo4j.Driver.Neo4jException"/> that reaches the execution boundary is mapped to
/// this exception (connection spec §9): named and specific — never a bare re-throw of the driver type, never
/// a silently swallowed error.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail loud, and tell the truth about whose fault it is.</b> <see cref="Classification"/> records whether
/// the error is a <see cref="ExecutionErrorClassification.Developer"/> or an
/// <see cref="ExecutionErrorClassification.Operational"/> one. A developer error (a
/// <see cref="Neo4j.Driver.ClientException"/> — bad Cypher, constraint violation) carries the Neo4j code and
/// message verbatim, because it is the caller's to fix. An operational error carries a friendly message and
/// <b>never a credential</b>: the mapped message is built only from non-secret facts (the target URI, the
/// Neo4j status code), never from <see cref="Connection.ConnConfig.Password"/> or
/// <see cref="Connection.ConnConfig.BearerToken"/>.
/// </para>
/// <para>
/// The originating driver exception is preserved as <see cref="Exception.InnerException"/> for diagnostics.
/// Driver exception messages do not contain credentials, so preserving the cause does not leak one.
/// </para>
/// </remarks>
public sealed class CypherExecutionException : Exception
{
    /// <summary>Creates the exception with its classification and (where the driver supplied one) the Neo4j code.</summary>
    /// <param name="message">
    /// The surfaced message — verbatim Neo4j text for a developer error, a friendly credential-free message for
    /// an operational one. Must never contain a credential value.
    /// </param>
    /// <param name="classification">Whether this is a developer or an operational error.</param>
    /// <param name="neo4jCode">The Neo4j status code (e.g. <c>Neo.ClientError.Statement.SyntaxError</c>), or null.</param>
    /// <param name="innerException">The originating driver exception, preserved for diagnostics.</param>
    public CypherExecutionException(
        string message,
        ExecutionErrorClassification classification,
        string? neo4jCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Classification = classification;
        Neo4jCode = neo4jCode;
    }

    /// <summary>Whether the error is the caller's fault (developer) or the environment's (operational).</summary>
    public ExecutionErrorClassification Classification { get; }

    /// <summary>Convenience: <see langword="true"/> when <see cref="Classification"/> is developer.</summary>
    public bool IsDeveloperError => Classification == ExecutionErrorClassification.Developer;

    /// <summary>
    /// The Neo4j status code the driver reported (e.g. <c>Neo.ClientError.Statement.SyntaxError</c>), or
    /// <see langword="null"/> when the mapped exception type carries no code.
    /// </summary>
    public string? Neo4jCode { get; }

    /// <summary>Builds a developer-classified execution error (the caller's query/data is at fault).</summary>
    internal static CypherExecutionException Developer(string message, string? code, Exception inner) =>
        new CypherExecutionException(message, ExecutionErrorClassification.Developer, code, inner);

    /// <summary>Builds an operational-classified execution error (an environment/runtime condition).</summary>
    internal static CypherExecutionException Operational(string message, string? code, Exception inner) =>
        new CypherExecutionException(message, ExecutionErrorClassification.Operational, code, inner);
}
