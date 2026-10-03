package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.WindowInsets;
import android.widget.LinearLayout;
import android.widget.HorizontalScrollView;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.LocalTime;
import java.time.format.DateTimeFormatter;
import java.math.BigDecimal;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Journal positions belonging to one child Simple tab of a screener. */
public final class ScreenerJournalActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::refresh;
    private McpBridge bridge;
    private String terminal;
    private String botId;
    private String botName;
    private String tabName;
    private String security;
    private TextView status;
    private LinearLayout tabs;
    private HorizontalScrollView tabScroll;
    private LinearLayout content;
    private ScrollView scroll;
    private boolean visible;
    private boolean loading;
    private int selected;
    private JSONArray positions = new JSONArray();

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        botName = getIntent().getStringExtra("bot_name");
        tabName = getIntent().getStringExtra("tab_name");
        security = getIntent().getStringExtra("security_name");
        if (terminal == null || botId == null) {
            finish(); return;
        }
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
        scroll = new BarScrollView(this);
        root.addView(scroll);
        LinearLayout body = new LinearLayout(this);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(14), dp(16), dp(14), dp(16));
        scroll.addView(body);
        TextView back = text("‹ " + botName + " · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> finish());
        body.addView(back, new LinearLayout.LayoutParams(-1, dp(48)));
        TextView title = text(security == null ? "Журнал · " + botName + " · все бумаги"
            : "Журнал · " + security, 21, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        body.addView(title);
        status = text("Загрузка журнала…", 12, R.color.text_secondary);
        body.addView(status);
        tabScroll = null;   // three tabs share the width, nothing slides
        tabs = new LinearLayout(this);
        tabs.setBaselineAligned(false);
        body.addView(tabs, new LinearLayout.LayoutParams(-1, -2));
        content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        body.addView(content);
        renderTabs();
    }

    private void refresh() {
        handler.removeCallbacks(polling);
        if (!visible || loading) return;
        if (!RemoteSsh.isConnected()) {
            status.setText("Нет связи · SSH");
            handler.postDelayed(polling, 15_000);
            return;
        }
        loading = true;
        final int requested = selected;
        worker.execute(() -> {
            JSONArray next = null;
            String error = null;
            try {
                JSONArray rows;
                if (requested == 0) {
                    JSONObject args = new JSONObject().put("bot_name", botId);
                    java.util.Map<String, Object> response = bridge.callBatch(terminal,
                        McpBridge.call("bot_journal_get_open_positions", args),
                        McpBridge.call("bot_journal_get_closed_positions", args));
                    JSONArray open = responsePositions(response.get("bot_journal_get_open_positions"));
                    JSONArray closed = responsePositions(response.get("bot_journal_get_closed_positions"));
                    rows = new JSONArray();
                    for (int i = 0; i < open.length(); i++) rows.put(open.opt(i));
                    for (int i = 0; i < closed.length(); i++) rows.put(closed.opt(i));
                } else {
                    // the journal answer carries side, result and times, unlike the short list of the tab
                    String tool = requested == 1 ? "bot_journal_get_open_positions"
                        : "bot_journal_get_closed_positions";
                    JSONObject args = requested == 1 ? new JSONObject().put("bot_name", botId)
                        : new JSONObject().put("bot_name", botId).put("include_failed", true);
                    rows = responsePositions(bridge.callBatch(terminal,
                        McpBridge.call(tool, args)).get(tool));
                }
                next = new JSONArray();
                for (int i = 0; i < rows.length(); i++) {
                    JSONObject position = rows.optJSONObject(i);
                    if (position != null && (security == null || SecurityNames.sameTicker(
                        security, position.optString("security_name"))))
                        next.put(position);
                }
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray result = next;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (requested == selected) {
                    if (finalError != null) status.setText("Не удалось загрузить журнал: " + finalError);
                    else {
                        positions = result;
                        status.setText("Обновлено " + LocalTime.now()
                            .format(DateTimeFormatter.ofPattern("HH:mm:ss")));
                        if (selected == 0) renderEquity();
                        else renderPositions();
                    }
                }
                if (requested != selected) handler.post(polling);
                else handler.postDelayed(polling, 15_000);
            });
        });
    }

    private static JSONArray responsePositions(Object value) throws Exception {
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject)) throw new IllegalStateException("Нет позиций журнала");
        JSONArray rows = ((JSONObject) value).optJSONArray("positions");
        if (rows == null) throw new IllegalStateException("Нет списка позиций");
        return rows;
    }

    private void renderTabs() {
        tabs.removeAllViews();
        String[] names = {"Эквити", "Открытые позиции", "Закрытые позиции"};
        for (int i = 0; i < names.length; i++) {
            final int index = i;
            TextView tab = text(names[i], 12, selected == i ? R.color.orange : R.color.text_primary);
            tab.setGravity(Gravity.CENTER);
            tab.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams tabParams = new LinearLayout.LayoutParams(0, dp(48), 1);
            if (i > 0) tabParams.leftMargin = dp(4);
            tabs.addView(tab, tabParams);
            tab.setOnClickListener(view -> {
                selected = index;
                positions = new JSONArray();
                status.setText("Загрузка журнала…");
                renderTabs();
                content.removeAllViews();
                handler.removeCallbacks(polling);
                handler.post(polling);
            });
        }
    }

    private void renderEquity() {
        int scrollY = scroll.getScrollY();
        content.removeAllViews();
        JSONArray points = JournalPage.buildEquityPoints(positions);
        if (points.length() == 0) {
            content.addView(text("Данных для графика нет", 14, R.color.text_secondary));
            return;
        }
        boolean tablet = getResources().getConfiguration().smallestScreenWidthDp >= 600;
        content.addView(new JournalPage.Graph(this, points,
            new String[]{"total", "long", "short"},
            new int[]{0xFFFFFFFF, 0xFF00BFFF, 0xFF915000}),
            new LinearLayout.LayoutParams(-1, dp(tablet ? 360 : 260)));
        LinearLayout legend = new LinearLayout(this);
        legend.addView(colored("● Общая прибыль   ", 0xFFFFFFFF));
        legend.addView(colored("● Лонг   ", 0xFF00BFFF));
        legend.addView(colored("● Шорт", 0xFF915000));
        content.addView(legend);
        JournalPage.addBars(this, content, points, tablet);
        JSONObject last = points.optJSONObject(points.length() - 1);
        if (last != null) content.addView(text("Общая прибыль: "
            + BigDecimal.valueOf(last.optDouble("total"))
                .stripTrailingZeros().toPlainString(), 14, R.color.text_primary));
        scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private TextView colored(String value, int color) {
        TextView view = text(value, 12, R.color.text_primary);
        view.setTextColor(color);
        return view;
    }

    private void renderPositions() {
        content.removeAllViews();
        if (positions.length() == 0) {
            content.addView(text("Позиций нет", 14, R.color.text_secondary));
            return;
        }
        for (int i = 0; i < positions.length(); i++) {
            JSONObject position = positions.optJSONObject(i);
            if (position == null) continue;
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.VERTICAL);
            row.setPadding(dp(12), dp(9), dp(12), dp(9));
            row.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(6);
            content.addView(row, params);
            row.addView(text("№ " + position.optString("position_number",
                position.optString("number")) + " · " + position.optString("state"),
                15, R.color.text_primary));
            String details = position.optString("side", position.optString("direction"))
                + " · Объём " + position.optString("open_volume", position.optString("volume"));
            if (position.has("profit_abs")) details += " · П/У " + position.optString("profit_abs");
            row.addView(text(details, 13, R.color.text_secondary));
        }
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
