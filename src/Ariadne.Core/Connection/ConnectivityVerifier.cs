using System;
using Neo4j.Driver;

namespace Ariadne.Core.Connection;

/// <summary>
/// The "test connection" diagnostic (spec §10 <c>VerifyConnectivity</c>): obtains the cached driver for a
/// config and blocks on <see cref="IDriver.VerifyConnectivityAsync"/> via the sync-over-async
/// <see cref="AsyncBridge"/>, turning a reachable server into <see cref="ConnectivityResult.Success"/> and a
/// driver-reported failure into a typed <see cref="ConnectivityResult"/> — not an unhandled throw.
/// </summary>
/// <remarks>
/// <para>
/// It reuses the <see cref="DriverCache"/> (so a verify does not build a throwaway driver) and is therefore
/// fully testable with a fake factory/driver.
/// </para>
/// <para>
/// <b>Design decision (spec ambiguity, resolved):</b> a <em>connectivity</em> failure (server down, auth
/// rejected) is returned as a typed result, because "test connection" is expected to report rather than
/// throw. A <em>configuration</em> error, by contrast — an unsupported <see cref="ConnConfig.AuthScheme"/>
/// that makes <see cref="DriverCache.GetDriver(ConnConfig)"/> / the factory throw
/// <see cref="ConnectionException"/> — is a programmer error and is left to propagate loudly, consistent
/// with the fail-loud philosophy. Only the connectivity call itself is wrapped.
/// </para>
/// </remarks>
public sealed class ConnectivityVerifier
{
    private readonly DriverCache _cache;

    /// <summary>Creates the verifier over a driver cache.</summary>
    /// <param name="cache">The shared driver cache. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cache"/> is null.</exception>
    public ConnectivityVerifier(DriverCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>
    /// Verifies connectivity for <paramref name="config"/>.
    /// </summary>
    /// <param name="config">The connection configuration. Must not be null.</param>
    /// <returns><see cref="ConnectivityResult.Success"/>, or a typed failure on a connectivity error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ConnectionException">
    /// The configuration is invalid (e.g. an unsupported auth scheme) — a loud, deliberate failure raised
    /// before the connectivity call.
    /// </exception>
    public ConnectivityResult VerifyConnectivity(ConnConfig config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        // Config/construction errors (bad scheme) stay loud — obtained outside the try on purpose.
        IDriver driver = _cache.GetDriver(config);

        try
        {
            AsyncBridge.RunSync(driver.VerifyConnectivityAsync());
            return ConnectivityResult.Success;
        }
        catch (Exception ex)
        {
            return ConnectivityResult.Failure(ex);
        }
    }
}
