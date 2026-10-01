package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Typeface;
import android.view.Gravity;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.OffsetDateTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.Locale;

/** Prime log for one selected VPS terminal. */
final class PrimeLogPage {
    private static final DateTimeFormatter TIME =
        DateTimeFormatter.ofPattern("dd.MM.yyyy HH:mm:ss", Locale.getDefault());
    private final Activity activity;
    private final LinearLayout target;
    private final ScrollView outer;
    private final boolean tablet;
    private final Runnable back;
    private JSONArray entries = new JSONArray();
    private boolean loaded;
    private String error;
    private String updatedAt = "";
    private String lastSnapshot;
    private TextView updatedLabel;

    PrimeLogPage(Activity activity, LinearLayout target, ScrollView outer,
                 boolean tablet, Runnable back) {
        this.activity = activity;
        this.target = target;
        this.outer = outer;
        this.tablet = tablet;
        this.back = back;
    }

    void showData(JSONArray next) {
        String snapshot = next.toString();
        boolean unchanged = loaded && error == null && snapshot.equals(lastSnapshot);
        entries = next;
        loaded = true;
        error = null;
        lastSnapshot = snapshot;
        updatedAt = java.time.LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"));
        if (unchanged && updatedLabel != null) updatedLabel.setText("Обновлено " + updatedAt);
        else render(true);
    }

    void showError(String message) {
        if (message.equals(error)) return;
        error = message;
        render(true);
    }

    void render(boolean preserveScroll) {
        int scrollY = preserveScroll ? outer.getScrollY() : 0;
        target.removeAllViews();
        updatedLabel = null;
        TextView title = text("Прайм лог", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        target.addView(title);
        if (error != null) target.addView(text(error
            + (loaded ? " · последний снимок " + updatedAt : ""), 13, R.color.text_secondary));
        else if (loaded) {
            updatedLabel = text("Обновлено " + updatedAt, 12, R.color.text_secondary);
            target.addView(updatedLabel);
        }
        if (!loaded) {
            if (error == null) target.addView(text("Ожидание прайм лога…", 15, R.color.text_secondary));
        } else if (entries.length() == 0) {
            target.addView(text("Записей нет", 15, R.color.text_secondary));
        } else if (tablet) renderTable();
        else renderCards();
        if (!tablet) {
            TextView backButton = text("‹ Ещё", 16, R.color.orange);
            backButton.setPadding(0, dp(14), 0, dp(14));
            backButton.setOnClickListener(view -> back.run());
            target.addView(backButton);
        }
        if (preserveScroll) outer.post(() -> outer.scrollTo(0, scrollY));
    }

    private void renderTable() {
        LinearLayout heading = new LinearLayout(activity);
        heading.addView(cell("Время", true), new LinearLayout.LayoutParams(dp(170), -2));
        heading.addView(cell("Тип", true), new LinearLayout.LayoutParams(dp(90), -2));
        heading.addView(cell("Сообщение", true), new LinearLayout.LayoutParams(0, -2, 1));
        target.addView(heading);
        for (int i = 0; i < entries.length(); i++) {
            JSONObject entry = entries.optJSONObject(i);
            if (entry == null) continue;
            LinearLayout row = new LinearLayout(activity);
            row.addView(cell(time(entry), false), new LinearLayout.LayoutParams(dp(170), -2));
            row.addView(cell(entry.optString("type"), false), new LinearLayout.LayoutParams(dp(90), -2));
            row.addView(cell(entry.optString("message"), false), new LinearLayout.LayoutParams(0, -2, 1));
            target.addView(row);
        }
    }

    private void renderCards() {
        for (int i = 0; i < entries.length(); i++) {
            JSONObject entry = entries.optJSONObject(i);
            if (entry == null) continue;
            LinearLayout card = new LinearLayout(activity);
            card.setOrientation(LinearLayout.VERTICAL);
            card.setPadding(dp(12), dp(9), dp(12), dp(9));
            card.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
            params.bottomMargin = dp(8);
            target.addView(card, params);
            card.addView(text(time(entry) + " · " + entry.optString("type"),
                13, R.color.text_secondary));
            card.addView(text(entry.optString("message"), 15, R.color.text_primary));
        }
    }

    private TextView cell(String value, boolean heading) {
        TextView view = text(value, 12, heading ? R.color.text_secondary : R.color.text_primary);
        view.setPadding(dp(7), dp(9), dp(7), dp(9));
        view.setBackgroundResource(R.drawable.position_cell);
        view.setMinHeight(dp(42));
        return view;
    }

    private static String time(JSONObject entry) {
        String raw = entry.optString("time");
        if (raw.isEmpty()) return "";
        try { return OffsetDateTime.parse(raw).atZoneSameInstant(ZoneId.systemDefault()).format(TIME); }
        catch (Exception ignored) { return raw; }
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
