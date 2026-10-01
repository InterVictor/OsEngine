package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Typeface;
import android.view.Gravity;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

/** The read-only Bot Station orders tables, using EntityLocal.OrderColumn1..12. */
final class OrdersPage {
    private static final String[] TABS = {"Активные ордера", "Завершённые ордера"};
    private static final String[] HEADERS = {"ID", "ID на бирже", "Время выст.",
        "Инструмент", "Портфель", "Напр.", "Статус", "Цена", "Исполнение",
        "Объём", "Тип", "RoundTrip"};
    private static final DateTimeFormatter TIME =
        DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm:ss", Locale.getDefault());
    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private JSONArray active = new JSONArray();
    private JSONArray historical = new JSONArray();
    private boolean loaded;
    private int tab;
    private String error;
    private String updatedAt = "";
    private String serverName = "";
    private String lastSnapshot;
    private TextView updatedLabel;
    private HorizontalScrollView tableScroll;

    OrdersPage(Activity activity, LinearLayout target, ScrollView outer, boolean tablet) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
    }

    void showData(JSONArray open, JSONArray done, String server) {
        String snapshot = open.toString() + done.toString() + server;
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        active = open;
        historical = done;
        serverName = server;
        loaded = true;
        error = null;
        lastSnapshot = snapshot;
        updatedAt = java.time.LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
        if (unchanged && updatedLabel != null) {
            updatedLabel.setText(serverName + " · обновлено " + updatedAt);
            return;
        }
        render(true);
    }

    void showError(String message) {
        if (message.equals(error)) return;
        error = message;
        render(true);
    }

    void render(boolean preserveScroll) {
        int scrollY = preserveScroll ? outer.getScrollY() : 0;
        int horizontalX = preserveScroll && tableScroll != null ? tableScroll.getScrollX() : 0;
        target.removeAllViews();
        tableScroll = null;
        updatedLabel = null;
        TextView title = text("Ордера", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        target.addView(title);
        LinearLayout tabs = new LinearLayout(activity);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        LinearLayout.LayoutParams tabsParams = new LinearLayout.LayoutParams(-1, dp(52));
        tabsParams.topMargin = dp(10);
        target.addView(tabs, tabsParams);
        for (int i = 0; i < TABS.length; i++) {
            final int selected = i;
            TextView item = text(TABS[i], 13, i == tab ? R.color.orange : R.color.text_primary);
            item.setGravity(Gravity.CENTER);
            item.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(42), 1);
            params.rightMargin = dp(4);
            tabs.addView(item, params);
            item.setOnClickListener(view -> { tab = selected; render(false); });
        }
        if (error != null)
            target.addView(text(error + (loaded ? " · последний снимок " + updatedAt : ""),
                13, R.color.text_secondary));
        else if (loaded) {
            updatedLabel = text(serverName + " · обновлено " + updatedAt,
                12, R.color.text_secondary);
            target.addView(updatedLabel);
        }
        if (!loaded) {
            if (error == null) target.addView(text("Ожидание ордеров…", 15, R.color.text_secondary));
            return;
        }
        JSONArray rows = tab == 0 ? active : historical;
        if (rows.length() == 0) target.addView(text("Ордеров нет", 15, R.color.text_secondary));
        else if (tablet) renderTable(rows);
        else renderCards(rows);
        LinearLayout page = new LinearLayout(activity);
        page.setGravity(Gravity.CENTER_VERTICAL);
        page.setPadding(0, dp(10), 0, dp(10));
        page.addView(text("Страница 1", 12, R.color.text_secondary));
        page.addView(text("   ‹   ›", 12, R.color.text_secondary));
        page.addView(text("   Количество на странице: 100", 12, R.color.text_secondary));
        target.addView(page);
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
        if (preserveScroll && tableScroll != null) {
            HorizontalScrollView current = tableScroll;
            current.post(() -> current.scrollTo(horizontalX, 0));
        }
    }

    private void renderTable(JSONArray rows) {
        HorizontalScrollView horizontal = new HorizontalScrollView(activity);
        LinearLayout table = new LinearLayout(activity);
        table.setOrientation(LinearLayout.VERTICAL);
        horizontal.addView(table);
        target.addView(horizontal);
        LinearLayout heading = new LinearLayout(activity);
        for (String name : HEADERS) heading.addView(cell(name, true, width(name)));
        table.addView(heading);
        for (int i = 0; i < rows.length(); i++) {
            JSONObject order = rows.optJSONObject(i);
            if (order == null) continue;
            String[] values = values(order);
            LinearLayout row = new LinearLayout(activity);
            for (int j = 0; j < values.length; j++)
                row.addView(cell(values[j], false, width(HEADERS[j])));
            table.addView(row);
        }
        tableScroll = horizontal;
    }

    private void renderCards(JSONArray rows) {
        for (int i = 0; i < rows.length(); i++) {
            JSONObject order = rows.optJSONObject(i);
            if (order == null) continue;
            String[] values = values(order);
            LinearLayout card = new LinearLayout(activity);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(12), dp(9), dp(12), dp(9));
            card.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            target.addView(card, params);
            TextView id = text("ID " + values[0] + " · " + values[6], 16, R.color.text_primary);
            id.setTypeface(null, Typeface.BOLD);
            card.addView(id);
            card.addView(text("ID на бирже: " + (values[1].isEmpty() ? "—" : values[1]),
                13, R.color.text_secondary));
            for (int j = 2; j < values.length; j++) {
                if (j == 6) continue;
                LinearLayout line = new LinearLayout(activity);
                line.addView(text(HEADERS[j], 12, R.color.text_secondary),
                    new LinearLayout.LayoutParams(0, -2, 1));
                line.addView(text(values[j].isEmpty() ? "—" : values[j], 13,
                    R.color.text_primary), new LinearLayout.LayoutParams(0, -2, 1));
                card.addView(line);
            }
        }
    }

    private static String[] values(JSONObject item) {
        return new String[]{string(item, "number_user"), string(item, "number_market"),
            time(item, "time_create"), string(item, "security"), string(item, "portfolio"),
            string(item, "side"), string(item, "state"), number(item, "price"),
            number(item, "price_real"), number(item, "volume"), string(item, "type"),
            string(item, "round_trip")};
    }

    private static String string(JSONObject item, String key) {
        return item.isNull(key) ? "" : item.optString(key, "");
    }

    private static String number(JSONObject item, String key) {
        String raw = string(item, key);
        if (raw.isEmpty()) return "";
        try { return new BigDecimal(raw).stripTrailingZeros().toPlainString(); }
        catch (Exception ignored) { return raw; }
    }

    private static String time(JSONObject item, String key) {
        String raw = string(item, key);
        if (raw.isEmpty()) return "";
        try { return OffsetDateTime.parse(raw).atZoneSameInstant(ZoneId.systemDefault()).format(TIME); }
        catch (Exception ignored) { return raw; }
    }

    private TextView cell(String value, boolean heading, int width) {
        TextView cell = text(value, 12, heading ? R.color.text_secondary : R.color.text_primary);
        cell.setPadding(dp(7), dp(9), dp(7), dp(9));
        cell.setBackgroundResource(R.drawable.position_cell);
        cell.setMinHeight(dp(42));
        cell.setLayoutParams(new LinearLayout.LayoutParams(dp(width), -2));
        return cell;
    }

    private static int width(String label) {
        return label.length() > 12 ? 160 : label.length() > 8 ? 135 : 110;
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(activity);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(activity.getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL);
        return view;
    }

    private int dp(int value) {
        return Math.round(value * activity.getResources().getDisplayMetrics().density);
    }
}
