# Neo4j Bolt Connector — Result Mapping Spec (Cypher → OutSystems)

*The mirror of the parameter spec, and the harder half — this is where the dynamic composites live.*
**Scope: query results only** (Cypher → OutSystems). Parameters (OutSystems → Cypher) are the sibling doc.

---

## 0. The core idea — one JSON contract, two consumption modes

The connector's real product is a **single canonical, documented, validated JSON serialization of a
Cypher result**. Everything else is how the developer consumes it:

- **Pattern A** — the developer hand-defines an OutSystems Structure matching their query's `RETURN`
  shape and `JSONDeserialize`s the records into it.
- **Pattern C** — a design-time sampler *generates* that Structure from a real sample run.

Both hit the **same runtime contract**: the action returns the records as JSON Text; A and C differ
only in *who authors the target Structure*. So this spec is 90% "define the JSON precisely enough
that (a) it deserializes cleanly into a static Structure and (b) a Structure can be mechanically
derived from it." **Pattern B (generic name→value lists) is excluded** — arbitrary-key maps are
handled as nested JSON instead (§6), never as a key-value list.

---

## 1. Design principles (inherited from PICASSO)

1. **Fail loud, never guess.** An unsupported/unknown Bolt type at serialize time throws; it is never
   emitted as a lossy approximation.
2. **Lossless where OutSystems has the type; documented downgrade where it doesn't** (Float, zoned
   temporals, Duration/Point) — and the downgrade is always *stated at the type*, with the lossless
   alternative named.
3. **Never fabricate.** A zoned value keeps its offset in the JSON; we don't silently drop it *or*
   invent one — the developer chooses Text (lossless) vs DateTime (drops zone, convenient).
4. **The JSON must be round-trippable into a static Structure.** No construct that OutSystems
   `JSONDeserialize` / "Paste JSON as Structure" can't represent.

---

## 2. The result envelope

The action returns three outputs, splitting the **static** part (typed) from the **dynamic** part (JSON):

```
RunCypher(Query Text, Parameters CypherParameter List) :
    RecordsJson  Text                 // dynamic — the records array as JSON (§3–§7)
    Columns      Text List            // the RETURN column names, in order
    Summary      CypherSummary        // STATIC shape → returned already-typed, no JSON step
```

`CypherSummary` is a fixed structure (counters are a known set), so it's typed directly — only the
*records* are dynamic:

```
Structure CypherSummary
  NodesCreated, NodesDeleted, RelationshipsCreated, RelationshipsDeleted,
  PropertiesSet, LabelsAdded, LabelsRemoved, IndexesAdded, ...   Long Integer
  ResultAvailableAfterMs, ResultConsumedAfterMs                   Long Integer
  QueryType   Text        // "r" | "rw" | "w" | "s"
```

Consumption (Pattern A): `JSONDeserialize(RecordsJson, "List of MyRecord")` where `MyRecord` has one
attribute per column in `Columns`.

`RecordsJson` is a JSON **array of records**; each record is a JSON object keyed by column name:
```jsonc
[ { "p": <value>, "since": <value> },
  { "p": <value>, "since": <value> } ]
```

---

## 3. The value type map (Bolt result type → JSON → OutSystems attribute)

| Neo4j / Cypher type | Driver CLR | JSON shape emitted | OutSystems attribute | Lossless | Notes |
|---|---|---|---|---|---|
| Null | `null` | `null` | (any) | ⚠ | see §5 — OutSystems can't hold null in a value type |
| Boolean | `bool` | `true`/`false` | Boolean | ✓ | |
| Integer | `long` | JSON number | **Long Integer** | ✓ | **never `Integer`** — Neo4j Integer is 64-bit; 32-bit would overflow |
| Float | `double` | JSON number | Decimal | ✓* | double→Decimal fine for display; huge/edge doubles noted |
| String | `string` | JSON string | Text | ✓ | |
| Bytes | `byte[]` | base64 JSON string | Binary Data | ✓† | †confirm OS base64↔Binary round-trip empirically |
| Date | `LocalDate` | `"2024-09-01"` | Date | ✓ | ISO-8601 |
| LocalTime | `LocalTime` | `"10:30:00"` | Time | ✓ | |
| LocalDateTime | `LocalDateTime` | `"2024-09-01T10:30:00"` | Date Time | ✓ | zoneless ↔ zoneless (mirror of param Decision A) |
| Time (offset) | `OffsetTime` | `"10:30:00+02:00"` | Time *or* Text | ⚠ | Time drops the offset (lossy) → use Text to keep it |
| DateTime (zoned) | `ZonedDateTime` | `{ "value":"2024-09-01T10:30:00", "zone":"Europe/Brussels" }` (value ZONELESS; zone = id or ±HH:MM — §4) | Date Time *or* Text | ⚠ | DateTime drops the zone → use Text to keep it |
| Duration | `Duration` | `{ "months","days","seconds","nanoseconds" }` | nested `CypherDuration` | ✓ | months/days aren't reducible to seconds — keep components |
| Point | `Point` | `{ "srid","x","y","z"? }` | nested `CypherPoint` | ✓ | |
| List | `IList` | JSON array | List of `<element type>` | ✓ | element type per §3; heterogeneous list → List of Text — §7 |
| Map | `IDictionary` | JSON object | nested Structure (§6) | ✓ | keys the dev knows → typed; arbitrary keys → raw-JSON Text |
| Node | `INode` | node envelope (§4) | `CypherNode` / typed | ✓ | |
| Relationship | `IRelationship` | rel envelope (§4) | `CypherRelationship` / typed | ✓ | |
| Path | `IPath` | `{ "nodes":[…], "relationships":[…] }` | `CypherPath` | ✓ | rarer; alternating traversal order preserved |

`*`/`†`/`⚠` = confirm or caveat; the round-trip oracle (§9) is what turns every ✓ from *claimed* into *demonstrated*.

---

## 4. Graph envelopes

Canonical, stable JSON shapes so a Structure can always be derived:

```jsonc
// Node
{ "elementId": "4:abc…:7", "labels": ["Person","Employee"],
  "properties": { "name": "Jonas", "born": "1990-12-19" } }

// Relationship
{ "elementId": "5:abc…:11", "type": "WORKS_AT",
  "startNodeElementId": "4:abc…:7", "endNodeElementId": "4:abc…:9",
  "properties": { "since": "2024-09-01" } }

// Path
{ "nodes": [ {node}, {node}, … ], "relationships": [ {rel}, … ] }

// ZonedDateTime (offset + optional named zone, never fabricated)
{ "value": "2024-09-01T10:30:00", "zone": "Europe/Brussels" }   // value is ZONELESS; zone is separate (id or ±HH:MM)
```

`elementId` is the primary identity (string; the pre-5.0 numeric `id` is deprecated — offered as an
optional extra field only if you want it). Reusable base Structures shipped with the connector:
`CypherNode`, `CypherRelationship`, `CypherPath`, `CypherDuration`, `CypherPoint`, `CypherSummary`.

---

## 5. Null handling (the one real OutSystems limitation — state it plainly)

OutSystems value-type attributes **cannot be null** — `JSONDeserialize` maps JSON `null` (and any
missing key) to the type's default (`""`, `0`, `false`, epoch date). So a query returning `null` and
one returning `0` **deserialize identically**. This is a platform limitation, not ours; the spec:

- **Emit explicit `null`** in the JSON (don't omit the key) so the shape stays stable for sampling.
- **Document** that null-vs-default is indistinguishable after deserialize; a developer who must know
  should `RETURN x IS NULL AS xIsNull` as a companion Boolean column, or read the raw JSON.
- This is exactly the kind of silent-collapse PICASSO refuses to paper over — so we name it, loudly, in the docs rather than pretend the mapping is total.

---

## 6. Arbitrary-key maps / node properties — without Pattern B

A node's `properties` (or a bare Cypher map) can have keys the connector can't know. With B excluded:

- **Default reusable path (A):** `CypherNode.Properties` is typed as **Text holding the raw JSON**
  object. The developer either ignores it, reads keys via a second `JSONDeserialize`, or — usually —
  doesn't use `CypherNode` at all and instead defines a **typed** node (below).
- **Typed path (A or C):** the developer defines `PersonNode { ElementId Text, Labels Text List,
  Properties PersonProps }` where `PersonProps` names the properties they query. This is the normal,
  recommended shape — no name/value list anywhere.
- **Genuinely open-ended maps** stay as raw-JSON Text. That's the escape valve; it mirrors the
  parameter side's `Json` hatch. No Pattern B.

---

## 7. Type stability (the assumption, made explicit)

A static Structure requires each **column** — and each **list's elements** — to have **one type across
all rows**. Well-formed Cypher does this naturally. Where it can't be guaranteed:

- The serializer still emits correct per-row JSON (it types each value individually).
- The **sampler (§8, C1) detects and flags** a column/list whose type varies across the sample
  ("column `x`: Integer in row 1, String in row 4 — type as Text/JSON or fix the query"). That's the
  fail-loud, at design time, where it belongs.

---

## 8. Pattern C — sampling → typed Structure

Two tiers, both leaning on OutSystems' native **"Paste JSON as Structure"**:

- **C0 (free, zero tooling):** because §3–§4 JSON is clean and canonical, the developer runs the query
  once, copies one record's JSON, and uses **Paste JSON as Structure** to generate `MyRecord`. Works
  today with no extra component.
- **C1 (the engineered sweetener) — `DescribeCypherResult(Query, Params, SampleSize) : Text`:** runs
  the query, inspects up to `SampleSize` records, and returns a **type-normalized exemplar JSON** that
  makes the generated Structure *complete and correct* where a single row wouldn't:
  - **union keys across rows** so a key that's null in row 1 still appears;
  - **fill nulls with a typed placeholder** so "Paste JSON as Structure" infers the right type;
  - **pick the element type** for lists from the sample;
  - **flag polymorphic columns** (§7) instead of silently picking one type.

  C1 is the real engineering — cross-row type inference — and it's the analogue of PICASSO's layout
  derivation: turn a pile of concrete samples into one correct static shape.

---

## 9. Validation — the oracle for this half

Same discipline as PICASSO's GnuCOBOL oracle, adapted: a **round-trip test against a real Neo4j in a
container**. For every type in §3: `RETURN <literal> AS v`, assert (a) the emitted JSON matches the
documented shape, and (b) `JSONDeserialize` of that JSON into the documented Structure yields the
original value (modulo the §3/§5 documented losses, which are asserted *as* losses). Graph shapes get
a fixtures graph: create known nodes/rels/paths, query them, assert the envelopes. This is what makes
every ✓ in §3 *demonstrated*, not plausible — and it's the thing none of the three existing Forge
Neo4j connectors publish.

---

## 10. Open decisions for Jonas

1. **Result set size** — v1 materializes the whole result into `RecordsJson` (fine for bounded
   queries). Streaming/paging (a cursor + `FetchNext`) is a real future need for large graphs — defer
   or design now?
2. **`elementId` vs legacy `id`** — ship `elementId` only (recommended; `id` is deprecated), or include
   both for pre-5.0 servers?
3. **Zoned temporals default** — emit the `{value, zone}` object (richer, needs a 2-field Structure) or
   a single ISO string with offset (simpler, loses the *named* zone but keeps the offset)? Recommend
   the object; it's the only lossless choice for named zones.
4. **C1 sampler in v1** — ship `DescribeCypherResult` now, or launch with C0 (native paste) + Pattern A
   and add C1 once the runtime contract is proven? Recommend C0+A first; C1 as the fast-follow that
   becomes the headline DX feature.

---
## Note: map-key order is server-determined
The round-trip oracle observed that Neo4j returns map/object keys in a **server-determined order** (not
insertion order); the serializer faithfully passes that order through. JSON objects are unordered and
OutSystems `JSONDeserialize` maps by key name, so this is harmless — but **callers must not rely on
map-key order**.
