#!/bin/bash
# Builds the headless linux-x64 OsEngine server package from the OsEngine fork and puts it where the
# Robots.VPS client picks it up ("Update build" / "Deploy / repair server"):
#   <fork>/OsEngineVPS/bin/Debug/VpsServer/osengine-headless-linux-x64.tgz (the OsEngineVPS program uploads it)
#
# Steps (the same as the manual deploys in docs/SERVER_SETUP.md):
#   1. PathFix  — Linux-safe copies of the server sources into OsEngine.Headless/core-src
#                 (NOT headless/core-src: that folder is stale and not compiled)
#   2. ShimGen  — regenerate Shim.Generated.cs (must report fullCompileErrors=0)
#   3. publish  — dotnet publish -c Release -r linux-x64 --self-contained -> publish_out
#   4. package  — tar.gz of publish_out into the client's VpsServer folder, previous package kept as .prev
#
# Usage: bash build-package.sh
#   OSENGINE_SRC     OsEngine project folder (default <репозиторий>/project/OsEngine)
#   VPS_PACKAGE_DIR  where to put the package (default <fork>/OsEngineVPS/bin/Debug/VpsServer)
#
# New server source files must be listed in files.txt to be included.

set -euo pipefail

DIR=$(cd "$(dirname "$0")" && pwd)
FORK=$(cd "$DIR/.." && { pwd -W 2>/dev/null || pwd; })   # корень репозитория OsEngineVPS
SRC=${OSENGINE_SRC:-$FORK/project/OsEngine}
OUT=${VPS_PACKAGE_DIR:-$SRC/../../OsEngineVPS/bin/Debug/VpsServer}
PACKAGE="$OUT/osengine-headless-linux-x64.tgz"

echo "== 0/4 references of OsEngine.csproj (ShimGen/refs.json)"
# refreshed every time: a new NuGet package in the client (e.g. SSH.NET) would otherwise break the ShimGen check
(cd "$SRC" && dotnet msbuild OsEngine.csproj -t:ResolveAssemblyReferences -getItem:ReferencePath -nologo) > "$DIR/ShimGen/refs.json.new"
grep -q '"ReferencePath"' "$DIR/ShimGen/refs.json.new" || { echo "FAIL could not resolve the references of OsEngine.csproj"; exit 1; }
mv -f "$DIR/ShimGen/refs.json.new" "$DIR/ShimGen/refs.json"

echo "== 1/4 PathFix ($SRC)"
(cd "$DIR/PathFix" && dotnet run -- "$SRC" ../files.txt ../OsEngine.Headless/core-src ../pathfix-report.tsv) | tail -1

echo "== 2/4 ShimGen"
GEN=$(cd "$DIR" && OSENGINE_SRC="$SRC" bash gen.sh 2>&1)
grep -i "fullCompileErrors" <<< "$GEN" || true
grep -q "fullCompileErrors=0" <<< "$GEN" || { echo "$GEN" | tail -20; echo "FAIL ShimGen reported compile errors"; exit 1; }

echo "== 3/4 dotnet publish linux-x64"
rm -rf "$DIR/publish_out"
(cd "$DIR/OsEngine.Headless" && dotnet publish -c Release -r linux-x64 --self-contained -o "$DIR/publish_out" -nologo -v q) \
    | grep -E " error |warning CS1066" || true
[ -f "$DIR/publish_out/OsEngine.dll" ] || { echo "FAIL publish produced no OsEngine.dll"; exit 1; }

echo "== 4/4 package -> $PACKAGE"
mkdir -p "$OUT"
[ -f "$PACKAGE" ] && mv -f "$PACKAGE" "$PACKAGE.prev"
# --force-local: Git Bash tar reads "D:" as a remote host otherwise
tar --force-local -czf "$PACKAGE" -C "$DIR/publish_out" .
SHA=$(sha256sum "$PACKAGE" | cut -c1-64)
SIZE=$(du -m "$PACKAGE" | cut -f1)

echo "OK package $SIZE MB, build ${SHA:0:8} (sha256 $SHA)"
echo "   Robots.VPS -> VPS window -> \"Update build\" installs it on the running terminals."
