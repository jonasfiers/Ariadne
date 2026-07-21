# Ariadne — OutSystems 11 Integration Studio extension bundle

Built by `packaging/build-bundle.sh` from the `Ariadne.Package` packaging project. Every
assembly here comes from a single NuGet resolution for **net472**, so the closure is internally
consistent — unlike the earlier hand-collected bundle, which mixed netstandard2.0 and net462
flavours of the same package generation and produced repeated "Missing Assembly Reference"
prompts in Integration Studio.

## Target

- **.NET Framework 4.7.2** assemblies, running on the OutSystems 11 **.NET Framework 4.8** stack.
  (OutSystems documents 4.7.2 as the minimum; 4.6.1 is explicitly an unsupported scenario.)
- `Neo4j.Driver` is pinned to the **5.x** line because it targets netstandard2.0. 6.x is net8.0+
  and is reserved for the future ODC track.
- `System.Text.Json` is pinned to **8.0.5** — matching the version OutSystems' own JWT Forge
  component ships. See "Why not a newer System.Text.Json" below.

## Importing

Import `Ariadne.Extension.dll` in Integration Studio. Add the remaining DLLs as **resources** with
Deploy Action **"Copy to Binaries directory"**.

Do **not** add BCL facades that OutSystems documents as excluded — `netstandard.dll`,
`mscorlib.dll`, `System.Runtime.InteropServices.RuntimeInformation.dll`, `System.Net.Http.dll`.
None of them are in this bundle. (`System.Runtime.CompilerServices.Unsafe.dll` *is* included;
OutSystems documents that one as safe to ship.)

## Known residual issue: three assembly version skews

`Neo4j.Driver` and `System.Text.Json` were compiled against different versions of three shared
facades. Only one copy of each filename can exist in a module's `bin2` folder, so this cannot be
resolved by package selection — it is structural:

| Assembly | Neo4j.Driver wants | System.Text.Json wants | Shipped |
|---|---|---|---|
| `System.Buffers` | 4.0.2.0 | 4.0.3.0 | 4.0.3.0 |
| `Microsoft.Bcl.AsyncInterfaces` | 6.0.0.0 | 8.0.0.0 | 8.0.0.0 |
| `System.Runtime.CompilerServices.Unsafe` | 4.0.4.1 | 6.0.0.0 | 6.0.0.0 |

On a normal .NET Framework application these are fixed by binding redirects. **OutSystems has no
supported mechanism for shipping them:**

- A per-DLL `.config` file is **not** read by the CLR for binding. Integration Studio will happily
  copy one to `bin2`; the binder ignores it. `required-binding-redirects.config.reference` in this
  bundle is included for reference only — it is **not** a working fix.
- The module's `web.config` is rebuilt from scratch on every deploy, so hand edits do not survive.
- The Factory Configuration Forge component can transform `web.config` via XSLT, but it is
  admin- and environment-scoped and cannot be shipped inside an `.xif`.

**Before relying on this bundle**, confirm whether the generated module `web.config` already
supplies these redirects. On a front-end:

```
C:\Program Files\OutSystems\Platform Server\running\<Module>.<hash>\web.config
```

Look for `<runtime><assemblyBinding>`. If the platform does not supply them, the fallback is to
ILRepack/internalize the dependency closure into `Ariadne.Core` so no shared facade assemblies are
shipped at all.

## Why not a newer System.Text.Json

An earlier analysis concluded that `System.Text.Json` 10.x was unusable because its assembly
version `10.0.0.0` meant "the .NET 10 in-box runtime assembly", which OutSystems could not load.
**That reasoning was incorrect** — the NuGet package ships netstandard2.0 and net462 builds that
load fine on .NET Framework, and `10.0.0.0` is simply the assembly version stamped on the
out-of-band package.

The real reasons to prefer 8.0.5 are the ones above: its transitive closure lines up with what
`Neo4j.Driver` 5.28.3 was compiled against (`System.Memory` 4.0.1.2,
`System.Threading.Tasks.Extensions` 4.2.0.1, `System.IO.Pipelines` 8.0.0.0 all match exactly,
where a 10.x closure matches none of them), and it matches the OutSystems ecosystem version.

Full analysis: `ariadne-reconciliation.md`.
