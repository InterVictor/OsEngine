package net.osa.osenginemobile;

import android.app.AlertDialog;
import android.app.Activity;
import android.app.TimePickerDialog;
import android.content.Intent;
import android.net.Uri;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
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

/** Robot parameters window, mirroring RobotsVpsParametersUi: tabs come only from the VPS answer. */
public final class RobotParametersActivity extends Activity {
    private static final int REQUEST_SAVE = 41;
    private static final int REQUEST_LOAD = 42;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private McpBridge bridge;
    private String terminal;
    private String botId;
    private String botName;
    private boolean busy;
    private boolean destroyed;
    private int tab;
    private String windowTitle = "";
    private String firstTabLabel = "";
    private JSONArray parameters = new JSONArray();
    private JSONArray customTabs = new JSONArray();
    private final ArrayList<String> tabNames = new ArrayList<>();
    private final Map<String, Object> changes = new LinkedHashMap<>();
    private LinearLayout tabs;
    private LinearLayout content;
    private TextView title;
    private TextView statusView;
    private TextView refreshButton;
    private TextView acceptButton;
    private ScrollView scroll;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        botName = getIntent().getStringExtra("bot_name");
        if (terminal == null || botId == null) { finish(); return; }
        if (botName == null || botName.isEmpty()) botName = botId;
        try { bridge = new McpBridge(this); }
        catch (Exception e) { finish(); return; }
        buildScreen();
        load(false);
    }

    @Override protected void onDestroy() {
        destroyed = true;
        worker.shutdownNow();
        super.onDestroy();
    }

    @Override public void onBackPressed() {
        if (changes.isEmpty()) { super.onBackPressed(); return; }
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setMessage("Есть неотправленные изменения (" + changes.size() + "). Закрыть без отправки?")
            .setNegativeButton("Остаться", null)
            .setPositiveButton("Закрыть", (dialog, which) -> finish()).show();
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
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setPadding(dp(14), dp(12), dp(14), 0);
        root.addView(Ime.hide(header));
        TextView back = text("‹ Роботы.VPS · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> onBackPressed());
        header.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        title = text("Параметры " + botName, 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(2));
        stripParams.bottomMargin = dp(8);
        header.addView(strip, stripParams);
        statusView = text("Загрузка параметров…", 12, R.color.text_secondary);
        header.addView(statusView);
        HorizontalScrollView tabScroll = new HorizontalScrollView(this);
        tabScroll.setHorizontalScrollBarEnabled(false);
        tabs = new LinearLayout(this);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        tabScroll.addView(tabs);
        header.addView(tabScroll);
        scroll = new BarScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(14), dp(6), dp(14), dp(12));
        scroll.addView(content);
        LinearLayout files = new LinearLayout(this);
        files.setPadding(dp(14), dp(6), dp(14), 0);
        root.addView(Ime.hide(files));
        TextView save = action("Сохранить");
        TextView load = action("Загрузить");
        files.addView(save, new LinearLayout.LayoutParams(0, dp(42), 1));
        LinearLayout.LayoutParams loadParams = new LinearLayout.LayoutParams(0, dp(42), 1);
        loadParams.leftMargin = dp(6);
        files.addView(load, loadParams);
        save.setOnClickListener(view -> {
            Intent intent = new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType("application/json").putExtra(Intent.EXTRA_TITLE, botName + "-parameters.json");
            startActivityForResult(intent, REQUEST_SAVE);
        });
        load.setOnClickListener(view -> {
            Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                .setType("*/*");
            startActivityForResult(intent, REQUEST_LOAD);
        });
        LinearLayout buttons = new LinearLayout(this);
        buttons.setOrientation(LinearLayout.HORIZONTAL);
        buttons.setPadding(dp(14), dp(6), dp(14), dp(8));
        root.addView(Ime.hide(buttons));
        refreshButton = action("Обновить");
        acceptButton = action("Принять");
        buttons.addView(refreshButton, new LinearLayout.LayoutParams(0, dp(42), 1));
        LinearLayout.LayoutParams second = new LinearLayout.LayoutParams(0, dp(42), 1);
        second.leftMargin = dp(6);
        buttons.addView(acceptButton, second);
        refreshButton.setOnClickListener(view -> submit(false));
        acceptButton.setOnClickListener(view -> submit(true));
    }

    private void load(boolean keepChanges) {
        if (busy || !RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
        busy = true;
        worker.execute(() -> {
            JSONObject result = null;
            String error = null;
            try {
                Object response = bridge.callBatch(terminal, McpBridge.call("bot_get_params",
                    new JSONObject().put("bot_id", botId))).get("bot_get_params");
                if (response instanceof Exception) throw (Exception) response;
                if (!(response instanceof JSONObject)) throw new IllegalStateException("Пустой ответ VPS");
                result = (JSONObject) response;
            } catch (Exception e) { error = e.getMessage(); }
            JSONObject data = result;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) { statusView.setText("Не удалось загрузить: " + finalError); return; }
                parameters = data.optJSONArray("parameters") == null ? new JSONArray()
                    : data.optJSONArray("parameters");
                customTabs = data.optJSONArray("custom_tabs") == null ? new JSONArray()
                    : data.optJSONArray("custom_tabs");
                firstTabLabel = data.optString("first_tab_label");
                windowTitle = data.optString("window_title");
                if (!windowTitle.isEmpty()) title.setText(windowTitle + " · " + botName);
                if (!keepChanges) changes.clear();
                buildTabNames();
                if (tab >= tabNames.size()) tab = 0;
                renderTabs();
                renderContent();
                updateStatus();
            });
        });
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (result != RESULT_OK || data == null || data.getData() == null) return;
        try {
            if (request == REQUEST_SAVE) saveJson(data.getData());
            else if (request == REQUEST_LOAD) loadJson(data.getData());
        } catch (Exception e) { statusView.setText("Файл: " + e.getMessage()); }
    }

    /** Current values (pending edits included) of every editable parameter as {"parameters": {name: value}}. */
    private void saveJson(Uri uri) throws Exception {
        JSONObject values = new JSONObject();
        for (int i = 0; i < parameters.length(); i++) {
            JSONObject parameter = parameters.optJSONObject(i);
            if (parameter == null) continue;
            String kind = parameter.optString("type");
            if ("Button".equalsIgnoreCase(kind) || "Label".equalsIgnoreCase(kind)) continue;
            String name = parameter.getString("name");
            values.put(name, changes.containsKey(name) ? changes.get(name) : parameter.opt("value"));
        }
        JSONObject file = new JSONObject().put("robot", botName).put("parameters", values);
        try (java.io.OutputStream out = getContentResolver().openOutputStream(uri, "wt")) {
            out.write(file.toString(2).getBytes("UTF-8"));
        }
        statusView.setText("Параметры сохранены в файл: " + values.length());
    }

    /** Applies a saved file as pending edits only; nothing is sent until «Обновить» / «Принять». */
    private void loadJson(Uri uri) throws Exception {
        StringBuilder text = new StringBuilder();
        try (java.io.BufferedReader reader = new java.io.BufferedReader(
            new java.io.InputStreamReader(getContentResolver().openInputStream(uri), "UTF-8"))) {
            String line;
            while ((line = reader.readLine()) != null) text.append(line).append('\n');
        }
        JSONObject root = new JSONObject(text.toString());
        JSONObject values = root.has("parameters") ? root.getJSONObject("parameters") : root;
        int applied = 0, skipped = 0;
        for (int i = 0; i < parameters.length(); i++) {
            JSONObject parameter = parameters.optJSONObject(i);
            if (parameter == null) continue;
            String name = parameter.optString("name");
            String kind = parameter.optString("type");
            if (!values.has(name) || "Button".equalsIgnoreCase(kind) || "Label".equalsIgnoreCase(kind)) continue;
            Object value = values.get(name);
            try {
                if ("Int".equalsIgnoreCase(kind)) value = Integer.parseInt(String.valueOf(value).trim());
                else if ("Decimal".equalsIgnoreCase(kind))
                    value = new BigDecimal(String.valueOf(value).trim().replace(',', '.'));
                else if ("Bool".equalsIgnoreCase(kind) || "CheckBox".equalsIgnoreCase(kind))
                    value = value instanceof Boolean ? value : Boolean.parseBoolean(String.valueOf(value));
                else value = String.valueOf(value);
                JSONArray options = parameter.optJSONArray("values");
                if (options != null && options.length() > 0) {
                    boolean known = false;
                    for (int j = 0; j < options.length(); j++) if (options.optString(j).equals(value)) known = true;
                    if (!known) { skipped++; continue; }
                }
                setChange(parameter, value);
                applied++;
            } catch (Exception e) { skipped++; }
        }
        statusView.setText("Из файла применено: " + applied + ", пропущено: " + skipped
            + ". Нажмите «Обновить» или «Принять», чтобы отправить на VPS");
    }

    private void buildTabNames() {
        tabNames.clear();
        boolean hasUngrouped = false;
        for (int i = 0; i < parameters.length(); i++) {
            JSONObject parameter = parameters.optJSONObject(i);
            if (parameter == null) continue;
            String group = parameter.optString("tab_name");
            if (group.isEmpty()) hasUngrouped = true;
            else if (!tabNames.contains(group)) tabNames.add(group);
        }
        if (hasUngrouped) tabNames.add(0, "");
        for (int i = 0; i < customTabs.length(); i++) {
            Object item = customTabs.opt(i);
            String name = item instanceof JSONObject ? ((JSONObject) item).optString("name",
                ((JSONObject) item).optString("tab_name")) : String.valueOf(item);
            tabNames.add("\u0000" + name);
        }
    }

    private String tabLabel(String name) {
        if (name.startsWith("\u0000")) return name.substring(1);
        return name.isEmpty() ? (firstTabLabel.isEmpty() ? "Prime" : firstTabLabel) : name;
    }

    private void renderTabs() {
        tabs.removeAllViews();
        for (int i = 0; i < tabNames.size(); i++) {
            int index = i;
            TextView item = action(tabLabel(tabNames.get(i)));
            item.setPadding(dp(14), 0, dp(14), 0);
            item.setTextColor(getColor(tab == i ? R.color.orange : R.color.text_primary));
            tabs.addView(item, new LinearLayout.LayoutParams(-2, dp(44)));
            item.setOnClickListener(view -> { tab = index; renderTabs(); renderContent(); });
        }
    }

    private void renderContent() {
        content.removeAllViews();
        if (tabNames.isEmpty()) {
            content.addView(text("У робота нет параметров", 14, R.color.text_secondary));
            return;
        }
        String group = tabNames.get(tab);
        if (group.startsWith("\u0000")) {
            content.addView(text("Удалённое содержимое вкладки «" + group.substring(1)
                + "» недоступно в мобильном приложении", 14, R.color.text_secondary));
            return;
        }
        LinearLayout heading = new LinearLayout(this);
        heading.addView(cell("Название параметра", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        heading.addView(cell("Значение", true), new LinearLayout.LayoutParams(0, dp(42), 1));
        content.addView(heading);
        for (int i = 0; i < parameters.length(); i++) {
            JSONObject parameter = parameters.optJSONObject(i);
            if (parameter == null || !group.equals(parameter.optString("tab_name"))) continue;
            String name = parameter.optString("name");
            String kind = parameter.optString("type");
            boolean button = "Button".equalsIgnoreCase(kind);
            LinearLayout row = new LinearLayout(this);
            row.addView(cell(button ? "" : name, false), new LinearLayout.LayoutParams(0, -2, 1));
            boolean changed = changes.containsKey(name);
            TextView value = cell(button ? name : shown(parameter), false);
            value.setTextColor(getColor(button || changed ? R.color.orange : R.color.text_primary));
            if (changed) value.setTypeface(null, Typeface.BOLD);
            row.addView(value, new LinearLayout.LayoutParams(0, -2, 1));
            value.setOnClickListener(view -> edit(parameter));
            content.addView(row);
        }
    }

    private String shown(JSONObject parameter) {
        String name = parameter.optString("name");
        Object value = changes.containsKey(name) ? changes.get(name) : parameter.opt("value");
        if (value instanceof Boolean) return ((Boolean) value) ? "True" : "False";
        return value == null ? "" : String.valueOf(value);
    }

    private void edit(JSONObject parameter) {
        if (busy) return;
        String name = parameter.optString("name");
        String kind = parameter.optString("type");
        if ("Label".equalsIgnoreCase(kind)) return;
        if ("Button".equalsIgnoreCase(kind)) { clickButton(name); return; }
        if ("TimeOfDay".equalsIgnoreCase(kind)) { editTime(parameter); return; }
        JSONArray options = parameter.optJSONArray("values");
        boolean bool = "Bool".equalsIgnoreCase(kind) || "CheckBox".equalsIgnoreCase(kind);
        if (bool || (options != null && options.length() > 0)) {
            String[] choices;
            if (bool) choices = new String[]{"True", "False"};
            else {
                choices = new String[options.length()];
                for (int i = 0; i < choices.length; i++) choices[i] = options.optString(i);
            }
            new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle(name).setItems(choices,
                (dialog, which) -> setChange(parameter, bool ? (Object) "True".equals(choices[which])
                    : choices[which])).show();
            return;
        }
        boolean number = "Int".equalsIgnoreCase(kind) || "Decimal".equalsIgnoreCase(kind);
        EditText input = new EditText(this);
        input.setSingleLine(true);
        input.setTextColor(getColor(R.color.text_primary));
        if (number) input.setInputType(InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_SIGNED
            | ("Decimal".equalsIgnoreCase(kind) ? InputType.TYPE_NUMBER_FLAG_DECIMAL : 0));
        input.setText(shown(parameter));
        String range = number && parameter.has("start")
            ? "Диапазон оптимизации: " + parameter.optString("start") + " … " + parameter.optString("stop")
            + ", шаг " + parameter.optString("step") : null;
        AlertDialog.Builder dialog = new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setTitle(name).setView(input).setNegativeButton("Отмена", null);
        if (range != null) dialog.setMessage(range);
        dialog.setPositiveButton("Принять", (d, which) -> {
            String raw = input.getText().toString().trim();
            try {
                Object value = "Int".equalsIgnoreCase(kind) ? (Object) Integer.parseInt(raw)
                    : "Decimal".equalsIgnoreCase(kind) ? new BigDecimal(raw.replace(',', '.')) : raw;
                setChange(parameter, value);
            } catch (Exception e) { statusView.setText("Недопустимое значение: " + raw); }
        }).show();
    }

    private void editTime(JSONObject parameter) {
        String[] parts = shown(parameter).split(":");
        int hour = 0, minute = 0;
        try { hour = Integer.parseInt(parts[0]); minute = Integer.parseInt(parts[1]); }
        catch (Exception ignored) { }
        new TimePickerDialog(this, R.style.OsEngineDialog, (picker, h, m) -> {
            String old = shown(parameter);
            String seconds = old.split(":").length > 2 ? old.split(":")[2] : "00";
            setChange(parameter, String.format("%02d:%02d:%s", h, m, seconds));
        }, hour, minute, true).show();
    }

    private void setChange(JSONObject parameter, Object value) {
        String name = parameter.optString("name");
        Object original = parameter.opt("value");
        if (String.valueOf(value).equals(String.valueOf(original))) changes.remove(name);
        else changes.put(name, value);
        renderContent();
        updateStatus();
    }

    private void clickButton(String name) {
        new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setMessage("Нажать «" + name + "» на VPS?")
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Нажать", (dialog, which) -> {
                if (busy || !RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
                busy = true;
                worker.execute(() -> {
                    String error = null;
                    try {
                        Object response = bridge.callBatch(terminal, McpBridge.call("bot_click_param_button",
                            new JSONObject().put("bot_id", botId).put("param_name", name)))
                            .get("bot_click_param_button");
                        if (response instanceof Exception) throw (Exception) response;
                    } catch (Exception e) { error = e.getMessage(); }
                    String finalError = error;
                    runOnUiThread(() -> {
                        busy = false;
                        if (destroyed) return;
                        statusView.setText(finalError == null ? "Кнопка «" + name + "» нажата"
                            : "Не удалось нажать: " + finalError);
                        if (finalError == null) load(true);
                    });
                });
            }).show();
    }

    private void submit(boolean close) {
        if (busy) return;
        if (changes.isEmpty()) {
            if (close) finish(); else load(false);
            return;
        }
        if (!RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
        busy = true;
        updateStatus();
        Map<String, Object> sent = new LinkedHashMap<>(changes);
        worker.execute(() -> {
            String error = null;
            try {
                JSONObject values = new JSONObject();
                for (Map.Entry<String, Object> entry : sent.entrySet()) values.put(entry.getKey(), entry.getValue());
                Object response = bridge.callBatch(terminal, McpBridge.call("bot_set_params",
                    new JSONObject().put("bot_id", botId).put("parameters", values))).get("bot_set_params");
                if (response instanceof Exception) throw (Exception) response;
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) {
                    statusView.setText("Не удалось отправить: " + finalError);
                    updateStatus();
                    return;
                }
                changes.clear();
                if (close) finish(); else load(false);
            });
        });
    }

    private void updateStatus() {
        if (busy) statusView.setText("Отправка на VPS…");
        else statusView.setText(changes.isEmpty() ? "Параметров: " + parameters.length()
            : "Изменено, не отправлено: " + changes.size());
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
}
