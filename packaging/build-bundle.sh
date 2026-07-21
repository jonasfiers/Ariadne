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

# The packaging shim itself is scaffolding, not part of the extension.
echo "==> Staging assemblies"
for f in "$RAW"/*.dll; do
  base="$(basename "$f")"
  [ "$base" = "Ariadne.Package.dll" ] && continue
  cp "$f" "$STAGE/"
done

# Carried for reference only - see README. Renamed so nobody mistakes it for a working config.
if [ -f "$RAW/Ariadne.Package.dll.config" ]; then
  cp "$RAW/Ariadne.Package.dll.config" "$STAGE/required-binding-redirects.config.reference"
fi

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
echo "Assemblies: $(find "$STAGE" -name '*.dll' | wc -l)"
