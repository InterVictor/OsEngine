package net.osa.osenginemobile;

import org.json.JSONArray;

/** Exact nine-line format used by NonTradePeriods.GetFullSaveArray on the VPS. */
final class NonTradePeriodsData {
    private final String[][] fields = new String[9][];

    NonTradePeriodsData(JSONArray values) {
        if (values == null || values.length() != 9)
            throw new IllegalArgumentException("Ожидалось девять строк настроек");
        for (int i = 0; i < 9; i++) {
            String value = values.optString(i, null);
            if (value == null) throw new IllegalArgumentException("Пустая строка настроек " + i);
            fields[i] = value.split("@", -1);
            if (fields[i].length < (i == 0 ? 7 : 15))
                throw new IllegalArgumentException("Неполная строка настроек " + i);
        }
        validate();
    }

    NonTradePeriodsData copy() { return new NonTradePeriodsData(toJson()); }

    boolean tradeDay(int day) { return bool(fields[0][day]); }
    void setTradeDay(int day, boolean value) { fields[0][day] = flag(value); }
    boolean enabled(int group, int period) { return bool(fields[group][period * 3]); }
    void setEnabled(int group, int period, boolean value) {
        fields[group][period * 3] = flag(value);
    }
    String time(int group, int period, boolean end) {
        return fields[group][period * 3 + (end ? 2 : 1)];
    }
    void setTime(int group, int period, boolean end, String value) {
        parseTime(value);
        fields[group][period * 3 + (end ? 2 : 1)] = value;
    }

    void validate() {
        for (int day = 0; day < 7; day++) bool(fields[0][day]);
        for (int group = 1; group <= 8; group++) {
            for (int period = 0; period < 5; period++) {
                boolean active = enabled(group, period);
                int start = parseTime(time(group, period, false));
                int end = parseTime(time(group, period, true));
                if (active && start >= end)
                    throw new IllegalArgumentException("Период " + (period + 1)
                        + ": начало должно быть раньше окончания");
            }
        }
    }

    JSONArray toJson() {
        JSONArray result = new JSONArray();
        for (String[] line : fields) result.put(String.join("@", line));
        return result;
    }

    String toText() {
        StringBuilder result = new StringBuilder();
        JSONArray values = toJson();
        for (int i = 0; i < 9; i++) result.append(values.optString(i)).append('\n');
        return result.toString();
    }

    static NonTradePeriodsData fromText(String text) {
        String[] lines = text.replace("\r", "").split("\n", -1);
        if (lines.length < 9) throw new IllegalArgumentException("Файл должен содержать девять строк");
        JSONArray values = new JSONArray();
        for (int i = 0; i < 9; i++) values.put(lines[i]);
        return new NonTradePeriodsData(values);
    }

    void applyMoexPreset(boolean futures) {
        for (int period = 0; period < 5; period++) setEnabled(1, period, false);
        for (int day = 0; day < 7; day++) {
            setTradeDay(day, true);
            int group = day + 2;
            if (day < 5) {
                preset(group, 0, true, "0:0:0:0", futures ? "8:52:0:0" : "6:52:0:0");
                preset(group, 1, !futures, futures ? "9:48:0:0" : "8:48:0:0",
                    futures ? "10:2:0:0" : "9:2:0:0");
                preset(group, 2, !futures, futures ? "13:58:0:0" : "18:58:0:0",
                    futures ? "14:7:0:0" : "19:2:0:0");
                preset(group, 3, !futures, futures ? "18:48:0:0" : "23:48:0:0",
                    futures ? "19:7:0:0" : "24:0:0:0");
                if (futures) preset(group, 4, true, "23:48:0:0", "24:0:0:0");
                else setEnabled(group, 4, false);
            } else {
                preset(group, 0, true, "0:0:0:0", "9:52:0:0");
                preset(group, 1, true, "18:58:0:0", "24:0:0:0");
                for (int period = 2; period < 5; period++) setEnabled(group, period, false);
            }
        }
        validate();
    }

    private void preset(int group, int period, boolean enabled, String start, String end) {
        setEnabled(group, period, enabled);
        setTime(group, period, false, start);
        setTime(group, period, true, end);
    }

    private static String flag(boolean value) { return value ? "True" : "False"; }
    private static boolean bool(String value) {
        if ("True".equalsIgnoreCase(value)) return true;
        if ("False".equalsIgnoreCase(value)) return false;
        throw new IllegalArgumentException("Некорректное значение флажка");
    }

    private static int parseTime(String value) {
        String[] parts = value.split(":", -1);
        if (parts.length != 4) throw new IllegalArgumentException("Время: ч:м:с:мс");
        try {
            int hour = Integer.parseInt(parts[0]);
            int minute = Integer.parseInt(parts[1]);
            int second = Integer.parseInt(parts[2]);
            int millisecond = Integer.parseInt(parts[3]);
            if (hour < 0 || hour > 24 || minute < 0 || minute > 59
                || second < 0 || second > 59 || millisecond < 0 || millisecond > 999
                || hour == 24 && (minute != 0 || second != 0 || millisecond != 0))
                throw new NumberFormatException();
            return ((hour * 60 + minute) * 60 + second) * 1000 + millisecond;
        } catch (NumberFormatException e) {
            throw new IllegalArgumentException("Время: ч:м:с:мс (0–24 часа)");
        }
    }
}
