# AGENTS.md — mobile-android (OsEngine Mobile)

Android-клиент для управления терминалами OsEngine на VPS (по SSH, как окно «VPS» в OsEngine).
Подпроект репозитория форка `D:\OsEngine-fork`, ветка `osengine-vps` (основная; `feature/robots-vps` — прежняя, не обновляется).

## Git

- Версии сохраняются в git этого репозитория (`D:\OsEngine-fork`), папка `mobile-android/`.
- `git commit` / `git push` — только с разрешения пользователя (как во всём проекте). Когда работа проверена,
  предложи пользователю коммит; один коммит — одна законченная доработка, сообщение по-английски,
  подробности — в `DEVELOPMENT_LOG.md`.
- Не коммитить: `.gradle/`, `build/`, `app/build/`, `local.properties`, `.idea/`, `*.iml` (уже в `.gitignore`),
  а также APK/AAB, keystore (`*.jks`, `*.keystore`), любые пароли, ключи SSH, `mcp.key`, адреса с паролями.
- Коммитить: исходники `app/src`, файлы Gradle и wrapper (`gradlew`, `gradlew.bat`, `gradle/wrapper/*`),
  документацию (`*.md`) и скриншоты проверки экранов в `review/`.
- Перед коммитом: `git status mobile-android` — в списке не должно быть ничего из запрещённого выше.

## Сервер

- VPS — отладочный, связь с биржей только на чтение. Выкладка серверной части OsEngine — только через
  `osengine-update.sh` (или «Update build» в окне VPS), не вручную.
- Кириллицу в запросах к MCP передавать файлом UTF-8 или через stdin, не аргументом командной строки Windows.
