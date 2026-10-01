package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Typeface;
import android.view.Gravity;
import android.view.View;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.math.RoundingMode;
import java.time.LocalTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.Map;

/** Read-only community journal of every robot in the selected VPS terminal. */
final class JournalPage {
    private static final String[] TABS = {"Эквити", "Открытые позиции", "Закрытые позиции"};
    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private final Runnable onTabChanged;
    private Map<String, Object> data = new HashMap<>();
    private String error;
    private String updatedAt = "";
    private String lastSnapshot;
    private boolean loaded;
    private int selected;
    private HorizontalScrollView tabsScroll;

    JournalPage(Activity activity, LinearLayout target, ScrollView outer, boolean tablet,
                Runnable onTabChanged) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
        this.onTabChanged = onTabChanged;
    }

    String selectedTool() {
        switch (selected) {
            case 0: return "bot_journal_get_equity";
            case 1: return "bot_journal_get_open_positions";
            default: return "bot_journal_get_closed_positions";
        }
    }

    void showData(Map<String, Object> next) {
        String snapshot = next.toString();
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        data.putAll(next);
        loaded = true;
        error = null;
        lastSnapshot = snapshot;
        updatedAt = LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
        if (!unchanged) render(true);
    }

    void showError(String message) {
        if (message.equals(error)) return;
        error = message;
        render(true);
    }

    void render(boolean preserveScroll) {
        int scrollY = preserveScroll ? outer.getScrollY() : 0;
        target.removeAllViews();
        TextView heading = text("Журнал общий", 19, R.color.text_primary);
        heading.setTypeface(null, Typeface.BOLD);
        target.addView(heading);
        if (error != null) target.addView(text(error + (loaded ? " · последний снимок " + updatedAt : ""),
            13, R.color.text_secondary));
        else if (loaded) target.addView(text("Обновлено " + updatedAt, 12, R.color.text_secondary));
        tabsScroll = new HorizontalScrollView(activity);
        tabsScroll.setHorizontalScrollBarEnabled(false);
        LinearLayout tabs = new LinearLayout(activity);
        tabsScroll.addView(tabs);
        target.addView(tabsScroll);
        for (int i = 0; i < TABS.length; i++) {
            int index = i;
            TextView tab = text(TABS[i], 13, selected == i ? R.color.orange : R.color.text_primary);
            tab.setGravity(Gravity.CENTER);
            tab.setPadding(dp(12), 0, dp(12), 0);
            tab.setBackgroundResource(R.drawable.input_background);
            tabs.addView(tab, new LinearLayout.LayoutParams(dp(tablet ? 150 : 145), dp(44)));
            tab.setOnClickListener(view -> {
                selected = index;
                render(false);
                onTabChanged.run();
            });
        }
        if (!loaded) {
            if (error == null) target.addView(text("Ожидание журнала…", 15, R.color.text_secondary));
            return;
        }
        switch (selected) {
            case 0: renderEquity(); break;
            case 1: renderPositions("bot_journal_get_open_positions"); break;
            default: renderPositions("bot_journal_get_closed_positions"); break;
        }
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
        if (tabsScroll != null) {
            View current = tabs.getChildAt(selected);
            tabsScroll.post(() -> tabsScroll.smoothScrollTo(current.getLeft(), 0));
        }
    }

    private void renderEquity() {
        JSONArray points = equityPoints();
        if (points.length() == 0) {
            target.addView(text("Данных для графика нет", 15, R.color.text_secondary));
            return;
        }
        target.addView(new Graph(activity, points,
            new String[]{"total", "long", "short"},
            new int[]{0xFFFFFFFF, 0xFF00BFFF, 0xFF915000}),
            new LinearLayout.LayoutParams(-1, dp(tablet ? 360 : 260)));
        LinearLayout legend = new LinearLayout(activity);
        legend.addView(text("● Общая прибыль   ", 12, 0xFFFFFFFF, true));
        legend.addView(text("● Лонг   ", 12, 0xFF00BFFF, true));
        legend.addView(text("● Шорт", 12, 0xFF915000, true));
        target.addView(legend);
        JSONObject summary = object("bot_journal_get_summary");
        if (summary != null) {
            pair("Общая прибыль", number(summary, "total_profit_abs"));
            pair("Прибыль %", number(summary, "total_profit_percent"));
        }
    }

    private JSONArray equityPoints() {
        JSONArray open = null, closed = null;
        JSONObject openResponse = object("bot_journal_get_open_positions");
        JSONObject closedResponse = object("bot_journal_get_closed_positions");
        if (openResponse != null) open = openResponse.optJSONArray("positions");
        if (closedResponse != null) closed = closedResponse.optJSONArray("positions");
        return buildEquityPoints(open, closed);
    }

    static JSONArray buildEquityPoints(JSONArray... lists) {
        ArrayList<JSONObject> positions = new ArrayList<>();
        for (JSONArray rows : lists) {
            if (rows == null) continue;
            for (int i = 0; i < rows.length(); i++) {
                JSONObject position = rows.optJSONObject(i);
                if (position != null && !"OpeningFail".equals(position.optString("state")))
                    positions.add(position);
            }
        }
        positions.sort(Comparator.comparing(p -> p.optString("time_create")));
        JSONArray points = new JSONArray();
        BigDecimal total = BigDecimal.ZERO, longs = BigDecimal.ZERO, shorts = BigDecimal.ZERO;
        for (JSONObject position : positions) {
            try {
                BigDecimal profit = new BigDecimal(position.optString("profit_abs", "0"));
                BigDecimal multiplier = new BigDecimal(position.optString("mult_to_journal", "100"));
                BigDecimal change = profit.multiply(multiplier)
                    .divide(new BigDecimal("100"), 8, RoundingMode.HALF_UP);
                total = total.add(change);
                if ("Buy".equals(position.optString("side"))) longs = longs.add(change);
                else if ("Sell".equals(position.optString("side"))) shorts = shorts.add(change);
                points.put(new JSONObject().put("total", total.doubleValue())
                    .put("long", longs.doubleValue()).put("short", shorts.doubleValue()));
            } catch (Exception ignored) { /* Malformed position is skipped, never guessed. */ }
        }
        return points;
    }

    private void renderPositions(String tool) {
        JSONObject result = object(tool);
        JSONArray positions = result == null ? null : result.optJSONArray("positions");
        if (positions == null || positions.length() == 0) {
            target.addView(text("Позиций нет", 15, R.color.text_secondary));
            return;
        }
        for (int i = 0; i < positions.length(); i++) {
            JSONObject position = positions.optJSONObject(i);
            if (position == null) continue;
            LinearLayout card = new LinearLayout(activity);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(12), dp(9), dp(12), dp(9));
            card.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            target.addView(card, params);
            TextView title = text("№ " + position.optString("number") + " · "
                + position.optString("security_name"), 15, R.color.text_primary);
            title.setTypeface(null, Typeface.BOLD);
            card.addView(title);
            card.addView(text(position.optString("bot_name") + " · "
                + position.optString("side") + " · " + position.optString("state"),
                13, R.color.text_secondary));
            card.addView(text("Объём: " + number(position, "volume") + "   П/У: "
                + number(position, "profit_abs"), 13, R.color.text_primary));
        }
    }

    private void pair(String label, String value) {
        LinearLayout row = new LinearLayout(activity);
        row.setPadding(dp(8), dp(8), dp(8), dp(8));
        row.setBackgroundResource(R.drawable.position_cell);
        row.addView(text(label, 13, R.color.text_secondary),
            new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(text(value, 13, R.color.text_primary),
            new LinearLayout.LayoutParams(0, -2, 1));
        target.addView(row);
    }

    private JSONObject object(String key) {
        Object value = data.get(key);
        return value instanceof JSONObject ? (JSONObject) value : null;
    }

    private static String number(JSONObject source, String key) {
        try { return new BigDecimal(source.optString(key, "0"))
            .setScale(6, RoundingMode.HALF_UP).stripTrailingZeros().toPlainString(); }
        catch (Exception ignored) { return source.optString(key, "—"); }
    }

    private TextView text(String value, int size, int colorValue, boolean rawColor) {
        TextView text = new TextView(activity);
        text.setText(value);
        text.setTextSize(size);
        text.setTextColor(rawColor ? colorValue : activity.getColor(colorValue));
        text.setGravity(Gravity.CENTER_VERTICAL);
        return text;
    }

    private TextView text(String value, int size, int color) {
        TextView text = new TextView(activity);
        text.setText(value);
        text.setTextSize(size);
        text.setTextColor(activity.getColor(color));
        text.setGravity(Gravity.CENTER_VERTICAL);
        return text;
    }

    private int dp(int value) {
        return Math.round(value * activity.getResources().getDisplayMetrics().density);
    }

    static final class Graph extends View {
        private final JSONArray points;
        private final String[] fields;
        private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final int[] colors;

        Graph(Activity activity, JSONArray points, String[] fields, int[] colors) {
            super(activity);
            this.points = points;
            this.fields = fields;
            this.colors = colors;
            setBackgroundResource(R.drawable.input_background);
        }

        @Override protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            double min = Double.POSITIVE_INFINITY, max = Double.NEGATIVE_INFINITY;
            for (int i = 0; i < points.length(); i++) {
                JSONObject point = points.optJSONObject(i);
                if (point == null) continue;
                for (String field : fields) {
                    double value = point.optDouble(field, 0);
                    min = Math.min(min, value); max = Math.max(max, value);
                }
            }
            if (!Double.isFinite(min)) return;
            if (max == min) { max += 1; min -= 1; }
            paint.setColor(0xFF384047);
            paint.setStrokeWidth(1);
            float left = 24, right = getWidth() - 24, top = 24, bottom = getHeight() - 24;
            canvas.drawLine(left, bottom, right, bottom, paint);
            for (int series = 0; series < fields.length; series++) {
                paint.setColor(colors[series]);
                paint.setStrokeWidth(series == 0 ? 3 : 2);
                float previousX = 0, previousY = 0;
                for (int i = 0; i < points.length(); i++) {
                    JSONObject point = points.optJSONObject(i);
                    if (point == null) continue;
                    float x = left + (right - left) * i / Math.max(1, points.length() - 1);
                    float y = (float) (bottom - (point.optDouble(fields[series], 0) - min)
                        / (max - min) * (bottom - top));
                    if (i > 0) canvas.drawLine(previousX, previousY, x, y, paint);
                    canvas.drawCircle(x, y, 3, paint);
                    previousX = x; previousY = y;
                }
            }
        }
    }
}
