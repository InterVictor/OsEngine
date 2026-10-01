package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

import java.math.BigDecimal;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** «Риск Менеджер» and «Настройки сопровождения позиций»: one server-backed form per mode. */
public final class BotSettingsActivity extends Activity {
    static final String MODE_RISK = "risk";
    static final String MODE_SUPPORT = "support";

    private static final class Field {
        final String key, label, kind;
        final String[] options;
        Object original;
        Object value;
        Field(String key, String label, String kind, String... options) {
            this.key = key; this.label = label; this.kind = kind; this.options = options;
        }
    }

    private static final Map<String, String> PAIRED = new LinkedHashMap<>();
    static {
        PAIRED.put("second_to_close_is_on", "second_to_close");
        PAIRED.put("setback_to_close_is_on", "setback_to_close_position");
        PAIRED.put("second_to_open_is_on", "second_to_open");
        PAIRED.put("setback_to_open_is_on", "setback_to_open_position");
    }

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final ArrayList<Object> layout = new ArrayList<>();   // String heading or Field
    private final Map<String, Field> fields = new LinkedHashMap<>();
    private McpBridge bridge;
    private String mode, terminal, botId, botName, tabName;
    private boolean loaded, busy, destroyed;
    private LinearLayout form;
    private TextView statusView;
    private TextView accept;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        mode = getIntent().getStringExtra("mode");
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        botName = getIntent().getStringExtra("bot_name");
        tabName = getIntent().getStringExtra("tab_name");
        if (mode == null || terminal == null || botId == null) { finish(); return; }
        if (botName == null || botName.isEmpty()) botName = botId;
        if (MODE_SUPPORT.equals(mode) && (tabName == null || tabName.isEmpty())) { finish(); return; }
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        defineFields();
        buildScreen();
        load();
    }

    @Override protected void onDestroy() {
        destroyed = true;
        worker.shutdownNow();
        super.onDestroy();
    }

    private void add(Field field) { fields.put(field.key, field); layout.add(field); }

    private void defineFields() {
        if (MODE_RISK.equals(mode)) {
            add(new Field("is_active", "Включить", "bool"));
            add(new Field("max_drawdown_to_day_percent", "Максимальный убыток за день в %", "decimal"));
            add(new Field("reaction_type", "Реакция на максимальный убыток", "enum",
                "CloseAndOff", "ShowDialog", "None"));
            return;
        }
        layout.add("Стоп");
        add(new Field("stop_is_on", "Включить", "bool"));
        add(new Field("stop_distance", "От входа до Стопа", "decimal"));
        add(new Field("stop_slippage", "Проскальз.", "decimal"));
        layout.add("Профит");
        add(new Field("profit_is_on", "Включить", "bool"));
        add(new Field("profit_distance", "От входа до Профита", "decimal"));
        add(new Field("profit_slippage", "Проскальз.", "decimal"));
        layout.add("Закрытие позиции");
        add(new Field("second_to_close_is_on", "Секунд на закрытие", "bool"));
        add(new Field("second_to_close", "Секунд на закрытие, значение", "int"));
        add(new Field("setback_to_close_is_on", "Макс откат цены", "bool"));
        add(new Field("setback_to_close_position", "Макс откат цены, значение", "decimal"));
        layout.add("Открытие позиции");
        add(new Field("second_to_open_is_on", "Секунд на открытие", "bool"));
        add(new Field("second_to_open", "Секунд на открытие, значение", "int"));
        add(new Field("setback_to_open_is_on", "Макс откат цены", "bool"));
        add(new Field("setback_to_open_position", "Макс откат цены, значение", "decimal"));
        layout.add("Ордер на закрытие отозван");
        add(new Field("type_double_exit_order", "Реакция", "enum", "Limit", "Market"));
        add(new Field("double_exit_slippage", "Проскальз.", "decimal"));
        layout.add("Общие параметры");
        add(new Field("values_type", "Тип переменных", "enum", "MinPriceStep", "Absolute", "Percent"));
        add(new Field("order_type_time", "Время жизни ордера", "enum", "Specified", "GTC", "Day"));
        add(new Field("limits_maker_only", "Лимит ордера только мейкер", "bool"));
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
        TextView back = text("‹ " + botName, 15, R.color.orange);
        back.setOnClickListener(view -> onBackPressed());
        header.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        TextView title = text(MODE_RISK.equals(mode) ? "Риск Менеджер"
            : "Настройки сопровождения позиций", 19, R.color.text_primary);
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
                String tool = MODE_RISK.equals(mode) ? "bot_risk_manager_get" : "bot_get_position_support";
                JSONObject args = new JSONObject().put("bot_id", botId);
                if (MODE_SUPPORT.equals(mode)) args.put("tab_name", tabName);
                Object response = bridge.callBatch(terminal, McpBridge.call(tool, args)).get(tool);
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
                for (Field field : fields.values()) {
                    Object raw = data.opt(field.key);
                    field.original = raw == null || raw == JSONObject.NULL ? null : raw;
                    field.value = field.original;
                }
                loaded = true;
                renderForm();
                refreshAccept();
            });
        });
    }

    private void renderForm() {
        form.removeAllViews();
        for (Object item : layout) {
            if (item instanceof String) {
                TextView heading = text((String) item, 14, R.color.orange);
                heading.setTypeface(null, Typeface.BOLD);
                heading.setPadding(0, dp(14), 0, dp(2));
                form.addView(heading);
                continue;
            }
            Field field = (Field) item;
            LinearLayout row = new LinearLayout(this);
            row.setGravity(Gravity.CENTER_VERTICAL);
            // A "checkbox + value" pair (seconds, setback) shares one row, as in the desktop window.
            if (PAIRED.containsValue(field.key)) continue;
            if ("bool".equals(field.kind)) {
                CheckBox box = new CheckBox(this);
                box.setChecked(Boolean.TRUE.equals(field.value));
                box.setText(field.label);
                box.setTextColor(getColor(R.color.text_primary));
                box.setTextSize(13);
                box.setOnCheckedChangeListener((view, checked) -> { field.value = checked; refreshAccept(); });
                row.addView(box, new LinearLayout.LayoutParams(0, dp(46), 1));
                Field paired = PAIRED.containsKey(field.key) ? fields.get(PAIRED.get(field.key)) : null;
                if (paired != null) row.addView(valueView(paired), new LinearLayout.LayoutParams(0, dp(42), 1));
            } else {
                row.addView(text(field.label, 13, R.color.text_primary),
                    new LinearLayout.LayoutParams(0, dp(46), 1));
                row.addView(valueView(field), new LinearLayout.LayoutParams(0, dp(42), 1));
            }
            form.addView(row);
        }
    }

    private TextView valueView(Field field) {
        TextView value = text(field.value == null ? "—" : String.valueOf(field.value), 13,
            field.value != null && isChanged(field) ? R.color.orange : R.color.text_primary);
        value.setGravity(Gravity.CENTER);
        value.setBackgroundResource(R.drawable.input_background);
        value.setOnClickListener(view -> edit(field));
        return value;
    }

    private void edit(Field field) {
        if (busy) return;
        if ("enum".equals(field.kind)) {
            new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(field.label)
                .setItems(field.options, (dialog, which) -> { field.value = field.options[which]; changed(); })
                .show();
            return;
        }
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setTextColor(getColor(R.color.text_primary));
        input.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL);
        input.setText(field.value == null ? "" : String.valueOf(field.value));
        new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(field.label).setView(input)
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                String raw = input.getText().toString().trim().replace(',', '.');
                try {
                    BigDecimal number = new BigDecimal(raw);
                    if (number.signum() < 0) throw new NumberFormatException();
                    field.value = "int".equals(field.kind) ? (Object) number.intValueExact() : number;
                    changed();
                } catch (Exception e) { statusView.setText("Недопустимое значение: " + raw); }
            }).show();
    }

    private void changed() {
        renderForm();
        refreshAccept();
    }

    private boolean isChanged(Field field) {
        if (field.value == null || field.original == null) return field.value != field.original;
        if (field.value instanceof Number && field.original instanceof Number)
            return new BigDecimal(field.value.toString()).compareTo(new BigDecimal(field.original.toString())) != 0;
        return !field.value.equals(field.original);
    }

    private int changedCount() {
        int count = 0;
        for (Field field : fields.values()) if (loaded && isChanged(field)) count++;
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
                String tool = MODE_RISK.equals(mode) ? "bot_risk_manager_set" : "bot_set_position_support";
                JSONObject args = new JSONObject().put("bot_id", botId);
                if (MODE_SUPPORT.equals(mode)) args.put("tab_name", tabName);
                for (Field field : fields.values()) if (isChanged(field)) args.put(field.key, field.value);
                Object response = bridge.callBatch(terminal, McpBridge.call(tool, args)).get(tool);
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
