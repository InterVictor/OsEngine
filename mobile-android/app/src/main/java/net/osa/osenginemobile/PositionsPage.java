package net.osa.osenginemobile;

import android.app.Activity;
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
import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

/** VPS position tables, in the same field order as DataGridFactory. */
final class PositionsPage {
    interface RowAction { void select(int section, JSONObject row); }
    private static final String[] TABS = {
        "Активные позиции", "Стоп Лимит", "Завершённые позиции"
    };
    private static final String[] POSITION_COLUMNS = {
        "Номер", "Время отк", "Время зак.", "Бот", "Инструмент", "Напр.",
        "Состояние", "Объём", "Текущий", "Ожидает", "Цена входа", "Цена выхода",
        "Прибыль", "Стоп Активация", "Стоп Цена", "Профит Активация",
        "Профит цена", "Тип Сигнала на Открытие", "Тип Сигнала на Закрытие"
    };
    private static final String[] STOP_COLUMNS = {
        "Номер", "Время создания", "Имя вкладки", "Бумага", "Объём", "Сторона",
        "Тип активации", "Цена активации", "Цена ордера", "Время жизни свечей",
        "Тип жизни заявки"
    };
    private static final DateTimeFormatter TIME_FORMAT =
        DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm:ss", Locale.getDefault());

    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private final RowAction rowAction;
    private JSONArray active = new JSONArray();
    private JSONArray stops = new JSONArray();
    private JSONArray closed = new JSONArray();
    private int tab;
    private boolean loaded;
    private String error;
    private String updatedAt = "";
    private String lastSnapshot;
    private TextView updatedLabel;
    private HorizontalScrollView tableScroll;
    private HorizontalScrollView tabScroll;

    PositionsPage(Activity activity, LinearLayout target, ScrollView outer, boolean tablet,
                  RowAction rowAction) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
        this.rowAction = rowAction;
    }

    void showData(JSONArray open, JSONArray stop, JSONArray done) {
        String snapshot = open.toString() + stop.toString() + done.toString();
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        active = open;
        stops = stop;
        closed = done;
        loaded = true;
        error = null;
        lastSnapshot = snapshot;
        updatedAt = java.time.LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
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

    boolean canAct() {
        return loaded && error == null;
    }

    void render(boolean preserveScroll) {
        int scrollY = preserveScroll ? outer.getScrollY() : 0;
        int horizontalX = preserveScroll && tableScroll != null ? tableScroll.getScrollX() : 0;
        int tabX = preserveScroll && tabScroll != null ? tabScroll.getScrollX() : -1;
        target.removeAllViews();
        tableScroll = null;
        tabScroll = null;
        updatedLabel = null;
        TextView heading = text("Позиции", 19, R.color.text_primary);
        heading.setTypeface(null, Typeface.BOLD);
        target.addView(heading);

        HorizontalScrollView tabs = new HorizontalScrollView(activity);
        tabs.setHorizontalScrollBarEnabled(false);
        LinearLayout tabRow = new LinearLayout(activity);
        tabRow.setOrientation(LinearLayout.HORIZONTAL);
        tabs.addView(tabRow);
        LinearLayout.LayoutParams tabParams = new LinearLayout.LayoutParams(-1, dp(52));
        tabParams.topMargin = dp(10);
        target.addView(tabs, tabParams);
        tabScroll = tabs;
        TextView selectedButton = null;
        for (int i = 0; i < TABS.length; i++) {
            final int selected = i;
            TextView button = text(TABS[i], 13,
                i == tab ? R.color.orange : R.color.text_primary);
            button.setGravity(Gravity.CENTER);
            button.setPadding(dp(12), 0, dp(12), 0);
            button.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, dp(42));
            params.rightMargin = dp(4);
            tabRow.addView(button, params);
            if (i == tab) selectedButton = button;
            button.setOnClickListener(view -> {
                tab = selected;
                outer.scrollTo(0, 0);
                render(false);
            });
        }
        TextView currentButton = selectedButton;
        tabs.post(() -> {
            if (tabScroll != tabs) return;
            tabs.scrollTo(tabX >= 0 ? tabX : currentButton.getLeft(), 0);
        });

        if (error != null) {
            TextView state = text(error + (loaded ? " · последний снимок " + updatedAt : ""),
                13, R.color.text_secondary);
            target.addView(state);
        } else if (loaded) {
            updatedLabel = text("Обновлено " + updatedAt, 12, R.color.text_secondary);
            target.addView(updatedLabel);
        }

        if (!loaded) {
            if (error == null)
                target.addView(text("Ожидание данных позиций…", 15, R.color.text_secondary));
            return;
        }
        JSONArray rows = tab == 0 ? active : tab == 1 ? stops : closed;
        if (rows.length() == 0) {
            target.addView(text("Позиций нет", 15, R.color.text_secondary));
            return;
        }
        if (tablet) renderTable(rows);
        else renderCards(rows);
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
        if (preserveScroll && tableScroll != null) {
            HorizontalScrollView current = tableScroll;
            current.post(() -> current.scrollTo(horizontalX, 0));
        }
    }

    private void renderTable(JSONArray rows) {
        HorizontalScrollView horizontal = new HorizontalScrollView(activity);
        horizontal.setFillViewport(true);
        LinearLayout table = new LinearLayout(activity);
        table.setOrientation(LinearLayout.VERTICAL);
        horizontal.addView(table);
        target.addView(horizontal);
        String[] headers = tab == 1 ? STOP_COLUMNS : POSITION_COLUMNS;
        LinearLayout header = new LinearLayout(activity);
        for (String name : headers) header.addView(cell(name, true, false, columnWidth(name)));
        table.addView(header);
        for (int index = 0; index < rows.length(); index++) {
            JSONObject value = rows.optJSONObject(index);
            if (value == null) continue;
            String[] fields = tab == 1 ? stopValues(value) : positionValues(value, tab == 2);
            LinearLayout row = new LinearLayout(activity);
            for (int i = 0; i < fields.length; i++)
                row.addView(cell(fields[i], false, tab != 1 && i < 5,
                    columnWidth(headers[i])));
            final int section = tab;
            row.setOnClickListener(view -> rowAction.select(section, value));
            table.addView(row);
        }
        tableScroll = horizontal;
    }

    private void renderCards(JSONArray rows) {
        String[] headers = tab == 1 ? STOP_COLUMNS : POSITION_COLUMNS;
        for (int index = 0; index < rows.length(); index++) {
            JSONObject value = rows.optJSONObject(index);
            if (value == null) continue;
            String[] fields = tab == 1 ? stopValues(value) : positionValues(value, tab == 2);
            LinearLayout card = new LinearLayout(activity);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(12), dp(8), dp(12), dp(8));
            card.setBackgroundResource(R.drawable.input_background);
            final int section = tab;
            card.setOnClickListener(view -> rowAction.select(section, value));
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            target.addView(card, params);
            TextView number = text("Номер  " + fields[0], 16, R.color.text_primary);
            number.setTypeface(null, Typeface.BOLD);
            card.addView(number);
            for (int i = 1; i < headers.length; i++) {
                LinearLayout line = new LinearLayout(activity);
                TextView name = text(headers[i], 12, R.color.text_secondary);
                TextView detail = text(fields[i].isEmpty() ? "—" : fields[i], 13,
                    R.color.text_primary);
                line.addView(name, new LinearLayout.LayoutParams(0, -2, 1));
                line.addView(detail, new LinearLayout.LayoutParams(0, -2, 1));
                LinearLayout.LayoutParams lineParams = new LinearLayout.LayoutParams(-1, -2);
                lineParams.topMargin = dp(5);
                card.addView(line, lineParams);
            }
        }
    }

    private static int columnWidth(String title) {
        return title.length() > 20 ? 190 : title.length() > 13 ? 155 : 120;
    }

    private TextView cell(String value, boolean header, boolean framed, int width) {
        TextView view = text(value, 12, header ? R.color.text_secondary : R.color.text_primary);
        view.setPadding(dp(7), dp(9), dp(7), dp(9));
        view.setBackgroundResource(framed ? R.drawable.input_background : R.drawable.position_cell);
        view.setSingleLine(false);
        view.setGravity(Gravity.CENTER_VERTICAL);
        view.setLayoutParams(new LinearLayout.LayoutParams(dp(width), -2));
        view.setMinHeight(dp(42));
        return view;
    }

    private static String[] positionValues(JSONObject item, boolean finished) {
        String side = string(item, "side");
        if (side.isEmpty()) side = string(item, "direction");
        return new String[]{
            string(item, "number"), time(item, "time_create"),
            finished ? time(item, "close_time") : "", string(item, "bot_name"),
            string(item, "security_name"), side, string(item, "state"),
            number(item, "volume"), number(item, "open_volume"), number(item, "wait_volume"),
            price(item, "entry_price"), price(item, "close_price"), price(item, "profit_abs"),
            price(item, "stop_order_red_line"), price(item, "stop_order_price"),
            price(item, "profit_order_red_line"), price(item, "profit_order_price"),
            string(item, "signal_type_open"), string(item, "signal_type_close")
        };
    }

    private static String[] stopValues(JSONObject item) {
        return new String[]{
            string(item, "number"), time(item, "time_create"), string(item, "tab_name"),
            string(item, "security_name"), number(item, "volume"), string(item, "side"),
            string(item, "activate_type"), number(item, "price_red_line"),
            number(item, "price_order"), string(item, "expires_bars"),
            string(item, "lifetime_type")
        };
    }

    private static String string(JSONObject item, String key) {
        return item.isNull(key) ? "" : item.optString(key, "");
    }

    private static String number(JSONObject item, String key) {
        String raw = string(item, key);
        if (raw.isEmpty()) return "";
        try { return new BigDecimal(raw).stripTrailingZeros().toPlainString(); }
        catch (NumberFormatException ignored) { return raw; }
    }

    private static String price(JSONObject item, String key) {
        try {
            BigDecimal value = new BigDecimal(string(item, key));
            BigDecimal step = new BigDecimal(string(item, "price_step"));
            if (step.signum() > 0)
                value = value.setScale(Math.max(0, step.stripTrailingZeros().scale()) + 1,
                    RoundingMode.HALF_EVEN);
            return value.stripTrailingZeros().toPlainString();
        } catch (Exception ignored) { return number(item, key); }
    }

    private static String time(JSONObject item, String key) {
        String raw = string(item, key);
        if (raw.isEmpty()) return "";
        try {
            return OffsetDateTime.parse(raw).atZoneSameInstant(ZoneId.systemDefault())
                .format(TIME_FORMAT);
        } catch (Exception ignored) { return raw; }
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
