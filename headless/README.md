# headless — серверная сборка OsEngine (Linux, без окон)

Собирает из исходников `project/OsEngine` программу «Роботы Light» для запуска на Ubuntu. Исходники OsEngine **не изменяются**:
графика заменяется сгенерированными пустышками, пути с `\` переписываются в копиях. Подробности и историю решений — в
`docs/HEADLESS_PHASE0.md`.

| Что | Назначение |
|---|---|
| `OsEngine.Headless/` | проект `net10.0`; точка входа `HeadlessProgram.cs` (`--root`, `--mcp-port`, `--mcp-key-file`, `--culture`, `--check-bot`) |
| `ShimGen/` | генератор «фальшивого интерфейса» → `OsEngine.Headless/Shim.Generated.cs` |
| `PathFix/` | копии исходников с путями для Linux → `OsEngine.Headless/core-src/` |
| `files.txt` | какие файлы OsEngine входят в серверную сборку (его же использует `tools/check-upstream.sh`) |
| `shim-allowed.txt` | что разрешено подменять пустышками; всё остальное компилируется настоящим кодом |
| `shimloop.sh` | PathFix + генерация + сборка в цикле до нуля подмен лишнего |
| `gen.sh`, `build.sh` | только генерация / только сборка; `build-package.sh` — серверный пакет в `OsEngineVPS/bin/Debug/VpsServer` |
| `deploy/osengine.service` | юнит systemd |

Пути определяются относительно скриптов; свои значения задаются переменными `OSENGINE_SRC` (исходники OsEngine) и `FF_ROBOTS`
(папка с роботами стратегий, по умолчанию `D:/ff-research/robots` — они лежат в другом репозитории).

После обновления OsEngine: `bash headless/shimloop.sh`, просмотреть `OsEngine.Headless/shimmed-osengine-types.txt` (не подменён ли
нужный класс), затем `bash tools/check-upstream.sh` показывает, затронута ли серверная часть.

Не хранится в git (создаётся заново): `Shim.Generated.cs`, `core-files.props`, `core-src/`, `ShimGen/refs.json` (содержит пути
конкретного компьютера), отчёты PathFix, архивы откатов.
