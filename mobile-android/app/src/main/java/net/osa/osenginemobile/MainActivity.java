package net.osa.osenginemobile;

import android.app.Activity;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.util.DisplayMetrics;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import net.schmizz.sshj.SSHClient;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public final class MainActivity extends Activity {
    private EditText host;
    private EditText user;
    private EditText password;
    private CheckBox autoConnect;
    private TextView status;
    private Button connect;
    private ProfileStore profile;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        ScrollView scrollRoot = findViewById(R.id.scroll_root);
        scrollRoot.setOnApplyWindowInsetsListener((view, insets) -> {
            int top;
            int bottom;
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
        scrollRoot.requestApplyInsets();

        host = findViewById(R.id.ssh_host);
        user = findViewById(R.id.ssh_user);
        password = findViewById(R.id.ssh_password);
        autoConnect = findViewById(R.id.auto_connect);
        status = findViewById(R.id.connection_status);
        connect = findViewById(R.id.connect_button);
        profile = new ProfileStore(this);

        host.setText(profile.host());
        user.setText(profile.user());
        autoConnect.setChecked(profile.autoConnect());

        autoConnect.setOnCheckedChangeListener((button, checked) -> {
            profile.setAutoConnect(checked);
            status.setText(R.string.status_not_connected);
        });
        connect.setOnClickListener(view -> validateForm());

        // Keep the same compact form on a phone and centre it on a wide tablet.
        LinearLayout column = findViewById(R.id.content_column);
        DisplayMetrics metrics = getResources().getDisplayMetrics();
        int maxWidth = Math.round(480 * metrics.density);
        ViewGroup.LayoutParams layout = column.getLayoutParams();
        layout.width = Math.min(metrics.widthPixels - Math.round(48 * metrics.density), maxWidth);
        column.setLayoutParams(layout);

        if (profile.autoConnect()) {
            String saved = profile.loadPassword(profile.host(), profile.user());
            if (saved == null) status.setText(R.string.status_auto_needs_password);
            else beginConnect(profile.host(), profile.user(), saved);
        }
    }

    @Override
    protected void onPause() {
        super.onPause();
        profile.saveForm(host.getText().toString().trim(),
            user.getText().toString().trim(), autoConnect.isChecked());
    }

    @Override
    protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    private void validateForm() {
        if (host.getText().toString().trim().isEmpty()) {
            host.setError(getString(R.string.error_host));
            host.requestFocus();
            return;
        }
        if (user.getText().toString().trim().isEmpty()) {
            user.setError(getString(R.string.error_user));
            user.requestFocus();
            return;
        }
        if (password.length() == 0) {
            password.setError(getString(R.string.error_password));
            password.requestFocus();
            return;
        }
        String targetHost = host.getText().toString().trim();
        String targetUser = user.getText().toString().trim();
        profile.saveForm(targetHost, targetUser, autoConnect.isChecked());
        beginConnect(targetHost, targetUser, password.getText().toString());
    }

    private void beginConnect(String targetHost, String targetUser, String secret) {
        connect.setEnabled(false);
        autoConnect.setEnabled(false);
        status.setText(R.string.status_connecting);
        worker.execute(() -> {
            try {
                SSHClient ssh = RemoteSsh.connect(this, profile, targetHost, targetUser, secret);
                RemoteSsh.replace(ssh, targetHost);
                if (profile.autoConnect()) {
                    try { profile.savePassword(targetHost, targetUser, secret); }
                    catch (Exception e) { profile.setAutoConnect(false); }
                }
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    password.setText("");
                    connect.setEnabled(true);
                    autoConnect.setEnabled(true);
                    autoConnect.setChecked(profile.autoConnect());
                    startActivity(new Intent(this, TerminalsActivity.class));
                });
            } catch (Exception e) {
                String message = e.getMessage() == null ? getString(R.string.status_connection_failed)
                    : e.getMessage();
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    connect.setEnabled(true);
                    autoConnect.setEnabled(true);
                    status.setText(message);
                });
            }
        });
    }
}
