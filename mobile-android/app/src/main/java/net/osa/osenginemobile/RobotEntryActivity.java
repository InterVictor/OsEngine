package net.osa.osenginemobile;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Opens the Simple chart directly, or the desktop screener's child security list. */
public final class RobotEntryActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::load;
    private final ArrayList<JSONObject> children = new ArrayList<>();
    private McpBridge bridge;
    private String terminal;
    private String botId;
    private String botName;
    private volatile String screenerSource;
    private TextView status;
    private LinearLayout list;
    private ScrollView scroll;
    private boolean visible;
    private boolean loading;

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

    private void buildScreen() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(getColor(R.color.background));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
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
        scroll = new ScrollView(this);
        root.addView(scroll);
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(14), dp(16), dp(14), dp(16));
        scroll.addView(content);
        TextView back = text("‹ Роботы.VPS · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> finish());
        content.addView(back, new LinearLayout.LayoutParams(-1, dp(48)));
        TextView title = text(botName, 21, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        content.addView(title);
        status = text("Загрузка бумаг…", 13, R.color.text_secondary);
        content.addView(status);
        // Screener-wide windows: shared by every ticker, so they sit above the ticker list.
        ActionGrid.add(this, content, new String[][]{{"Риск-менеджер", ActionGrid.RISK},
            {"Сопровождение позиции", ActionGrid.SUPPORT}, {"Настройки данных", ActionGrid.DATA}},
            terminal, botId, botName, () -> screenerSource);
        list = new LinearLayout(this);
        list.setOrientation(LinearLayout.VERTICAL);
        content.addView(list);
    }

    private void load() {
        handler.removeCallbacks(polling);
        if (!visible || loading) return;
        if (!RemoteSsh.isConnected()) {
            status.setText("Нет связи · SSH");
            handler.postDelayed(polling, 15_000);
            return;
        }
        loading = true;
        worker.execute(() -> {
            ArrayList<JSONObject> next = new ArrayList<>();
            String simple = null;
            String error = null;
            try {
                Object response = bridge.callBatch(terminal, McpBridge.call("bot_get_sources",
                    new JSONObject().put("bot_id", botId))).get("bot_get_sources");
                if (response instanceof Exception) throw (Exception) response;
                if (!(response instanceof JSONObject)) throw new IllegalStateException("Нет вкладок робота");
                JSONArray sources = ((JSONObject) response).optJSONArray("sources");
                if (sources == null) throw new IllegalStateException("Нет списка вкладок робота");
                boolean screener = false;
                for (int i = 0; i < sources.length(); i++) {
                    JSONObject source = sources.optJSONObject(i);
                    if (source == null) continue;
                    if ("Simple".equals(source.optString("type")) && simple == null)
                        simple = source.optString("name");
                    if (!"Screener".equals(source.optString("type"))) continue;
                    screener = true;
                    if (screenerSource == null) screenerSource = source.optString("name");
                    Object value = bridge.callBatch(terminal, McpBridge.call("bot_screener_get_tabs",
                        new JSONObject().put("bot_id", botId)
                            .put("tab_name", source.optString("name"))))
                        .get("bot_screener_get_tabs");
                    if (value instanceof Exception) throw (Exception) value;
                    if (!(value instanceof JSONObject)) throw new IllegalStateException("Нет бумаг скринера");
                    JSONArray tabs = ((JSONObject) value).optJSONArray("tabs");
                    if (tabs == null) throw new IllegalStateException("Нет списка бумаг скринера");
                    for (int j = 0; j < tabs.length(); j++) {
                        JSONObject child = tabs.optJSONObject(j);
                        if (child != null && !child.optString("tab_name").isEmpty()) next.add(child);
                    }
                }
                if (screener) simple = null;
                else if (simple == null) error = "У робота нет торговой вкладки";
            } catch (Exception e) { error = e.getMessage(); }
            String firstSimple = simple, finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (finalError != null) status.setText("Не удалось загрузить бумаги: " + finalError);
                else if (firstSimple != null) {
                    openChart(firstSimple, false);
                    finish();
                    return;
                } else {
                    children.clear();
                    children.addAll(next);
                    status.setText("Бумаги · " + children.size());
                    renderList();
                }
                handler.postDelayed(polling, 15_000);
            });
        });
    }

    private void renderList() {
        int scrollY = scroll.getScrollY();
        list.removeAllViews();
        if (children.isEmpty()) {
            list.addView(text("В скринере пока нет торговых бумаг", 14, R.color.text_secondary));
            return;
        }
        for (JSONObject child : children) {
            String tab = child.optString("tab_name");
            String security = child.optString("security_name");
            if (security.isEmpty()) security = tab;
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setPadding(dp(12), dp(10), dp(12), dp(10));
            row.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(-1, -2);
            rowParams.bottomMargin = dp(6);
            list.addView(row, rowParams);
            TextView title = text(security, 16, R.color.text_primary);
            title.setTypeface(null, Typeface.BOLD);
            row.addView(title);
            row.addView(text(child.optString("security_class") + " · Поз (откр/всего): "
                + child.optInt("positions_open") + "/" + child.optInt("positions_total"),
                12, R.color.text_secondary));
            LinearLayout actions = new LinearLayout(this);
            actions.setOrientation(LinearLayout.HORIZONTAL);
            row.addView(actions);
            TextView chart = action("Чарт");
            TextView journal = action("Журнал");
            actions.addView(chart, new LinearLayout.LayoutParams(0, dp(44), 1));
            actions.addView(journal, new LinearLayout.LayoutParams(0, dp(44), 1));
            chart.setOnClickListener(view -> openChart(tab, true));
            String ticker = security;
            journal.setOnClickListener(view -> {
                Intent intent = new Intent(this, ScreenerJournalActivity.class);
                intent.putExtra("terminal_name", terminal);
                intent.putExtra("bot_id", botId);
                intent.putExtra("bot_name", botName);
                intent.putExtra("tab_name", tab);
                intent.putExtra("security_name", ticker);
                startActivity(intent);
            });
        }
        scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private void openChart(String tab, boolean screenerTicker) {
        Intent intent = new Intent(this, ChartActivity.class);
        intent.putExtra("terminal_name", terminal);
        intent.putExtra("bot_id", botId);
        intent.putExtra("bot_name", botName);
        intent.putExtra("tab_name", tab);
        intent.putExtra("screener", screenerTicker);
        startActivity(intent);
    }

    private TextView action(String value) {
        TextView view = text(value, 14, R.color.orange);
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
