using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using OsEngine.Entity;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// File manager of the VPS: the data folders of the terminals (robots, indicators, settings, logs), the trash and
    /// removed terminals, shown under readable names instead of Linux paths. Upload / download, new folder, rename,
    /// copy / cut / paste, delete to trash. In a robots folder the changed scripts can be applied (terminal restart +
    /// compile check).
    /// </summary>
    public partial class RobotsVpsFilesUi : Window
    {
        public sealed class FileRow
        {
            internal VpsFileEntry Entry;
            public string DisplayName => (Entry.IsDirectory ? "📁  " : "      ") + Entry.Name;
            public string SizeText => Entry.IsDirectory ? "" : FormatSize(Entry.Size);
            public string ModifiedText => Entry.Modified.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        }

        private readonly VpsFileService _files;
        private readonly List<VpsPlace> _places;
        private readonly Func<VpsInstance, List<string>, Task> _applyRobotChanges;
        private readonly Action<string> _log;

        private string _currentFolder;
        private List<string> _clipboard = new List<string>();
        private bool _clipboardKeepsSource;

        // robot scripts changed in this window, per terminal — what "Apply robot changes" compiles
        private readonly Dictionary<string, HashSet<string>> _changedRobots = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        internal RobotsVpsFilesUi(VpsSshCredentials credentials, Func<string, Task<string>> run, IReadOnlyList<VpsInstance> instances,
            VpsInstance initialInstance, Func<VpsInstance, List<string>, Task> applyRobotChanges, Action<string> log)
        {
            InitializeComponent();
            OsEngine.Layout.StickyBorders.Listen(this);

            _files = new VpsFileService(credentials, run, instances);
            _places = VpsFileService.Places(instances);
            _applyRobotChanges = applyRobotChanges;
            _log = log;

            ListBoxPlaces.ItemsSource = _places;

            VpsPlace initial = _places.FirstOrDefault(p => initialInstance != null && p.Instance?.Name == initialInstance.Name) ?? _places.FirstOrDefault();

            Loaded += async (s, e) =>
            {
                try
                {
                    SetBusy(true, "Connecting...");
                    await _files.ConnectAsync().ConfigureAwait(true);
                    SetBusy(false, "");
                    ListBoxPlaces.SelectedItem = initial;
                }
                catch (Exception ex)
                {
                    SetBusy(false, "");
                    MessageBox.Show("Could not open the VPS files: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Close();
                }
            };

            Closed += (s, e) => _files.Dispose();
        }

        #region Navigation

        private async void ListBoxPlaces_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ListBoxPlaces.SelectedItem is VpsPlace place)
            {
                TextBlockPlaceHint.Text = place.Hint;
                await OpenFolderAsync(place.Path).ConfigureAwait(true);
            }
        }

        private async void ButtonUp_Click(object sender, RoutedEventArgs e)
        {
            string parent = VpsFileService.Parent(_currentFolder);
            if (_files.IsAllowed(parent)) await OpenFolderAsync(parent).ConfigureAwait(true);
        }

        private async void ButtonRefresh_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync(_currentFolder).ConfigureAwait(true);

        private async void DataGridFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataGridFiles.SelectedItem is FileRow row && row.Entry.IsDirectory)
                await OpenFolderAsync(row.Entry.FullPath).ConfigureAwait(true);
        }

        private async Task OpenFolderAsync(string folder)
        {
            if (folder == null) return;

            try
            {
                SetBusy(true, "Reading...");
                List<VpsFileEntry> entries = await _files.ListAsync(folder).ConfigureAwait(true);
                _currentFolder = VpsFileService.Normalize(folder);
                DataGridFiles.ItemsSource = entries.Select(entry => new FileRow { Entry = entry }).ToList();
                ShowPath();
                SetBusy(false, entries.Count + " item(s)");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Could not open the folder: " + ex.Message);
            }
        }

        // "main: robots › subfolder" plus the real Linux path underneath
        private void ShowPath()
        {
            VpsPlace place = _places
                .Where(p => _currentFolder == p.Path || _currentFolder.StartsWith(p.Path + "/", StringComparison.Ordinal))
                .OrderByDescending(p => p.Path.Length)
                .FirstOrDefault();

            string rest = place == null ? _currentFolder : _currentFolder.Substring(place.Path.Length).Trim('/').Replace("/", " › ");
            TextBlockFriendlyPath.Text = place == null ? _currentFolder : place.Title + (rest.Length > 0 ? " › " + rest : "");
            TextBlockRealPath.Text = _currentFolder;

            ButtonUp.IsEnabled = _files.IsAllowed(VpsFileService.Parent(_currentFolder));
            ButtonApplyRobots.Visibility = VpsFileService.IsRobotsFolder(_currentFolder) ? Visibility.Visible : Visibility.Collapsed;
            ButtonPaste.IsEnabled = _clipboard.Count > 0;
        }

        #endregion

        #region Actions

        private List<FileRow> SelectedRows() => DataGridFiles.SelectedItems.OfType<FileRow>().ToList();

        private async void ButtonUpload_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Files to upload into " + TextBlockFriendlyPath.Text,
                Multiselect = true,
                Filter = VpsFileService.IsRobotsFolder(_currentFolder) ? "Robot scripts (*.cs)|*.cs|All files (*.*)|*.*" : "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string[] files = dialog.FileNames;
            await RunAsync("Uploading...", async () =>
            {
                await _files.UploadAsync(files, _currentFolder, _log).ConfigureAwait(true);
                RememberRobotChanges(_currentFolder, files.Select(System.IO.Path.GetFileName));
            }).ConfigureAwait(true);
        }

        private async void ButtonDownload_Click(object sender, RoutedEventArgs e)
        {
            List<FileRow> rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show("Select files or folders first"); return; }

            using System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Folder on this computer for the downloaded items",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string local = dialog.SelectedPath;
            await RunAsync("Downloading...", () => _files.DownloadAsync(rows.Select(r => r.Entry).ToList(), local, _log), refresh: false).ConfigureAwait(true);
        }

        private async void ButtonNewFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = AskName("New folder", "");
            if (name == null) return;
            await RunAsync("Creating...", () => _files.CreateFolderAsync(_currentFolder, name)).ConfigureAwait(true);
        }

        private async void ButtonRename_Click(object sender, RoutedEventArgs e)
        {
            List<FileRow> rows = SelectedRows();
            if (rows.Count != 1) { MessageBox.Show("Select one item to rename"); return; }

            string name = AskName("Rename", rows[0].Entry.Name);
            if (name == null || name == rows[0].Entry.Name) return;

            await RunAsync("Renaming...", async () =>
            {
                await _files.RenameAsync(rows[0].Entry.FullPath, name).ConfigureAwait(true);
                RememberRobotChanges(_currentFolder, new[] { rows[0].Entry.Name, name });
            }).ConfigureAwait(true);
        }

        private void ButtonCopy_Click(object sender, RoutedEventArgs e) => RememberForPaste(true);

        private void ButtonCut_Click(object sender, RoutedEventArgs e) => RememberForPaste(false);

        private void RememberForPaste(bool keepSource)
        {
            List<FileRow> rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show("Select files or folders first"); return; }

            _clipboard = rows.Select(r => r.Entry.FullPath).ToList();
            _clipboardKeepsSource = keepSource;
            ButtonPaste.IsEnabled = true;
            TextBlockStatus.Text = $"{(keepSource ? "Copy" : "Move")}: {_clipboard.Count} item(s) — open the target folder and press Paste";
        }

        private async void ButtonPaste_Click(object sender, RoutedEventArgs e)
        {
            if (_clipboard.Count == 0) return;

            List<string> sources = _clipboard;
            bool keep = _clipboardKeepsSource;
            string target = _currentFolder;

            List<string> existing = (DataGridFiles.ItemsSource as IEnumerable<FileRow> ?? Enumerable.Empty<FileRow>())
                .Select(r => r.Entry.Name)
                .Intersect(sources.Select(System.IO.Path.GetFileName))
                .ToList();

            if (existing.Count > 0 && sources.Any(s => VpsFileService.Parent(s) != target))
            {
                AcceptDialogUi replace = new AcceptDialogUi("These items already exist here and will be replaced:\n" + string.Join("\n", existing.Take(15)) + "\n\nContinue?");
                replace.ShowDialog();
                if (!replace.UserAcceptAction) return;
            }

            await RunAsync(keep ? "Copying..." : "Moving...", async () =>
            {
                await _files.CopyOrMoveAsync(sources, target, keep).ConfigureAwait(true);
                RememberRobotChanges(target, sources.Select(System.IO.Path.GetFileName));
                if (!keep)
                {
                    foreach (string source in sources) RememberRobotChanges(VpsFileService.Parent(source), new[] { System.IO.Path.GetFileName(source) });
                    _clipboard = new List<string>();
                }
            }).ConfigureAwait(true);
        }

        private async void ButtonDelete_Click(object sender, RoutedEventArgs e)
        {
            List<FileRow> rows = SelectedRows();
            if (rows.Count == 0) { MessageBox.Show("Select files or folders first"); return; }

            bool inTrash = _currentFolder.StartsWith(VpsFileService.TrashFolder + "/", StringComparison.Ordinal);
            AcceptDialogUi confirm = new AcceptDialogUi(
                (inTrash ? "Delete for good (cannot be undone)?\n\n" : "Move to the Trash on the VPS?\n\n")
                + string.Join("\n", rows.Take(15).Select(r => r.Entry.Name)) + (rows.Count > 15 ? $"\n... and {rows.Count - 15} more" : ""));
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunAsync("Deleting...", async () =>
            {
                string trash = await _files.DeleteAsync(rows.Select(r => r.Entry.FullPath).ToList()).ConfigureAwait(true);
                RememberRobotChanges(_currentFolder, rows.Select(r => r.Entry.Name));
                _log?.Invoke(trash == null ? $"Deleted for good: {rows.Count} item(s)" : $"Moved to the trash: {rows.Count} item(s) -> {trash}");
            }).ConfigureAwait(true);
        }

        private async void ButtonApplyRobots_Click(object sender, RoutedEventArgs e)
        {
            VpsPlace place = _places.FirstOrDefault(p => p.Instance != null && _currentFolder == p.Path);
            VpsInstance instance = place?.Instance;
            if (instance == null || _applyRobotChanges == null) return;

            List<string> classes = _changedRobots.TryGetValue(instance.Name, out HashSet<string> set) ? set.ToList() : new List<string>();

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Restart terminal \"{instance.Name}\" so it compiles the robot scripts?\n\n"
                + (classes.Count > 0 ? "Changed here: " + string.Join(", ", classes) + " — they are checked after the restart.\n\n" : "No script was changed in this window — the terminal is only restarted.\n\n")
                + "Its robots stop for about 10–30 s.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunAsync("Restarting the terminal...", async () =>
            {
                await _applyRobotChanges(instance, classes).ConfigureAwait(true);
                _changedRobots.Remove(instance.Name);
            }, refresh: false).ConfigureAwait(true);
        }

        private void RememberRobotChanges(string folder, IEnumerable<string> names)
        {
            if (!VpsFileService.IsRobotsFolder(folder)) return;

            VpsPlace place = _places.FirstOrDefault(p => p.Instance != null && VpsFileService.Normalize(folder) == p.Path);
            if (place == null) return;

            if (!_changedRobots.TryGetValue(place.Instance.Name, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _changedRobots[place.Instance.Name] = set;
            }

            foreach (string name in names.Where(n => n != null && n.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                set.Add(System.IO.Path.GetFileNameWithoutExtension(name));
        }

        private async Task RunAsync(string status, Func<Task> action, bool refresh = true)
        {
            try
            {
                SetBusy(true, status);
                await action().ConfigureAwait(true);
                SetBusy(false, "Done");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Failed: " + ex.Message);
                MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            if (refresh) await OpenFolderAsync(_currentFolder).ConfigureAwait(true);
        }

        #endregion

        private void SetBusy(bool busy, string status)
        {
            PanelButtons.IsEnabled = !busy;
            ListBoxPlaces.IsEnabled = !busy;
            TextBlockStatus.Text = status;
        }

        // small input box built in code, styled like the rest of the terminal
        private string AskName(string title, string initial)
        {
            System.Windows.Controls.TextBox box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(10), Height = 26 };
            System.Windows.Controls.Button ok = new System.Windows.Controls.Button { Content = "OK", Width = 90, Height = 26, Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            System.Windows.Controls.StackPanel panel = new System.Windows.Controls.StackPanel();
            panel.Children.Add(box);
            panel.Children.Add(ok);

            Window dialog = new Window
            {
                Title = title,
                Content = panel,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Style = TryFindResource("WindowStyleNoResize") as Style
            };

            string result = null;
            ok.Click += (s, e) =>
            {
                if (!VpsFileService.IsValidName(box.Text.Trim()))
                {
                    MessageBox.Show("The name must not be empty or contain / or \\");
                    return;
                }
                result = box.Text.Trim();
                dialog.DialogResult = true;
            };
            dialog.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            dialog.ShowDialog();
            return result;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        }
    }
}
