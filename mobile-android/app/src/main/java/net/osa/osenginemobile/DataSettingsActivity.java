package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** «Подключение потока данных» of a screener tab (RobotsVpsScreenerSettingsUi). */
public final class DataSettingsActivity extends Activity {
    private static final String[] TIME_FRAMES = {"Sec1", "Sec2", "Sec5", "Sec10", "Sec15", "Sec20",
        "Sec30", "Min1", "Min2", "Min3", "Min5", "Min10", "Min15", "Min20", "Min30", "Min45",
        "Hour1", "Hour2", "Hour4", "Day"};

    private static final class Field {
        final String key, label, kind;
        final String[] options;
        Object original, value;
        Field(String key, String label, String kind, String... options) {
            this.key = key; this.label = label; this.kind = kind; this.options = options;
        }
        boolean changed() {
            if (value == null || original == null) return value != original;
            if (value instanceof Number && original instanceof Number)
                return new BigDecimal(value.toString()).compareTo(new BigDecimal(original.toString())) != 0;
            return !value.equals(original);
        }
    }

    private static final class Security {
        final String name, className;
        final boolean original;
        boolean on;
        Security(JSONObject json) {
            name = json.optString("name");
            className = json.optString("class_name");
            original = on = json.optBoolean("is_on");
        }
    }

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Map<String, Field> fields = new LinkedHashMap<>();
    private final ArrayList<Security> securities = new ArrayList<>();
    private McpBridge bridge;
    private String terminal, botId, botName, tabName;
    private String serverType = "", serverName = "", simpleSecurity = "";
    private boolean loaded, busy, destroyed, resolved, simple;
    private final ArrayList<String> seriesKeys = new ArrayList<>();
    private String search = "";
    private LinearLayout form, securityList;
    private TextView statusView, accept, counter;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        botName = getIntent().getStringExtra("bot_name");
        tabName = getIntent().getStringExtra("tab_name");
        if (terminal == null || botId == null || tabName == null || tabName.isEmpty()) { finish(); return; }
        if (botName == null || botName.isEmpty()) botName = botId;
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        define();
        buildScreen();
        load();
    }

    @Override protected void onDestroy() {
        destroyed = true;
        worker.shutdownNow();
        super.onDestroy();
    }

    private void add(Field field) { fields.put(field.key, field); }

    private void define() {
        add(new Field("portfolio_name", "Портфель", "text"));
        add(new Field("emulator_is_on", "Исполнять сделки в эмуляторе", "bool"));
        add(new Field("commission_type", "Тип комиссии", "enum", "None", "Percent", "OneLotFix"));
        add(new Field("commission_value", "Значение комиссии", "decimal"));
        add(new Field("candle_market_data_type", "Из чего собираем свечи", "enum", "Tick", "MarketDepth"));
        add(new Field("save_trades_in_candles", "Сохранять трейды в свече", "bool"));
        add(new Field("candle_create_method_type", "Тип свечей", "enum", "Simple", "Renko", "Volume",
            "Ticks", "Delta", "HeikenAshi", "Revers", "Range"));
        add(new Field("time_frame", "ТаймФрейм", "enum", TIME_FRAMES));
    }

    private void buildScreen() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(getColor(R.color.background));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
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
        setContentView(root);
        root.requestApplyInsets();
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setPadding(dp(14), dp(12), dp(14), 0);
        root.addView(header);
        TextView back = text("‹ " + botName + " · Чарт", 15, R.color.orange);
        back.setOnClickListener(view -> onBackPressed());
        header.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        TextView title = text("Подключение потока данных — " + botName, 18, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(2));
        stripParams.bottomMargin = dp(8);
        header.addView(strip, stripParams);
        statusView = text("Загрузка с VPS…", 12, R.color.text_secondary);
        header.addView(statusView);
        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        form = new LinearLayout(this);
        form.setOrientation(LinearLayout.VERTICAL);
        form.setPadding(dp(14), dp(6), dp(14), dp(12));
        scroll.addView(form);
        LinearLayout buttons = new LinearLayout(this);
        buttons.setPadding(dp(14), dp(6), dp(14), dp(8));
        root.addView(buttons);
        accept = text("Принять", 14, R.color.text_primary);
        accept.setGravity(Gravity.CENTER);
        accept.setBackgroundResource(R.drawable.input_background);
        buttons.addView(accept, new LinearLayout.LayoutParams(-1, dp(44)));
        accept.setOnClickListener(view -> submit());
        refreshAccept();
    }

    @Override public void onBackPressed() {
        if (changedCount() == 0) { super.onBackPressed(); return; }
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setMessage("Есть неотправленные изменения (" + changedCount() + "). Закрыть без отправки?")
            .setNegativeButton("Остаться", null)
            .setPositiveButton("Закрыть", (dialog, which) -> finish()).show();
    }

    private void load() {
        if (!RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
        busy = true;
        refreshAccept();
        worker.execute(() -> {
            JSONObject result = null;
            String error = null;
            try {
                if (!resolved) resolveScreener();
                String getTool = simple ? "bot_get_config_tab_simple" : "bot_get_config_tab_screener";
                Object response = bridge.callBatch(terminal, McpBridge.call(getTool,
                    new JSONObject().put("bot_id", botId).put("tab_name", tabName)))
                    .get(getTool);
                if (response instanceof Exception) throw (Exception) response;
                if (!(response instanceof JSONObject)) throw new IllegalStateException("Пустой ответ VPS");
                result = (JSONObject) response;
            } catch (Exception e) { error = e.getMessage(); }
            JSONObject data = result;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) {
                    statusView.setText("Не удалось загрузить: " + finalError);
                    refreshAccept();
                    return;
                }
                serverType = data.optString("server_type");
                serverName = data.optString("server_name", data.optString("server_full_name"));
                if (simple) {
                    simpleSecurity = data.optString("security_class") + " " + data.optString("security_name");
                    JSONArray methods = data.optJSONArray("candle_create_method_types");
                    if (methods != null && methods.length() > 0) {
                        String[] types = new String[methods.length()];
                        for (int i = 0; i < types.length; i++) types[i] = methods.optString(i);
                        fields.put("candle_create_method_type", new Field("candle_create_method_type",
                            "Тип свечей", "enum", types));
                    }
                    for (String key : seriesKeys) fields.remove(key);
                    seriesKeys.clear();
                }
                for (Field field : fields.values()) {
                    Object raw = data.opt(field.key);
                    field.original = raw == null || raw == JSONObject.NULL ? null : raw;
                    field.value = field.original;
                }
                JSONArray series = simple ? data.optJSONArray("candle_series_parameters") : null;
                if (series != null) for (int i = 0; i < series.length(); i++) {
                    JSONObject item = series.optJSONObject(i);
                    if (item == null) continue;
                    JSONArray values = item.optJSONArray("values");
                    String type = item.optString("type");
                    String[] options = new String[values == null ? 0 : values.length()];
                    for (int j = 0; j < options.length; j++) options[j] = values.optString(j);
                    String kind = "Bool".equalsIgnoreCase(type) ? "bool" : options.length > 0 ? "enum"
                        : "Int".equalsIgnoreCase(type) || "Decimal".equalsIgnoreCase(type) ? "decimal" : "text";
                    Field field = new Field("series:" + item.optString("sys_name"),
                        item.optString("label", item.optString("sys_name")), kind, options);
                    Object raw = item.opt("value");
                    field.original = field.value = raw == JSONObject.NULL ? null : raw;
                    fields.put(field.key, field);
                    seriesKeys.add(field.key);
                }
                securities.clear();
                JSONArray array = data.optJSONArray("securities");
                if (array != null) for (int i = 0; i < array.length(); i++) {
                    JSONObject item = array.optJSONObject(i);
                    if (item != null) securities.add(new Security(item));
                }
                loaded = true;
                renderForm();
                refreshAccept();
            });
        });
    }

    /** The chart knows a child tab name; the config tools want the Screener source name. */
    private void resolveScreener() throws Exception {
        Object response = bridge.callBatch(terminal, McpBridge.call("bot_get_sources",
            new JSONObject().put("bot_id", botId))).get("bot_get_sources");
        if (response instanceof Exception) throw (Exception) response;
        if (!(response instanceof JSONObject)) throw new IllegalStateException("Нет вкладок робота");
        JSONArray sources = ((JSONObject) response).optJSONArray("sources");
        String first = null;
        if (sources != null) for (int i = 0; i < sources.length(); i++) {
            JSONObject source = sources.optJSONObject(i);
            if (source != null && "Simple".equals(source.optString("type"))
                && source.optString("name").equals(tabName)) { simple = true; resolved = true; return; }
            if (source == null || !"Screener".equals(source.optString("type"))) continue;
            String name = source.optString("name");
            if (first == null) first = name;
            if (name.equals(tabName)) { resolved = true; return; }
            Object tabsResponse = bridge.callBatch(terminal, McpBridge.call("bot_screener_get_tabs",
                new JSONObject().put("bot_id", botId).put("tab_name", name))).get("bot_screener_get_tabs");
            if (!(tabsResponse instanceof JSONObject)) continue;
            JSONArray tabs = ((JSONObject) tabsResponse).optJSONArray("tabs");
            if (tabs != null) for (int j = 0; j < tabs.length(); j++) {
                JSONObject child = tabs.optJSONObject(j);
                if (child != null && tabName.equals(child.optString("tab_name"))) {
                    tabName = name;
                    resolved = true;
                    return;
                }
            }
        }
        if (first == null) throw new IllegalStateException("У робота нет вкладки скринера или Simple с таким именем");
        tabName = first;
        resolved = true;
    }

    private void renderForm() {
        form.removeAllViews();
        heading("Торговый сервер");
        form.addView(readOnlyRow("Сервер", serverName.isEmpty() ? serverType : serverName));
        heading("Исполнение ордеров");
        for (String key : new String[]{"portfolio_name", "emulator_is_on", "commission_type", "commission_value"})
            form.addView(row(fields.get(key)));
        heading("Настройки свечей");
        for (String key : new String[]{"candle_market_data_type", "save_trades_in_candles",
            "candle_create_method_type"}) form.addView(row(fields.get(key)));
        if (simple) {
            for (String key : seriesKeys) form.addView(row(fields.get(key)));
            heading("Инструмент");
            form.addView(readOnlyRow("Бумага", simpleSecurity));
            return;
        }
        form.addView(row(fields.get("time_frame")));
        heading("Инструменты");
        counter = text("", 12, R.color.text_secondary);
        form.addView(counter);
        EditText find = new EditText(this);
        find.setHint("поиск...");
        find.setSingleLine(true);
        find.setTextColor(getColor(R.color.text_primary));
        find.setText(search);
        find.addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void afterTextChanged(Editable s) {
                search = s.toString().trim().toUpperCase();
                renderSecurities();
            }
        });
        form.addView(find);
        TextView all = text("Выбрать все", 13, R.color.orange);
        all.setGravity(Gravity.CENTER);
        all.setBackgroundResource(R.drawable.input_background);
        all.setOnClickListener(view -> {
            boolean enable = false;
            for (Security item : visibleSecurities()) if (!item.on) enable = true;
            for (Security item : visibleSecurities()) item.on = enable;
            renderSecurities();
            refreshAccept();
        });
        form.addView(all, new LinearLayout.LayoutParams(-1, dp(40)));
        securityList = new LinearLayout(this);
        securityList.setOrientation(LinearLayout.VERTICAL);
        form.addView(securityList);
        renderSecurities();
    }

    private ArrayList<Security> visibleSecurities() {
        ArrayList<Security> list = new ArrayList<>();
        for (Security item : securities)
            if (search.isEmpty() || item.name.toUpperCase().contains(search)) list.add(item);
        return list;
    }

    private void renderSecurities() {
        securityList.removeAllViews();
        int selected = 0;
        for (Security item : securities) if (item.on) selected++;
        counter.setText("selected: " + selected + " / " + securities.size());
        for (Security item : visibleSecurities()) {
            CheckBox box = new CheckBox(this);
            box.setText(item.name + "   " + item.className);
            box.setTextColor(getColor(item.on != item.original ? R.color.orange : R.color.text_primary));
            box.setTextSize(13);
            box.setChecked(item.on);
            box.setOnCheckedChangeListener((view, checked) -> {
                item.on = checked;
                box.setTextColor(getColor(item.on != item.original ? R.color.orange : R.color.text_primary));
                int count = 0;
                for (Security s : securities) if (s.on) count++;
                counter.setText("selected: " + count + " / " + securities.size());
                refreshAccept();
            });
            securityList.addView(box, new LinearLayout.LayoutParams(-1, dp(44)));
        }
    }

    private void heading(String value) {
        TextView view = text(value, 14, R.color.orange);
        view.setTypeface(null, Typeface.BOLD);
        view.setPadding(0, dp(14), 0, dp(2));
        form.addView(view);
    }

    private View readOnlyRow(String label, String value) {
        LinearLayout row = new LinearLayout(this);
        row.addView(text(label, 13, R.color.text_primary), new LinearLayout.LayoutParams(0, dp(46), 1));
        TextView shown = text(value, 13, R.color.text_secondary);
        shown.setGravity(Gravity.CENTER);
        row.addView(shown, new LinearLayout.LayoutParams(0, dp(42), 1));
        return row;
    }

    private View row(Field field) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        if ("bool".equals(field.kind)) {
            CheckBox box = new CheckBox(this);
            box.setChecked(Boolean.TRUE.equals(field.value));
            box.setText(field.label);
            box.setTextColor(getColor(R.color.text_primary));
            box.setTextSize(13);
            box.setOnCheckedChangeListener((view, checked) -> { field.value = checked; refreshAccept(); });
            row.addView(box, new LinearLayout.LayoutParams(0, dp(46), 1));
            return row;
        }
        row.addView(text(field.label, 13, R.color.text_primary), new LinearLayout.LayoutParams(0, dp(46), 1));
        TextView value = text(field.value == null ? "—" : String.valueOf(field.value), 13,
            field.changed() ? R.color.orange : R.color.text_primary);
        value.setGravity(Gravity.CENTER);
        value.setBackgroundResource(R.drawable.input_background);
        value.setOnClickListener(view -> edit(field));
        row.addView(value, new LinearLayout.LayoutParams(0, dp(42), 1));
        return row;
    }

    private void edit(Field field) {
        if (busy) return;
        if ("enum".equals(field.kind)) {
            ArrayList<String> choices = new ArrayList<>();
            for (String option : field.options) choices.add(option);
            if (field.value != null && !choices.contains(String.valueOf(field.value)))
                choices.add(0, String.valueOf(field.value));
            new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(field.label)
                .setItems(choices.toArray(new String[0]), (dialog, which) -> {
                    field.value = choices.get(which);
                    renderForm();
                    refreshAccept();
                }).show();
            return;
        }
        boolean number = "decimal".equals(field.kind);
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setTextColor(getColor(R.color.text_primary));
        if (number) input.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        input.setText(field.value == null ? "" : String.valueOf(field.value));
        new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(field.label).setView(input)
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                String raw = input.getText().toString().trim();
                try {
                    if (number) {
                        BigDecimal parsed = new BigDecimal(raw.replace(',', '.'));
                        if (parsed.signum() < 0) throw new NumberFormatException();
                        field.value = parsed;
                    } else {
                        if (raw.isEmpty()) throw new NumberFormatException();
                        field.value = raw;
                    }
                    renderForm();
                    refreshAccept();
                } catch (Exception e) { statusView.setText("Недопустимое значение: " + raw); }
            }).show();
    }

    private boolean securitiesChanged() {
        for (Security item : securities) if (item.on != item.original) return true;
        return false;
    }

    private int changedCount() {
        if (!loaded) return 0;
        int count = 0;
        for (Field field : fields.values()) if (field.changed()) count++;
        if (securitiesChanged()) count++;
        return count;
    }

    private void refreshAccept() {
        if (accept == null) return;
        boolean enabled = loaded && !busy;
        accept.setEnabled(enabled);
        accept.setAlpha(enabled ? 1f : .5f);
        if (loaded && !busy) statusView.setText(changedCount() == 0 ? "Значения VPS"
            : "Изменено, не отправлено: " + changedCount());
    }

    private void submit() {
        if (!loaded || busy) return;
        if (changedCount() == 0) { finish(); return; }
        if (!RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
        busy = true;
        refreshAccept();
        statusView.setText("Отправка на VPS…");
        worker.execute(() -> {
            String error = null;
            try {
                JSONObject args = new JSONObject().put("bot_id", botId).put("tab_name", tabName);
                JSONArray series = new JSONArray();
                boolean seriesChanged = false;
                for (Field field : fields.values()) {
                    if (field.key.startsWith("series:")) {
                        series.put(new JSONObject().put("sys_name", field.key.substring(7))
                            .put("value", field.value));
                        if (field.changed()) seriesChanged = true;
                    } else if (field.changed() && !(simple && field.key.equals("time_frame")))
                        args.put(field.key, field.value);
                }
                if (simple && seriesChanged) args.put("candle_series_parameters", series);
                if (!simple && securitiesChanged()) {
                    JSONArray array = new JSONArray();
                    for (Security item : securities) array.put(new JSONObject().put("name", item.name)
                        .put("class_name", item.className).put("is_on", item.on));
                    args.put("securities", array);
                }
                Object response = bridge.callBatch(terminal, McpBridge.call(simple ? "bot_set_config_tab_simple" : "bot_set_config_tab_screener", args))
                    .get(simple ? "bot_set_config_tab_simple" : "bot_set_config_tab_screener");
                if (response instanceof Exception) throw (Exception) response;
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) {
                    refreshAccept();
                    statusView.setText("Не удалось сохранить: " + finalError);
                    return;
                }
                finish();
            });
        });
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
}
