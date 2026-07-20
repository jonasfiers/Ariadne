# Changelog

All notable changes to Ariadne are recorded here. Each entry corresponds to a landed, tested feature.

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
