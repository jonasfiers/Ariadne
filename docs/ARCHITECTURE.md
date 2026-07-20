# Architecture

ARIADNE mirrors the structure that made [PICASSO](https://github.com/jonasfiers/PICASSO) verifiable
outside Windows GUI tooling: a portable, testable .NET core, with the OutSystems packaging as a thin,
manual outer layer.

## Two C# assemblies

- **`Ariadne.Core`** (`netstandard2.0`) — the whole engine, with **no OutSystems dependency**: the
  parameter type-map (OutSystems value → Neo4j/Bolt type), the result serializer (Bolt result →
  canonical JSON), the driver-singleton cache and session/transaction handling, and error mapping.
  Fully unit-testable; the driver's `IDriver` / `IAsyncSession` / `IResultCursor` are interfaces, so
  logic is exercised without a live server.
- **`Ariadne.Extension`** (`netstandard2.0`) — thin Integration Studio action classes that call Core.
  Imported into Integration Studio as the **`Neo4jBolt`** extension module. Assembling the actual
  extension is a manual Windows step.

## OutSystems layering (for the packaged component)

The published component is designed to pass the OutSystems Architecture Dashboard cleanly (a
prerequisite for the Forge "Trusted" badge). References point downward only:

```
[End-User]   demo app                 (parameter form in, typed results out)
[Core]       consumer's typed wrappers (e.g. GetPeople : Person List — lives in the CONSUMING app)
[Foundation] Neo4jBoltConnector        (public Service Actions, connection config, result structures)
[Foundation] Neo4jBolt (extension)     (Ariadne.Extension → Ariadne.Core → Neo4j.Driver → Bolt)
```

The generic connector stays in Foundation; query-specific typed wrappers belong one layer up, in the
consuming app's Core — never baked into the connector.

## Validation

The correctness claim is proven by a **round-trip oracle against a real Neo4j**: bind a value of every
supported parameter type, `RETURN $p`, and assert the driver returns what was sent; and for results,
`RETURN <literal>`, assert the emitted JSON matches the documented shape and deserializes back to the
original (documented lossy cases asserted *as* lossy). This is the analogue of PICASSO's GnuCOBOL oracle.
It requires a live Neo4j (Docker/Testcontainers or a configured server) and therefore runs in CI, not in
environments without one.

## Design specifications

The full type-maps and rules (parameters, results, connection lifecycle, packaging) are maintained as
detailed specs outside this repo during development and migrate into `docs/` as each feature lands, so
this repo never documents behaviour that isn't yet built and tested.
