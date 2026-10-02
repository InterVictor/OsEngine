#!/bin/bash
# Этап 0: транзитивное замыкание файлов ядра OsEngine для headless-сборки.
# Вход: seed.txt (пути относительно OsEngineSrc). Выход: core-files.props, missing.txt.
SRC=/d/OsEngine-fork/project/OsEngine
DIR=$(cd "$(dirname "$0")" && pwd)
P=$DIR/OsEngine.Headless
cp $DIR/seed.txt $DIR/files.txt
for it in $(seq 1 40); do
  { echo '<Project><ItemGroup>'; tr '/' '\' < $DIR/files.txt | awk '{print "    <Compile Include=\"$(OsEngineSrc)\\" $0 "\" Link=\"core\\" $0 "\" />"}'; echo '</ItemGroup></Project>'; } > $P/core-files.props
  (cd $P && dotnet build -nologo -v q 2>&1) | grep -E "error CS" | tr '\' '/' | sort -u > $DIR/errors.txt
  n=$(wc -l < $DIR/errors.txt)
  grep -oE '(найти тип или имя пространства имен|type or namespace name) "[A-Za-z0-9_]+"' $DIR/errors.txt | grep -oE '"[^"]+"' | tr -d '"' | sort -u > $DIR/missing.txt
  add=$(while read t; do awk -F'\t' -v t="$t" '$1==t{print $2}' $DIR/index.tsv; done < $DIR/missing.txt | grep -v '\.xaml\.cs$' | sort -u | comm -23 - <(sort -u $DIR/files.txt))
  echo "iter $it: files=$(wc -l < $DIR/files.txt) errors=$n add=$(echo -n "$add" | grep -c .)"
  [ -z "$add" ] && break
  echo "$add" >> $DIR/files.txt
done
