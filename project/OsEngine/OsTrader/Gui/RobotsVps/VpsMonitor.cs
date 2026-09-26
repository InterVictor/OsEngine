// VPS load monitoring for Robots.VPS: CPU, RAM, disk read over SSH from /proc (works even when OsEngine hangs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal sealed class VpsMetrics
    {
        public DateTime Time;
        public double CpuPercent = double.NaN;   // NaN on the first sample: CPU needs two readings
        public long RamTotal;
        public long RamUsed;
        public long SwapTotal;
        public long SwapUsed;
        public long DiskTotal;
        public long DiskUsed;
        public double Load1;
        public int Cores;
        public TimeSpan Uptime;

        public double RamPercent => RamTotal > 0 ? 100.0 * RamUsed / RamTotal : 0;
        public double DiskPercent => DiskTotal > 0 ? 100.0 * DiskUsed / DiskTotal : 0;
    }

    internal sealed class VpsMonitor
    {
        // one round trip: CPU counters, memory, root disk, load average, uptime, CPU count
        private const string Command =
            "head -1 /proc/stat; " +
            "grep -E '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo; " +
            "df -B1 --output=size,used / | tail -1; " +
            "cat /proc/loadavg; " +
            "cut -d' ' -f1 /proc/uptime; " +
            "nproc";

        private long _previousIdle;
        private long _previousTotal;

        // CPU time of each terminal service (systemd CPUUsageNSec) at the previous sample
        private readonly Dictionary<string, (long Nanoseconds, DateTime Time)> _previousServiceCpu =
            new Dictionary<string, (long, DateTime)>(StringComparer.OrdinalIgnoreCase);

        public async Task<VpsMetrics> SampleAsync(Func<string, Task<string>> run)
        {
            string[] lines = (await run(Command).ConfigureAwait(false))
                .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

            VpsMetrics metrics = new VpsMetrics { Time = DateTime.Now };
            long memTotal = 0, memAvailable = 0, swapTotal = 0, swapFree = 0;

            foreach (string line in lines)
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts[0] == "cpu" && parts.Length >= 8)
                {
                    // user nice system idle iowait irq softirq steal
                    long[] values = parts.Skip(1).Take(8).Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    long idle = values[3] + values[4];
                    long total = values.Sum();

                    if (_previousTotal > 0 && total > _previousTotal)
                    {
                        metrics.CpuPercent = 100.0 * (1.0 - (double)(idle - _previousIdle) / (total - _previousTotal));
                    }

                    _previousIdle = idle;
                    _previousTotal = total;
                }
                else if (parts[0] == "MemTotal:") memTotal = long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
                else if (parts[0] == "MemAvailable:") memAvailable = long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
                else if (parts[0] == "SwapTotal:") swapTotal = long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
                else if (parts[0] == "SwapFree:") swapFree = long.Parse(parts[1], CultureInfo.InvariantCulture) * 1024;
                else if (parts.Length == 2 && long.TryParse(parts[0], out long diskSize) && long.TryParse(parts[1], out long diskUsed))
                {
                    metrics.DiskTotal = diskSize;
                    metrics.DiskUsed = diskUsed;
                }
                else if (parts.Length >= 5 && parts[3].Contains('/'))
                {
                    metrics.Load1 = double.Parse(parts[0], CultureInfo.InvariantCulture);
                }
                else if (parts.Length == 1 && parts[0].Contains('.'))
                {
                    metrics.Uptime = TimeSpan.FromSeconds(double.Parse(parts[0], CultureInfo.InvariantCulture));
                }
                else if (parts.Length == 1 && int.TryParse(parts[0], out int cores))
                {
                    metrics.Cores = cores;
                }
            }

            metrics.RamTotal = memTotal;
            metrics.RamUsed = memTotal - memAvailable;
            metrics.SwapTotal = swapTotal;
            metrics.SwapUsed = swapTotal - swapFree;
            return metrics;
        }

        // CPU load of one terminal in % of the whole VPS since the previous call (NaN on the first call).
        public double ServiceCpuPercent(string service, long cpuNanoseconds, int cores)
        {
            DateTime now = DateTime.UtcNow;
            double result = double.NaN;

            if (_previousServiceCpu.TryGetValue(service, out (long Nanoseconds, DateTime Time) previous)
                && cpuNanoseconds >= previous.Nanoseconds && cores > 0)
            {
                double elapsedNs = (now - previous.Time).TotalMilliseconds * 1_000_000;
                if (elapsedNs > 0) result = 100.0 * (cpuNanoseconds - previous.Nanoseconds) / (elapsedNs * cores);
            }

            _previousServiceCpu[service] = (cpuNanoseconds, now);
            return result;
        }

        public static string FormatBytes(long bytes)
        {
            double gb = bytes / 1024.0 / 1024.0 / 1024.0;
            return gb >= 1 ? gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : (bytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }
    }

    // Threshold alarm with hysteresis: fires once when the value goes above the limit for the required number of
    // samples in a row, re-arms only after it drops 5 points below the limit (no alert storm around the limit).
    internal sealed class VpsAlarm
    {
        private readonly double _limit;
        private readonly int _samplesInRow;
        private int _above;
        private bool _fired;

        public VpsAlarm(double limit, int samplesInRow)
        {
            _limit = limit;
            _samplesInRow = samplesInRow;
        }

        public double Limit => _limit;

        // true exactly once per crossing
        public bool Check(double value)
        {
            if (double.IsNaN(value)) return false;

            if (value >= _limit)
            {
                _above++;
                if (!_fired && _above >= _samplesInRow)
                {
                    _fired = true;
                    return true;
                }
            }
            else
            {
                _above = 0;
                if (value < _limit - 5) _fired = false;
            }

            return false;
        }
    }
}
