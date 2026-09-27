// VPS file manager back end: the data folders of the terminals, the trash and removed terminals — over SFTP and SSH.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // A named shortcut in the file manager ("main: robots") to a real folder on the VPS.
    internal sealed class VpsPlace
    {
        public string Title { get; set; }
        public string Path { get; set; }
        public string Hint { get; set; }
        public VpsInstance Instance { get; set; }

        public override string ToString() => Title;
    }

    internal sealed class VpsFileEntry
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
    }

    internal sealed class VpsFileService : IDisposable
    {
        public const string TrashFolder = "/opt/osengine-trash";
        public const string RemovedFolder = "/opt/osengine-removed";

        private readonly VpsSshCredentials _credentials;
        private readonly Func<string, Task<string>> _run;
        private readonly List<string> _allowedRoots;
        private SftpClient _sftp;

        // run: shell command over the VPS window's SSH connection (SshTunnel.RunCommandAsync)
        public VpsFileService(VpsSshCredentials credentials, Func<string, Task<string>> run, IEnumerable<VpsInstance> instances)
        {
            _credentials = credentials;
            _run = run;

            // Only the terminals' data folders, the trash and removed terminals are reachable: the build (app), its backups
            // and the MCP keys cannot be damaged from here, and neither can the rest of the Linux system.
            _allowedRoots = instances.Select(i => i.BaseFolder + "/data").ToList();
            _allowedRoots.Add(TrashFolder);
            _allowedRoots.Add(RemovedFolder);
        }

        public async Task ConnectAsync()
        {
            SftpClient sftp = new SftpClient(_credentials.CreateConnectionInfo());
            await _credentials.ConnectAsync(sftp, null, default).ConfigureAwait(false);
            _sftp = sftp;
        }

        // The shortcuts shown on the left of the file manager, per terminal
        public static List<VpsPlace> Places(IEnumerable<VpsInstance> instances)
        {
            List<VpsPlace> places = new List<VpsPlace>();

            foreach (VpsInstance instance in instances)
            {
                string data = instance.BaseFolder + "/data";
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: robots", Path = data + "/Custom/Robots", Hint = "Robot scripts (*.cs). The terminal compiles them when it starts or when a robot is created" });
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: indicators", Path = data + "/Custom/Indicators", Hint = "Indicator scripts (*.cs) used by the robots" });
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: candle series", Path = data + "/Custom/CandleSeries", Hint = "Custom candle types (*.cs)" });
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: settings and journals", Path = data + "/Engine", Hint = "Settings of robots, connectors and their position journals. Change only when the terminal is stopped" });
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: logs", Path = data + "/Engine/Log", Hint = "Log files of the terminal" });
                places.Add(new VpsPlace { Instance = instance, Title = $"{instance.Name}: all data", Path = data, Hint = "The whole working folder of the terminal" });
            }

            places.Add(new VpsPlace { Title = "Trash", Path = TrashFolder, Hint = "Deleted files, grouped by the time of deletion. Can be restored by moving them back" });
            places.Add(new VpsPlace { Title = "Removed terminals", Path = RemovedFolder, Hint = "Data of terminals removed in the VPS window" });
            return places;
        }

        public bool IsAllowed(string path)
        {
            path = Normalize(path);
            return _allowedRoots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
        }

        public string RootOf(string path)
        {
            path = Normalize(path);
            return _allowedRoots.FirstOrDefault(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal));
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "/";
            List<string> parts = new List<string>();
            foreach (string part in path.Replace('\\', '/').Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(part);
            }
            return "/" + string.Join("/", parts);
        }

        public static string Parent(string path)
        {
            path = Normalize(path);
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "/" : path.Substring(0, slash);
        }

        public static string Combine(string folder, string name) => Normalize(folder + "/" + name);

        public static bool IsValidName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.IndexOfAny(new[] { '/', '\\', '\0' }) < 0;

        private void Check(string path)
        {
            if (!IsAllowed(path)) throw new InvalidOperationException("Outside the terminals' data folders: " + path);
        }

        public async Task<List<VpsFileEntry>> ListAsync(string folder)
        {
            folder = Normalize(folder);
            Check(folder);

            // the trash and removed-terminals folders appear only when something is put there
            if ((folder == TrashFolder || folder == RemovedFolder) && !await Task.Run(() => _sftp.Exists(folder)).ConfigureAwait(false))
            {
                return new List<VpsFileEntry>();
            }

            return await Task.Run(() => _sftp.ListDirectory(folder)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new VpsFileEntry
                {
                    Name = f.Name,
                    FullPath = Normalize(f.FullName),
                    IsDirectory = f.IsDirectory,
                    Size = f.IsDirectory ? 0 : f.Length,
                    Modified = f.LastWriteTime
                })
                .OrderBy(f => f.IsDirectory ? 0 : 1)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()).ConfigureAwait(false);
        }

        public async Task CreateFolderAsync(string folder, string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Invalid folder name");
            string path = Combine(folder, name);
            Check(path);
            await _run($"mkdir -p {Q(path)} && {Chown(path)}").ConfigureAwait(false);
        }

        public async Task RenameAsync(string path, string newName)
        {
            if (!IsValidName(newName)) throw new ArgumentException("Invalid name");
            path = Normalize(path);
            string target = Combine(Parent(path), newName);
            Check(path);
            Check(target);
            await _run($"[ ! -e {Q(target)} ] || {{ echo 'already exists: {Esc(newName)}' >&2; exit 1; }}; mv {Q(path)} {Q(target)}").ConfigureAwait(false);
            await AfterRobotsChangedAsync(Parent(path), new[] { System.IO.Path.GetFileName(path), newName }).ConfigureAwait(false);
        }

        // copy (keepSource) or move the given files/folders into targetFolder; an existing item with the same name is replaced
        public async Task CopyOrMoveAsync(IReadOnlyList<string> sources, string targetFolder, bool keepSource)
        {
            targetFolder = Normalize(targetFolder);
            Check(targetFolder);

            foreach (string source in sources.Select(Normalize))
            {
                Check(source);
                if (targetFolder == source || targetFolder.StartsWith(source + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("A folder cannot be copied or moved into itself: " + source);
            }

            string list = string.Join(" ", sources.Select(s => Q(Normalize(s))));
            string command = keepSource ? $"cp -a {list} {Q(targetFolder)}/" : $"mv -f {list} {Q(targetFolder)}/";
            await _run($"{command} && {Chown(targetFolder)}").ConfigureAwait(false);

            List<string> names = sources.Select(s => System.IO.Path.GetFileName(Normalize(s))).ToList();
            await AfterRobotsChangedAsync(targetFolder, names).ConfigureAwait(false);
            if (!keepSource)
            {
                foreach (IGrouping<string, string> group in sources.GroupBy(s => Parent(Normalize(s))))
                    await AfterRobotsChangedAsync(group.Key, group.Select(s => System.IO.Path.GetFileName(Normalize(s)))).ConfigureAwait(false);
            }
        }

        // "Delete": moves the items to /opt/osengine-trash/<time>/, keeping their names — nothing is lost by a wrong click.
        // Inside the trash itself it deletes for good. Returns the trash folder (or null after a final delete).
        public async Task<string> DeleteAsync(IReadOnlyList<string> paths)
        {
            List<string> items = paths.Select(Normalize).ToList();
            items.ForEach(Check);

            if (items.All(p => p.StartsWith(TrashFolder + "/", StringComparison.Ordinal)))
            {
                await _run("rm -rf " + string.Join(" ", items.Select(Q))).ConfigureAwait(false);
                return null;
            }

            string trash = $"{TrashFolder}/{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            await _run($"mkdir -p {Q(trash)} && mv -f {string.Join(" ", items.Select(Q))} {Q(trash)}/ && {Chown(TrashFolder)}").ConfigureAwait(false);

            foreach (IGrouping<string, string> group in items.GroupBy(Parent))
                await AfterRobotsChangedAsync(group.Key, group.Select(p => System.IO.Path.GetFileName(p))).ConfigureAwait(false);

            return trash;
        }

        public async Task UploadAsync(IReadOnlyList<string> localFiles, string targetFolder, Action<string> log)
        {
            targetFolder = Normalize(targetFolder);
            Check(targetFolder);
            bool robots = IsRobotsFolder(targetFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            foreach (string local in localFiles)
            {
                string name = System.IO.Path.GetFileName(local);
                string remote = Combine(targetFolder, name);

                // robot scripts: the replaced version is kept in Custom/Robots-backup, as the old "Upload robots" did
                if (robots && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    string backup = Combine(Parent(targetFolder), "Robots-backup");
                    await _run($"if [ -f {Q(remote)} ]; then mkdir -p {Q(backup)} && cp {Q(remote)} {Q(Combine(backup, System.IO.Path.GetFileNameWithoutExtension(name) + "-" + stamp + ".cs"))} && {Chown(backup)}; fi").ConfigureAwait(false);
                }

                await Task.Run(() =>
                {
                    using FileStream stream = File.OpenRead(local);
                    _sftp.UploadFile(stream, remote, true);
                }).ConfigureAwait(false);

                log?.Invoke($"Uploaded {name} ({new FileInfo(local).Length / 1024.0:0.#} KB) to {remote}");
            }

            await _run(Chown(targetFolder)).ConfigureAwait(false);
            await AfterRobotsChangedAsync(targetFolder, localFiles.Select(System.IO.Path.GetFileName)).ConfigureAwait(false);
        }

        // Files and whole folders (recursively) into a local folder
        public async Task DownloadAsync(IReadOnlyList<VpsFileEntry> entries, string localFolder, Action<string> log)
        {
            foreach (VpsFileEntry entry in entries)
            {
                Check(entry.FullPath);
                await Task.Run(() => DownloadItem(entry.FullPath, entry.IsDirectory, System.IO.Path.Combine(localFolder, entry.Name))).ConfigureAwait(false);
                log?.Invoke($"Downloaded {entry.FullPath} to {System.IO.Path.Combine(localFolder, entry.Name)}");
            }
        }

        private void DownloadItem(string remote, bool isDirectory, string local)
        {
            if (!isDirectory)
            {
                using FileStream stream = File.Create(local);
                _sftp.DownloadFile(remote, stream);
                return;
            }

            Directory.CreateDirectory(local);
            foreach (ISftpFile child in _sftp.ListDirectory(remote).Where(f => f.Name != "." && f.Name != ".."))
            {
                if (child.IsDirectory || child.IsRegularFile)
                    DownloadItem(child.FullName, child.IsDirectory, System.IO.Path.Combine(local, child.Name));
            }
        }

        public static bool IsRobotsFolder(string folder) =>
            Normalize(folder).EndsWith("/data/Custom/Robots", StringComparison.Ordinal);

        // A robot script added, replaced, renamed or removed: drop its line from the robot description cache
        // (BotsDescription.txt, used by "Add bot") so the list is built from the new code.
        private async Task AfterRobotsChangedAsync(string folder, IEnumerable<string> names)
        {
            if (!IsRobotsFolder(folder)) return;

            string cache = Combine(Parent(Parent(Normalize(folder))), "BotsDescription.txt");
            List<string> classes = names.Where(n => n != null && n.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(System.IO.Path.GetFileNameWithoutExtension).Where(IsValidName).ToList();
            if (classes.Count == 0) return;

            string sed = string.Join(" ", classes.Select(c => $"-e {Q("/^" + System.Text.RegularExpressions.Regex.Escape(c) + "&/d")}"));
            await _run($"[ ! -f {Q(cache)} ] || sed -i {sed} {Q(cache)}").ConfigureAwait(false);
        }

        private static string Chown(string path) => $"chown -R osengine:osengine {Q(path)}";

        private static string Q(string value) => "'" + value.Replace("'", "'\\''") + "'";

        private static string Esc(string value) => value.Replace("'", "");

        public void Dispose()
        {
            try { _sftp?.Disconnect(); } catch { /* already closed */ }
            _sftp?.Dispose();
            _sftp = null;
        }
    }
}
