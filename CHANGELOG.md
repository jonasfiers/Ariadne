# Changelog

All notable changes to Ariadne are recorded here. Each entry corresponds to a landed, tested feature.

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
