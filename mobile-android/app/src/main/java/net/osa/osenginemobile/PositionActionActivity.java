package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.DatePickerDialog;
import android.app.TimePickerDialog;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.WindowInsets;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.HorizontalScrollView;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.ArrayAdapter;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONArray;
import org.json.JSONObject;

import java.math.BigDecimal;
import java.time.LocalDate;
import java.time.LocalTime;
import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Mobile layout of PositionAddingUi2 / PositionCloseUi2 using the existing VPS MCP API. */
public final class PositionActionActivity extends Activity {
    private static final String[] ADD_TABS = {"Лимит", "Маркет", "Стоп-Лимит", "Стоп-Маркет", "Фэйк"};
    private static final String[] CLOSE_TABS = {"Лимит", "Маркет", "Стоп-Лимит", "Стоп-Маркет", "Профит", "Фэйк"};
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable polling = this::refresh;
    private final Map<Integer, Map<String, String>> savedFields = new HashMap<>();
    private final Map<String, EditText> fields = new HashMap<>();
    private McpBridge bridge;
    private String terminal;
    private String botId;
    private String tabName;
    private String security;
    private String side = "";
    private String state = "";
    private String server = "";
    private String volume = "";
    private int number;
    private int tab;
    private boolean adding;
    private boolean tablet;
    private boolean visible;
    private boolean loading;
    private boolean busy;
    private boolean positionFound;
    private boolean serverStopSupported;
    private boolean serverStopOn;
    private TextView title;
    private JSONObject depth;
    private String depthMode = "";
    private LinearLayout info;
    private LinearLayout depthContent;
    private LinearLayout tabs;
    private HorizontalScrollView tabScroll;
    private ScrollView depthScroll;
    private LinearLayout form;
    private LinearLayout actions;
    private TextView status;
    private CheckBox serverStopBox;

    @Override protected void onCreate(Bundle savedState) {
        super.onCreate(savedState);
        terminal = getIntent().getStringExtra("terminal_name");
        botId = getIntent().getStringExtra("bot_id");
        security = getIntent().getStringExtra("security_name");
        number = getIntent().getIntExtra("position_number", -1);
        adding = "add".equals(getIntent().getStringExtra("mode"));
        tab = getIntent().getIntExtra("initial_tab", 0);
        tablet = getResources().getConfiguration().smallestScreenWidthDp >= 600;
        if (terminal == null || botId == null || number < 0) { finish(); return; }
        if (security == null) security = "";
        try { bridge = new McpBridge(this); }
        catch (Exception error) { Toast.makeText(this, error.getMessage(), Toast.LENGTH_LONG).show(); finish(); return; }
        buildScreen();
        renderTabs();
        renderForm();
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
        LinearLayout root = column();
        root.setBackgroundColor(getColor(R.color.background));
        setContentView(root);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            int top, bottom;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars());
                top = bars.top;
                bottom = bars.bottom;
            } else {
                top = insets.getSystemWindowInsetTop();
                bottom = insets.getSystemWindowInsetBottom();
            }
            view.setPadding(0, top, 0, bottom);
            return insets;
        });
        root.requestApplyInsets();
        ScrollView outer = new ScrollView(this);
        outer.setFillViewport(true);
        root.addView(outer, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout content = column();
        content.setPadding(dp(16), dp(14), dp(16), dp(12));
        outer.addView(content);
        TextView back = text("‹ Позиции", 16, R.color.orange);
        back.setOnClickListener(view -> finish());
        content.addView(back, new LinearLayout.LayoutParams(-1, dp(44)));
        LinearLayout heading = new LinearLayout(this);
        heading.setOrientation(LinearLayout.HORIZONTAL);
        heading.setGravity(Gravity.CENTER_VERTICAL);
        ImageView logo = new ImageView(this);
        logo.setImageResource(R.drawable.os_logo);
        heading.addView(logo, new LinearLayout.LayoutParams(dp(36), dp(36)));
        title = text(adding ? "Окно дооткрытия позиции" : "Окно закрытия позиции", 21,
            R.color.text_primary);
        title.setTypeface(null, Typeface.BOLD);
        LinearLayout.LayoutParams titleParams = new LinearLayout.LayoutParams(0, -2, 1);
        titleParams.leftMargin = dp(8);
        heading.addView(title, titleParams);
        content.addView(heading);
        View strip = new View(this);
        strip.setBackgroundColor(getColor(R.color.brand_strip));
        LinearLayout.LayoutParams stripParams = new LinearLayout.LayoutParams(-1, dp(3));
        stripParams.topMargin = dp(12);
        content.addView(strip, stripParams);
        status = text("Загрузка данных VPS…", 12, R.color.text_secondary);
        status.setPadding(0, dp(8), 0, dp(8));
        content.addView(status);

        info = column();
        depthContent = column();
        if (tablet) {
            LinearLayout top = new LinearLayout(this);
            top.setOrientation(LinearLayout.HORIZONTAL);
            top.addView(info, new LinearLayout.LayoutParams(0, -2, 1));
            LinearLayout.LayoutParams right = new LinearLayout.LayoutParams(0, -2, 1);
            right.leftMargin = dp(12);
            top.addView(depthPanel(), right);
            content.addView(top);
        } else {
            content.addView(info);
            content.addView(depthPanel());
        }
        tabScroll = new HorizontalScrollView(this);
        tabScroll.setHorizontalScrollBarEnabled(false);
        tabs = new LinearLayout(this);
        tabs.setOrientation(LinearLayout.HORIZONTAL);
        tabScroll.addView(tabs);
        LinearLayout.LayoutParams tabParams = new LinearLayout.LayoutParams(-1, dp(54));
        tabParams.topMargin = dp(12);
        content.addView(tabScroll, tabParams);
        form = column();
        content.addView(form);
        actions = new LinearLayout(this);
        actions.setGravity(Gravity.END | Gravity.CENTER_VERTICAL);
        actions.setPadding(dp(12), dp(5), dp(12), dp(5));
        actions.setBackgroundColor(getColor(R.color.panel));
        root.addView(actions, new LinearLayout.LayoutParams(-1, dp(62)));
        renderInfo();
        renderDepth();
    }

    private View depthPanel() {
        LinearLayout panel = column();
        panel.setBackgroundResource(R.drawable.input_background);
        panel.setPadding(dp(8), dp(6), dp(8), dp(6));
        TextView heading = text("Стакан", 16, R.color.orange);
        panel.addView(heading);
        depthScroll = new ScrollView(this);
        depthScroll.addView(depthContent);
        panel.addView(depthScroll, new LinearLayout.LayoutParams(-1, dp(tablet ? 310 : 160)));
        TextView grip = text("≡", 15, R.color.orange);
        grip.setGravity(Gravity.CENTER);
        panel.addView(grip, new LinearLayout.LayoutParams(-1, dp(24)));
        grip.setOnTouchListener(new View.OnTouchListener() {
            private float lastY;
            @Override public boolean onTouch(View view, MotionEvent event) {
                if (event.getAction() == MotionEvent.ACTION_DOWN) {
                    lastY = event.getRawY();
                    return true;
                }
                if (event.getAction() == MotionEvent.ACTION_MOVE) {
                    int height = depthScroll.getLayoutParams().height +
                        Math.round(event.getRawY() - lastY);
                    height = Math.max(dp(tablet ? 200 : 110),
                        Math.min(dp(tablet ? 650 : 420), height));
                    depthScroll.getLayoutParams().height = height;
                    depthScroll.requestLayout();
                    lastY = event.getRawY();
                    return true;
                }
                return event.getAction() == MotionEvent.ACTION_UP;
            }
        });
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(10);
        panel.setLayoutParams(params);
        return panel;
    }

    private void renderInfo() {
        info.removeAllViews();
        info.addView(text("Сервер: " + server, 14, R.color.text_primary));
        info.addView(text("Инструмент: " + security, 14, R.color.text_primary));
        if (adding) info.addView(text("Вкладка робота: " + (tabName == null ? "" : tabName),
            14, R.color.text_primary));
        info.addView(text("Номер позиции: " + number, 14, R.color.text_primary));
        info.addView(text("Состояние: " + state, 14, R.color.text_primary));
        info.addView(text("Направление: " + side, 14, R.color.text_primary));
        info.addView(text("Открытый объём: " + volume, 14, R.color.text_primary));
    }

    private void renderTabs() {
        tabs.removeAllViews();
        String[] names = adding ? ADD_TABS : CLOSE_TABS;
        TextView selectedView = null;
        for (int i = 0; i < names.length; i++) {
            final int selected = i;
            TextView item = text(names[i], 14, i == tab ? R.color.orange : R.color.text_primary);
            item.setPadding(dp(13), 0, dp(13), 0);
            item.setGravity(Gravity.CENTER);
            item.setBackgroundResource(R.drawable.input_background);
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-2, dp(46));
            params.rightMargin = dp(4);
            tabs.addView(item, params);
            if (i == tab) selectedView = item;
            item.setOnClickListener(view -> {
                if (busy) return;
                saveCurrentFields();
                tab = selected;
                renderTabs();
                renderForm();
            });
        }
        TextView chosen = selectedView;
        tabScroll.post(() -> tabScroll.smoothScrollTo(Math.max(0, chosen.getLeft() - dp(12)), 0));
    }

    private void renderForm() {
        fields.clear();
        form.removeAllViews();
        serverStopBox = null;
        if (adding) renderAddForm();
        else renderCloseForm();
        renderActions();
    }

    private void renderAddForm() {
        if (tab == 0) { input("price", "Цена ордера", ""); input("volume", "Объём", ""); }
        if (tab == 1) input("volume", "Объём", "");
        if (tab == 2 || tab == 3) {
            if (tab == 2) {
                if (!serverStopOn) {
                    spinner("activate_type", "Тип активации", "HigherOrEqual", "LowerOrEqual");
                    spinner("lifetime_type", "Тип жизни заявки", "CandlesCount", "NoLifeTime");
                    input("lifetime_bars", "Время жизни свечей", "1");
                }
                input("activation_price", "Цена активации", "");
                input("price", "Цена ордера", "");
                input("volume", "Объём", "");
            } else {
                input("activation_price", "Цена активации", "");
                input("volume", "Объём", "");
                if (!serverStopOn) {
                    spinner("activate_type", "Тип активации", "HigherOrEqual", "LowerOrEqual");
                    spinner("lifetime_type", "Тип жизни заявки", "CandlesCount", "NoLifeTime");
                    input("lifetime_bars", "Время жизни свечей", "1");
                }
            }
            serverStop();
        }
        if (tab == 4) {
            fakeDateTime("Дата открытия", "Время открытия");
            input("price", "Цена ордера", "");
            input("volume", "Объём", "");
        }
    }

    private void renderCloseForm() {
        if (tab == 0) input("price", "Цена ордера", "");
        if (tab == 2 || tab == 3 || tab == 4) input("activation_price", "Цена активации", "");
        if (tab == 2 || tab == 4) input("price", "Цена ордера", "");
        if (tab == 2 || tab == 3) serverStop();
        if (tab == 5) {
            fakeDateTime("Дата закрытия", "Время закрытия");
            form.addView(text("На VPS время закрытия будет текущим", 12, R.color.text_secondary));
            input("price", "Цена ордера", "");
        }
        if (tab != 4) {
            input("volume", "Объём", "");
            TextView all = text("Весь открытый объём", 13, R.color.orange);
            all.setPadding(0, dp(10), 0, dp(10));
            all.setOnClickListener(view -> fields.get("volume").setText(volume));
            form.addView(all);
        }
    }

    private void serverStop() {
        serverStopBox = new CheckBox(this);
        serverStopBox.setText("Серверный стоп ордер");
        serverStopBox.setTextColor(getColor(R.color.text_primary));
        serverStopBox.setButtonTintList(ColorStateList.valueOf(getColor(R.color.orange)));
        serverStopBox.setChecked(serverStopOn);
        serverStopBox.setEnabled(positionFound && serverStopSupported && !busy);
        form.addView(serverStopBox);
        serverStopBox.setOnClickListener(view -> setServerStop(serverStopBox.isChecked()));
    }

    private void fakeDateTime(String dateLabel, String timeLabel) {
        LocalDate today = LocalDate.now();
        LocalTime now = LocalTime.now();
        EditText date = input("date", dateLabel, today.toString());
        date.setInputType(InputType.TYPE_NULL);
        date.setOnClickListener(view -> new DatePickerDialog(this, (picker, year, month, day) ->
            date.setText(LocalDate.of(year, month + 1, day).toString()),
            today.getYear(), today.getMonthValue() - 1, today.getDayOfMonth()).show());
        EditText time = input("time", timeLabel, now.format(DateTimeFormatter.ofPattern("HH:mm")));
        time.setInputType(InputType.TYPE_NULL);
        time.setOnClickListener(view -> new TimePickerDialog(this, (picker, hour, minute) ->
            time.setText(String.format(Locale.ROOT, "%02d:%02d", hour, minute)),
            now.getHour(), now.getMinute(), true).show());
        TextView current = text("Сейчас", 13, R.color.orange);
        current.setPadding(0, dp(10), 0, dp(10));
        current.setOnClickListener(view -> {
            date.setText(LocalDate.now().toString());
            time.setText(LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm")));
        });
        form.addView(current);
    }

    private EditText input(String key, String title, String initial) {
        form.addView(text(title, 13, R.color.orange));
        EditText edit = new EditText(this);
        edit.setSingleLine(true);
        edit.setTextSize(16);
        edit.setTextColor(getColor(R.color.text_primary));
        edit.setBackgroundResource(R.drawable.input_background);
        edit.setPadding(dp(12), 0, dp(12), 0);
        edit.setInputType("date".equals(key) || "time".equals(key) ? InputType.TYPE_CLASS_TEXT
            : InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_DECIMAL
                | InputType.TYPE_NUMBER_FLAG_SIGNED);
        edit.setText(savedValue(key, initial));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(48));
        params.topMargin = dp(4);
        params.bottomMargin = dp(8);
        form.addView(edit, params);
        fields.put(key, edit);
        return edit;
    }

    private void spinner(String key, String title, String first, String second) {
        form.addView(text(title, 13, R.color.orange));
        Spinner spinner = new Spinner(this);
        String[] choices = {first, second};
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this,
            android.R.layout.simple_spinner_item, choices);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spinner.setAdapter(adapter);
        spinner.setBackgroundResource(R.drawable.input_background);
        String saved = savedValue(key, first);
        spinner.setSelection(second.equals(saved) ? 1 : 0);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(48));
        params.bottomMargin = dp(8);
        form.addView(spinner, params);
        spinner.setTag(key);
    }

    private String savedValue(String key, String fallback) {
        Map<String, String> values = savedFields.get(tab);
        return values == null ? fallback : values.getOrDefault(key, fallback);
    }

    private void saveCurrentFields() {
        Map<String, String> values = savedFields.computeIfAbsent(tab, ignored -> new HashMap<>());
        for (Map.Entry<String, EditText> entry : fields.entrySet())
            values.put(entry.getKey(), entry.getValue().getText().toString());
        for (int i = 0; i < form.getChildCount(); i++) {
            View child = form.getChildAt(i);
            if (child instanceof Spinner && child.getTag() instanceof String)
                values.put((String) child.getTag(), ((Spinner) child).getSelectedItem().toString());
        }
    }

    private void renderActions() {
        actions.removeAllViews();
        if (!adding && (tab == 0 || tab == 2 || tab == 3 || tab == 4)) {
            TextView revoke = button("Отозвать", false);
            actions.addView(revoke, new LinearLayout.LayoutParams(0, dp(48), 1));
            revoke.setOnClickListener(view -> submit(true));
        }
        String label;
        if (adding) {
            String verb = "Sell".equalsIgnoreCase(side) ? "Продать" : "Купить";
            label = tab == 0 ? verb + " лимит ордером" : tab == 1 ? verb + " маркет ордером"
                : tab == 2 ? verb + " стоп ордером" : tab == 3 ? verb + " стоп-маркет ордером"
                : verb + " фейк ордером";
        } else {
            label = new String[]{"Закрыть лимит ордером", "Закрыть маркет ордером",
                "Выставить стоп ордер", "Выставить стоп-маркет ордер",
                "Выставить профит ордер", "Закрыть фейк ордером"}[tab];
        }
        TextView submit = button(label, true);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, dp(48), 2);
        params.leftMargin = dp(8);
        actions.addView(submit, params);
        submit.setOnClickListener(view -> submit(false));
    }

    private void refresh() {
        handler.removeCallbacks(polling);
        if (!visible || loading || busy || bridge == null) return;
        if (!RemoteSsh.isConnected()) {
            status.setText("Нет связи · SSH");
            positionFound = false;
            renderActions();
            handler.postDelayed(polling, 3_000);
            return;
        }
        loading = true;
        worker.execute(() -> {
            JSONObject snapshot = null;
            JSONObject open = null;
            JSONObject book = null;
            String nextTab = tabName;
            String error = null;
            try {
                if (nextTab == null) nextTab = sourceName();
                if (nextTab == null || nextTab.isEmpty())
                    throw new IllegalStateException("VPS API не вернул торговую вкладку Simple");
                JSONObject args = new JSONObject().put("bot_id", botId).put("tab_name", nextTab);
                Map<String, Object> values = bridge.callBatch(terminal,
                    McpBridge.call("bot_chart_get_snapshot", new JSONObject(args.toString())
                        .put("candle_count", 1)),
                    McpBridge.call("bot_position_get_open", args),
                    McpBridge.call("bot_chart_get_market_depth", new JSONObject(args.toString())
                        .put("level_count", 25)));
                snapshot = result(values, "bot_chart_get_snapshot");
                open = result(values, "bot_position_get_open");
                book = result(values, "bot_chart_get_market_depth");
            } catch (Exception e) { error = e.getMessage(); }
            String resolved = nextTab;
            JSONObject nextSnapshot = snapshot, nextOpen = open, nextBook = book;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (finalError == null) {
                    tabName = resolved;
                    server = nextSnapshot.optString("server_type");
                    serverStopSupported = nextSnapshot.optBoolean("server_stop_orders_supported");
                    boolean oldServerStop = serverStopOn;
                    if (adding) serverStopOn = nextSnapshot.optBoolean("server_stop_orders_is_on");
                    if (adding && oldServerStop != serverStopOn) {
                        saveCurrentFields();
                        renderForm();
                    }
                    JSONArray positions = nextOpen.optJSONArray("positions");
                    JSONObject found = null;
                    if (positions != null) for (int i = 0; i < positions.length(); i++) {
                        JSONObject item = positions.optJSONObject(i);
                        if (item != null && item.optInt("position_number", -1) == number) {
                            found = item;
                            break;
                        }
                    }
                    positionFound = found != null;
                    if (found != null) {
                        security = found.optString("security_name", security);
                        side = found.optString("direction");
                        state = found.optString("state");
                        volume = plain(found.optString("open_volume"));
                    }
                    depth = nextBook;
                    status.setText(positionFound ? "VPS · обновлено " +
                        LocalTime.now().format(DateTimeFormatter.ofPattern("HH:mm:ss"))
                        : "Позиция больше не открыта");
                    renderInfo();
                    if (adding) title.setText("Окно дооткрытия позиции. " +
                        ("Sell".equalsIgnoreCase(side) ? "Продаем" : "Покупаем"));
                    renderDepth();
                    if (serverStopBox != null) {
                        serverStopBox.setChecked(serverStopOn);
                        serverStopBox.setEnabled(positionFound && serverStopSupported && !busy);
                    }
                    renderActions();
                } else {
                    positionFound = false;
                    status.setText("Не удалось загрузить позицию: " + finalError);
                    renderActions();
                }
                handler.postDelayed(polling, 3_000);
            });
        });
    }

    private String sourceName() throws Exception {
        JSONObject result = call("bot_get_sources", new JSONObject().put("bot_id", botId));
        JSONArray sources = result.optJSONArray("sources");
        if (sources != null) for (int i = 0; i < sources.length(); i++) {
            JSONObject source = sources.optJSONObject(i);
            if (source != null && "Simple".equals(source.optString("type")))
                return source.optString("name");
        }
        return null;
    }

    private void renderDepth() {
        String mode = depth == null ? "" : depth.optString("mode");
        if (!mode.equals(depthMode)) {
            depthMode = mode;
            depthScroll.getLayoutParams().height = dp("BidAsk".equalsIgnoreCase(mode)
                ? (tablet ? 90 : 72) : (tablet ? 310 : 160));
            depthScroll.requestLayout();
        }
        int scrollY = depthScroll.getScrollY();
        depthContent.removeAllViews();
        if (depth == null) {
            depthContent.addView(text("Ожидание стакана…", 13, R.color.text_secondary));
            return;
        }
        if ("BidAsk".equalsIgnoreCase(depth.optString("mode"))) {
            depthPrice(depth.optString("best_ask"), "", R.color.orange);
            depthPrice(depth.optString("best_bid"), "", R.color.server_connected);
            depthScroll.post(() -> depthScroll.scrollTo(0, scrollY));
            return;
        }
        depthContent.addView(text("Сумма        Объём          Цена          Объём", 12,
            R.color.text_secondary));
        JSONArray asks = depth.optJSONArray("asks"), bids = depth.optJSONArray("bids");
        double maxVolume = 0, totalAsk = 0, totalBid = 0;
        for (JSONArray side : new JSONArray[]{asks, bids}) if (side != null)
            for (int i = 0; i < Math.min(25, side.length()); i++) {
                JSONObject level = side.optJSONObject(i);
                if (level == null) continue;
                double levelVolume = level.optDouble("volume");
                maxVolume = Math.max(maxVolume, levelVolume);
                if (side == asks) totalAsk += levelVolume;
                else totalBid += levelVolume;
            }
        double maxTotal = Math.max(totalAsk, totalBid);
        double askSum = 0;
        if (asks != null) for (int i = Math.min(25, asks.length()) - 1; i >= 0; i--) {
            JSONObject level = asks.optJSONObject(i);
            if (level != null) {
                for (int j = 0; j <= i; j++) {
                    JSONObject sumLevel = asks.optJSONObject(j);
                    if (sumLevel != null) askSum += sumLevel.optDouble("volume");
                }
                depthPrice(level.optString("price"), level.optString("volume"),
                    bar(askSum, maxTotal), bar(level.optDouble("volume"), maxVolume),
                    R.color.orange);
                askSum = 0;
            }
        }
        double bidSum = 0;
        if (bids != null) for (int i = 0; i < Math.min(25, bids.length()); i++) {
            JSONObject level = bids.optJSONObject(i);
            if (level != null) {
                bidSum += level.optDouble("volume");
                depthPrice(level.optString("price"), level.optString("volume"),
                    bar(bidSum, maxTotal), bar(level.optDouble("volume"), maxVolume),
                    R.color.server_connected);
            }
        }
        depthScroll.post(() -> depthScroll.scrollTo(0, scrollY));
    }

    private void depthPrice(String rawPrice, String rawVolume, int color) {
        depthPrice(rawPrice, rawVolume, "", "", color);
    }

    private static String bar(double value, double max) {
        if (value <= 0 || max <= 0) return "";
        int count = Math.max(1, Math.min(10, (int) Math.round(value / max * 10)));
        StringBuilder result = new StringBuilder(count);
        for (int i = 0; i < count; i++) result.append('|');
        return result.toString();
    }

    private void depthPrice(String rawPrice, String rawVolume, String sumBar, String volumeBar,
                            int color) {
        if (rawPrice.isEmpty() || "0".equals(rawPrice)) return;
        String price = plain(rawPrice);
        TextView row = text(rawVolume.isEmpty() ? price :
            sumBar + "   " + volumeBar + "   " + price + "   " + plain(rawVolume),
            13, color);
        row.setPadding(dp(5), dp(3), dp(5), dp(3));
        depthContent.addView(row);
        row.setOnClickListener(view -> {
            for (int i = 0; i < (adding ? ADD_TABS.length : CLOSE_TABS.length); i++) {
                Map<String, String> values = savedFields.computeIfAbsent(i, ignored -> new HashMap<>());
                values.put("price", price);
                values.put("activation_price", price);
            }
            if (fields.containsKey("price")) fields.get("price").setText(price);
            if (fields.containsKey("activation_price"))
                fields.get("activation_price").setText(price);
        });
    }

    private void setServerStop(boolean enabled) {
        if (!positionFound || !serverStopSupported || busy) return;
        if (!adding) { serverStopOn = enabled; return; }
        busy = true;
        renderActions();
        serverStopBox.setEnabled(false);
        worker.execute(() -> {
            String error = null;
            try {
                call("bot_chart_execute_action", baseArgs().put("action", "SetServerStopOrders")
                    .put("server_stop", enabled));
            } catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                if (isDestroyed()) return;
                busy = false;
                if (finalError == null) serverStopOn = enabled;
                else Toast.makeText(this, finalError, Toast.LENGTH_LONG).show();
                if (serverStopBox != null) {
                    serverStopBox.setChecked(serverStopOn);
                    serverStopBox.setEnabled(positionFound && serverStopSupported && !busy);
                }
                saveCurrentFields();
                renderForm();
                handler.removeCallbacks(polling);
                handler.post(polling);
            });
        });
    }

    private void submit(boolean revoke) {
        if (!visible || !positionFound || busy || !RemoteSsh.isConnected()) return;
        String tool;
        JSONObject args;
        try {
            args = baseArgs().put("position_number", number);
            if (adding) {
                tool = "bot_position_add";
                String[] types = {"Limit", "Market", "Stop", "StopMarket", "Fake"};
                args.put("order_type", types[tab]).put("volume", positive("volume"));
                if (tab == 0 || tab == 2 || tab == 4) args.put("price", positive("price"));
                if (tab == 2 || tab == 3) {
                    args.put("activation_price", positive("activation_price"));
                    args.put("server_stop", serverStopOn);
                    if (!serverStopOn) {
                        args.put("stop_activate_type", selected("activate_type"));
                        args.put("lifetime_type", selected("lifetime_type"));
                        args.put("lifetime_bars", positiveInt("lifetime_bars"));
                    }
                }
                if (tab == 4) args.put("time_local", fakeTime());
            } else if (revoke) {
                tool = tab == 0 ? "bot_position_revoke_close_orders"
                    : tab == 4 ? "bot_position_revoke_profit" : "bot_position_revoke_stop";
                if (tab == 2 || tab == 3) args.put("server_side", serverStopOn);
            } else {
                String[] tools = {"bot_position_close_at_limit", "bot_position_close_at_market",
                    "bot_position_close_at_stop", "bot_position_close_at_stop_market",
                    "bot_position_close_at_profit", "bot_position_close_at_market"};
                tool = tools[tab];
                if (tab == 0 || tab == 2 || tab == 4 || tab == 5) args.put("price", positive("price"));
                if (tab == 2 || tab == 3 || tab == 4)
                    args.put("activation_price", positive("activation_price"));
                if (tab != 4) args.put("volume", closeVolume());
                if (tab == 2 || tab == 3) args.put("server_side", serverStopOn);
                if (tab == 5) args.put("is_fake", true);
            }
        } catch (Exception error) {
            Toast.makeText(this, "Проверьте поля: " + error.getMessage(), Toast.LENGTH_LONG).show();
            return;
        }
        busy = true;
        renderActions();
        worker.execute(() -> {
            String error = null;
            try { call(tool, args); }
            catch (Exception e) { error = e.getMessage(); }
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                Toast.makeText(this, finalError == null ? "Команда выполнена на VPS"
                    : "Ошибка VPS: " + finalError, Toast.LENGTH_LONG).show();
                handler.removeCallbacks(polling);
                handler.post(polling);
                renderActions();
            });
        });
    }

    private JSONObject baseArgs() throws Exception {
        return new JSONObject().put("bot_id", botId).put("tab_name", tabName)
            .put("security_name", security);
    }

    private BigDecimal closeVolume() {
        EditText edit = fields.get("volume");
        String value = edit == null ? "" : edit.getText().toString().trim();
        BigDecimal amount = value.isEmpty() ? new BigDecimal(volume.replace(',', '.'))
            : parse(value);
        if (amount.signum() <= 0 || amount.compareTo(new BigDecimal(volume.replace(',', '.'))) > 0)
            throw new IllegalArgumentException("Объём больше открытого или равен нулю");
        return amount;
    }

    private BigDecimal positive(String key) {
        EditText field = fields.get(key);
        if (field == null) throw new IllegalArgumentException(key);
        BigDecimal value = parse(field.getText().toString());
        if (value.signum() <= 0) throw new IllegalArgumentException(key + " должен быть больше нуля");
        return value;
    }

    private int positiveInt(String key) {
        int value = Integer.parseInt(fields.get(key).getText().toString().trim());
        if (value <= 0) throw new IllegalArgumentException(key + " должен быть больше нуля");
        return value;
    }

    private String selected(String key) {
        for (int i = 0; i < form.getChildCount(); i++) {
            View child = form.getChildAt(i);
            if (child instanceof Spinner && key.equals(child.getTag()))
                return ((Spinner) child).getSelectedItem().toString();
        }
        throw new IllegalArgumentException(key);
    }

    private String fakeTime() {
        LocalDate date = LocalDate.parse(fields.get("date").getText().toString());
        LocalTime time = LocalTime.parse(fields.get("time").getText().toString());
        return ZonedDateTime.of(date, time, ZoneId.systemDefault()).toOffsetDateTime()
            .format(DateTimeFormatter.ISO_OFFSET_DATE_TIME);
    }

    private static BigDecimal parse(String raw) {
        return new BigDecimal(raw.trim().replace(',', '.'));
    }

    private static String plain(String raw) {
        try { return new BigDecimal(raw).stripTrailingZeros().toPlainString(); }
        catch (Exception ignored) { return raw; }
    }

    private JSONObject call(String tool, JSONObject args) throws Exception {
        return result(bridge.callBatch(terminal, McpBridge.call(tool, args)), tool);
    }

    private static JSONObject result(Map<String, Object> data, String tool) throws Exception {
        Object value = data.get(tool);
        if (value instanceof Exception) throw (Exception) value;
        if (!(value instanceof JSONObject)) throw new IllegalStateException("Пустой ответ MCP: " + tool);
        return (JSONObject) value;
    }

    private TextView button(String title, boolean primary) {
        TextView view = text(title, 13, primary ? R.color.text_primary : R.color.orange);
        view.setGravity(Gravity.CENTER);
        view.setBackgroundResource(primary ? R.drawable.button_background : R.drawable.input_background);
        view.setEnabled(positionFound && !busy);
        view.setAlpha(view.isEnabled() ? 1f : .45f);
        return view;
    }

    private TextView text(String value, int size, int color) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        view.setTextColor(getColor(color));
        view.setGravity(Gravity.CENTER_VERTICAL);
        view.setPadding(0, dp(5), 0, dp(5));
        return view;
    }

    private LinearLayout column() {
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        return layout;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
