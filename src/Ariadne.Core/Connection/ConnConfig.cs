using System;

namespace Ariadne.Core.Connection;

/// <summary>
/// The connection identity plus optional driver tuning for a single Neo4j endpoint (connection spec §7).
/// A plain data POCO with mutable auto-properties, matching the other Ariadne POCOs; it carries no
/// behaviour. Two <see cref="ConnConfig"/> values that differ in any identity field (URI, user, database,
/// auth scheme, or the secret) resolve to <b>different</b> cached drivers — see
/// <see cref="DriverCache.CacheKey(ConnConfig)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TLS is carried by the URI scheme, never by config.</b> Encryption and certificate trust are selected
/// entirely by the <see cref="Uri"/> scheme (<c>neo4j+s://</c>, <c>bolt+ssc://</c>, …, spec §6). This POCO
/// deliberately exposes <b>no</b> encryption knob: the 5.x driver throws if scheme-based and config-based
/// encryption disagree, so there is exactly one place to set it.
/// </para>
/// <para>
/// The four tuning knobs are <see langword="null"/> by default, meaning "use the driver's own default"
/// (spec §7: pool size 100, acquisition timeout 60 s, retry time 30 s, fetch size 1000). Only a non-null
/// value is applied to the <c>ConfigBuilder</c>.
/// </para>
/// </remarks>
public sealed class ConnConfig
{
    /// <summary>
    /// The Bolt URI, including the scheme that selects routing and TLS (e.g. <c>neo4j+s://host:7687</c>).
    /// Part of the cache identity.
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// The authentication scheme: <c>"Basic"</c> (default), <c>"Bearer"</c>, or <c>"None"</c>. Compared
    /// case-insensitively. Any other value (including <c>Kerberos</c>/<c>Custom</c>, which are out of scope
    /// for v1) fails loud in <see cref="AuthTokenBuilder.BuildAuthToken(ConnConfig)"/>. Part of the cache
    /// identity.
    /// </summary>
    public string AuthScheme { get; set; } = "Basic";

    /// <summary>The username, for <c>Basic</c> auth. Part of the cache identity.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// The password, for <c>Basic</c> auth. A <b>secret</b>: it is never placed raw in the cache key (only a
    /// SHA-256 hash is — see <see cref="DriverCache.CacheKey(ConnConfig)"/>) and never echoed into an
    /// exception message.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The bearer token, for <c>Bearer</c> auth (SSO/OIDC, Aura enterprise). A <b>secret</b>, treated the
    /// same way as <see cref="Password"/> for cache-key hashing and non-leakage. v1 takes a static token;
    /// auto-refresh via <c>AuthTokenManagers</c> is a documented future option.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// The target database name (multi-database, spec §7 default <c>neo4j</c>). Part of the cache identity.
    /// Carried here for identity/keying; session-level use of it is Feature 09.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// Optional. Maximum size of the driver's connection pool. <see langword="null"/> ⇒ driver default (100).
    /// </summary>
    public int? MaxConnectionPoolSize { get; set; }

    /// <summary>
    /// Optional. How long to wait for a pooled connection before failing. <see langword="null"/> ⇒ driver
    /// default (60 s).
    /// </summary>
    public TimeSpan? ConnectionAcquisitionTimeout { get; set; }

    /// <summary>
    /// Optional. How long managed-transaction functions keep retrying transient errors.
    /// <see langword="null"/> ⇒ driver default (30 s).
    /// </summary>
    public TimeSpan? MaxTransactionRetryTime { get; set; }

    /// <summary>
    /// Optional. Records fetched per round-trip (the driver's <c>WithFetchSize</c> takes a 64-bit value).
    /// <see langword="null"/> ⇒ driver default (1000).
    /// </summary>
    public long? FetchSize { get; set; }
}
