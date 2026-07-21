# Packaging a .NET library as an OutSystems 11 extension — a field guide

*Written 2026-07-21 from the Ariadne (Neo4j Bolt Connector) packaging effort, which took several
failed Integration Studio imports to get right. Everything here was measured on real assemblies, not
inferred from documentation.*

*Scope: this is about **C# assemblies inside a single extension** — which DLLs go in the `.xif`, what
versions they carry, and why Integration Studio rejects them. It is a different level from
`docs/design/packaging.md`, which is about **OutSystems modules** (the Architecture Canvas
End-User/Core/Foundation layering between eSpaces, and Trusted eligibility). That layering is a
property of OutSystems-built apps and says nothing about C# code. The two never interact: you could
get the module layering perfect and still fail every import below.*

Applies to OutSystems 11 on the **.NET Framework** stack. The ODC / .NET 8 track is different and
most of the pain below evaporates there.

---

## STATUS — read before trusting any of this

**Verified, measured, reproducible:** everything about assembly versions and the dependency closure.
Facts 1, 2, 4 and 5 below; "The root difficulty"; "The decision procedure"; "Choosing dependency
versions"; and the PICASSO measurements at the end. That work stands.

**NOT verified — the extension has never imported into a usable state.** As of 2026-07-21 no build
produced a working action surface. The best result was tens of actions imported with every custom
type mapped to an opaque `Object`, which OutSystems cannot construct or read. So "Where to merge, and
what to leave public" is a **record of what was tried and what each attempt produced** — not
instructions. Do not follow it as a recipe. The leading hypothesis is that the import wizard is the
wrong tool entirely; see "Failure log".

---

## The five facts that determine everything

These are the constraints. Almost every packaging decision follows mechanically from them.

**1. Integration Studio enforces exact assembly versions at import time.**
If assembly A references `System.Runtime.CompilerServices.Unsafe 4.0.4.1` and you ship `6.0.0.0`,
the import fails with *"The assembly you have selected has a reference that could not be found.
Please load it from the correct file."* Browsing to the file does not help, because the file is the
wrong version — the message is misleading. It is a version mismatch, not a missing file.

**2. There is no binding-redirect mechanism. At all.**
On normal .NET Framework, fact 1 is what `<bindingRedirect>` exists to solve. In OutSystems you
cannot ship one:
- A per-DLL `MyLib.dll.config` is **inert** — the CLR reads binding redirects only from the
  application config, publisher policy, or machine.config. Integration Studio will happily copy a
  `config` resource to `bin2`; the binder ignores it. (Microsoft is explicit: plugin `.dll.config`
  honoring is not a CLR feature.)
- The module's `web.config` is **rebuilt from scratch on every deploy**, so hand edits do not survive.
- The Factory Configuration Forge component can transform `web.config` via XSLT, but it is admin- and
  environment-scoped and **cannot travel inside an `.xif`**. Also: no documented precedent for using
  it for assembly binding.

Consequence: **every assembly version in your closure must match exactly what references it.**
OutSystems' own documented answer to version conflicts is physical version alignment, not redirects.

**3. Integration Studio enumerates every type in the assembly you import — not just public ones.**
This one is expensive to learn. If you ILRepack a large closure into the assembly you import, you
will be offered *every* merged type as an action. Deselecting does not help. See "Where to merge, and
what to leave public — UNRESOLVED" below.

**4. Extensions are flat-copied into the consuming module's `bin2` folder.**
Two extensions shipping the same filename silently overwrite each other, last writer wins, and which
one wins is undefined in multi-producer graphs. So `System.Text.Json.dll` from your extension can be
replaced by a different version from someone else's.

**5. The platform no longer supplies the BCL facades.**
A Platform Server change reorganized `\plugins\database`; `System.Memory`, `System.Buffers`,
`System.Runtime.CompilerServices.Unsafe`, `System.Text.Json`, `System.Text.Encodings.Web` and
`Microsoft.Bcl.AsyncInterfaces` must now be carried by the extension. Do not assume they are present.

---

## The root difficulty: netstandard2.0 libraries on .NET Framework

Nearly every version conflict traces back to this.

A `netstandard2.0` library was compiled against the **netstandard2.0 flavour** of the BCL facades.
When you consume it from .NET Framework you must ship the **net46x flavour**, which carries
*different assembly versions* for the same file name. Example, measured from one NuGet package:

| `System.Text.Json` 10.0.10 lib folder | Assembly version |
|---|---|
| `netstandard2.0` | 10.0.0.0 |
| `net462` | 10.0.0.10 |

On a normal app this is invisible — binding redirects absorb it. Here it is fatal (facts 1 + 2).

It gets worse when **two libraries from different dependency generations** are in the same closure.
Ariadne shipped `Neo4j.Driver` 5.28.3 (an older facade generation) alongside `System.Text.Json` 8:

| Assembly | Neo4j.Driver wants | System.Text.Json wants |
|---|---|---|
| `System.Runtime.CompilerServices.Unsafe` | 4.0.4.1 | 6.0.0.0 |
| `System.Buffers` | 4.0.2.0 | 4.0.3.0 |
| `Microsoft.Bcl.AsyncInterfaces` | 6.0.0.0 | 8.0.0.0 |

Only one copy of each filename can exist in `bin2`. **This is unresolvable by package selection.**
Verified across System.Text.Json 4.7.2 / 5.0.2 / 6.0.11 / 8.0.5 / 10.0.10, in both `net472` and
`netstandard2.0` resolutions — the floor was 3 mismatches in *every* combination.

---

## The decision procedure

Do this in order. Stop as soon as you reach zero mismatches.

### Step 1 — Multi-target the source projects to `netstandard2.0;net472`

```xml
<TargetFrameworks>netstandard2.0;net472</TargetFrameworks>
<!-- lets net472 build on Linux/macOS; no-op on Windows with targeting packs -->
<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
```

`net472` is what ships (OutSystems documents 4.7.2 as the minimum; **4.6.1 is explicitly an
unsupported scenario**). Compiling against net472 makes your compile-time references match the
net46x-flavour DLLs you actually bundle, which removes the flavour skew from fact-2 territory.

Keep `netstandard2.0` in the list so `net10.0` test projects can still reference the assemblies — a
`net10.0` project cannot reference a `net472`-only one.

### Step 2 — Resolve the closure once, with a packaging project

Do **not** hand-collect DLLs. That is how Ariadne's first bundle ended up mixing the `net462` build of
`Microsoft.Bcl.AsyncInterfaces` with the `netstandard2.0` build of `System.Text.Json`. Add a code-free
project whose only job is to make NuGet produce one coherent closure:

```xml
<TargetFramework>net472</TargetFramework>
<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
```

### Step 3 — Measure the mismatches

Enumerate every shipped assembly's identity and its references, then compare demanded vs shipped.
`AutoGenerateBindingRedirects` is a convenient oracle: **each redirect MSBuild generates is one
mismatch that OutSystems cannot absorb.** Zero redirects = importable.

### Step 4 — If mismatches remain, ILRepack

If steps 1–3 leave any mismatch, no version choice will fix it (see above). Merge the closure with
`/internalize`. Nothing shared ships, so nothing can mismatch — and as a bonus you become immune to
the `bin2` collision in fact 4.

---

## Where to merge, and what to leave public — UNRESOLVED

Two constraints were observed to pull in opposite directions. Both are real. **Satisfying both did
not produce a usable extension**, which is why this section is a log rather than a recipe.

**Constraint A — types must live IN the assembly you import.** Integration Studio appears to build
its parameter mapping only from types in the imported assembly. With the DTOs in a separate
`Ariadne.Core.dll` shipped as a resource, every action taking `CypherParameter[]` was silently
dropped; only methods touching a single custom class survived.

**Constraint B — the imported assembly must expose almost nothing.** Integration Studio enumerates
every type in the assembly you import. Merging the closure in and leaving it public took the assembly
from 9 types to 1442, and the wizard offered to import all of them. Deselecting did not help.

### What was tried, and what each produced

| # | Arrangement | Observed outcome |
|---|---|---|
| 1 | Multi-DLL closure, mixed TFM flavours (hand-collected) | Import failed: "reference that could not be found", `System.Runtime.CompilerServices.Unsafe 4.0.4.1` |
| 2 | Multi-DLL, one consistent net472 closure + generated binding redirects | Same failure. Redirects are inert in O11 (fact 2) |
| 3 | Everything ILRepack-merged into the imported assembly, `/internalize` | Imported, but offered all 1442 types |
| 4 | Merge into `Ariadne.Core.dll`, import a thin `Ariadne.Extension.dll` | Only `VerifyConnectivity`/`ResetDriver` imported. **Structures folder empty**; `ConnConfig` mapped to **`Object`** |
| 5 | As 4, plus `IList<T>` → arrays on the DTOs | No change |
| 6 | As 5, plus removing an internal interface from the public DTOs | No change |
| 7 | Merge into the imported assembly, `/internalize:<excludeFile>` keeping 6 public types | Tens of actions imported, **all custom types still `Object`** |

### The leading hypothesis (untested)

`Data Type = Object` was visible in the parameter grid from attempt 4 onward and is probably the
whole story: **the "Import Actions from .NET Assembly" wizard does not build OutSystems Structures
from arbitrary .NET classes** — it maps them to opaque handles that OutSystems cannot construct or
inspect. If true, no packaging arrangement fixes it, because the wizard is not the right tool for a
rich API surface.

The conventional path for a Forge connector would then be the inverse: define the Actions and
Structures **by hand in Integration Studio**, let it generate the C# signatures with its own record
types, and implement those stubs by delegating into the merged library. **This has not been
verified** — research was commissioned but no conclusion had landed when work stopped.

---

## Failure log — how this went wrong

Recorded deliberately, because the process errors cost far more than the technical ones and will
recur on PICASSO otherwise.

**1. Guessed from declarations instead of measuring.** Attempts 5, 6 and 7 were each shipped with
confidence, based on reading type declarations and reasoning about what Integration Studio "must" be
doing. Three consecutive wrong causes: `IList<T>`, then an internal interface, then type location.
Each fix was plausible, each was announced as the fix, none was.

**2. Built the discriminating experiment far too late.** A probe assembly — one method per isolated
construct — was only built after three failed hypotheses. It should have been built the moment the
first hypothesis failed. It cost one round trip and immediately eliminated an entire class of theory.

**3. Misread the probe.** The probe showed all 12 signature shapes **listed in the wizard**, which was
reported as proof they would import. Listing is not importing — the real extension listed 7 actions
and imported them, while the wizard had happily listed shapes it would later drop. Measuring a proxy
and reporting it as the real thing.

**4. Ignored the strongest evidence, twice.** The **empty Structures folder** and the
**`Data Type: Object`** column were both visible in screenshots before the last two attempts. Both
pointed at the actual problem. Both were skimmed past in favour of a theory already in flight.

**5. Declared success on a partial signal.** "All the actions are imported again" was taken as
confirmation and written into this document as the arrangement that worked — before learning every
type had come through as `Object`. Documentation asserting success was committed for a state that had
never been verified end to end.

**6. Verified at the wrong level.** Each attempt ended with a metadata check — "exactly 6 public
types", "interfaces=0", "all 5 actions present in the DLL". All true, all irrelevant. The thing that
mattered was whether OutSystems could *use* the actions, and that was never the acceptance criterion.

### Rules that follow

- When a hypothesis fails, **build the experiment that discriminates between the remaining ones**
  before touching production code again. Do not ship a second guess.
- **Verify at the level the user cares about.** "The DLL contains the method" is not "the action
  works". Define the acceptance criterion first, in those terms.
- **Read the tool's own diagnostics before theorising.** An empty folder and a `Data Type` column
  outrank any amount of reasoning about reflection semantics.
- **Distinguish "listed" from "works"**, and say which one you measured.
- **Do not write documentation asserting success until the end-to-end outcome is confirmed.** Wrong
  documentation is worse than none — it sends the next person round the same loop with confidence.

---

## Import checklist

*Mechanical steps that were confirmed to matter. None of them made the extension usable — see STATUS.*

- **Extract the zip to a real folder first.** Browsing into a `.zip` in Explorer extracts only the
  file you click, so siblings are missing and the import misbehaves in confusing ways.
- Import the **thin** assembly; add the merged one as a **resource** with Deploy Action
  *Copy to Binaries directory*.
- Import only real operations as **Actions**. Integration Studio offers an action for every public
  method, including property accessors — if it starts with `get_` or `set_`, it is not an action.
- Data types belong as **Structures**, and you must keep the ones appearing in action signatures or
  the actions will not import.
- Do **not** ship the BCL facades OutSystems names as excluded: `netstandard.dll`, `mscorlib.dll`,
  `System.Runtime.InteropServices.RuntimeInformation.dll`, `System.Net.Http.dll`.
  `System.Runtime.CompilerServices.Unsafe.dll` *is* documented as safe to ship.
- `netstandard 2.0.0.0` may still appear as a reference if you consume a netstandard2.0 library. It is
  part of .NET Framework 4.7.2+ and resolves from the GAC. If asked, point at
  `C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\netstandard.dll`
  — **not** the NuGet `netstandard.library` package, which is a different assembly.

---

## Choosing dependency versions

Prefer the version **OutSystems' own Forge components ship**, because of fact 4. Their JWT component
ships `System.Text.Json` **8.0.5** (bumped there for CVE-2024-30105). Matching it means that if both
extensions land in one module's `bin2`, the collision is harmless.

This is also the *correct* reason to avoid System.Text.Json 10.x on O11 — not the one that looks
obvious. See the trap below.

---

## Traps that cost real time

**Assembly version does not tell you provenance.** The first Ariadne analysis concluded that
`System.Text.Json 10.0.0.0` meant "the .NET 10 in-box runtime assembly, which OutSystems cannot
load", and prescribed a downgrade. Wrong on all counts: the NuGet package ships `netstandard2.0` and
`net462` builds that load fine on .NET Framework, and its netstandard2.0 build is *also* stamped
`10.0.0.0`. The version number carries no information about where the file came from. The downgrade
was still the right move — for the unrelated reasons above. **Measure the file; never pattern-match
the version number.**

**A row where required == shipped is not a mismatch.** That same analysis flagged
`System.Text.Json 10.0.0.0 → 10.0.0.0` as a showstopper. Identical columns should have been the
tell.

**"Could not be found" usually means "wrong version", not "missing".** Selecting the file again is
futile. Read the version in the dialog and compare it to what you shipped.

**`dotnet new tool-manifest` may write to the repo root** rather than `.config/`. Move it to
`.config/dotnet-tools.json` or `dotnet tool restore` will not find it.

---

## What this means for PICASSO — measured, 2026-07-21

Good news: **PICASSO is a much easier case than Ariadne, despite being the bigger component.**

`Picasso.Core` and `Picasso.Extension` are `netstandard2.0` with a single NuGet dependency,
`System.Text.Json` 10.0.10 — and no second library from a different facade generation. That absence
is what made Ariadne hard.

Measured on the current source:

| Configuration | Mismatches |
|---|---|
| As-is (`netstandard2.0`, resolved for net472) | **1** — `System.Text.Json`: ships 10.0.0.10, `Picasso.Extension` references 10.0.0.0 |
| Multi-targeted `netstandard2.0;net472` | **0** |

So the whole fix is **Step 1 alone**. Add `net472` to `TargetFrameworks` on both projects plus
`Microsoft.NETFramework.ReferenceAssemblies`, resolve the closure with a packaging project, ship the
~10 DLLs. **No ILRepack needed.**

Two judgement calls remain, neither forced:

1. **Stay on System.Text.Json 10.0.10, or align to 8.0.5?** 10.0.10 works and needs no merge. But it
   collides in `bin2` with anything shipping 8.0.5 — including OutSystems' own JWT component — and
   which one wins is undefined. If PICASSO is expected to coexist with other Forge components,
   8.0.5 is the safer citizen. If it ships alone, 10.0.10 is fine.
2. **Merge anyway for collision immunity?** ILRepack is not needed for *correctness* here, but it
   would make PICASSO immune to fact 4 entirely. If merging, apply the one-level-down rule:
   `Picasso.Extension.dll` is already thin (11 types, 1 public — `PicassoActions`), so merge into
   `Picasso.Core.dll` and ship `Picasso.Extension.dll` unmerged.

Verify the merge before trusting it. The devbox is Linux, so a merged `net472` assembly cannot be
executed here — only its metadata inspected. Ariadne's merge is metadata-verified but *not*
runtime-tested; the plausible failure mode is System.Text.Json's reflection-based serialization
behaving differently once internalized. Run the oracle suite on Windows, or accept the risk knowingly.

**The dependency numbers above are solid. The import-surface question is not** — PICASSO's
`PicassoActions` has the same shape as Ariadne's action class (custom DTOs as parameters), so it will
almost certainly hit the same `Object`-mapping wall. **Settle how the action surface is built before
spending time on PICASSO packaging**, because the packaging is the easy half and it is already done.
