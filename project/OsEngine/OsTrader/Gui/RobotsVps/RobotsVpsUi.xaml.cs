/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OsEngine.Logging;
using OsEngine.Market;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// "Bot Station VPS" — remote counterpart of Bot Station Lite: shows servers and bots of ANOTHER
    /// OsEngine instance (typically headless, on a VPS) over its MCP API, instead of running a local
    /// engine. First slice of the architecture: read-only servers/bots/journal view + server connect
    /// control, polled every few seconds (the remote MCP API has no bot/journal push events yet —
    /// see D:\ff-research\docs\SERVER_SETUP.md). Trading control (open/close positions, bot params)
    /// is added incrementally on top of the same RemoteMcpClient.
    /// «Роботы. VPS» — удалённый аналог Роботы.Lite: показывает серверы и боты ДРУГОГО экземпляра
    /// OsEngine (обычно headless, на VPS) через его MCP API, вместо запуска локального движка.
    /// </summary>
    public partial class RobotsVpsUi : Window
    {
        private const string SettingsFile = @"Engine\RobotsVpsSettings.txt";

        private RemoteMcpClient _client;
        private DispatcherTimer _pollTimer;
        private readonly ObservableCollection<ServerRow> _servers = new ObservableCollection<ServerRow>();
        private readonly ObservableCollection<BotRow> _bots = new ObservableCollection<BotRow>();
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();
        private volatile bool _pollInFlight;

        public RobotsVpsUi()
        {
            InitializeComponent();

            ServersDataGrid.ItemsSource = _servers;
            BotsDataGrid.ItemsSource = _bots;
            LogDataGrid.ItemsSource = _log;

            LoadSettings();

            Closed += (s, e) => Disconnect();
        }

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
                    PasswordBoxApiKey.Password = lines[1];
                }
            }
            catch (Exception ex)
            {
                AppendLog("Settings load failed: " + ex.Message);
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory("Engine");
                File.WriteAllLines(SettingsFile, new[] { TextBoxUrl.Text.Trim(), PasswordBoxApiKey.Password });
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

            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("MCP URL is required");
                return;
            }

            ButtonConnect.IsEnabled = false;
            SetStatus("Connecting...", Brushes.Orange);

            RemoteMcpClient client = new RemoteMcpClient(url, apiKey);
            client.EventReceived += Client_EventReceived;
            client.Disconnected += Client_Disconnected;

            try
            {
                await client.ConnectAsync().ConfigureAwait(true);

                _client = client;
                SaveSettings();
                SetStatus("Connected", Brushes.Green);
                ButtonDisconnect.IsEnabled = true;
                AppendLog("Connected to " + url);

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _pollTimer.Tick += async (s, args) => await PollAsync().ConfigureAwait(true);
                _pollTimer.Start();

                await PollAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                client.EventReceived -= Client_EventReceived;
                client.Disconnected -= Client_Disconnected;
                client.Dispose();
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
            _pollTimer?.Stop();
            _pollTimer = null;

            if (_client != null)
            {
                _client.EventReceived -= Client_EventReceived;
                _client.Disconnected -= Client_Disconnected;
                _client.Dispose();
                _client = null;
            }

            _servers.Clear();
            _bots.Clear();
            ButtonConnect.IsEnabled = true;
            ButtonDisconnect.IsEnabled = false;
            SetStatus("Disconnected", Brushes.Gray);
        }

        private void Client_Disconnected(Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                SetStatus("Reconnecting...", Brushes.Orange);
                AppendLog("SSE stream dropped: " + ex.Message);
            });
        }

        private void Client_EventReceived(string eventName, JsonElement payload)
        {
            Dispatcher.Invoke(() => AppendLog(eventName));
        }

        private void SetStatus(string text, Brush color)
        {
            LabelStatus.Content = text;
            EllipseStatus.Fill = color;
        }

        #endregion

        #region Polling (servers + bots + selected bot journal)

        private async Task PollAsync()
        {
            RemoteMcpClient client = _client;

            if (client == null || !client.IsConnected || _pollInFlight)
            {
                return;
            }

            _pollInFlight = true;

            try
            {
                await RefreshServersAsync(client).ConfigureAwait(true);
                await RefreshBotsAsync(client).ConfigureAwait(true);
                await RefreshSelectedBotJournalAsync(client).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog("Poll failed: " + ex.Message);
            }
            finally
            {
                _pollInFlight = false;
            }
        }

        private async Task RefreshServersAsync(RemoteMcpClient client)
        {
            JsonElement result = await client.CallToolAsync("server_management_get_list", null).ConfigureAwait(true);

            if (result.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            // сохраняем выбор/скролл: обновляем по месту, а не Clear+Add (реже дёргает грид)
            int i = 0;

            foreach (JsonElement item in result.EnumerateArray())
            {
                string type = GetString(item, "type");
                int number = GetInt(item, "number");
                string status = GetString(item, "status");

                if (i < _servers.Count)
                {
                    _servers[i].Type = type;
                    _servers[i].Number = number;
                    _servers[i].Status = status;
                }
                else
                {
                    _servers.Add(new ServerRow { Type = type, Number = number, Status = status });
                }

                i++;
            }

            while (_servers.Count > i)
            {
                _servers.RemoveAt(_servers.Count - 1);
            }
        }

        private async Task RefreshBotsAsync(RemoteMcpClient client)
        {
            JsonElement result = await client.CallToolAsync("bot_get_list", null).ConfigureAwait(true);

            if (!result.TryGetProperty("bots", out JsonElement bots) || bots.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int i = 0;

            foreach (JsonElement item in bots.EnumerateArray())
            {
                int number = GetInt(item, "number");
                string name = GetString(item, "name");
                string className = GetString(item, "class_name");

                if (i < _bots.Count)
                {
                    _bots[i].Number = number;
                    _bots[i].Name = name;
                    _bots[i].ClassName = className;
                }
                else
                {
                    _bots.Add(new BotRow { Number = number, Name = name, ClassName = className });
                }

                i++;
            }

            while (_bots.Count > i)
            {
                _bots.RemoveAt(_bots.Count - 1);
            }
        }

        private void BotsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _ = RefreshSelectedBotJournalAsync(_client);
        }

        private async Task RefreshSelectedBotJournalAsync(RemoteMcpClient client)
        {
            if (client == null || !client.IsConnected)
            {
                return;
            }

            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            if (selected == null)
            {
                LabelBotProfitAbs.Content = "—";
                LabelBotProfitPercent.Content = "—";
                LabelBotDeals.Content = "—";
                LabelBotCommission.Content = "—";
                return;
            }

            try
            {
                JsonElement summary = await client.CallToolAsync("bot_journal_get_summary",
                    new { bot_name = selected.Name }).ConfigureAwait(true);
                JsonElement statistics = await client.CallToolAsync("bot_journal_get_statistics",
                    new { bot_name = selected.Name }).ConfigureAwait(true);

                LabelBotProfitAbs.Content = GetDecimal(summary, "total_profit_abs").ToString("0.########");
                LabelBotProfitPercent.Content = GetDecimal(summary, "total_profit_percent").ToString("0.####") + " %";
                LabelBotDeals.Content = GetInt(statistics, "deals_count").ToString();
                LabelBotCommission.Content = GetDecimal(statistics, "commission").ToString("0.########");
            }
            catch (Exception ex)
            {
                AppendLog("Journal for '" + selected.Name + "' failed: " + ex.Message);
            }
        }

        #endregion

        #region Server connect / disconnect buttons

        private async void ServerConnectButton_Click(object sender, RoutedEventArgs e)
        {
            await CallServerCommand((Button)sender, "server_instance_connect").ConfigureAwait(true);
        }

        private async void ServerDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await CallServerCommand((Button)sender, "server_instance_disconnect").ConfigureAwait(true);
        }

        private async Task CallServerCommand(Button button, string toolName)
        {
            if (_client == null || !(button.Tag is ServerRow row))
            {
                return;
            }

            try
            {
                await _client.CallToolAsync(toolName, new { type = row.Type, number = row.Number }).ConfigureAwait(true);
                AppendLog(toolName + " " + row.Type + "#" + row.Number);
            }
            catch (Exception ex)
            {
                AppendLog(toolName + " failed: " + ex.Message);
            }
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

        private static string GetString(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : "";

        private static int GetInt(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i)
                ? i : 0;

        private static decimal GetDecimal(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetDecimal(out decimal d)
                ? d : 0m;

        #endregion
    }

    public class ServerRow
    {
        public string Type { get; set; }
        public int Number { get; set; }
        public string Status { get; set; }
    }

    public class BotRow
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public string ClassName { get; set; }
    }

    public class LogRow
    {
        public DateTime Time { get; set; }
        public string Message { get; set; }
    }
}
