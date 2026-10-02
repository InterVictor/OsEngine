package net.osa.osenginemobile;

import android.app.Activity;
import android.app.Application;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;

import net.schmizz.sshj.SSHClient;
import net.schmizz.sshj.userauth.UserAuthException;

import java.lang.ref.WeakReference;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Opens the common alert window when an alert arrives and keeps the SSH link alive: when it drops while
 * a screen is open, the app silently logs in again with the device key; only when that is impossible
 * (key revoked, no key, repeated failures) the login screen is shown with an explanation.
 */
public final class OsApp extends Application {
    private static final long WATCH_MS = 10_000;
    private static final int FAILURES_BEFORE_LOGIN = 12;   // about two minutes of silent retries

    private static WeakReference<Activity> resumed = new WeakReference<>(null);
    private final Handler main = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private boolean reconnecting;
    private int failures;
    private final Runnable watch = new Runnable() {
        @Override public void run() {
            check();
            main.postDelayed(this, WATCH_MS);
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        registerActivityLifecycleCallbacks(new ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) {
                resumed = new WeakReference<>(activity);
                main.removeCallbacks(watch);
                main.postDelayed(watch, 1_500);
            }
            @Override public void onActivityPaused(Activity activity) {
                if (resumed.get() == activity) resumed = new WeakReference<>(null);
            }
            @Override public void onActivityCreated(Activity activity, Bundle state) { }
            @Override public void onActivityStarted(Activity activity) { Ime.attach(activity); }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { }
        });
        AlertCenter.addListener(added -> {
            if (!added) return;
            main.post(() -> {
                Activity activity = resumed.get();
                if (activity == null || activity instanceof AlertsActivity) return;
                activity.startActivity(new Intent(activity, AlertsActivity.class));
            });
        });
    }

    /** Runs on the main thread: nothing to do while connected, on the login screen or in the background. */
    private void check() {
        Activity activity = resumed.get();
        if (activity == null) { main.removeCallbacks(watch); return; }   // restarted on the next resume
        if (activity instanceof MainActivity) return;
        if (RemoteSsh.isConnected()) { failures = 0; return; }
        if (reconnecting) return;
        ProfileStore profile = new ProfileStore(this);
        String host = profile.host(), user = profile.user();
        String key = profile.loadPrivateKey(host, user);
        if (key == null) { showLogin(activity, getString(R.string.status_connection_lost_login)); return; }
        reconnecting = true;
        worker.execute(() -> {
            String problem = null;
            boolean login = false;
            try {
                SSHClient ssh = RemoteSsh.connectWithKey(activity, profile, host, user, key);
                RemoteSsh.replace(ssh, host);
                AlertCenter.restartStreams(getApplicationContext());
            } catch (UserAuthException revoked) {
                profile.clearPrivateKey();
                problem = getString(R.string.status_key_revoked);
                login = true;
            } catch (Exception e) {
                problem = e.getMessage();
            }
            String finalProblem = problem;
            boolean forceLogin = login;
            main.post(() -> {
                reconnecting = false;
                if (finalProblem == null) { failures = 0; return; }
                failures++;
                Activity current = resumed.get();
                if (current != null && !(current instanceof MainActivity)
                    && (forceLogin || failures >= FAILURES_BEFORE_LOGIN))
                    showLogin(current, forceLogin ? finalProblem
                        : getString(R.string.status_connection_lost_login) + "\n" + finalProblem);
            });
        });
    }

    private void showLogin(Activity from, String message) {
        failures = 0;
        Intent intent = new Intent(from, MainActivity.class)
            .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra(MainActivity.EXTRA_RELOGIN, message);
        from.startActivity(intent);
    }
}
