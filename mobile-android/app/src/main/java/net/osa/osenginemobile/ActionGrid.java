package net.osa.osenginemobile;

import android.app.Activity;
import android.content.Intent;
import android.view.Gravity;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.function.Supplier;

/** Fixed grid (two buttons per row, nothing slides sideways) of robot-level windows. */
final class ActionGrid {
    static final String RISK = BotSettingsActivity.MODE_RISK;
    static final String SUPPORT = BotSettingsActivity.MODE_SUPPORT;
    static final String DATA = "data";
    static final String TRADE = "open";

    private ActionGrid() { }

    static void add(Activity activity, LinearLayout parent, String[][] items, String terminal,
                    String botId, String botName, Supplier<String> tabName) {
        float density = activity.getResources().getDisplayMetrics().density;
        for (int start = 0; start < items.length; start += 2) {
            LinearLayout row = new LinearLayout(activity);
            int count = Math.min(2, items.length - start);
            for (int column = 0; column < count; column++) {
                String[] item = items[start + column];
                TextView button = new TextView(activity);
                button.setText(item[0]);
                button.setTextSize(12);
                button.setTextColor(activity.getColor(R.color.orange));
                button.setGravity(Gravity.CENTER);
                button.setBackgroundResource(R.drawable.input_background);
                LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, Math.round(44 * density), 1);
                if (column == 1) params.leftMargin = Math.round(6 * density);
                row.addView(button, params);
                button.setOnClickListener(view -> {
                    String tab = tabName.get();
                    if ((tab == null || tab.isEmpty()) && !RISK.equals(item[1])) return;
                    Intent intent = new Intent(activity,
                        TRADE.equals(item[1]) ? PositionActionActivity.class
                        : DATA.equals(item[1]) ? DataSettingsActivity.class : BotSettingsActivity.class);
                    intent.putExtra("mode", item[1]);
                    intent.putExtra("terminal_name", terminal);
                    intent.putExtra("bot_id", botId);
                    intent.putExtra("bot_name", botName);
                    intent.putExtra("tab_name", tab);
                    activity.startActivity(intent);
                });
            }
            LinearLayout.LayoutParams rowParams = new LinearLayout.LayoutParams(-1, -2);
            rowParams.topMargin = Math.round(6 * density);
            parent.addView(row, rowParams);
        }
    }
}
