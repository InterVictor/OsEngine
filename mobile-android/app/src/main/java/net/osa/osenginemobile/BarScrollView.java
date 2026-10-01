package net.osa.osenginemobile;

import android.content.Context;
import android.util.AttributeSet;
import android.widget.ScrollView;

/** ScrollView whose always-visible bar is shown only when the content really does not fit. */
public class BarScrollView extends ScrollView {
    public BarScrollView(Context context) { super(context); }
    public BarScrollView(Context context, AttributeSet attrs) { super(context, attrs); }
    public BarScrollView(Context context, AttributeSet attrs, int style) { super(context, attrs, style); }

    @Override protected void onLayout(boolean changed, int left, int top, int right, int bottom) {
        super.onLayout(changed, left, top, right, bottom);
        refreshBar();
    }

    @Override protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        refreshBar();
    }

    private void refreshBar() {
        android.view.View child = getChildCount() == 0 ? null : getChildAt(0);
        boolean scrollable = child != null && child.getHeight() > getHeight() - getPaddingTop() - getPaddingBottom();
        if (isVerticalScrollBarEnabled() != scrollable) setVerticalScrollBarEnabled(scrollable);
    }
}
