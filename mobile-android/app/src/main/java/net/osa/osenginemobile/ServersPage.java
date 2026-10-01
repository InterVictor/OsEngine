package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Color;
import android.graphics.Typeface;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.Locale;

/** The Lite server list and connector log for one VPS terminal. */
final class ServersPage {
    interface Actions {
        void setAutoConnect(boolean enabled);
        void command(JSONObject server);
        void openSettings(JSONObject server);
    }

    private static final DateTimeFormatter TIME =
        DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm:ss", Locale.getDefault());
    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private final Actions actions;
    private final ArrayList<JSONObject> rows = new ArrayList<>();
    private final ArrayList<LinearLayout> rowViews = new ArrayList<>();
    private final ArrayList<Integer> matches = new ArrayList<>();
    private JSONArray messages = new JSONArray();
    private String logSource = "";
    private String logError;
    private String error;
    private String updatedAt = "";
    private String lastSnapshot;
    private boolean loaded;
    private boolean connected;
    private boolean autoConnect;
    private boolean busy;
    private int matchIndex;
    private EditText search;
    private TextView matchLabel;
    private TextView updatedLabel;
    private TextView logHeader;
    private CheckBox autoCheck;
    private LinearLayout list;
    private LinearLayout log;
    private ScrollView listScroll;
    private ScrollView logScroll;

    ServersPage(Activity activity, LinearLayout target, ScrollView outer,
                boolean tablet, Actions actions) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
        this.actions = actions;
    }

    void showData(JSONArray instances, JSONArray types, boolean auto,
                  JSONArray nextMessages, String source, String nextLogError) {
        String snapshot = instances.toString() + types.toString() + auto
            + nextMessages.toString() + source + nextLogError;
        boolean hadError = error != null;
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        rows.clear();
        HashSet<String> present = new HashSet<>();
        for (int i = 0; i < instances.length(); i++) {
            JSONObject item = instances.optJSONObject(i);
            if (item == null || "Optimizer".equalsIgnoreCase(item.optString("type"))) continue;
            rows.add(item);
            present.add(item.optString("type").toLowerCase(Locale.ROOT));
        }
        for (int i = 0; i < types.length(); i++) {
            String type = types.optString(i);
            if (type.isEmpty() || "Optimizer".equalsIgnoreCase(type)
                || !present.add(type.toLowerCase(Locale.ROOT))) continue;
            JSONObject item = new JSONObject();
            try { item.put("type", type).put("name", type).put("status", "Disabled"); }
            catch (Exception ignored) { continue; }
            rows.add(item);
        }
        sortPinned();
        messages = nextMessages;
        logSource = source;
        logError = nextLogError;
        autoConnect = auto;
        loaded = true;
        connected = true;
        error = null;
        lastSnapshot = snapshot;
        updatedAt = java.time.LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
        if (unchanged) {
            if (updatedLabel != null) updatedLabel.setText("Обновлено " + updatedAt);
            return;
        }
        if (hadError || list == null || log == null) render(true);
        else {
            populateList();
            populateLog();
            if (updatedLabel != null) updatedLabel.setText("Обновлено " + updatedAt);
            if (logHeader != null) logHeader.setText("Журнал подключения" +
                (logSource.isEmpty() ? "" : " · " + logSource));
            if (autoCheck != null) {
                autoCheck.setChecked(autoConnect);
                autoCheck.setEnabled(!busy);
            }
        }
    }

    void showError(String message) {
        if (message.equals(error)) return;
        error = message;
        connected = false;
        if (autoCheck != null) autoCheck.setEnabled(false);
        render(true);
    }

    void setBusy(boolean value) {
        busy = value;
        if (autoCheck != null) autoCheck.setEnabled(connected && !busy);
    }

    void render(boolean preserveScroll) {
        int scrollY = preserveScroll ? outer.getScrollY() : 0;
        int listY = preserveScroll && listScroll != null ? listScroll.getScrollY() : 0;
        int logY = preserveScroll && logScroll != null ? logScroll.getScrollY() : 0;
        String query = search == null ? "" : search.getText().toString();
        target.removeAllViews();
        list = null;
        log = null;
        listScroll = null;
        logScroll = null;
        updatedLabel = null;
        logHeader = null;
        TextView title = text("Серверы подключения", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        target.addView(title);
        if (error != null) target.addView(text(error
            + (loaded ? " · последний снимок " + updatedAt : ""), 13, R.color.text_secondary));
        else if (loaded) {
            updatedLabel = text("Обновлено " + updatedAt, 12, R.color.text_secondary);
            target.addView(updatedLabel);
        }
        LinearLayout columns = new LinearLayout(activity);
        columns.setOrientation(tablet ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        target.addView(columns);
        LinearLayout serversPanel = panel();
        LinearLayout logPanel = panel();
        LinearLayout.LayoutParams left = tablet
            ? new LinearLayout.LayoutParams(0, -2, 2)
            : new LinearLayout.LayoutParams(-1, -2);
        LinearLayout.LayoutParams right = tablet
            ? new LinearLayout.LayoutParams(0, -2, 3)
            : new LinearLayout.LayoutParams(-1, -2);
        if (tablet) right.leftMargin = dp(8);
        columns.addView(serversPanel, left);
        columns.addView(logPanel, right);
        addSearch(serversPanel, query);
        listScroll = new ScrollView(activity);
        serversPanel.addView(listScroll,
            new LinearLayout.LayoutParams(-1, dp(tablet ? 500 : 340)));
        list = panel();
        listScroll.addView(list);
        autoCheck = new CheckBox(activity);
        autoCheck.setText("Авто-развёртывание подключений");
        autoCheck.setTextColor(activity.getColor(R.color.text_primary));
        autoCheck.setChecked(autoConnect);
        autoCheck.setEnabled(loaded && connected && !busy);
        autoCheck.setOnClickListener(view -> {
            boolean requested = autoCheck.isChecked();
            autoCheck.setChecked(autoConnect);
            actions.setAutoConnect(requested);
        });
        serversPanel.addView(autoCheck);
        logHeader = text("Журнал подключения" +
            (logSource.isEmpty() ? "" : " · " + logSource), 15, R.color.text_primary);
        logPanel.addView(logHeader);
        if (tablet) {
            logScroll = new ScrollView(activity);
            logPanel.addView(logScroll, new LinearLayout.LayoutParams(-1, dp(500)));
        }
        log = panel();
        if (tablet) logScroll.addView(log);
        else logPanel.addView(log);
        addInactiveActions();
        populateList();
        populateLog();
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
        if (listScroll != null) listScroll.post(() -> listScroll.scrollTo(0, listY));
        if (logScroll != null) logScroll.post(() -> logScroll.scrollTo(0, logY));
    }

    private void addSearch(LinearLayout parent, String query) {
        LinearLayout searchRow = new LinearLayout(activity);
        searchRow.setGravity(Gravity.CENTER_VERTICAL);
        parent.addView(searchRow);
        search = new EditText(activity);
        search.setSingleLine(true);
        search.setTextSize(14);
        search.setHint("поиск...");
        search.setTextColor(activity.getColor(R.color.text_primary));
        search.setHintTextColor(activity.getColor(R.color.text_secondary));
        search.setBackgroundResource(R.drawable.input_background);
        search.setText(query);
        searchRow.addView(search, new LinearLayout.LayoutParams(0, dp(46), 1));
        TextView previous = text("‹", 22, R.color.text_primary);
        TextView next = text("›", 22, R.color.text_primary);
        previous.setGravity(Gravity.CENTER);
        next.setGravity(Gravity.CENTER);
        searchRow.addView(previous, new LinearLayout.LayoutParams(dp(34), dp(46)));
        matchLabel = text("", 12, R.color.text_secondary);
        matchLabel.setGravity(Gravity.CENTER);
        searchRow.addView(matchLabel, new LinearLayout.LayoutParams(dp(46), dp(46)));
        searchRow.addView(next, new LinearLayout.LayoutParams(dp(34), dp(46)));
        previous.setOnClickListener(view -> selectMatch(-1));
        next.setOnClickListener(view -> selectMatch(1));
        search.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                updateMatches(true);
            }
            @Override public void afterTextChanged(Editable s) { }
        });
    }

    private void populateList() {
        if (list == null) return;
        list.removeAllViews();
        rowViews.clear();
        if (!loaded && error == null) {
            list.addView(text("Ожидание серверов…", 14, R.color.text_secondary));
            return;
        }
        LinearLayout heading = new LinearLayout(activity);
        heading.addView(cell("Источник", true), new LinearLayout.LayoutParams(0, dp(42), 2));
        heading.addView(cell("Статус", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("", true), new LinearLayout.LayoutParams(dp(44), dp(42)));
        list.addView(heading);
        if (rows.isEmpty()) list.addView(text("Подключений нет", 14, R.color.text_secondary));
        for (JSONObject server : rows) {
            boolean exists = server.has("number");
            String status = server.optString("status", "Disabled");
            int color = "Connect".equalsIgnoreCase(status) ? 0xFF3CB371 : 0xFFFF7F50;
            LinearLayout row = new LinearLayout(activity);
            TextView name = cell((isPinned(server) ? "★ " : "") + server.optString("name", server.optString("type")), false);
            TextView state = cell(status, false);
            if (exists) {
                name.setBackgroundColor(color);
                state.setBackgroundColor(color);
                name.setTextColor(Color.BLACK);
                state.setTextColor(Color.BLACK);
            }
            row.addView(name, new LinearLayout.LayoutParams(0, dp(50), 2));
            row.addView(state, new LinearLayout.LayoutParams(0, dp(50), 1));
            TextView menu = cell("⋮", false);
            menu.setGravity(Gravity.CENTER);
            row.addView(menu, new LinearLayout.LayoutParams(dp(44), dp(50)));
            menu.setOnClickListener(view -> showMenu(server));
            list.addView(row);
            rowViews.add(row);
        }
        updateMatches(false);
    }

    private void populateLog() {
        if (log == null) return;
        int scrollY = logScroll == null ? 0 : logScroll.getScrollY();
        log.removeAllViews();
        if (logError != null) log.addView(text("Не удалось загрузить журнал: " + logError,
            13, R.color.text_secondary));
        if (messages.length() == 0) {
            log.addView(text(logSource.isEmpty() ? "Нет подключённого источника" : "Записей нет",
                14, R.color.text_secondary));
        } else if (tablet) {
            LinearLayout heading = new LinearLayout(activity);
            heading.addView(cell("Время", true), new LinearLayout.LayoutParams(dp(145), -2));
            heading.addView(cell("Тип", true), new LinearLayout.LayoutParams(dp(80), -2));
            heading.addView(cell("Сообщение", true), new LinearLayout.LayoutParams(0, -2, 1));
            log.addView(heading);
            for (int i = 0; i < messages.length(); i++) {
                JSONObject message = messages.optJSONObject(i);
                if (message == null) continue;
                LinearLayout row = new LinearLayout(activity);
                row.addView(cell(time(message), false), new LinearLayout.LayoutParams(dp(145), -2));
                row.addView(cell(message.optString("type"), false), new LinearLayout.LayoutParams(dp(80), -2));
                row.addView(cell(message.optString("message"), false), new LinearLayout.LayoutParams(0, -2, 1));
                log.addView(row);
            }
        } else for (int i = 0; i < messages.length(); i++) {
            JSONObject message = messages.optJSONObject(i);
            if (message == null) continue;
            LinearLayout card = panel();
            card.setPadding(dp(10), dp(7), dp(10), dp(7));
            card.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(6);
            log.addView(card, params);
            card.addView(text(time(message) + " · " + message.optString("type"),
                12, R.color.text_secondary));
            card.addView(text(message.optString("message"), 14, R.color.text_primary));
        }
        if (logScroll != null) logScroll.post(() -> logScroll.scrollTo(0, scrollY));
    }

    private void updateMatches(boolean reset) {
        matches.clear();
        if (search == null) return;
        String query = search.getText().toString().trim().toLowerCase(Locale.ROOT);
        if (!query.isEmpty()) for (int i = 0; i < rows.size(); i++) {
            if (rows.get(i).optString("name").toLowerCase(Locale.ROOT).contains(query))
                matches.add(i);
        }
        if (reset || matchIndex >= matches.size()) matchIndex = 0;
        matchLabel.setText(query.isEmpty() ? "" : matches.isEmpty() ? "0"
            : (matchIndex + 1) + "/" + matches.size());
        if (!matches.isEmpty()) highlightMatch();
        else for (LinearLayout row : rowViews) row.setAlpha(1f);
    }

    private void selectMatch(int direction) {
        if (matches.isEmpty()) return;
        matchIndex = (matchIndex + direction + matches.size()) % matches.size();
        updateMatches(false);
    }

    private void highlightMatch() {
        int index = matches.get(matchIndex);
        for (int i = 0; i < rowViews.size(); i++) rowViews.get(i).setAlpha(i == index ? 1f : .7f);
        View row = rowViews.get(index);
        if (listScroll != null) listScroll.post(() -> listScroll.smoothScrollTo(0, row.getTop()));
        else outer.post(() -> outer.smoothScrollTo(0, row.getTop() + list.getTop()));
    }

    // The VPS API has no pin storage, so pinned connector types are kept on this device only.
    private static final String PINS = "server_pins";

    private java.util.Set<String> pins() {
        return new HashSet<>(activity.getSharedPreferences(PINS, android.content.Context.MODE_PRIVATE)
            .getStringSet("types", new HashSet<>()));
    }

    private boolean isPinned(JSONObject server) {
        return pins().contains(server.optString("type").toLowerCase(Locale.ROOT));
    }

    private void sortPinned() {
        java.util.Set<String> pinned = pins();
        rows.sort((a, b) -> Boolean.compare(
            !pinned.contains(a.optString("type").toLowerCase(Locale.ROOT)),
            !pinned.contains(b.optString("type").toLowerCase(Locale.ROOT))));
    }

    private void togglePin(JSONObject server) {
        java.util.Set<String> pinned = pins();
        String type = server.optString("type").toLowerCase(Locale.ROOT);
        boolean now = !pinned.remove(type);
        if (now) pinned.add(type);
        activity.getSharedPreferences(PINS, android.content.Context.MODE_PRIVATE).edit()
            .putStringSet("types", pinned).apply();
        sortPinned();
        populateList();
        Toast.makeText(activity, now ? "Закреплено на этом устройстве" : "Откреплено", Toast.LENGTH_SHORT).show();
    }

    private void showMenu(JSONObject server) {
        if (!connected || busy) {
            Toast.makeText(activity, "Нет связи с VPS", Toast.LENGTH_SHORT).show();
            return;
        }
        boolean active = "Connect".equalsIgnoreCase(server.optString("status"));
        String command = active ? "Отключить" : "Подключить";
        String[] items = {"Окно настроек", "Закрепить / открепить", command};
        new AlertDialog.Builder(activity).setTitle(server.optString("name"))
            .setItems(items, (dialog, which) -> {
                if (which == 0) actions.openSettings(server);
                else if (which == 2) actions.command(server);
                else togglePin(server);
            }).show();
    }

    private void addInactiveActions() {
        LinearLayout actionsRow = new LinearLayout(activity);
        actionsRow.setOrientation(tablet ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        target.addView(actionsRow);
        for (String label : new String[]{"Правила поддержки", "Прокси", "Нагрузка на систему",
            "Доступность серверов"}) {
            TextView action = text(label, 13, R.color.text_secondary);
            action.setBackgroundResource(R.drawable.input_background);
            action.setPadding(dp(10), dp(8), dp(10), dp(8));
            action.setEnabled(false);
            action.setAlpha(.55f);
            LinearLayout.LayoutParams params = tablet
                ? new LinearLayout.LayoutParams(0, dp(40), 1)
                : new LinearLayout.LayoutParams(-1, dp(40));
            params.topMargin = dp(4);
            actionsRow.addView(action, params);
        }
    }

    private LinearLayout panel() {
        LinearLayout view = new LinearLayout(activity);
        view.setOrientation(LinearLayout.VERTICAL);
        return view;
    }

    private TextView cell(String value, boolean heading) {
        TextView view = text(value, 12, heading ? R.color.text_secondary : R.color.text_primary);
        view.setBackgroundResource(R.drawable.position_cell);
        view.setPadding(dp(6), dp(7), dp(6), dp(7));
        return view;
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(activity);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(activity.getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private static String time(JSONObject message) {
        String value = message.optString("time");
        try { return OffsetDateTime.parse(value).atZoneSameInstant(ZoneId.systemDefault()).format(TIME); }
        catch (Exception ignored) { return value; }
    }

    private int dp(int value) {
        return Math.round(value * activity.getResources().getDisplayMetrics().density);
    }
}
