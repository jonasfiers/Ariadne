using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Neo4j.Driver;
using Xunit;

namespace Ariadne.Core.Tests.Connection;

/// <summary>
/// Unit tests for <see cref="DriverCache"/> (spec §1) — the cardinal one-driver-per-identity rule. All via
/// the <see cref="FakeDriverFactory"/>/<see cref="FakeDriver"/> fakes; zero network.
/// </summary>
public class DriverCacheTests
{
    private static ConnConfig BasicConfig(string uri = "neo4j+s://host:7687", string user = "neo4j",
        string password = "pw", string db = "neo4j") =>
        new ConnConfig { Uri = uri, Username = user, Password = password, Database = db, AuthScheme = "Basic" };

    // ============================== same config → one driver ==============================

    [Fact]
    public void Same_config_returns_the_same_driver_and_builds_once()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);
        ConnConfig config = BasicConfig();

        IDriver a = cache.GetDriver(config);
        IDriver b = cache.GetDriver(config);
        IDriver c = cache.GetDriver(BasicConfig()); // distinct but equal config instance

        Assert.Same(a, b);
        Assert.Same(a, c);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public void Different_uri_user_or_database_build_different_drivers()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        IDriver baseDriver = cache.GetDriver(BasicConfig());
        IDriver otherUri = cache.GetDriver(BasicConfig(uri: "neo4j+s://other:7687"));
        IDriver otherUser = cache.GetDriver(BasicConfig(user: "alice"));
        IDriver otherDb = cache.GetDriver(BasicConfig(db: "movies"));

        Assert.NotSame(baseDriver, otherUri);
        Assert.NotSame(baseDriver, otherUser);
        Assert.NotSame(baseDriver, otherDb);
        Assert.Equal(4, factory.CreateCount);
    }

    [Fact]
    public void Different_auth_scheme_builds_a_different_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        cache.GetDriver(BasicConfig());
        cache.GetDriver(new ConnConfig { Uri = "neo4j+s://host:7687", Database = "neo4j", AuthScheme = "None" });

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public void Different_config_knob_builds_a_different_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        ConnConfig a = BasicConfig();
        ConnConfig b = BasicConfig();
        b.MaxConnectionPoolSize = 50;

        cache.GetDriver(a);
        cache.GetDriver(b);

        Assert.Equal(2, factory.CreateCount);
    }

    // ============================== secret exclusion + rotation ==============================

    [Fact]
    public void Rotated_password_yields_a_new_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        IDriver before = cache.GetDriver(BasicConfig(password: "old"));
        IDriver after = cache.GetDriver(BasicConfig(password: "new"));

        Assert.NotSame(before, after);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public void Rotated_bearer_token_yields_a_new_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        cache.GetDriver(new ConnConfig { Uri = "neo4j+s://h", AuthScheme = "Bearer", BearerToken = "t1" });
        cache.GetDriver(new ConnConfig { Uri = "neo4j+s://h", AuthScheme = "Bearer", BearerToken = "t2" });

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public void Cache_key_never_contains_the_raw_secret()
    {
        string password = "S3cr3t-P@ss";
        string token = "bearer-9f8e7d";

        string basicKey = DriverCache.CacheKey(BasicConfig(password: password));
        string bearerKey = DriverCache.CacheKey(new ConnConfig
        {
            Uri = "neo4j+s://h", AuthScheme = "Bearer", BearerToken = token,
        });

        Assert.DoesNotContain(password, basicKey);
        Assert.DoesNotContain(token, bearerKey);
    }

    [Fact]
    public void Cache_key_contains_a_sha256_hash_of_the_secret_that_changes_with_it()
    {
        string k1 = DriverCache.CacheKey(BasicConfig(password: "old"));
        string k2 = DriverCache.CacheKey(BasicConfig(password: "new"));

        Assert.NotEqual(k1, k2);
        // The hash segment is 64 lowercase hex chars.
        string hashSegment = k1.Split('|')[4];
        Assert.Equal(64, hashSegment.Length);
        Assert.Matches("^[0-9a-f]{64}$", hashSegment);
    }

    [Fact]
    public void Cache_key_is_stable_for_equal_configs()
    {
        Assert.Equal(DriverCache.CacheKey(BasicConfig()), DriverCache.CacheKey(BasicConfig()));
    }

    // ============================== Reset / ResetAll / Dispose ==============================

    [Fact]
    public void Reset_disposes_and_evicts_so_next_get_rebuilds()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);
        ConnConfig config = BasicConfig();

        var first = (FakeDriver)cache.GetDriver(config);
        cache.Reset(config);

        Assert.True(first.Disposed);

        var second = (FakeDriver)cache.GetDriver(config);
        Assert.NotSame(first, second);
        Assert.False(second.Disposed);
        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public void Reset_of_an_absent_config_is_a_no_op()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        cache.Reset(BasicConfig()); // nothing cached yet
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void ResetAll_disposes_every_cached_driver()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        var d1 = (FakeDriver)cache.GetDriver(BasicConfig(user: "a"));
        var d2 = (FakeDriver)cache.GetDriver(BasicConfig(user: "b"));

        cache.ResetAll();

        Assert.True(d1.Disposed);
        Assert.True(d2.Disposed);

        // Rebuilds afterwards.
        cache.GetDriver(BasicConfig(user: "a"));
        Assert.Equal(3, factory.CreateCount);
    }

    [Fact]
    public void Dispose_disposes_every_cached_driver()
    {
        var factory = new FakeDriverFactory();
        var cache = new DriverCache(factory);

        var d1 = (FakeDriver)cache.GetDriver(BasicConfig(user: "a"));
        var d2 = (FakeDriver)cache.GetDriver(BasicConfig(user: "b"));

        cache.Dispose();

        Assert.True(d1.Disposed);
        Assert.True(d2.Disposed);
    }

    // ============================== argument guards ==============================

    [Fact]
    public void Null_factory_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DriverCache(null!));
    }

    [Fact]
    public void Null_config_on_get_throws()
    {
        using var cache = new DriverCache(new FakeDriverFactory());
        Assert.Throws<ArgumentNullException>(() => cache.GetDriver(null!));
    }

    [Fact]
    public void Null_config_on_cache_key_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DriverCache.CacheKey(null!));
    }

    // ============================== concurrency: one driver per key ==============================

    [Fact]
    public async Task Concurrent_first_calls_for_one_config_build_exactly_one_driver()
    {
        // A gate makes every thread pile up inside Create at the same time, maximizing contention on the
        // GetOrAdd/Lazy path — the exact race the Lazy+GetOrAdd pattern must survive.
        using var gate = new ManualResetEventSlim(false);
        var factory = new FakeDriverFactory { Gate = gate };
        using var cache = new DriverCache(factory);
        ConnConfig config = BasicConfig();

        const int threads = 32;
        var ready = new CountdownEvent(threads);
        var go = new ManualResetEventSlim(false);
        var results = new IDriver[threads];
        var tasks = new List<Task>();

        for (int i = 0; i < threads; i++)
        {
            int idx = i;
            tasks.Add(Task.Run(() =>
            {
                ready.Signal();
                go.Wait();
                results[idx] = cache.GetDriver(config);
            }));
        }

        ready.Wait();      // all threads spawned
        go.Set();          // release them together
        Thread.Sleep(50);  // let them collide on GetOrAdd
        gate.Set();        // now allow Create() to proceed
        await Task.WhenAll(tasks);

        Assert.Equal(1, factory.CreateCount);
        IDriver first = results[0];
        Assert.All(results, r => Assert.Same(first, r));
    }

    [Fact]
    public async Task Concurrent_calls_across_distinct_configs_build_one_each()
    {
        var factory = new FakeDriverFactory();
        using var cache = new DriverCache(factory);

        const int distinct = 16;
        var tasks = Enumerable.Range(0, distinct * 4).Select(i => Task.Run(() =>
            cache.GetDriver(BasicConfig(user: "user" + (i % distinct))))).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(distinct, factory.CreateCount);
    }
}
