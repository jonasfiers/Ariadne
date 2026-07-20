# Neo4j Bolt Connector — Parameter Type-Mapping Spec

*The spec the Integration Studio Extension's parameter-binding layer is built from.*
**Scope: input parameters only** (OutSystems → Cypher). Result mapping (Cypher → OutSystems)
is the mirror-image sibling doc, deliberately out of scope here.

---

## 1. Design principles (inherited from PICASSO)

1. **Fail loud, never guess a type.** Every parameter carries an explicit type tag; the C#
   builds the exact driver type from it. An unknown/ambiguous tag throws — it is never
   coerced by heuristics.
2. **Lossless where the target type exists.** OutSystems types that have an exact Neo4j
   counterpart map with no loss.
3. **Explicit, documented downgrade where it doesn't.** The one type with no lossless target
   (`Decimal`) downgrades to Neo4j `Float` **with a stated caveat**, and the doc names the
   correct alternative (String).
4. **Never fabricate data.** OutSystems `Date Time` carries no timezone, so it maps to a
   *zoneless* Neo4j type. We do **not** invent a zone (UTC/server-local) — that would be a
   silent miscompute. A zoned value is only produced when the caller *supplies* the zone.
5. **Don't rely on the driver's implicit `System.DateTime` → temporal conversion.** Newer
   drivers map a CLR `DateTime` to `LocalDateTime` *or* `ZonedDateTime` based on its
   `DateTimeKind` — i.e. behaviour depends on a hidden flag. We construct the explicit driver
   temporal type from the tag instead, so the mapping is stated, not inferred.

---

## 2. The two pinned decisions

### Decision A — `Date Time` → `LocalDateTime`, not `ZonedDateTime`

OutSystems `Date Time` is a wall-clock value with **no zone**. Mapping it to Neo4j
`DateTime` (`ZonedDateTime`) would require fabricating an offset — and a value stored as
`2024-09-01T10:00Z` when the user meant local time compares and sorts wrong forever. So:

- **`Date Time` → Neo4j `LocalDateTime`** (zoneless ↔ zoneless, lossless, honest).
- When the caller genuinely needs a zoned timestamp, they use the explicit **`ZonedDateTime`**
  parameter type, which carries the zone *alongside* the value (never invented — see §4).

### Decision B — Lists & maps: flat via typed structures, nested via a typed-JSON escape hatch

OutSystems structures **cannot be self-referential**, but Cypher accepts arbitrarily nested
list/map parameters. So:

- **Flat (one level, scalar elements)** — `WHERE id IN $ids`, `SET n += $props` — is served by
  dedicated non-recursive structures (`CypherListElement`, `CypherMapEntry`), each carrying a
  scalar. This is the common case and needs no JSON.
- **Arbitrary nesting** (list-of-maps, map-of-lists, …) is served by a single **typed-JSON**
  escape hatch: a `Json` parameter whose value is JSON annotated with explicit type tags,
  `{"$type":"date","$value":"2024-09-01"}`. The C# walks it and builds real driver types from
  the tags. Still **lossless** (types are declared, not guessed) and still **fail-loud** (an
  unknown or malformed `$type` throws). Advanced path; most callers never touch it.

---

## 3. The full parameter type map

`Type` = the tag the caller sets on `CypherParameter.Type`.
"Driver CLR type" = the `Neo4j.Driver`-namespace type the extension constructs.

| OutSystems source | `Type` tag | .NET seen in extension | Driver CLR type | Neo4j / Cypher type | Lossless | Notes |
|---|---|---|---|---|---|---|
| Text | `String` | `string` | `string` | String | ✓ | |
| Integer (32-bit) | `Integer` | `int` | `long` | Integer | ✓ | widened 32→64 |
| Long Integer (64-bit) | `Integer` | `long` | `long` | Integer | ✓ | Neo4j Integer *is* Int64 |
| Decimal | `Float` | `decimal` | `double` | Float | ⚠ | **lossy** past ~15–17 sig. digits; Neo4j has no decimal type — see §5 |
| Boolean | `Boolean` | `bool` | `bool` | Boolean | ✓ | |
| Date | `Date` | `DateTime` (date part) | `LocalDate` | Date | ✓ | time part ignored |
| Time | `Time` | `DateTime` (time part) | `LocalTime` | LocalTime | ✓ | date part ignored |
| Date Time | `DateTime` | `DateTime` | `LocalDateTime` | LocalDateTime | ✓ | **Decision A** |
| Date Time **+ zone** | `ZonedDateTime` | `DateTime` + `ZoneId`/`OffsetMinutes` | `ZonedDateTime` | DateTime | ✓ | zone supplied, never fabricated |
| Time **+ offset** | `OffsetTime` | `DateTime` + `OffsetMinutes` | `OffsetTime` | Time | ✓ | optional (v1?) |
| — (component fields) | `Duration` | `{Months,Days,Seconds,Nanoseconds}` | `Duration` | Duration | ✓ | OutSystems has no duration type; optional (v1?) |
| — (component fields) | `Point` | `{Crs,X,Y,Z?}` | `Point` | Point | ✓ | optional (v1?) |
| Binary Data | `Bytes` | `byte[]` | `byte[]` | Bytes | ✓ | |
| any | `Null` | — | `null` | null | ✓ | explicit; empty Text ≠ Null |
| List of scalars | `List` | `CypherListElement[]` | `IList<object>` | List | ✓ | one level — **Decision B** |
| Flat map | `Map` | `CypherMapEntry[]` | `IDictionary<string,object>` | Map | ✓ | one level — **Decision B** |
| nested anything | `Json` | typed-JSON `string` | recursively built | any | ✓* | *lossless iff every `$type` valid; else throws — **Decision B** |

---

## 4. The OutSystems-side model

```
Structure CypherParameter          // one bound parameter ($name)
  Name           Text              // Cypher identifier, no leading '$'
  Type           Text              // one tag from §3
  // — scalar value carriers (exactly one is read, selected by Type) —
  StringValue    Text
  IntegerValue   Long Integer
  FloatValue     Decimal
  BooleanValue   Boolean
  DateTimeValue  Date Time         // serves Date / Time / DateTime / ZonedDateTime / OffsetTime
  BytesValue     Binary Data
  // — zone / offset (read only for ZonedDateTime, OffsetTime) —
  ZoneId         Text              // IANA zone, e.g. "Europe/Brussels" (preferred if set)
  OffsetMinutes  Integer           // fixed offset fallback, e.g. +120
  // — composite carriers (read only for the matching Type) —
  ListElements   CypherListElement List
  MapEntries     CypherMapEntry    List
  Duration       CypherDuration    // read only for Type = Duration
  Point          CypherPoint       // read only for Type = Point
  JsonValue      Text              // read only for Type = Json

Structure CypherListElement        // scalar-only (no recursion)
  Type   Text                      // any SCALAR tag from §3
  <the same scalar value carriers as above>

Structure CypherMapEntry           // scalar-only (no recursion)
  Key    Text
  Type   Text                      // any SCALAR tag
  <the same scalar value carriers as above>

Structure CypherDuration  { Months, Days, Seconds Long Integer; Nanoseconds Integer }
Structure CypherPoint     { Crs Text; X, Y, Z Decimal }   // Crs: "wgs-84" | "wgs-84-3d" | "cartesian" | "cartesian-3d"
```

Action signature: `RunCypher(Query Text, Parameters CypherParameter List) : <result>`.

**Why the fat-struct-with-one-field-used is right *here* (and was wrong for results):** the
caller *knows each parameter's type when they build it*, and parameters are overwhelmingly
scalars. The tagged-union that fell apart on dynamic, composite *results* is exactly the right
shape for static, known-typed *input*.

---

## 5. The `Decimal` problem (call it out loudly)

Neo4j has **no decimal / fixed-point type** — its only non-integer number is IEEE-754 `Float`
(double). So `Decimal → Float` is the only numeric mapping available, and it is **lossy** for
values beyond double's ~15–17 significant digits, and non-exact for many ordinary fractions.

**Guidance baked into the doc and the action's description:**
- For **money / exact decimals**, store as `String` (tag `String`) and parse on read, **or** as
  a scaled `Integer` (cents). Never as `Float`.
- `Decimal → Float` stays available for genuine measurements where double is fine.
- **Open decision (Jonas):** hard-*reject* a `Decimal` whose `double` round-trip loses
  precision, or allow-with-caveat? Recommendation: **allow + document** (Cypher Float is the
  right target for real-valued data; a hard reject would surprise users passing `3.5`). A value
  that overflows to `±Infinity`/`NaN` **is** rejected (that's a miscompute, not a rounding).

---

## 6. Validation & failure rules (all throw a named exception — fail loud)

1. **Unknown `Type` tag** → `NotSupportedException("Cypher parameter type '{Type}' is not supported")`.
2. **Empty / whitespace `Name`**, or a `Name` containing `$` or whitespace → reject.
3. **Duplicate parameter `Name`** in the list → reject (last-write-wins would be a silent bug).
4. **Composite tag on a scalar carrier** (e.g. `Type=List` with `ListElements` empty, or a
   scalar tag with a composite field populated) → reject the mismatch rather than ignore it.
5. **`ZonedDateTime`/`OffsetTime` with neither `ZoneId` nor `OffsetMinutes`** → reject (would
   otherwise fabricate a zone — violates principle 4).
6. **`Json` escape hatch**: any object lacking `$type`/`$value`, or an unknown `$type`, or a
   `$value` that doesn't parse for its `$type` → reject with the JSON path to the offender.
7. **`Decimal` → non-finite `double`** → reject (see §5).
8. **Nested composite in a `List`/`Map` element** (element `Type` is itself `List`/`Map`/`Json`)
   → reject with "use the `Json` parameter for nested structures" (Decision B boundary).

---

## 7. Reference mapping (C# sketch)

```csharp
using Neo4j.Driver;

IDictionary<string, object> BuildParameters(IEnumerable<CypherParameter> ps)
{
    var dict = new Dictionary<string, object>();
    foreach (var p in ps)
    {
        ValidateName(p.Name, dict);                 // rules 2 & 3
        dict[p.Name] = BuildValue(p.Type, p);       // scalar, composite, or throw
    }
    return dict;
}

object BuildValue(string type, IValueCarrier v) => type switch
{
    "String"   => v.StringValue,
    "Integer"  => v.IntegerValue,                                   // long
    "Float"    => ToFiniteDouble(v.FloatValue),                    // rule 7
    "Boolean"  => v.BooleanValue,
    "Date"     => new LocalDate(v.DateTimeValue),                  // date part
    "Time"     => new LocalTime(v.DateTimeValue),                  // time part
    "DateTime" => new LocalDateTime(v.DateTimeValue),             // Decision A — zoneless
    "ZonedDateTime" => BuildZoned(v),                              // rule 5 — zone required
    "OffsetTime"    => BuildOffsetTime(v),                         // rule 5
    "Bytes"    => v.BytesValue,                                    // byte[]
    "Duration" => new Duration(v.Duration.Months, v.Duration.Days,
                               v.Duration.Seconds, v.Duration.Nanoseconds),
    "Point"    => BuildPoint(v.Point),
    "Null"     => null,
    "List"     => v.ListElements.Select(e => BuildValue(e.Type, e)).ToList(),   // scalars only, rule 8
    "Map"      => v.MapEntries.ToDictionary(e => e.Key, e => BuildValue(e.Type, e)),
    "Json"     => BuildFromTypedJson(v.JsonValue),                 // recursive, rule 6
    _ => throw new NotSupportedException($"Cypher parameter type '{type}' is not supported")  // rule 1
};

object BuildZoned(IValueCarrier v)                                 // never fabricates a zone
{
    if (!string.IsNullOrEmpty(v.ZoneId))  return new ZonedDateTime(v.DateTimeValue, v.ZoneId);
    if (v.OffsetMinutes is int m)         return new ZonedDateTime(v.DateTimeValue, m * 60);
    throw new ArgumentException($"'{v.Name}' is ZonedDateTime but supplies no ZoneId or OffsetMinutes");
}
```

The `_ => throw` default is the PICASSO ethos in one line: an unrecognised parameter type is a
loud error, never a best-effort guess.

---

## 8. Open decisions for Jonas

1. **v1 surface** — ship `Duration`/`Point`/`OffsetTime` now, or scalars + `List`/`Map` +
   `Json` first and add the exotic temporals/spatials when a real graph needs them? (They're in
   the ~300-copybook-equivalent "does anyone actually pass this?" bucket.)
2. **Zone representation** — accept **both** `ZoneId` (IANA, DST-aware, preferred) and
   `OffsetMinutes` (fixed) as proposed, or just one? Recommendation: both, prefer `ZoneId`.
3. **`Decimal` precision policy** — allow-with-caveat (recommended) vs hard-reject on lossy
   round-trip (§5).
4. **Validation harness / oracle** — the PICASSO-equivalent proof is a round-trip test: bind
   one parameter of every type, `RETURN $p`, and assert the value that comes back through the
   driver equals what went in (against a real Neo4j in a container). That's the "GnuCOBOL
   oracle" analogue for this connector and it's what makes the mapping *demonstrably* correct
   rather than plausibly correct.
