package net.osa.osenginemobile;

import android.content.Context;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CopyOnWriteArrayList;

/** Collects `alert.raised` events of every terminal while the app has an SSH connection. */
final class AlertCenter {
    static final class Alert {
        final String time, bot, message, terminal;
        Alert(String time, String bot, String message, String terminal) {
            this.time = time; this.bot = bot; this.message = message; this.terminal = terminal;
        }
    }

    interface Listener { void onAlertsChanged(boolean added); }

    private static final int LIMIT = 200;
    private static final List<Alert> alerts = new ArrayList<>();
    private static final Map<String, Thread> streams = new HashMap<>();
    private static final Map<String, String> states = new HashMap<>();
    private static final CopyOnWriteArrayList<Listener> listeners = new CopyOnWriteArrayList<>();

    private AlertCenter() { }

    static void addListener(Listener listener) { listeners.addIfAbsent(listener); }
    static void removeListener(Listener listener) { listeners.remove(listener); }

    static synchronized List<Alert> snapshot() { return new ArrayList<>(alerts); }

    static synchronized String streamStates() {
        StringBuilder text = new StringBuilder();
        for (Map.Entry<String, String> entry : states.entrySet()) {
            if (text.length() > 0) text.append("  ·  ");
            text.append(entry.getKey()).append(": ").append(entry.getValue());
        }
        return text.length() == 0 ? "поток событий не запущен" : text.toString();
    }

    /** Idempotent: starts one reconnecting event stream for the terminal. */
    static synchronized void ensureStream(Context context, String terminal) {
        Thread running = streams.get(terminal);
        if (running != null && running.isAlive()) return;
        Context app = context.getApplicationContext();
        Thread thread = new Thread(() -> run(app, terminal), "alerts-" + terminal);
        thread.setDaemon(true);
        streams.put(terminal, thread);
        thread.start();
    }

    private static void run(Context context, String terminal) {
        McpBridge bridge;
        try { bridge = new McpBridge(context); }
        catch (Exception e) { setState(terminal, "ошибка: " + e.getMessage()); return; }
        while (RemoteSsh.isConnected()) {
            try {
                setState(terminal, "подключение…");
                String command = bridge.eventCommand(context, terminal);
                String[] event = {""};
                RemoteSsh.stream(command, line -> {
                    if (line.startsWith("event:")) { event[0] = line.substring(6).trim(); return; }
                    if (!line.startsWith("data:")) return;
                    setState(terminal, "поток событий активен");
                    if (event[0].equals("alert.raised")) parse(terminal, line.substring(5).trim());
                    event[0] = "";
                });
            } catch (Exception e) {
                setState(terminal, "переподключение: " + e.getMessage());
            }
            try { Thread.sleep(5_000); }
            catch (InterruptedException e) { return; }
        }
        setState(terminal, "SSH не подключён");
    }

    private static void parse(String terminal, String json) {
        try {
            JSONObject root = new JSONObject(json);
            JSONObject payload = null;
            Iterator<String> keys = root.keys();
            while (keys.hasNext()) {
                String key = keys.next();
                if (key.equalsIgnoreCase("payload")) payload = root.optJSONObject(key);
            }
            if (payload == null) payload = root;
            add(new Alert(payload.optString("time"), payload.optString("bot_name", "VPS"),
                payload.optString("message"), terminal));
        } catch (Exception ignored) { }
    }

    private static void add(Alert alert) {
        synchronized (AlertCenter.class) {
            alerts.add(0, alert);
            while (alerts.size() > LIMIT) alerts.remove(alerts.size() - 1);
        }
        for (Listener listener : listeners) listener.onAlertsChanged(true);
    }

    private static void setState(String terminal, String state) {
        synchronized (AlertCenter.class) {
            if (state.equals(states.get(terminal))) return;
            states.put(terminal, state);
        }
        for (Listener listener : listeners) listener.onAlertsChanged(false);
    }
}
