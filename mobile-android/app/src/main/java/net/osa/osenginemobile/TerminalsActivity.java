package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.res.ColorStateList;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.TextView;
import android.widget.Toast;

import java.io.IOException;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class TerminalsActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final VpsSnapshotReader reader = new VpsSnapshotReader();
    private final Runnable refresh = this::loadSnapshot;
    private TextView status;
    private LinearLayout terminalList;
    private boolean loading;
    private boolean visible;
    private long vpsRamTotal;
    private boolean preview;
    private VpsSnapshot lastSnapshot;
    private final Set<String> restartingServices = new HashSet<>();
    private boolean unavailable;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_terminals);
        ScreenLayout.apply(this, 760);
        status = findViewById(R.id.server_status);
        terminalList = findViewById(R.id.terminal_list);
        preview = (getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0
            && getIntent().getBooleanExtra("preview_terminals", false);
        if (preview) {
            showSnapshot(previewSnapshot());
            status.setText(R.string.terminal_preview);
            return;
        }
        status.setText(RemoteSsh.host() == null ? getString(R.string.status_not_connected)
            : RemoteSsh.host() + " · SSH");
    }

    @Override
    protected void onResume() {
        super.onResume();
        visible = true;
        if (!preview) handler.post(refresh);
    }

    @Override
    protected void onPause() {
        visible = false;
        handler.removeCallbacks(refresh);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    private void loadSnapshot() {
        handler.removeCallbacks(refresh);
        if (!visible || loading) return;
        if (!RemoteSsh.isConnected()) {
            if (lastSnapshot != null) renderSnapshot(lastSnapshot, false);
            status.setText(R.string.status_not_connected);
            return;
        }
        loading = true;
        worker.execute(() -> {
            VpsSnapshot snapshot = null;
            String error = null;
            try { snapshot = reader.read(); }
            catch (Exception e) { error = e.getMessage(); }
            VpsSnapshot finalSnapshot = snapshot;
            String finalError = error;
            runOnUiThread(() -> {
                loading = false;
                if (!visible || isDestroyed()) return;
                if (finalError != null) {
                    if (lastSnapshot != null) renderSnapshot(lastSnapshot, false);
                    status.setText(finalError);
                } else showSnapshot(finalSnapshot);
                handler.postDelayed(refresh, 10_000);
            });
        });
    }

    private void showSnapshot(VpsSnapshot snapshot) {
        lastSnapshot = snapshot;
        renderSnapshot(snapshot, true);
    }

    private void renderSnapshot(VpsSnapshot snapshot, boolean available) {
        unavailable = !available;
        vpsRamTotal = snapshot.ramTotal;
        status.setText(RemoteSsh.host() + " · SSH · "
            + new SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(new Date()));
        ((TextView) findViewById(R.id.cpu_value)).setText("CPU  " + percent(snapshot.cpuPercent));
        ((TextView) findViewById(R.id.ram_value)).setText("RAM  " + percent(snapshot.ramPercent));
        ((TextView) findViewById(R.id.disk_value)).setText("Диск  " + percent(snapshot.diskPercent));
        terminalList.removeAllViews();
        if (snapshot.terminals.isEmpty()) {
            TextView empty = label(getString(R.string.terminal_empty), 14,
                R.color.text_secondary);
            terminalList.addView(empty);
            return;
        }
        for (VpsSnapshot.Terminal terminal : snapshot.terminals) {
            terminalList.addView(card(terminal));
        }
    }

    private View card(VpsSnapshot.Terminal terminal) {
        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setPadding(dp(16), dp(12), dp(16), dp(12));
        card.setBackgroundResource(R.drawable.input_background);
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(-1, -2);
        cardParams.bottomMargin = dp(10);
        card.setLayoutParams(cardParams);
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        card.addView(header, new LinearLayout.LayoutParams(-1, -2));
        TextView name = label(terminal.name, 20, R.color.text_primary);
        name.setTypeface(null, android.graphics.Typeface.BOLD);
        header.addView(name, new LinearLayout.LayoutParams(0, -2, 1));
        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.VERTICAL);
        header.addView(actions, new LinearLayout.LayoutParams(dp(96), -2));
        TextView open = label(getString(R.string.terminal_open_button), 14, R.color.text_primary);
        open.setGravity(Gravity.CENTER);
        open.setBackgroundResource(R.drawable.button_background);
        open.setContentDescription(getString(R.string.terminal_open_robots, terminal.name));
        actions.addView(open, new LinearLayout.LayoutParams(-1, dp(36)));
        open.setOnClickListener(view -> openRobots(terminal));
        open.setEnabled(!preview);
        if (preview) open.setAlpha(0.45f);
        TextView restart = label(getString(R.string.terminal_restart_button), 14, R.color.orange);
        restart.setGravity(Gravity.CENTER);
        restart.setBackgroundResource(R.drawable.restart_outline);
        restart.setContentDescription(getString(R.string.terminal_restart_accessibility,
            terminal.name));
        restart.setTooltipText(getString(R.string.terminal_restart_accessibility,
            terminal.name));
        LinearLayout.LayoutParams restartParams = new LinearLayout.LayoutParams(-1, dp(36));
        restartParams.topMargin = dp(5);
        actions.addView(restart, restartParams);
        restart.setOnClickListener(view -> confirmRestart(terminal));
        boolean canRestart = !preview && !unavailable
            && !restartingServices.contains(terminal.service);
        restart.setEnabled(canRestart);
        if (!canRestart) restart.setAlpha(0.45f);

        String state;
        int stateColor = R.color.text_secondary;
        if (unavailable) {
            state = getString(R.string.terminal_state_inactive);
        } else if (restartingServices.contains(terminal.service)) {
            state = getString(R.string.terminal_state_restarting);
            stateColor = R.color.orange;
        } else if ("active".equals(terminal.state)) {
            state = getString(R.string.terminal_state_active);
            stateColor = R.color.connected;
        } else if ("inactive".equals(terminal.state)) {
            state = getString(R.string.terminal_state_inactive);
        } else if ("failed".equals(terminal.state)) {
            state = getString(R.string.terminal_state_failed);
            stateColor = R.color.orange;
        } else if ("activating".equals(terminal.state)) {
            state = getString(R.string.terminal_state_starting);
        } else if ("deactivating".equals(terminal.state)) {
            state = getString(R.string.terminal_state_stopping);
        } else if ("reloading".equals(terminal.state)) {
            state = getString(R.string.terminal_state_reloading);
        } else {
            state = getString(R.string.terminal_state_other, terminal.state);
        }
        TextView stateLabel = label(state, 14, stateColor);
        LinearLayout.LayoutParams stateParams = new LinearLayout.LayoutParams(-1, -2);
        stateParams.topMargin = dp(5);
        card.addView(stateLabel, stateParams);
        addMetric(card, "CPU", percent(terminal.cpuPercent), terminal.cpuPercent);
        double ramPercent = vpsRamTotal > 0
            ? 100.0 * terminal.memoryBytes / vpsRamTotal : Double.NaN;
        addMetric(card, "RAM", memory(terminal.memoryBytes) + "  ·  "
            + percent(ramPercent) + " VPS", ramPercent);
        card.setContentDescription(getString(R.string.terminal_open_robots, terminal.name));
        card.setClickable(true);
        card.setFocusable(true);
        card.setOnClickListener(view -> openRobots(terminal));
        return card;
    }

    private void openRobots(VpsSnapshot.Terminal terminal) {
        if (preview) return;
        Intent intent = new Intent(this, RobotsActivity.class);
        intent.putExtra("terminal_name", terminal.name);
        startActivity(intent);
    }

    private void addMetric(LinearLayout card, String title, String value, double amount) {
        TextView text = label(title + "  " + value, 14, R.color.text_primary);
        LinearLayout.LayoutParams textParams = new LinearLayout.LayoutParams(-1, -2);
        textParams.topMargin = dp(10);
        card.addView(text, textParams);
        ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setMax(100);
        bar.setProgress(Double.isNaN(amount) ? 0 : (int) Math.round(amount));
        bar.setProgressTintList(ColorStateList.valueOf(getColor(R.color.orange)));
        LinearLayout.LayoutParams barParams = new LinearLayout.LayoutParams(-1, dp(5));
        barParams.topMargin = dp(5);
        card.addView(bar, barParams);
    }

    private static VpsSnapshot previewSnapshot() {
        VpsSnapshot snapshot = new VpsSnapshot();
        snapshot.cpuPercent = 31.2;
        snapshot.ramPercent = 62.4;
        snapshot.diskPercent = 41.8;
        snapshot.ramTotal = 2L * 1024 * 1024 * 1024;
        VpsSnapshot.Terminal main = new VpsSnapshot.Terminal();
        main.name = "main";
        main.service = "osengine";
        main.state = "active";
        main.cpuPercent = 12.6;
        main.memoryBytes = 608L * 1024 * 1024;
        snapshot.terminals.add(main);
        VpsSnapshot.Terminal binance = new VpsSnapshot.Terminal();
        binance.name = "binance";
        binance.service = "osengine-binance";
        binance.state = "active";
        binance.cpuPercent = 18.4;
        binance.memoryBytes = 384L * 1024 * 1024;
        snapshot.terminals.add(binance);
        return snapshot;
    }

    private void confirmRestart(VpsSnapshot.Terminal terminal) {
        new AlertDialog.Builder(this)
            .setTitle(R.string.restart_confirm_title)
            .setMessage(getString(R.string.restart_confirm_message, terminal.name))
            .setNegativeButton(R.string.cancel, null)
            .setPositiveButton(R.string.restart, (dialog, which) -> restart(terminal.service))
            .show();
    }

    private void restart(String service) {
        if (!service.matches("osengine(?:-[a-z0-9-]+)?")) return;
        restartingServices.add(service);
        if (lastSnapshot != null) showSnapshot(lastSnapshot);
        status.setText(R.string.terminal_restarting);
        worker.execute(() -> {
            String message;
            try {
                RemoteSsh.run("systemctl restart " + service);
                message = getString(R.string.terminal_restart_started);
            } catch (IOException e) {
                message = getString(R.string.terminal_restart_failed, e.getMessage());
            }
            String finalMessage = message;
            runOnUiThread(() -> {
                if (isDestroyed()) return;
                restartingServices.remove(service);
                Toast.makeText(this, finalMessage, Toast.LENGTH_LONG).show();
                if (visible) handler.post(refresh);
            });
        });
    }

    private TextView label(String value, int sp, int color) {
        TextView text = new TextView(this);
        text.setText(value);
        text.setTextSize(sp);
        text.setTextColor(getColor(color));
        return text;
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String percent(double value) {
        return Double.isNaN(value) ? "—" : String.format(Locale.US, "%.1f%%", value);
    }

    private static String memory(long bytes) {
        return bytes <= 0 ? "—" : String.format(Locale.US, "%.0f MB", bytes / 1_048_576.0);
    }
}
