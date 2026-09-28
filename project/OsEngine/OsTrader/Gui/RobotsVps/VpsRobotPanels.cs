using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Robot buttons that make sense only on this computer. A robot on the VPS writes its data files there, but a
    /// panel built from them has to be shown here: the files are brought over SSH into a local mirror and the robot's
    /// own local tool builds the panel. Now: "Открыть панель: RegimeMonitor" of FF144Regime (and its predecessors).
    /// </summary>
    internal static class VpsRobotPanels
    {
        private static readonly string[] RegimeFiles = { "regime.txt", "regime_log.csv", "spectrum.csv", "cycle_profile.csv" };

        /// <summary>true if the button is handled here instead of being pressed on the VPS</summary>
        public static bool IsLocalButton(string buttonName) =>
            buttonName != null && buttonName.IndexOf("RegimeMonitor", StringComparison.OrdinalIgnoreCase) >= 0;

        public static async Task OpenRegimePanelAsync(RemoteMcpClient client, string botId, Action<string> status)
        {
            string instanceName = VpsRemoteSession.GetInstanceName(client);
            VpsInstance instance = VpsRemoteSession.Instances.FirstOrDefault(i => string.Equals(i.Name, instanceName, StringComparison.OrdinalIgnoreCase));

            if (instance == null || VpsRemoteSession.SshCredentials == null || VpsRemoteSession.SshRun == null)
            {
                throw new InvalidOperationException("Connect to the VPS over SSH in the VPS window first");
            }

            // where the robot writes: the folder of its "Flag file"; its tools on this computer: "Tools folder"
            Dictionary<string, string> parameters = await ReadStringParametersAsync(client, botId).ConfigureAwait(true);
            string flagFile = parameters.TryGetValue("Flag file", out string flag) ? flag : null;
            string toolsFolder = parameters.TryGetValue("Tools folder", out string tools) ? tools : null;

            if (string.IsNullOrWhiteSpace(flagFile))
            {
                throw new InvalidOperationException("The robot has no \"Flag file\" parameter");
            }

            string relativeFolder = RemoteParent(flagFile.Replace('\\', '/').Trim());
            string dataRoot = instance.BaseFolder + "/data";
            string remoteFolder = relativeFolder.StartsWith("/", StringComparison.Ordinal)
                ? relativeFolder
                : dataRoot + (relativeFolder.Length > 0 ? "/" + relativeFolder : "");

            // local mirror: VpsData\<terminal>\<the robot's folder inside data>
            string mirrorRelative = remoteFolder.StartsWith(dataRoot + "/", StringComparison.Ordinal)
                ? remoteFolder.Substring(dataRoot.Length + 1)
                : remoteFolder.Trim('/');
            string localFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VpsData", instance.Name,
                mirrorRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(localFolder);

            status?.Invoke("Taking the robot data from the VPS...");

            using (VpsFileService files = new VpsFileService(VpsRemoteSession.SshCredentials, VpsRemoteSession.SshRun, VpsRemoteSession.Instances))
            {
                await files.ConnectAsync().ConfigureAwait(true);
                List<VpsFileEntry> entries = (await files.ListAsync(remoteFolder).ConfigureAwait(true))
                    .Where(e => !e.IsDirectory && RegimeFiles.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (entries.Count == 0)
                {
                    throw new InvalidOperationException("No data of the robot yet in " + remoteFolder);
                }

                await files.DownloadAsync(entries, localFolder, null).ConfigureAwait(true);
            }

            string monitor = FindMonitor(toolsFolder);

            if (monitor == null)
            {
                throw new FileNotFoundException("RegimeMonitor.exe not found on this computer (robot parameter \"Tools folder\": "
                    + (toolsFolder ?? "") + "). The data is in " + localFolder);
            }

            status?.Invoke("Building the panel...");
            string html = Path.Combine(localFolder, "regime_monitor.html");

            await Task.Run(() =>
            {
                ProcessStartInfo info = new ProcessStartInfo(monitor)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(monitor)
                };
                info.ArgumentList.Add(localFolder);
                info.ArgumentList.Add(html);

                using (Process process = Process.Start(info))
                {
                    if (!process.WaitForExit(120000))
                    {
                        process.Kill();
                        throw new TimeoutException("RegimeMonitor did not finish in 2 minutes");
                    }
                }
            }).ConfigureAwait(true);

            if (!File.Exists(html))
            {
                throw new InvalidOperationException("RegimeMonitor did not build " + html);
            }

            Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
            status?.Invoke("Panel opened: " + html);
        }

        // the robot's tools folder, relative to this OsEngine like on the VPS; else the robots' folders
        private static string FindMonitor(string toolsFolder)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            List<string> candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(toolsFolder))
            {
                string folder = toolsFolder.Replace('/', Path.DirectorySeparatorChar);
                candidates.Add(Path.IsPathRooted(folder) ? folder : Path.Combine(baseDir, folder));
            }

            string robots = Path.Combine(baseDir, "Custom", "Robots");
            candidates.Add(robots);

            foreach (string folder in candidates.Where(Directory.Exists))
            {
                string direct = Path.Combine(folder, "RegimeMonitor.exe");
                if (File.Exists(direct)) return direct;
            }

            return Directory.Exists(robots)
                ? Directory.EnumerateFiles(robots, "RegimeMonitor.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }

        private static string RemoteParent(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? (slash == 0 ? "/" : "") : path.Substring(0, slash);
        }

        private static async Task<Dictionary<string, string>> ReadStringParametersAsync(RemoteMcpClient client, string botId)
        {
            JsonElement result = await client.CallToolAsync("bot_get_params", new { bot_id = botId }).ConfigureAwait(true);
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            JsonElement list = result.TryGetProperty("parameters", out JsonElement p) ? p : result;
            if (list.ValueKind != JsonValueKind.Array) return values;

            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    values[name.GetString()] = value.GetString();
                }
            }

            return values;
        }
    }
}
