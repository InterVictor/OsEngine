package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** «Создание робота»: strategy table of CreateBotDialog / BotCreateUi2 for one terminal. */
public final class AddBotActivity extends Activity {
    private static final String[] LOCATIONS = {"All", "Include", "Script"};

    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final ArrayList<JSONObject> strategies = new ArrayList<>();
    private final HashSet<String> usedNames = new HashSet<>();
    private McpBridge bridge;
    private String terminal;
    private String selected = "";
    private String search = "";
    private int location;
    private boolean busy, destroyed, loaded;
    private EditText nameInput;
    private TextView locationButton, statusView, accept;
    private LinearLayout list;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        if (terminal == null) { finish(); return; }
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
        TextView back = text("‹ Роботы.VPS · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(-1, dp(42)));
        TextView title = text("Создание робота", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(2));
        stripParams.bottomMargin = dp(8);
        header.addView(strip, stripParams);
        header.addView(text("Имя", 12, R.color.text_secondary));
        nameInput = new EditText(this);
        nameInput.setSingleLine(true);
        nameInput.setTextColor(getColor(R.color.text_primary));
        nameInput.addTextChangedListener(simpleWatcher(value -> refreshAccept()));
        header.addView(nameInput);
        LinearLayout filters = new LinearLayout(this);
        filters.setGravity(Gravity.CENTER_VERTICAL);
        header.addView(filters);
        locationButton = text("Расположение: All", 13, R.color.orange);
        locationButton.setGravity(Gravity.CENTER);
        locationButton.setBackgroundResource(R.drawable.input_background);
        locationButton.setOnClickListener(view -> {
            location = (location + 1) % LOCATIONS.length;
            locationButton.setText("Расположение: " + LOCATIONS[location]);
            renderList();
        });
        filters.addView(locationButton, new LinearLayout.LayoutParams(0, dp(42), 1));
        EditText find = new EditText(this);
        find.setHint("поиск...");
        find.setSingleLine(true);
        find.setTextColor(getColor(R.color.text_primary));
        find.addTextChangedListener(simpleWatcher(value -> { search = value.trim().toLowerCase(); renderList(); }));
        filters.addView(find, new LinearLayout.LayoutParams(0, dp(48), 1));
        statusView = text("Загрузка стратегий с VPS…", 12, R.color.text_secondary);
        header.addView(statusView);
        ScrollView scroll = new BarScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        list.setPadding(dp(14), dp(4), dp(14), dp(10));
        scroll.addView(list);
        LinearLayout buttons = new LinearLayout(this);
        buttons.setPadding(dp(14), dp(6), dp(14), dp(8));
        root.addView(buttons);
        TextView refresh = text("Обновить информацию", 13, R.color.orange);
        refresh.setGravity(Gravity.CENTER);
        refresh.setBackgroundResource(R.drawable.input_background);
        refresh.setOnClickListener(view -> new AlertDialog.Builder(this, R.style.OsEngineDialog)
            .setMessage("VPS перечитает сведения о роботах, это может занять время. Продолжить?")
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Продолжить", (dialog, which) -> load(true)).show());
        buttons.addView(refresh, new LinearLayout.LayoutParams(0, dp(44), 1));
        accept = text("Принять", 14, R.color.text_primary);
        accept.setGravity(Gravity.CENTER);
        accept.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams second = new LinearLayout.LayoutParams(0, dp(44), 1);
        second.leftMargin = dp(6);
        buttons.addView(accept, second);
        accept.setOnClickListener(view -> submit());
        refreshAccept();
    }

    private interface Changed { void on(String value); }

    private TextWatcher simpleWatcher(Changed changed) {
        return new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void onTextChanged(CharSequence s, int a, int b, int c) { }
            @Override public void afterTextChanged(Editable s) { changed.on(s.toString()); }
        };
    }

    private void load(boolean refresh) {
        if (busy) return;
        if (!RemoteSsh.isConnected()) { statusView.setText("Нет связи · SSH"); return; }
        busy = true;
        refreshAccept();
        statusView.setText("Загрузка стратегий с VPS…");
        worker.execute(() -> {
            JSONArray robots = null;
            JSONArray bots = null;
            String error = null;
            try {
                java.util.Map<String, Object> result = bridge.callBatch(terminal,
                    McpBridge.call("wiki_robots_list", new JSONObject().put("include_engines", false)
                        .put("refresh", refresh)),
                    McpBridge.call("bot_get_list", new JSONObject()));
                Object list = result.get("wiki_robots_list");
                if (list instanceof Exception) throw (Exception) list;
                if (list instanceof JSONObject) robots = ((JSONObject) list).optJSONArray("robots");
                if (robots == null) throw new IllegalStateException("Список стратегий недоступен");
                Object existing = result.get("bot_get_list");
                if (existing instanceof JSONObject) bots = ((JSONObject) existing).optJSONArray("bots");
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray finalRobots = robots, finalBots = bots;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) {
                    statusView.setText("Не удалось загрузить: " + finalError);
                    refreshAccept();
                    return;
                }
                strategies.clear();
                for (int i = 0; i < finalRobots.length(); i++) {
                    JSONObject item = finalRobots.optJSONObject(i);
                    if (item != null) strategies.add(item);
                }
                usedNames.clear();
                if (finalBots != null) for (int i = 0; i < finalBots.length(); i++) {
                    JSONObject bot = finalBots.optJSONObject(i);
                    if (bot != null) usedNames.add(bot.optString("name").toLowerCase());
                }
                loaded = true;
                renderList();
                refreshAccept();
            });
        });
    }

    private void renderList() {
        list.removeAllViews();
        int shown = 0;
        for (JSONObject strategy : strategies) {
            String name = strategy.optString("class_name");
            String place = strategy.optString("location");
            if (location > 0 && !LOCATIONS[location].equalsIgnoreCase(place)) continue;
            if (!search.isEmpty() && !name.toLowerCase().contains(search)) continue;
            shown++;
            boolean chosen = name.equals(selected);
            LinearLayout card = new LinearLayout(this);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(12), dp(8), dp(12), dp(8));
            card.setBackgroundResource(R.drawable.input_background);
            if (chosen) card.setAlpha(1f);
            TextView title = text(shown + "   " + name, 15, chosen ? R.color.orange : R.color.text_primary);
            title.setTypeface(null, Typeface.BOLD);
            card.addView(title);
            card.addView(text("Расположение: " + place + "  ·  Источники: " + sources(strategy), 12,
                R.color.text_secondary));
            String indicators = indicators(strategy);
            if (!indicators.isEmpty()) card.addView(text("Индикаторы: " + indicators, 12, R.color.text_secondary));
            String description = strategy.optString("description");
            if (!description.isEmpty()) {
                TextView more = text("Описание ?", 12, R.color.orange);
                more.setOnClickListener(view -> new AlertDialog.Builder(this, R.style.OsEngineDialog)
                    .setTitle(name).setMessage(description).setPositiveButton("ОК", null).show());
                card.addView(more);
            }
            card.setOnClickListener(view -> { selected = name; renderList(); refreshAccept(); });
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(6);
            list.addView(card, params);
        }
        statusView.setText("Стратегий: " + shown + " / " + strategies.size()
            + (selected.isEmpty() ? "" : " · выбрана " + selected));
        if (shown == 0) list.addView(text("Ничего не найдено", 14, R.color.text_secondary));
    }

    private static String sources(JSONObject strategy) {
        JSONArray array = strategy.optJSONArray("sources");
        StringBuilder result = new StringBuilder();
        if (array != null) for (int i = 0; i < array.length(); i++) {
            JSONObject item = array.optJSONObject(i);
            if (item == null) continue;
            if (result.length() > 0) result.append(", ");
            result.append(item.optString("type")).append(" ").append(item.optInt("count"));
        }
        return result.length() == 0 ? "—" : result.toString();
    }

    private static String indicators(JSONObject strategy) {
        JSONArray array = strategy.optJSONArray("indicators");
        StringBuilder result = new StringBuilder();
        if (array != null) for (int i = 0; i < array.length(); i++) {
            if (result.length() > 0) result.append(", ");
            result.append(array.optString(i));
        }
        return result.toString();
    }

    private void refreshAccept() {
        if (accept == null) return;
        boolean enabled = loaded && !busy;
        accept.setEnabled(enabled);
        accept.setAlpha(enabled ? 1f : .5f);
    }

    private void submit() {
        if (!loaded || busy) return;
        String name = nameInput.getText().toString().trim();
        if (selected.isEmpty()) { Toast.makeText(this, "Выберите стратегию", Toast.LENGTH_LONG).show(); return; }
        if (name.isEmpty()) { Toast.makeText(this, "Введите имя робота", Toast.LENGTH_LONG).show(); return; }
        if (usedNames.contains(name.toLowerCase())) {
            Toast.makeText(this, "Робот с таким именем уже есть", Toast.LENGTH_LONG).show();
            return;
        }
        new AlertDialog.Builder(this, R.style.OsEngineDialog).setTitle("Создать робота")
            .setMessage("Стратегия: " + selected + "\nИмя: " + name + "\nТерминал: " + terminal)
            .setNegativeButton("Отмена", null)
            .setPositiveButton("Создать", (dialog, which) -> create(name)).show();
    }

    private void create(String name) {
        if (busy || !RemoteSsh.isConnected()) return;
        busy = true;
        refreshAccept();
        statusView.setText("Создание робота…");
        String strategy = selected;
        worker.execute(() -> {
            String error = null;
            try {
                Object result = bridge.callBatch(terminal, McpBridge.call("bot_create",
                    new JSONObject().put("strategy_name", strategy).put("name", name))).get("bot_create");
                if (result instanceof Exception) throw (Exception) result;
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (destroyed) return;
                if (finalError != null) {
                    statusView.setText("Не удалось создать робота: " + finalError);
                    refreshAccept();
                    return;
                }
                Toast.makeText(this, "Робот создан: " + name, Toast.LENGTH_LONG).show();
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
