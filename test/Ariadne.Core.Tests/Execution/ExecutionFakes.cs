using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Ariadne.Core.Connection;
using Neo4j.Driver;

namespace Ariadne.Core.Tests.Execution;

/// <summary>
/// Hand-rolled fakes for the Feature 09 execution tests — no live Neo4j, no mocking libraries. They implement
/// exactly the slice of the driver's async surface the executor is documented to touch
/// (<see cref="IDriver.AsyncSession(Action{SessionConfigBuilder})"/>, the managed-tx functions,
/// <see cref="IAsyncQueryRunner.RunAsync(string,IDictionary{string,object})"/>, and the cursor's
/// keys/records/summary members); every other member throws <see cref="NotImplementedException"/> so an
/// accidental dependence on unfaked behaviour fails loudly and proves the executor uses only the intended API.
/// </summary>
/// <summary>An <see cref="IDriverFactory"/> that hands back a single pre-built <see cref="ExecFakeDriver"/>.</summary>
internal sealed class ExecFakeDriverFactory : IDriverFactory
{
    private readonly IDriver _driver;

    public ExecFakeDriverFactory(IDriver driver) => _driver = driver;

    public IDriver Create(ConnConfig config) => _driver;
}

internal sealed class ExecFakeDriver : IDriver
{
    private readonly FakeCursor _cursor;
    private readonly Exception? _throwOnRun;

    public ExecFakeDriver(FakeCursor cursor, Exception? throwOnRun = null)
    {
        _cursor = cursor;
        _throwOnRun = throwOnRun;
    }

    /// <summary>The session produced by the last <see cref="AsyncSession(Action{SessionConfigBuilder})"/> call.</summary>
    public FakeSession? LastSession { get; private set; }

    public IAsyncSession AsyncSession(Action<SessionConfigBuilder> action)
    {
        // Observe what database the executor's configuration action selects by replaying it against a real
        // (reflection-constructed) SessionConfigBuilder and reading the resulting SessionConfig.Database.
        string? database = ResolveDatabase(action);
        var session = new FakeSession(_cursor, database, _throwOnRun);
        LastSession = session;
        return session;
    }

    // Reflection: SessionConfigBuilder has a non-public ctor(SessionConfig); WithDatabase mutates that config,
    // whose Database getter is public. This lets a test assert the executor wired WithDatabase correctly.
    private static string? ResolveDatabase(Action<SessionConfigBuilder> action)
    {
        var config = (SessionConfig)Activator.CreateInstance(typeof(SessionConfig), nonPublic: true)!;
        var builder = (SessionConfigBuilder)Activator.CreateInstance(
            typeof(SessionConfigBuilder),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: new object[] { config },
            culture: null)!;
        action(builder);
        return config.Database;
    }

    // ---- unused IDriver surface: fail loud if the executor ever reaches for it ----
    public Config Config => throw new NotImplementedException();
    public bool Encrypted => throw new NotImplementedException();
    public IAsyncSession AsyncSession() => throw new NotImplementedException();
    public Task CloseAsync() => throw new NotImplementedException();
    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
    public Task VerifyConnectivityAsync() => throw new NotImplementedException();
    public Task<IServerInfo> GetServerInfoAsync() => throw new NotImplementedException();
    public Task<bool> TryVerifyConnectivityAsync() => throw new NotImplementedException();
    public Task<bool> SupportsMultiDbAsync() => throw new NotImplementedException();
    public Task<bool> SupportsSessionAuthAsync() => throw new NotImplementedException();
    public IExecutableQuery<IRecord, IRecord> ExecutableQuery(string cypher) => throw new NotImplementedException();
    public Task<bool> VerifyAuthenticationAsync(IAuthToken authToken) => throw new NotImplementedException();
}

/// <summary>
/// A fake <see cref="IAsyncSession"/> that records which execution path was taken (managed read / managed
/// write / auto-commit), the query and parameters it received, the database it was opened for, and whether it
/// was disposed. The managed-tx functions hand the work lambda a <see cref="FakeRunner"/> (a separate
/// <see cref="IAsyncQueryRunner"/>) so the auto-commit counter — incremented only by the session's own
/// <c>RunAsync</c> — is never conflated with a managed run.
/// </summary>
internal sealed class FakeSession : IAsyncSession
{
    private readonly FakeCursor _cursor;
    private readonly Exception? _throwOnRun;

    public FakeSession(FakeCursor cursor, string? database, Exception? throwOnRun)
    {
        _cursor = cursor;
        Database = database;
        _throwOnRun = throwOnRun;
    }

    /// <summary>The database the executor opened this session for (null ⇒ driver default).</summary>
    public string? Database { get; }

    public int ReadCalls { get; private set; }
    public int WriteCalls { get; private set; }
    public int AutoCommitCalls { get; private set; }
    public string? LastQuery { get; private set; }
    public IDictionary<string, object>? LastParameters { get; private set; }
    public bool Disposed { get; private set; }

    // Shared run behaviour: record the call, then either throw the configured driver error or hand back the cursor.
    private async Task<IResultCursor> DoRun(string query, IDictionary<string, object> parameters)
    {
        LastQuery = query;
        LastParameters = parameters;
        await Task.Yield();
        if (_throwOnRun is not null)
        {
            throw _throwOnRun;
        }

        return _cursor;
    }

    internal Task<IResultCursor> RunFromRunner(string query, IDictionary<string, object> parameters) =>
        DoRun(query, parameters);

    // ---- managed transaction functions (routing + retry live here in the real driver) ----
    public Task<TResult> ExecuteReadAsync<TResult>(
        Func<IAsyncQueryRunner, Task<TResult>> work, Action<TransactionConfigBuilder>? action = null)
    {
        ReadCalls++;
        return work(new FakeRunner(this));
    }

    public Task<TResult> ExecuteWriteAsync<TResult>(
        Func<IAsyncQueryRunner, Task<TResult>> work, Action<TransactionConfigBuilder>? action = null)
    {
        WriteCalls++;
        return work(new FakeRunner(this));
    }

    // ---- IAsyncQueryRunner.RunAsync(string, IDictionary) — the auto-commit entry the executor uses ----
    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters)
    {
        AutoCommitCalls++;
        return DoRun(query, parameters);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return default;
    }

    public void Dispose() => Disposed = true;

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

/// <summary>
/// The <see cref="IAsyncQueryRunner"/> the managed-tx functions hand to the work lambda. It forwards the one
/// overload the executor calls back to the owning session (so query/params are recorded once) and fails loud
/// on everything else.
/// </summary>
internal sealed class FakeRunner : IAsyncQueryRunner
{
    private readonly FakeSession _session;

    public FakeRunner(FakeSession session) => _session = session;

    public Task<IResultCursor> RunAsync(string query, IDictionary<string, object> parameters) =>
        _session.RunFromRunner(query, parameters);

    public Task<IResultCursor> RunAsync(string query) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(string query, object parameters) => throw new NotImplementedException();
    public Task<IResultCursor> RunAsync(Query query) => throw new NotImplementedException();
    public void Dispose() { }
    public ValueTask DisposeAsync() => default;
}

/// <summary>
/// A fake <see cref="IResultCursor"/> over a fixed set of records, keys, and summary. Implements both the
/// fetch/current cursor loop and the async-enumerable surface so whichever path the driver's
/// <c>ToListAsync</c> extension uses to drain it, it works.
/// </summary>
internal sealed class FakeCursor : IResultCursor
{
    private readonly List<IRecord> _records;
    private readonly string[] _keys;
    private readonly IResultSummary _summary;
    private int _position = -1;

    public FakeCursor(string[] keys, List<IRecord> records, IResultSummary summary)
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

/// <summary>A minimal <see cref="IRecord"/> backed by ordered (key, value) pairs — enough for JSON building.</summary>
internal sealed class ExecFakeRecord : IRecord
{
    private readonly List<string> _keys = new List<string>();
    private readonly List<object?> _values = new List<object?>();

    public ExecFakeRecord With(string key, object? value)
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
internal sealed class ExecFakeCounters : ICounters
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

/// <summary>Minimal <see cref="IDatabaseInfo"/> fake.</summary>
internal sealed class ExecFakeDatabaseInfo : IDatabaseInfo
{
    public string Name { get; set; } = "neo4j";
}

/// <summary>Minimal <see cref="IResultSummary"/> fake — only the members the mapper reads carry values.</summary>
internal sealed class ExecFakeSummary : IResultSummary
{
    public ICounters Counters { get; set; } = new ExecFakeCounters();
    public TimeSpan ResultAvailableAfter { get; set; } = TimeSpan.Zero;
    public TimeSpan ResultConsumedAfter { get; set; } = TimeSpan.Zero;
    public QueryType QueryType { get; set; } = QueryType.ReadOnly;
    public IDatabaseInfo? Database { get; set; } = new ExecFakeDatabaseInfo();

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
