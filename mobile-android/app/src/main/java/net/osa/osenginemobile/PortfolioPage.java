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
import java.time.LocalTime;
import java.time.format.DateTimeFormatter;

/** The VPS portfolio table: summary rows followed by nonzero instrument rows. */
final class PortfolioPage {
    private static final String[] HEADERS = {"Сервер", "Портфель", "Средства входящие",
        "Средства сейчас", "Средства блок.", "Незакрытая прибыль", "Инструмент",
        "Объём входящий", "Объём сейчас", "Объём блок.", "Незакрытая прибыль"};
    private static final int[] WIDTHS = {150, 150, 145, 145, 130, 155, 155,
        140, 140, 125, 155};
    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private final java.util.function.Consumer<JSONObject> openCompare;
    private final java.util.function.BiConsumer<JSONObject, JSONObject> closeOnBoard;
    private JSONArray portfolios = new JSONArray();
    private boolean loaded;
    private String error;
    private String status;
    private String updatedAt = "";
    private String lastSnapshot;
    private TextView updatedLabel;
    private HorizontalScrollView tableScroll;

    PortfolioPage(Activity activity, LinearLayout target, ScrollView outer, boolean tablet,
                  java.util.function.Consumer<JSONObject> openCompare,
                  java.util.function.BiConsumer<JSONObject, JSONObject> closeOnBoard) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
        this.openCompare = openCompare;
        this.closeOnBoard = closeOnBoard;
    }

    void showData(JSONArray rows, String message) {
        String snapshot = rows.toString() + message;
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        portfolios = rows;
        loaded = true;
        error = null;
        status = message;
        lastSnapshot = snapshot;
        updatedAt = LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
        if (unchanged && updatedLabel != null) {
            updatedLabel.setText("Обновлено " + updatedAt);
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
        TextView title = text("Портфель", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        target.addView(title);
        if (error != null) target.addView(text(error + (loaded ? " · последний снимок " + updatedAt : ""),
            13, R.color.text_secondary));
        else if (loaded) {
            updatedLabel = text("Обновлено " + updatedAt, 12, R.color.text_secondary);
            target.addView(updatedLabel);
            if (status != null && portfolios.length() > 0)
                target.addView(text(status, 13, R.color.text_secondary));
        }
        if (!loaded) {
            if (error == null) target.addView(text("Ожидание портфелей…", 15, R.color.text_secondary));
            return;
        }
        int visible = 0;
        for (int i = 0; i < portfolios.length(); i++) {
            JSONObject portfolio = portfolios.optJSONObject(i);
            if (portfolio != null && visible(portfolio)) visible++;
        }
        if (visible == 0) target.addView(text(status == null ? "Портфелей нет" : status,
            15, R.color.text_secondary));
        else if (tablet) renderTable();
        else renderCards();
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
        if (preserveScroll && tableScroll != null) {
            HorizontalScrollView current = tableScroll;
            current.post(() -> current.scrollTo(horizontalX, 0));
        }
    }

    private void renderTable() {
        HorizontalScrollView horizontal = new HorizontalScrollView(activity);
        LinearLayout table = new LinearLayout(activity);
        table.setOrientation(LinearLayout.VERTICAL);
        horizontal.addView(table);
        target.addView(horizontal);
        LinearLayout heading = new LinearLayout(activity);
        for (int i = 0; i < HEADERS.length; i++) heading.addView(cell(HEADERS[i], true, WIDTHS[i]));
        heading.addView(cell("", true, 145));
        table.addView(heading);
        for (int i = 0; i < portfolios.length(); i++) {
            JSONObject portfolio = portfolios.optJSONObject(i);
            if (portfolio == null || !visible(portfolio)) continue;
            String[] summary = {portfolio.optString("serverUniqueName"), portfolio.optString("number"),
                number(portfolio, "valueBegin"), number(portfolio, "valueCurrent"),
                number(portfolio, "valueBlocked"), number(portfolio, "unrealizedPnl"),
                "", "", "", "", ""};
            addRow(table, summary, portfolio, null, null);
            JSONArray positions = portfolio.optJSONArray("positions");
            int count = 0;
            if (positions != null) for (int j = 0; j < positions.length(); j++) {
                JSONObject position = positions.optJSONObject(j);
                if (position == null || !nonZero(position)) continue;
                String[] values = {"", "", "", "", "", "",
                    position.optString("securityNameCode"), number(position, "valueBegin"),
                    number(position, "valueCurrent"), number(position, "valueBlocked"),
                    number(position, "unrealizedPnl")};
                addRow(table, values, null, portfolio, position);
                count++;
            }
            if (count == 0) {
                String[] empty = new String[HEADERS.length];
                java.util.Arrays.fill(empty, "");
                empty[6] = "No positions";
                addRow(table, empty, null, null, null);
            }
        }
        tableScroll = horizontal;
    }

    private void addRow(LinearLayout table, String[] values, JSONObject portfolio,
                        JSONObject owner, JSONObject position) {
        LinearLayout row = new LinearLayout(activity);
        for (int i = 0; i < HEADERS.length; i++) row.addView(cell(values[i], false, WIDTHS[i]));
        TextView action = cell(portfolio != null ? "Сравнить позиции" : position != null ? "Закрыть" : "",
            false, 145);
        if (portfolio != null) action.setOnClickListener(view -> openCompare.accept(portfolio));
        else if (position != null) {
            action.setTextColor(activity.getColor(R.color.orange));
            action.setOnClickListener(view -> closeOnBoard.accept(owner, position));
        }
        row.addView(action);
        table.addView(row);
    }

    private void renderCards() {
        for (int i = 0; i < portfolios.length(); i++) {
            JSONObject portfolio = portfolios.optJSONObject(i);
            if (portfolio == null || !visible(portfolio)) continue;
            LinearLayout card = card(target);
            TextView heading = text(portfolio.optString("serverUniqueName") + " · "
                + portfolio.optString("number"), 16, R.color.text_primary);
            heading.setTypeface(null, Typeface.BOLD);
            card.addView(heading);
            TextView compare = text("Сравнить позиции", 13, R.color.orange);
            compare.setPadding(0, dp(8), 0, dp(8));
            compare.setOnClickListener(view -> openCompare.accept(portfolio));
            card.addView(compare);
            for (int j = 2; j <= 5; j++)
                card.addView(line(HEADERS[j], number(portfolio,
                    new String[]{"valueBegin", "valueCurrent", "valueBlocked", "unrealizedPnl"}[j - 2])));
            JSONArray positions = portfolio.optJSONArray("positions");
            int count = 0;
            if (positions != null) for (int j = 0; j < positions.length(); j++) {
                JSONObject position = positions.optJSONObject(j);
                if (position == null || !nonZero(position)) continue;
                LinearLayout instrument = card(target);
                TextView name = text(position.optString("securityNameCode"), 15, R.color.text_primary);
                name.setTypeface(null, Typeface.BOLD);
                instrument.addView(name);
                TextView close = text("Закрыть", 13, R.color.orange);
                close.setPadding(0, dp(8), 0, dp(8));
                close.setOnClickListener(view -> closeOnBoard.accept(portfolio, position));
                instrument.addView(close);
                for (int k = 7; k <= 10; k++)
                    instrument.addView(line(HEADERS[k], number(position,
                        new String[]{"valueBegin", "valueCurrent", "valueBlocked", "unrealizedPnl"}[k - 7])));
                count++;
            }
            if (count == 0) card.addView(text("No positions", 13, R.color.text_secondary));
        }
    }

    private LinearLayout card(LinearLayout parent) {
        LinearLayout card = new LinearLayout(activity);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(12), dp(9), dp(12), dp(9));
        card.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(8);
        parent.addView(card, params);
        return card;
    }

    private LinearLayout line(String label, String value) {
        LinearLayout line = new LinearLayout(activity);
        line.addView(text(label, 12, R.color.text_secondary),
            new LinearLayout.LayoutParams(0, -2, 1));
        line.addView(text(value, 13, R.color.text_primary),
            new LinearLayout.LayoutParams(0, -2, 1));
        return line;
    }

    private static boolean nonZero(JSONObject row) {
        return nonZero(row, "valueBegin") || nonZero(row, "valueCurrent")
            || nonZero(row, "valueBlocked");
    }

    private static boolean visible(JSONObject portfolio) {
        if ("FinamVirtual".equals(portfolio.optString("number"))) return false;
        if (nonZero(portfolio)) return true;
        JSONArray positions = portfolio.optJSONArray("positions");
        if (positions != null) for (int i = 0; i < positions.length(); i++) {
            JSONObject position = positions.optJSONObject(i);
            if (position != null && nonZero(position, "valueCurrent")) return true;
        }
        return false;
    }

    private static boolean nonZero(JSONObject row, String key) {
        try { return new BigDecimal(row.optString(key, "0")).signum() != 0; }
        catch (Exception ignored) { return false; }
    }

    private static String number(JSONObject row, String key) {
        String raw = row.optString(key, "0");
        try { return new BigDecimal(raw).stripTrailingZeros().toPlainString(); }
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
