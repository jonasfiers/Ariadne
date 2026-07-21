# Ariadne — OutSystems 11 Integration Studio extension bundle

**Two assemblies, and the distinction matters:**

| File | Role |
|---|---|
| `Ariadne.Extension.dll` | **The one you import.** Thin: 9 types, exactly 1 public (`Neo4jBoltActions`). |
| `Ariadne.Core.dll` | **A resource, not an import.** Neo4j.Driver, System.Text.Json and the whole BCL facade closure are merged into it (ILRepack `/internalize`). |

## Importing

1. Extract this zip to a real folder. **Do not browse into the .zip from Explorer** —
   Windows extracts only the file you click, and the import will misbehave.
2. In Integration Studio, import **`Ariadne.Extension.dll`**.
3. Add **`Ariadne.Core.dll`** as a *resource* with Deploy Action
   **"Copy to Binaries directory"**. Do not import it as an assembly.

There are no other DLLs, and no assembly versions to be prompted about.

### Why the split

Integration Studio enumerates **every** type in the assembly you import, not just the
public ones. An earlier build merged the whole closure directly into
`Ariadne.Extension.dll`, taking it from 9 types to 1442 — and Integration Studio then
tried to import all of them. Selecting only the 5 actions did not help.

Keeping the imported assembly thin holds the import surface to exactly the action class,
while the merge (which is what removes the version conflicts) happens one level down in
`Ariadne.Core.dll`, which Integration Studio never enumerates.

The exposed action surface is `Ariadne.Extension.Neo4jBoltActions`:

| Action | Purpose |
|---|---|
| `RunCypherRead` | Read query, returns the record envelope as JSON |
| `RunCypherWrite` | Write query, returns JSON + `CypherSummary` counters |
| `RunCypherAutoCommit` | Auto-commit query (for `PERIODIC COMMIT` etc.) |
| `VerifyConnectivity` | Bool-success connectivity/auth probe |
| `ResetDriver` | Drops the cached driver singleton |

Plus the `CypherSummary` structure for the write counters.

## Why it is merged into one assembly

`Neo4j.Driver` 5.28.3 and `System.Text.Json` were compiled against different
generations of the shared BCL facades. Measured across the closure:

| Assembly | Neo4j.Driver generation wants | System.Text.Json wants |
|---|---|---|
| `System.Runtime.CompilerServices.Unsafe` | 4.0.4.1 | 6.0.0.0 |
| `System.Buffers` | 4.0.2.0 | 4.0.3.0 |
| `Microsoft.Bcl.AsyncInterfaces` | 6.0.0.0 | 8.0.0.0 |

Only one copy of each filename can exist in a module's `bin2` folder, and
**Integration Studio enforces exact assembly versions at import time** — it rejects a
shipped `6.0.0.0` where `4.0.4.1` is referenced, which is precisely the error this
bundle's predecessor produced.

On a normal .NET Framework application this is what binding redirects are for.
OutSystems has no supported way to ship them:

- A per-DLL `.config` is not read by the CLR for assembly binding.
- The module's `web.config` is rebuilt from scratch on every deploy.
- Factory Configuration can transform `web.config`, but it is admin- and
  environment-scoped and cannot travel inside an `.xif`.

And no package selection avoids the conflict. It was verified against System.Text.Json
**4.7.2, 5.0.2, 6.0.11, 8.0.5 and 10.0.10**, in both `net472` and `netstandard2.0`
resolutions — the floor is 3 irreconcilable mismatches every time.

Merging removes the problem at the root: nothing shared is shipped, so nothing can
mismatch. It also makes the extension immune to `bin2` filename collisions with other
Forge components, where extension DLLs are flat-copied and the last writer wins.

## Build provenance

- Built by `packaging/build-bundle.sh` from the `Ariadne.Package` project.
- Assemblies compiled for **net472**, running on the OutSystems 11 **.NET Framework 4.8**
  stack. (OutSystems documents 4.7.2 as the minimum; 4.6.1 is an unsupported scenario.)
- `Neo4j.Driver` pinned to the **5.x** line — it targets netstandard2.0, so it is
  consumable from .NET Framework. 6.x is net8.0+ and is reserved for the future ODC track.
- `System.Text.Json` pinned to **8.0.5**, matching what OutSystems' own JWT Forge
  component ships.
- Validated: the full suite, including the F10 live-Neo4j round-trip oracle, passes with
  zero disagreements.

## Residual references

`Ariadne.Extension.dll` references only `mscorlib`, `System.Core` and `Ariadne.Core 1.0.0.0`
— which is the exact version shipped beside it.

The merged `Ariadne.Core.dll` references only .NET Framework assemblies:

```
mscorlib 4.0.0.0 · System 4.0.0.0 · System.Core 4.0.0.0 · System.Numerics 4.0.0.0
netstandard 2.0.0.0
```

`netstandard 2.0.0.0` comes from Neo4j.Driver being a netstandard2.0 library. It is part
of .NET Framework 4.7.2+ and normally resolves from the GAC. In the unlikely event
Integration Studio asks for it, point it at:

```
C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll
```

(not the NuGet `netstandard.library` package — that is a different assembly).

Full analysis of how this was diagnosed: `ariadne-reconciliation.md` in the repo root.
