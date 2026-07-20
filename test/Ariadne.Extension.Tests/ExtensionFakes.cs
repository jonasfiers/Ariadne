using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Neo4j.Driver;

namespace Ariadne.Extension.Tests;

/// <summary>
/// Hand-rolled fakes for the Extension boundary tests — no live Neo4j, no mocking libraries. They implement
/// exactly the slice of the driver surface a <see cref="Ariadne.Core.Execution.CypherExecutor"/> /
/// <see cref="ConnectivityVerifier"/> touch, so the wrapper's behaviour can be exercised with zero network.
/// The "fake executor/cache" the brief calls for is a <b>real</b> executor/cache wired to a fake
/// <see cref="IDriverFactory"/> (both Core types are sealed) — the factory either hands back a driver that
/// returns canned records / throws a chosen driver exception, or throws on <c>Create</c> itself.
/// </summary>
internal sealed class FakeExtDriverFactory : IDriverFactory
{
    private readonly IDriver? _driver;
    private readonly Exception? _throwOnCreate;

    private FakeExtDriverFactory(IDriver? driver, Exception? throwOnCreate)
    {
        _driver = driver;
        _throwOnCreate = throwOnCreate;
    }

    /// <summary>A factory that hands back <paramref name="driver"/> on every <c>Create</c>.</summary>
    public static FakeExtDriverFactory Returning(IDriver driver) => new FakeExtDriverFactory(driver, null);

    /// <summary>A factory that throws <paramref name="error"/> from <c>Create</c> (a config/build failure).</summary>
    public static FakeExtDriverFactory ThrowingOnCreate(Exception error) => new FakeExtDriverFactory(null, error);

    public IDriver Create(ConnConfig config)
    {
        if (_throwOnCreate is not null)
        {
            throw _throwOnCreate;
        }

        return _driver!;
    }
}

/// <summary>
/// A fake <see cref="IDriver"/> serving both paths: query execution (via <see cref="AsyncSession"/>) and the
/// connectivity check (via <see cref="VerifyConnectivityAsync"/>). Configure a cursor for the happy path, a
/// <c>throwOnRun</c> to fail a query, or a <c>connectivityError</c> to fail the check.
/// </summary>
internal sealed class FakeExtDriver : IDriver
{
    private readonly FakeExtCursor? _cursor;
    private readonly Exception? _throwOnRun;
    private readonly Exception? _connectivityError;

    public FakeExtDriver(
        FakeExtCursor? cursor = null,
        Exception? throwOnRun = null,
        Exception? connectivityError = null)
    {
        _cursor = cursor;
        _throwOnRun = throwOnRun;
        _connectivityError = connectivityError;
    }

    public Task VerifyConnectivityAsync() =>
        _connectivityError is null ? Task.CompletedTask : Task.FromException(_connectivityError);

    public IAsyncSession AsyncSession(Action<SessionConfigBuilder> action) =>
        new FakeExtSession(_cursor, _throwOnRun);

    public void Dispose() { }
    public ValueTask DisposeAsync() => default;

    // ---- unused IDriver surface: fail loud if anything reaches for it ----
    public Config Config => throw new NotImplementedException();
    public bool Encrypted => throw new NotImplementedException();
    public IAsyncSession AsyncSession() => throw new NotImplementedException();
    public Task CloseAsync() => throw new NotImplementedException();
    public Task<IServerInfo> GetServerInfoAsync() => throw new NotImplementedException();
    public Task<bool> TryVerifyConnectivityAsync() => throw new NotImplementedException();
    public Task<bool> SupportsMultiDbAsync() => throw new NotImplementedException();
    public Task<bool> SupportsSessionAuthAsync() => throw new NotImplementedException();
    public IExecutableQuery<IRecord, IRecord> ExecutableQuery(string cypher) => throw new NotImplementedException();
    public Task<bool> VerifyAuthenticationAsync(IAuthToken authToken) => throw new NotImplementedException();
}

internal sealed class FakeExtSession : IAsyncSession
{
    private readonly FakeExtCursor? _cursor;
    private readonly Exception? _throwOnRun;

    public FakeExtSession(FakeExtCursor? cursor, Exception? throwOnRun)
    {
        _cursor = cursor;
        _throwOnRun = throwOnRun;
    }

    private async Task<IResultCursor> DoRun()
    {
        await Task.Yield();
        if (_throwOnRun is not null)
        {
            throw _throwOnRun;
        }

        return _cursor ?? throw new InvalidOperationException("No cursor configured on the fake session.");
    }

    public Task<TResult> ExecuteReadAsync<TResult>(
        Func<IAsyncQueryRunner, Task<TResult>> work, Action<TransactionConfigBuilder>? action = null) =>
        work(new FakeExtRunner(this));

    public Task<TResult> ExecuteWriteAsync<TResult>(
        Func<IAsyncQueryRunner, Task<TResult>> work, Action<TransactionConfigBuilder>? action = null) =>
        work(new FakeExtRunner(this));

    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) => DoRun();

    internal Task<IResultCursor> RunFromRunner() => DoRun();

    public ValueTask DisposeAsync() => default;
    public void Dispose() { }

    // ---- members the executor must never touch: fail loud ----
    public Task<IResultCursor> RunAsync(string query) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(string query, object parameters) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(Query query) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(string query, Action<TransactionConfigBuilder> action) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters, Action<TransactionConfigBuilder> action) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(Query query, Action<TransactionConfigBuilder> action) => throw new NotImplementedException();
    public Task<IAsyncTransaction> BeginTransactionAsync() => throw new NotImplementedException();
    public Task<IAsyncTransaction> BeginTransactionAsync(Action<TransactionConfigBuilder> action) => throw new NotImplementedException();
    public Task<T> ReadTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task ReadTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task<T> WriteTransactionAsync<T>(Func<IAsyncTransaction, Task<T>> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task WriteTransactionAsync(Func<IAsyncTransaction, Task> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task ExecuteReadAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task ExecuteWriteAsync(Func<IAsyncQueryRunner, Task> work, Action<TransactionConfigBuilder>? action = null) => throw new NotImplementedException();
    public Task CloseAsync() => throw new NotImplementedException();
#pragma warning disable CS0618 // Bookmark is obsolete; the interface member must still be implemented.
    public Bookmark LastBookmark => throw new NotImplementedException();
#pragma warning restore CS0618
    public Bookmarks LastBookmarks => throw new NotImplementedException();
    public SessionConfig SessionConfig => throw new NotImplementedException();
}

internal sealed class FakeExtRunner : IAsyncQueryRunner
{
    private readonly FakeExtSession _session;

    public FakeExtRunner(FakeExtSession session) => _session = session;

    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        _session.RunFromRunner();

    public Task<IResultCursor> RunAsync(string query) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(string query, object parameters) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(Query query) => throw new NotImplementedException();
    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
}

/// <summary>A fake cursor over fixed keys, records, and summary — drains via both the fetch loop and enumerator.</summary>
internal sealed class FakeExtCursor : IResultCursor
{
    private readonly List<IRecord> _records;
    private readonly string[] _keys;
    private readonly IResultSummary _summary;
    private int _position = -1;

    public FakeExtCursor(string[] keys, List<IRecord> records, IResultSummary summary)
    {
        _keys = keys;
        _records = records;
        _summary = summary;
    }

    public Task<string[]> KeysAsync() => Task.FromResult(_keys);
    public Task<IResultSummary> ConsumeAsync() => Task.FromResult(_summary);

    public IRecord Current => _position >= 0 && _position < _records.Count
        ? _records[_position]
        : throw new InvalidOperationException("No current record.");

    public bool IsOpen => true;

    public Task<bool> FetchAsync()
    {
        _position++;
        return Task.FromResult(_position < _records.Count);
    }

    public Task<IRecord> PeekAsync() =>
        Task.FromResult(_position + 1 < _records.Count ? _records[_position + 1] : null!);

    public IAsyncEnumerator<IRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new Enumerator(_records);

    private sealed class Enumerator : IAsyncEnumerator<IRecord>
    {
        private readonly List<IRecord> _records;
        private int _index = -1;

        public Enumerator(List<IRecord> records) => _records = records;

        public IRecord Current => _records[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            _index++;
            return new ValueTask<bool>(_index < _records.Count);
        }

        public ValueTask DisposeAsync() => default;
    }
}

/// <summary>A minimal <see cref="IRecord"/> backed by ordered (key, value) pairs.</summary>
internal sealed class FakeExtRecord : IRecord
{
    private readonly List<string> _keys = new List<string>();
    private readonly List<object?> _values = new List<object?>();

    public FakeExtRecord With(string key, object? value)
    {
        _keys.Add(key);
        _values.Add(value);
        return this;
    }

    public IReadOnlyList<string> Keys => _keys;
    public object this[int index] => _values[index]!;
    public object this[string key] => _values[_keys.IndexOf(key)]!;

    public IReadOnlyDictionary<string, object> Values
    {
        get
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < _keys.Count; i++) d[_keys[i]] = _values[i]!;
            return d;
        }
    }

    public T Get<T>(string key) => (T)this[key];
    public bool TryGet<T>(string key, out T value)
    {
        int i = _keys.IndexOf(key);
        if (i >= 0) { value = (T)_values[i]!; return true; }
        value = default!;
        return false;
    }
    public T GetCaseInsensitive<T>(string key) => Get<T>(key);
    public bool TryGetCaseInsensitive<T>(string key, out T value) => TryGet(key, out value);

    IEnumerable<string> IReadOnlyDictionary<string, object>.Keys => _keys;
    IEnumerable<object> IReadOnlyDictionary<string, object>.Values
    {
        get { foreach (var v in _values) yield return v!; }
    }
    int IReadOnlyCollection<KeyValuePair<string, object>>.Count => _keys.Count;
    bool IReadOnlyDictionary<string, object>.ContainsKey(string key) => _keys.Contains(key);
    bool IReadOnlyDictionary<string, object>.TryGetValue(string key, out object value)
    {
        int i = _keys.IndexOf(key);
        if (i >= 0) { value = _values[i]!; return true; }
        value = null!;
        return false;
    }
    IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator()
    {
        for (int i = 0; i < _keys.Count; i++)
            yield return new KeyValuePair<string, object>(_keys[i], _values[i]!);
    }
    IEnumerator IEnumerable.GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object>>)this).GetEnumerator();
}

/// <summary>Minimal <see cref="ICounters"/> fake — every member a settable auto-property.</summary>
internal sealed class FakeExtCounters : ICounters
{
    public bool ContainsUpdates { get; set; }
    public bool ContainsSystemUpdates { get; set; }
    public int NodesCreated { get; set; }
    public int NodesDeleted { get; set; }
    public int RelationshipsCreated { get; set; }
    public int RelationshipsDeleted { get; set; }
    public int PropertiesSet { get; set; }
    public int LabelsAdded { get; set; }
    public int LabelsRemoved { get; set; }
    public int IndexesAdded { get; set; }
    public int IndexesRemoved { get; set; }
    public int ConstraintsAdded { get; set; }
    public int ConstraintsRemoved { get; set; }
    public int SystemUpdates { get; set; }
}

internal sealed class FakeExtDatabaseInfo : IDatabaseInfo
{
    public string Name { get; set; } = "neo4j";
}

/// <summary>Minimal <see cref="IResultSummary"/> fake — only the members the summary mapper reads carry values.</summary>
internal sealed class FakeExtSummary : IResultSummary
{
    public ICounters Counters { get; set; } = new FakeExtCounters();
    public TimeSpan ResultAvailableAfter { get; set; } = TimeSpan.Zero;
    public TimeSpan ResultConsumedAfter { get; set; } = TimeSpan.Zero;
    public QueryType QueryType { get; set; } = QueryType.ReadOnly;
    public IDatabaseInfo? Database { get; set; } = new FakeExtDatabaseInfo();

    public Query Query => new Query("");
    public bool HasPlan => false;
    public bool HasProfile => false;
    public IPlan Plan => null!;
    public IProfiledPlan Profile => null!;
    public IList<INotification> Notifications => Array.Empty<INotification>();
    public IList<IGqlStatusObject> GqlStatusObjects => Array.Empty<IGqlStatusObject>();
    public IServerInfo Server => null!;
    IDatabaseInfo IResultSummary.Database => Database!;
}
