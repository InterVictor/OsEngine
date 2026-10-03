package net.osa.osenginemobile;

import java.io.IOException;
import java.util.HashMap;
import java.util.Map;

final class VpsSnapshotReader {
    // Mirrors VpsMonitor and VpsInstances in the Windows client. Disk is the VPS root filesystem.
    private static final String COMMAND =
        "head -1 /proc/stat | sed 's/^/CPU|/'; "
        + "grep -E '^(MemTotal|MemAvailable):' /proc/meminfo | sed 's/^/MEM|/'; "
        + "df -B1 --output=size,used / | tail -1 | sed 's/^/DISK|/'; "
        + "nproc | sed 's/^/CORES|/'; "
        + "for f in /etc/systemd/system/osengine*.service; do "
        + "[ -f \"$f\" ] || continue; "
        + "svc=$(basename \"$f\" .service); "
        + "state=$(systemctl is-active \"$svc\" 2>/dev/null); "
        + "mem=$(systemctl show \"$svc\" -p MemoryCurrent --value); "
        + "cpu=$(systemctl show \"$svc\" -p CPUUsageNSec --value); "
        + "printf 'SVC|%s|%s|%s|%s\\n' \"$svc\" \"$state\" \"$mem\" \"$cpu\"; "
        + "done; true";

    private long previousTotal;
    private long previousIdle;
    private long previousTimeNs;
    private final Map<String, Long> previousServiceCpu = new HashMap<>();

    VpsSnapshot read() throws IOException {
        return parse(RemoteSsh.run(COMMAND), System.nanoTime());
    }

    VpsSnapshot parse(String output, long timeNs) {
        VpsSnapshot result = new VpsSnapshot();
        long total = 0;
        long idle = 0;
        long memTotal = 0;
        long memAvailable = 0;
        int cores = 0;

        for (String line : output.split("\\R")) {
            if (line.startsWith("CPU|")) {
                String[] parts = line.substring(4).trim().split("\\s+");
                if (parts.length >= 9 && "cpu".equals(parts[0])) {
                    for (int i = 1; i <= 8; i++) total += number(parts[i]);
                    idle = number(parts[4]) + number(parts[5]);
                }
            } else if (line.startsWith("MEM|")) {
                String[] parts = line.substring(4).trim().split("\\s+");
                if (parts.length >= 2 && "MemTotal:".equals(parts[0]))
                    memTotal = number(parts[1]) * 1024;
                if (parts.length >= 2 && "MemAvailable:".equals(parts[0]))
                    memAvailable = number(parts[1]) * 1024;
            } else if (line.startsWith("DISK|")) {
                String[] parts = line.substring(5).trim().split("\\s+");
                if (parts.length >= 2) {
                    long size = number(parts[0]);
                    if (size > 0) result.diskPercent = 100.0 * number(parts[1]) / size;
                    result.diskTotal = size;
                }
            } else if (line.startsWith("CORES|")) {
                cores = (int) number(line.substring(6).trim());
            } else if (line.startsWith("SVC|")) {
                String[] parts = line.split("\\|", -1);
                if (parts.length < 5 || !parts[1].matches("osengine(?:-[a-z0-9-]+)?")) continue;
                VpsSnapshot.Terminal terminal = new VpsSnapshot.Terminal();
                terminal.service = parts[1];
                terminal.name = "osengine".equals(parts[1]) ? "main" : parts[1].substring(9);
                terminal.state = parts[2];
                terminal.memoryBytes = number(parts[3]);
                result.terminals.add(terminal);
            }
        }

        if (previousTotal > 0 && total > previousTotal)
            result.cpuPercent = clamp(100.0 * (1.0 - (double) (idle - previousIdle)
                / (total - previousTotal)));
        previousTotal = total;
        previousIdle = idle;

        result.cores = cores;
        result.ramTotal = memTotal;
        result.ramUsed = Math.max(0, memTotal - memAvailable);
        if (memTotal > 0) result.ramPercent = clamp(100.0 * result.ramUsed / memTotal);

        long elapsedNs = timeNs - previousTimeNs;
        for (String line : output.split("\\R")) {
            if (!line.startsWith("SVC|")) continue;
            String[] parts = line.split("\\|", -1);
            if (parts.length < 5) continue;
            long cpuNs = number(parts[4]);
            for (VpsSnapshot.Terminal terminal : result.terminals) {
                if (!terminal.service.equals(parts[1])) continue;
                Long previous = previousServiceCpu.put(terminal.service, cpuNs);
                if (previous != null && cpuNs >= previous && elapsedNs > 0 && cores > 0)
                    terminal.cpuPercent = clamp(100.0 * (cpuNs - previous)
                        / (elapsedNs * (double) cores));
            }
        }
        previousTimeNs = timeNs;
        result.terminals.sort((a, b) -> {
            if ("main".equals(a.name)) return -1;
            if ("main".equals(b.name)) return 1;
            return a.name.compareToIgnoreCase(b.name);
        });
        return result;
    }

    private static long number(String value) {
        try { return Long.parseLong(value.trim()); }
        catch (NumberFormatException e) { return 0; }
    }

    private static double clamp(double value) { return Math.max(0, Math.min(100, value)); }
}
