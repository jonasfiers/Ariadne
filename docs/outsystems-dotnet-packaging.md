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
what to leave public" below.

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

## Where to merge, and what to leave public

**Two constraints pull in opposite directions. Both are real, and satisfying only one fails.**

**Constraint A — the types must live IN the assembly you import.** Integration Studio only builds
Structures from types in the imported assembly. With the DTOs in a separate `Ariadne.Core.dll`
shipped as a resource, the extension's Structures folder came out **empty**, `ConnConfig` was mapped
to an opaque **`Object`** parameter, and every action taking `CypherParameter[]` was silently
dropped. Only `VerifyConnectivity` and `ResetDriver` survived, because a lone opaque param is
tolerated where an array of one is not.

**Constraint B — the imported assembly must expose almost nothing.** Integration Studio enumerates
every type in the assembly you import, not just exported ones. Merging the closure in and leaving it
public took `Ariadne.Extension.dll` from 9 types to 1442, and the wizard offered to import all of
them. Deselecting did not help.

**The resolution is selective internalization.** Merge everything into the assembly you import, and
use ILRepack's exclude file to keep public *only* the action class and the DTOs its signatures name:

```bash
dotnet ilrepack /out:"$STAGE/Ariadne.Extension.dll" \
  /lib:"$FACADES" /lib:"$FACADES/Facades" /lib:"$RAW" \
  "/targetplatform:v4,$FACADES" \
  "/internalize:packaging/internalize-exclude.txt" \
  "$RAW/Ariadne.Extension.dll" "$RAW/Ariadne.Core.dll" "$RAW/Neo4j.Driver.dll" ...
```

with `internalize-exclude.txt` holding one regex per line:

```
^Ariadne\.Extension\.Neo4jBoltActions$
^Ariadne\.Core\.Connection\.ConnConfig$
^Ariadne\.Core\.Parameters\.CypherParameter$
...
```

Result: one DLL, 6 public types, ~1440 internal. Both constraints satisfied.

Gotchas:

- `/targetplatform:v4,<dir>` needs the explicit directory on Linux/macOS, or ILRepack looks for
  .NET Framework inside the .NET SDK and fails.
- **The exclude file is regexes only.** ILRepack compiles *every* line, so a `#` comment containing
  an unbalanced bracket crashes the run with a `Regex` constructor exception.
- Plain `/internalize` with no exclude file **does not internalize as much as you expect**. ILRepack
  refuses to internalize types reachable from the primary assembly's public API — `Ariadne.Core`
  exposed `IDriver`/`IAuthToken`/`IResultSummary`, so the entire `Neo4j.Driver` public surface stayed
  public (127 public types). Narrow the public API or use the exclude file.

## Public boundary types implement nothing and inherit nothing

`IScalarCarrier` was an **internal** interface implemented by three **public** DTOs. A public type
still advertises its interfaces through reflection, and that is a real hazard on this boundary — the
model classes mirror OutSystems Structures, which cannot inherit either. Replaced with an internal
`readonly struct` holding the same properties, built via a static factory at each call site.

Verify at metadata level, not by reading source: every boundary type should report `interfaces=0`.

## Diagnose by probe assembly, not by inspection

When Integration Studio silently drops a method it tells you nothing about why. Build a throwaway DLL
with one method per isolated construct — `string[]` in, `out string[]`, array of class, `List<T>`,
class as `out`, nullable members, `byte[]`, parameter arity, then the exact failing signature — and
run the wizard on it. Whichever methods behave differently name the cause.

Three hypotheses were burned guessing from type declarations before doing this. The probe settled the
question in one round trip. Build it as soon as the first hypothesis fails.

**But read the probe correctly:** the wizard *listing* a method is not proof it will *import*. The
probe listed all 12 shapes while the real extension imported only 7 actions. Confirm against the
imported extension tree, not the wizard.

## Import checklist

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
