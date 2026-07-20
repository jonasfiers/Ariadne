using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Neo4j.Driver;

namespace Ariadne.Core.Tests.Connection;

/// <summary>
/// Hand-rolled fakes for the Feature 08 connection tests — no live Neo4j, no mocking libraries. A
/// <see cref="FakeDriverFactory"/> counts how many drivers it built (per config and in total) so
/// one-driver-per-key can be asserted, and a <see cref="FakeDriver"/> records disposal and can be told to
/// fail its connectivity check.
/// </summary>
internal sealed class FakeDriverFactory : IDriverFactory
{
    private int _createCount;
    private readonly Func<ConnConfig, FakeDriver>? _driverFor;

    public FakeDriverFactory(Func<ConnConfig, FakeDriver>? driverFor = null)
    {
        _driverFor = driverFor;
    }

    /// <summary>Total number of <see cref="Create(ConnConfig)"/> calls across all keys.</summary>
    public int CreateCount => Volatile.Read(ref _createCount);

    /// <summary>Every driver this factory has produced, in creation order.</summary>
    public List<FakeDriver> Created { get; } = new List<FakeDriver>();

    /// <summary>Optional latch: when set, <see cref="Create"/> blocks until released — to force contention.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public IDriver Create(ConnConfig config)
    {
        // Mirror the production factory's fail-loud auth validation so bad-scheme configs behave
        // identically through the fake (the built token is discarded — we only need its validation).
        AuthTokenBuilder.BuildAuthToken(config);

        Gate?.Wait();
        Interlocked.Increment(ref _createCount);
        FakeDriver driver = _driverFor?.Invoke(config) ?? new FakeDriver();
        lock (Created)
        {
            Created.Add(driver);
        }

        return driver;
    }
}

/// <summary>
/// A minimal <see cref="IDriver"/> fake. Only the members the connection layer touches are implemented
/// (<see cref="VerifyConnectivityAsync"/>, <see cref="Dispose"/>); everything else throws
/// <see cref="NotImplementedException"/> so an accidental dependency on unfaked behaviour is caught loudly.
/// </summary>
internal sealed class FakeDriver : IDriver
{
    private readonly Exception? _connectivityError;

    public FakeDriver(Exception? connectivityError = null)
    {
        _connectivityError = connectivityError;
    }

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many times <see cref="VerifyConnectivityAsync"/> was invoked.</summary>
    public int VerifyCallCount { get; private set; }

    public Task VerifyConnectivityAsync()
    {
        VerifyCallCount++;
        if (_connectivityError is not null)
        {
            return Task.FromException(_connectivityError);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }

    // ---- unused IDriver surface: fail loud if the connection layer ever reaches for it ----
    public Config Config => throw new NotImplementedException();
    public bool Encrypted => throw new NotImplementedException();
    public IAsyncSession AsyncSession() => throw new NotImplementedException();
    public IAsyncSession AsyncSession(Action<SessionConfigBuilder> action) => throw new NotImplementedException();
    public Task CloseAsync() => throw new NotImplementedException();
    public Task<IServerInfo> GetServerInfoAsync() => throw new NotImplementedException();
    public Task<bool> TryVerifyConnectivityAsync() => throw new NotImplementedException();
    public Task<bool> SupportsMultiDbAsync() => throw new NotImplementedException();
    public Task<bool> SupportsSessionAuthAsync() => throw new NotImplementedException();
    public IExecutableQuery<IRecord, IRecord> ExecutableQuery(string cypher) => throw new NotImplementedException();
    public Task<bool> VerifyAuthenticationAsync(IAuthToken authToken) => throw new NotImplementedException();
}
