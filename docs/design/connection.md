# Neo4j Bolt Connector — Connection, Session & Transaction Spec

*The third pillar, after parameters and results. This is the part that a naive connector gets
catastrophically wrong (a driver per call), and where getting it right is invisible when it works and
a production incident when it doesn't.*

---

## 0. Design principles (connection-specific, on top of the inherited ones)

- **The `IDriver` is a process-lifetime singleton, never per-call.** This is the single most important
  rule in the whole connector.
- **Fail loud, but tell the truth about *whose* fault it is.** A bad Cypher query is the developer's
  error (surface it fully); an unreachable server is an operational error (friendly message, no stack).
- **Never leak credentials** — not in logs, exceptions, cache keys, or connectivity errors.
- **Don't offer a leaky abstraction.** Where the stateless-Extension model can't safely support a
  feature (developer-managed multi-call transactions — §4), we reject it explicitly rather than ship
  something that breaks under concurrency.

---

## 1. The lifecycle model — Driver singleton vs Session per action *(the central decision)*

`Neo4j.Driver` has two objects with opposite lifetimes:

| Object | Lifetime | Thread-safe | Cost | In our connector |
|---|---|---|---|---|
| **`IDriver`** | **process** (app-pool) | **yes** | heavy — owns the connection pool | **cached singleton**, one per connection config |
| **`IAsyncSession`** | **one unit of work** | **no** | cheap — borrows from the pool | **created & disposed inside each action call** |

The anti-pattern (and near-certain cause of the existing connectors' problems if they have it):
`GraphDatabase.Driver(...)` on every action call → a new pool every query → connection storms, TLS
handshakes per call, exhausted sockets under load. **We create the driver once and reuse it.**

Because an Integration Studio Extension is stateless C# invoked per action, the singleton lives in a
**thread-safe static cache** in the Extension assembly (survives across calls for the app-pool's life):

```csharp
static readonly ConcurrentDictionary<string, Lazy<IDriver>> _drivers = new();

static IDriver GetDriver(ConnConfig c) =>
    _drivers.GetOrAdd(CacheKey(c), _ => new Lazy<IDriver>(() =>
    {
        var driver = GraphDatabase.Driver(c.Uri, BuildAuth(c), o => o
            .WithMaxConnectionPoolSize(c.MaxPoolSize)
            .WithConnectionAcquisitionTimeout(c.AcquisitionTimeout)
            .WithMaxTransactionRetryTime(c.MaxRetryTime)
            .WithFetchSize(c.FetchSize));
        return driver;
    })).Value;

// Key on URI + user + database + auth-scheme + a HASH of the secret (so a rotated
// password builds a fresh driver) + a config fingerprint. NEVER the raw password.
static string CacheKey(ConnConfig c) => $"{c.Uri}|{c.User}|{c.Database}|{c.AuthScheme}|{Sha256(c.Secret)}|{c.ConfigHash}";
```

- **`Lazy<>` + `GetOrAdd`** → exactly one driver per key even under concurrent first calls.
- **App-pool recycle** disposes statics → the next call lazily rebuilds. Fine.
- **Multiple front-ends** each pool independently. Fine (that's per-node pooling, as intended).
- We rely on **pool config** (`MaxConnectionLifetime`, idle handling) rather than explicit disposal;
  a `ResetDriver` admin action (§10) forces re-init after a config change.

---

## 2. Sync-over-async bridge (do it correctly)

Modern `Neo4j.Driver` (5.x) is **async-first** (`AsyncSession`, `ExecuteReadAsync`); OutSystems
Extension actions are **synchronous**. We block on the async API — carefully:

- Use `.GetAwaiter().GetResult()`, **not `.Result`** (`.Result` wraps the real exception in
  `AggregateException`; we want the original `Neo4jException` for §9 mapping).
- `ConfigureAwait(false)` throughout the async internals (no SynchronizationContext to deadlock on in
  the extension, but be defensive).
- Keep the blocking at the outermost boundary (one `GetAwaiter().GetResult()` per action), not sprinkled.

This is a small amount of code that is easy to get subtly wrong; it gets its own unit tests.

---

## 3. Execution modes — routing + automatic retry

Queries run through **managed transaction functions** (`ExecuteReadAsync`/`ExecuteWriteAsync`), which
give two things auto-commit (`session.RunAsync`) does not: **cluster routing** and **automatic retry**
of transient errors (leader switch, deadlock, transient network) up to `MaxTransactionRetryTime`. So we
expose the read/write distinction as first-class actions:

| Action | Driver call | Routes to (cluster/Aura) | Retry | Use for |
|---|---|---|---|---|
| **`RunCypherRead`** | `ExecuteReadAsync` | followers / read replicas | ✓ | pure `MATCH … RETURN` |
| **`RunCypherWrite`** | `ExecuteWriteAsync` | leader | ✓ | `CREATE`/`MERGE`/`SET`/`DELETE` |
| **`RunCypherAutoCommit`** | `session.RunAsync` | leader (no routing benefit) | ✗ | **only** `CALL { … } IN TRANSACTIONS` / admin cmds that *can't* run in a managed tx |

- Making read-vs-write explicit isn't bureaucracy — on Aura/clusters it's what lets reads scale onto
  replicas. Document: "use Read for anything that only reads."
- `RunCypherAutoCommit` exists for a real, narrow reason: batched-import queries (`CALL {} IN
  TRANSACTIONS`) and some administrative commands **must** be auto-commit. It carries a doc warning: no
  retry, no routing.
- The `+s`/scheme and `neo4j://` (routing) vs `bolt://` (direct) choice is in the URI (§6), independent
  of these.

---

## 4. What we deliberately DON'T support: developer-managed multi-call transactions

A `BeginTransaction` in one action and `Commit` in another would require caching a **live, non-thread-
safe session + transaction** across stateless action calls keyed by a handle — fragile under OutSystems'
threading, prone to leaks and cross-thread misuse, and silently corrupting when it goes wrong. So v1
**rejects that model by omission**:

- **Each action = one managed transaction.** A single Cypher statement can already do a great deal
  (`WITH`, `UNWIND`, subqueries, `FOREACH`), so most "multi-step atomic" needs compose into one query.
- If someone needs true cross-call transaction control, that's a **documented non-goal**, not a
  half-working feature. (This is the same fail-loud honesty as PICASSO refusing to guess a layout.)

---

## 5. Authentication modes

Built via `AuthTokens`, selected by an `AuthScheme` config value:

| Scheme | `AuthTokens` | Notes |
|---|---|---|
| **Basic** (default) | `AuthTokens.Basic(user, password)` | the common case |
| **Bearer** | `AuthTokens.Bearer(token)` | SSO/OIDC, Aura enterprise |
| **None** | `AuthTokens.None()` | dev/local only — **discouraged**, doc-flagged |
| Kerberos / Custom | `AuthTokens.Kerberos` / `.Custom` | *later* — noted, not v1 |

Note for a fast-follow: expiring **bearer tokens** want the driver's `AuthTokenManagers` (auto-refresh)
rather than a static token; v1 can take a static bearer token and document the refresh limitation.

---

## 6. TLS / encryption — carried by the URI scheme (preferred over config)

| Scheme | Routing | Encryption | Cert trust |
|---|---|---|---|
| `bolt://` / `neo4j://` | direct / routing | none | — |
| `bolt+s://` / **`neo4j+s://`** | direct / routing | **TLS** | **system CA (full verification)** |
| `bolt+ssc://` / `neo4j+ssc://` | direct / routing | TLS | self-signed accepted — **internal only** |

- **`neo4j+s://` is the recommended default** for anything non-local, and is **required by Neo4j Aura**.
- Prefer **scheme-based** encryption; do **not** also set encryption via `ConfigBuilder` (the driver
  throws if scheme and config conflict — we document "set it in the URI, once").
- `+ssc` (trust-any-cert) is offered for internal servers with self-signed certs, explicitly labelled as
  weaker.

---

## 7. Driver config surface + defaults

Exposed as optional config (Site Properties / advanced overload), with driver defaults where sane:

| Setting | Default | Why expose it |
|---|---|---|
| `MaxConnectionPoolSize` | 100 | tune for concurrency |
| `ConnectionAcquisitionTimeout` | 60 s | fail fast when the pool is saturated |
| `ConnectionTimeout` | 30 s | TCP connect timeout |
| `MaxConnectionLifetime` | 1 h | pool hygiene behind load balancers |
| `MaxTransactionRetryTime` | 30 s | how long §3 keeps retrying transient errors |
| `FetchSize` | 1000 | records per round-trip (ties to result materialization) |
| `Database` | `neo4j` | multi-database (`SessionConfig.WithDatabase`) |

---

## 8. Site-Property config model (the OutSystems side)

The `Neo4jBoltConnector` (Foundation) module owns configuration:

- **Default connection via Site Properties** — `Neo4j_Uri`, `Neo4j_User`, `Neo4j_Password`,
  `Neo4j_Database`. The public `RunCypherRead/Write` actions read these internally, so the **common-case
  signature is just `(Query, Parameters)`** — no connection args. That's the best DX and the default.
- **Advanced overload** — a `RunCypher…WithConnection(Query, Parameters, ConnConfig)` variant for
  multi-tenant / multi-database apps talking to more than one Neo4j. Same cached-driver machinery, keyed
  per config.
- **Credential honesty (Trusted security tie-in):** the password lives in a Site Property; be upfront in
  the docs that **OutSystems Site Properties are not encrypted at rest by default** — for
  high-sensitivity deployments recommend a secrets-manager/vault integration (a documented future
  option, not a silent gap). Credentials are **never** logged, echoed in exceptions, or placed raw in a
  cache key (§1 hashes the secret).

---

## 9. Error mapping — driver exception → named OutSystems error

Every driver exception maps to a **named, specific** error (fail loud), and the message distinguishes
developer errors from operational ones:

| Driver exception | Class | Surfaced as | Detail shown |
|---|---|---|---|
| `AuthenticationException` | operational | "Neo4j authentication failed" | **no** hint which field — don't leak |
| `ServiceUnavailableException` (retries exhausted) | operational | "Cannot reach Neo4j at `{uri}`" | uri only, no stack |
| `SessionExpiredException` (retries exhausted) | operational | "Neo4j routing/session error after retries" | — |
| `TransientException` (retries exhausted) | operational | "Neo4j transient error persisted after retries" | Neo4j code |
| `ClientException` (bad Cypher, constraint) | **developer** | the Neo4j message **verbatim** | full Neo4j code + message — it's their query |
| `DatabaseException` | operational | "Neo4j server error" | Neo4j code |
| timeout (acquisition/connection) | operational | "Neo4j connection timed out" | which timeout |

The read/write managed-tx functions already retried the retriable ones (§3); by the time we map, a
transient error means retries were *exhausted*, which is worth saying explicitly.

---

## 10. Admin / diagnostics actions

- **`VerifyConnectivity()`** → wraps `driver.VerifyConnectivityAsync()`; a "test connection" action for
  setup screens and the demo. Returns ok / typed error (§9).
- **`ResetDriver([ConnConfig])`** → disposes and evicts the cached driver so the next call rebuilds it;
  use after rotating credentials or changing config. Cheap insurance against the singleton going stale.

---

## 11. Open decisions for Jonas

1. **Config granularity in v1** — ship just the four Site Properties (Uri/User/Password/Database) and
   sensible driver defaults, or expose the full §7 knob set from the start? Recommend: four properties +
   defaults now; advanced knobs via the `WithConnection` overload later.
2. **Multi-connection in v1** — ship the `WithConnection` overload immediately, or single Site-Property
   connection first? Recommend single first; it's the 90% case and the overload is additive.
3. **Bearer-token refresh** — accept a static bearer token in v1 (documented "no auto-refresh"), or wire
   `AuthTokenManagers` now? Recommend static + documented limitation.
4. **`RunCypherAutoCommit` in v1** — include it (for `CALL {} IN TRANSACTIONS`) or defer until someone
   needs batched import? Recommend include; it's small and its absence is a real functional hole for
   data-load use cases.
5. **FetchSize vs the result spec's materialize/stream open item** — these are the same underlying
   question surfacing in two docs; decide them together when we consolidate.
