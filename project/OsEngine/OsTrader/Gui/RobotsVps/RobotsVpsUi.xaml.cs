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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OsEngine.Alerts;
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

        private RemoteMcpClient _client;
        private SshTunnel _sshTunnel;
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();

        public RobotsVpsUi()
        {
            InitializeComponent();

            LogDataGrid.ItemsSource = _log;

            TextBoxSshKeyPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "ff_server");

            LoadSettings();

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
                if (lines.Length > 4 && !string.IsNullOrWhiteSpace(lines[4])) TextBoxSshKeyPath.Text = lines[4];
                if (lines.Length > 5 && !string.IsNullOrWhiteSpace(lines[5])) TextBoxSshLocalPort.Text = lines[5];
                if (lines.Length > 6 && !string.IsNullOrWhiteSpace(lines[6])) TextBoxSshRemotePort.Text = lines[6];
                if (lines.Length > 7 && bool.TryParse(lines[7], out bool autoConnect)) CheckBoxAutoConnectSsh.IsChecked = autoConnect;
                if (lines.Length > 8 && !string.IsNullOrWhiteSpace(lines[8])) PasswordBoxSshPassword.Password = UnprotectSecret(lines[8]);
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
                    ProtectSecret(PasswordBoxSshPassword.Password)
                });
            }
            catch (Exception ex)
            {
                AppendLog("Settings save failed: " + ex.Message);
            }
        }

        #endregion

        #region Connect / Disconnect

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

            RemoteMcpClient client = null;
            SshTunnel tunnel = null;

            try
            {
                SaveSettings();

                if (!string.IsNullOrWhiteSpace(TextBoxSshHost.Text))
                {
                    if (!int.TryParse(TextBoxSshLocalPort.Text, out int localPort)
                        || !int.TryParse(TextBoxSshRemotePort.Text, out int remotePort))
                    {
                        throw new FormatException("SSH local and VPS API ports must be whole numbers");
                    }

                    tunnel = await SshTunnel.StartAsync(
                        TextBoxSshHost.Text.Trim(),
                        TextBoxSshUser.Text.Trim(),
                        TextBoxSshKeyPath.Text,
                        PasswordBoxSshPassword.Password,
                        localPort,
                        remotePort,
                        message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message))));

                    url = $"http://127.0.0.1:{localPort}/api/v2/mcp";
                    TextBoxUrl.Text = url;

                    // The MCP API key lives on the VPS (/opt/osengine/mcp.key); read it over the same SSH
                    // connection instead of relying on what was typed into the API Key box.
                    if (tunnel.StartedByThisWindow)
                    {
                        try
                        {
                            string serverKey = (await tunnel.RunCommandAsync("cat /opt/osengine/mcp.key").ConfigureAwait(true)).Trim();

                            if (serverKey.Length > 0)
                            {
                                if (serverKey != apiKey) AppendLog("MCP API key read from the VPS");
                                apiKey = serverKey;
                                PasswordBoxApiKey.Password = serverKey;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppendLog("Could not read the MCP API key from the VPS, using the API Key box: " + ex.Message);
                        }
                    }
                }

                client = new RemoteMcpClient(url, apiKey);
                client.EventReceived += Client_EventReceived;
                client.Disconnected += Client_Disconnected;
                client.Reconnected += Client_Reconnected;
                await client.ConnectAsync().ConfigureAwait(true);

                _client = client;
                VpsRemoteSession.SetClient(client);
                _sshTunnel = tunnel;
                tunnel = null;
                SaveSettings();
                SetStatus("Connected", Brushes.Green);
                ButtonDisconnect.IsEnabled = true;
                AppendLog("Connected to " + url
                    + (_sshTunnel?.StartedByThisWindow == true ? " (SSH tunnel started by Robots.VPS)"
                        : !string.IsNullOrWhiteSpace(TextBoxSshHost.Text) ? " (SSH tunnel already running)" : ""));
            }
            catch (Exception ex)
            {
                if (client != null)
                {
                    client.EventReceived -= Client_EventReceived;
                    client.Disconnected -= Client_Disconnected;
                    client.Reconnected -= Client_Reconnected;
                    client.Dispose();
                }
                tunnel?.Dispose();
                ButtonConnect.IsEnabled = true;
                SetStatus("Disconnected", Brushes.Gray);
                AppendLog("Connect failed: " + ex.Message);
            }
        }

        private void ButtonDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            if (_client != null)
            {
                if (ReferenceEquals(VpsRemoteSession.Client, _client))
                {
                    VpsRemoteSession.SetClient(null);
                }
                _client.EventReceived -= Client_EventReceived;
                _client.Disconnected -= Client_Disconnected;
                _client.Reconnected -= Client_Reconnected;
                _client.Dispose();
                _client = null;
            }

            if (_sshTunnel != null)
            {
                bool stoppedTunnel = _sshTunnel.StartedByThisWindow;
                _sshTunnel.Dispose();
                _sshTunnel = null;
                if (stoppedTunnel) AppendLog("SSH tunnel stopped");
            }

            _reconnecting = false;
            ButtonConnect.IsEnabled = true;
            ButtonDisconnect.IsEnabled = false;
            SetStatus("Disconnected", Brushes.Gray);
        }

        // Raised on every failed retry (~2 s) while the server is unreachable — log only the first one.
        private bool _reconnecting;

        private void Client_Disconnected(Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("Reconnecting...", Brushes.Orange);
                if (_reconnecting) return;
                _reconnecting = true;
                AppendLog("Connection to the VPS lost, reconnecting: " + ex.Message);
            });
        }

        private void Client_Reconnected()
        {
            Dispatcher.Invoke(() =>
            {
                _reconnecting = false;
                SetStatus("Connected", Brushes.Green);
                AppendLog("Connection to the VPS restored");
            });
        }

        private void Client_EventReceived(string eventName, JsonElement payload)
        {
            Dispatcher.Invoke(() =>
            {
                if (eventName == "alert.raised")
                {
                    string botName = payload.TryGetProperty("bot_name", out JsonElement bot) ? bot.GetString() : "VPS";
                    string message = payload.TryGetProperty("message", out JsonElement text) ? text.GetString() : "";
                    string time = payload.TryGetProperty("time", out JsonElement timestamp) ? timestamp.GetString() : DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    AlertMessageManager.ThrowRemoteAlert(botName, message, time);
                    AppendLog("Emergency alert received from VPS: " + botName);
                    return;
                }

                // heartbeat arrives every 5 s just to keep the stream alive — not worth a log line
                if (eventName == "heartbeat") return;

                AppendLog(eventName);
            });
        }

        private void SetStatus(string text, Brush color)
        {
            LabelStatus.Content = text;
            EllipseStatus.Fill = color;
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
}
