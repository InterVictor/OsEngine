package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.ScaleGestureDetector;
import android.view.View;
import android.view.WindowInsets;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.LocalTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Entry from a VPS robot tile to its live Simple-tab candle chart. */
public final class ChartActivity extends Activity {
    private static final String[] LOWER_TABS = {"Открытые позиции", "Стоп-лимит",
        "Закрытые позиции", "Лог робота"};
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::refresh;
    private final ArrayList<String> sources = new ArrayList<>();
    private McpBridge bridge;
    private String terminal;
    private String botId;
    private String botName;
    private String tabName;
    private boolean visible;
    private boolean loading;
    private LinearLayout tabs;
    private TextView info;
    private TextView status;
    private CandleView chart;
    private LinearLayout lowerTabs;
    private HorizontalScrollView tabScroll;
    private volatile boolean screenerBot;
    private boolean screenerChart;
    private LinearLayout lowerContent;
    private ScrollView outerScroll;
    private int lowerTab;
    private String lowerSnapshot;
    private volatile long lastLowerFetchMs;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        botName = getIntent().getStringExtra("bot_name");
        tabName = getIntent().getStringExtra("tab_name");
        screenerChart = getIntent().getBooleanExtra("screener", false);
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
        setContentView(root);
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
        root.requestApplyInsets();
        // Fixed header: the way back and the title stay on screen while the chart scrolls.
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.VERTICAL);
        header.setPadding(dp(14), dp(6), dp(14), dp(4));
        root.addView(header);
        TextView back = text("‹ Роботы.VPS · " + terminal, 15, R.color.orange);
        back.setOnClickListener(view -> finish());
        header.addView(back, new LinearLayout.LayoutParams(-1, dp(40)));
        TextView title = text(botName + " · Чарт", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        header.addView(title);
        info = text("Загрузка данных робота…", 12, R.color.text_secondary);
        header.addView(info);
        status = text("", 11, R.color.text_secondary);
        header.addView(status);
        ScrollView scroll = new ScrollView(this);
        outerScroll = scroll;
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(14), dp(6), dp(14), dp(16));
        scroll.addView(content);
        // A screener's data / risk / position-support settings are shared by all its tickers and live on the
        // ticker list; a ticker's chart only keeps «Торговать». Simple robots have no list, so they keep all four.
        ActionGrid.add(this, content, screenerChart
            ? new String[][]{{"Торговать", ActionGrid.TRADE}}
            : new String[][]{{"Риск-менеджер", ActionGrid.RISK}, {"Сопровождение позиции", ActionGrid.SUPPORT},
                {"Настройки данных", ActionGrid.DATA}, {"Торговать", ActionGrid.TRADE}},
            terminal, botId, botName, () -> tabName);
        // Tabs are only for robots with several own Simple tabs; screener tickers are picked on the previous screen.
        tabScroll = new HorizontalScrollView(this);
        tabScroll.setHorizontalScrollBarEnabled(false);
        tabs = new LinearLayout(this);
        tabScroll.addView(tabs);
        tabScroll.setVisibility(View.GONE);
        content.addView(tabScroll);
        chart = new CandleView();
        LinearLayout.LayoutParams graph = new LinearLayout.LayoutParams(-1,
            dp(getResources().getConfiguration().smallestScreenWidthDp >= 600 ? 490 : 340));
        graph.topMargin = dp(8);
        baseChartHeight = graph.height;
        content.addView(chart, graph);
        // Lower tabs share the width equally and never slide.
        lowerTabs = new LinearLayout(this);
        lowerTabs.setBaselineAligned(false);
        LinearLayout.LayoutParams lowerParams = new LinearLayout.LayoutParams(-1, dp(48));
        lowerParams.topMargin = dp(12);
        content.addView(lowerTabs, lowerParams);
        lowerContent = new LinearLayout(this);
        lowerContent.setOrientation(LinearLayout.VERTICAL);
        content.addView(lowerContent);
        renderLowerTabs();
        lowerContent.addView(text("Ожидание позиций…", 14, R.color.text_secondary));
    }

    private void refresh() {
        handler.removeCallbacks(polling);
        if (!visible || loading) return;
        if (!RemoteSsh.isConnected()) {
            status.setText("Нет связи · SSH");
            handler.postDelayed(polling, 5_000);
            return;
        }
        loading = true;
        final int requestedLowerTab = lowerTab;
        worker.execute(() -> {
            JSONArray nextSources = null;
            JSONObject snapshot = null;
            JSONArray indicatorList = null;
            JSONObject lower = null;
            String lowerError = null;
            String error = null;
            try {
                if (sources.isEmpty()) {
                    Object value = bridge.callBatch(terminal, McpBridge.call("bot_get_sources",
                        new JSONObject().put("bot_id", botId))).get("bot_get_sources");
                    if (value instanceof Exception) throw (Exception) value;
                    if (!(value instanceof JSONObject)) throw new IllegalStateException("Нет вкладок робота");
                    nextSources = ((JSONObject) value).optJSONArray("sources");
                    if (nextSources == null) throw new IllegalStateException("Нет списка вкладок робота");
                    JSONArray chartSources = new JSONArray();
                    for (int i = 0; i < nextSources.length(); i++) {
                        JSONObject source = nextSources.optJSONObject(i);
                        if (source == null) continue;
                        if ("Simple".equals(source.optString("type"))) chartSources.put(source);
                        else if ("Screener".equals(source.optString("type"))) {
                            screenerBot = true;
                            JSONObject args = new JSONObject().put("bot_id", botId)
                                .put("tab_name", source.optString("name"));
                            Object children = bridge.callBatch(terminal,
                                McpBridge.call("bot_screener_get_tabs", args))
                                .get("bot_screener_get_tabs");
                            if (children instanceof Exception) throw (Exception) children;
                            if (!(children instanceof JSONObject)) continue;
                            JSONArray childTabs = ((JSONObject) children).optJSONArray("tabs");
                            if (childTabs == null) continue;
                            for (int j = 0; j < childTabs.length(); j++) {
                                JSONObject child = childTabs.optJSONObject(j);
                                if (child != null && !child.optString("tab_name").isEmpty())
                                    chartSources.put(new JSONObject().put("type", "Simple")
                                        .put("name", child.optString("tab_name")));
                            }
                        }
                    }
                    nextSources = chartSources;
                }
                String selected = tabName;
                if (selected == null && nextSources != null)
                    for (int i = 0; i < nextSources.length(); i++) {
                        JSONObject source = nextSources.optJSONObject(i);
                        if (source != null && "Simple".equals(source.optString("type"))) {
                            selected = source.optString("name"); break;
                        }
                    }
                if (selected != null) {
                    Object value = bridge.callBatch(terminal, McpBridge.call("bot_chart_get_snapshot",
                        new JSONObject().put("bot_id", botId).put("tab_name", selected)
                            .put("candle_count", 500))).get("bot_chart_get_snapshot");
                    if (value instanceof Exception) throw (Exception) value;
                    if (!(value instanceof JSONObject)) throw new IllegalStateException("Нет данных графика");
                    snapshot = (JSONObject) value;
                    // Indicator values are computed by the robot on the VPS; the phone only draws them.
                    try {
                        Object indicators = bridge.callBatch(terminal, McpBridge.call("bot_chart_get_indicators",
                            new JSONObject().put("bot_id", botId).put("tab_name", selected)
                                .put("candle_count", 500))).get("bot_chart_get_indicators");
                        if (indicators instanceof JSONObject)
                            indicatorList = ((JSONObject) indicators).optJSONArray("indicators");
                    } catch (Exception ignored) { /* indicators are optional */ }
                    try {
                        if (SystemClock.elapsedRealtime() - lastLowerFetchMs < 15_000)
                            throw new SkipLowerFetch();
                        String lowerTool = lowerTool(requestedLowerTab);
                        JSONObject lowerArgs = requestedLowerTab == 0
                            ? new JSONObject().put("bot_id", botId).put("tab_name", selected)
                            : requestedLowerTab == 3
                                ? new JSONObject().put("bot_id", botId).put("tab_name", selected)
                                    .put("count", 100)
                                : new JSONObject().put("bot_name", botId).put("limit", 500);
                        Object lowerValue = bridge.callBatch(terminal,
                            McpBridge.call(lowerTool, lowerArgs)).get(lowerTool);
                        if (lowerValue instanceof Exception) throw (Exception) lowerValue;
                        if (!(lowerValue instanceof JSONObject))
                            throw new IllegalStateException("Неверный ответ MCP");
                        lower = (JSONObject) lowerValue;
                    } catch (SkipLowerFetch ignored) {
                    } catch (Exception e) { lowerError = e.getMessage(); }
                }
            } catch (Exception e) { error = e.getMessage(); }
            JSONArray resolvedSources = nextSources;
            JSONObject resolvedSnapshot = snapshot;
            JSONArray resolvedIndicators = indicatorList;
            JSONObject resolvedLower = lower;
            String resolvedLowerError = lowerError;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (resolvedSources != null) {
                    for (int i = 0; i < resolvedSources.length(); i++) {
                        JSONObject source = resolvedSources.optJSONObject(i);
                        if (source != null && "Simple".equals(source.optString("type")))
                            sources.add(source.optString("name"));
                    }
                    if (tabName == null && !sources.isEmpty()) tabName = sources.get(0);
                    renderTabs();
                }
                if (resolvedSnapshot != null
                    && !resolvedSnapshot.optString("tab_name").equals(tabName)) {
                    handler.post(polling);
                    return;
                }
                if (finalError != null) status.setText("Не удалось загрузить чарт: " + finalError);
                else if (tabName == null) status.setText("У робота нет вкладки Simple для свечного чарта");
                else if (resolvedSnapshot != null) {
                    info.setText(resolvedSnapshot.optString("security_name") + " · "
                        + resolvedSnapshot.optString("time_frame") + " · " + tabName);
                    JSONArray candles = resolvedSnapshot.optJSONArray("candles");
                    if (candles != null) chart.setCandles(candles);
                    if (resolvedIndicators != null) chart.setIndicators(resolvedIndicators);
                    status.setText("Обновлено " + LocalTime.now()
                        .format(DateTimeFormatter.ofPattern("HH:mm:ss"))
                        + " · свечей " + resolvedSnapshot.optInt("count"));
                    if (requestedLowerTab == lowerTab && resolvedLowerError != null)
                        renderLowerError(resolvedLowerError);
                    else if (requestedLowerTab == lowerTab && resolvedLower != null) {
                        lastLowerFetchMs = SystemClock.elapsedRealtime();
                        renderLower(resolvedLower, resolvedSnapshot.optString("security_name"));
                    }
                }
                handler.postDelayed(polling, 5_000);
            });
        });
    }

    private static final class SkipLowerFetch extends Exception { }

    private String lowerTool(int tab) {
        switch (tab) {
            case 0: return "bot_position_get_open";
            case 1: return "bot_journal_get_stop_limit_positions";
            case 2: return "bot_journal_get_closed_positions";
            default: return "bot_chart_get_log";
        }
    }

    private void renderLowerTabs() {
        lowerTabs.removeAllViews();
        for (int i = 0; i < LOWER_TABS.length; i++) {
            final int selected = i;
            TextView tab = text(LOWER_TABS[i], 12,
                lowerTab == i ? R.color.orange : R.color.text_primary);
            tab.setGravity(Gravity.CENTER);
            tab.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams tabParams = new LinearLayout.LayoutParams(0, dp(48), 1);
            if (i > 0) tabParams.leftMargin = dp(4);
            lowerTabs.addView(tab, tabParams);
            tab.setOnClickListener(view -> {
                lowerTab = selected;
                lowerSnapshot = null;
                lastLowerFetchMs = 0;
                renderLowerTabs();
                lowerContent.removeAllViews();
                lowerContent.addView(text("Ожидание данных…", 14, R.color.text_secondary));
                handler.removeCallbacks(polling);
                handler.post(polling);
            });
        }
    }

    private void renderLowerError(String error) {
        lowerContent.removeAllViews();
        lowerContent.addView(text("Не удалось загрузить " + LOWER_TABS[lowerTab]
            + ": " + error, 13, R.color.text_secondary));
    }

    private void renderLower(JSONObject response, String security) {
        String snapshot = response.toString() + security + lowerTab + tabName;
        if (snapshot.equals(lowerSnapshot)) return;
        lowerSnapshot = snapshot;
        int scrollY = outerScroll.getScrollY();
        lowerContent.removeAllViews();
        JSONArray rows = response.optJSONArray(lowerTab == 3 ? "messages" : "positions");
        if (rows == null || rows.length() == 0) {
            lowerContent.addView(text(lowerTab == 3 ? "Сообщений нет" : "Позиций нет",
                14, R.color.text_secondary));
            return;
        }
        int shown = 0;
        for (int i = 0; i < rows.length(); i++) {
            JSONObject row = rows.optJSONObject(i);
            if (row == null) continue;
            if (lowerTab == 1 && !tabName.equals(row.optString("tab_name"))) continue;
            if (lowerTab == 2 && !SecurityNames.sameTicker(
                security, row.optString("security_name"))) continue;
            LinearLayout card = new LinearLayout(this);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(10), dp(7), dp(10), dp(7));
            card.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(5);
            lowerContent.addView(card, params);
            if (lowerTab == 3) {
                card.addView(text(row.optString("time") + " · " + row.optString("type"),
                    12, R.color.text_secondary));
                card.addView(text(row.optString("message"), 13, R.color.text_primary));
            } else {
                String number = row.optString(lowerTab == 0 ? "position_number" : "number");
                String name = row.optString("security_name");
                card.addView(text("№ " + number + " · " + name, 14, R.color.text_primary));
                card.addView(text(row.optString("state") + " · "
                    + row.optString("side", row.optString("direction")) + " · "
                    + row.optString(lowerTab == 0 ? "open_volume" : "volume"),
                    12, R.color.text_secondary));
            }
            shown++;
        }
        if (shown == 0) lowerContent.addView(text("Позиций нет", 14, R.color.text_secondary));
        outerScroll.post(() -> outerScroll.scrollTo(0, scrollY));
    }

    private void renderTabs() {
        tabs.removeAllViews();
        tabScroll.setVisibility(screenerBot || sources.size() <= 1 ? View.GONE : View.VISIBLE);
        for (String source : sources) {
            TextView tab = text(source, 13, source.equals(tabName) ? R.color.orange : R.color.text_primary);
            tab.setPadding(dp(12), 0, dp(12), 0);
            tab.setGravity(Gravity.CENTER);
            tab.setBackgroundResource(R.drawable.input_background);
            tabs.addView(tab, new LinearLayout.LayoutParams(dp(130), dp(44)));
            tab.setOnClickListener(view -> {
                tabName = source;
                chart.resetViewport();
                lowerSnapshot = null;
                lastLowerFetchMs = 0;
                renderTabs();
                handler.removeCallbacks(polling);
                handler.post(polling);
            });
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

    private int baseChartHeight;

    private final class CandleView extends View {
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private JSONArray indicators = new JSONArray();
        private final ArrayList<String> areas = new ArrayList<>();
        private final ScaleGestureDetector scale = new ScaleGestureDetector(ChartActivity.this,
            new ScaleGestureDetector.SimpleOnScaleGestureListener() {
                @Override public boolean onScale(ScaleGestureDetector detector) {
                    count = Math.max(20, Math.min(300,
                        Math.round(count / detector.getScaleFactor())));
                    invalidate();
                    return true;
                }
            });
        private JSONArray candles = new JSONArray();
        private int count = 65;
        private int offset;
        private float lastX;

        CandleView() {
            super(ChartActivity.this);
            setBackgroundResource(R.drawable.input_background);
        }

        void setCandles(JSONArray next) { candles = next; invalidate(); }

        /** Indicators of the robot's tab: «Prime» lines go over the price, other areas get panels below. */
        void setIndicators(JSONArray next) {
            indicators = next;
            ArrayList<String> found = new ArrayList<>();
            for (int i = 0; i < next.length(); i++) {
                JSONObject item = next.optJSONObject(i);
                if (item == null || !item.optBoolean("is_supported", true)) continue;
                String area = item.optString("area", "Prime");
                if (!area.isEmpty() && !"Prime".equals(area) && !found.contains(area)) found.add(area);
            }
            if (!found.equals(areas)) {
                areas.clear();
                areas.addAll(found);
                android.view.ViewGroup.LayoutParams params = getLayoutParams();
                if (params != null && baseChartHeight > 0) {
                    params.height = baseChartHeight + areas.size() * dp(PANEL_DP);
                    setLayoutParams(params);
                }
            }
            invalidate();
        }

        private static final int PANEL_DP = 110;

        /** Value of series point aligned to candle i (values are the trailing candle_count points). */
        private double valueAt(JSONArray values, int candleIndex) {
            int index = values.length() - (candles.length() - candleIndex);
            return index < 0 || index >= values.length() ? Double.NaN : values.optDouble(index, Double.NaN);
        }

        private boolean skip(double value, boolean zeroIsGap, int candleIndex) {
            return Double.isNaN(value) || (value == 0 && (zeroIsGap || candleIndex == candles.length() - 1));
        }

        private void drawSeries(Canvas canvas, JSONObject series, int start, int end,
                                float left, float bar, double min, double max, float top, float bottom) {
            JSONArray values = series.optJSONArray("values");
            if (values == null) return;
            boolean zeroIsGap = series.optBoolean("zero_is_gap");
            paint.setColor(series.optInt("color_argb", 0xFFFFFFFF) | 0xFF000000);
            paint.setStrokeWidth(Math.max(1, dp(Math.min(3, Math.max(1, series.optInt("line_width", 1))))));
            float px = 0, py = 0;
            boolean have = false;
            for (int i = start; i < end; i++) {
                double v = valueAt(values, i);
                if (skip(v, zeroIsGap, i)) { have = false; continue; }
                float x = left + bar * (i - start + .5f);
                float y = y(v, min, max, top, bottom);
                if (have && y >= top - dp(2) && y <= bottom + dp(2) && py >= top - dp(2) && py <= bottom + dp(2))
                    canvas.drawLine(px, py, x, y, paint);
                px = x; py = y; have = true;
            }
        }

        private void drawPanels(Canvas canvas, int start, int end, float left, float right, float bar,
                                float panelTop) {
            for (int a = 0; a < areas.size(); a++) {
                float top = panelTop + a * dp(PANEL_DP) + dp(6);
                float bottom = panelTop + (a + 1) * dp(PANEL_DP) - dp(6);
                double min = Double.POSITIVE_INFINITY, max = Double.NEGATIVE_INFINITY;
                for (int s = 0; s < indicators.length(); s++) {
                    JSONObject item = indicators.optJSONObject(s);
                    if (item == null || !areas.get(a).equals(item.optString("area"))) continue;
                    JSONArray list = item.optJSONArray("data_series");
                    if (list == null) continue;
                    for (int k = 0; k < list.length(); k++) {
                        JSONObject series = list.optJSONObject(k);
                        JSONArray values = series == null ? null : series.optJSONArray("values");
                        if (values == null) continue;
                        for (int i = start; i < end; i++) {
                            double v = valueAt(values, i);
                            if (skip(v, series.optBoolean("zero_is_gap"), i)) continue;
                            min = Math.min(min, v); max = Math.max(max, v);
                        }
                    }
                }
                paint.setColor(0xFF313A42);
                paint.setStrokeWidth(1);
                canvas.drawLine(left, top - dp(3), right, top - dp(3), paint);
                paint.setColor(getColor(R.color.text_secondary));
                paint.setTextSize(dp(10));
                canvas.drawText(areas.get(a), left, top + dp(8), paint);
                if (!Double.isFinite(min)) continue;
                if (max <= min) max = min + 1;
                canvas.drawText(compact(max), right + dp(3), top + dp(8), paint);
                canvas.drawText(compact(min), right + dp(3), bottom, paint);
                for (int s = 0; s < indicators.length(); s++) {
                    JSONObject item = indicators.optJSONObject(s);
                    if (item == null || !areas.get(a).equals(item.optString("area"))) continue;
                    JSONArray list = item.optJSONArray("data_series");
                    if (list == null) continue;
                    for (int k = 0; k < list.length(); k++) {
                        JSONObject series = list.optJSONObject(k);
                        if (series != null) drawSeries(canvas, series, start, end, left, bar, min, max, top, bottom);
                    }
                }
            }
        }

        private String compact(double value) {
            return java.math.BigDecimal.valueOf(value).round(new java.math.MathContext(4))
                .stripTrailingZeros().toPlainString();
        }
        void resetViewport() { count = 65; offset = 0; invalidate(); }

        @Override public boolean onTouchEvent(MotionEvent event) {
            scale.onTouchEvent(event);
            if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                lastX = event.getX(); return true;
            }
            if (event.getActionMasked() == MotionEvent.ACTION_MOVE && !scale.isInProgress()) {
                float width = Math.max(1, getWidth() - dp(52));
                int bars = Math.round((event.getX() - lastX) * count / width);
                if (bars != 0) {
                    offset = Math.max(0, Math.min(Math.max(0, candles.length() - count), offset + bars));
                    lastX = event.getX(); invalidate();
                }
                return true;
            }
            return true;
        }

        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            if (candles.length() == 0) {
                paint.setColor(getColor(R.color.text_secondary));
                paint.setTextSize(dp(14));
                canvas.drawText("Свечей пока нет", dp(14), getHeight() / 2f, paint);
                return;
            }
            int end = Math.max(0, candles.length() - offset);
            int start = Math.max(0, end - count);
            double low = Double.POSITIVE_INFINITY, high = Double.NEGATIVE_INFINITY;
            for (int i = start; i < end; i++) {
                JSONObject candle = candles.optJSONObject(i);
                if (candle == null) continue;
                low = Math.min(low, candle.optDouble("low"));
                high = Math.max(high, candle.optDouble("high"));
            }
            if (!Double.isFinite(low)) return;
            if (high <= low) high = low + 1;
            float left = dp(8), right = getWidth() - dp(48);
            float panelsHeight = areas.size() * dp(PANEL_DP);
            float top = dp(20), bottom = getHeight() - dp(24) - panelsHeight;
            paint.setColor(0xFF313A42);
            paint.setStrokeWidth(1);
            for (int j = 0; j < 5; j++) {
                float y = top + (bottom - top) * j / 4f;
                canvas.drawLine(left, y, right, y, paint);
            }
            float bar = (right - left) / Math.max(1, end - start);
            for (int i = start; i < end; i++) {
                JSONObject candle = candles.optJSONObject(i);
                if (candle == null) continue;
                double open = candle.optDouble("open"), close = candle.optDouble("close");
                float x = left + bar * (i - start + .5f);
                float yHigh = y(candle.optDouble("high"), low, high, top, bottom);
                float yLow = y(candle.optDouble("low"), low, high, top, bottom);
                float yOpen = y(open, low, high, top, bottom);
                float yClose = y(close, low, high, top, bottom);
                paint.setColor(close >= open ? 0xFF399D36 : 0xFFFE5400);
                paint.setStrokeWidth(Math.max(1, dp(1)));
                canvas.drawLine(x, yHigh, x, yLow, paint);
                canvas.drawRect(x - Math.max(1, bar * .32f), Math.min(yOpen, yClose),
                    x + Math.max(1, bar * .32f), Math.max(yOpen + 1, yClose), paint);
            }
            for (int s = 0; s < indicators.length(); s++) {
                JSONObject item = indicators.optJSONObject(s);
                if (item == null || !item.optBoolean("is_supported", true)) continue;
                String area = item.optString("area", "Prime");
                if (!area.isEmpty() && !"Prime".equals(area)) continue;
                JSONArray list = item.optJSONArray("data_series");
                if (list == null) continue;
                for (int k = 0; k < list.length(); k++) {
                    JSONObject series = list.optJSONObject(k);
                    if (series != null) drawSeries(canvas, series, start, end, left, bar, low, high, top, bottom);
                }
            }
            drawPanels(canvas, start, end, left, right, bar, bottom + dp(24));
        }

        private float y(double price, double min, double max, float top, float bottom) {
            return (float) (bottom - (price - min) / (max - min) * (bottom - top));
        }
    }
}
