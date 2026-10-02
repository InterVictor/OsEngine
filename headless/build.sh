#!/bin/bash
# сборка пробного проекта, ошибки -> errors.txt, недостающие типы -> missing.txt
DIR=$(cd "$(dirname "$0")" && pwd)
(cd $DIR/OsEngine.Headless && dotnet build -nologo -v q 2>&1) | grep "error CS" | tr '\' '/' | sed -E 's#^D:/OsEngine-fork/project/OsEngine/##; s# \[D:.*##' | sort -u > $DIR/errors.txt
grep -oE '(найти тип или имя пространства имен|type or namespace name) "[A-Za-z0-9_]+"' $DIR/errors.txt | grep -oE '"[^"]+"' | tr -d '"' | sort -u > $DIR/missing.txt
echo "errors=$(wc -l < $DIR/errors.txt) missing=$(wc -l < $DIR/missing.txt)"
grep -oE "error CS[0-9]+" $DIR/errors.txt | sort | uniq -c | sort -rn | head -8
