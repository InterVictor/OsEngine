#!/bin/bash
# перегенерировать Shim.Generated.cs (refs.json: dotnet msbuild OsEngine.csproj -t:ResolveAssemblyReferences -getItem:ReferencePath)
DIR=$(cd "$(dirname "$0")" && pwd)
FORK=$(cd "$DIR/.." && { pwd -W 2>/dev/null || pwd; })   # корень репозитория OsEngineVPS
SRC=${OSENGINE_SRC:-$FORK/project/OsEngine}
ROBOTS=${FF_ROBOTS:-D:/ff-research/robots}   # роботы лежат в репозитории стратегий
[ -f $DIR/ShimGen/refs.json ] || (cd "$SRC" && dotnet msbuild OsEngine.csproj -nologo -t:ResolveAssemblyReferences -getItem:ReferencePath > "$DIR/ShimGen/refs.json")
cd $DIR/ShimGen && dotnet build -nologo -v q 2>&1 | grep -E " error " ; dotnet run --no-build -- "$SRC" ../files.txt refs.json ../OsEngine.Headless/Shim.Generated.cs $ROBOTS/FF140.cs $ROBOTS/FF141.cs $ROBOTS/FF142.cs $ROBOTS/FF143.cs $ROBOTS/FF142Regime.cs
