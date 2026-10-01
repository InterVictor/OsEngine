package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class RobotsActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::load;
    private final Runnable positionsPolling = this::loadPositions;
    private final Runnable ordersPolling = this::loadOrders;
    private final Runnable portfolioPolling = this::loadPortfolios;
    private final Runnable journalPolling = this::loadJournal;
    private final Runnable primeLogPolling = this::loadPrimeLog;
    private final Runnable serversPolling = this::loadServers;
    private McpBridge bridge;
    private String terminal;
    private String page = "Роботы";
    private JSONArray bots = new JSONArray();
    private JSONArray servers = new JSONArray();
    private boolean visible;
    private boolean loading;
    private boolean positionsLoading;
    private boolean ordersLoading;
    private boolean portfolioLoading;
    private boolean journalLoading;
    private boolean primeLogLoading;
    private boolean serversLoading;
    private boolean serverActionRunning;
    private boolean positionActionRunning;
    private boolean available;
    private boolean tablet;
    private TextView status;
    private LinearLayout botList;
    private LinearLayout pageContent;
    private PositionsPage positionsPage;
    private OrdersPage ordersPage;
    private PortfolioPage portfolioPage;
    private JournalPage journalPage;
    private PrimeLogPage primeLogPage;
    private ServersPage serversPage;
    private LinearLayout nav;
    private View robotSection;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        setContentView(R.layout.activity_robots);
        terminal = getIntent().getStringExtra("terminal_name");
        if (terminal == null || terminal.isEmpty()) terminal = "?";
        tablet = getResources().getConfiguration().smallestScreenWidthDp >= 600;
        View root = findViewById(R.id.robots_root);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top;
            int bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
                top = bars.top;
                bottom = bars.bottom;
            } else {
                top = insets.getSystemWindowInsetTop();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(0, top, 0, bottom);
            return insets;
        });
        root.requestApplyInsets();
        View column = findViewById(R.id.content_column);
        ViewGroup.LayoutParams width = column.getLayoutParams();
        int screenWidth = getResources().getDisplayMetrics().widthPixels;
        width.width = Math.min(screenWidth - dp(32), dp(tablet ? 1100 : 480));
        column.setLayoutParams(width);
        ((TextView) findViewById(R.id.robots_heading)).setText(
            getString(R.string.robots_heading, terminal));
        status = findViewById(R.id.robots_status);
        botList = findViewById(R.id.robots_list);
        pageContent = findViewById(R.id.page_content);
        positionsPage = new PositionsPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet, this::showPositionActions);
        ordersPage = new OrdersPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet);
        portfolioPage = new PortfolioPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet, this::openComparePositions,
            this::confirmCloseOnBoard);
        journalPage = new JournalPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet, () -> {
                handler.removeCallbacks(journalPolling);
                handler.post(journalPolling);
            });
        primeLogPage = new PrimeLogPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet, () -> {
                page = "Ещё";
                handler.removeCallbacks(primeLogPolling);
                render();
            });
        serversPage = new ServersPage(this, pageContent,
            (ScrollView) findViewById(R.id.scroll_root), tablet, new ServersPage.Actions() {
                @Override public void setAutoConnect(boolean enabled) {
                    setServerAutoConnect(enabled);
                }
                @Override public void command(JSONObject server) {
                    commandServer(server);
                }
                @Override public void openSettings(JSONObject server) {
                    openServerSettings(server);
                }
            });
        robotSection = findViewById(R.id.robot_section);
        nav = findViewById(tablet ? R.id.tablet_nav_slot : R.id.phone_nav_items);
        if (tablet) findViewById(R.id.phone_nav).setVisibility(View.GONE);
        findViewById(R.id.back_terminals).setOnClickListener(view -> finish());
        findViewById(R.id.add_robot).setOnClickListener(view -> addBot());
        try { bridge = new McpBridge(this); }
        catch (Exception error) { status.setText(error.getMessage()); }
        render();
    }

    @Override protected void onResume() {
        super.onResume();
        visible = true;
        handler.post(polling);
        if ("Позиции".equals(page)) handler.post(positionsPolling);
        if ("Ордера".equals(page)) handler.post(ordersPolling);
        if ("Портфель".equals(page)) handler.post(portfolioPolling);
        if ("Журнал".equals(page)) handler.post(journalPolling);
        if ("Прайм лог".equals(page)) handler.post(primeLogPolling);
        if ("Серверы".equals(page)) handler.post(serversPolling);
    }

    @Override protected void onPause() {
        visible = false;
        handler.removeCallbacks(polling);
        handler.removeCallbacks(positionsPolling);
        handler.removeCallbacks(ordersPolling);
        handler.removeCallbacks(portfolioPolling);
        handler.removeCallbacks(journalPolling);
        handler.removeCallbacks(primeLogPolling);
        handler.removeCallbacks(serversPolling);
        super.onPause();
    }

    @Override protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    private void load() {
        handler.removeCallbacks(polling);
        if (!visible || loading || bridge == null) return;
        if (!RemoteSsh.isConnected()) {
            available = false;
            status.setText("Нет связи · SSH");
            if ("Позиции".equals(page)) {
                renderNav();
                positionsPage.showError("Нет связи · SSH");
            } else if ("Ордера".equals(page)) {
                renderNav();
                ordersPage.showError("Нет связи · SSH");
            } else if ("Портфель".equals(page)) {
                renderNav();
                portfolioPage.showError("Нет связи · SSH");
            } else if ("Журнал".equals(page)) {
                renderNav();
                journalPage.showError("Нет связи · SSH");
            } else if ("Прайм лог".equals(page)) {
                renderNav();
                primeLogPage.showError("Нет связи · SSH");
            } else if ("Серверы".equals(page)) {
                renderNav();
                serversPage.showError("Нет связи · SSH");
            } else render();
            handler.postDelayed(polling, 5_000);
            return;
        }
        loading = true;
        worker.execute(() -> {
            JSONArray nextBots = null;
            JSONArray nextServers = null;
            String error = null;
            try {
                Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("bot_get_list", null),
                    McpBridge.call("server_management_get_list", null));
                Object botResult = result.get("bot_get_list");
                Object serverResult = result.get("server_management_get_list");
                if (botResult instanceof JSONObject)
                    nextBots = ((JSONObject) botResult).optJSONArray("bots");
                if (serverResult instanceof JSONArray)
                    nextServers = (JSONArray) serverResult;
                if (nextBots == null || nextServers == null)
                    throw new IllegalStateException("Ответ MCP не содержит список роботов или серверов");
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray finalBots = nextBots;
            JSONArray finalServers = nextServers;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (finalError == null) {
                    bots = finalBots;
                    servers = finalServers;
                    available = true;
                    status.setText(RemoteSsh.host() + " · SSH · "
                        + new SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(new Date()));
                } else {
                    available = false;
                    status.setText("Нет связи · " + finalError);
                }
                if ("Позиции".equals(page) || "Ордера".equals(page)
                    || "Портфель".equals(page) || "Журнал".equals(page)
                    || "Прайм лог".equals(page) || "Серверы".equals(page)) renderNav();
                else render();
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private void loadPositions() {
        handler.removeCallbacks(positionsPolling);
        if (!visible || !"Позиции".equals(page) || positionsLoading) return;
        if (bridge == null) {
            positionsPage.showError("MCP недоступен");
            handler.postDelayed(positionsPolling, 3_000);
            return;
        }
        if (!RemoteSsh.isConnected()) {
            positionsPage.showError("Нет связи · SSH");
            handler.postDelayed(positionsPolling, 3_000);
            return;
        }
        positionsLoading = true;
        worker.execute(() -> {
            JSONArray active = null;
            JSONArray stops = null;
            JSONArray closed = null;
            String error = null;
            try {
                Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("bot_journal_get_open_positions", null),
                    McpBridge.call("bot_journal_get_stop_limit_positions", null),
                    McpBridge.call("bot_journal_get_closed_positions", null));
                active = positionsResult(result, "bot_journal_get_open_positions");
                stops = positionsResult(result, "bot_journal_get_stop_limit_positions");
                closed = positionsResult(result, "bot_journal_get_closed_positions");
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray nextActive = active;
            JSONArray nextStops = stops;
            JSONArray nextClosed = closed;
            String finalError = error;
            runOnUiThread(() -> {
                positionsLoading = false;
                if (!visible || isDestroyed() || !"Позиции".equals(page)) return;
                if (finalError == null)
                    positionsPage.showData(nextActive, nextStops, nextClosed);
                else positionsPage.showError("Не удалось загрузить позиции: " + finalError);
                handler.postDelayed(positionsPolling, 3_000);
            });
        });
    }

    private static JSONArray positionsResult(Map<String, Object> result, String name)
        throws Exception {
        Object value = result.get(name);
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject))
            throw new IllegalStateException("Ответ MCP не содержит позиции: " + name);
        JSONArray rows = ((JSONObject) value).optJSONArray("positions");
        if (rows == null)
            throw new IllegalStateException("Ответ MCP не содержит список позиций: " + name);
        return rows;
    }

    private void loadOrders() {
        handler.removeCallbacks(ordersPolling);
        if (!visible || !"Ордера".equals(page) || ordersLoading) return;
        if (bridge == null || !RemoteSsh.isConnected()) {
            ordersPage.showError("Нет связи · SSH");
            handler.postDelayed(ordersPolling, 15_000);
            return;
        }
        ordersLoading = true;
        worker.execute(() -> {
            JSONArray current = null;
            JSONArray history = null;
            String serverName = null;
            String error = null;
            try {
                Object serverResult = bridge.callBatch(terminal,
                    McpBridge.call("server_management_get_list", null))
                    .get("server_management_get_list");
                if (serverResult instanceof Exception) throw (Exception) serverResult;
                if (!(serverResult instanceof JSONArray))
                    throw new IllegalStateException("Список серверов недоступен");
                JSONArray list = (JSONArray) serverResult;
                JSONObject selected = null;
                for (int i = 0; i < list.length(); i++) {
                    JSONObject item = list.optJSONObject(i);
                    if (item != null && "Connect".equals(item.optString("status"))) {
                        selected = item;
                        break;
                    }
                }
                if (selected == null) selected = list.optJSONObject(0);
                if (selected == null || selected.optString("type").isEmpty())
                    throw new IllegalStateException("Нет торгового сервера на VPS");
                serverName = selected.optString("type");
                JSONObject args = new JSONObject().put("type", serverName)
                    .put("number", selected.optInt("number"))
                    .put("offset", 0).put("limit", 100);
                Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("server_instance_get_active_orders", args),
                    McpBridge.call("server_instance_get_historical_orders", args));
                current = orderRows(result, "server_instance_get_active_orders");
                history = orderRows(result, "server_instance_get_historical_orders");
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray nextCurrent = current, nextHistory = history;
            String nextServer = serverName, finalError = error;
            runOnUiThread(() -> {
                ordersLoading = false;
                if (!visible || isDestroyed() || !"Ордера".equals(page)) return;
                if (finalError == null) ordersPage.showData(nextCurrent, nextHistory, nextServer);
                else ordersPage.showError("Не удалось загрузить ордера: " + finalError);
                handler.postDelayed(ordersPolling, 15_000);
            });
        });
    }

    private static JSONArray orderRows(Map<String, Object> result, String name) throws Exception {
        Object value = result.get(name);
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject))
            throw new IllegalStateException("Ответ MCP не содержит ордера: " + name);
        JSONArray rows = ((JSONObject) value).optJSONArray("orders");
        if (rows == null)
            throw new IllegalStateException("Ответ MCP не содержит список ордеров: " + name);
        return rows;
    }

    private void loadPortfolios() {
        handler.removeCallbacks(portfolioPolling);
        if (!visible || !"Портфель".equals(page) || portfolioLoading) return;
        if (bridge == null || !RemoteSsh.isConnected()) {
            portfolioPage.showError("Нет связи · SSH");
            handler.postDelayed(portfolioPolling, 15_000);
            return;
        }
        portfolioLoading = true;
        worker.execute(() -> {
            JSONArray collected = new JSONArray();
            String message = null;
            String error = null;
            try {
                Object response = bridge.callBatch(terminal,
                    McpBridge.call("server_management_get_list", null))
                    .get("server_management_get_list");
                if (response instanceof Exception) throw (Exception) response;
                if (!(response instanceof JSONArray))
                    throw new IllegalStateException("Список серверов недоступен");
                JSONArray list = (JSONArray) response;
                ArrayList<String> errors = new ArrayList<>();
                int serverCount = 0;
                for (int i = 0; i < list.length(); i++) {
                    JSONObject server = list.optJSONObject(i);
                    if (server == null || server.optString("type").isEmpty()) continue;
                    String type = server.optString("type");
                    int number = server.optInt("number");
                    serverCount++;
                    try {
                        JSONObject args = new JSONObject().put("type", type).put("number", number);
                        Object value = bridge.callBatch(terminal,
                            McpBridge.call("server_instance_get_portfolios", args))
                            .get("server_instance_get_portfolios");
                        if (value instanceof Exception) throw (Exception) value;
                        if (!(value instanceof JSONObject))
                            throw new IllegalStateException("Неверный ответ MCP");
                        JSONArray items = ((JSONObject) value).optJSONArray("portfolios");
                        if (items == null) throw new IllegalStateException("Список портфелей отсутствует");
                        for (int j = 0; j < items.length(); j++) {
                            JSONObject item = items.optJSONObject(j);
                            if (item == null || "FinamVirtual".equals(item.optString("number"))) continue;
                            JSONObject copy = new JSONObject(item.toString());
                            copy.put("serverType", type);
                            copy.put("serverNumber", number);
                            if (copy.optString("serverUniqueName").isEmpty())
                                copy.put("serverUniqueName", type + "_" + number);
                            collected.put(copy);
                        }
                    } catch (Exception e) { errors.add(type + "#" + number + ": " + e.getMessage()); }
                }
                if (collected.length() == 0) message = serverCount == 0
                    ? "На VPS нет серверов подключения"
                    : errors.isEmpty() ? "На VPS нет портфелей"
                    : "Не удалось загрузить портфели: " + android.text.TextUtils.join("; ", errors);
                else if (!errors.isEmpty()) message = "Часть серверов недоступна: "
                    + android.text.TextUtils.join("; ", errors);
                java.util.ArrayList<JSONObject> sorted = new java.util.ArrayList<>();
                for (int i = 0; i < collected.length(); i++) sorted.add(collected.getJSONObject(i));
                sorted.sort((a, b) -> a.optString("serverUniqueName")
                    .compareToIgnoreCase(b.optString("serverUniqueName")));
                collected = new JSONArray();
                for (JSONObject item : sorted) collected.put(item);
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray next = collected;
            String nextMessage = message, finalError = error;
            runOnUiThread(() -> {
                portfolioLoading = false;
                if (!visible || isDestroyed() || !"Портфель".equals(page)) return;
                if (finalError == null) portfolioPage.showData(next, nextMessage);
                else portfolioPage.showError("Не удалось загрузить портфели: " + finalError);
                handler.postDelayed(portfolioPolling, 15_000);
            });
        });
    }

    private void loadJournal() {
        handler.removeCallbacks(journalPolling);
        if (!visible || !"Журнал".equals(page) || journalLoading) return;
        if (bridge == null || !RemoteSsh.isConnected()) {
            journalPage.showError("Нет связи · SSH");
            handler.postDelayed(journalPolling, 15_000);
            return;
        }
        journalLoading = true;
        worker.execute(() -> {
            Map<String, Object> result = null;
            String error = null;
            try {
                String selectedTool = journalPage.selectedTool();
                if ("bot_journal_get_equity".equals(selectedTool))
                    result = bridge.callBatch(terminal,
                        McpBridge.call("bot_journal_get_summary", null),
                        McpBridge.call("bot_journal_get_equity",
                            new JSONObject().put("chart_type", "Absolute")),
                        McpBridge.call("bot_journal_get_open_positions", null),
                        McpBridge.call("bot_journal_get_closed_positions", null));
                else result = bridge.callBatch(terminal,
                    McpBridge.call("bot_journal_get_summary", null),
                    McpBridge.call(selectedTool, null));
                for (Object value : result.values()) if (value instanceof Exception)
                    throw (Exception) value;
            } catch (Exception e) { error = e.getMessage(); }
            Map<String, Object> next = result;
            String finalError = error;
            runOnUiThread(() -> {
                journalLoading = false;
                if (!visible || isDestroyed() || !"Журнал".equals(page)) return;
                if (finalError == null) journalPage.showData(next);
                else journalPage.showError("Не удалось загрузить журнал: " + finalError);
                handler.postDelayed(journalPolling, 15_000);
            });
        });
    }

    private void loadPrimeLog() {
        handler.removeCallbacks(primeLogPolling);
        if (!visible || !"Прайм лог".equals(page) || primeLogLoading) return;
        if (bridge == null || !RemoteSsh.isConnected()) {
            primeLogPage.showError("Нет связи · SSH");
            handler.postDelayed(primeLogPolling, 3_000);
            return;
        }
        primeLogLoading = true;
        worker.execute(() -> {
            JSONArray entries = null;
            String error = null;
            try {
                Object result = bridge.callBatch(terminal,
                    McpBridge.call("log_get_prime_log", new JSONObject().put("count", 500)))
                    .get("log_get_prime_log");
                if (result instanceof Exception) throw (Exception) result;
                if (!(result instanceof JSONArray))
                    throw new IllegalStateException("Ответ MCP не содержит прайм лог");
                entries = (JSONArray) result;
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray next = entries;
            String finalError = error;
            runOnUiThread(() -> {
                primeLogLoading = false;
                if (!visible || isDestroyed() || !"Прайм лог".equals(page)) return;
                if (finalError == null) primeLogPage.showData(next);
                else primeLogPage.showError("Не удалось загрузить прайм лог: " + finalError);
                handler.postDelayed(primeLogPolling, 3_000);
            });
        });
    }

    private void loadServers() {
        handler.removeCallbacks(serversPolling);
        if (!visible || !"Серверы".equals(page) || serversLoading || serverActionRunning) return;
        if (bridge == null || !RemoteSsh.isConnected()) {
            serversPage.showError("Нет связи · SSH");
            handler.postDelayed(serversPolling, 5_000);
            return;
        }
        serversLoading = true;
        worker.execute(() -> {
            JSONArray instances = null;
            JSONArray types = null;
            JSONArray messages = new JSONArray();
            boolean auto = false;
            String source = "";
            String logError = null;
            String error = null;
            try {
                Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("server_management_get_list", null),
                    McpBridge.call("server_management_get_trade_connectors", null),
                    McpBridge.call("server_management_get_auto_connect", null));
                for (Object value : result.values()) if (value instanceof Exception)
                    throw (Exception) value;
                if (!(result.get("server_management_get_list") instanceof JSONArray)
                    || !(result.get("server_management_get_trade_connectors") instanceof JSONArray)
                    || !(result.get("server_management_get_auto_connect") instanceof JSONObject))
                    throw new IllegalStateException("Ответ MCP не содержит список серверов");
                instances = (JSONArray) result.get("server_management_get_list");
                types = (JSONArray) result.get("server_management_get_trade_connectors");
                auto = ((JSONObject) result.get("server_management_get_auto_connect"))
                    .optBoolean("enabled");
                JSONObject selected = null;
                for (int i = 0; i < instances.length(); i++) {
                    JSONObject item = instances.optJSONObject(i);
                    if (item == null || "Optimizer".equalsIgnoreCase(item.optString("type"))) continue;
                    if (selected == null) selected = item;
                    if ("Connect".equalsIgnoreCase(item.optString("status"))) {
                        selected = item;
                        break;
                    }
                }
                if (selected != null) {
                    source = selected.optString("name", selected.optString("type"));
                    JSONObject args = new JSONObject().put("type", selected.optString("type"))
                        .put("number", selected.optInt("number")).put("count", 200);
                    Object logResult = bridge.callBatch(terminal,
                        McpBridge.call("server_instance_get_log", args))
                        .get("server_instance_get_log");
                    if (logResult instanceof Exception) throw (Exception) logResult;
                    if (!(logResult instanceof JSONObject)
                        || ((JSONObject) logResult).optJSONArray("messages") == null)
                        throw new IllegalStateException("Ответ MCP не содержит журнал сервера");
                    messages = ((JSONObject) logResult).getJSONArray("messages");
                }
            } catch (Exception e) {
                if (instances == null || types == null) error = e.getMessage();
                else logError = e.getMessage();
            }
            JSONArray nextInstances = instances;
            JSONArray nextTypes = types;
            JSONArray nextMessages = messages;
            boolean nextAuto = auto;
            String nextSource = source;
            String nextLogError = logError;
            String finalError = error;
            runOnUiThread(() -> {
                serversLoading = false;
                if (!visible || isDestroyed() || !"Серверы".equals(page)) return;
                if (finalError == null) serversPage.showData(nextInstances, nextTypes, nextAuto,
                    nextMessages, nextSource, nextLogError);
                else serversPage.showError("Не удалось загрузить серверы: " + finalError);
                handler.postDelayed(serversPolling, 5_000);
            });
        });
    }

    private void setServerAutoConnect(boolean enabled) {
        if (serverActionRunning || !RemoteSsh.isConnected()) return;
        serverActionRunning = true;
        serversPage.setBusy(true);
        worker.execute(() -> {
            String error = null;
            try {
                Object result = bridge.callBatch(terminal,
                    McpBridge.call("server_management_set_auto_connect",
                        new JSONObject().put("enabled", enabled)))
                    .get("server_management_set_auto_connect");
                if (result instanceof Exception) throw (Exception) result;
                if (!(result instanceof JSONObject) || !((JSONObject) result).has("enabled"))
                    throw new IllegalStateException("VPS не подтвердил настройку");
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                serverActionRunning = false;
                serversPage.setBusy(false);
                if (finalError != null) Toast.makeText(this,
                    "Не удалось сохранить автоподключение: " + finalError,
                    Toast.LENGTH_LONG).show();
                if ("Серверы".equals(page)) handler.post(serversPolling);
            });
        });
    }

    private void commandServer(JSONObject server) {
        if (serverActionRunning || !RemoteSsh.isConnected()) return;
        serverActionRunning = true;
        serversPage.setBusy(true);
        worker.execute(() -> {
            String error = null;
            try {
                String type = server.getString("type");
                if (!server.has("number")) {
                    Object result = bridge.callBatch(terminal,
                        McpBridge.call("server_management_activate",
                            new JSONObject().put("type", type))).get("server_management_activate");
                    if (result instanceof Exception) throw (Exception) result;
                } else {
                    JSONObject args = new JSONObject().put("type", type)
                        .put("number", server.getInt("number"));
                    Object status = bridge.callBatch(terminal,
                        McpBridge.call("server_instance_get_status", args))
                        .get("server_instance_get_status");
                    if (status instanceof Exception) throw (Exception) status;
                    if (!(status instanceof JSONObject))
                        throw new IllegalStateException("VPS не вернул состояние коннектора");
                    String tool = "Connect".equalsIgnoreCase(
                        ((JSONObject) status).optString("status"))
                        ? "server_instance_disconnect" : "server_instance_connect";
                    Object result = bridge.callBatch(terminal, McpBridge.call(tool, args)).get(tool);
                    if (result instanceof Exception) throw (Exception) result;
                }
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                serverActionRunning = false;
                serversPage.setBusy(false);
                if (finalError != null) Toast.makeText(this,
                    "Не удалось изменить подключение: " + finalError,
                    Toast.LENGTH_LONG).show();
                if ("Серверы".equals(page)) {
                    handler.post(serversPolling);
                    handler.post(polling);
                }
            });
        });
    }

    private void openServerSettings(JSONObject server) {
        if (serverActionRunning || !RemoteSsh.isConnected()) return;
        serverActionRunning = true;
        serversPage.setBusy(true);
        worker.execute(() -> {
            String error = null;
            int number = server.optInt("number");
            String name = server.optString("name", server.optString("type"));
            try {
                if (!server.has("number")) {
                    Object result = bridge.callBatch(terminal,
                        McpBridge.call("server_management_activate",
                            new JSONObject().put("type", server.getString("type"))))
                        .get("server_management_activate");
                    if (result instanceof Exception) throw (Exception) result;
                    if (!(result instanceof JSONArray) || ((JSONArray) result).length() == 0)
                        throw new IllegalStateException("VPS не вернул экземпляр подключения");
                    JSONObject activated = ((JSONArray) result).getJSONObject(0);
                    number = activated.getInt("number");
                    name = activated.optString("name", name);
                }
            } catch (Exception e) { error = e.getMessage(); }
            int selectedNumber = number;
            String selectedName = name;
            String finalError = error;
            runOnUiThread(() -> {
                serverActionRunning = false;
                serversPage.setBusy(false);
                if (finalError != null) {
                    Toast.makeText(this, "Не удалось открыть настройки: " + finalError,
                        Toast.LENGTH_LONG).show();
                    return;
                }
                Intent intent = new Intent(this, ConnectorSettingsActivity.class);
                intent.putExtra("terminal_name", terminal);
                intent.putExtra("server_type", server.optString("type"));
                intent.putExtra("server_number", selectedNumber);
                intent.putExtra("server_name", selectedName);
                startActivity(intent);
            });
        });
    }

    private void openComparePositions(JSONObject portfolio) {
        if (!RemoteSsh.isConnected()) {
            Toast.makeText(this, "Нет связи с VPS", Toast.LENGTH_SHORT).show();
            return;
        }
        Intent intent = new Intent(this, ComparePositionsActivity.class);
        intent.putExtra("terminal_name", terminal);
        intent.putExtra("server_type", portfolio.optString("serverType"));
        intent.putExtra("server_number", portfolio.optInt("serverNumber"));
        intent.putExtra("portfolio_name", portfolio.optString("number"));
        startActivity(intent);
    }

    /** The portfolio table's «Закрыть»: cancels robot orders, deletes their positions, closes on the exchange. */
    private void confirmCloseOnBoard(JSONObject portfolio, JSONObject position) {
        if (!available || !RemoteSsh.isConnected()) {
            Toast.makeText(this, "Нет связи с VPS", Toast.LENGTH_SHORT).show();
            return;
        }
        String security = position.optString("securityNameCode");
        String type = portfolio.optString("serverType");
        int number = portfolio.optInt("serverNumber");
        if (security.isEmpty() || type.isEmpty()) return;
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle("Закрыть позицию на бирже")
            .setMessage("Вы хотите закрыть позицию на бирже?\n\n" + security + "\nСервер: " + type + " #" + number
                + "\nТерминал: " + terminal + "\n\nЗаявки роботов по этой бумаге будут отменены, их позиции "
                + "удалены, остаток закроется рыночным ордером.")
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Закрыть", (dialog, which) -> {
                loading = true;
                worker.execute(() -> {
                    String error = null;
                    try {
                        Object result = bridge.callBatch(terminal, McpBridge.call(
                            "server_instance_close_position_on_board", new JSONObject()
                                .put("type", type).put("number", number).put("security_name", security)))
                            .get("server_instance_close_position_on_board");
                        if (result instanceof Exception) throw (Exception) result;
                    } catch (Exception e) { error = e.getMessage(); }
                    String finalError = error;
                    runOnUiThread(() -> {
                        loading = false;
                        Toast.makeText(this, finalError == null ? "Команда закрытия отправлена: " + security
                            : "Не удалось закрыть: " + finalError, Toast.LENGTH_LONG).show();
                    });
                });
            }).show();
    }

    private void showPositionActions(int section, JSONObject row) {
        if (!available || !RemoteSsh.isConnected() || !positionsPage.canAct()) {
            Toast.makeText(this, "Нет связи с VPS", Toast.LENGTH_SHORT).show();
            return;
        }
        if (positionActionRunning) return;
        int number = row.optInt("number", -1);
        if (number < 0) return;
        String botId = row.optString("bot_name");
        String[] actions = section == 0
            ? new String[]{"Закрыть все по маркету", "Закрыть выбранную",
                "Докупить в выбранную", "Переставить стоп", "Переставить профит",
                "Удалить позицию"}
            : section == 1 ? new String[]{"Удалить все", "Удалить выбранную"}
            : new String[]{"Удалить позицию"};
        new AlertDialog.Builder(this).setTitle("Номер " + number)
            .setItems(actions, (dialog, which) ->
            selectPositionAction(section, which, botId, number, row)).show();
    }

    private void selectPositionAction(int section, int action, String botId, int number,
                                      JSONObject row) {
        if (section == 0 && action >= 2 && action <= 4) {
            if (botId.isEmpty()) {
                Toast.makeText(this, "Не найден робот позиции", Toast.LENGTH_LONG).show();
                return;
            }
            Intent intent = new Intent(this, PositionActionActivity.class);
            intent.putExtra("terminal_name", terminal);
            intent.putExtra("bot_id", botId);
            intent.putExtra("position_number", number);
            intent.putExtra("security_name", row.optString("security_name"));
            intent.putExtra("mode", action == 2 ? "add" : "close");
            intent.putExtra("initial_tab", action == 3 ? 2 : action == 4 ? 4 : 0);
            startActivity(intent);
            return;
        }
        if (section != 1 && botId.isEmpty()) {
            Toast.makeText(this, "Не найден робот позиции", Toast.LENGTH_LONG).show();
            return;
        }
        String confirmation = null;
        if (section == 0 && action == 0)
            confirmation = "Вы уверены что хотите закрыть все позиции по маркету?";
        else if (section == 1)
            confirmation = action == 0 ? "Вы хотите отозвать все ордера?"
                : "Вы хотите отозвать выбранную заявку?";
        else if (section == 2 || action == 5)
            confirmation = "Вы уверены что хотите удалить позицию?";
        if (confirmation == null) {
            executePositionAction(section, action, botId, number);
        } else {
            new AlertDialog.Builder(this).setMessage(confirmation)
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Подтвердить", (dialog, button) ->
                    executePositionAction(section, action, botId, number)).show();
        }
    }

    private void executePositionAction(int section, int action, String botId, int number) {
        if (positionActionRunning || !"Позиции".equals(page)
            || !available || !RemoteSsh.isConnected()
            || !positionsPage.canAct()) return;
        positionActionRunning = true;
        worker.execute(() -> {
            String error = null;
            try {
                if (section == 1) {
                    JSONObject args = action == 0 ? new JSONObject()
                        : new JSONObject().put("number", number);
                    callPositionTool("bot_stop_limit_cancel", args);
                } else if (section == 0 && action == 0) {
                    JSONArray snapshot = positionsResult(bridge.callBatch(terminal,
                        McpBridge.call("bot_journal_get_open_positions", null)),
                        "bot_journal_get_open_positions");
                    ArrayList<JSONObject> targets = new ArrayList<>();
                    Map<String, String> sourceNames = new HashMap<>();
                    for (int i = 0; i < snapshot.length(); i++) {
                        JSONObject position = snapshot.optJSONObject(i);
                        if (position == null) continue;
                        String currentBot = position.optString("bot_name");
                        int currentNumber = position.optInt("number", -1);
                        if (currentBot.isEmpty() || currentNumber < 0)
                            throw new IllegalStateException("В списке есть позиция без номера или робота");
                        String tabName = sourceNames.get(currentBot);
                        if (tabName == null) {
                            tabName = simpleSource(currentBot);
                            if (tabName == null)
                                throw new IllegalStateException("Нет вкладки Simple у робота " + currentBot);
                            sourceNames.put(currentBot, tabName);
                        }
                        targets.add(positionArguments(currentBot, tabName, currentNumber));
                    }
                    for (JSONObject target : targets)
                        callPositionTool("bot_position_close_at_market", target);
                } else if (section == 2) {
                    int journal = journalIndex(botId, number);
                    callPositionTool("bot_journal_delete_position", new JSONObject()
                        .put("bot_name", botId).put("tab_num", journal)
                        .put("position_number", number));
                } else {
                    String tabName = simpleSource(botId);
                    if (tabName == null)
                        throw new IllegalStateException("VPS API не вернул торговую вкладку Simple для робота");
                    callPositionTool(action == 1 ? "bot_position_close_at_market"
                        : "bot_position_delete", positionArguments(botId, tabName, number));
                }
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                positionActionRunning = false;
                if (isDestroyed()) return;
                Toast.makeText(this, finalError == null ? "Команда выполнена на VPS"
                    : "Не удалось выполнить действие: " + finalError, Toast.LENGTH_LONG).show();
                if ("Позиции".equals(page)) {
                    handler.removeCallbacks(positionsPolling);
                    handler.post(positionsPolling);
                }
            });
        });
    }

    private JSONObject positionArguments(String botId, String tabName, int number) throws Exception {
        return new JSONObject().put("bot_id", botId).put("tab_name", tabName)
            .put("position_number", number);
    }

    private void callPositionTool(String tool, JSONObject args) throws Exception {
        Object result = bridge.callBatch(terminal, McpBridge.call(tool, args)).get(tool);
        if (result instanceof Exception) throw (Exception) result;
        if (result == null) throw new IllegalStateException("Пустой ответ MCP: " + tool);
    }

    private String simpleSource(String botId) throws Exception {
        Object value = bridge.callBatch(terminal, McpBridge.call("bot_get_sources",
            new JSONObject().put("bot_id", botId))).get("bot_get_sources");
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject)) throw new IllegalStateException("Нет источников робота");
        JSONArray sources = ((JSONObject) value).optJSONArray("sources");
        if (sources == null) return null;
        for (int i = 0; i < sources.length(); i++) {
            JSONObject source = sources.optJSONObject(i);
            if (source != null && "Simple".equals(source.optString("type")))
                return source.optString("name");
        }
        return null;
    }

    private int journalIndex(String botId, int number) throws Exception {
        Object value = bridge.callBatch(terminal, McpBridge.call("bot_journal_get_panels",
            new JSONObject().put("bot_name", botId))).get("bot_journal_get_panels");
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject)) throw new IllegalStateException("Журнал робота недоступен");
        JSONArray journalBots = ((JSONObject) value).optJSONArray("bots");
        if (journalBots != null) for (int i = 0; i < journalBots.length(); i++) {
            JSONObject bot = journalBots.optJSONObject(i);
            if (bot == null || !botId.equals(bot.optString("bot_name"))) continue;
            JSONArray tabs = bot.optJSONArray("tabs");
            if (tabs == null) continue;
            for (int j = 0; j < tabs.length(); j++) {
                JSONObject tab = tabs.optJSONObject(j);
                if (tab == null) continue;
                JSONArray positions = tab.optJSONArray("positions");
                if (positions == null) continue;
                for (int k = 0; k < positions.length(); k++) {
                    String[] fields = positions.optString(k).split("#", 8);
                    if (fields.length > 6 && String.valueOf(number).equals(fields[6]))
                        return tab.optInt("tab_num", j);
                }
            }
        }
        throw new IllegalStateException("Позиция не найдена в журнале робота");
    }

    private void render() {
        renderNav();
        robotSection.setVisibility("Роботы".equals(page) ? View.VISIBLE : View.GONE);
        pageContent.removeAllViews();
        if ("Роботы".equals(page)) renderBots();
        else if ("Позиции".equals(page)) positionsPage.render(false);
        else if ("Ордера".equals(page)) ordersPage.render(false);
        else if ("Портфель".equals(page)) portfolioPage.render(false);
        else if ("Журнал".equals(page)) journalPage.render(false);
        else if ("Прайм лог".equals(page)) primeLogPage.render(false);
        else if ("Серверы".equals(page)) serversPage.render(false);
        else if ("Ещё".equals(page)) renderMore();
        else pending(pageContent, page);
    }

    private void renderNav() {
        HorizontalScrollView phoneScroll = tablet ? null : findViewById(R.id.phone_nav);
        int previousX = phoneScroll == null ? 0 : phoneScroll.getScrollX();
        nav.removeAllViews();
        String[] tabs = tablet
            ? new String[]{"Роботы", "Позиции", "Серверы", "Портфель", "Журнал", "Прайм лог", "Ордера"}
            : new String[]{"Роботы", "Позиции", "Серверы", "Портфель", "Журнал", "Ещё"};
        boolean connected = false;
        if (available) for (int i = 0; i < servers.length(); i++) {
            JSONObject server = servers.optJSONObject(i);
            if (server != null && "Connect".equalsIgnoreCase(server.optString("status")))
                connected = true;
        }
        for (String tab : tabs) {
            TextView item = label(tab, 13, "Серверы".equals(tab) && connected
                ? R.color.server_connected
                : tab.equals(page) ? R.color.orange : R.color.text_primary);
            item.setGravity(Gravity.CENTER);
            item.setPadding(dp(9), 0, dp(9), 0);
            item.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = tablet
                ? new LinearLayout.LayoutParams(0, dp(48), 1)
                : new LinearLayout.LayoutParams(dp(92), dp(48));
            params.rightMargin = dp(3);
            nav.addView(item, params);
            item.setOnClickListener(view -> {
                page = tab;
                handler.removeCallbacks(positionsPolling);
                handler.removeCallbacks(ordersPolling);
                handler.removeCallbacks(portfolioPolling);
                handler.removeCallbacks(journalPolling);
                handler.removeCallbacks(primeLogPolling);
                handler.removeCallbacks(serversPolling);
                ((ScrollView) findViewById(R.id.scroll_root)).smoothScrollTo(0, 0);
                render();
                if ("Позиции".equals(page)) handler.post(positionsPolling);
                if ("Ордера".equals(page)) handler.post(ordersPolling);
                if ("Портфель".equals(page)) handler.post(portfolioPolling);
                if ("Журнал".equals(page)) handler.post(journalPolling);
                if ("Прайм лог".equals(page)) handler.post(primeLogPolling);
                if ("Серверы".equals(page)) handler.post(serversPolling);
            });
        }
        if (phoneScroll != null) phoneScroll.post(() -> phoneScroll.scrollTo(previousX, 0));
    }

    private void renderBots() {
        botList.removeAllViews();
        ((TextView) findViewById(R.id.robot_count)).setText("Роботы · " + bots.length());
        findViewById(R.id.add_robot).setEnabled(available);
        findViewById(R.id.add_robot).setAlpha(available ? 1f : .45f);
        if (bots.length() == 0) {
            botList.addView(label(available ? "Роботов пока нет" : "Ожидание данных роботов…",
                15, R.color.text_secondary));
            return;
        }
        LinearLayout table = null;
        if (tablet) {
            HorizontalScrollView horizontal = new HorizontalScrollView(this);
            horizontal.setFillViewport(true);
            table = new LinearLayout(this);
            table.setOrientation(LinearLayout.VERTICAL);
            horizontal.addView(table);
            botList.addView(horizontal);
            LinearLayout headings = new LinearLayout(this);
            String[] titles = {"#", "Имя робота", "Тип", "Первая бумага", "Поз (откр/закр)",
                "Вкл/выкл", "Эмулятор", "Чарт", "Удалить", "Журнал"};
            int[] widths = {40, 140, 110, 140, 130, 80, 90, 80, 90, 90};
            for (int i = 0; i < titles.length; i++)
                headings.addView(tableCell(titles[i], widths[i], R.color.text_secondary));
            table.addView(headings);
        }
        for (int i = 0; i < bots.length(); i++) {
            JSONObject bot = bots.optJSONObject(i);
            if (bot == null) continue;
            if (tablet) table.addView(botRow(bot));
            else botList.addView(botCard(bot));
        }
    }

    private View botRow(JSONObject bot) {
        LinearLayout row = new LinearLayout(this);
        row.setBackgroundResource(R.drawable.input_background);
        String name = bot.optString("public_name");
        if (name.isEmpty()) name = bot.optString("name");
        row.addView(tableCell(String.valueOf(bot.optInt("number")), 40, R.color.text_primary));
        TextView robotName = tableCell(name, 140, R.color.text_primary);
        robotName.setOnClickListener(view -> openRobot(bot));
        row.addView(robotName);
        row.addView(tableCell(bot.optString("class_name"), 110, R.color.text_primary));
        row.addView(tableCell(bot.optString("first_security"), 140, R.color.text_primary));
        row.addView(tableCell(bot.optInt("open_positions_count") + "/"
            + bot.optInt("closed_positions_count"), 130, R.color.text_primary));
        CheckBox on = check("", bot.optBoolean("is_on"));
        CheckBox emulator = check("", bot.optBoolean("emulator_is_on"));
        row.addView(on, new LinearLayout.LayoutParams(0, dp(48), 0.8f));
        row.addView(emulator, new LinearLayout.LayoutParams(0, dp(48), 0.9f));
        on.setOnClickListener(view -> changeState(bot, "is_on", on.isChecked()));
        emulator.setOnClickListener(view -> changeState(bot, "emulator_is_on", emulator.isChecked()));
        for (String action : new String[]{"Чарт", "Удалить", "Журнал"}) {
            int width = "Удалить".equals(action) ? 90
                : "Журнал".equals(action) ? 90 : 80;
            TextView button = tableCell(action, width, R.color.orange);
            row.addView(button);
            button.setOnClickListener(view -> {
                if ("Чарт".equals(action)) openRobot(bot);
                else if ("Журнал".equals(action)) openRobotJournal(bot);
                else Toast.makeText(this, "Раздел «" + action
                    + "» будет подключён на следующем этапе", Toast.LENGTH_LONG).show();
            });
        }
        return row;
    }

    private TextView tableCell(String value, int width, int color) {
        TextView cell = label(value, 13, color);
        cell.setPadding(dp(5), 0, dp(5), 0);
        cell.setSingleLine(true);
        return withWidth(cell, width);
    }

    private TextView withWidth(TextView cell, int width) {
        cell.setLayoutParams(new LinearLayout.LayoutParams(0, dp(48), width / 100f));
        return cell;
    }

    private View botCard(JSONObject bot) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(14), dp(10), dp(14), dp(10));
        card.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(-1, -2);
        cardParams.bottomMargin = dp(8);
        card.setLayoutParams(cardParams);
        String name = bot.optString("public_name");
        if (name.isEmpty()) name = bot.optString("name");
        LinearLayout top = new LinearLayout(this);
        top.setOrientation(LinearLayout.HORIZONTAL);
        top.setGravity(Gravity.CENTER_VERTICAL);
        TextView heading = label(bot.optInt("number") + "   " + name + "   ›",
            17, R.color.text_primary);
        heading.setTypeface(null, Typeface.BOLD);
        top.addView(heading, new LinearLayout.LayoutParams(0, dp(44), 1));
        card.addView(top);
        LinearLayout buttons = new LinearLayout(this);
        buttons.setOrientation(LinearLayout.HORIZONTAL);
        String[] titles = {"Журнал", "Чарт"};
        for (int i = 0; i < titles.length; i++) {
            final String title = titles[i];
            TextView button = label(title, 14, R.color.orange);
            button.setGravity(Gravity.CENTER);
            button.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams buttonParams = new LinearLayout.LayoutParams(0, dp(44), 1);
            if (i > 0) buttonParams.leftMargin = dp(6);
            buttons.addView(button, buttonParams);
            button.setOnClickListener(view -> {
                if ("Журнал".equals(title)) openRobotJournal(bot);
                else openRobot(bot);
            });
        }
        card.addView(buttons);
        card.addView(label(bot.optString("class_name") + " · "
            + bot.optString("first_security", "—"), 14, R.color.text_secondary));
        card.addView(label("Поз (откр/закр): " + bot.optInt("open_positions_count")
            + "/" + bot.optInt("closed_positions_count"), 14, R.color.text_primary));
        LinearLayout switches = new LinearLayout(this);
        switches.setOrientation(LinearLayout.HORIZONTAL);
        CheckBox on = check("Вкл", bot.optBoolean("is_on"));
        CheckBox emulator = check("Эмулятор", bot.optBoolean("emulator_is_on"));
        switches.addView(on, new LinearLayout.LayoutParams(0, dp(48), 1));
        switches.addView(emulator, new LinearLayout.LayoutParams(0, dp(48), 1));
        card.addView(switches);
        on.setOnClickListener(view -> changeState(bot, "is_on", on.isChecked()));
        emulator.setOnClickListener(view -> changeState(bot, "emulator_is_on", emulator.isChecked()));
        card.setOnClickListener(view -> openRobot(bot));
        return card;
    }

    private void openRobot(JSONObject bot) {
        String id = bot.optString("name");
        if (id.isEmpty()) {
            Toast.makeText(this, "У робота нет имени на VPS", Toast.LENGTH_LONG).show();
            return;
        }
        // The robot list does not expose tab types. Resolve them on the destination screen.
        Intent intent = new Intent(this, RobotEntryActivity.class);
        intent.putExtra("terminal_name", terminal);
        intent.putExtra("bot_id", id);
        intent.putExtra("bot_name", (bot.optString("public_name").isEmpty() ? id : bot.optString("public_name")));
        startActivity(intent);
    }

    /** Robot-level journal: all tickers of the robot (screener), not one security. */
    private void openRobotJournal(JSONObject bot) {
        String id = bot.optString("name");
        if (id.isEmpty()) return;
        Intent intent = new Intent(this, ScreenerJournalActivity.class);
        intent.putExtra("terminal_name", terminal);
        intent.putExtra("bot_id", id);
        intent.putExtra("bot_name", (bot.optString("public_name").isEmpty() ? id : bot.optString("public_name")));
        startActivity(intent);
    }

    private void changeState(JSONObject bot, String field, boolean enabled) {
        if (!available) return;
        String id = bot.optString("name");
        if (id.isEmpty()) return;
        loading = true;
        worker.execute(() -> {
            String error = null;
            try {
                JSONObject arguments = new JSONObject().put("bot_id", id).put(field, enabled);
                Object result = bridge.callBatch(terminal,
                    McpBridge.call("bot_set_state", arguments)).get("bot_set_state");
                if (result instanceof Exception) throw (Exception) result;
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (finalError != null)
                    Toast.makeText(this, "Не удалось изменить робота: " + finalError,
                        Toast.LENGTH_LONG).show();
                render();
                load();
            });
        });
    }

    private void renderMore() {
        TextView alerts = label("Окно оповещений  ›", 16, R.color.text_primary);
        alerts.setPadding(dp(14), dp(14), dp(14), dp(14));
        alerts.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams alertParams = new LinearLayout.LayoutParams(-1, -2);
        alertParams.bottomMargin = dp(8);
        pageContent.addView(alerts, alertParams);
        alerts.setOnClickListener(view -> startActivity(new Intent(this, AlertsActivity.class)));
        for (String item : new String[]{"Ордера", "Прайм лог"}) {
            TextView row = label(item + "  ›", 16, R.color.text_primary);
            row.setPadding(dp(14), dp(14), dp(14), dp(14));
            row.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            pageContent.addView(row, params);
            row.setOnClickListener(view -> {
                page = item;
                render();
                if ("Ордера".equals(item)) handler.post(ordersPolling);
                if ("Прайм лог".equals(item)) handler.post(primeLogPolling);
            });
        }
    }

    private void pending(LinearLayout target, String title) {
        target.addView(label(title, 19, R.color.text_primary));
        TextView message = label("Данные этого раздела будут подключены на следующем этапе.",
            15, R.color.text_secondary);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(16);
        target.addView(message, params);
        if (!tablet) {
            TextView back = label("‹ Ещё", 16, R.color.orange);
            back.setPadding(0, dp(14), 0, dp(14));
            target.addView(back);
            back.setOnClickListener(view -> { page = "Ещё"; render(); });
        }
    }

    private void addBot() {
        if (!available) return;
        Intent intent = new Intent(this, AddBotActivity.class);
        intent.putExtra("terminal_name", terminal);
        startActivity(intent);
    }


    private CheckBox check(String title, boolean checked) {
        CheckBox box = new CheckBox(this);
        box.setText(title);
        box.setTextColor(getColor(R.color.text_primary));
        box.setButtonTintList(ColorStateList.valueOf(getColor(R.color.orange)));
        box.setChecked(checked);
        box.setEnabled(available && !loading);
        return box;
    }

    private TextView label(String text, int size, int color) {
        TextView view = new TextView(this);
        view.setText(text);
        view.setTextColor(getColor(color));
        view.setTextSize(size);
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
