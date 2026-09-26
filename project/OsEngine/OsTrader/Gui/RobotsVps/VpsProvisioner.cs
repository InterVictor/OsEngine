// Robots.VPS server provisioning: deploy OsEngine on a VPS and register this computer's SSH key.
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Everything runs over plain SSH/SFTP with the root login, so it works on a fresh server where
    // OsEngine (and its MCP API) does not exist yet.
    internal sealed class VpsProvisioner
    {
        // Files shipped next to OsEngine.exe: the setup script (copied by the build) and the headless
        // linux-x64 build package (produced in D:\ff-research\headless, not stored in git — ~60 MB).
        public const string PackageFolder = "VpsServer";
        public const string ScriptFileName = "osengine-setup.sh";
        public const string PackageFileName = "osengine-headless-linux-x64.tgz";

        private const string RemoteScript = "/tmp/osengine-setup.sh";
        private const string RemotePackage = "/tmp/osengine-app.tgz";
        private const string RemoteCustom = "/tmp/osengine-custom.tgz";

        private readonly VpsSshCredentials _credentials;
        private readonly Action<string> _log;

        public VpsProvisioner(VpsSshCredentials credentials, Action<string> log)
        {
            _credentials = credentials;
            _log = log;
        }

        public static string LocalPath(string fileName) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, PackageFolder, fileName);

        // Uploads what the server is missing and runs osengine-setup.sh, showing its progress in the log.
        // Safe on a working server: the script only fills in what is absent ("repair" mode).
        // instanceName: VpsRemoteSession.MainInstance for the main terminal, otherwise an extra terminal
        // (/opt/osengine-<name>, service osengine-<name>, its own MCP port).
        public Task DeployAsync(CancellationToken cancel) =>
            DeployAsync(VpsRemoteSession.MainInstance, VpsInstances.MainPort, cancel);

        public async Task DeployAsync(string instanceName, int mcpPort, CancellationToken cancel)
        {
            string baseFolder = VpsInstances.BaseFolderFor(instanceName);
            string environment = $"OSENGINE_BASE={baseFolder} OSENGINE_SERVICE={VpsInstances.ServiceFor(instanceName)} OSENGINE_MCP_PORT={mcpPort}";

            string scriptPath = LocalPath(ScriptFileName);
            if (!File.Exists(scriptPath)) throw new FileNotFoundException("Setup script not found", scriptPath);

            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);

            using SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, _log, cancel).ConfigureAwait(false);

            _log("Connected to " + _credentials.HostId + " as " + _credentials.User);

            try
            {
                // Windows checkouts may turn LF into CRLF — bash needs LF.
                string script = File.ReadAllText(scriptPath).Replace("\r\n", "\n");
                await UploadAsync(sftp, new MemoryStream(Encoding.UTF8.GetBytes(script)), RemoteScript, "setup script", cancel).ConfigureAwait(false);

                // an extra terminal copies the build and the robot scripts from the main one on the server itself
                bool appInstalled = Run(ssh, $"test -x {baseFolder}/app/OsEngine || test -x /opt/osengine/app/OsEngine") == 0;
                string packageArg = "";

                if (appInstalled)
                {
                    _log("OsEngine build is already installed on the server — package upload skipped");
                }
                else
                {
                    string packagePath = LocalPath(PackageFileName);
                    if (!File.Exists(packagePath)) throw new FileNotFoundException("Server build package not found", packagePath);

                    using FileStream package = File.OpenRead(packagePath);
                    await UploadAsync(sftp, package, RemotePackage, "OsEngine build", cancel).ConfigureAwait(false);
                    packageArg = RemotePackage;
                }

                bool customPresent = Run(ssh, $"test -d {baseFolder}/data/Custom/Robots || test -d /opt/osengine/data/Custom/Robots") == 0;
                string customArg = "";
                string customFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Custom");

                if (customPresent)
                {
                    _log("Robot scripts (Custom) are already on the server — upload skipped");
                }
                else if (Directory.Exists(customFolder))
                {
                    using MemoryStream custom = PackFolder(customFolder);
                    await UploadAsync(sftp, custom, RemoteCustom, "robot scripts (Custom)", cancel).ConfigureAwait(false);
                    customArg = RemoteCustom;
                }

                _log("Running the setup script on the server...");
                int exitCode = await RunStreamingAsync(ssh, $"{environment} bash {RemoteScript} '{packageArg}' '{customArg}'", cancel).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Setup script failed (exit code {exitCode}) — see the lines above");
                }

                _log("Server is ready");
            }
            finally
            {
                Run(ssh, $"rm -f {RemoteScript} {RemotePackage} {RemoteCustom}");
            }
        }

        public const string UpdateScriptFileName = "osengine-update.sh";
        private const string RemoteUpdateScript = "/tmp/osengine-update.sh";

        // SHA-256 of the local build package, first 8 chars — the same short id the server shows per terminal.
        public static string LocalPackageVersion()
        {
            string path = LocalPath(PackageFileName);
            if (!File.Exists(path)) return null;

            using FileStream stream = File.OpenRead(path);
            byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 8);
        }

        // Uploads the local build package once and updates the terminals one by one with osengine-update.sh
        // (new build next to the old one, switch, health check, automatic rollback). Terminals that already run
        // this package are skipped. Throws if any terminal failed (the others are still updated).
        public async Task UpdateBuildAsync(IReadOnlyList<VpsInstance> instances, CancellationToken cancel)
        {
            string scriptPath = LocalPath(UpdateScriptFileName);
            string packagePath = LocalPath(PackageFileName);
            if (!File.Exists(scriptPath)) throw new FileNotFoundException("Update script not found", scriptPath);
            if (!File.Exists(packagePath)) throw new FileNotFoundException("Server build package not found", packagePath);

            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);
            using SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, _log, cancel).ConfigureAwait(false);

            List<string> failed = new List<string>();

            try
            {
                string script = File.ReadAllText(scriptPath).Replace("\r\n", "\n");
                await UploadAsync(sftp, new MemoryStream(Encoding.UTF8.GetBytes(script)), RemoteUpdateScript, "update script", cancel).ConfigureAwait(false);

                using (FileStream package = File.OpenRead(packagePath))
                {
                    await UploadAsync(sftp, package, RemotePackage, "OsEngine build", cancel).ConfigureAwait(false);
                }

                foreach (VpsInstance instance in instances)
                {
                    _log($"=== Updating terminal \"{instance.Name}\" ===");
                    int exitCode = await RunStreamingAsync(ssh,
                        $"{InstanceEnvironment(instance)} bash {RemoteUpdateScript} {RemotePackage}", cancel).ConfigureAwait(false);

                    if (exitCode != 0) failed.Add(instance.Name);
                }
            }
            finally
            {
                Run(ssh, $"rm -f {RemoteUpdateScript} {RemotePackage}");
            }

            if (failed.Count > 0)
            {
                throw new InvalidOperationException("Update failed for: " + string.Join(", ", failed) + " — see the lines above");
            }
        }

        // Puts robot scripts (*.cs) into Custom/Robots of a terminal. A replaced script is kept in
        // Custom/Robots-backup/<name>-<time>.cs; its line in the robot description cache (BotsDescription.txt)
        // is removed so "Add bot" shows the new sources/indicators. Returns the robot class names.
        // The terminal must be restarted afterwards to compile the new code.
        public async Task<List<string>> UploadRobotsAsync(VpsInstance instance, IReadOnlyList<string> files, CancellationToken cancel)
        {
            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);
            using SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, _log, cancel).ConfigureAwait(false);

            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string remoteDir = "/tmp/osengine-robots-" + stamp;
            List<string> classNames = new List<string>();

            try
            {
                sftp.CreateDirectory(remoteDir);

                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    classNames.Add(Path.GetFileNameWithoutExtension(file));

                    using FileStream source = File.OpenRead(file);
                    await UploadAsync(sftp, source, remoteDir + "/" + fileName, fileName, cancel).ConfigureAwait(false);
                }

                string data = instance.DataRoot;
                string script =
                    "set -e\n" +
                    $"R='{data}/Custom/Robots'; B='{data}/Custom/Robots-backup'; D='{data}/BotsDescription.txt'\n" +
                    "mkdir -p \"$R\" \"$B\"\n" +
                    $"for f in {remoteDir}/*.cs; do\n" +
                    "  n=$(basename \"$f\"); c=${n%.cs}\n" +
                    $"  if [ -f \"$R/$n\" ]; then cp \"$R/$n\" \"$B/$c-{stamp}.cs\"; echo \"OK $n replaced (previous copy: Custom/Robots-backup/$c-{stamp}.cs)\";\n" +
                    "  else echo \"OK $n added\"; fi\n" +
                    "  cp \"$f\" \"$R/$n\"\n" +
                    "  [ -f \"$D\" ] && sed -i \"/^$c&/d\" \"$D\"\n" +
                    "done\n" +
                    "chown -R osengine:osengine \"$R\" \"$B\"\n" +
                    "[ -f \"$D\" ] && chown osengine:osengine \"$D\"\n" +
                    "true\n";

                int exitCode = await RunStreamingAsync(ssh, "bash -c " + ShellQuote(script), cancel).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Could not put the robot scripts in place (exit code {exitCode})");
                }
            }
            finally
            {
                Run(ssh, $"rm -rf {remoteDir}");
            }

            return classNames;
        }

        // Deletes the terminal's OsEngine log files except today's (those are still being written) and trims the
        // systemd journal to the last 7 days. Returns a short report.
        public async Task<string> CleanLogsAsync(VpsInstance instance, CancellationToken cancel)
        {
            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);

            string log = instance.DataRoot + "/Engine/Log";
            string script =
                $"L='{log}'\n" +
                "before=$(du -sk \"$L\" 2>/dev/null | cut -f1)\n" +
                "count=$(find \"$L\" -type f ! -newermt \"$(date +%Y-%m-%d)\" 2>/dev/null | wc -l)\n" +
                "find \"$L\" -type f ! -newermt \"$(date +%Y-%m-%d)\" -delete 2>/dev/null\n" +
                "after=$(du -sk \"$L\" 2>/dev/null | cut -f1)\n" +
                "jb=$(journalctl --disk-usage | grep -o '[0-9.]*[KMG]' | head -1)\n" +
                "journalctl --vacuum-time=7d >/dev/null 2>&1\n" +
                "ja=$(journalctl --disk-usage | grep -o '[0-9.]*[KMG]' | head -1)\n" +
                "echo \"OsEngine logs: $count files deleted, $((before/1024)) MB -> $((after/1024)) MB; systemd journal: $jb -> $ja\"\n";

            using SshCommand command = ssh.RunCommand("bash -c " + ShellQuote(script));
            return command.Result.Trim();
        }

        // Reboots the whole VPS. The command returns at once; the SSH tunnel reconnects by itself and the
        // terminals start with the server (their services are enabled).
        public async Task RebootAsync(CancellationToken cancel)
        {
            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);
            Run(ssh, "nohup sh -c 'sleep 2; systemctl reboot' >/dev/null 2>&1 &");
        }

        // Local folder for data backups: VpsBackups\<host>\<terminal> next to OsEngine.exe.
        public static string BackupFolder(string host, string instanceName) =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VpsBackups", host.Replace(':', '_'), instanceName);

        public const int BackupsToKeep = 14;

        // Packs the terminal's data (robots, bots, settings, journals, Custom scripts) and its MCP key — without the
        // log files — on the server and downloads the archive to this computer. The terminal keeps running: the
        // archive is a live snapshot (files changed while being read are taken as they were at that moment).
        // Returns the local file. Keeps the newest BackupsToKeep archives of the terminal.
        public async Task<string> BackupAsync(VpsInstance instance, CancellationToken cancel)
        {
            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);
            using SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, _log, cancel).ConfigureAwait(false);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string remote = $"/tmp/osengine-backup-{instance.Name}-{stamp}.tgz";

            try
            {
                // tar exit code 1 = "some files changed while being read" — fine for a live snapshot
                string script =
                    $"cd '{instance.BaseFolder}' && " +
                    $"tar czf '{remote}' --warning=no-file-changed --exclude='data/Engine/Log' data $( [ -f mcp.key ] && echo mcp.key ); " +
                    "rc=$?; [ $rc -le 1 ] || exit $rc";

                using (SshCommand command = ssh.RunCommand(script))
                {
                    if (!(command.ExitStatus is int status) || status != 0)
                    {
                        throw new InvalidOperationException("Could not pack the data on the server: " + command.Error.Trim());
                    }
                }

                string folder = BackupFolder(_credentials.Host, instance.Name);
                Directory.CreateDirectory(folder);
                string local = Path.Combine(folder, $"{instance.Name}-{stamp}.tgz");

                await Task.Run(() =>
                {
                    using FileStream output = File.Create(local);
                    sftp.DownloadFile(remote, output);
                }, cancel).ConfigureAwait(false);

                foreach (FileInfo old in new DirectoryInfo(folder).GetFiles("*.tgz")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Skip(BackupsToKeep))
                {
                    old.Delete();
                }

                return local;
            }
            finally
            {
                Run(ssh, $"rm -f '{remote}'");
            }
        }

        // Replaces the terminal's data with a backup archive (from this terminal or another one — e.g. to move
        // robots to a new VPS). The current data is not deleted: it is kept as data.before-restore-<time>.
        // The terminal is stopped for the swap and started again; its MCP key comes from the archive.
        public async Task RestoreAsync(VpsInstance instance, string localArchive, CancellationToken cancel)
        {
            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);
            using SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, _log, cancel).ConfigureAwait(false);

            string stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            string remote = $"/tmp/osengine-restore-{stamp}.tgz";

            try
            {
                using (FileStream source = File.OpenRead(localArchive))
                {
                    await UploadAsync(sftp, source, remote, "backup archive", cancel).ConfigureAwait(false);
                }

                string b = instance.BaseFolder;
                string script =
                    "set -e\n" +
                    $"tar tzf '{remote}' | grep -q '^data/' || {{ echo 'FAIL not an OsEngine data backup (no data/ folder inside)'; exit 1; }}\n" +
                    $"systemctl stop {instance.Service}\n" +
                    $"mv '{b}/data' '{b}/data.before-restore-{stamp}'\n" +
                    // any failure from here on: put the previous data back and start the terminal again
                    $"trap \"rm -rf '{b}/data'; mv '{b}/data.before-restore-{stamp}' '{b}/data'; systemctl start {instance.Service}; " +
                    "echo 'FAIL restore failed — the previous data was put back'\" ERR\n" +
                    $"[ -f '{b}/mcp.key' ] && cp '{b}/mcp.key' '{b}/mcp.key.before-restore-{stamp}'\n" +
                    $"tar xzf '{remote}' -C '{b}'\n" +
                    $"mkdir -p '{b}/data/Engine/Log'\n" +
                    $"chown -R osengine:osengine '{b}/data' && [ -f '{b}/mcp.key' ] && chown osengine:osengine '{b}/mcp.key' && chmod 600 '{b}/mcp.key'\n" +
                    $"systemctl start {instance.Service}\n" +
                    $"echo \"OK data restored; previous data kept as {b}/data.before-restore-{stamp}\"\n";

                int exitCode = await RunStreamingAsync(ssh, "bash -c " + ShellQuote(script), cancel).ConfigureAwait(false);

                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Restore failed (exit code {exitCode}) — see the lines above");
                }
            }
            finally
            {
                Run(ssh, $"rm -f '{remote}'");
            }
        }

        private static string InstanceEnvironment(VpsInstance instance) =>
            $"OSENGINE_BASE={instance.BaseFolder} OSENGINE_SERVICE={instance.Service} OSENGINE_MCP_PORT={instance.Port}";

        private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

        // Creates a key pair for this computer on the server, authorizes its public half for the login user
        // and returns the private half. The client keeps it (DPAPI-encrypted) and logs in with it from then on,
        // so the root password is needed only once per computer. The comment names the computer, so the key
        // can be found and revoked in ~/.ssh/authorized_keys later.
        public async Task<(string PrivateKey, string Comment)> RegisterThisComputerAsync(CancellationToken cancel)
        {
            string machine = new string(Environment.MachineName.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            string comment = $"osengine-client-{machine}-{DateTime.UtcNow:yyyyMMdd}";

            using SshClient ssh = new SshClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(ssh, _log, cancel).ConfigureAwait(false);

            string script =
                "set -e\n" +
                "d=$(mktemp -d)\n" +
                $"ssh-keygen -q -t ed25519 -N '' -C '{comment}' -f \"$d/k\" >/dev/null\n" +
                "mkdir -p ~/.ssh && chmod 700 ~/.ssh\n" +
                "touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys\n" +
                "cat \"$d/k.pub\" >> ~/.ssh/authorized_keys\n" +
                "cat \"$d/k\"\n" +
                "rm -rf \"$d\"\n";

            using SshCommand command = ssh.RunCommand(script);

            if (!(command.ExitStatus is int status) || status != 0 || !command.Result.Contains("PRIVATE KEY"))
            {
                throw new InvalidOperationException("Could not create an SSH key on the server: " + command.Error.Trim());
            }

            string privateKey = command.Result;

            // make sure the new key really opens the server before relying on it
            VpsSshCredentials keyOnly = VpsSshCredentials.Create(_credentials.HostId, _credentials.User, null, privateKey, null, _log);
            using (SshClient check = new SshClient(keyOnly.CreateConnectionInfo()))
            {
                await keyOnly.ConnectAsync(check, _log, cancel).ConfigureAwait(false);
            }

            _log($"SSH key of this computer registered on the server ({comment})");
            return (privateKey, comment);
        }

        private async Task UploadAsync(SftpClient sftp, Stream source, string remotePath, string what, CancellationToken cancel)
        {
            long total = source.CanSeek ? source.Length : 0;
            int lastTenth = -1;

            Progress<UploadFileProgressReport> progress = new Progress<UploadFileProgressReport>(report =>
            {
                if (total <= 0) return;
                int tenth = (int)((long)report.TotalBytesUploaded * 10 / total);
                if (tenth > lastTenth && tenth < 10)
                {
                    lastTenth = tenth;
                    _log($"Uploading {what}: {tenth * 10} %");
                }
            });

            await sftp.UploadFileAsync(source, remotePath, true, progress, cancel).ConfigureAwait(false);
            _log($"Uploaded {what}" + (total > 0 ? $" ({total / 1024 / 1024.0:0.#} MB)" : ""));
        }

        // Runs a command and passes every output line to the log as it arrives.
        private async Task<int> RunStreamingAsync(SshClient ssh, string commandText, CancellationToken cancel)
        {
            using SshCommand command = ssh.CreateCommand(commandText + " 2>&1");
            Task execution = command.ExecuteAsync(cancel);

            using StreamReader reader = new StreamReader(command.OutputStream, Encoding.UTF8);
            Task reading = Task.Run(async () =>
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length > 0) _log("  " + line);
                }
            });

            await execution.ConfigureAwait(false);
            await Task.WhenAny(reading, Task.Delay(3000)).ConfigureAwait(false);

            return command.ExitStatus is int status ? status : -1;
        }

        private static int Run(SshClient ssh, string commandText)
        {
            using SshCommand command = ssh.RunCommand(commandText);
            return command.ExitStatus is int status ? status : -1;
        }

        // Custom folder -> Custom.tgz with "Custom/..." entries (unpacked into /opt/osengine/data).
        private static MemoryStream PackFolder(string folder)
        {
            MemoryStream result = new MemoryStream();

            using (GZipStream gzip = new GZipStream(result, CompressionLevel.Optimal, true))
            {
                TarFile.CreateFromDirectory(folder, gzip, true);
            }

            result.Position = 0;
            return result;
        }
    }
}
