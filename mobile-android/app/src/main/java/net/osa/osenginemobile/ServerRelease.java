package net.osa.osenginemobile;

import java.util.ArrayList;
import java.util.List;

/**
 * Answer of "osengine-release check" on the VPS: the build installed in every terminal and the newest signed release.
 * Lines: INSTALLED name version state / LATEST version date note / STATE name current|update / RESULT ... / ERROR text.
 */
final class ServerRelease {
    static final class Terminal {
        String name;
        String version = "—";
        String state = "";
        boolean needsUpdate;
    }

    final List<Terminal> terminals = new ArrayList<>();
    String latestVersion;
    String latestDate = "";
    String latestNote = "";
    String error;
    boolean updateAvailable;

    static ServerRelease parse(String output) {
        ServerRelease release = new ServerRelease();
        for (String raw : output.split("\n")) {
            String line = raw.trim();
            if (line.startsWith("INSTALLED ")) {
                String[] parts = line.split(" ", 4);
                Terminal terminal = new Terminal();
                terminal.name = parts.length > 1 ? parts[1] : "?";
                terminal.version = parts.length > 2 && !"none".equals(parts[2]) ? parts[2] : "—";
                terminal.state = parts.length > 3 ? parts[3] : "";
                release.terminals.add(terminal);
            } else if (line.startsWith("LATEST ")) {
                String[] parts = line.split(" ", 4);
                release.latestVersion = parts.length > 1 ? parts[1] : null;
                release.latestDate = parts.length > 2 ? parts[2] : "";
                release.latestNote = parts.length > 3 ? parts[3] : "";
            } else if (line.startsWith("STATE ")) {
                String[] parts = line.split(" ", 3);
                if (parts.length > 2) {
                    for (Terminal terminal : release.terminals) {
                        if (terminal.name.equals(parts[1])) terminal.needsUpdate = "update".equals(parts[2]);
                    }
                }
            } else if (line.startsWith("RESULT ")) {
                release.updateAvailable = line.endsWith("update-available");
            } else if (line.startsWith("ERROR ")) {
                release.error = line.substring(6);
            }
        }
        // the main terminal first, the others alphabetically (the same order as on the terminals screen)
        release.terminals.sort((a, b) -> {
            if (a.name.equals("main") != b.name.equals("main")) return a.name.equals("main") ? -1 : 1;
            return a.name.compareToIgnoreCase(b.name);
        });
        return release;
    }

    /** "2026-10-02T13:09:22Z" -> "02.10.2026 13:09" (UTC as sent); anything else is returned as it is. */
    static String shortDate(String iso) {
        if (iso == null || iso.length() < 16 || iso.charAt(4) != '-') return iso == null ? "" : iso;
        return iso.substring(8, 10) + "." + iso.substring(5, 7) + "." + iso.substring(0, 4)
            + " " + iso.substring(11, 16) + " UTC";
    }

    /** Terminal names that the update would restart. */
    List<String> terminalsToUpdate() {
        List<String> names = new ArrayList<>();
        for (Terminal terminal : terminals) if (terminal.needsUpdate) names.add(terminal.name);
        return names;
    }

    /** Progress of "osengine-release status": finished once the log holds the DONE line. */
    static final class Progress {
        boolean running;
        boolean finished;
        boolean ok;
        final List<String> lines = new ArrayList<>();

        static Progress parse(String output) {
            Progress progress = new Progress();
            for (String raw : output.split("\n")) {
                String line = raw.trim();
                if (line.isEmpty()) continue;
                if (line.equals("RUNNING")) { progress.running = true; continue; }
                if (line.equals("IDLE") || line.startsWith("LASTRESULT ")) continue;
                if (line.equals("DONE ok")) { progress.finished = true; progress.ok = true; }
                else if (line.equals("DONE fail")) { progress.finished = true; progress.ok = false; }
                String friendly = friendly(line);
                if (friendly != null) progress.lines.add(friendly);
            }
            return progress;
        }
    }

    /** Server log line -> short Russian line for the screen (null = noise). */
    static String friendly(String line) {
        String text = line;
        String terminal = "";
        if (text.startsWith("[")) {
            int close = text.indexOf(']');
            if (close > 0) {
                terminal = text.substring(1, close);
                text = text.substring(close + 1).trim();
            }
        }
        if (text.equals("DONE")) return null;
        if (text.equals("DONE ok")) return "Готово";
        if (text.equals("DONE fail")) return "Обновление не выполнено";
        if (text.startsWith("STEP 1/3")) return "Поиск новой версии…";
        if (text.startsWith("STEP 2/3")) return "Загрузка и проверка подписи…";
        if (text.startsWith("STEP 3/3")) return "Обновление терминалов по очереди…";
        if (text.startsWith("STEP ") && terminal.isEmpty()) {
            return text.contains("[") ? "Терминал " + text.substring(text.indexOf('[') + 1, text.indexOf(']')) + "…" : null;
        }
        if (text.startsWith("OK signature is valid")) return "✓ Подпись верна";
        if (text.startsWith("OK server-")) {
            int space = text.indexOf(' ', 3);
            return "✓ Найдена версия " + text.substring(10, space < 0 ? text.length() : space);
        }
        if (!terminal.isEmpty()) {
            if (text.startsWith("STEP 2/4")) return terminal + ": распаковка новой версии…";
            if (text.startsWith("STEP 3/4")) return terminal + ": переключение и запуск…";
            if (text.startsWith("STEP 4/4")) return terminal + ": ожидание ответа терминала…";
            if (text.startsWith("OK MCP API answers")) return "✓ " + terminal + ": терминал ответил";
            if (text.startsWith("SKIP")) return terminal + ": уже на этой версии";
            if (text.startsWith("FAIL")) {
                if (text.contains("rolled back")) {
                    return "✗ " + terminal + ": откат выполнен, терминал снова работает на прежней версии";
                }
                if (text.contains("must run as root") || text.contains("no build installed")) {
                    return "✗ " + terminal + ": " + text.substring(4).trim();
                }
                return "✗ " + terminal + ": " + text.substring(4).trim();
            }
            if (text.startsWith("WARN") && text.contains("rolling back")) {
                return terminal + ": новая версия не отвечает — откат…";
            }
            return null; // the other WARN lines are the terminal's journal, too noisy for a phone
        }
        if (text.startsWith("FAIL")) {
            if (text.contains("the update failed")) return "✗ Обновление остановлено, остальные терминалы не тронуты";
            if (text.contains("signature is NOT valid")) return "✗ Подпись НЕ верна — пакет отклонён, ничего не изменено";
            if (text.contains("no signed server release")) return "✗ Подписанный релиз не найден (или нет связи с GitHub)";
            if (text.contains("download of the")) return "✗ Не удалось скачать пакет с GitHub";
            if (text.contains("checksum")) return "✗ Контрольная сумма пакета не совпала с версией релиза";
            if (text.contains("no terminal to update")) return "✗ Нет терминала для обновления";
            return "✗ " + text.substring(4).trim();
        }
        return null;
    }
}
