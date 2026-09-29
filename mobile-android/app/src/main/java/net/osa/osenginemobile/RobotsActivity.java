package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
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
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class RobotsActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::load;
    private McpBridge bridge;
    private String terminal;
    private String page = "Роботы";
    private JSONArray bots = new JSONArray();
    private JSONArray servers = new JSONArray();
    private boolean visible;
    private boolean loading;
    private boolean available;
    private boolean tablet;
    private TextView status;
    private LinearLayout botList;
    private LinearLayout pageContent;
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

    private void load() {
        handler.removeCallbacks(polling);
        if (!visible || loading || bridge == null) return;
        if (!RemoteSsh.isConnected()) {
            available = false;
            status.setText("Нет связи · SSH");
            render();
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
                render();
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private void render() {
        renderNav();
        robotSection.setVisibility("Роботы".equals(page) ? View.VISIBLE : View.GONE);
        pageContent.removeAllViews();
        if ("Роботы".equals(page)) renderBots();
        else if ("Серверы".equals(page)) renderServers();
        else if ("Ещё".equals(page)) renderMore();
        else pending(pageContent, page);
    }

    private void renderNav() {
        nav.removeAllViews();
        String[] tabs = tablet
            ? new String[]{"Роботы", "Позиции", "Серверы", "Портфель", "Прайм лог", "Ордера"}
            : new String[]{"Роботы", "Позиции", "Серверы", "Портфель", "Ещё"};
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
                ((ScrollView) findViewById(R.id.scroll_root)).smoothScrollTo(0, 0);
                render();
            });
        }
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
            table = new LinearLayout(this);
            table.setOrientation(LinearLayout.VERTICAL);
            horizontal.addView(table);
            botList.addView(horizontal);
            LinearLayout headings = new LinearLayout(this);
            String[] titles = {"#", "Имя робота", "Тип", "Первая бумага", "Поз (откр/закр)",
                "Вкл/выкл", "Эмулятор", "Чарт", "Параметры", "Удалить", "Журнал"};
            int[] widths = {40, 160, 150, 150, 145, 100, 110, 100, 105, 90, 95};
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
        row.addView(tableCell(name, 160, R.color.text_primary));
        row.addView(tableCell(bot.optString("class_name"), 150, R.color.text_primary));
        row.addView(tableCell(bot.optString("first_security"), 150, R.color.text_primary));
        row.addView(tableCell(bot.optInt("open_positions_count") + "/"
            + bot.optInt("closed_positions_count"), 145, R.color.text_primary));
        CheckBox on = check("", bot.optBoolean("is_on"));
        CheckBox emulator = check("", bot.optBoolean("emulator_is_on"));
        row.addView(on, new LinearLayout.LayoutParams(dp(100), dp(48)));
        row.addView(emulator, new LinearLayout.LayoutParams(dp(110), dp(48)));
        on.setOnClickListener(view -> changeState(bot, "is_on", on.isChecked()));
        emulator.setOnClickListener(view -> changeState(bot, "emulator_is_on", emulator.isChecked()));
        for (String action : new String[]{"Чарт", "Параметры", "Удалить", "Журнал"}) {
            int width = "Параметры".equals(action) ? 105 : "Удалить".equals(action) ? 90
                : "Журнал".equals(action) ? 95 : 100;
            TextView button = tableCell(action, width, R.color.orange);
            row.addView(button);
            button.setOnClickListener(view -> Toast.makeText(this,
                "Раздел «" + action + "» будет подключён на следующем этапе",
                Toast.LENGTH_LONG).show());
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
        cell.setLayoutParams(new LinearLayout.LayoutParams(dp(width), dp(48)));
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
        TextView heading = label(bot.optInt("number") + "   " + name + "   ›",
            17, R.color.text_primary);
        heading.setTypeface(null, Typeface.BOLD);
        card.addView(heading);
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
        card.setOnClickListener(view -> new AlertDialog.Builder(this)
            .setTitle(bot.optString("public_name", bot.optString("name")))
            .setItems(new String[]{"Чарт", "Параметры", "Журнал", "Удалить"},
                (dialog, which) -> Toast.makeText(this,
                    "Раздел «" + new String[]{"Чарт", "Параметры", "Журнал", "Удалить"}[which]
                        + "» будет подключён на следующем этапе", Toast.LENGTH_LONG).show())
            .show());
        return card;
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

    private void renderServers() {
        pageContent.addView(label("Серверы подключения", 19, R.color.text_primary));
        if (servers.length() == 0) {
            pageContent.addView(label(available ? "Подключений нет" : "Ожидание данных…",
                15, R.color.text_secondary));
        }
        for (int i = 0; i < servers.length(); i++) {
            JSONObject server = servers.optJSONObject(i);
            if (server == null) continue;
            boolean connected = "Connect".equalsIgnoreCase(server.optString("status"));
            TextView row = label(server.optString("type") + "  ·  "
                + server.optString("name") + "  ·  " + server.optString("status"),
                15, connected && available ? R.color.server_connected : R.color.text_secondary);
            row.setBackgroundResource(R.drawable.input_background);
            row.setPadding(dp(12), dp(12), dp(12), dp(12));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.topMargin = dp(8);
            pageContent.addView(row, params);
        }
    }

    private void renderMore() {
        for (String item : new String[]{"Ордера", "Прайм лог", "Копи трейдинг",
            "Журнал общий", "Миграция"}) {
            TextView row = label(item + "  ›", 16, R.color.text_primary);
            row.setPadding(dp(14), dp(14), dp(14), dp(14));
            row.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            pageContent.addView(row, params);
            row.setOnClickListener(view -> {
                page = item;
                render();
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
        if (!available || loading) return;
        loading = true;
        worker.execute(() -> {
            JSONArray strategies = null;
            String error = null;
            try {
                JSONObject arguments = new JSONObject().put("include_engines", false)
                    .put("refresh", false);
                Object result = bridge.callBatch(terminal,
                    McpBridge.call("wiki_robots_list", arguments)).get("wiki_robots_list");
                if (result instanceof JSONObject)
                    strategies = ((JSONObject) result).optJSONArray("robots");
                if (strategies == null) throw new IllegalStateException("Список стратегий недоступен");
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray finalStrategies = strategies;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (finalError != null) {
                    Toast.makeText(this, finalError, Toast.LENGTH_LONG).show();
                    return;
                }
                showStrategyChooser(finalStrategies);
            });
        });
    }

    private void showStrategyChooser(JSONArray strategies) {
        String[] names = new String[strategies.length()];
        for (int i = 0; i < names.length; i++) {
            JSONObject strategy = strategies.optJSONObject(i);
            names[i] = strategy == null ? "" : strategy.optString("class_name");
        }
        if (names.length == 0) {
            Toast.makeText(this, "Стратегии на VPS не найдены", Toast.LENGTH_LONG).show();
            return;
        }
        new AlertDialog.Builder(this).setTitle("Выберите стратегию")
            .setItems(names, (dialog, which) -> {
                EditText input = new EditText(this);
                input.setSingleLine(true);
                input.setHint("Имя робота");
                LinearLayout wrapper = new LinearLayout(this);
                wrapper.setPadding(dp(20), 0, dp(20), 0);
                wrapper.addView(input);
                new AlertDialog.Builder(this).setTitle("Добавить " + names[which])
                    .setView(wrapper)
                    .setNegativeButton("Отмена", null)
                    .setPositiveButton("Создать", (confirm, button) ->
                        createBot(names[which], input.getText().toString().trim()))
                    .show();
            }).show();
    }

    private void createBot(String strategy, String name) {
        if (name.isEmpty()) {
            Toast.makeText(this, "Введите имя робота", Toast.LENGTH_LONG).show();
            return;
        }
        loading = true;
        worker.execute(() -> {
            String error = null;
            try {
                JSONObject arguments = new JSONObject().put("strategy_name", strategy)
                    .put("name", name);
                Object result = bridge.callBatch(terminal,
                    McpBridge.call("bot_create", arguments)).get("bot_create");
                if (result instanceof Exception) throw (Exception) result;
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                Toast.makeText(this, finalError == null ? "Робот создан: " + name
                    : "Не удалось создать робота: " + finalError, Toast.LENGTH_LONG).show();
                load();
            });
        });
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
