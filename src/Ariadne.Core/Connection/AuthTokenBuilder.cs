using System;
using Neo4j.Driver;

namespace Ariadne.Core.Connection;

/// <summary>
/// Builds a driver <see cref="IAuthToken"/> from a <see cref="ConnConfig"/> (connection spec §5). A pure,
/// side-effect-free projection of the scheme + credentials onto the driver's <see cref="AuthTokens"/>
/// factory. No network, no driver construction.
/// </summary>
/// <remarks>
/// Verified against the real Neo4j.Driver 5.28.3 API: <see cref="AuthTokens.Basic(string, string)"/> and
/// <see cref="AuthTokens.Bearer(string)"/> are static methods; <see cref="AuthTokens.None"/> is a static
/// <b>property</b> (not a method). Kerberos/Custom exist on <see cref="AuthTokens"/> but are deliberately
/// out of scope for v1 and fail loud here.
/// </remarks>
public static class AuthTokenBuilder
{
    /// <summary>The scheme value selecting HTTP-Basic (username + password) authentication.</summary>
    public const string BasicScheme = "Basic";

    /// <summary>The scheme value selecting bearer-token (SSO/OIDC) authentication.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>The scheme value selecting no authentication (dev/local only — discouraged).</summary>
    public const string NoneScheme = "None";

    /// <summary>
    /// Maps <paramref name="config"/>'s <see cref="ConnConfig.AuthScheme"/> (case-insensitive) to the
    /// matching driver <see cref="IAuthToken"/>.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// <see cref="AuthTokens.Basic(string, string)"/> for <c>Basic</c>, <see cref="AuthTokens.Bearer(string)"/>
    /// for <c>Bearer</c>, or <see cref="AuthTokens.None"/> for <c>None</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ConnectionException">
    /// The scheme is unknown, empty, or an out-of-scope value (Kerberos/Custom). The message names the
    /// offending scheme but never any credential.
    /// </exception>
    public static IAuthToken BuildAuthToken(ConnConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        string scheme = config.AuthScheme?.Trim() ?? string.Empty;

        if (scheme.Equals(BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthTokens.Basic(config.Username, config.Password);
        }

        if (scheme.Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthTokens.Bearer(config.BearerToken);
        }

        if (scheme.Equals(NoneScheme, StringComparison.OrdinalIgnoreCase))
        {
            return AuthTokens.None;
        }

        throw new ConnectionException(
            $"Unsupported authentication scheme '{config.AuthScheme}'. Ariadne v1 supports only " +
            $"'{BasicScheme}', '{BearerScheme}', and '{NoneScheme}' (Kerberos and Custom are out of scope). " +
            "The scheme fails loud rather than falling back to a guessed default.");
    }
}
