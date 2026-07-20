using System;
using Xunit;
using Xunit.Sdk;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when a live Neo4j is configured via the
/// <c>NEO4J_TEST_URI</c> / <c>NEO4J_TEST_USER</c> / <c>NEO4J_TEST_PASSWORD</c> environment variables, and
/// otherwise reports the test as <b>skipped</b> (never failed). This is the idiomatic xunit v2 way to skip
/// conditionally at runtime: xunit 2.9.3's <c>Assert</c> has no <c>Skip</c>/<c>SkipUnless</c> API (that is a
/// v3 addition), so a discovery-time <see cref="FactAttribute.Skip"/> is set instead — no new package.
/// </summary>
/// <remarks>
/// The environment is read when xunit constructs the attribute during test discovery. Because the tests are
/// run by exporting the variables inline for the <c>dotnet test</c> invocation, discovery happens in that same
/// process: with the variables set the facts execute; without them every fact is skipped with the reason
/// below. Keeps the public repo credential-free (details live only in env vars, never in source).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresNeo4jFactAttribute : FactAttribute
{
    private const string SkipReason =
        "Live Neo4j not configured — set NEO4J_TEST_URI / NEO4J_TEST_USER / NEO4J_TEST_PASSWORD to run.";

    public RequiresNeo4jFactAttribute()
    {
        if (!Neo4jTestEnvironment.IsConfigured)
        {
            Skip = SkipReason;
        }
    }
}

/// <summary>Reads the env-var connection for the live oracle, treating whitespace as unset.</summary>
internal static class Neo4jTestEnvironment
{
    public static string? Uri => Env("NEO4J_TEST_URI");
    public static string? User => Env("NEO4J_TEST_USER");
    public static string? Password => Env("NEO4J_TEST_PASSWORD");
    public static string? Database => Env("NEO4J_TEST_DATABASE");

    /// <summary>True when the URI, user, and password are all present.</summary>
    public static bool IsConfigured => Uri is not null && User is not null && Password is not null;

    private static string? Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
