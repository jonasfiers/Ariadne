# Changelog

All notable changes to Ariadne are recorded here. Each entry corresponds to a landed, tested feature.

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
