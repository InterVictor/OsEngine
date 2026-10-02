package net.osa.osenginemobile;

import android.app.Activity;
import android.os.Build;
import android.view.View;
import android.view.ViewGroup;
import android.view.ViewTreeObserver;
import android.view.WindowInsets;

/**
 * One rule for every screen: while the keyboard is open, views tagged {@link #HIDE} (title block, bottom
 * button bars) disappear so the list or the form gets the whole space above the keyboard; they come back
 * when the keyboard closes. The focused input is scrolled into view by its ScrollView.
 */
final class Ime {
    static final String HIDE = "hide_on_keyboard";
    private static final int MARK = 0x7f0a0000;   // any free id: stores the attached listener

    private Ime() { }

    static <T extends View> T hide(T view) {
        view.setTag(HIDE);
        return view;
    }

    /** Called for every started activity; idempotent. */
    static void attach(Activity activity) {
        View decor = activity.getWindow().getDecorView();
        if (decor.getTag(MARK) != null) return;
        boolean[] open = {false};
        ViewTreeObserver.OnGlobalLayoutListener listener = () -> {
            boolean now = keyboardVisible(decor);
            if (now == open[0]) return;
            open[0] = now;
            apply(decor, now);
        };
        decor.getViewTreeObserver().addOnGlobalLayoutListener(listener);
        decor.setTag(MARK, listener);
    }

    private static boolean keyboardVisible(View decor) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return false;
        WindowInsets insets = decor.getRootWindowInsets();
        return insets != null && insets.isVisible(WindowInsets.Type.ime());
    }

    private static void apply(View view, boolean keyboard) {
        if (HIDE.equals(view.getTag())) view.setVisibility(keyboard ? View.GONE : View.VISIBLE);
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) apply(group.getChildAt(i), keyboard);
        }
    }
}
