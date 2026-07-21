# Ariadne — library for an OutSystems 11 extension

**This is a library you reference from Visual Studio. It is NOT imported into Integration Studio.**

| File | Purpose |
|---|---|
| `Ariadne.Extension.dll` | Self-contained net472 assembly. Neo4j.Driver, System.Text.Json and the whole BCL facade closure are merged in; exactly **6 types are public**. |
| `Ariadne.Extension.xml` | XML docs — gives IntelliSense on those 6 types while writing the adapter. |
| `Ariadne.Extension.pdb` | Symbols, so you can step into this from Visual Studio. |

## Do not use "Import Actions from .NET Assembly"

It will not work, and the failure is quiet rather than loud. The wizard builds OutSystems Structures
only from **web-service proxy assemblies**. For an ordinary class library it maps every custom type
to an opaque **`Object`**, and silently drops methods it cannot map at all. Confirmed by OutSystems
staff on the forums and by the wizard's own documented limitations:

> "In all other cases the import cannot assume that the objects don't have state, **it imports almost
> everything as Objects** … **avoid the automatic import in those cases where it creates a complex
> API. You can manually create the necessary actions and structures and just do a little bit of code
> to call the methods in your assembly.**"
> — João Rosado, OutSystems staff

An `Object` is legal only as an action input/output parameter. Service Studio cannot construct or
read one, so an `Object`-typed `ConnConfig` would force a consumer to chain a setter action per field
before every query.

## The workflow

1. In Integration Studio, **hand-define the Structures and Actions** mirroring the six public types
   below.
2. **Update Source Code** — generates the Visual Studio project under `Source\NET`.
3. **Retarget the generated project to .NET Framework 4.7.2.** It is created as 4.6.1, which
   OutSystems documents as an unsupported scenario, and which *cannot reference* this net472
   assembly. Do this before adding the reference.
4. Drop the three files above into the extension's `Bin` folder, then **Add Reference → Browse** to
   `Ariadne.Extension.dll`.
5. **Edit Source Code** — implement each `Mss<ActionName>` stub in `<ExtensionName>.cs` as a thin
   marshaller (sketch below).
6. Build in Visual Studio → back to Integration Studio → **1-Click Publish**.

Never change a generated signature in Visual Studio; change the action in Integration Studio and
re-run Update Source Code. Your code in `<ExtensionName>.cs` survives regeneration — the `.csproj`
may not, so re-check the 4.7.2 retarget and the reference after the first regeneration.

## The public surface

```
Ariadne.Extension.Neo4jBoltActions
    bool RunCypherRead      (ConnConfig, string query, CypherParameter[],
                             out string recordsJson, out string[] columns,
                             out CypherSummary, out string errorMessage)
    bool RunCypherWrite     (… same shape …)
    bool RunCypherAutoCommit(… same shape …)
    bool VerifyConnectivity (ConnConfig, out bool ok, out string errorMessage)
    bool ResetDriver        (ConnConfig, out string errorMessage)

Ariadne.Core.Connection.ConnConfig
Ariadne.Core.Parameters.CypherParameter        (carries CypherListElement[] / CypherMapEntry[])
Ariadne.Core.Parameters.CypherListElement
Ariadne.Core.Parameters.CypherMapEntry
Ariadne.Core.Results.CypherSummary
```

Every action returns `bool` for success, reports failure through `errorMessage`, and never lets an
exception cross the boundary — so the adapter needs no try/catch of its own.

## Adapter sketch

Illustrative; the exact generated type names come from your Integration Studio definitions.

```csharp
using Ariadne.Extension;
using Ariadne.Core.Connection;
using Ariadne.Core.Parameters;

public bool MssRunCypherRead(STConnConfigStructure ssconnection, string ssquery,
    RLCypherParameterRecordList ssparameters, out string ssrecordsJson,
    out RLTextRecordList sscolumns, out STCypherSummaryStructure sssummary,
    out string sserrorMessage)
{
    var conn = new ConnConfig {
        Uri = ssconnection.ssUri, AuthScheme = ssconnection.ssAuthScheme,
        Username = ssconnection.ssUsername, Password = ssconnection.ssPassword,
        Database = ssconnection.ssDatabase,
    };

    var parameters = new CypherParameter[ssparameters.Length];
    // … map each RCCypherParameterRecord → CypherParameter …

    var ok = new Neo4jBoltActions().RunCypherRead(
        conn, ssquery, parameters,
        out var recordsJson, out var columns, out var summary, out var error);

    ssrecordsJson  = recordsJson ?? "";      // never hand null back to the platform
    sserrorMessage = error ?? "";
    // … map columns → RLTextRecordList, summary → STCypherSummaryStructure …
    return ok;
}
```

**Never return `null`** for a Text, Binary Data or record list. Wizard-imported actions get null
checks generated; hand-written ones do not. Platform defaults are `""`, `new byte[0]`,
`#1900-01-01#`, `0`.

## Platform limits worth documenting for consumers

These are OutSystems constraints, not Ariadne ones, and they bound what can round-trip:

- **No nulls for value types.** An OutSystems Integer arrives as `0`, never absent. Ariadne's design
  already handles this: `CypherParameter.Type` is an explicit discriminator, including a `Null` tag —
  so intent is declared by the tag, not inferred from a missing value. The adapter can always
  populate the carrier.
- **Decimal is capped at 8 decimal places.**
- **Date Time range is 1900-01-01 … 3000-12-31.**

Neo4j values outside the last two will not round-trip faithfully.

## Why one merged assembly

`Neo4j.Driver` 5.28.3 and `System.Text.Json` were compiled against different generations of the
shared BCL facades:

| Assembly | Neo4j.Driver wants | System.Text.Json wants |
|---|---|---|
| `System.Runtime.CompilerServices.Unsafe` | 4.0.4.1 | 6.0.0.0 |
| `System.Buffers` | 4.0.2.0 | 4.0.3.0 |
| `Microsoft.Bcl.AsyncInterfaces` | 6.0.0.0 | 8.0.0.0 |

Only one copy of each filename can exist in a module's `bin2`, and OutSystems has **no working
binding-redirect mechanism** (`.dll.config` is inert; `web.config` is regenerated every deploy).
No package selection resolves this — verified across System.Text.Json 4.7.2 / 5.0.2 / 6.0.11 /
8.0.5 / 10.0.10 in both `net472` and `netstandard2.0` resolutions, floor of 3 mismatches every time.

Merging removes the problem at the root: no shared assemblies ship, so nothing can mismatch, and the
library is immune to `bin2` collisions with other Forge components.

## Build provenance

- Built by `packaging/build-bundle.sh`. `Neo4j.Driver` pinned to **5.28.3** (netstandard2.0; 6.x is
  net8.0+, reserved for ODC). `System.Text.Json` pinned to **8.0.5**, matching what OutSystems' own
  JWT Forge component ships.
- Full suite green including the F10 live-Neo4j round-trip oracle: **382 tests, 0 failed**.
- **Not runtime-tested after merging.** The devbox is Linux and cannot execute a net472 assembly;
  the merge is verified at metadata level only. The plausible failure mode is System.Text.Json's
  reflection-based serialization behaving differently once internalized — exercise
  `VerifyConnectivity` first, then a `RunCypherRead` returning several types.

Full analysis: `docs/outsystems-dotnet-packaging.md` in the repo.
