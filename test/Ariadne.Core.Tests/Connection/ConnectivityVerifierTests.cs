using System;
using Ariadne.Core.Connection;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Connection;

/// <summary>
/// Unit tests for <see cref="ConnectivityVerifier"/> (spec §10): success returns
/// <see cref="ConnectivityResult.Success"/>, a driver-reported connectivity failure becomes a typed result
/// (not an unhandled throw), and a configuration error (bad scheme) still fails loud. All via fakes.
/// </summary>
public class ConnectivityVerifierTests
{
    private static ConnConfig BasicConfig() =>
        new ConnConfig { Uri = "neo4j+s://h:7687", Username = "neo4j", Password = "pw", AuthScheme = "Basic" };

    [Fact]
    public void Reachable_server_returns_success_and_uses_the_cached_driver()
    {
        var driver = new FakeDriver();
        var factory = new FakeDriverFactory(_ => driver);
        using var cache = new DriverCache(factory);
        var verifier = new ConnectivityVerifier(cache);

        ConnectivityResult result = verifier.VerifyConnectivity(BasicConfig());

        Assert.True(result.Ok);
        Assert.Null(result.ErrorType);
        Assert.Equal(1, driver.VerifyCallCount);
        Assert.Equal(1, factory.CreateCount); // used the cache, did not build a throwaway
    }

    [Fact]
    public void Repeated_verify_reuses_the_singleton_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);
        var verifier = new ConnectivityVerifier(cache);

        verifier.VerifyConnectivity(BasicConfig());
        verifier.VerifyConnectivity(BasicConfig());

        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public void Driver_reporting_failure_returns_typed_error_not_a_throw()
    {
        var failing = new FakeDriver(new ServiceUnavailableException("cannot reach host"));
        var factory = new FakeDriverFactory(_ => failing);
        using var cache = new DriverCache(factory);
        var verifier = new ConnectivityVerifier(cache);

        ConnectivityResult result = verifier.VerifyConnectivity(BasicConfig());

        Assert.False(result.Ok);
        Assert.Equal(nameof(ServiceUnavailableException), result.ErrorType);
        Assert.Equal("cannot reach host", result.ErrorMessage);
    }

    [Fact]
    public void Authentication_failure_returns_typed_error()
    {
        var failing = new FakeDriver(new AuthenticationException("auth failed"));
        var factory = new FakeDriverFactory(_ => failing);
        using var cache = new DriverCache(factory);
        var verifier = new ConnectivityVerifier(cache);

        ConnectivityResult result = verifier.VerifyConnectivity(BasicConfig());

        Assert.False(result.Ok);
        Assert.Equal(nameof(AuthenticationException), result.ErrorType);
    }

    [Fact]
    public void Invalid_auth_scheme_still_fails_loud()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);
        var verifier = new ConnectivityVerifier(cache);

        var bad = new ConnConfig { Uri = "neo4j+s://h", AuthScheme = "Kerberos" };

        // Construction error is a programmer error — it propagates, not swallowed into a result.
        Assert.Throws<ConnectionException>(() => verifier.VerifyConnectivity(bad));
    }

    [Fact]
    public void Null_cache_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectivityVerifier(null!));
    }

    [Fact]
    public void Null_config_throws()
    {
        var verifier = new ConnectivityVerifier(new DriverCache(new FakeDriverFactory()));
        Assert.Throws<ArgumentNullException>(() => verifier.VerifyConnectivity(null!));
    }
}
