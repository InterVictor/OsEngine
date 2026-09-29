package net.osa.osenginemobile;

import android.app.Activity;
import android.os.Build;
import android.util.DisplayMetrics;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;

final class ScreenLayout {
    private ScreenLayout() { }

    static void apply(Activity activity, int maxWidthDp) {
        View root = activity.findViewById(R.id.scroll_root);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
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
        root.requestApplyInsets();
        View column = activity.findViewById(R.id.content_column);
        DisplayMetrics metrics = activity.getResources().getDisplayMetrics();
        ViewGroup.LayoutParams layout = column.getLayoutParams();
        layout.width = Math.min(metrics.widthPixels - Math.round(48 * metrics.density),
            Math.round(maxWidthDp * metrics.density));
        column.setLayoutParams(layout);
    }
}
