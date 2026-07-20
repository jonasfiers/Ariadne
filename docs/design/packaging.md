# Neo4j Bolt Connector — Packaging & Architecture-Canvas Layering

*Build to Architecture-Dashboard-clean layering from day one, so the component is Trusted-eligible
without a later restructure. Target: OutSystems 11 (Integration Studio Extension). ODC variant noted
in §7.*

Recap of why this matters: **Trusted is a human curation review run through the Architecture
Dashboard**, and **layering violations are the most common, most mechanical way to fail it.** Getting
the layer split right up front is the cheapest Trusted-eligibility insurance we can buy.

---

## 1. The layering rules the Dashboard actually enforces

The Architecture Canvas has three layers; the Dashboard flags violations of these:

- **End-User** (top) — apps/UI. May consume Core and Foundation. **Must be consumed by nothing.**
- **Core** (middle) — reusable *business/domain* services + entities. May consume Core and Foundation.
- **Foundation** (bottom) — non-business framework/library/**integration** code. Consumes Foundation only.

Enforced checks: **references point downward only** (no *upward reference*), **no cyclic references**,
End-user modules aren't consumed by anyone. An Integration Studio **Extension is Foundation by nature**.

**Key architectural insight for a *connector*:** the component we publish is itself a **Foundation
asset** — it's an integration, not business logic. The *query-specific typed actions* (`GetPeople :
Person List`) are business logic that belongs in the **consuming app's Core**, not in our package. So
our job is: ship a clean generic Foundation connector + a properly-layered **demo** that shows the
consumer where their own Core wrappers go. The demo doubles as best-practice documentation.

---

## 2. The module set (the deliverable)

| Module | Layer | Type | Responsibility | References |
|---|---|---|---|---|
| **`Neo4jBolt`** | Foundation | Integration Studio **Extension** (C#) | Wraps `Neo4j.Driver`. Primitive boundary actions: `RunCypher(query, Parameters) → (RecordsJson, Columns, Summary)`, read/write variants, `DescribeCypherResult` sampler, session/driver lifecycle. **No business logic.** Bundles the driver DLL. | *(none)* |
| **`Neo4jBoltConnector`** | Foundation | Service/Library module | The public API surface: exposes the Extension as clean **Service Actions**, owns **connection config** (URI/auth/db) + typed error handling, and publishes the result-deserialization Structures (`CypherNode`, `CypherRelationship`, `CypherPath`, `CypherDuration`, `CypherPoint`) and param-builder convenience actions. This is what customers install & reference. | `Neo4jBolt` |
| **`SampleGraph_Core`** | Core | Module *(demo — also the documented consumer pattern)* | Shows the recommended consumer shape: app Structures (`Person`, …) + **generated typed wrappers** (`GetPeople : Person List` = `RunCypher` + `JSONDeserialize(_, Person List)`). Proves where query-specific typing belongs. | `Neo4jBoltConnector` |
| **`SampleGraph`** | End-User | Demo app | Screens showing a real round-trip: a **parameterized** query form (typed params in) and a results screen (typed results out). The "demo module showcasing the use cases" Trusted wants. | `SampleGraph_Core` |

**Structure ownership (avoids duplication across the Extension boundary):**
- `CypherParameter` (+ value carriers) and `CypherSummary` **cross into C#** → defined in the **`Neo4jBolt` Extension**.
- `CypherNode`/`Relationship`/`Path`/`Duration`/`Point` are **OutSystems-side JSONDeserialize targets** that never cross into C# → defined in **`Neo4jBoltConnector`**.

---

## 3. Reference graph (downward only — Dashboard-clean)

```
   [End-User]   SampleGraph
                    │  (consumes)
                    ▼
   [Core]       SampleGraph_Core
                    │
                    ▼
   [Foundation] Neo4jBoltConnector   ← what customers reference
                    │
                    ▼
   [Foundation] Neo4jBolt (Extension) ── Neo4j.Driver.dll → Neo4j (Bolt)
```

No arrow ever points up; no cycles; `SampleGraph` is consumed by nothing. That is the entire set of
mechanical layering checks, satisfied by construction.

---

## 4. Where each earlier design piece lands

| Design piece (from the specs) | Home | Layer |
|---|---|---|
| Parameter type-map / `CypherParameter` variant | `Neo4jBolt` boundary + `Neo4jBoltConnector` builders | Foundation |
| Result JSON contract + graph envelopes | emitted by `Neo4jBolt`; Structures in `Neo4jBoltConnector` | Foundation |
| `DescribeCypherResult` sampler (C1) — runtime | `Neo4jBolt` action | Foundation |
| C1's *output* (generated typed wrapper `GetPeople`) | the **consumer's** Core (demo shows it in `SampleGraph_Core`) | Core |
| Typed wrapper pattern (typed-in/typed-out) | consumer Core — **deliberately NOT in the connector** | Core |

This is the crux: **the connector stays generic Foundation; typed, query-shaped actions live one layer
up in whoever consumes it.** Baking a `Person` wrapper into the connector would be a business concept in
a Foundation module — an anti-pattern the Dashboard/reviewer would (rightly) ding.

---

## 5. Connection & security (maps to the Trusted *security* criteria)

- **Encrypted Bolt** (`neo4j+s://` / `bolt+s://`) supported and documented as the default for anything
  non-local; **authentication required** (no anonymous default).
- **Credentials never hard-coded** — connection URI/user/password/database via **Site Properties** (or a
  secured configuration store), owned by `Neo4jBoltConnector`.
- **No unauthenticated REST proxy exposed.** We expose **Service Actions**, not a public REST endpoint —
  so the "exposed REST must enforce TLS + auth" rule is satisfied by not opening one. Document this
  explicitly (reviewers look for it).
- **Driver lifecycle**: one pooled `IDriver` per configuration (it's thread-safe and pools internally);
  sessions are short-lived per action. No connection leak, no per-call driver construction.

---

## 6. Trusted-criteria → how this packaging satisfies each

| Criterion | Satisfied by |
|---|---|
| **Component information** | Clear names (above), long/short descriptions, **semantic versioning + changelog**, correct category (Connectors/Integrations) + tags (neo4j, graph, cypher, bolt). |
| **Installation** | Installs cleanly; **documents** Neo4j connection setup (Site Properties) and that the Extension bundles the driver DLL; no manual server steps beyond config. |
| **Functionality** | Delivers the two things nobody else does — **typed parameters** + **typed results** — and ships `SampleGraph` as the live demo. |
| **Best practices / architecture** | The §2–§3 layer split → **Architecture Dashboard passes** (no upward/cyclic refs). |
| **Security** | §5. |

---

## 7. Open items / notes

1. **O11-first.** This plan is Integration Studio (O11). The **ODC** variant (External Libraries SDK,
   NuGet-packaged) restructures the mechanics but the **same Canvas concept** applies (Library vs App).
   Parallel track, per the standing open item — not this deliverable.
2. **Component name** is yours to pick — `Neo4jBolt*` here is a placeholder. (PICASSO has a name; this
   one will want one too.)
3. **Two demo screens or one?** Recommend two — a read/browse screen *and* a write/parameter form — so
   the demo proves both param binding and result typing. Small extra effort, much stronger showcase.
4. **Trusted is lagging, not a gate** (from the badge research): build to this substance, **publish,
   accrue real installs, *then* request the badge**. The layering here is what makes that request
   pass on the first review instead of bouncing on a mechanical violation.
