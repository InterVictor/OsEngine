package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Remote counterpart of RobotsVpsComparePositionsUi for one portfolio. */
public final class ComparePositionsActivity extends Activity {
    private static final String[] HEADERS = {"Портфель", "Инструмент", "Статус",
        "Роботы Лонг", "Роботы Шорт", "Роботы Общее", "Портфель Лонг",
        "Портфель Шорт", "Портфель Общее", "Игнор", ""};
    private static final int[] WIDTHS = {145, 160, 100, 110, 110, 110, 110, 110, 110, 80, 90};
    private static final String[] PERIODS = {"Min1", "Min5", "Min10", "Min30"};
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::refresh;
    private McpBridge bridge;
    private String terminal;
    private String type;
    private int number;
    private String portfolioName;
    private boolean visible;
    private boolean busy;
    private boolean loaded;
    private JSONArray securities = new JSONArray();
    private JSONArray watched = new JSONArray();
    private JSONArray ignored = new JSONArray();
    private String period = "Min1";
    private int delay = 20;
    private TextView message;
    private TextView periodButton;
    private TextView delayButton;
    private TextView syncButton;
    private CheckBox autoMessage;
    private LinearLayout table;
    private HorizontalScrollView horizontal;
    private ScrollView vertical;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        type = getIntent().getStringExtra("server_type");
        number = getIntent().getIntExtra("server_number", 0);
        portfolioName = getIntent().getStringExtra("portfolio_name");
        if (terminal == null || type == null || type.isEmpty() || portfolioName == null) {
            finish(); return;
        }
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        buildScreen();
    }

    @Override protected void onResume() {
        super.onResume();
        visible = true;
        refresh();
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
        LinearLayout root = column();
        root.setBackgroundColor(getColor(R.color.background));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.ime());
                top = bars.top; bottom = bars.bottom;
            } else {
                top = insets.getSystemWindowInsetTop();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(0, top, 0, bottom);
            return insets;
        });
        setContentView(root);
        root.requestApplyInsets();
        TextView back = text("‹ Портфель · " + terminal, 14, R.color.orange);
        back.setPadding(dp(14), 0, 0, 0);
        back.setOnClickListener(view -> finish());
        root.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        TextView title = text("Модуль сравнения позиций", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        title.setPadding(dp(14), 0, dp(14), 0);
        root.addView(title, new LinearLayout.LayoutParams(-1, dp(38)));
        TextView connection = text("Подключение: " + type + "-" + number, 14, R.color.orange);
        connection.setPadding(dp(14), 0, dp(14), 0);
        root.addView(connection, new LinearLayout.LayoutParams(-1, dp(35)));
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        root.addView(strip, new LinearLayout.LayoutParams(-1, dp(2)));
        message = text("Загрузка с VPS…", 12, R.color.text_secondary);
        message.setPadding(dp(14), dp(7), dp(14), dp(7));
        root.addView(message);

        vertical = new BarScrollView(this);
        root.addView(vertical, new LinearLayout.LayoutParams(-1, 0, 1));
        horizontal = new HorizontalScrollView(this);
        vertical.addView(horizontal);
        table = column();
        horizontal.addView(table);

        LinearLayout footer = column();
        footer.setPadding(dp(14), dp(5), dp(14), dp(10));
        root.addView(footer);
        LinearLayout first = row();
        footer.addView(first);
        autoMessage = new CheckBox(this);
        autoMessage.setText("Автоматическое сообщение об ошибке");
        autoMessage.setTextSize(12);
        autoMessage.setTextColor(getColor(R.color.text_primary));
        autoMessage.setButtonTintList(getColorStateList(R.color.orange));
        first.addView(autoMessage, new LinearLayout.LayoutParams(0, dp(48), 2));
        syncButton = button("Синхронизировать");
        first.addView(syncButton, new LinearLayout.LayoutParams(0, dp(44), 1));
        LinearLayout second = row();
        second.setGravity(Gravity.CENTER_VERTICAL);
        footer.addView(second);
        second.addView(text("Период проверки", 12, R.color.orange),
            new LinearLayout.LayoutParams(0, dp(48), 2));
        periodButton = button(period);
        second.addView(periodButton, new LinearLayout.LayoutParams(0, dp(42), 1));
        LinearLayout third = row();
        third.setGravity(Gravity.CENTER_VERTICAL);
        footer.addView(third);
        third.addView(text("Задержка в секундах", 12, R.color.orange),
            new LinearLayout.LayoutParams(0, dp(48), 2));
        delayButton = button(String.valueOf(delay));
        third.addView(delayButton, new LinearLayout.LayoutParams(0, dp(42), 1));
        autoMessage.setOnCheckedChangeListener((button, checked) -> changeAutoMessage(checked));
        periodButton.setOnClickListener(view -> new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle("Период проверки").setItems(PERIODS,
                (dialog, which) -> setSetting("verification_period", PERIODS[which])).show());
        delayButton.setOnClickListener(view -> editDelay());
        syncButton.setOnClickListener(view -> synchronize(null));
        render();
    }

    private JSONObject args() throws Exception {
        return new JSONObject().put("server_type", type).put("number", number);
    }

    private void refresh() {
        handler.removeCallbacks(polling);
        if (!visible || busy) return;
        if (!RemoteSsh.isConnected()) {
            message.setText("Нет связи · SSH");
            updateControls();
            handler.postDelayed(polling, 5_000);
            return;
        }
        busy = true;
        updateControls();
        worker.execute(() -> {
            JSONArray nextRows = null;
            JSONObject nextSettings = null;
            String error = null;
            try {
                java.util.Map<String, Object> response = bridge.callBatch(terminal,
                    McpBridge.call("compare_positions_get", args()),
                    McpBridge.call("compare_positions_get_settings", args()));
                Object positions = response.get("compare_positions_get");
                Object settings = response.get("compare_positions_get_settings");
                if (positions instanceof Exception) throw (Exception) positions;
                if (settings instanceof Exception) throw (Exception) settings;
                JSONArray portfolios = ((JSONObject) positions).getJSONArray("portfolios");
                for (int i = 0; i < portfolios.length(); i++) {
                    JSONObject item = portfolios.optJSONObject(i);
                    if (item != null && portfolioName.equals(item.optString("portfolio_name"))) {
                        nextRows = item.optJSONArray("securities");
                        break;
                    }
                }
                if (nextRows == null) nextRows = new JSONArray();
                nextSettings = (JSONObject) settings;
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray rows = nextRows;
            JSONObject settings = nextSettings;
            String failure = error;
            runOnUiThread(() -> {
                busy = false;
                if (!visible || isDestroyed()) return;
                if (failure == null) {
                    loaded = true;
                    securities = rows;
                    watched = settings.optJSONArray("portfolios_to_watch");
                    ignored = settings.optJSONArray("ignored_securities");
                    if (watched == null) watched = new JSONArray();
                    if (ignored == null) ignored = new JSONArray();
                    period = settings.optString("verification_period", "Min1");
                    delay = settings.optInt("time_delay_seconds", 20);
                    message.setText("");
                    render();
                } else {
                    message.setText("Не удалось обновить сравнение: " + failure);
                    updateControls();
                }
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private void render() {
        int x = horizontal.getScrollX();
        int y = vertical.getScrollY();
        table.removeAllViews();
        LinearLayout heading = row();
        for (int i = 0; i < HEADERS.length; i++)
            heading.addView(cell(HEADERS[i], WIDTHS[i], R.color.text_secondary));
        table.addView(heading);
        if (loaded) {
            int longCount = 0, shortCount = 0;
            for (int i = 0; i < securities.length(); i++) {
                JSONObject item = securities.optJSONObject(i);
                if (item == null) continue;
                if (number(item, "robots_long").signum() > 0) longCount++;
                if (number(item, "robots_short").signum() < 0) shortCount++;
            }
            String[] summary = {portfolioName, "", "", String.valueOf(longCount),
                String.valueOf(shortCount), "", "", "", "", "", ""};
            addTextRow(summary, R.color.text_primary);
            for (int i = 0; i < securities.length(); i++) {
                JSONObject item = securities.optJSONObject(i);
                if (item != null) addSecurityRow(item);
            }
        }
        horizontal.post(() -> horizontal.scrollTo(x, 0));
        vertical.post(() -> vertical.scrollTo(0, y));
        autoMessage.setOnCheckedChangeListener(null);
        autoMessage.setChecked(contains(watched, portfolioName));
        autoMessage.setOnCheckedChangeListener((button, checked) -> changeAutoMessage(checked));
        periodButton.setText(period);
        delayButton.setText(String.valueOf(delay));
        updateControls();
    }

    private void addTextRow(String[] values, int color) {
        LinearLayout line = row();
        for (int i = 0; i < values.length; i++) line.addView(cell(values[i], WIDTHS[i], color));
        table.addView(line);
    }

    private void addSecurityRow(JSONObject item) {
        String security = item.optString("security");
        boolean isIgnored = contains(ignored, security);
        String state = item.optString("status");
        String[] values = {"", security, state, value(item, "robots_long"),
            value(item, "robots_short"), value(item, "robots_common"),
            value(item, "portfolio_long"), value(item, "portfolio_short"),
            value(item, "portfolio_common")};
        LinearLayout line = row();
        for (int i = 0; i < values.length; i++) {
            int color = isIgnored ? R.color.text_secondary
                : i == 2 ? "Normal".equals(state) ? R.color.server_connected : R.color.compare_error
                : R.color.text_primary;
            line.addView(cell(values[i], WIDTHS[i], color));
        }
        CheckBox check = new CheckBox(this);
        check.setGravity(Gravity.CENTER);
        check.setButtonTintList(getColorStateList(R.color.orange));
        check.setBackgroundResource(R.drawable.position_cell);
        check.setChecked(isIgnored);
        check.setEnabled(loaded && !busy && RemoteSsh.isConnected());
        check.setOnCheckedChangeListener((button, checked) -> changeIgnored(security, checked));
        line.addView(check, new LinearLayout.LayoutParams(dp(WIDTHS[9]), dp(43)));
        TextView sync = cell("Error".equals(state) && !isIgnored ? "Синх." : "",
            WIDTHS[10], R.color.text_primary);
        if ("Error".equals(state) && !isIgnored)
            sync.setOnClickListener(view -> synchronize(item));
        line.addView(sync);
        table.addView(line);
    }

    private void updateControls() {
        boolean enabled = loaded && !busy && RemoteSsh.isConnected();
        autoMessage.setEnabled(enabled);
        periodButton.setEnabled(enabled);
        delayButton.setEnabled(enabled);
        syncButton.setEnabled(enabled);
        syncButton.setAlpha(enabled ? 1f : .5f);
    }

    private void changeAutoMessage(boolean enabled) {
        JSONArray next = without(watched, portfolioName);
        if (enabled) next.put(portfolioName);
        setSetting("portfolios_to_watch", next);
    }

    private void changeIgnored(String security, boolean enabled) {
        JSONArray next = without(ignored, security);
        if (enabled) next.put(security);
        command("compare_positions_set_ignored", () -> args().put("securities", next), null);
    }

    private void editDelay() {
        if (!loaded || busy) return;
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);
        input.setText(String.valueOf(delay));
        input.setTextColor(getColor(R.color.text_primary));
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle("Задержка в секундах").setView(input)
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                try {
                    int selected = Integer.parseInt(input.getText().toString());
                    if (selected <= 0) throw new NumberFormatException();
                    setSetting("time_delay_seconds", selected);
                } catch (NumberFormatException e) { message.setText("Введите целое число больше нуля"); }
            }).show();
    }

    private void setSetting(String key, Object value) {
        command("compare_positions_set_settings", () -> args().put(key, value), null);
    }

    private void synchronize(JSONObject one) {
        if (!loaded || busy || !RemoteSsh.isConnected()) return;
        String orders;
        if (one != null) orders = formatOrder(one);
        else {
            StringBuilder list = new StringBuilder();
            for (int i = 0; i < securities.length(); i++) {
                JSONObject item = securities.optJSONObject(i);
                if (item == null || !"Error".equals(item.optString("status"))
                    || contains(ignored, item.optString("security"))) continue;
                if (list.length() > 0) list.append('\n');
                list.append(formatOrder(item));
            }
            if (list.length() == 0) {
                new AlertDialog.Builder(this, R.style.OsEngineDialog)
                    .setMessage("Расхождений нет. Все позиции синхронизированы.")
                    .setPositiveButton("ОК", null).show();
                return;
            }
            orders = list.toString();
        }
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle("Модуль сравнения позиций")
            .setMessage("Будут отправлены следующие рыночные ордера:\n" + orders + "\n\nПродолжить?")
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                String tool = one == null ? "compare_positions_sync_all" : "compare_positions_sync_this";
                command(tool, () -> {
                    JSONObject args = args().put("portfolio_name", portfolioName);
                    if (one != null) args.put("security_name", one.optString("security"));
                    return args;
                }, tool);
            }).show();
    }

    private interface Arguments { JSONObject get() throws Exception; }

    private void command(String tool, Arguments arguments, String syncTool) {
        if (busy || !RemoteSsh.isConnected()) {
            message.setText("Нет связи · SSH"); return;
        }
        busy = true;
        updateControls();
        worker.execute(() -> {
            JSONObject response = null;
            String error = null;
            try {
                Object value = bridge.callBatch(terminal, McpBridge.call(tool, arguments.get()))
                    .get(tool);
                if (value instanceof Exception) throw (Exception) value;
                response = (JSONObject) value;
            } catch (Exception e) { error = e.getMessage(); }
            JSONObject result = response;
            String failure = error;
            runOnUiThread(() -> {
                busy = false;
                if (!visible || isDestroyed()) return;
                if (failure != null) {
                    message.setText("Не удалось выполнить действие: " + failure);
                    updateControls();
                    handler.postDelayed(polling, 5_000);
                    return;
                }
                if (syncTool != null) {
                    String info = "compare_positions_sync_all".equals(syncTool)
                        ? "Отправлено ордеров: " + result.optInt("sent_count")
                        : result.optBoolean("sent") ? "Ордер отправлен" : "Ордер не отправлен";
                    new AlertDialog.Builder(this, R.style.OsEngineDialog)
                        .setMessage(info).setPositiveButton("ОК", null).show();
                }
                refresh();
            });
        });
    }

    private static String formatOrder(JSONObject item) {
        BigDecimal robots = number(item, "robots_common");
        BigDecimal portfolio = number(item, "portfolio_common");
        BigDecimal difference = robots.subtract(portfolio);
        String direction = difference.signum() > 0 ? "Купить" : "Продать";
        String action = portfolio.signum() == 0
            || difference.signum() > 0 && portfolio.signum() > 0
            || difference.signum() < 0 && portfolio.signum() < 0
                ? "Позиция будет открыта или увеличена" : "Позиция будет сокращена или закрыта";
        return item.optString("security") + ": " + direction + " "
            + difference.abs().stripTrailingZeros().toPlainString() + ". " + action
            + " (robots " + robots.stripTrailingZeros().toPlainString()
            + ", portfolio " + portfolio.stripTrailingZeros().toPlainString() + ")";
    }

    private static BigDecimal number(JSONObject item, String name) {
        try { return new BigDecimal(item.optString(name, "0")); }
        catch (Exception e) { return BigDecimal.ZERO; }
    }

    private static String value(JSONObject item, String name) {
        return number(item, name).stripTrailingZeros().toPlainString();
    }

    private static boolean contains(JSONArray values, String name) {
        for (int i = 0; i < values.length(); i++)
            if (name.equals(values.optString(i))) return true;
        return false;
    }

    private static JSONArray without(JSONArray values, String name) {
        JSONArray next = new JSONArray();
        for (int i = 0; i < values.length(); i++) {
            String value = values.optString(i);
            if (!name.equals(value)) next.put(value);
        }
        return next;
    }

    private LinearLayout column() { LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.VERTICAL); return view; }
    private LinearLayout row() { LinearLayout view = new LinearLayout(this);
        view.setOrientation(LinearLayout.HORIZONTAL); return view; }
    private TextView text(String value, int size, int color) { TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView button(String value) { TextView view = text(value, 13, R.color.text_primary);
        view.setGravity(Gravity.CENTER); view.setBackgroundResource(R.drawable.input_background);
        return view; }
    private TextView cell(String value, int width, int color) { TextView view = text(value, 12, color);
        view.setPadding(dp(6), dp(7), dp(6), dp(7));
        view.setBackgroundResource(R.drawable.position_cell);
        view.setMinHeight(dp(43));
        view.setLayoutParams(new LinearLayout.LayoutParams(dp(width), -2));
        return view; }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
