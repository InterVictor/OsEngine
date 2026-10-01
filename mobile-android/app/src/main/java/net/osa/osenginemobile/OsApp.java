package net.osa.osenginemobile;

import android.app.Activity;
import android.app.Application;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;

import java.lang.ref.WeakReference;

/** Opens the common alert window over the current screen when a new alert arrives. */
public final class OsApp extends Application {
    private static WeakReference<Activity> resumed = new WeakReference<>(null);

    @Override public void onCreate() {
        super.onCreate();
        registerActivityLifecycleCallbacks(new ActivityLifecycleCallbacks() {
            @Override public void onActivityResumed(Activity activity) { resumed = new WeakReference<>(activity); }
            @Override public void onActivityPaused(Activity activity) {
                if (resumed.get() == activity) resumed = new WeakReference<>(null);
            }
            @Override public void onActivityCreated(Activity activity, Bundle state) { }
            @Override public void onActivityStarted(Activity activity) { }
            @Override public void onActivityStopped(Activity activity) { }
            @Override public void onActivitySaveInstanceState(Activity activity, Bundle state) { }
            @Override public void onActivityDestroyed(Activity activity) { }
        });
        Handler main = new Handler(Looper.getMainLooper());
        AlertCenter.addListener(added -> {
            if (!added) return;
            main.post(() -> {
                Activity activity = resumed.get();
                if (activity == null || activity instanceof AlertsActivity) return;
                activity.startActivity(new Intent(activity, AlertsActivity.class));
            });
        });
    }
}
