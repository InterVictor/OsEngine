package net.osa.osenginemobile;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONObject;

import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.ZoneId;
import java.time.ZoneOffset;

/** Daily profit shown on the terminal tile: how it is measured is chosen in «Настройки». */
final class DayProfit {
    static final String ABSOLUTE = "Absolute";
    static final String PER_CONTRACT = "Percent1Contract";
    static final String DEPOSIT = "DepositPercent";
    private static final String STORE = "view_settings";
    private static final String KEY = "day_profit_mode";

    private DayProfit() { }

    static String mode(Context context) {
        String value = prefs(context).getString(KEY, ABSOLUTE);
        return PER_CONTRACT.equals(value) || DEPOSIT.equals(value) ? value : ABSOLUTE;
    }

    static void setMode(Context context, String mode) {
        prefs(context).edit().putString(KEY, mode).apply();
    }

    static String label(String mode) {
        if (PER_CONTRACT.equals(mode)) return "Процент на контракт";
        if (DEPOSIT.equals(mode)) return "Процент от депозита";
        return "Абсолют";
    }

    static String suffix(String mode) {
        return ABSOLUTE.equals(mode) ? "" : " %";
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(STORE, Context.MODE_PRIVATE);
    }

    /**
     * The server returns the cumulative curve of the journal (closed positions and the current result of
     * open ones) in position order. The profit of the phone's calendar day is the sum of the steps of the
     * points whose time falls on today; server times carry no zone and are UTC.
     */
    static double today(JSONArray points) {
        LocalDate today = LocalDate.now();
        ZoneId zone = ZoneId.systemDefault();
        double previous = 0, sum = 0;
        boolean havePrevious = false;
        for (int i = 0; i < points.length(); i++) {
            JSONObject point = points.optJSONObject(i);
            if (point == null) continue;
            double value = point.optDouble("value", Double.NaN);
            if (Double.isNaN(value)) continue;
            double step = value - (havePrevious ? previous : 0);
            previous = value;
            havePrevious = true;
            try {
                LocalDateTime utc = LocalDateTime.parse(point.optString("time").replaceAll("(\\.\\d{1,9}).*$", "$1"));
                LocalDate day = Instant.ofEpochSecond(utc.toEpochSecond(ZoneOffset.UTC)).atZone(zone).toLocalDate();
                if (day.equals(today)) sum += step;
            } catch (Exception ignored) { /* a point without a valid time is not counted */ }
        }
        return sum;
    }

    static String format(double value, String mode) {
        String text = java.math.BigDecimal.valueOf(value)
            .setScale(ABSOLUTE.equals(mode) ? 2 : 2, java.math.RoundingMode.HALF_UP).toPlainString();
        return (value > 0 ? "+" : "") + text + suffix(mode);
    }
}
