package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
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

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Mobile counterpart of NonTradePeriodsUi for a selected VPS connector instance. */
public final class NonTradePeriodsActivity extends Activity {
    private static final String[] DAYS = {"Понедельник", "Вторник", "Среда", "Четверг",
        "Пятница", "Суббота", "Воскресенье"};
    private static final String[] TRADE_DAYS = {"Торгуем по понедельникам", "Торгуем по вторникам",
        "Торгуем по средам", "Торгуем по четвергам", "Торгуем по пятницам",
        "Торгуем по субботам", "Торгуем по воскресеньям"};
    private static final int EXPORT = 401;
    private static final int IMPORT = 402;

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private McpBridge bridge;
    private String terminal;
    private String type;
    private int number;
    private int tab;
    private int day;
    private boolean busy;
    private NonTradePeriodsData data;
    private LinearLayout tabs;
    private LinearLayout content;
    private TextView message;
    private TextView save;
    private TextView load;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        type = getIntent().getStringExtra("server_type");
        number = getIntent().getIntExtra("server_number", 0);
        if (terminal == null || type == null) { finish(); return; }
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        buildScreen();
        fetch();
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

        TextView back = text("‹ Настройка подключения " + type, 14, R.color.orange);
        back.setPadding(dp(14), 0, 0, 0);
        back.setOnClickListener(view -> finish());
        root.addView(back, new LinearLayout.LayoutParams(-1, dp(44)));
        TextView title = text("Настройки неторговых периодов", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        title.setPadding(dp(14), 0, dp(14), 0);
        root.addView(title, new LinearLayout.LayoutParams(-1, dp(42)));
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        root.addView(strip, new LinearLayout.LayoutParams(-1, dp(2)));

        message = text("Загрузка с VPS…", 12, R.color.text_secondary);
        message.setPadding(dp(14), dp(7), dp(14), dp(7));
        root.addView(message);

        HorizontalScrollView tabScroll = new HorizontalScrollView(this);
        tabScroll.setHorizontalScrollBarEnabled(false);
        tabs = row();
        tabScroll.addView(tabs);
        root.addView(tabScroll);

        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        content = column();
        content.setPadding(dp(14), dp(8), dp(14), dp(12));
        scroll.addView(content);

        LinearLayout footer = row();
        footer.setPadding(dp(14), dp(6), dp(14), dp(8));
        save = button("Сохранить");
        load = button("Загрузить");
        footer.addView(save, new LinearLayout.LayoutParams(0, dp(44), 1));
        LinearLayout.LayoutParams second = new LinearLayout.LayoutParams(0, dp(44), 1);
        second.leftMargin = dp(8);
        footer.addView(load, second);
        root.addView(footer);
        save.setOnClickListener(view -> filePicker(Intent.ACTION_CREATE_DOCUMENT, EXPORT));
        load.setOnClickListener(view -> filePicker(Intent.ACTION_OPEN_DOCUMENT, IMPORT));
        render();
    }

    private void fetch() {
        if (busy || !RemoteSsh.isConnected()) { show("Нет связи · SSH"); return; }
        busy = true;
        render();
        worker.execute(() -> {
            NonTradePeriodsData received = null;
            String error = null;
            try {
                Object value = bridge.callBatch(terminal, McpBridge.call(
                    "server_instance_get_non_trade_periods", args())).get("server_instance_get_non_trade_periods");
                if (value instanceof Exception) throw (Exception) value;
                received = new NonTradePeriodsData(((JSONObject) value).getJSONArray("values"));
            } catch (Exception e) { error = e.getMessage(); }
            NonTradePeriodsData result = received;
            String failure = error;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                if (failure == null) { data = result; show(""); }
                else show("Не удалось загрузить расписание: " + failure);
                render();
            });
        });
    }

    private JSONObject args() throws Exception {
        return new JSONObject().put("type", type).put("number", number);
    }

    private void saveData(NonTradePeriodsData changed) {
        if (busy || !RemoteSsh.isConnected()) { show("Нет связи · SSH"); return; }
        try { changed.validate(); }
        catch (Exception e) { show(e.getMessage()); return; }
        busy = true;
        render();
        worker.execute(() -> {
            String error = null;
            try {
                JSONObject parameters = args().put("values", changed.toJson());
                Object value = bridge.callBatch(terminal, McpBridge.call(
                    "server_instance_set_non_trade_periods", parameters))
                    .get("server_instance_set_non_trade_periods");
                if (value instanceof Exception) throw (Exception) value;
                if (!(value instanceof JSONObject) || !((JSONObject) value).optBoolean("saved"))
                    throw new IllegalStateException("VPS не подтвердил сохранение");
            } catch (Exception e) { error = e.getMessage(); }
            String failure = error;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                if (failure == null) fetch();
                else { show("Не удалось сохранить: " + failure); render(); }
            });
        });
    }

    private void changeDay(int selected, boolean value) {
        NonTradePeriodsData changed = data.copy();
        changed.setTradeDay(selected, value);
        saveData(changed);
    }

    private void changePeriod(int group, int period, boolean value) {
        NonTradePeriodsData changed = data.copy();
        changed.setEnabled(group, period, value);
        saveData(changed);
    }

    private void editTime(int group, int period, boolean end) {
        if (busy || data == null || !RemoteSsh.isConnected()) return;
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setText(data.time(group, period, end));
        input.setTextColor(getColor(R.color.text_primary));
        input.selectAll();
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle("Неторговый период " + (period + 1) + (end ? " · окончание" : " · начало"))
            .setMessage("Время коннектора на VPS: часы:минуты:секунды:миллисекунды")
            .setView(input).setNegativeButton("Отмена", null)
            .setPositiveButton("Принять", (dialog, which) -> {
                try {
                    NonTradePeriodsData changed = data.copy();
                    changed.setTime(group, period, end, input.getText().toString().trim());
                    saveData(changed);
                } catch (Exception e) { show(e.getMessage()); }
            }).show();
    }

    private void render() {
        if (tabs == null) return;
        tabs.removeAllViews();
        content.removeAllViews();
        String[] names = {"Общие неторговые периоды", "Настройки для каждого дня недели", "Преднастройки"};
        for (int i = 0; i < names.length; i++) {
            final int selected = i;
            TextView item = button(names[i]);
            item.setTextColor(getColor(tab == i ? R.color.orange : R.color.text_primary));
            item.setPadding(dp(12), 0, dp(12), 0);
            tabs.addView(item, new LinearLayout.LayoutParams(-2, dp(46)));
            item.setOnClickListener(view -> { tab = selected; render(); });
        }
        boolean enabled = data != null && !busy && RemoteSsh.isConnected();
        save.setEnabled(data != null);
        load.setEnabled(enabled);
        if (data == null) return;
        if (tab == 0) periods(1);
        else if (tab == 1) {
            HorizontalScrollView dayScroll = new HorizontalScrollView(this);
            dayScroll.setHorizontalScrollBarEnabled(false);
            LinearLayout dayTabs = row();
            dayScroll.addView(dayTabs);
            content.addView(dayScroll);
            for (int i = 0; i < DAYS.length; i++) {
                int selected = i;
                TextView button = button(DAYS[i]);
                button.setTextColor(getColor(day == i ? R.color.orange : R.color.text_primary));
                button.setPadding(dp(9), 0, dp(9), 0);
                dayTabs.addView(button, new LinearLayout.LayoutParams(-2, dp(42)));
                button.setOnClickListener(view -> { day = selected; render(); });
            }
            CheckBox trade = new CheckBox(this);
            trade.setText(TRADE_DAYS[day]);
            trade.setTextColor(getColor(R.color.text_primary));
            trade.setButtonTintList(getColorStateList(R.color.orange));
            trade.setChecked(data.tradeDay(day));
            trade.setEnabled(enabled);
            trade.setOnCheckedChangeListener((button, checked) -> changeDay(day, checked));
            content.addView(trade, new LinearLayout.LayoutParams(-1, dp(52)));
            periods(day + 2);
        } else presets(enabled);
    }

    private void periods(int group) {
        boolean enabled = !busy && RemoteSsh.isConnected();
        for (int i = 0; i < 5; i++) {
            int period = i;
            LinearLayout line = row();
            line.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams lineParams = new LinearLayout.LayoutParams(-1, dp(54));
            lineParams.bottomMargin = dp(5);
            content.addView(line, lineParams);
            CheckBox check = new CheckBox(this);
            check.setText("Неторговый период " + (i + 1));
            check.setTextSize(12);
            check.setTextColor(getColor(R.color.text_primary));
            check.setButtonTintList(getColorStateList(R.color.orange));
            check.setChecked(data.enabled(group, i));
            check.setEnabled(enabled);
            check.setOnCheckedChangeListener((button, checked) -> changePeriod(group, period, checked));
            line.addView(check, new LinearLayout.LayoutParams(0, -1, 5));
            for (boolean end : new boolean[]{false, true}) {
                TextView time = text(data.time(group, i, end), 12, R.color.text_primary);
                time.setGravity(Gravity.CENTER);
                time.setBackgroundResource(R.drawable.position_cell);
                time.setEnabled(enabled);
                line.addView(time, new LinearLayout.LayoutParams(0, -1, 3));
                time.setOnClickListener(view -> editTime(group, period, end));
            }
        }
    }

    private void presets(boolean enabled) {
        String[] captions = {
            "Установить настройки рынка MOEX СПОТ (акции, облигации) площадки",
            "Установить настройки рынка MOEX СРОЧНОЙ (фьючерсы, опционы) площадки"};
        for (int i = 0; i < captions.length; i++) {
            boolean futures = i == 1;
            TextView button = button(captions[i]);
            button.setPadding(dp(10), dp(8), dp(10), dp(8));
            button.setEnabled(enabled);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(68));
            params.bottomMargin = dp(8);
            content.addView(button, params);
            button.setOnClickListener(view -> new AlertDialog.Builder(this, R.style.OsEngineDialog)
                .setMessage("Применить настройки рынка MOEX " + (futures ? "СРОЧНОЙ" : "СПОТ") + "?")
                .setNegativeButton("Отмена", null)
                .setPositiveButton("Принять", (dialog, which) -> {
                    NonTradePeriodsData changed = data.copy();
                    changed.applyMoexPreset(futures);
                    saveData(changed);
                }).show());
        }
    }

    private void filePicker(String action, int requestCode) {
        if (data == null || busy || requestCode == IMPORT && !RemoteSsh.isConnected()) return;
        Intent intent = new Intent(action);
        intent.setType("text/plain");
        intent.addCategory(Intent.CATEGORY_OPENABLE);
        if (requestCode == EXPORT) intent.putExtra(Intent.EXTRA_TITLE, "NonTradePeriods.txt");
        startActivityForResult(intent, requestCode);
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent result) {
        super.onActivityResult(requestCode, resultCode, result);
        if (resultCode != RESULT_OK || result == null || result.getData() == null) return;
        Uri uri = result.getData();
        if (requestCode == EXPORT) {
            try (OutputStream output = getContentResolver().openOutputStream(uri)) {
                if (output == null) throw new IllegalStateException("Файл недоступен");
                output.write(data.toText().getBytes(StandardCharsets.UTF_8));
                show("Настройки сохранены в файл");
            } catch (Exception e) { show("Не удалось сохранить файл: " + e.getMessage()); }
        } else if (requestCode == IMPORT) {
            try (InputStream input = getContentResolver().openInputStream(uri)) {
                if (input == null) throw new IllegalStateException("Файл недоступен");
                ByteArrayOutputStream output = new ByteArrayOutputStream();
                byte[] buffer = new byte[4096];
                int length;
                while ((length = input.read(buffer)) > 0) {
                    output.write(buffer, 0, length);
                    if (output.size() > 64 * 1024) throw new IllegalArgumentException("Файл слишком велик");
                }
                NonTradePeriodsData changed = NonTradePeriodsData.fromText(
                    new String(output.toByteArray(), StandardCharsets.UTF_8));
                saveData(changed);
            } catch (Exception e) { show("Не удалось загрузить файл: " + e.getMessage()); }
        }
    }

    private void show(String value) { message.setText(value); }
    private LinearLayout column() { LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL); return layout; }
    private LinearLayout row() { LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.HORIZONTAL); return layout; }
    private TextView text(String value, int size, int color) { TextView view = new TextView(this);
        view.setText(value); view.setTextSize(size); view.setTextColor(getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView button(String value) { TextView view = text(value, 13, R.color.text_primary);
        view.setGravity(Gravity.CENTER); view.setBackgroundResource(R.drawable.input_background);
        return view; }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
