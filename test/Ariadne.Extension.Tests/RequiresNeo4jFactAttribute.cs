using System;
using Xunit;

namespace Ariadne.Extension.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when a live Neo4j is configured via the
/// <c>NEO4J_TEST_URI</c> / <c>NEO4J_TEST_USER</c> / <c>NEO4J_TEST_PASSWORD</c> environment variables, and
/// otherwise reports the test as <b>skipped</b> (never failed) — the same env-gating the Core integration
/// tests use, kept credential-free (details live only in env vars, never in source).
/// </summary>
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

    public static bool IsConfigured => Uri is not null && User is not null && Password is not null;

    private static string? Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
