# Changelog

All notable changes to Ariadne are recorded here. Each entry corresponds to a landed, tested feature.

## Feature 11 — `Ariadne.Extension` action surface (`Neo4jBoltActions`)

The thin C# action surface an OutSystems Integration Studio Extension is generated from — plain methods
wrapping `Ariadne.Core`, shaped the OutSystems way (a `bool` success result, inputs first, outputs as `out`,
`out string errorMessage` last) with **no exception ever crossing the boundary**. The actual Integration
Studio import is manual Windows work (out of scope); this C# is buildable and testable now. Mirrors the
sibling PICASSO `PicassoActions` pattern.

- **`public sealed class Neo4jBoltActions`** (plain class, no OutSystems base type) with five actions:
  - `RunCypherRead` / `RunCypherWrite` / `RunCypherAutoCommit` —
    `(ConnConfig connection, string query, CypherParameter[] parameters, out string recordsJson,
    out string[] columns, out CypherSummary summary, out string errorMessage)`. Parameters arrive as a
    **typed `CypherParameter[]` Structure list** (the differentiator — not a JSON string); the dynamic
    record set comes back as `recordsJson` Text, the fixed-shape summary as the typed `CypherSummary`.
  - `VerifyConnectivity` — `(ConnConfig connection, out bool ok, out string errorMessage)`.
  - `ResetDriver` — `(ConnConfig connection, out string errorMessage)`.
- **Process-lifetime singletons:** a `static readonly DriverCache` + one shared `CypherExecutor` over it,
  reused by every instance — never per-call (the whole point of the connection layer). An `internal`
  test-seam constructor injects an executor/cache wired to a fake `IDriverFactory`; the public parameterless
  constructor binds to the shared singletons.
- **Boundary discipline (still fail-loud):** every Core exception — `CypherExecutionException`,
  `CypherParameterException`, `ConnectionException`, `CypherResultException`, or anything else — is caught
  and returned as `false` + a populated `errorMessage`, with the outputs reset to safe defaults; nothing
  throws across the surface. A `CypherExecutionException` is shaped by its `Classification` (`Query error:` vs
  `Connection error:`). The password is **never** folded into a message (Core avoids it; this surface
  preserves that).
- **Tests (`test/Ariadne.Extension.Tests`, added to `Ariadne.slnx`):** 21 total. 18 headless unit tests
  inject a fake executor/cache (no server) proving success→`true`+mapped outputs, each exception type→`false`
  + a credential-free message with **no throw**, and that two default instances share one static
  cache/executor. 3 env-gated `[RequiresNeo4jFact]` integration tests (CREATE→read-back, VerifyConnectivity
  good, bad-password→`false`/`ok=false`) run against the live Neo4j and **skip** when the env vars are unset.
  Core's 361 tests unchanged and green.

## Feature 10 — The round-trip oracle (live-Neo4j integration tests)

The differentiator proof — the PICASSO-GnuCOBOL analogue. A systematic, **test-only**, end-to-end validation
that drives the whole stack against the **live Neo4j** for every supported type and asserts the exact
canonical JSON. One loop — `CypherExecutor.RunCypherRead("RETURN $p AS p", [<typed CypherParameter>])` →
assert `RecordsJson` — proves the parameter mapper (F01–03), the real server, and the result serializer
(F04–06) all agree. No production code changed.

- **`CypherRoundTripOracleTests`** — 21 env-gated `[RequiresNeo4jFact]` tests, **exact** JSON assertions
  (no tautological passes). Full §3 parameter-map + §3 result-map coverage:
  - **Scalars:** `String → "hi"`, `Integer → 42`, `Float → 3.5`, `Boolean → true`, `Null → null`.
  - **Documented `Decimal→Float` loss (spec §5)** asserted *as* a loss: `1.2345678901234567890` comes back
    `1.2345678901234567` (low digits dropped); `123456789012345678` comes back `1.2345678901234568E+17`.
  - **Temporals:** `Date`, `Time`, `DateTime` (zoneless), and `ZonedDateTime` in **both** forms — a named
    IANA zone (`{"value":"…","zone":"Europe/Brussels"}`) and a fixed offset (`{"value":"…","zone":"+02:00"}`).
    The **100-ns temporal boundary** is proven live: a CLR tick (`.1234567`) round-trips exactly; genuine
    sub-100-ns precision (the documented loss direction) is unreachable from CLR and fails loud on read.
  - **Bytes** → base64 (`AQIDBA==`); **List** of scalars → ordered array; **Map** of scalars → object;
    **`Json`** escape hatch → a nested list-of-maps-with-nested-lists round-trips at depth.
  - **Multi-record / multi-column** envelope (2×2) proves record-array + column ordering live.
  - **Graph types** by create-then-read-back — `Node`, `Relationship`, `Path` — asserting the §4 envelope
    structure, labels, type, and properties, and only the *presence* (not the value) of the
    non-deterministic `elementId`.
- **Oracle finding (documented, not a bug):** Neo4j returns **map keys in a server-determined order**
  (observed `gamma, alpha, beta` for insertion order `alpha, beta, gamma`); the serializer faithfully passes
  that order through. Stable per server, so the exact assertion holds — but callers must not assume map-key
  order.
- **Isolation:** a shared `[CollectionDefinition("Neo4jLiveOracle")]` collection fixture groups the live
  classes so they run **sequentially** and share one driver + one `:AriadneOracleTest` teardown, removing the
  cross-class collision two independent broad-delete fixtures would risk. Graph tests create only
  `:AriadneOracleTest` data and clear it before/after. (Feature 09's `CypherExecutorIntegrationTests` joined
  the same collection — its only change.)
- **Env-gated + skippable:** reuses Feature 09's `[RequiresNeo4jFact]` (reads `NEO4J_TEST_URI`/`_USER`/
  `_PASSWORD`). With the vars set, all 21 oracle tests run and pass; unset, they skip and CI stays green.
- **The headline number: 0 disagreements** — every supported type's live round-trip matched the documented
  canonical JSON exactly.
- **Tests: 21 new, suite 340 → 361.** With a live server: 361 pass, 0 skipped. Without: 334 pass, 27 skipped
  (the 21 oracle + Feature 09's 6 integration tests).
- **Deliberately not covered:** the deferred `Duration`/`Point`/`OffsetTime` tags — unsupported by design
  (fail loud), so there is nothing to round-trip.

## Feature 09 — Query execution: RunCypherRead/Write/AutoCommit + error mapping (`Ariadne.Core.Execution`)

The layer that actually runs a query — the piece that makes Core functionally complete. It wires the Feature 08
driver-singleton cache → a per-call session → the right transaction mode → the cursor → the Feature 06
`RecordsJson` envelope + the Feature 07 `CypherSummary`, converting the caller's typed parameters via the
Features 01-03 mapper and blocking the async driver once at the outermost boundary via `AsyncBridge`. A **live
Neo4j** is available on this box, so this feature ships **real integration tests**, not just mocks. The real
Neo4j.Driver 5.28.3 execution/cursor API was verified by reflection before use (not trusted from the brief).

- **`CypherExecutor`** (ctor takes a `DriverCache`) with three methods, each
  `(ConnConfig, string query, IEnumerable<CypherParameter>)` → a **`CypherExecutionResult`** carrying
  `RecordsJson` (string), `Columns` (`IReadOnlyList<string>`), and `Summary` (`CypherSummary`):
  - **`RunCypherRead`** → `session.ExecuteReadAsync` — managed read tx (routes to replicas, auto-retries transient errors).
  - **`RunCypherWrite`** → `session.ExecuteWriteAsync` — managed write tx (routes to leader, auto-retries).
  - **`RunCypherAutoCommit`** → `session.RunAsync` — implicit tx, **no routing, no retry** (for `CALL {} IN TRANSACTIONS` / admin).
- **Wiring:** `DriverCache.GetDriver` → `driver.AsyncSession(o => o.WithDatabase(db))` (only when `Database`
  is non-null) → the tx mode → cursor. Inside the tx function (mandatory for a managed tx) the cursor is fully
  drained: `KeysAsync()` for the columns, `ToListAsync()` to materialize the records, then `ConsumeAsync()`
  for the summary — all before the function returns. Records → `RecordsJsonBuilder`; summary →
  `CypherSummaryMapper`. Parameters → `CypherParameterMapper.BuildParameters`. Blocked via `AsyncBridge.RunSync`
  (the original driver exception, never an `AggregateException`, surfaces for mapping). The **columns come from
  the cursor's `Keys`**, so an empty (zero-row) result still reports its projected column names.
- **The three modes share one cursor-handling lambda** (a session *is* an `IAsyncQueryRunner`, so the
  auto-commit path calls the same lambda directly, without the managed-tx wrapper).
- **Error mapping (spec §9)** — every driver `Neo4jException` → a named **`CypherExecutionException`** carrying
  a `Classification` (Developer vs Operational) and the Neo4j `Code`. Developer errors surface **verbatim**;
  operational errors get a friendly message that **never leaks a credential** (built only from the URI + status
  code):
  | Driver exception | Class | Surfaced message |
  |---|---|---|
  | `ClientException` (bad Cypher / constraint) | **Developer** | `"{code}: {message}"` verbatim |
  | `AuthenticationException` | Operational | `"Neo4j authentication failed."` (no field hint) |
  | `ServiceUnavailableException` | Operational | `"Cannot reach Neo4j at {uri}."` |
  | `SessionExpiredException` | Operational | `"Neo4j routing/session error ({retried})."` |
  | `TransientException` | Operational | `"Neo4j transient error persisted ({retried}): {code}."` |
  | `ConnectionReadTimeoutException` | Operational | `"Neo4j connection timed out (read timeout)."` |
  | `DatabaseException` | Operational | `"Neo4j server error: {code}."` |
  | any other `Neo4jException` | Operational | `"Neo4j error: {code}."` |
  `{retried}` says "after retries were exhausted" for the managed modes and "in an auto-commit statement (no
  retry)" for auto-commit — truthful about whether a retry happened. A bad **parameter** (`CypherParameterException`)
  or a bad **config/auth scheme** (`ConnectionException`) still surfaces unwrapped and loud — they never reach the network.
- **Session disposal** uses a synchronous `using` (not `await using`): on netstandard2.0 the `await using`
  lowering hard-references `Microsoft.Bcl.AsyncInterfaces`, which the net10 test host omits as "in-box"; the
  driver session's own `IDisposable.Dispose()` closes it by blocking on `CloseAsync().GetAwaiter().GetResult()`
  — the same sync-over-async pattern the connector already uses — so cleanup is equivalent with no extra package.
- **Not offered:** developer-managed multi-call transactions (`BeginTransaction`/`Commit` across calls) — a
  documented non-goal (spec §4); each method here is exactly one transaction.
- **Tests: 30 new (24 unit + 6 live integration), suite 309 → 339.** Unit tests use hand-rolled fakes of the
  driver's async surface (no server, no mocking libs) for the orchestration: routing (Read→ExecuteReadAsync,
  Write→ExecuteWriteAsync, AutoCommit→session.RunAsync), parameter flow, session database + disposal,
  cursor-keys columns on empty results, and every error-mapping row (incl. asserting no operational message
  leaks the password). Integration tests hit a **real Neo4j**, are **env-gated** (`NEO4J_TEST_URI`/`_USER`/`_PASSWORD`)
  and **skip cleanly when unset** via a custom `[RequiresNeo4jFact]` (xunit 2.9.3 has no `Assert.Skip` — a v3
  API — so a discovery-time `FactAttribute.Skip` is set; no new package): a bound scalar + temporal round-trip,
  a `CREATE` with `NodesCreated == 1` read back, an auto-commit `RETURN`, an empty-result column check, and a
  real bad-Cypher developer error (`Neo.ClientError.Statement.SyntaxError`). They create/delete only
  `:AriadneOracleTest` nodes and never assume a clean DB. Verified live: 6/6 pass, 0 leftover nodes.

## Feature 08 — Connection foundation: driver-singleton cache + auth + connectivity (`Ariadne.Core.Connection`)

The start of the connection layer (connection spec §1/§2/§5/§7/§10) and the cardinal rule of the whole
connector: an `IDriver` is a **process-lifetime singleton per connection identity, never per-call** (a
driver-per-call = connection storms). Built behind a mockable factory seam so the cache/auth/connectivity
logic is fully unit-testable with hand-rolled fakes and **zero network** (there is no live Neo4j here). No
query execution / sessions / cursors (Feature 09), no driver-exception → OutSystems error mapping table yet.

- **`ConnConfig`** (POCO) — connection identity + optional §7 tuning: `Uri`, `AuthScheme` (default `Basic`),
  `Username`, `Password`, `BearerToken`, `Database`, and the nullable knobs `MaxConnectionPoolSize`,
  `ConnectionAcquisitionTimeout`, `MaxTransactionRetryTime`, `FetchSize` ("null ⇒ driver default"). TLS is
  carried by the URI scheme only (spec §6) — deliberately **no** encryption knob, since the 5.x driver
  throws if scheme- and config-based encryption disagree.
- **`AuthTokenBuilder.BuildAuthToken(ConnConfig)`** (static) — `Basic` → `AuthTokens.Basic(user, password)`,
  `Bearer` → `AuthTokens.Bearer(token)`, `None` → `AuthTokens.None` (verified: `None` is a static
  **property**, not a method). Scheme compared case-insensitively; any unknown/out-of-scope value
  (`Kerberos`/`Custom`/empty) **fails loud** with a named `ConnectionException` that never echoes a credential.
- **`IDriverFactory`** seam + production **`GraphDatabaseDriverFactory`** — `IDriver Create(ConnConfig)`. The
  production impl calls `GraphDatabase.Driver(uri, authToken, o => o.With…())`, applying only the non-null
  §7 knobs. Tests inject a fake factory returning a fake `IDriver` — no network.
- **`DriverCache`** (instance `IDisposable`) — a `ConcurrentDictionary<string, Lazy<IDriver>>`. `GetDriver`
  does `GetOrAdd(CacheKey, _ => new Lazy<IDriver>(() => factory.Create(config), ExecutionAndPublication)).Value`
  → **exactly one driver per key even under concurrent first calls** (proven with 32 threads gated to collide
  inside `GetOrAdd`; factory invoked exactly once). **`CacheKey`** =
  `Uri | Username | Database | AuthScheme | SHA-256(secret) | configFingerprint` — the raw password/bearer
  token is **never** in the key (only its hash), so a rotated secret → different key → fresh driver, with the
  secret never materialized in the dictionary. `Reset(config)` / `ResetAll()` / `Dispose()` evict and dispose
  cached drivers (only realized `Lazy` values are disposed — never forcing construction just to dispose it).
- **`AsyncBridge.RunSync`** (static) — the sync-over-async bridge (spec §2) via
  `.ConfigureAwait(false).GetAwaiter().GetResult()`, **not** `.Result`/`.Wait()`; a faulted task surfaces the
  **original** exception (asserted), not an `AggregateException`, which the Feature 09 error mapping depends on.
- **`ConnectivityVerifier.VerifyConnectivity(ConnConfig)`** (spec §10) + **`ConnectivityResult`** — obtains the
  cached driver and blocks on `driver.VerifyConnectivityAsync()` via `RunSync`; a reachable server →
  `ConnectivityResult.Success`, a driver-reported connectivity failure → a typed result (never an unhandled
  throw). A *configuration* error (bad auth scheme) still propagates loudly — resolved spec ambiguity: only
  the connectivity call is wrapped, programmer errors stay loud.
- **Verified against the real Neo4j.Driver 5.28.3 API** (by reflection): `GraphDatabase.Driver(string,
  IAuthToken, Action<ConfigBuilder>)`; `AuthTokens.Basic/Bearer` (methods) + `AuthTokens.None` (static
  property, with value equality on tokens); `IDriver : IDisposable, IAsyncDisposable` with
  `VerifyConnectivityAsync() : Task`; `ConfigBuilder.WithMaxConnectionPoolSize(int)` /
  `WithConnectionAcquisitionTimeout(TimeSpan)` / `WithMaxTransactionRetryTime(TimeSpan)` / `WithFetchSize(long)`.
- **43 new tests** (264 → **307**), all green. Pure logic + hand-rolled `IDriverFactory`/`IDriver` fakes (no
  server, no mocking libraries): auth per scheme + fail-loud + no-secret-leak; `RunSync` original-exception
  surfacing (including a genuinely-async fault) and value return; one-driver-per-key (same/different
  uri/user/db/scheme/knob), 32-thread concurrency, secret-excluded + rotating cache key, `Reset`/`ResetAll`/
  `Dispose` disposal, and connectivity success / typed-failure / loud-config-error.

## Feature 07 — Result summary / counters mapping: `CypherSummary` (`Ariadne.Core`)

The last result-side piece before the connection/execution layer: a **pure projection** of the driver's
`IResultSummary` into the static, typed `CypherSummary` POCO (result spec §2). Unlike records (dynamic → JSON),
the summary shape is known and fixed, so it is returned already-typed with no JSON step. No connection, session,
or query execution — `Map` reads an already-obtained summary.

- **`CypherSummary`** (POCO) — the fixed, typed summary: write counters (`NodesCreated`, `NodesDeleted`,
  `RelationshipsCreated`, `RelationshipsDeleted`, `PropertiesSet`, `LabelsAdded`, `LabelsRemoved`,
  `IndexesAdded`, `IndexesRemoved`, `ConstraintsAdded`, `ConstraintsRemoved`, `SystemUpdates`), the two update
  flags (`ContainsUpdates`, `ContainsSystemUpdates`), timings (`ResultAvailableAfterMs`,
  `ResultConsumedAfterMs`), `QueryType` (short code), and `Database`. Counters are **widened to `long`** to match
  the OutSystems **Long Integer** attribute type of §2 (the driver exposes them as `int` on `ICounters`; the
  widening is lossless). Mutable `{ get; set; }` auto-properties, matching the other POCOs.
- **`CypherSummaryMapper.Map(IResultSummary) : CypherSummary`** (static) — the pure projection. Counters copied
  1:1; timings converted `(long)TimeSpan.TotalMilliseconds`; `QueryType` mapped to its short code; database name
  read via `summary.Database?.Name` (passed through as reported, may be null — never fabricated).
- **Timing "unavailable" sentinel:** the driver reports an unavailable timing as `TimeSpan.FromMilliseconds(-1)`.
  It is **passed through as `-1`** (a documented sentinel the caller can detect), never silently zeroed — `0`
  would be indistinguishable from a genuine sub-millisecond timing. (Sub-millisecond values truncate toward
  zero, per the `(long)` cast — asserted.)
- **`QueryType` short codes** (verified against the real 5.28.3 enum): `ReadOnly`→`"r"`, `ReadWrite`→`"rw"`,
  `WriteOnly`→`"w"`, `SchemaWrite`→`"s"`.
- **Fail loud:** `Map(null)` → `ArgumentNullException`; a null `Counters` → `ArgumentNullException`; the driver's
  `QueryType.Unknown` (a real value with **no defined short code**) and any undefined enum value → named
  `CypherResultException` (never a blank or guessed classification).
- **Verified against the real Neo4j.Driver 5.28.3 API** (by reflection): `IResultSummary` members `Counters`
  (`ICounters`), `QueryType` (`QueryType` enum), `ResultAvailableAfter`/`ResultConsumedAfter` (`TimeSpan`),
  `Database` (`IDatabaseInfo`, with a `Name`); `ICounters` counter fields are `int`; `QueryType` enum =
  `Unknown=0, ReadOnly=1, ReadWrite=2, WriteOnly=3, SchemaWrite=4`.
- **19 new tests** (245 → **264**), all green. Pure logic — hand-rolled `IResultSummary`/`ICounters`/
  `IDatabaseInfo` fakes (no server, no session, no mocking libraries): every counter 1:1 (distinct primes so a
  swapped field is caught), int→long widening, timing conversion + the `-1` sentinel + sub-ms truncation, each
  `QueryType` code, `Unknown`/undefined → throw, both update flags, database pass-through (name, null-info,
  null-name), and both null-argument throws.

## Feature 06 — Record-envelope builder: `RecordsJson` (`Ariadne.Core`)

Assembles a whole query result into the canonical `RecordsJson` envelope (result spec §2): a JSON **array** of
per-record JSON **objects** (`{ "<column>": <value>, ... }` in `Keys` order), every value serialized by the
Feature 04/05 `CypherValueSerializer` (unchanged — only consumed). The last pure-serialization layer before the
connection/execution layer. It also **owns the writer-abandon-on-throw responsibility** (BACKLOG N1).

- **`RecordsJsonBuilder`** (static) — public entry points designed to compose with the future execution layer:
  - `RecordsJsonResult Build(IEnumerable<IRecord>)` — the JSON array text **and** the ordered column names,
    produced in a **single pass** over the sequence (verified — no double enumeration).
  - `string BuildRecordsJson(IEnumerable<IRecord>)` — convenience wrapper returning just the JSON.
  - `IReadOnlyList<string> WriteRecords(Utf8JsonWriter, IEnumerable<IRecord>)` — writes the array into an
    already-open writer (for composing into a larger writer later) and returns the columns.
- **`RecordsJsonResult`** — a readonly struct pairing `Json` (the array) with `Columns` (the first record's
  `Keys`, empty for an empty result). `Summary` (typed counters) is deliberately a separate feature.
- **Shape:** array of objects; per-record keys in the record's own `Keys` order; empty sequence → `[]` with no
  columns; a zero-column record → `{}`. Values read by the **integer indexer `IRecord.this[int]`** (aligned 1:1
  with `Keys`), so column order is exact and a duplicate name can't silently resolve to one column's value.
- **Fail loud:** a `null` records argument → `ArgumentNullException`; a `null` record in the sequence, or a
  **duplicate column name** within a record (Bolt never produces one — decided: fail loud rather than emit a
  duplicate JSON object key), → named `CypherResultException`. Distinct-case names (`X` vs `x`) are allowed
  (ordinal). Any unserializable value fails loud via `CypherValueSerializer`, inherited unchanged.
- **Writer integrity (BACKLOG N1 — this feature owns it).** A `CypherResultException` mid-result abandons the
  **whole** result: `Build`/`BuildRecordsJson` serialize into a fresh, method-local `MemoryStream` and only
  materialize the string **after** the array is fully closed and flushed — a throw propagates out **before** any
  string is built, so no truncated array, half-record, or reused-buffer leak can reach the caller. Proven:
  a 2nd-record unsupported `Duration`/`Point` → throws, the result reference is never assigned; and a failing
  build does **not** corrupt a subsequent clean one. The `WriteRecords` overload propagates the throw with the
  shared writer left holding an unclosed, invalid-JSON fragment (`[{"n":1},{"n":`) that `JsonDocument.Parse`
  rejects — the caller (execution layer) abandons it.
- **Verified against the real Neo4j.Driver 5.28.3 `IRecord`** (by reflection): its own members `Keys`
  (`IReadOnlyList<string>`), `Values` (`IReadOnlyDictionary<string,object>`), the integer indexer `this[int]`,
  and `Get`/`TryGet`/`GetCaseInsensitive`/`TryGetCaseInsensitive`; `IRecord` extends
  `IReadOnlyDictionary<string,object>` (so the string indexer, `Count`, `ContainsKey`, enumeration come from
  there). The test fake implements the full interface but the builder relies only on `Keys` + `this[int]`.
- **20 new tests** (225 → **245**), all green. Pure logic — a hand-rolled `IRecord` fake (and a minimal `INode`
  fake for value-routing spot-checks), no server, no session, no mocking libraries; shape/order/empty/columns,
  value routing (ZonedDateTime `{value,zone}`, node envelope, explicit null), duplicate-key + null-record
  fail-loud, and the full N1 no-partial-JSON behaviour all asserted.

## Feature 05 — Result composites + graph envelopes (`Ariadne.Core`)

The second half of result value serialization: extends `CypherValueSerializer.Write` from leaves (Feature 04)
to the **composite** and **graph** driver types, completing the value contract (the record envelope —
columns/records/summary — is Feature 06). Result spec §4 (graph envelopes) and §6 (arbitrary-key maps).

- **Composites → recursion through the same `Write`.** `System.Collections.IList` → JSON **array** (each
  element via `Write`); `System.Collections.IDictionary` → JSON **object** (each value via `Write`). A
  composite's children go back through `Write`, so arbitrary nesting (list-of-maps, map-of-lists, node
  whose property is a list, …) works to any depth and every leaf rule (fail-loud, 100-ns precision) is
  inherited unchanged. `byte[]` is still caught by the leaf case above, so it never falls into the `IList`
  path.
- **Arbitrary-key maps (§6) — keys verbatim, no Pattern B.** Map/property keys are emitted exactly as-is
  (spaces, dots, `$` preserved); there is no name/value list anywhere. A non-string map key (which Neo4j
  never produces) fails loud rather than being coerced.
- **Graph envelopes (§4), `elementId` only (decision R2 — the deprecated numeric `id` is never emitted):**
  - `Neo4j.Driver.INode` → `{ "elementId", "labels": [...], "properties": { ... } }`
  - `Neo4j.Driver.IRelationship` → `{ "elementId", "type", "startNodeElementId", "endNodeElementId", "properties": { ... } }`
  - `Neo4j.Driver.IPath` → `{ "nodes": [ <node>, ... ], "relationships": [ <rel>, ... ] }`, driver traversal
    order preserved.
  - `properties` is the driver's typed `IReadOnlyDictionary<string, object>`, serialized by recursing
    `Write` over each value — the same object shape as a bare map, but read from the entity's typed member
    so it never depends on the concrete map class.
- **Verified against the real Neo4j.Driver 5.28.3 API** (by reflection): `INode.ElementId`/`Labels`/
  `Properties`, `IRelationship.ElementId`/`Type`/`StartNodeElementId`/`EndNodeElementId`/`Properties`,
  `IPath.Nodes`/`Relationships` (`ElementId`/`Properties` inherited from `IEntity`).
- **Fail loud + writer integrity (BACKLOG P1/N1).** An unsupported value (deferred `Duration`/`Point`/
  `OffsetTime`, non-finite `double`, or unknown type) **anywhere in the tree** throws the named
  `CypherResultException` and the exception **propagates** — the serializer never catches it nor tries to
  "close" the partial JSON. Proven empirically: a mid-array throw leaves the shared `Utf8JsonWriter`
  holding an unclosed, invalid-JSON fragment (`[1,"ok"`), which `JsonDocument.Parse` rejects — Feature 06's
  record layer is responsible for abandoning that writer/buffer.
- **24 new tests** (201 → **225**), all green. Pure logic — the driver graph interfaces are satisfied by
  small hand-rolled fakes (no server, no session, no mocking libraries); lists/maps/nodes/rels/paths,
  arbitrary keys, deep nesting, traversal order, `elementId`-only identity, and the N1 mid-tree-throw
  behavior all asserted. (The two Feature 04 `IList`/`IDictionary` "not yet supported" placeholder tests
  were updated to assert the now-supported array/object output.)

## Feature 04 — Result leaf-value serialization: scalars + temporals (`Ariadne.Core`)

The first half of the **result** side (the inverse of Feature 01): serialize a single **leaf** Bolt/driver
value — as the driver hands it back — into the canonical result JSON (result spec §3 value type map, §5
null handling). New `Ariadne.Core.Results` namespace, `System.Text.Json`. This is the foundation the
graph/composite serializer (Feature 05) and the record envelope (Feature 06) build on.

- **`CypherValueSerializer`** — the public entry points: `void Write(Utf8JsonWriter, object?)` writes one
  value into an open writer (composes cleanly for the later nested list/graph work — a nested element is
  the same call, recursively), and `string Serialize(object?)` is a convenience wrapper returning the JSON
  text. A public `CanonicalWriterOptions` (relaxed escaping, so a `+` offset and non-ASCII text render
  literally rather than as `\uXXXX`) is shared so later features encode identically.
- **Leaf type map:** `null`→`null` (explicit, never omitted — §5), `bool`→JSON bool, `long`→JSON integer,
  `double`→JSON number, `string`→JSON string, `byte[]`→base64 string, and the driver temporals
  `LocalDate`→`"yyyy-MM-dd"`, `LocalTime`→`"HH:mm:ss[.fffffff]"`, `LocalDateTime`→`"yyyy-MM-ddTHH:mm:ss[.fffffff]"`
  (zoneless), and `ZonedDateTime`→ the pinned full-fidelity object `{ "value": "<zoneless ISO wall-clock>",
  "zone": "<named zone id or ±HH:MM offset>" }`.
- **Strict, culture-invariant ISO rendering** in one place (`TemporalFormat`), reading the driver's stored
  wall-clock **components** (`Year`…`Nanosecond`) directly — never a converted `DateTime` — so the emitted
  string is the literal local time and **cannot depend on the host timezone** (asserted under UTC / LA /
  Tokyo). The `ZonedDateTime` zone is extracted from the driver's `Zone`: a `ZoneId` yields its `.Id`; a
  `ZoneOffset` yields its `.OffsetSeconds` rendered as `±HH:MM` — a zone is read, never fabricated.
  Fractional seconds are emitted **only when non-zero**, up to 7 digits (100 ns / one CLR tick), trailing
  zeros trimmed.
- **Fail-loud rules** (all `CypherResultException`, message naming the offending runtime type): any value
  whose runtime type is not a supported leaf — the deferred `Duration`/`Point`/`OffsetTime`, the composites
  `Node`/`Relationship`/`Path`/`IList`/`IDictionary` (Feature 05), or any unknown type (incl. a boxed
  `int`/`decimal`, which are **not** silently widened) — throws, never a placeholder or a guess. Also fail
  loud: a non-finite `double` (`NaN`/±∞, which JSON cannot represent), a fixed-offset `ZonedDateTime` whose
  offset is not a whole number of minutes, and a temporal carrying genuine sub-100-ns precision (a
  nanosecond not a multiple of 100) the pinned ≤7-digit format cannot hold — all rejected rather than
  silently truncated. Feature 05 will replace the composite throws with real handling.
- **Out of scope (deliberately, land later):** composites/graph envelopes (Feature 05), the record
  envelope / columns / summary (Feature 06), the deferred temporals/spatials, and all
  connection/driver/session/execution code — serialization is pure over already-materialized values.
- **52 new tests** (149 → **201**), all green. Pure logic — real driver values constructed directly, no
  server, no session, no mocking libraries; the `+02:00`/named-zone extraction and host-TZ independence
  verified empirically against Neo4j.Driver 5.28.3.

## Feature 03 — `Json` escape-hatch parameter (`Ariadne.Core`)

The advanced, **recursive** typed-JSON escape hatch (Decision B) for arbitrarily nested structures
(list-of-maps, map-of-lists, …) the flat `List`/`Map` (Feature 02) deliberately cannot express. The
caller supplies JSON where every node carries an explicit `$type` tag (and, except `Null`, a `$value`);
the mapper walks it and builds the exact driver types — so it stays **lossless** (types declared, never
guessed) and **fail-loud**.

- **New dependency: `System.Text.Json` `10.0.10`** — the only package Feature 03 adds, pinned to a
  **netstandard2.0**-compatible version (matching PICASSO; confirmed it restores on netstandard2.0). Its
  **transitive dependencies must be bundled into the O11 Integration Studio extension package** (the
  extension has no NuGet restore at runtime). The Neo4j.Driver 5.28.3 pin and the TFMs are unchanged.
- **`JsonValue` carrier** on `CypherParameter`; the `Json` tag routes (top-level only) to a new recursive
  `TypedJsonBuilder`. Node `$type`s: `String`/`Integer`/`Float`/`Boolean`/`Null`/`Date`/`Time`/`DateTime`/
  `ZonedDateTime` (`$zone` **or** `$offsetMinutes`)/`Bytes` (base64), plus `List` (`$value` array of nodes)
  and `Map` (`$value` object of nodes) nesting to any depth → nested `IList<object?>`/`IDictionary<string,object?>`.
- **Scalar construction is shared, not duplicated.** The temporal build logic (100-ns precision,
  `DateTime.Kind`→`Unspecified` normalization for zoned values, `offset = minutes×60`) was factored out of
  the mapper into a new internal `ScalarValueFactory` that **both** the scalar/List/Map path and the JSON
  builder call — so a `Json` `Date`/`Time`/`DateTime`/`ZonedDateTime` node is byte-identical to the
  scalar-path value (asserted by cross-consistency tests).
- **Fail-loud rules** (all `CypherParameterException`, message carrying the **JSON path** to the offender,
  e.g. `$[1]`, `$.outer.inner`): invalid JSON; a node that isn't an object; a node missing `$type`; a
  non-string `$type`; an unknown or deferred (`Duration`/`Point`/`OffsetTime`) `$type`; a nested `Json`
  `$type` (top-level only); a `$value` that doesn't parse for its `$type` (`"abc"`/`3.5` for Integer, a
  bad ISO date, bad base64, wrong JSON kind); a `ZonedDateTime` with neither `$zone` nor `$offsetMinutes`
  (or an invalid zone); a `Map` with a duplicate or empty key. **No type inference** — an untagged JSON
  value is always an error.
- **Out of scope (fail loud):** the deferred temporals/spatials (`Duration`/`Point`/`OffsetTime`) even
  inside `Json`; `Json` nested inside the tree (use nested `List`/`Map`); any connection/driver/session code.

## Feature 02 — Composite parameters: List + Map (`Ariadne.Core`)

The two **flat, one-level** composite types, layered on Feature 01 by reusing the exact scalar path for
each element (no duplicated type switch). Nesting fails loud — that is the future `Json` escape hatch's
job (Feature 03, Decision B), not this feature's.

- **`CypherListElement`** / **`CypherMapEntry`** — POCOs mirroring the OutSystems structures: the same
  scalar value carriers as `CypherParameter`, with **no recursion** (`CypherMapEntry` adds a `Key`,
  neither has composite carriers). New `ListElements` / `MapEntries` carriers on `CypherParameter`.
- **`List` → `IList<object?>`**, **`Map` → `IDictionary<string, object?>`** (ordinal/case-sensitive keys),
  each element/entry mapped through the **shared scalar path**. New `internal IScalarCarrier` interface
  unifies how the mapper *reads* the carriers, so a top-level scalar and a List/Map element run the exact
  same code — the Feature 01 scalar behaviour (incl. `DateTime.Kind` normalization for `ZonedDateTime`)
  applies to elements unchanged, verified by test.
- **Fail-loud rules** (all `CypherParameterException`, naming the parameter and — for maps — the `Key`):
  a `List`/`Map`/`Json` element/entry type → throw pointing to the `Json` parameter for nesting; a
  deferred/unknown element type → throw; an empty/whitespace or duplicate map `Key` → throw; a `List`/`Map`
  tag with a **null** carrier → throw (missing carrier); a null element/entry → throw (never silently
  dropped). An **empty** list/map is valid → empty container.
- **Out of scope (fail loud here, land later):** the `Json` escape hatch (Feature 03), any nesting
  (list-of-lists, map-of-maps, …), and the deferred temporals/spatials (`Duration`/`Point`/`OffsetTime`).

## Feature 01 — Scalar parameter mapping (`Ariadne.Core`)

The typed-parameter core: turn caller-supplied, explicitly-typed **scalar** parameters into the exact
`Neo4j.Driver` value types the driver expects, failing loudly on anything unmappable.

- **`CypherParameter`** — a POCO mirroring the OutSystems structure: `Name`, `Type` tag, and the scalar
  value carriers (`StringValue`, `IntegerValue`, `FloatValue`, `BooleanValue`, `DateTimeValue`,
  `BytesValue`) plus `ZoneId` / `OffsetMinutes` for the zoned type.
- **`CypherParameterMapper.BuildParameters`** — pure logic (no driver/session/connection opened) that
  maps each parameter by its case-insensitive `Type` tag to the exact driver type:

  | Tag | Driver CLR type |
  |---|---|
  | `String` | `string` |
  | `Integer` | `long` |
  | `Float` | `double` (via `(double)decimal`; lossy, documented — Neo4j has no decimal type) |
  | `Boolean` | `bool` |
  | `Date` | `Neo4j.Driver.LocalDate` |
  | `Time` | `Neo4j.Driver.LocalTime` |
  | `DateTime` | `Neo4j.Driver.LocalDateTime` (zoneless — never fabricates a zone) |
  | `ZonedDateTime` | `Neo4j.Driver.ZonedDateTime` (from `ZoneId`, else `OffsetMinutes`) |
  | `Bytes` | `byte[]` |
  | `Null` | `null` |

- **Sub-second precision preserved**: a CLR `DateTime`'s 100-ns ticks are carried into the driver
  temporal's nanoseconds (verified by test for `LocalTime`, `LocalDateTime`, `ZonedDateTime`).
- **`CypherParameterException`** — the single named, fail-loud failure signal. Thrown on: an unknown,
  composite (`List`/`Map`/`Json`), or deferred (`Duration`/`Point`/`OffsetTime`) type tag; an
  empty/whitespace name, a name containing `$` or whitespace, or a duplicate name; a missing required
  value carrier; a `ZonedDateTime` supplying neither `ZoneId` nor `OffsetMinutes`; or an invalid `ZoneId`.
  Every message names the offending parameter.
- **Out of scope (fail loud here, land later):** composites (`List`/`Map`/`Json` — Feature 02) and the
  deferred temporals/spatials (`Duration`/`Point`/`OffsetTime`).
