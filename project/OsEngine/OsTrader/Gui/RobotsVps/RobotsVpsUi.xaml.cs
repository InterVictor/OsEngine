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
            UpdatePackageText();

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
                if (lines.Length > 11 && bool.TryParse(lines[11], out bool dailyBackup)) CheckBoxDailyBackup.IsChecked = dailyBackup;
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
                    _computerKeyComment ?? "",
                    (CheckBoxDailyBackup.IsChecked == true).ToString()
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

        private void ButtonComputers_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            // the keys this computer logs in with: its registered key and the key file, if any
            string[] mine =
            {
                VpsComputers.Fingerprint(_computerKey, null),
                VpsComputers.Fingerprint(null, Environment.ExpandEnvironmentVariables(TextBoxSshKeyPath.Text.Trim().Trim('"')))
            };

            RobotsVpsComputersUi window = new RobotsVpsComputersUi(_sshTunnel.RunCommandAsync, mine, AppendLog) { Owner = this };
            window.ShowDialog();
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

                await SampleMetricsAsync(tunnel, instances).ConfigureAwait(true);
                RunDailyBackupIfDue(instances);

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
            ResetMonitoring();
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
                    Build = string.IsNullOrEmpty(instance.Build) ? "—" : instance.Build,
                    Cpu = _terminalCpu.TryGetValue(instance.Name, out double cpu) && !double.IsNaN(cpu) && instance.IsActive
                        ? cpu.ToString("0.0", CultureInfo.InvariantCulture) + " %" : "—",
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

        #region Maintenance: update build, upload robots, clean logs, reboot VPS

        private void UpdatePackageText()
        {
            try
            {
                string version = VpsProvisioner.LocalPackageVersion();
                string path = VpsProvisioner.LocalPath(VpsProvisioner.PackageFileName);

                TextBlockPackage.Text = version == null
                    ? "No build package in the VpsServer folder — \"Update build\" is not available"
                    : $"Build package on this computer: {version} ({File.GetLastWriteTime(path):dd.MM.yyyy HH:mm})";
            }
            catch (Exception ex)
            {
                TextBlockPackage.Text = "Build package: " + ex.Message;
            }
        }

        // The terminal selected in the list, or the only one when the VPS runs just one.
        private VpsInstance SelectedOrOnlyInstance()
        {
            if (DataGridTerminals.SelectedItem == null && _instances.Count == 1)
            {
                return _instances[0];
            }

            return SelectedInstance();
        }

        private async Task RunMaintenanceAsync(Func<Task> action)
        {
            PanelMaintenanceButtons.IsEnabled = false;
            PanelTerminalButtons.IsEnabled = false;

            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog("Failed: " + ex.Message);
            }
            finally
            {
                PanelMaintenanceButtons.IsEnabled = true;
                PanelTerminalButtons.IsEnabled = true;
                await SyncTerminalsAsync().ConfigureAwait(true);
            }
        }

        private async void ButtonUpdateBuild_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            UpdatePackageText();
            string version = VpsProvisioner.LocalPackageVersion();

            if (version == null)
            {
                MessageBox.Show("Put the server build package into the VpsServer folder first:\n"
                    + VpsProvisioner.LocalPath(VpsProvisioner.PackageFileName));
                return;
            }

            // a stopped terminal stays stopped: the update restarts the service, so only running ones are updated
            List<VpsInstance> running = _instances.Where(i => i.IsActive).ToList();
            List<VpsInstance> toUpdate = running.Where(i => i.Build != version).ToList();

            if (toUpdate.Count == 0)
            {
                MessageBox.Show(running.Count == 0 ? "No terminal is running" : $"All running terminals already run build {version}");
                return;
            }

            string list = string.Join("\n", toUpdate.Select(i => $"  {i.Name}: {(string.IsNullOrEmpty(i.Build) ? "unknown" : i.Build)} -> {version}"));
            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Update the OsEngine build?\n\n{list}\n\nTerminals are updated one by one; each one's robots stop for about "
                + "10–30 s. If a terminal does not start with the new build, its previous build is put back automatically. "
                + "Robots, settings, journals and keys are not touched.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Update build {version} ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .UpdateBuildAsync(toUpdate, CancellationToken.None)).ConfigureAwait(true);
                AppendLog("Build update finished");
            }).ConfigureAwait(true);
        }

        private async void ButtonUploadRobots_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Robot scripts for terminal \"{instance.Name}\"",
                Filter = "Robot scripts (*.cs)|*.cs",
                Multiselect = true
            };

            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;

            string[] files = dialog.FileNames;
            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Upload {files.Length} robot script(s) to terminal \"{instance.Name}\" and restart it?\n\n"
                + string.Join("\n", files.Select(f => "  " + Path.GetFileName(f)))
                + "\n\nA script with the same name is replaced (the old copy is kept in Custom/Robots-backup). "
                + "The restart compiles the new code; the terminal's robots stop for about 10–30 s.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Upload robots to terminal \"{instance.Name}\" ===");
                List<string> classNames = await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .UploadRobotsAsync(instance, files, CancellationToken.None)).ConfigureAwait(true);

                AppendLog($"Restarting terminal \"{instance.Name}\" to compile the new code...");
                await VpsInstances.RestartAsync(_sshTunnel.RunCommandAsync, instance).ConfigureAwait(true);

                await CheckRobotsCompileAsync(instance, classNames).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }

        // After the restart asks the terminal to build each uploaded robot (wiki_robot_info compiles a script on
        // demand). A compile error is taken from the service journal, where BotFactory writes the compiler output.
        private async Task CheckRobotsCompileAsync(VpsInstance instance, List<string> classNames)
        {
            List<string> pending = new List<string>(classNames);
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);

            while (pending.Count > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                await SyncTerminalsAsync().ConfigureAwait(true);

                RemoteMcpClient client = VpsRemoteSession.GetClient(instance.Name);
                if (client == null || !client.IsConnected) continue;

                foreach (string className in pending.ToList())
                {
                    try
                    {
                        await client.CallToolAsync("wiki_robot_info", new { class_name = className, is_script = true }).ConfigureAwait(true);
                        AppendLog($"Robot {className}: compiled OK");
                        pending.Remove(className);
                    }
                    catch (Exception ex) when (ex.Message.Contains("MCP tool", StringComparison.Ordinal))
                    {
                        // the terminal answered: the script does not build — show the compiler output
                        string details = "";
                        try
                        {
                            details = (await _sshTunnel.RunCommandAsync(
                                $"journalctl -u {instance.Service} --since '-5 min' --no-pager | grep -A4 'compilation problem (Path: Custom/Robots/{className}.cs' | tail -5 || true")
                                .ConfigureAwait(true)).Trim();
                        }
                        catch
                        {
                            // the journal is only a hint
                        }

                        AppendLog($"Robot {className}: DOES NOT COMPILE — {ex.Message}" + (details.Length > 0 ? "\n" + details : ""));
                        pending.Remove(className);
                    }
                    catch
                    {
                        // terminal still starting — try again
                        break;
                    }
                }
            }

            foreach (string className in pending)
            {
                AppendLog($"Robot {className}: could not check — the terminal did not answer in time");
            }
        }

        private async void ButtonCleanLogs_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Clean the logs of terminal \"{instance.Name}\"?\n\nIts OsEngine log files are deleted except today's; "
                + "the system journal of the VPS keeps the last 7 days. Robots keep working.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                string report = await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .CleanLogsAsync(instance, CancellationToken.None)).ConfigureAwait(true);
                AppendLog($"Logs of terminal \"{instance.Name}\" cleaned: {report}");
            }).ConfigureAwait(true);
        }

        private async void ButtonRebootVps_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Reboot the whole VPS {TextBoxSshHost.Text.Trim()}?\n\nAll terminals and their robots stop and start again with the "
                + "server, usually within 1–2 minutes. The connection comes back by itself.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread).RebootAsync(CancellationToken.None)).ConfigureAwait(true);
                AppendLog("VPS is rebooting — the connection comes back by itself in 1–2 minutes");
            }).ConfigureAwait(true);
        }

        #endregion

        #region Monitoring (CPU / RAM / disk of the VPS, per-terminal load) and data backups

        private VpsMonitor _monitor = new VpsMonitor();
        private readonly Dictionary<string, double> _terminalCpu = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // CPU alert only after a whole minute above the limit (6 samples x 10 s): short spikes are normal
        private readonly VpsAlarm _cpuAlarm = new VpsAlarm(90, 6);
        private readonly VpsAlarm _ramAlarm = new VpsAlarm(85, 1);
        private readonly VpsAlarm _diskAlarm = new VpsAlarm(90, 1);

        private bool _backupRunning;

        private async Task SampleMetricsAsync(SshTunnel tunnel, List<VpsInstance> instances)
        {
            VpsMetrics metrics;

            try
            {
                metrics = await _monitor.SampleAsync(tunnel.RunCommandAsync).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                TextBlockVpsInfo.Text = "Monitoring: " + ex.Message;
                return;
            }

            foreach (VpsInstance instance in instances)
            {
                _terminalCpu[instance.Name] = _monitor.ServiceCpuPercent(instance.Service, instance.CpuNanoseconds, metrics.Cores);
            }

            SparklineCpu.Add(metrics.CpuPercent);
            SparklineRam.Add(metrics.RamPercent);
            SparklineDisk.Add(metrics.DiskPercent);

            TextBlockCpu.Text = double.IsNaN(metrics.CpuPercent)
                ? $"CPU: — ({metrics.Cores} cores)"
                : $"CPU: {metrics.CpuPercent:0} % ({metrics.Cores} cores, load {metrics.Load1.ToString("0.00", CultureInfo.InvariantCulture)})";
            TextBlockRam.Text = $"RAM: {metrics.RamPercent:0} % ({VpsMonitor.FormatBytes(metrics.RamUsed)} of {VpsMonitor.FormatBytes(metrics.RamTotal)})"
                + (metrics.SwapTotal > 0 ? $", swap {VpsMonitor.FormatBytes(metrics.SwapUsed)}" : "");
            TextBlockDisk.Text = $"Disk: {metrics.DiskPercent:0} % ({VpsMonitor.FormatBytes(metrics.DiskUsed)} of {VpsMonitor.FormatBytes(metrics.DiskTotal)})";
            TextBlockVpsInfo.Text = "Uptime: " + (metrics.Uptime.TotalDays >= 1
                ? $"{(int)metrics.Uptime.TotalDays} d {metrics.Uptime.Hours} h"
                : $"{metrics.Uptime.Hours} h {metrics.Uptime.Minutes} min");

            if (_cpuAlarm.Check(metrics.CpuPercent))
                RaiseVpsAlert($"CPU load {metrics.CpuPercent:0} % for over a minute (limit {_cpuAlarm.Limit:0} %)");
            if (_ramAlarm.Check(metrics.RamPercent))
                RaiseVpsAlert($"Memory {metrics.RamPercent:0} % used: {VpsMonitor.FormatBytes(metrics.RamUsed)} of {VpsMonitor.FormatBytes(metrics.RamTotal)} (limit {_ramAlarm.Limit:0} %)");
            if (_diskAlarm.Check(metrics.DiskPercent))
                RaiseVpsAlert($"Disk {metrics.DiskPercent:0} % full: {VpsMonitor.FormatBytes(metrics.DiskTotal - metrics.DiskUsed)} free (limit {_diskAlarm.Limit:0} %) — clean the logs");
        }

        private void RaiseVpsAlert(string message)
        {
            string source = "VPS " + TextBoxSshHost.Text.Trim();
            AlertMessageManager.ThrowRemoteAlert(source, message, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            AppendLog("ALERT: " + message);
        }

        private void ResetMonitoring()
        {
            _monitor = new VpsMonitor();
            _terminalCpu.Clear();
            SparklineCpu.Clear();
            SparklineRam.Clear();
            SparklineDisk.Clear();
            TextBlockCpu.Text = "CPU: —";
            TextBlockRam.Text = "RAM: —";
            TextBlockDisk.Text = "Disk: —";
            TextBlockVpsInfo.Text = "Uptime: —";
        }

        private void CheckBoxDailyBackup_Click(object sender, RoutedEventArgs e) => SaveSettings();

        // While connected: one running terminal per 10 s tick whose newest local backup is older than a day.
        private void RunDailyBackupIfDue(List<VpsInstance> instances)
        {
            if (CheckBoxDailyBackup.IsChecked != true || _backupRunning)
            {
                return;
            }

            string host = TextBoxSshHost.Text.Trim();

            VpsInstance due = instances.FirstOrDefault(instance =>
            {
                if (!instance.IsActive) return false;
                string folder = VpsProvisioner.BackupFolder(host, instance.Name);
                DateTime newest = Directory.Exists(folder)
                    ? new DirectoryInfo(folder).GetFiles("*.tgz").Select(f => f.LastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max()
                    : DateTime.MinValue;
                return DateTime.Now - newest > TimeSpan.FromDays(1);
            });

            if (due != null)
            {
                _ = BackupTerminalAsync(due, "Daily backup");
            }
        }

        private async Task BackupTerminalAsync(VpsInstance instance, string what)
        {
            if (_backupRunning) return;
            _backupRunning = true;

            try
            {
                VpsSshCredentials credentials = CreateCredentials();
                AppendLog($"{what} of terminal \"{instance.Name}\"...");
                string file = await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .BackupAsync(instance, CancellationToken.None)).ConfigureAwait(true);
                AppendLog($"{what} of terminal \"{instance.Name}\" saved: {file} ({new FileInfo(file).Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                AppendLog($"{what} of terminal \"{instance.Name}\" failed: {ex.Message}");
            }
            finally
            {
                _backupRunning = false;
            }
        }

        private async void ButtonBackupData_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            if (_backupRunning)
            {
                MessageBox.Show("A backup is already running");
                return;
            }

            PanelBackupButtons.IsEnabled = false;

            try
            {
                await BackupTerminalAsync(instance, "Backup").ConfigureAwait(true);
            }
            finally
            {
                PanelBackupButtons.IsEnabled = true;
            }
        }

        private async void ButtonRestoreData_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureSshCommands()) return;
            VpsInstance instance = SelectedOrOnlyInstance();
            if (instance == null) return;

            string folder = VpsProvisioner.BackupFolder(TextBoxSshHost.Text.Trim(), instance.Name);
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"Backup to restore into terminal \"{instance.Name}\"",
                Filter = "OsEngine data backup (*.tgz)|*.tgz",
                InitialDirectory = Directory.Exists(folder) ? folder : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VpsBackups")
            };

            if (dialog.ShowDialog() != true) return;
            string archive = dialog.FileName;

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Restore terminal \"{instance.Name}\" from\n{Path.GetFileName(archive)}?\n\n"
                + "The terminal is stopped, its data (bots, settings, journals, robot scripts, MCP key) is replaced with the "
                + "backup and it is started again. The current data is NOT deleted: it stays on the VPS as "
                + "data.before-restore-<time>. Open positions of its robots are only what the backup contains.");
            confirm.ShowDialog();
            if (!confirm.UserAcceptAction) return;

            VpsSshCredentials credentials = CreateCredentials();

            await RunMaintenanceAsync(async () =>
            {
                AppendLog($"=== Restore terminal \"{instance.Name}\" from {Path.GetFileName(archive)} ===");
                await Task.Run(() => new VpsProvisioner(credentials, LogFromAnyThread)
                    .RestoreAsync(instance, archive, CancellationToken.None)).ConfigureAwait(true);

                // the MCP key may have changed with the data: reconnect this terminal with the key now on the VPS
                if (_terminals.TryGetValue(instance.Name, out TerminalConnection terminal))
                {
                    DropTerminal(terminal);
                    PublishSession();
                }
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
        public string Build { get; set; }
        public string Cpu { get; set; }
        public string Connection { get; set; }
    }
}
