using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using OsEngine.Entity;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>Lists the SSH keys that may log in to the VPS and revokes the key of a lost or replaced computer.</summary>
    public partial class RobotsVpsComputersUi : Window
    {
        public sealed class KeyRow
        {
            public string Comment { get; set; }
            public string Marker { get; set; }
            public string Type { get; set; }
            public string Fingerprint { get; set; }
            public bool IsThisComputer { get; set; }
        }

        private readonly Func<string, Task<string>> _run;
        private readonly HashSet<string> _thisComputer;
        private readonly Action<string> _log;

        // run: a shell command over the VPS window's SSH connection; thisComputerFingerprints: keys this computer
        // logs in with (its registered key and/or the key file) — they are marked and cannot be revoked from here.
        internal RobotsVpsComputersUi(Func<string, Task<string>> run, IEnumerable<string> thisComputerFingerprints, Action<string> log)
        {
            InitializeComponent();
            _run = run;
            _log = log;
            _thisComputer = new HashSet<string>(thisComputerFingerprints.Where(f => f != null), StringComparer.Ordinal);
            Loaded += async (s, e) => await LoadAsync().ConfigureAwait(true);
        }

        private async Task LoadAsync()
        {
            LabelStatus.Content = "Reading...";

            try
            {
                List<VpsAuthorizedKey> keys = await VpsComputers.ListAsync(_run).ConfigureAwait(true);

                DataGridKeys.ItemsSource = keys.Select(k => new KeyRow
                {
                    Comment = string.IsNullOrWhiteSpace(k.Comment) ? "(no name)" : k.Comment,
                    Type = k.Type,
                    Fingerprint = k.Fingerprint,
                    IsThisComputer = _thisComputer.Contains(k.Fingerprint),
                    Marker = _thisComputer.Contains(k.Fingerprint) ? "this computer" : ""
                }).ToList();

                LabelStatus.Content = keys.Count + " key(s)";
            }
            catch (Exception ex)
            {
                LabelStatus.Content = "Could not read the keys: " + ex.Message;
            }
        }

        private async void ButtonRefresh_Click(object sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

        private async void ButtonRevoke_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataGridKeys.SelectedItem is KeyRow row))
            {
                MessageBox.Show("Select a key first");
                return;
            }

            if (row.IsThisComputer)
            {
                MessageBox.Show("This is the key this computer is connected with — it cannot be revoked from here. "
                    + "Revoke it from another computer.");
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Revoke the key \"{row.Comment}\"?\n\n{row.Fingerprint}\n\nThat computer can no longer log in with its key "
                + "(a connection it has open now stays until closed). It can be connected again with the root password. "
                + "The previous authorized_keys file is kept on the VPS as a backup.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            try
            {
                string result = (await VpsComputers.RevokeAsync(_run, row.Fingerprint).ConfigureAwait(true)).Trim();
                _log?.Invoke($"SSH key \"{row.Comment}\" revoked: {result}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Revoke failed: " + ex.Message);
            }

            await LoadAsync().ConfigureAwait(true);
        }
    }
}
