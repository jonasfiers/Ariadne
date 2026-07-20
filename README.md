# Ariadne

**The thread through the graph** — a Neo4j connector for OutSystems, over the Bolt protocol.

> **Status: the .NET is complete and validated against a real Neo4j.** `Ariadne.Core` (parameter mapping,
> result serialization, connection lifecycle, query execution) and `Ariadne.Extension` (the Integration
> Studio action surface) are built and green — **380+ tests** — and every supported type round-trips through
> a live Neo4j with **zero disagreements** (see [How it's validated](#how-its-validated)). What's *not* here
> is the packaged extension: assembling `Ariadne.Extension` into an Integration Studio Extension, and
> building the Service Studio demo, are manual Windows-only steps — documented, not automated — exactly as
> with its sibling [PICASSO](https://github.com/jonasfiers/PICASSO).

Ariadne is the portable .NET core a Neo4j connector for OutSystems is built from — the mapping and
connection engine (`Ariadne.Core`) and the thin Integration Studio action classes that wrap it
(`Ariadne.Extension`). This repo **isn't** the packaged extension: on the Forge the published component
is named descriptively — **Neo4j Bolt Connector** — while *Ariadne* is the codename this repo carries.

*Built by [Jonas Fiers](https://github.com/jonasfiers) with Claude (Anthropic) as an implementation
collaborator — under Jonas's specification and verification throughout.*

## Why this exists

You can already reach Neo4j from OutSystems, and connectors for it already exist on the Forge. The gap
is narrower and sharper than "there's no connector":

- **The existing connectors take only server credentials and a single raw Cypher string.** As of
  July 2026, inspecting the three published Forge Neo4j connectors in Service Studio, none exposes a
  query-**parameter** input, and two of the three ship no public documentation at all. *(This rests on
  firsthand inspection — the components publish no API signatures — and is stated as an observation, not
  a criticism. They are minimal and undocumented, not broken.)*
- Concatenating values into a query string instead of binding parameters is **injection-prone**, defeats
  Neo4j's **query-plan cache** (which keys on the query string), and forces **hand-formatted, untyped**
  literals.

Ariadne's reason to exist is the part nobody else covers: **typed query parameters, typed results, and a
correct connection lifecycle** — each validated against a real Neo4j rather than assumed correct.

## Design

Three pillars:

- **Typed parameters** — a caller supplies values with explicit types (a tagged-union `CypherParameter`);
  the engine builds the exact Neo4j/Bolt type (integers, floats, temporals, byte arrays, lists, maps, and
  an arbitrary-nesting typed-JSON escape hatch) and **fails loudly** on anything it can't map, never guessing.
- **Typed results** — a query's results come back as a canonical, documented JSON shape that deserializes
  cleanly into an OutSystems structure the developer defines; the summary (counters/timings) is a typed
  structure. Every driver value — scalars, temporals, nodes, relationships, paths — has a defined shape,
  and anything with no OutSystems representation fails loud rather than being silently emitted.
- **Correct connection lifecycle** — the Neo4j driver is a process-lifetime pooled singleton (never
  created per call), keyed on connection identity with the secret hashed out of the key; read/write
  transaction routing, automatic retry of transient errors, and dev-vs-operational error mapping that
  never leaks a credential.

The correctness bar is inherited from [PICASSO](https://github.com/jonasfiers/PICASSO) (a COBOL copybook
connector for OutSystems): **fail loudly, never silently miscompute**, and prove it empirically.

## How it's validated

The headline claim — that the typed round-trip is correct — is not asserted, it's **demonstrated against a
real Neo4j**. A round-trip oracle binds a value of *every supported type*, sends it through the full stack
(`RETURN $p`), and asserts the returned JSON equals the documented canonical form; graph types (nodes,
relationships, paths) are proven by create-and-read-back; the documented lossy cases (`Decimal`→`Float`,
100 ns temporal resolution) are asserted *as* losses, not hidden.

**Result: 0 disagreements** between Ariadne's output and real Neo4j, across every type. This is the direct
analogue of PICASSO's cross-check against the GnuCOBOL compiler.

Each feature was built with an **implement-then-independently-review** workflow (a second, fresh reviewer
in a separate checkout, verifying against the live server). That loop caught and fixed several real defects
a green test suite alone missed — a `DateTime.Kind`-dependent wall-clock shift, a host-timezone parse shift,
a permanently-poisoned driver-cache entry, and a mis-classified pool-timeout among them.

## Layout

```
src/Ariadne.Core/        portable engine (netstandard2.0) — parameters, results, connection, execution
src/Ariadne.Extension/   the Integration Studio action surface (Neo4jBoltActions) wrapping Core
test/Ariadne.Core.Tests/       xunit tests (net10.0) — unit + env-gated live-Neo4j integration + the oracle
test/Ariadne.Extension.Tests/  xunit tests for the action surface
docs/                    architecture + the design specifications (parameters, results, connection, packaging)
```

### A note on the driver version

`Ariadne.Core` pins **Neo4j.Driver 5.x**, which targets `netstandard2.0` and is therefore consumable by
.NET Framework — the runtime OutSystems 11 Integration Studio extensions require. Neo4j.Driver 6.x is
.NET 8+ only; it's reserved for a future OutSystems Developer Cloud (ODC) build, which reuses the same
`Ariadne.Core` engine.

## Building and testing

```
dotnet test
```

Pure-logic layers (parameter mapping, result serialization, connection-cache mechanics, error mapping) are
unit-tested with no external dependency. The **live-Neo4j integration tests and the round-trip oracle** are
env-gated — set `NEO4J_TEST_URI`, `NEO4J_TEST_USER`, `NEO4J_TEST_PASSWORD` to run them against a server, and
they **skip cleanly** when unset, so `dotnet test` stays green in CI without a database.

## Using it from OutSystems

`Ariadne.Extension`'s `Neo4jBoltActions` is a plain class shaped the way Integration Studio expects an
extension action to look (a Boolean success result, inputs first, outputs as `out` parameters, and no
exception crossing the boundary — a failure comes back as `false` + `errorMessage`). Assembling it into an
Integration Studio Extension, and building a demo in Service Studio, are manual Windows-GUI steps and are
**not** automated in this repo. Result records travel as a JSON string that the OutSystems app deserializes
into Structures it defines — the same pattern real Forge components use for dynamic data.

## License

Licensed under the [MIT License](LICENSE).
