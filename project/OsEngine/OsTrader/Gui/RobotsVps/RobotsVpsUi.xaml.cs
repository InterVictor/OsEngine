/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OsEngine.Alerts;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// VPS connection and administration window: SSH tunnel + MCP connection to a remote (headless)
    /// OsEngine and its event log. Robots and connectors of the remote terminal are managed from the
    /// Robots.VPS workspace (RobotsVpsLiteClone), not here.
    /// Окно подключения и администрирования VPS: SSH-туннель, подключение MCP к удалённому OsEngine и
    /// лог событий. Роботами и коннекторами удалённого терминала управляет окно Роботы.VPS.
    /// </summary>
    public partial class RobotsVpsUi : Window
    {
        private const string SettingsFile = @"Engine\RobotsVpsSettings.txt";

        private SshTunnel _sshTunnel;
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();

        public RobotsVpsUi()
        {
            InitializeComponent();

            LogDataGrid.ItemsSource = _log;
            DataGridTerminals.ItemsSource = _terminalRows;

            TextBoxSshKeyPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "ff_server");

            LoadSettings();
            UpdateComputerKeyStatus();

            Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }

        public void ShutdownConnection()
        {
            Disconnect();
        }

        public static bool IsAutoConnectOnStartupEnabled()
        {
            try
            {
                string[] lines = File.Exists(SettingsFile) ? File.ReadAllLines(SettingsFile) : Array.Empty<string>();
                return lines.Length > 7 && bool.TryParse(lines[7], out bool enabled) && enabled;
            }
            catch { return false; }
        }

        public void StartAutomaticConnect()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (CheckBoxAutoConnectSsh.IsChecked == true) ButtonConnect_Click(null, null);
            }));
        }

        private void CheckBoxAutoConnectSsh_Click(object sender, RoutedEventArgs e) => SaveSettings();
        #region Settings (Url + API key; excluded from git like other Engine\* connector settings)

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                {
                    return;
                }

                string[] lines = File.ReadAllLines(SettingsFile);

                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    TextBoxUrl.Text = lines[0];
                }

                if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
                {
                    PasswordBoxApiKey.Password = UnprotectSecret(lines[1]);
                }

                if (lines.Length > 2) TextBoxSshHost.Text = lines[2];
                if (lines.Length > 3) TextBoxSshUser.Text = lines[3];
                // an empty key path is a valid choice (password or registered key only) — keep it empty
                if (lines.Length > 4) TextBoxSshKeyPath.Text = lines[4];
                if (lines.Length > 5 && !string.IsNullOrWhiteSpace(lines[5])) TextBoxSshLocalPort.Text = lines[5];
                if (lines.Length > 6 && !string.IsNullOrWhiteSpace(lines[6])) TextBoxSshRemotePort.Text = lines[6];
                if (lines.Length > 7 && bool.TryParse(lines[7], out bool autoConnect)) CheckBoxAutoConnectSsh.IsChecked = autoConnect;
                if (lines.Length > 8 && !string.IsNullOrWhiteSpace(lines[8])) PasswordBoxSshPassword.Password = UnprotectSecret(lines[8]);
                if (lines.Length > 9 && !string.IsNullOrWhiteSpace(lines[9])) _computerKey = UnprotectSecret(lines[9]);
                if (lines.Length > 10) _computerKeyComment = lines[10];
            }
            catch (Exception ex)
            {
                AppendLog("Settings load failed: " + ex.Message);
            }
        }

        // Secrets (MCP API key, SSH password) are stored encrypted for the current Windows user (DPAPI).
        // Values saved by older versions in plain text are still read and get encrypted on the next save.
        private const string ProtectedPrefix = "dpapi:";

        private static string ProtectSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(data);
        }

        private static string UnprotectSecret(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored ?? "";
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length)), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory("Engine");
                File.WriteAllLines(SettingsFile, new[]
                {
                    TextBoxUrl.Text.Trim(),
                    ProtectSecret(PasswordBoxApiKey.Password),
                    TextBoxSshHost.Text.Trim(),
                    TextBoxSshUser.Text.Trim(),
                    TextBoxSshKeyPath.Text.Trim(),
                    TextBoxSshLocalPort.Text.Trim(),
                    TextBoxSshRemotePort.Text.Trim(),
                    (CheckBoxAutoConnectSsh.IsChecked == true).ToString(),
                    ProtectSecret(PasswordBoxSshPassword.Password),
                    ProtectSecret(_computerKey ?? ""),
                    _computerKeyComment ?? ""
                });
            }
            catch (Exception ex)
            {
                AppendLog("Settings save failed: " + ex.Message);
            }
        }

        #endregion

        #region Server: deploy over SSH + SSH key of this computer

        // Registered SSH key of this computer (private key text, DPAPI-encrypted in the settings file) and its
        // comment in the server's ~/.ssh/authorized_keys (osengine-client-<computer>-<date>).
        private string _computerKey;
        private string _computerKeyComment;

        private VpsSshCredentials CreateCredentials() =>
            VpsSshCredentials.Create(TextBoxSshHost.Text, TextBoxSshUser.Text, TextBoxSshKeyPath.Text,
                _computerKey, PasswordBoxSshPassword.Password, LogFromAnyThread);

        private void LogFromAnyThread(string message) =>
            Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));

        private void UpdateComputerKeyStatus()
        {
            TextBlockComputerKey.Text = _computerKey != null
                ? $"SSH key of this computer: registered ({_computerKeyComment}). The root password is not stored."
                : "SSH key of this computer: not registered — connect once with the root password";
        }

        private async Task RegisterComputerKeyAsync(VpsSshCredentials credentials)
        {
            try
            {
                (string privateKey, string comment) = await new VpsProvisioner(credentials, LogFromAnyThread)
                    .RegisterThisComputerAsync(CancellationToken.None).ConfigureAwait(true);

                _computerKey = privateKey;
                _computerKeyComment = comment;
                PasswordBoxSshPassword.Password = "";
                SaveSettings();
                UpdateComputerKeyStatus();
                AppendLog("From now on this computer logs in with its own key; the root password was removed from the settings");
            }
            catch (Exception ex)
            {
                AppendLog("Could not register the SSH key of this computer (the password keeps working): " + ex.Message);
            }
        }

        private async void ButtonDeployServer_Click(object sender, RoutedEventArgs e)
        {
            VpsSshCredentials credentials;

            try
            {
                credentials = CreateCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Set up OsEngine on {credentials.HostId} as {credentials.User}?\n\n"
                + "Time zone UTC + NTP, firewall (only SSH open), fail2ban, service user, OsEngine build, "
                + "robot scripts, MCP key and the systemd service. Parts that already exist are kept as they are.");
            confirm.ShowDialog();

            if (!confirm.UserAcceptAction)
            {
                return;
            }

            ButtonDeployServer.IsEnabled = false;
            SaveSettings();

            try
            {
                AppendLog("=== Deploy / repair server " + credentials.HostId + " ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread).DeployAsync(CancellationToken.None)).ConfigureAwait(true);

                if (_computerKey == null && credentials.Password != null)
                {
                    await RegisterComputerKeyAsync(credentials).ConfigureAwait(true);
                }

                if (!IsConnected)
                {
                    ButtonConnect_Click(null, null);
                }
            }
            catch (Exception ex)
            {
                AppendLog("Deploy failed: " + ex.Message);
            }
            finally
            {
                ButtonDeployServer.IsEnabled = true;
            }
        }

        #endregion

        #region Connect / Disconnect (one SSH tunnel, one MCP client per VPS terminal)

        // A running terminal on the VPS and its MCP client. The main terminal uses the ports of the window fields
        // (local 6510 -> VPS 6500); an extra terminal on VPS port P gets local port <Local port> + (P - 6500),
        // all carried by the same SSH connection.
        private sealed class TerminalConnection
        {
            public string Name;
            public int LocalPort;
            public RemoteMcpClient Client;
            public bool Reconnecting;
        }

        private readonly Dictionary<string, TerminalConnection> _terminals =
            new Dictionary<string, TerminalConnection>(StringComparer.OrdinalIgnoreCase);

        private List<VpsInstance> _instances = new List<VpsInstance>();
        private DispatcherTimer _terminalsTimer;
        private bool _syncingTerminals;
        private bool _noServiceLogged;

        private bool IsConnected => _terminals.Count > 0 || _sshTunnel != null;

        private async void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            string url = TextBoxUrl.Text.Trim();
            string apiKey = PasswordBoxApiKey.Password;

            if (string.IsNullOrEmpty(url) && string.IsNullOrWhiteSpace(TextBoxSshHost.Text))
            {
                MessageBox.Show("Enter an MCP URL or SSH host");
                return;
            }

            ButtonConnect.IsEnabled = false;
            SetStatus("Connecting...", Brushes.Orange);

            try
            {
                SaveSettings();

                if (string.IsNullOrWhiteSpace(TextBoxSshHost.Text))
                {
                    // no SSH: direct MCP URL + the API Key box, main terminal only
                    await ConnectTerminalAsync(VpsRemoteSession.MainInstance, url, apiKey, 0).ConfigureAwait(true);
                }
                else
                {
                    if (!int.TryParse(TextBoxSshLocalPort.Text, out int localPort)
                        || !int.TryParse(TextBoxSshRemotePort.Text, out int remotePort))
                    {
                        throw new FormatException("SSH local and VPS API ports must be whole numbers");
                    }

                    VpsSshCredentials credentials = CreateCredentials();
                    _sshTunnel = await SshTunnel.StartAsync(credentials, localPort, remotePort, LogFromAnyThread).ConfigureAwait(true);
                    TextBoxUrl.Text = $"http://127.0.0.1:{localPort}/api/v2/mcp";

                    if (!_sshTunnel.StartedByThisWindow)
                    {
                        // someone else's tunnel on the local port: no SSH commands, main terminal via the API Key box
                        AppendLog("SSH tunnel already running — only the main terminal is available");
                        await ConnectTerminalAsync(VpsRemoteSession.MainInstance, TextBoxUrl.Text, apiKey, localPort).ConfigureAwait(true);
                    }
                    else
                    {
                        // First login with the root password from this computer: create and register its own key,
                        // so the password is not needed (nor stored) from now on.
                        if (_computerKey == null && !string.IsNullOrEmpty(PasswordBoxSshPassword.Password))
                        {
                            await RegisterComputerKeyAsync(credentials).ConfigureAwait(true);
                        }

                        await SyncTerminalsAsync().ConfigureAwait(true);

                        _terminalsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                        _terminalsTimer.Tick += async (s, args) => await SyncTerminalsAsync().ConfigureAwait(true);
                        _terminalsTimer.Start();
                    }
                }

                SaveSettings();
                ButtonDisconnect.IsEnabled = true;
                UpdateOverallStatus();
            }
            catch (Exception ex)
            {
                DisconnectCore();
                ButtonConnect.IsEnabled = true;
                SetStatus("Disconnected", Brushes.Gray);
                AppendLog("Connect failed: " + ex.Message);
            }
        }

        // Brings the MCP connections in line with the terminals on the VPS: connects running ones that are not
        // connected yet, drops those that were stopped or removed. Runs on connect and every 10 s.
        private async Task SyncTerminalsAsync()
        {
            SshTunnel tunnel = _sshTunnel;

            if (tunnel == null || !tunnel.StartedByThisWindow || _syncingTerminals)
            {
                return;
            }

            _syncingTerminals = true;

            try
            {
                List<VpsInstance> instances = await VpsInstances.ListAsync(tunnel.RunCommandAsync).ConfigureAwait(true);

                if (!ReferenceEquals(tunnel, _sshTunnel))
                {
                    return; // disconnected meanwhile
                }

                _instances = instances;

                if (instances.Count == 0 && !_noServiceLogged)
                {
                    _noServiceLogged = true;
                    AppendLog("No OsEngine terminal found on the VPS — use \"Deploy / repair server\"");
                }

                int localBase = int.Parse(TextBoxSshLocalPort.Text, CultureInfo.InvariantCulture);
                bool changed = false;

                foreach (VpsInstance instance in instances.Where(i => i.IsActive && !_terminals.ContainsKey(i.Name)))
                {
                    int localPort = instance.IsMain ? localBase : localBase + (instance.Port - VpsInstances.MainPort);

                    try
                    {
                        if (!instance.IsMain)
                        {
                            tunnel.AddForward(localPort, instance.Port);
                        }

                        string key = await VpsInstances.ReadKeyAsync(tunnel.RunCommandAsync, instance).ConfigureAwait(true);
                        await ConnectTerminalAsync(instance.Name, $"http://127.0.0.1:{localPort}/api/v2/mcp", key, localPort).ConfigureAwait(true);

                        if (instance.IsMain) PasswordBoxApiKey.Password = key;
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"Terminal \"{instance.Name}\": could not connect: {ex.Message}");
                    }
                }

                foreach (string name in _terminals.Keys.Where(n => !instances.Any(i => i.IsActive && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))).ToList())
                {
                    TerminalConnection terminal = _terminals[name];
                    DropTerminal(terminal);
                    AppendLog($"Terminal \"{name}\" is not running — disconnected");
                    changed = true;
                }

                if (changed)
                {
                    PublishSession();
                }

                RenderTerminals();
                UpdateOverallStatus();
            }
            catch (Exception ex)
            {
                AppendLog("Could not read the terminals of the VPS: " + ex.Message);
            }
            finally
            {
                _syncingTerminals = false;
            }
        }

        private async Task ConnectTerminalAsync(string name, string url, string apiKey, int localPort)
        {
            RemoteMcpClient client = new RemoteMcpClient(url, apiKey);
            TerminalConnection terminal = new TerminalConnection { Name = name, LocalPort = localPort, Client = client };

            client.EventReceived += (eventName, payload) => Client_EventReceived(terminal, eventName, payload);
            client.Disconnected += ex => Client_Disconnected(terminal, ex);
            client.Reconnected += () => Client_Reconnected(terminal);

            try
            {
                await client.ConnectAsync().ConfigureAwait(true);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _terminals[name] = terminal;
            PublishSession();
            AppendLog(TerminalPrefix(name) + "connected to " + url);
        }

        private void DropTerminal(TerminalConnection terminal)
        {
            _terminals.Remove(terminal.Name);
            terminal.Client.Dispose();

            if (!string.Equals(terminal.Name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase))
            {
                _sshTunnel?.RemoveForward(terminal.LocalPort);
            }
        }

        private void PublishSession()
        {
            VpsRemoteSession.SetClients(_terminals.ToDictionary(t => t.Key, t => t.Value.Client, StringComparer.OrdinalIgnoreCase));
        }

        private void ButtonDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            DisconnectCore();
            ButtonConnect.IsEnabled = true;
            ButtonDisconnect.IsEnabled = false;
            SetStatus("Disconnected", Brushes.Gray);
        }

        private void DisconnectCore()
        {
            _terminalsTimer?.Stop();
            _terminalsTimer = null;

            foreach (TerminalConnection terminal in _terminals.Values.ToList())
            {
                terminal.Client.Dispose();
            }

            _terminals.Clear();
            VpsRemoteSession.SetClients(null);

            if (_sshTunnel != null)
            {
                bool stoppedTunnel = _sshTunnel.StartedByThisWindow;
                _sshTunnel.Dispose();
                _sshTunnel = null;
                if (stoppedTunnel) AppendLog("SSH tunnel stopped");
            }

            _instances = new List<VpsInstance>();
            _noServiceLogged = false;
            RenderTerminals();
        }

        // prefix log lines with the terminal name only when the VPS runs several terminals
        private string TerminalPrefix(string name) =>
            _instances.Count > 1 || !string.Equals(name, VpsRemoteSession.MainInstance, StringComparison.OrdinalIgnoreCase)
                ? $"[{name}] " : "";

        // Raised on every failed retry (~2 s) while a terminal is unreachable — log only the first one.
        private void Client_Disconnected(TerminalConnection terminal, Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                if (!terminal.Reconnecting)
                {
                    terminal.Reconnecting = true;
                    AppendLog(TerminalPrefix(terminal.Name) + "connection lost, reconnecting: " + ex.Message);
                }

                UpdateOverallStatus();
            });
        }

        private void Client_Reconnected(TerminalConnection terminal)
        {
            Dispatcher.Invoke(() =>
            {
                terminal.Reconnecting = false;
                AppendLog(TerminalPrefix(terminal.Name) + "connection restored");
                UpdateOverallStatus();
            });
        }

        private void Client_EventReceived(TerminalConnection terminal, string eventName, JsonElement payload)
        {
            Dispatcher.Invoke(() =>
            {
                if (eventName == "alert.raised")
                {
                    string botName = payload.TryGetProperty("bot_name", out JsonElement bot) ? bot.GetString() : "VPS";
                    string message = payload.TryGetProperty("message", out JsonElement text) ? text.GetString() : "";
                    string time = payload.TryGetProperty("time", out JsonElement timestamp) ? timestamp.GetString() : DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    AlertMessageManager.ThrowRemoteAlert(botName, message, time);
                    AppendLog(TerminalPrefix(terminal.Name) + "emergency alert received: " + botName);
                    return;
                }

                // heartbeat arrives every 5 s just to keep the stream alive — not worth a log line
                if (eventName == "heartbeat") return;

                AppendLog(TerminalPrefix(terminal.Name) + eventName);
            });
        }

        private void UpdateOverallStatus()
        {
            if (!IsConnected)
            {
                SetStatus("Disconnected", Brushes.Gray);
            }
            else if (_terminals.Values.Any(t => t.Reconnecting))
            {
                SetStatus("Reconnecting...", Brushes.Orange);
            }
            else if (_terminals.Count == 0)
            {
                SetStatus("SSH connected, no terminal running", Brushes.Orange);
            }
            else
            {
                SetStatus(_terminals.Count == 1 ? "Connected" : $"Connected ({_terminals.Count} terminals)", Brushes.Green);
            }
        }

        private void SetStatus(string text, Brush color)
        {
            LabelStatus.Content = text;
            EllipseStatus.Fill = color;
        }

        #endregion

        #region Terminals on the VPS (systemd services osengine / osengine-<name>)

        private readonly ObservableCollection<TerminalRow> _terminalRows = new ObservableCollection<TerminalRow>();

        private void RenderTerminals()
        {
            string selected = (DataGridTerminals.SelectedItem as TerminalRow)?.Name;
            _terminalRows.Clear();

            foreach (VpsInstance instance in _instances)
            {
                _terminalRows.Add(new TerminalRow
                {
                    Name = instance.Name,
                    Port = instance.Port,
                    State = instance.State,
                    Memory = instance.MemoryText,
                    Connection = _terminals.TryGetValue(instance.Name, out TerminalConnection t)
                        ? (t.Reconnecting ? "reconnecting" : "connected")
                        : "—"
                });
            }

            DataGridTerminals.SelectedItem = _terminalRows.FirstOrDefault(r => r.Name == selected);
        }

        private VpsInstance SelectedInstance()
        {
            string name = (DataGridTerminals.SelectedItem as TerminalRow)?.Name;
            VpsInstance instance = _instances.FirstOrDefault(i => i.Name == name);

            if (instance == null)
            {
                MessageBox.Show("Select a terminal in the list first");
            }

            return instance;
        }

        private bool EnsureSshCommands()
        {
            if (_sshTunnel != null && _sshTunnel.StartedByThisWindow)
            {
                return true;
            }

            MessageBox.Show("Connect to the VPS over SSH first");
            return false;
        }

        private async Task RunTerminalActionAsync(string what, Func<Task> action)
        {
            PanelTerminalButtons.IsEnabled = false;

            try
            {
                AppendLog(what + "...");
                await action().ConfigureAwait(true);
                AppendLog(what + ": done");
            }
            catch (Exception ex)
            {
                AppendLog(what + " failed: " + ex.Message);
            }
            finally
            {
                PanelTerminalButtons.IsEnabled = true;
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
        }

        private async void ButtonTerminalStart_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;
            await RunTerminalActionAsync($"Starting terminal \"{instance.Name}\"",
                () => VpsInstances.StartAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalStop_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi($"Stop terminal \"{instance.Name}\"? Its robots stop trading until it is started again.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Stopping terminal \"{instance.Name}\"",
                () => VpsInstances.StopAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalRestart_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi($"Restart terminal \"{instance.Name}\"? Its robots are stopped and started again.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Restarting terminal \"{instance.Name}\"",
                () => VpsInstances.RestartAsync(_sshTunnel.RunCommandAsync, instance)).ConfigureAwait(true);
        }

        private async void ButtonTerminalRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedInstance();
            if (instance == null) return;

            if (instance.IsMain)
            {
                MessageBox.Show("The main terminal cannot be removed");
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Remove terminal \"{instance.Name}\"?\n\nIts service is stopped and deleted. Its data (robots, settings, journals) "
                + "is not deleted but moved to /opt/osengine-removed on the VPS.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            await RunTerminalActionAsync($"Removing terminal \"{instance.Name}\"", async () =>
            {
                string moved = await VpsInstances.RemoveAsync(_sshTunnel.RunCommandAsync, instance).ConfigureAwait(true);
                AppendLog($"Data of terminal \"{instance.Name}\" moved to {moved.Trim()}");
            }).ConfigureAwait(true);
        }

        private async void ButtonTerminalAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            string name = TextBoxNewTerminal.Text.Trim().ToLowerInvariant();

            if (!VpsInstances.IsValidName(name))
            {
                MessageBox.Show("Terminal name: 1–20 characters, latin letters, digits and '-', not \"main\"");
                return;
            }

            if (_instances.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"Terminal \"{name}\" already exists");
                return;
            }

            int port = VpsInstances.NextFreePort(_instances);

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Create terminal \"{name}\" on the VPS?\n\nIts own folder {VpsInstances.BaseFolderFor(name)}, service "
                + $"{VpsInstances.ServiceFor(name)}, MCP port {port}. The build and the robot scripts are copied from the main "
                + "terminal; robots, connectors and keys start empty. Each terminal needs RAM (about 150–400 MB with robots).");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials;

            try
            {
                credentials = CreateCredentials();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }

            await RunTerminalActionAsync($"Creating terminal \"{name}\"", async () =>
            {
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .DeployAsync(name, port, CancellationToken.None)).ConfigureAwait(true);
                TextBoxNewTerminal.Text = "";
            }).ConfigureAwait(true);
        }

        #endregion

        #region Small helpers

        private void AppendLog(string message)
        {
            _log.Insert(0, new LogRow { Time = DateTime.Now, Message = message });

            while (_log.Count > 500)
            {
                _log.RemoveAt(_log.Count - 1);
            }

            ServerMaster.SendNewLogMessage("RobotsVps: " + message, LogMessageType.System);
        }

        #endregion
    }

    public class LogRow
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
    }

    public class TerminalRow
    {
        public string Name { get; set; }
        public int Port { get; set; }
        public string State { get; set; }
        public string Memory { get; set; }
        public string Connection { get; set; }
    }
}
