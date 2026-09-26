// Several independent OsEngine terminals on one VPS: discovery and control over SSH.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // One terminal = one systemd service with its own folder, MCP port and MCP key:
    //   main   -> service osengine,        /opt/osengine,        port 6500 (the original single-terminal layout)
    //   <name> -> service osengine-<name>, /opt/osengine-<name>, port 6501, 6502, ...
    internal sealed class VpsInstance
    {
        public string Name { get; set; }
        public string Service { get; set; }
        public int Port { get; set; }
        public string DataRoot { get; set; }
        public string KeyFile { get; set; }
        public string State { get; set; }
        public long MemoryBytes { get; set; }

        public bool IsMain => string.Equals(Name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase);
        public bool IsActive => State == "active";
        public string BaseFolder => VpsInstances.BaseFolderFor(Name);
        public string MemoryText => MemoryBytes > 0 ? (MemoryBytes / 1024.0 / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " MB" : "—";
    }

    internal static class VpsInstances
    {
        public const int MainPort = 6500;

        public static string ServiceFor(string name) =>
            string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase) ? "osengine" : "osengine-" + name;

        public static string BaseFolderFor(string name) => "/opt/" + ServiceFor(name);

        // short, file-name and service-name safe
        public static bool IsValidName(string name) =>
            !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,19}$")
            && !string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase)
            && name != "removed";

        // Reads every /etc/systemd/system/osengine*.service: the terminal's data root, MCP port and key file come
        // from its ExecStart line, so the hand-written unit of the first VPS is recognized as well.
        public static async Task<List<VpsInstance>> ListAsync(Func<string, Task<string>> run)
        {
            const string script =
                "for f in /etc/systemd/system/osengine*.service; do " +
                "[ -f \"$f\" ] || continue; " +
                "svc=$(basename \"$f\" .service); " +
                "ex=$(grep '^ExecStart=' \"$f\"); " +
                "root=$(echo \"$ex\" | sed -n 's/.*--root \\([^ ]*\\).*/\\1/p'); " +
                "port=$(echo \"$ex\" | sed -n 's/.*--mcp-port \\([0-9]*\\).*/\\1/p'); " +
                "key=$(echo \"$ex\" | sed -n 's/.*--mcp-key-file \\([^ ]*\\).*/\\1/p'); " +
                "state=$(systemctl is-active \"$svc\"); " +
                "mem=$(systemctl show \"$svc\" -p MemoryCurrent --value); " +
                "echo \"$svc|$port|$root|$key|$state|$mem\"; " +
                "done; true";

            string output = await run(script).ConfigureAwait(false);
            List<VpsInstance> result = new List<VpsInstance>();

            foreach (string line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 6 || !int.TryParse(parts[1], out int port)) continue;

                string service = parts[0];
                string name = service == "osengine" ? VpsRemoteSession.MainInstance
                    : service.StartsWith("osengine-", StringComparison.Ordinal) ? service.Substring("osengine-".Length) : null;
                if (name == null) continue;

                result.Add(new VpsInstance
                {
                    Name = name,
                    Service = service,
                    Port = port,
                    DataRoot = parts[2],
                    KeyFile = parts[3],
                    State = parts[4],
                    MemoryBytes = long.TryParse(parts[5], out long memory) ? memory : 0
                });
            }

            return result.OrderBy(i => i.IsMain ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static int NextFreePort(IEnumerable<VpsInstance> existing)
        {
            int port = MainPort + 1;
            HashSet<int> used = new HashSet<int>(existing.Select(i => i.Port));
            while (used.Contains(port)) port++;
            return port;
        }

        public static async Task<string> ReadKeyAsync(Func<string, Task<string>> run, VpsInstance instance)
        {
            return (await run("cat " + Quote(instance.KeyFile)).ConfigureAwait(false)).Trim();
        }

        public static Task StartAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl start " + instance.Service);

        public static Task StopAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl stop " + instance.Service);

        // stop may hang on a stuck process — systemd kills it after TimeoutStopSec (30 s) anyway
        public static Task RestartAsync(Func<string, Task<string>> run, VpsInstance instance) =>
            run("systemctl restart " + instance.Service);

        // Removes the service. The data (robots, settings, journals, MCP key) is not deleted but moved to
        // /opt/osengine-removed/<name>-<time> — only the build (app) is dropped, it can be reinstalled any time.
        public static Task<string> RemoveAsync(Func<string, Task<string>> run, VpsInstance instance)
        {
            if (instance.IsMain) throw new InvalidOperationException("The main terminal cannot be removed");

            string trash = $"/opt/osengine-removed/{instance.Name}-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
            string script =
                "set -e; " +
                $"systemctl stop {instance.Service} || true; " +
                $"systemctl disable {instance.Service} >/dev/null 2>&1 || true; " +
                $"rm -f /etc/systemd/system/{instance.Service}.service; " +
                "systemctl daemon-reload; " +
                $"mkdir -p /opt/osengine-removed; " +
                $"rm -rf {Quote(instance.BaseFolder + "/app")}; " +
                $"mv {Quote(instance.BaseFolder)} {Quote(trash)}; " +
                $"echo {Quote(trash)}";

            return run(script);
        }

        private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    }
}
