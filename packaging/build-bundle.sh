#!/usr/bin/env bash
#
# Builds the OutSystems 11 Integration Studio extension bundle.
#
# Produces packaging/out/ariadne-extension-bundle.zip containing one internally consistent
# .NET Framework (net472) dependency closure, plus a README documenting the residual assembly
# version skew that Integration Studio will ask about.
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG_PROJ="$REPO_ROOT/packaging/Ariadne.Package/Ariadne.Package.csproj"
STAGE="$REPO_ROOT/packaging/out/bundle"
ZIP="$REPO_ROOT/packaging/out/ariadne-extension-bundle.zip"

echo "==> Building packaging closure (net472, Release)"
rm -rf "$REPO_ROOT/packaging/out"
mkdir -p "$STAGE"
dotnet build "$PKG_PROJ" -c Release --nologo -o "$REPO_ROOT/packaging/out/raw"

RAW="$REPO_ROOT/packaging/out/raw"

# ---------------------------------------------------------------------------
# Merge everything into ONE assembly.
#
# Why: Neo4j.Driver 5.28.3 and System.Text.Json were compiled against different
# generations of the shared BCL facades. Measured, irreconcilably:
#
#   System.Runtime.CompilerServices.Unsafe  Neo4j-gen wants 4.0.4.1  STJ wants 6.0.0.0
#   System.Buffers                          Neo4j-gen wants 4.0.2.0  rest wants 4.0.3.0
#   Microsoft.Bcl.AsyncInterfaces           Neo4j-gen wants 6.0.0.0  STJ wants 8.0.0.0
#
# Only one copy of each filename can exist in a module's bin2, and Integration
# Studio enforces EXACT assembly versions at import time - it rejects a 6.0.0.0
# where 4.0.4.1 is referenced, and offers no binding-redirect mechanism to bridge
# the gap. No combination of package versions or TFM flavours removes this: it was
# verified against System.Text.Json 4.7.2 / 5.0.2 / 6.0.11 / 8.0.5 / 10.0.10, in
# both net472 and netstandard2.0 resolutions. The floor is 3 mismatches.
#
# Merging with /internalize collapses the whole closure into one assembly, so no
# shared facade assemblies ship at all and there is nothing left to mismatch.
# It also makes the extension immune to bin2 filename collisions with other Forge
# components (flat copy, last writer wins).
# ---------------------------------------------------------------------------
#
# IMPORTANT - what gets merged into WHAT.
#
# The closure is merged into Ariadne.Core.dll, NOT into Ariadne.Extension.dll.
# Ariadne.Extension.dll stays thin and unmerged (9 types, 1 public: Neo4jBoltActions)
# and is the assembly you point Integration Studio at. Ariadne.Core.dll ships beside
# it as a resource.
#
# Why: Integration Studio enumerates every type in the assembly you import, not just
# the exported ones. Merging the closure INTO Ariadne.Extension.dll took it from 9
# types to 1442, and Integration Studio then tried to import all of them - selecting
# only the 5 actions did not help. Keeping the imported assembly thin keeps the import
# surface to exactly the action class.
#
echo "==> Merging dependency closure into Ariadne.Core.dll"
dotnet tool restore >/dev/null
FACADES="$(ls -d "$HOME"/.nuget/packages/microsoft.netframework.referenceassemblies.net472/*/build/.NETFramework/v4.7.2 | head -1)"

# Primary assembly must come first; its public types stay public, the rest are internalized.
# Primary is Ariadne.Core so ConnConfig / CypherParameter / CypherSummary etc. remain public -
# Ariadne.Extension's signatures need them - and the output keeps the name and version
# Ariadne.Extension.dll already references (Ariadne.Core 1.0.0.0).
dotnet ilrepack \
  /out:"$STAGE/Ariadne.Core.dll" \
  /lib:"$FACADES" /lib:"$FACADES/Facades" /lib:"$RAW" \
  "/targetplatform:v4,$FACADES" \
  /internalize \
  "$RAW/Ariadne.Core.dll" \
  "$RAW/Neo4j.Driver.dll" \
  "$RAW/System.Text.Json.dll" \
  "$RAW/System.Text.Encodings.Web.dll" \
  "$RAW/System.Buffers.dll" \
  "$RAW/System.Memory.dll" \
  "$RAW/System.Numerics.Vectors.dll" \
  "$RAW/System.Runtime.CompilerServices.Unsafe.dll" \
  "$RAW/System.Threading.Tasks.Extensions.dll" \
  "$RAW/System.ValueTuple.dll" \
  "$RAW/Microsoft.Bcl.AsyncInterfaces.dll" \
  "$RAW/System.IO.Pipelines.dll"

rm -f "$STAGE/Ariadne.Core.pdb"

# The thin action-surface assembly, shipped as-is. This is what you import.
cp "$RAW/Ariadne.Extension.dll" "$STAGE/Ariadne.Extension.dll"

cp "$REPO_ROOT/packaging/BUNDLE-README.md" "$STAGE/README.md"

echo "==> Bundle contents:"
ls -1 "$STAGE"

echo "==> Zipping"
if command -v zip >/dev/null 2>&1; then
  ( cd "$STAGE" && zip -q -r "$ZIP" . )
else
  # zip(1) isn't installed everywhere (it isn't on the devbox); stdlib does the job.
  python3 -c '
import os, sys, zipfile
stage, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for name in sorted(os.listdir(stage)):
        z.write(os.path.join(stage, name), name)
' "$STAGE" "$ZIP"
fi

echo
echo "Bundle written to: $ZIP"
echo "Assemblies: $(find "$STAGE" -name '*.dll' | wc -l)  (expected: 2)"
