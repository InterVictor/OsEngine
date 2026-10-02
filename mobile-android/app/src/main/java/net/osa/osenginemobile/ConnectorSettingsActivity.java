package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Mobile counterpart of RobotsVpsServerParametersUi for one connector instance. */
public final class ConnectorSettingsActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::refreshStatus;
    private McpBridge bridge;
    private String terminal;
    private String type;
    private int number;
    private String name;
    private boolean visible;
    private boolean loading;
    private boolean busy;
    private boolean reloadRequested;
    private boolean supportsMultiple;
    private int tab;
    private String status = "—";
    private JSONArray parameters = new JSONArray();
    private JSONArray instances = new JSONArray();
    private JSONArray messages = new JSONArray();
    private LinearLayout body;
    private LinearLayout content;
    private LinearLayout tabs;
    private LinearLayout connections;
    private TextView statusView;
    private TextView errorView;
    private TextView instanceName;
    private TextView connectButton;
    private TextView disconnectButton;
    private ScrollView scroll;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        type = getIntent().getStringExtra("server_type");
        number = getIntent().getIntExtra("server_number", 0);
        name = getIntent().getStringExtra("server_name");
        if (terminal == null || type == null || type.isEmpty()) { finish(); return; }
        if (name == null || name.isEmpty()) name = type;
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        buildScreen();
    }

    @Override protected void onResume() {
        super.onResume();
        visible = true;
        loadAll();
    }

    @Override protected void onPause() {
        visible = false;
        handler.removeCallbacks(polling);
        super.onPause();
    }

    @Override protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    private void buildScreen() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(getColor(R.color.background));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                top = bars.top;
                bottom = bars.bottom;
            } else {
                top = insets.getSystemWindowInsetTop();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(0, top, 0, bottom);
            return insets;
        });
        setContentView(root);
        root.requestApplyInsets();
        scroll = new BarScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(14), dp(12), dp(14), dp(12));
        scroll.addView(body);
        TextView back = text("‹ Серверы · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> finish());
        body.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        TextView title = text("Настройка подключения " + type, 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        body.addView(title);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(2));
        stripParams.bottomMargin = dp(10);
        body.addView(strip, stripParams);
        errorView = text("Загрузка настроек…", 12, R.color.text_secondary);
        body.addView(errorView);
        connections = panel();
        body.addView(connections);
        instanceName = text("Имя " + name, 14, R.color.orange);
        body.addView(instanceName);
        statusView = text("Статус сервера  " + status, 14, R.color.orange);
        body.addView(statusView);
        tabs = panel();
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        body.addView(tabs);
        content = panel();
        body.addView(content);
        LinearLayout buttons = panel();
        buttons.setOrientation(LinearLayout.HORIZONTAL);
        buttons.setPadding(dp(14), dp(6), dp(14), dp(8));
        root.addView(Ime.hide(buttons));
        connectButton = action("Подключить");
        disconnectButton = action("Отключить");
        buttons.addView(connectButton, new LinearLayout.LayoutParams(0, dp(42), 1));
        LinearLayout.LayoutParams second = new LinearLayout.LayoutParams(0, dp(42), 1);
        second.leftMargin = dp(6);
        buttons.addView(disconnectButton, second);
        connectButton.setOnClickListener(view -> command("server_instance_connect", number));
        disconnectButton.setOnClickListener(view -> command("server_instance_disconnect", number));
        renderTabs();
        renderContent();
        updateStatus();
    }

    private void loadAll() {
        if (loading || busy || !visible) return;
        if (!RemoteSsh.isConnected()) { showError("Нет связи · SSH"); return; }
        loading = true;
        int requestedNumber = number;
        worker.execute(() -> {
            JSONObject nextParams = null;
            JSONObject nextStatus = null;
            JSONArray nextInstances = null;
            boolean multiple = false;
            String error = null;
            try {
                JSONObject args = instanceArgs(requestedNumber);
                java.util.Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("server_instance_get_params", args),
                    McpBridge.call("server_instance_get_status", args),
                    McpBridge.call("server_management_get_list", null),
                    McpBridge.call("server_management_get_connector_permissions",
                        new JSONObject().put("type", type)));
                for (String key : new String[]{"server_instance_get_params",
                    "server_instance_get_status", "server_management_get_list"}) {
                    Object value = result.get(key);
                    if (value instanceof Exception) throw (Exception) value;
                }
                nextParams = (JSONObject) result.get("server_instance_get_params");
                nextStatus = (JSONObject) result.get("server_instance_get_status");
                nextInstances = (JSONArray) result.get("server_management_get_list");
                JSONObject permission = (JSONObject) result.get("server_management_get_connector_permissions");
                if (nextParams == null || nextStatus == null || nextInstances == null)
                    throw new IllegalStateException("VPS не вернул настройки коннектора");
                JSONObject fields = permission == null ? null : permission.optJSONObject("permissions");
                multiple = fields != null && fields.optBoolean("IsSupports_MultipleInstances");
            } catch (Exception e) { error = e.getMessage(); }
            JSONObject params = nextParams;
            JSONObject state = nextStatus;
            JSONArray list = nextInstances;
            boolean supports = multiple;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (reloadRequested || requestedNumber != number) {
                    reloadRequested = false;
                    loadAll();
                    return;
                }
                if (finalError != null) { showError("Не удалось загрузить настройки: " + finalError); return; }
                parameters = params.optJSONArray("parameters");
                if (parameters == null) parameters = new JSONArray();
                instances = list;
                supportsMultiple = supports;
                status = state.optString("status", "—");
                errorView.setText("");
                renderConnections();
                renderContent();
                updateStatus();
                handler.removeCallbacks(polling);
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private void refreshStatus() {
        handler.removeCallbacks(polling);
        if (!visible || busy || loading) return;
        if (!RemoteSsh.isConnected()) { showError("Нет связи · SSH"); return; }
        loading = true;
        int requestedNumber = number;
        int requestedTab = tab;
        worker.execute(() -> {
            JSONObject nextStatus = null;
            JSONArray nextLog = null;
            String error = null;
            try {
                JSONObject args = instanceArgs(requestedNumber);
                java.util.Map<String, Object> result = requestedTab == 1
                    ? bridge.callBatch(terminal,
                        McpBridge.call("server_instance_get_status", args),
                        McpBridge.call("server_instance_get_log",
                            new JSONObject(args.toString()).put("count", 200)))
                    : bridge.callBatch(terminal,
                        McpBridge.call("server_instance_get_status", args));
                Object value = result.get("server_instance_get_status");
                if (value instanceof Exception) throw (Exception) value;
                nextStatus = (JSONObject) value;
                if (requestedTab == 1) {
                    Object logValue = result.get("server_instance_get_log");
                    if (logValue instanceof Exception) throw (Exception) logValue;
                    nextLog = ((JSONObject) logValue).optJSONArray("messages");
                }
            } catch (Exception e) { error = e.getMessage(); }
            JSONObject state = nextStatus;
            JSONArray log = nextLog;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (reloadRequested || requestedNumber != number) {
                    reloadRequested = false;
                    loadAll();
                    return;
                }
                if (finalError == null) {
                    status = state.optString("status", "—");
                    updateStatus();
                    if (requestedTab == tab && log != null && !log.toString().equals(messages.toString())) {
                        messages = log;
                        if (tab == 1) renderContent();
                    }
                    errorView.setText("");
                } else showError("Не удалось обновить подключение: " + finalError);
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private void renderConnections() {
        connections.removeAllViews();
        if (!supportsMultiple) return;
        connections.addView(text("Преднастроенные соединения", 15, R.color.orange));
        LinearLayout heading = panel();
        heading.setOrientation(LinearLayout.HORIZONTAL);
        heading.addView(cell("Имя", true), new LinearLayout.LayoutParams(0, dp(42), 2));
        heading.addView(cell("Номер", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("Приставка", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("Статус", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        connections.addView(heading);
        for (int i = 0; i < instances.length(); i++) {
            JSONObject item = instances.optJSONObject(i);
            if (item == null || !type.equalsIgnoreCase(item.optString("type"))) continue;
            int instanceNumber = item.optInt("number");
            String itemName = item.optString("name");
            String prefixKey = type + "_" + instanceNumber + "_";
            String prefix = instanceNumber > 0 && itemName.startsWith(prefixKey)
                ? itemName.substring(prefixKey.length()) : "";
            LinearLayout row = panel();
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.addView(cell(itemName, false), new LinearLayout.LayoutParams(0, dp(48), 2));
            row.addView(cell(String.valueOf(instanceNumber), false), new LinearLayout.LayoutParams(0, dp(48), 1));
            row.addView(cell(prefix, false), new LinearLayout.LayoutParams(0, dp(48), 1));
            TextView state = cell(item.optString("status"), false);
            state.setTextColor(getColor("Connect".equalsIgnoreCase(item.optString("status"))
                ? R.color.server_connected : R.color.orange));
            row.addView(state, new LinearLayout.LayoutParams(0, dp(48), 1));
            row.setOnClickListener(view -> selectInstance(item));
            connections.addView(row);
            LinearLayout actions = panel();
            actions.setOrientation(LinearLayout.HORIZONTAL);
            for (String label : new String[]{"Подключить", "Отключить", "Удалить"}) {
                if ("Удалить".equals(label) && instanceNumber == 0) continue;
                TextView button = action(label);
                actions.addView(button, new LinearLayout.LayoutParams(0, dp(38), 1));
                button.setOnClickListener(view -> {
                    if ("Подключить".equals(label)) command("server_instance_connect", instanceNumber);
                    else if ("Отключить".equals(label)) command("server_instance_disconnect", instanceNumber);
                    else showDeleteConfirmation(instanceNumber);
                });
            }
            connections.addView(actions);
        }
        TextView add = action("Добавить подключение");
        connections.addView(add, new LinearLayout.LayoutParams(-1, dp(40)));
        add.setOnClickListener(view -> runInstanceAction("server_instance_create", -1));
    }

    private void selectInstance(JSONObject item) {
        if (busy) return;
        number = item.optInt("number");
        name = item.optString("name", type);
        instanceName.setText("Имя " + name);
        status = item.optString("status", "—");
        updateStatus();
        if (loading) { reloadRequested = true; return; }
        loadAll();
    }

    private void showDeleteConfirmation(int selected) {
        new AlertDialog.Builder(this, R.style.OsEngineDialog).setMessage("Удалить " + type + " #" + selected + "?")
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Удалить", (d, w) ->
                runInstanceAction("server_instance_delete", selected)).show();
    }

    private void renderTabs() {
        tabs.removeAllViews();
        String[] names = {"Настройки", "Логирование"};
        for (int i = 0; i < names.length; i++) {
            int index = i;
            TextView item = action(names[i]);
            item.setTextColor(getColor(tab == i ? R.color.orange : R.color.text_primary));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(44), 1);
            tabs.addView(item, params);
            item.setOnClickListener(view -> {
                tab = index;
                renderTabs();
                renderContent();
                handler.removeCallbacks(polling);
                handler.post(polling);
            });
        }
    }

    private void renderContent() {
        int scrollY = scroll.getScrollY();
        content.removeAllViews();
        if (tab == 1) renderLog();
        else renderParameters();
        scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private void renderParameters() {
        LinearLayout heading = panel();
        heading.setOrientation(LinearLayout.HORIZONTAL);
        heading.addView(cell("Название параметра", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("Значение", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        content.addView(heading);
        if (parameters.length() == 0) content.addView(text("Параметров нет", 14, R.color.text_secondary));
        for (int i = 0; i < parameters.length(); i++) {
            JSONObject parameter = parameters.optJSONObject(i);
            if (parameter == null || "securities".equals(parameter.optString("button_action"))) continue;
            String kind = parameter.optString("type");
            LinearLayout row = panel();
            row.setOrientation(LinearLayout.HORIZONTAL);
            TextView label = cell("Button".equalsIgnoreCase(kind) ? "" : parameter.optString("name"), false);
            row.addView(label, new LinearLayout.LayoutParams(0, -2, 1));
            String shown = "Bool".equalsIgnoreCase(kind)
                ? (parameter.optBoolean("value") ? "True" : "False")
                : parameter.optBoolean("is_secret")
                ? (parameter.optString("value").isEmpty() ? "" : "••••")
                : parameter.optString("value");
            TextView value = cell("Button".equalsIgnoreCase(kind) ? parameter.optString("name") : shown, false);
            value.setTextColor(getColor("Button".equalsIgnoreCase(kind)
                ? R.color.orange : R.color.text_primary));
            row.addView(value, new LinearLayout.LayoutParams(0, -2, 1));
            value.setOnClickListener(view -> editParameter(parameter));
            content.addView(row);
            if (!parameter.optString("comment").isEmpty()) {
                TextView details = text("Подробнее", 12, R.color.orange);
                details.setOnClickListener(view -> new AlertDialog.Builder(this, R.style.OsEngineDialog)
                    .setMessage(parameter.optString("comment")).setPositiveButton("ОК", null).show());
                content.addView(details);
            }
        }
    }

    private void editParameter(JSONObject parameter) {
        if (busy || !RemoteSsh.isConnected()) { showError("Нет связи · SSH"); return; }
        String kind = parameter.optString("type");
        if ("Button".equalsIgnoreCase(kind)) {
            if ("non_trade_periods".equals(parameter.optString("button_action"))) {
                Intent intent = new Intent(this, NonTradePeriodsActivity.class);
                intent.putExtra("terminal_name", terminal);
                intent.putExtra("server_type", type);
                intent.putExtra("server_number", number);
                startActivity(intent);
            }
            return;
        }
        String title = parameter.optString("name");
        if ("Bool".equalsIgnoreCase(kind) || "Enum".equalsIgnoreCase(kind)) {
            JSONArray options = parameter.optJSONArray("enum_values");
            String[] choices;
            if ("Bool".equalsIgnoreCase(kind)) choices = new String[]{"True", "False"};
            else {
                if (options == null || options.length() == 0) return;
                choices = new String[options.length()];
                for (int i = 0; i < choices.length; i++) choices[i] = options.optString(i);
            }
            new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(title).setItems(choices,
                (dialog, which) -> saveParameter(parameter,
                    "Bool".equalsIgnoreCase(kind) ? "True".equals(choices[which]) : choices[which]))
                .show();
            return;
        }
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setTextColor(getColor(R.color.text_primary));
        if (parameter.optBoolean("is_secret"))
            input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        else if ("Int".equalsIgnoreCase(kind) || "Decimal".equalsIgnoreCase(kind)) {
            input.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_SIGNED
                | ("Decimal".equalsIgnoreCase(kind) ? InputType.TYPE_NUMBER_FLAG_DECIMAL : 0));
            input.setText(parameter.optString("value"));
        } else input.setText(parameter.optString("value"));
        new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(title).setView(input)
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                String raw = input.getText().toString();
                if (parameter.optBoolean("is_secret") && raw.isEmpty()) return;
                try {
                    Object value = "Int".equalsIgnoreCase(kind) ? Integer.parseInt(raw)
                        : "Decimal".equalsIgnoreCase(kind) ? new BigDecimal(raw.replace(',', '.'))
                        : raw;
                    saveParameter(parameter, value);
                } catch (Exception e) { showError("Недопустимое значение: " + raw); }
            }).show();
    }

    private void saveParameter(JSONObject parameter, Object value) {
        runCommand("server_instance_set_params", () -> {
            JSONArray changes = new JSONArray().put(new JSONObject()
                .put("name", parameter.getString("name")).put("value", value));
            return new JSONObject(instanceArgs(number).toString()).put("parameters", changes);
        }, false);
    }

    private void renderLog() {
        if (!tablet()) {
            if (messages.length() == 0)
                content.addView(text("Записей нет", 14, R.color.text_secondary));
            for (int i = 0; i < messages.length(); i++) {
                JSONObject entry = messages.optJSONObject(i);
                if (entry == null) continue;
                LinearLayout card = panel();
                card.setPadding(dp(10), dp(8), dp(10), dp(8));
                card.setBackgroundResource(R.drawable.input_background);
                LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
                params.bottomMargin = dp(6);
                content.addView(card, params);
                card.addView(text(entry.optString("time") + " · " + entry.optString("type"),
                    12, R.color.text_secondary));
                card.addView(text(entry.optString("message"), 14, R.color.text_primary));
            }
            return;
        }
        LinearLayout heading = panel();
        heading.setOrientation(LinearLayout.HORIZONTAL);
        heading.addView(cell("Время", true), new LinearLayout.LayoutParams(0, dp(42), 2));
        heading.addView(cell("Тип", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("Сообщение", true), new LinearLayout.LayoutParams(0, dp(42), 4));
        content.addView(heading);
        if (messages.length() == 0) content.addView(text("Записей нет", 14, R.color.text_secondary));
        for (int i = 0; i < messages.length(); i++) {
            JSONObject entry = messages.optJSONObject(i);
            if (entry == null) continue;
            LinearLayout row = panel();
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.addView(cell(entry.optString("time"), false), new LinearLayout.LayoutParams(0, -2, 2));
            row.addView(cell(entry.optString("type"), false), new LinearLayout.LayoutParams(0, -2, 1));
            row.addView(cell(entry.optString("message"), false), new LinearLayout.LayoutParams(0, -2, 4));
            content.addView(row);
        }
    }

    private void command(String tool, int selectedNumber) {
        runCommand(tool, () -> instanceArgs(selectedNumber), false);
    }

    private void runInstanceAction(String tool, int selectedNumber) {
        runCommand(tool, () -> selectedNumber < 0
            ? new JSONObject().put("type", type) : instanceArgs(selectedNumber), true);
    }

    private interface Arguments { JSONObject get() throws Exception; }

    private void runCommand(String tool, Arguments arguments, boolean instanceAction) {
        if (busy || !RemoteSsh.isConnected()) { showError("Нет связи · SSH"); return; }
        busy = true;
        updateStatus();
        worker.execute(() -> {
            Object response = null;
            String error = null;
            try {
                response = bridge.callBatch(terminal, McpBridge.call(tool, arguments.get())).get(tool);
                if (response instanceof Exception) throw (Exception) response;
                if (response == null) throw new IllegalStateException("VPS не подтвердил действие");
            } catch (Exception e) { error = e.getMessage(); }
            Object result = response;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (!visible || isDestroyed()) return;
                if (finalError != null) {
                    showError("Не удалось выполнить действие: " + finalError);
                    updateStatus();
                    handler.removeCallbacks(polling);
                    handler.postDelayed(polling, 5_000);
                    return;
                }
                if (instanceAction) {
                    if ("server_instance_create".equals(tool) && result instanceof JSONObject) {
                        number = ((JSONObject) result).optInt("number");
                        name = ((JSONObject) result).optString("name", type);
                    } else if ("server_instance_delete".equals(tool)
                        && result instanceof JSONObject
                        && number == ((JSONObject) result).optInt("number", -1)) {
                        number = 0;
                        name = type;
                    }
                    instanceName.setText("Имя " + name);
                }
                loadAll();
            });
        });
    }

    private JSONObject instanceArgs(int selectedNumber) throws Exception {
        return new JSONObject().put("type", type).put("number", selectedNumber);
    }

    private void updateStatus() {
        statusView.setText("Статус сервера  " + status);
        statusView.setTextColor(getColor("Connect".equalsIgnoreCase(status)
            ? R.color.server_connected : R.color.orange));
        boolean enabled = !busy && RemoteSsh.isConnected();
        connectButton.setEnabled(enabled);
        disconnectButton.setEnabled(enabled);
        connectButton.setAlpha(enabled ? 1f : .5f);
        disconnectButton.setAlpha(enabled ? 1f : .5f);
    }

    private void showError(String message) {
        errorView.setText(message);
        updateStatus();
    }

    private LinearLayout panel() {
        LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.VERTICAL);
        return view;
    }

    private TextView cell(String value, boolean heading) {
        TextView view = text(value, 12, heading ? R.color.text_secondary : R.color.text_primary);
        view.setBackgroundResource(R.drawable.position_cell);
        view.setPadding(dp(5), dp(7), dp(5), dp(7));
        view.setMinHeight(dp(42));
        return view;
    }

    private TextView action(String value) {
        TextView view = text(value, 13, R.color.text_primary);
        view.setGravity(Gravity.CENTER);
        view.setBackgroundResource(R.drawable.input_background);
        return view;
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private boolean tablet() {
        return getResources().getConfiguration().smallestScreenWidthDp >= 600;
    }
}
