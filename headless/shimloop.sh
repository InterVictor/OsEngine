#!/bin/bash
# Генерирует шим; если подменён класс OsEngine, которого нет в shim-allowed.txt,
# добавляет его файл в files.txt (настоящий код) и повторяет.
DIR=$(cd "$(dirname "$0")" && pwd)
FORK=$(cd "$DIR/.." && { pwd -W 2>/dev/null || pwd; })   # корень репозитория OsEngineVPS
PAT=$(grep -v '^#' $DIR/shim-allowed.txt | grep -v '^\s*$' | paste -sd'|')
for it in $(seq 1 20); do
  bash $DIR/gen.sh | grep -E " error |types=" 
  add=$(cut -f1 $DIR/OsEngine.Headless/shimmed-osengine-types.txt | tr ';' '\n' | grep -v -P "$PAT" | sort -u | comm -23 - <(sort -u $DIR/files.txt))
  echo "iter $it: add $(echo -n "$add" | grep -c .)"; [ -z "$add" ] && break
  echo "$add" | sed 's/^/  + /'
  echo "$add" >> $DIR/files.txt
done
(cd $DIR/PathFix && dotnet build -nologo -v q 2>&1 | grep -E " error "; dotnet run --no-build -- "${OSENGINE_SRC:-$FORK/project/OsEngine}" ../files.txt ../OsEngine.Headless/core-src ../pathfix-report.tsv)
{ echo '<Project><ItemGroup>'; tr '/' '\' < $DIR/files.txt | awk '{print "    <Compile Include=\"core-src\\" $0 "\" Link=\"core\\" $0 "\" />"}'; echo '</ItemGroup></Project>'; } > $DIR/OsEngine.Headless/core-files.props
bash $DIR/build.sh
