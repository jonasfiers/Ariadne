using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Neo4j.Driver;

namespace Ariadne.Core.Connection;

/// <summary>
/// A thread-safe, process-lifetime cache of <see cref="IDriver"/> singletons keyed on connection identity
/// (connection spec §1) — the cardinal rule of the whole connector. A driver owns a heavyweight connection
/// pool and must be created <b>once per connection identity and reused</b>; a driver-per-call is the exact
/// anti-pattern (connection storms, TLS handshakes per call, socket exhaustion) this type exists to
/// prevent.
/// </summary>
/// <remarks>
/// <para>
/// This is an <b>instance</b> class (not static global state) so it is testable in isolation; the
/// OutSystems Extension will hold a single static instance for the app-pool's life. It is constructed with
/// an <see cref="IDriverFactory"/> seam so tests exercise it with a fake factory and zero network.
/// </para>
/// <para>
/// <b>One driver per key, even under concurrent first calls.</b> Each key maps to a
/// <see cref="Lazy{IDriver}"/>. <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
/// may evaluate its value-factory more than once under contention, but stores only one winner — and the
/// <see cref="Lazy{T}"/>'s own <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> guarantees the
/// wrapped <see cref="IDriverFactory.Create"/> runs at most once for that stored winner. Constructing extra
/// throwaway <see cref="Lazy{T}"/> objects is cheap and does <b>not</b> call <c>Create</c> (that only fires
/// on <c>.Value</c>).
/// </para>
/// <para>
/// <b>The cache key never contains a raw secret.</b> See <see cref="CacheKey(ConnConfig)"/>: the password
/// or bearer token is included only as a SHA-256 hash, so rotating a credential yields a different key (a
/// fresh driver) while the secret itself is never materialized in the dictionary.
/// </para>
/// </remarks>
public sealed class DriverCache : IDisposable
{
    private readonly IDriverFactory _factory;
    private readonly ConcurrentDictionary<string, Lazy<IDriver>> _drivers =
        new ConcurrentDictionary<string, Lazy<IDriver>>();

    /// <summary>Creates the cache over a driver factory.</summary>
    /// <param name="factory">The factory that builds drivers on first use. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is null.</exception>
    public DriverCache(IDriverFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>
    /// Returns the cached driver for <paramref name="config"/>, constructing it exactly once on first use.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <returns>The shared, cached <see cref="IDriver"/> for this connection identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public IDriver GetDriver(ConnConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        // GetOrAdd's factory may run more than once under contention, but only one Lazy is stored; the
        // Lazy (ExecutionAndPublication) then ensures Create() runs at most once for that winner.
        return _drivers.GetOrAdd(
            CacheKey(config),
            _ => new Lazy<IDriver>(() => _factory.Create(config), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    /// <summary>
    /// Computes the cache identity for <paramref name="config"/>:
    /// <c>Uri | Username | Database | AuthScheme | SHA-256(secret) | configFingerprint</c>. The raw password
    /// or bearer token is <b>never</b> present — only its hash — so a rotated secret produces a different
    /// key (and thus a rebuilt driver) without the secret ever entering the dictionary.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <returns>A stable, secret-free key string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public static string CacheKey(ConnConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        string secretHash = Sha256Hex(SelectSecret(config));

        // Config fingerprint: the four tuning knobs. A changed knob → a different driver (§10 ResetDriver
        // semantics achieved implicitly by keying).
        string fingerprint = string.Join(
            ",",
            config.MaxConnectionPoolSize?.ToString() ?? string.Empty,
            config.ConnectionAcquisitionTimeout?.Ticks.ToString() ?? string.Empty,
            config.MaxTransactionRetryTime?.Ticks.ToString() ?? string.Empty,
            config.FetchSize?.ToString() ?? string.Empty);

        return string.Join(
            "|",
            config.Uri ?? string.Empty,
            config.Username ?? string.Empty,
            config.Database ?? string.Empty,
            config.AuthScheme ?? string.Empty,
            secretHash,
            fingerprint);
    }

    /// <summary>
    /// Evicts and disposes the driver cached for <paramref name="config"/> (spec §10 <c>ResetDriver</c>), so
    /// the next <see cref="GetDriver(ConnConfig)"/> rebuilds it. Use after rotating credentials or changing
    /// config. A no-op if nothing is cached for that key.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    public void Reset(ConnConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (_drivers.TryRemove(CacheKey(config), out Lazy<IDriver> lazy))
        {
            DisposeIfCreated(lazy);
        }
    }

    /// <summary>Evicts and disposes every cached driver. Leaves the cache empty and reusable.</summary>
    public void ResetAll()
    {
        foreach (string key in System.Linq.Enumerable.ToArray(_drivers.Keys))
        {
            if (_drivers.TryRemove(key, out Lazy<IDriver> lazy))
            {
                DisposeIfCreated(lazy);
            }
        }
    }

    /// <summary>Disposes the cache, disposing every driver it holds.</summary>
    public void Dispose()
    {
        ResetAll();
    }

    // Only dispose a driver that was actually constructed; touching .Value on an unrealized Lazy would
    // force the very construction we are trying to discard.
    private static void DisposeIfCreated(Lazy<IDriver> lazy)
    {
        if (lazy.IsValueCreated)
        {
            lazy.Value.Dispose();
        }
    }

    // The credential that participates in identity, by scheme. Basic → password, Bearer → token, None → "".
    // An unknown scheme contributes no secret here (construction will fail loud in the factory anyway).
    private static string SelectSecret(ConnConfig config)
    {
        string scheme = config.AuthScheme?.Trim() ?? string.Empty;

        if (scheme.Equals(AuthTokenBuilder.BasicScheme, StringComparison.OrdinalIgnoreCase))
        {
            return config.Password ?? string.Empty;
        }

        if (scheme.Equals(AuthTokenBuilder.BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return config.BearerToken ?? string.Empty;
        }

        return string.Empty;
    }

    private static string Sha256Hex(string value)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash)
        {
            sb.Append(b.ToString("x2"));
        }

        return sb.ToString();
    }
}
