package net.osa.osenginemobile;

import android.app.Activity;
import android.graphics.Typeface;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.util.List;

/** «Окно оповещений»: Время / Бот / Сообщение, newest first (AlertMessageFullUi). */
public final class AlertsActivity extends Activity implements AlertCenter.Listener {
    private LinearLayout rows;
    private TextView stateView;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(getColor(R.color.panel));
        root.setPadding(dp(14), dp(12), dp(14), dp(12));
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
            view.setPadding(dp(14), bars.top + dp(12), dp(14), bars.bottom + dp(12));
            return insets;
        });
        setContentView(root);
        root.requestApplyInsets();
        TextView title = text("Окно оповещений", 19, R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        root.addView(title);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(2));
        stripParams.bottomMargin = dp(6);
        root.addView(strip, stripParams);
        stateView = text("", 11, R.color.text_secondary);
        root.addView(stateView);
        root.addView(header());
        ScrollView scroll = new ScrollView(this);
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        rows = new LinearLayout(this);
        rows.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(rows);
        TextView close = text("Закрыть", 14, R.color.orange);
        close.setGravity(Gravity.CENTER);
        close.setBackgroundResource(R.drawable.input_background);
        close.setOnClickListener(view -> finish());
        root.addView(close, new LinearLayout.LayoutParams(-1, dp(44)));
    }

    @Override protected void onResume() {
        super.onResume();
        AlertCenter.addListener(this);
        render();
    }

    @Override protected void onPause() {
        AlertCenter.removeListener(this);
        super.onPause();
    }

    @Override public void onAlertsChanged(boolean added) { runOnUiThread(this::render); }

    private void render() {
        stateView.setText("Поток VPS: " + AlertCenter.streamStates());
        rows.removeAllViews();
        List<AlertCenter.Alert> list = AlertCenter.snapshot();
        if (list.isEmpty()) {
            rows.addView(text("Оповещений пока нет. Показываются события, полученные при открытом "
                + "приложении и активном соединении.", 13, R.color.text_secondary));
            return;
        }
        for (AlertCenter.Alert alert : list) {
            LinearLayout row = new LinearLayout(this);
            row.addView(cell(alert.time + " UTC", false), new LinearLayout.LayoutParams(0, -2, 2));
            row.addView(cell(alert.bot, false), new LinearLayout.LayoutParams(0, -2, 2));
            row.addView(cell(alert.message, false), new LinearLayout.LayoutParams(0, -2, 5));
            rows.addView(row);
        }
    }

    private View header() {
        LinearLayout row = new LinearLayout(this);
        row.addView(cell("Время", true), new LinearLayout.LayoutParams(0, dp(40), 2));
        row.addView(cell("Бот", true), new LinearLayout.LayoutParams(0, dp(40), 2));
        row.addView(cell("Сообщение", true), new LinearLayout.LayoutParams(0, dp(40), 5));
        return row;
    }

    private TextView cell(String value, boolean heading) {
        TextView view = text(value, 12, heading ? R.color.text_secondary : R.color.text_primary);
        view.setBackgroundResource(R.drawable.position_cell);
        view.setPadding(dp(5), dp(7), dp(5), dp(7));
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
