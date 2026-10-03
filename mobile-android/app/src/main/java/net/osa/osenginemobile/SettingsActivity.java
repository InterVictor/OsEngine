package net.osa.osenginemobile;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.KeyguardManager;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.CheckBox;
import android.widget.ProgressBar;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.IOException;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Settings: the update of the VPS terminals from the newest signed GitHub release (the VPS does the work through
 * "osengine-release", this screen only starts it and shows the progress), the connection and the app version.
 */
public final class SettingsActivity extends Activity {
    private static final int REQUEST_CREDENTIAL = 41;
    private static final String CHECK_COMMAND = "osengine-release check";

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable poll = this::pollStatus;
    private TextView installedView;
    private TextView latestView;
    private TextView stateView;
    private TextView logView;
    private TextView checkButton;
    private TextView applyButton;
    private ProgressBar progress;
    private ProfileStore profile;
    private McpBridge bridge;
    private ServerRelease release;
    private boolean visible;
    private boolean busy;
    private boolean updating;
    private boolean preview;
    private int lostPolls;
    private int idlePolls;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_settings);
        ScreenLayout.apply(this, 560);
        installedView = findViewById(R.id.update_installed);
        latestView = findViewById(R.id.update_latest);
        stateView = findViewById(R.id.update_state);
        logView = findViewById(R.id.update_log);
        checkButton = findViewById(R.id.update_check);
        applyButton = findViewById(R.id.update_apply);
        progress = findViewById(R.id.update_progress);
        profile = new ProfileStore(this);
        findViewById(R.id.back_terminals).setOnClickListener(view -> finish());
        checkButton.setOnClickListener(view -> check());
        applyButton.setOnClickListener(view -> confirmApply());
        setApplyEnabled(false);

        TextView connection = findViewById(R.id.connection_info);
        connection.setText(getString(R.string.connection_info,
            RemoteSsh.host() == null ? "—" : RemoteSsh.host()));
        CheckBox autoConnect = findViewById(R.id.auto_connect_setting);
        autoConnect.setChecked(profile.autoConnect());
        autoConnect.setOnCheckedChangeListener((button, checked) -> profile.setAutoConnect(checked));
        ((TextView) findViewById(R.id.about_info)).setText(aboutText());
        android.widget.RadioGroup profitGroup = findViewById(R.id.day_profit_group);
        String profitMode = DayProfit.mode(this);
        profitGroup.check(DayProfit.PER_CONTRACT.equals(profitMode) ? R.id.day_profit_contract
            : DayProfit.DEPOSIT.equals(profitMode) ? R.id.day_profit_deposit : R.id.day_profit_abs);
        profitGroup.setOnCheckedChangeListener((group, id) -> DayProfit.setMode(this,
            id == R.id.day_profit_contract ? DayProfit.PER_CONTRACT
            : id == R.id.day_profit_deposit ? DayProfit.DEPOSIT : DayProfit.ABSOLUTE));

        String previewMode = getIntent().getStringExtra("preview_update");
        preview = (getApplicationInfo().flags & ApplicationInfo.FLAG_DEBUGGABLE) != 0 && previewMode != null;
        if (preview) showPreview(previewMode);
    }

    @Override
    protected void onResume() {
        super.onResume();
        visible = true;
        if (!preview && !updating && !busy) startWatching();
    }

    @Override
    protected void onPause() {
        visible = false;
        handler.removeCallbacks(poll);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    // on entering: continue watching an update that is already running on the VPS, otherwise check the versions
    private void startWatching() {
        if (!RemoteSsh.isConnected()) {
            showState(getString(R.string.update_no_ssh), R.color.orange);
            return;
        }
        busy = true;
        showBusy(true);
        worker.execute(() -> {
            boolean running = false;
            try { running = ServerRelease.Progress.parse(RemoteSsh.run("osengine-release status")).running; }
            catch (IOException ignored) { /* the check below reports the problem */ }
            boolean finalRunning = running;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed() || !visible) return;
                if (finalRunning) {
                    beginWatching();
                } else {
                    showBusy(false);
                    check();
                }
            });
        });
    }

    private void check() {
        if (busy || updating) return;
        if (!RemoteSsh.isConnected()) {
            showState(getString(R.string.update_no_ssh), R.color.orange);
            return;
        }
        busy = true;
        showBusy(true);
        showState(getString(R.string.update_checking), R.color.text_secondary);
        worker.execute(() -> {
            ServerRelease result = null;
            String error = null;
            try { result = ServerRelease.parse(RemoteSsh.run(CHECK_COMMAND)); }
            catch (IOException e) { error = e.getMessage(); }
            ServerRelease finalResult = result;
            String finalError = error;
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                showBusy(false);
                if (finalResult != null) showRelease(finalResult);
                else showFailure(finalError);
            });
        });
    }

    private void showFailure(String error) {
        release = null;
        setApplyEnabled(false);
        boolean missing = error != null && error.contains("not found");
        showState(missing ? getString(R.string.update_tool_missing) : String.valueOf(error), R.color.orange);
    }

    private void showRelease(ServerRelease result) {
        release = result;
        StringBuilder installed = new StringBuilder(getString(R.string.update_installed_title));
        for (ServerRelease.Terminal terminal : result.terminals) {
            installed.append('\n').append(terminal.name).append("  ").append(terminal.version);
            if (terminal.needsUpdate) installed.append("  →");
        }
        installedView.setText(installed);
        if (result.latestVersion != null) {
            String note = result.latestNote.isEmpty() ? "" : "\n" + result.latestNote;
            latestView.setText(getString(R.string.update_latest_title, result.latestVersion,
                ServerRelease.shortDate(result.latestDate)) + note);
            latestView.setVisibility(View.VISIBLE);
        } else {
            latestView.setVisibility(View.GONE);
        }
        if (result.error != null) {
            showState(result.error, R.color.orange);
            setApplyEnabled(false);
        } else if (result.updateAvailable) {
            showState(getString(R.string.update_available), R.color.orange);
            setApplyEnabled(true);
        } else {
            showState(getString(R.string.update_current), R.color.connected);
            setApplyEnabled(false);
        }
    }

    // ---- update

    private void confirmApply() {
        if (release == null || !release.updateAvailable || updating || busy) return;
        busy = true;
        showBusy(true);
        worker.execute(() -> {
            String positions = openPositionsText(release);
            runOnUiThread(() -> {
                busy = false;
                if (isDestroyed()) return;
                showBusy(false);
                String names = String.join(", ", release.terminalsToUpdate());
                new AlertDialog.Builder(this)
                    .setTitle(R.string.update_confirm_title)
                    .setMessage(getString(R.string.update_confirm_message, release.latestVersion, names, positions))
                    .setNegativeButton(R.string.cancel, null)
                    .setPositiveButton(R.string.update_run, (dialog, which) -> confirmCredential())
                    .show();
            });
        });
    }

    /** Open positions of the terminals that will restart, so the decision is made knowing what is at stake. */
    private String openPositionsText(ServerRelease current) {
        int total = 0;
        StringBuilder details = new StringBuilder();
        boolean unknown = false;
        try {
            if (bridge == null) bridge = new McpBridge(this);
            for (ServerRelease.Terminal terminal : current.terminals) {
                if (!terminal.needsUpdate || !"active".equals(terminal.state)) continue;
                try {
                    Map<String, Object> data = bridge.callBatch(terminal.name,
                        McpBridge.call("bot_journal_get_open_positions", null));
                    Object value = data.get("bot_journal_get_open_positions");
                    int count = -1;
                    if (value instanceof JSONObject) count = ((JSONObject) value).optInt("count", -1);
                    else if (value instanceof JSONArray) count = ((JSONArray) value).length();
                    if (count < 0) { unknown = true; continue; }
                    total += count;
                    if (count > 0) {
                        if (details.length() > 0) details.append(", ");
                        details.append(terminal.name).append(' ').append(count);
                    }
                } catch (Exception e) {
                    unknown = true;
                }
            }
        } catch (Exception e) {
            unknown = true;
        }
        if (unknown) return getString(R.string.update_positions_unknown);
        if (total == 0) return getString(R.string.update_positions_none);
        return getString(R.string.update_positions_some, total, details.toString());
    }

    // with a screen lock the phone asks for the PIN / fingerprint first: the update restarts trading terminals
    private void confirmCredential() {
        KeyguardManager keyguard = (KeyguardManager) getSystemService(KEYGUARD_SERVICE);
        if (keyguard != null && keyguard.isDeviceSecure()) {
            Intent intent = keyguard.createConfirmDeviceCredentialIntent(
                getString(R.string.update_credential_title), getString(R.string.update_credential_message));
            if (intent != null) {
                startActivityForResult(intent, REQUEST_CREDENTIAL);
                return;
            }
        }
        startApply();
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_CREDENTIAL && resultCode == RESULT_OK) startApply();
    }

    private void startApply() {
        if (updating) return;
        updating = true;
        lostPolls = 0;
        idlePolls = 0;
        setApplyEnabled(false);
        showBusy(true);
        showState(getString(R.string.update_running), R.color.orange);
        logView.setText("");
        logView.setVisibility(View.VISIBLE);
        worker.execute(() -> {
            String error = null;
            try {
                String out = RemoteSsh.run("osengine-release apply");
                if (!out.contains("STARTED")) {
                    error = out.trim().startsWith("ERROR ") ? out.trim().substring(6) : out.trim();
                }
            } catch (IOException e) {
                error = e.getMessage();
            }
            String finalError = error;
            runOnUiThread(() -> {
                if (isDestroyed()) return;
                if (finalError != null) {
                    updating = false;
                    showBusy(false);
                    showState(finalError, R.color.orange);
                    setApplyEnabled(release != null && release.updateAvailable);
                } else {
                    handler.postDelayed(poll, 1500);
                }
            });
        });
    }

    private void beginWatching() {
        updating = true;
        lostPolls = 0;
        idlePolls = 0;
        setApplyEnabled(false);
        showBusy(true);
        showState(getString(R.string.update_running), R.color.orange);
        logView.setVisibility(View.VISIBLE);
        handler.post(poll);
    }

    private void pollStatus() {
        handler.removeCallbacks(poll);
        if (!updating || isDestroyed()) return;
        worker.execute(() -> {
            ServerRelease.Progress state = null;
            try { state = ServerRelease.Progress.parse(RemoteSsh.run("osengine-release status")); }
            catch (IOException ignored) { /* the phone may lose the link; the update goes on at the VPS */ }
            ServerRelease.Progress finalState = state;
            runOnUiThread(() -> {
                if (isDestroyed() || !updating) return;
                if (finalState == null) {
                    if (++lostPolls > 20) {
                        updating = false;
                        showBusy(false);
                        showState(getString(R.string.update_link_lost), R.color.orange);
                    } else if (visible) {
                        handler.postDelayed(poll, 4000);
                    }
                    return;
                }
                lostPolls = 0;
                logView.setText(String.join("\n", finalState.lines));
                if (finalState.finished) {
                    finishUpdate(finalState.ok);
                } else if (!finalState.running && ++idlePolls > 5) {
                    // the VPS unit ended without a DONE line (it was killed): do not wait for ever
                    finishUpdate(false);
                } else if (visible) {
                    handler.postDelayed(poll, 2000);
                }
            });
        });
    }

    private void finishUpdate(boolean ok) {
        updating = false;
        showBusy(false);
        showState(getString(ok ? R.string.update_done : R.string.update_failed), ok ? R.color.connected : R.color.orange);
        handler.postDelayed(this::check, 1500);
    }

    // ---- view helpers

    private void showState(String text, int color) {
        stateView.setText(text);
        stateView.setTextColor(getColor(color));
    }

    private void showBusy(boolean on) {
        progress.setVisibility(on ? View.VISIBLE : View.GONE);
        checkButton.setEnabled(!on && !updating);
        checkButton.setAlpha(on || updating ? 0.45f : 1f);
    }

    private void setApplyEnabled(boolean enabled) {
        applyButton.setEnabled(enabled);
        applyButton.setAlpha(enabled ? 1f : 0.45f);
    }

    private String aboutText() {
        String version = "—";
        try {
            PackageInfo info = getPackageManager().getPackageInfo(getPackageName(), 0);
            version = info.versionName + " (" + info.getLongVersionCode() + ")";
        } catch (Exception ignored) { /* shown as a dash */ }
        return "OsEngine Mobile " + version + "\n" + String.format(Locale.US, "Android %s", android.os.Build.VERSION.RELEASE);
    }

    // ---- debug-build preview of every state without a server: adb ... --es preview_update available|current|error|running|done|failed
    private void showPreview(String mode) {
        String check = "INSTALLED main 5842d961 active\nINSTALLED binance 5842d961 active\n"
            + "LATEST d8840047 2026-10-02T13:09:22Z Серверная сборка d8840047 из osengine-vps, 02.10.2026\n"
            + "STATE main update\nSTATE binance update\nRESULT update-available\n";
        switch (mode) {
            case "current":
                showRelease(ServerRelease.parse(check.replace("5842d961", "d8840047").replace("update", "current")
                    .replace("RESULT current-available", "RESULT up-to-date")));
                break;
            case "error":
                showRelease(ServerRelease.parse("INSTALLED main 5842d961 active\nERROR no signed server release found in InterVictor/OsEngineVPS (or GitHub cannot be reached)\n"));
                break;
            default:
                showRelease(ServerRelease.parse(check));
        }
        if (mode.equals("running") || mode.equals("done") || mode.equals("failed")) {
            String log = "STEP 1/3 looking for the newest release\nOK server-d8840047 (note)\n"
                + "STEP 2/3 downloading and verifying the package\nOK signature is valid, build d8840047\n"
                + "STEP 3/3 updating the terminals one by one\nSTEP [main] osengine\n"
                + "[main] STEP 3/4 switching osengine to the new build\n"
                + "[main] OK MCP API answers on 127.0.0.1:6500\n[main] DONE\nSTEP [binance] osengine-binance\n";
            if (mode.equals("done")) log += "[binance] OK MCP API answers on 127.0.0.1:6501\n[binance] DONE\nDONE ok\n";
            if (mode.equals("failed")) log += "[binance] WARN the new build does not answer — rolling back\n[binance] FAIL update rolled back: osengine-binance runs the previous build again\nFAIL [binance] the update failed (the terminal was rolled back); the other terminals were not touched\nDONE fail\n";
            ServerRelease.Progress state = ServerRelease.Progress.parse((mode.equals("running") ? "RUNNING\n" : "IDLE\n") + log);
            logView.setVisibility(View.VISIBLE);
            logView.setText(String.join("\n", state.lines));
            if (mode.equals("running")) {
                showBusy(true);
                showState(getString(R.string.update_running), R.color.orange);
            } else {
                showState(getString(state.ok ? R.string.update_done : R.string.update_failed),
                    state.ok ? R.color.connected : R.color.orange);
            }
        }
    }
}
