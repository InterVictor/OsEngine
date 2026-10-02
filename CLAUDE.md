# OsEngineVPS: система (форк OsEngine)

Язык общения и документов: русский.

Это папка **системы**: исходники OsEngine, программа OsEngineVPS (клиент серверных терминалов), мобильное приложение.
Здесь же ведётся и разработка стратегий (решение 2026-10-02: одна рабочая папка и одна ветка с исправлениями системы, чтобы не было
конфликтов слияния). Стратегии лежат в **приватном** репозитории `D:\ff-research`, доступном отсюда по ссылке `strategies/`
(junction, в git этого репозитория не входит). Репозиторий `OsEngineVPS` публичный: роботы, исследования, документы стратегий и
журнал сервера (адрес) сюда не коммитить — они коммитятся в `strategies/` (git там свой).

## Карта папок (решение 2026-10-02)

| Назначение | Папка | Что там |
|---|---|---|
| **Система** | `D:\OsEngine-fork` (этот репозиторий, рабочая ветка `osengine-vps`) | исходники OsEngine, `OsEngineVPS/`, `mobile-android/` |
| **Стратегии** | `D:\ff-research` (приватный репозиторий, ветка `server`), здесь доступен как `strategies/` | роботы FF140–FF145 (`strategies/robots`), исследования (`strategies/research`, она же `project/FF141Research`), журнал разработки `strategies/docs/FF140_DEV_LOG.md` — читать первым, журнал сервера `strategies/docs/SERVER_SETUP.md`, память Claude `strategies/claude-memory` |
| Переходная установка | `D:\OsEngine-masterBB` (без git) | сейчас рабочий терминал стратегий (релиз 2.0.1.8, данные и боты); будет заменён терминалом из этого репозитория |

## Структура

- `project/OsEngine/` — исходники OsEngine. Официальная программа плюс серверные MCP-инструменты (`MCP/Modules/RobotsApi.cs` и др.)
  и `OsEngineVps.InternalsVisibleTo.cs`.
- `OsEngineVPS/` — отдельная программа: SSH-туннель, администрирование VPS, клиент серверных терминалов (см. `OsEngineVPS/README.md`,
  `OsEngineVPS/RobotsVps/ARCHITECTURE.md`).
- `mobile-android/` — мобильное приложение (см. `mobile-android/AGENTS.md`).
- `backup/` — копии старой привязки окон VPS, не удалять без разговора с пользователем.

## Ветки

- `master` — чистое зеркало `AlexWan/OsEngine:master`, коммитов не делать, только fast-forward.
- `osengine-vps` — рабочая ветка; upstream вливается сюда.
- `feature/robots-vps` — заморожена (тег `robots-vps-before-split`).
- `pr/*` — предложения разработчикам; создавать от свежего `master`. Правка оптимизатора (`pr/optimizer-fixed-params`, PR в AlexWan
  закрыт без слияния) влита в `osengine-vps` 2026-10-02 (коммит `15a80f9`); ветка остаётся как источник для нового PR.
- Терминал стратегий: `project/OsEngine/bin/Debug` содержит скопированные из `masterBB` `Engine` и `Data` (в git не попадают) и роботы
  FF14x из `strategies/robots` (доставка — `strategies/deploy.ps1`). Правила работы со стратегиями и план — в `strategies/CLAUDE.md`.

## Не коммитить

`project/OsEngine/bin/Debug/Engine`, `Data`, `VpsData`, `VpsBackups`, `VpsServer`, файлы `.lnk`, логи сборки (`build-*.log`),
ключи и настройки коннекторов. Данные запуска стратегий (FF144, FF144Regime) в этой папке — лишь следы запуска терминала.

## Сервер и сборка серверной части

- `headless/` — сборка серверной части (Linux), см. `headless/README.md`. Переехала сюда из `D:\ff-research` 2026-10-02.
- `tools/check-upstream.sh` — проверка обновлений upstream; `tools/set-server-keys.ps1` — ввод ключей биржи на сервер через MCP.
- Журнал настройки сервера (с адресом) здесь **не хранится**: репозиторий публичный. Он остаётся в приватном `D:\ff-research\docs\SERVER_SETUP.md`.
- Ни адреса серверов, ни ключи, ни пути с именем пользователя в этот репозиторий не коммитить.

## Правила стратегий

Подключается из приватного репозитория (если папка `strategies/` на месте):

@strategies/CLAUDE.md
