# Ariadne

**The thread through the graph** — a Neo4j connector for OutSystems, over the Bolt protocol.

> **Status: early development.** This repo is being built feature by feature; it is not yet a
> functional or published component. What's here today is the scaffold plus whatever features the
> commit history shows as landed. Nothing below describes behaviour that isn't yet implemented and
> tested — see the [changelog](#status--roadmap).

Ariadne is the portable .NET core a Neo4j connector for OutSystems is built from — the mapping and
connection engine (`Ariadne.Core`) and the thin Integration Studio action classes that wrap it
(`Ariadne.Extension`). This repo **isn't** the packaged extension: assembling these DLLs into an
Integration Studio Extension, and building the demo app in Service Studio, are manual Windows-only
steps, documented but not automated here. On the Forge, the published component is named descriptively
— **Neo4j Bolt Connector** — while *Ariadne* is the codename this repo carries.

*The .NET implementation is written by Claude (Anthropic), directed and verified throughout by
[Jonas Fiers](https://github.com/jonasfiers).*

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

Three pillars, specified before implementation:

- **Typed parameters** — a caller supplies values with explicit types; the engine builds the exact
  Neo4j/Bolt types (integers, floats, temporals, points, byte arrays, lists, maps) and **fails loudly**
  on anything it can't map, never guessing.
- **Typed results** — a query's results are returned as a canonical, documented JSON shape that
  deserializes cleanly into an OutSystems structure the developer defines (or that a sampler generates).
- **Correct connection lifecycle** — the Neo4j driver is a process-lifetime pooled singleton (never
  created per call), with read/write transaction routing and automatic retry of transient errors.

The correctness bar is inherited from Ariadne's sibling, [PICASSO](https://github.com/jonasfiers/PICASSO)
(a COBOL copybook connector for OutSystems): **fail loudly, never silently miscompute**, and prove it
empirically. Here that means a round-trip oracle — bind a value of every supported type, send it to a
real Neo4j, and assert what comes back equals what went in.

## Layout

```
src/Ariadne.Core/        portable engine (netstandard2.0) — parameter/result mapping, connection lifecycle
src/Ariadne.Extension/   thin Integration Studio action classes wrapping Core (imported as "Neo4jBolt")
test/Ariadne.Core.Tests/ xunit tests (net10.0)
docs/                    architecture and design notes
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

Pure-logic layers (parameter mapping, result serialization, connection-cache mechanics) are unit-tested
with no external dependency. The end-to-end round-trip oracle needs a live Neo4j; those tests skip
automatically where none is configured and run in CI.

## Status / roadmap

Built feature by feature via an implement-then-independently-review workflow. See the commit history and
`CHANGELOG.md` (added as features land) for what's actually done and validated.

## Contributions

Requirements, scope, and validation direction by Jonas Fiers; .NET implementation by Claude (Anthropic).
Licensed under the [MIT License](LICENSE).
