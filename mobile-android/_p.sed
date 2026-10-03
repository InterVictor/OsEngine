s|^                    if (size > 0) result.diskPercent = 100.0 \* number(parts\[1\]) / size;|                    if (size > 0) result.diskPercent = 100.0 * number(parts[1]) / size;\n                    result.diskTotal = size;|
s|^        result.ramTotal = memTotal;|        result.cores = cores;\n        result.ramTotal = memTotal;|
