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
using System.Text.Json;
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
    /// see D:\ff-research\docs\SERVER_SETUP.md). This slice adds robot creation, parameter editing
    /// and connector-tab configuration (incl. emulator mode) on top of the same RemoteMcpClient.
    /// «Роботы. VPS» — удалённый аналог Роботы.Lite: показывает серверы и боты ДРУГОГО экземпляра
    /// OsEngine (обычно headless, на VPS) через его MCP API, вместо запуска локального движка.
    /// </summary>
    public partial class RobotsVpsUi : Window
    {
        private const string SettingsFile = @"Engine\RobotsVpsSettings.txt";

        private RemoteMcpClient _client;
        private SshTunnel _sshTunnel;
        private DispatcherTimer _pollTimer;
        private readonly ObservableCollection<ServerRow> _servers = new ObservableCollection<ServerRow>();
        private readonly ObservableCollection<BotRow> _bots = new ObservableCollection<BotRow>();
        private readonly ObservableCollection<LogRow> _log = new ObservableCollection<LogRow>();
        private readonly ObservableCollection<ParamRow> _params = new ObservableCollection<ParamRow>();
        private volatile bool _pollInFlight;

        // выбранная вкладка бота на "Tab config" — храним тип, чтобы Save знал, какой bot_set_config_tab_* звать
        private TabInfo _selectedTab;

        private static readonly string[] CommonTimeFrames =
        {
            "Sec1", "Sec2", "Sec5", "Sec10", "Sec15", "Sec20", "Sec30",
            "Min1", "Min2", "Min3", "Min5", "Min10", "Min15", "Min20", "Min30", "Min45",
            "Hour1", "Hour2", "Hour4", "Day"
        };

        public RobotsVpsUi()
        {
            InitializeComponent();

            ServersDataGrid.ItemsSource = _servers;
            BotsDataGrid.ItemsSource = _bots;
            LogDataGrid.ItemsSource = _log;
            ParametersDataGrid.ItemsSource = _params;

            ComboBoxSimpleTimeFrame.ItemsSource = CommonTimeFrames;
            ComboBoxScreenerTimeFrame.ItemsSource = CommonTimeFrames;

            TextBoxSshKeyPath.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "ff_server");

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

                if (lines.Length > 2) TextBoxSshHost.Text = lines[2];
                if (lines.Length > 3) TextBoxSshUser.Text = lines[3];
                if (lines.Length > 4 && !string.IsNullOrWhiteSpace(lines[4])) TextBoxSshKeyPath.Text = lines[4];
                if (lines.Length > 5 && !string.IsNullOrWhiteSpace(lines[5])) TextBoxSshLocalPort.Text = lines[5];
                if (lines.Length > 6 && !string.IsNullOrWhiteSpace(lines[6])) TextBoxSshRemotePort.Text = lines[6];
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
                File.WriteAllLines(SettingsFile, new[]
                {
                    TextBoxUrl.Text.Trim(),
                    PasswordBoxApiKey.Password,
                    TextBoxSshHost.Text.Trim(),
                    TextBoxSshUser.Text.Trim(),
                    TextBoxSshKeyPath.Text.Trim(),
                    TextBoxSshLocalPort.Text.Trim(),
                    TextBoxSshRemotePort.Text.Trim()
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
                        localPort,
                        remotePort,
                        message => Dispatcher.BeginInvoke(new Action(() => AppendLog(message))));

                    url = $"http://127.0.0.1:{localPort}/api/v2/mcp";
                    TextBoxUrl.Text = url;
                }

                client = new RemoteMcpClient(url, apiKey);
                client.EventReceived += Client_EventReceived;
                client.Disconnected += Client_Disconnected;
                await client.ConnectAsync().ConfigureAwait(true);

                _client = client;
                _sshTunnel = tunnel;
                tunnel = null;
                SaveSettings();
                SetStatus("Connected", Brushes.Green);
                ButtonDisconnect.IsEnabled = true;
                AppendLog("Connected to " + url
                    + (_sshTunnel?.StartedByThisWindow == true ? " (SSH tunnel started by Robots.VPS)"
                        : !string.IsNullOrWhiteSpace(TextBoxSshHost.Text) ? " (SSH tunnel already running)" : ""));

                _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _pollTimer.Tick += async (s, args) => await PollAsync().ConfigureAwait(true);
                _pollTimer.Start();

                await PollAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (client != null)
                {
                    client.EventReceived -= Client_EventReceived;
                    client.Disconnected -= Client_Disconnected;
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
            _pollTimer?.Stop();
            _pollTimer = null;

            if (_client != null)
            {
                _client.EventReceived -= Client_EventReceived;
                _client.Disconnected -= Client_Disconnected;
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

            _servers.Clear();
            _bots.Clear();
            _params.Clear();
            TabsComboBox.ItemsSource = null;
            ShowTabPanel(null);
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

        // SelectionChanged фильтра-неустойчив к обновлению строк по месту (RefreshBotsAsync) — он срабатывает
        // только когда реально меняется выбранный объект, поэтому опрос раз в 5с не дёргает параметры/вкладки.
        private void BotsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            _ = RefreshSelectedBotJournalAsync(_client);
            _ = LoadParametersAsync(selected);
            _ = LoadTabsAsync(selected);
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

        #region Create / delete bot (wiki_robots_list, bot_create, bot_delete)

        private async void ButtonCreateBot_Click(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                MessageBox.Show("Connect first");
                return;
            }

            CreateBotDialog dialog = new CreateBotDialog(_client) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                JsonElement result = await _client.CallToolAsync("bot_create", new
                {
                    strategy_name = dialog.SelectedStrategy,
                    name = string.IsNullOrWhiteSpace(dialog.BotName) ? null : dialog.BotName.Trim()
                }).ConfigureAwait(true);

                string createdName = GetString(result, "name");
                AppendLog("Created bot '" + createdName + "' (" + dialog.SelectedStrategy + ")");

                await RefreshBotsAsync(_client).ConfigureAwait(true);

                BotRow createdRow = _bots.FirstOrDefault(b => b.Name == createdName);
                if (createdRow != null)
                {
                    BotsDataGrid.SelectedItem = createdRow;
                    BotsDataGrid.ScrollIntoView(createdRow);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("bot_create failed: " + ex.Message);
            }
        }

        private async void ButtonDeleteBot_Click(object sender, RoutedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            if (_client == null || selected == null)
            {
                return;
            }

            if (MessageBox.Show($"Delete bot '{selected.Name}' on the VPS?", "Confirm",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await _client.CallToolAsync("bot_delete", new { bot_id = selected.Name }).ConfigureAwait(true);
                AppendLog("Deleted bot '" + selected.Name + "'");
                await RefreshBotsAsync(_client).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("bot_delete failed: " + ex.Message);
            }
        }

        #endregion

        #region Parameters (bot_get_params / bot_set_params / bot_click_param_button)

        private async Task LoadParametersAsync(BotRow bot)
        {
            _params.Clear();
            LabelParametersStatus.Content = "";

            if (_client == null || bot == null)
            {
                return;
            }

            try
            {
                JsonElement result = await _client.CallToolAsync("bot_get_params", new { bot_id = bot.Name }).ConfigureAwait(true);

                if (!result.TryGetProperty("parameters", out JsonElement parameters) || parameters.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                foreach (JsonElement p in parameters.EnumerateArray())
                {
                    ParamRow row = ParamRow.FromJson(p);
                    if (row != null)
                    {
                        _params.Add(row);
                    }
                }
            }
            catch (Exception ex)
            {
                LabelParametersStatus.Content = "Load failed: " + ex.Message;
            }
        }

        private async void ButtonSaveParameters_Click(object sender, RoutedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            if (_client == null || selected == null)
            {
                return;
            }

            Dictionary<string, object> toSet = new Dictionary<string, object>();

            foreach (ParamRow row in _params)
            {
                try
                {
                    object value = row.ToWireValue();
                    if (value != ParamRow.NotSettable)
                    {
                        toSet[row.Name] = value;
                    }
                }
                catch (Exception ex)
                {
                    LabelParametersStatus.Content = $"'{row.Name}': {ex.Message}";
                    return;
                }
            }

            try
            {
                JsonElement result = await _client.CallToolAsync("bot_set_params",
                    new { bot_id = selected.Name, parameters = toSet }).ConfigureAwait(true);

                int updated = GetInt(result, "updated_count");
                int notFound = GetInt(result, "not_found_count");
                LabelParametersStatus.Content = $"Saved: {updated} updated" + (notFound > 0 ? $", {notFound} not found" : "");
                AppendLog($"bot_set_params '{selected.Name}': {updated} updated");
            }
            catch (Exception ex)
            {
                LabelParametersStatus.Content = "Save failed: " + ex.Message;
            }
        }

        private async void ParamButton_Click(object sender, RoutedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;
            string paramName = (sender as Button)?.Tag as string;

            if (_client == null || selected == null || paramName == null)
            {
                return;
            }

            try
            {
                await _client.CallToolAsync("bot_click_param_button",
                    new { bot_id = selected.Name, param_name = paramName }).ConfigureAwait(true);
                AppendLog($"Clicked '{paramName}' on '{selected.Name}'");
            }
            catch (Exception ex)
            {
                AppendLog($"bot_click_param_button '{paramName}' failed: " + ex.Message);
            }
        }

        #endregion

        #region Tab config (bot_get_sources + bot_get/set_config_tab_simple|screener — incl. emulator_is_on)

        private async Task LoadTabsAsync(BotRow bot)
        {
            TabsComboBox.ItemsSource = null;
            ShowTabPanel(null);

            if (_client == null || bot == null)
            {
                return;
            }

            try
            {
                JsonElement result = await _client.CallToolAsync("bot_get_sources", new { bot_id = bot.Name }).ConfigureAwait(true);

                if (!result.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
                {
                    return;
                }

                List<TabInfo> tabs = sources.EnumerateArray()
                    .Select(s => new TabInfo { Name = GetString(s, "name"), Type = GetString(s, "type") })
                    .Where(t => !string.IsNullOrEmpty(t.Name))
                    .ToList();

                TabsComboBox.ItemsSource = tabs;

                if (tabs.Count > 0)
                {
                    TabsComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                LabelTabConfigStatus.Content = "Load failed: " + ex.Message;
            }
        }

        private async void TabsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedTab = TabsComboBox.SelectedItem as TabInfo;
            await LoadSelectedTabConfigAsync().ConfigureAwait(true);
        }

        private async void ButtonReloadTabConfig_Click(object sender, RoutedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;
            await LoadTabsAsync(selected).ConfigureAwait(true);
        }

        private void ShowTabPanel(string type)
        {
            SimpleConfigPanel.Visibility = type == "Simple" ? Visibility.Visible : Visibility.Collapsed;
            ScreenerConfigPanel.Visibility = type == "Screener" ? Visibility.Visible : Visibility.Collapsed;
            TextBlockTabUnsupported.Visibility = type != null && type != "Simple" && type != "Screener"
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task LoadSelectedTabConfigAsync()
        {
            LabelTabConfigStatus.Content = "";
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            if (_client == null || selected == null || _selectedTab == null)
            {
                ShowTabPanel(null);
                return;
            }

            ShowTabPanel(_selectedTab.Type);

            try
            {
                if (_selectedTab.Type == "Simple")
                {
                    JsonElement config = await _client.CallToolAsync("bot_get_config_tab_simple",
                        new { bot_id = selected.Name, tab_name = _selectedTab.Name }).ConfigureAwait(true);

                    TextBoxSimpleServerType.Text = GetString(config, "server_type");
                    TextBoxSimpleServerFullName.Text = GetString(config, "server_full_name");
                    TextBoxSimpleSecurityClass.Text = GetString(config, "security_class");
                    TextBoxSimpleSecurityName.Text = GetString(config, "security_name");
                    TextBoxSimplePortfolio.Text = GetString(config, "portfolio_name");
                    ComboBoxSimpleTimeFrame.Text = GetString(config, "time_frame");
                    SetComboBoxItem(ComboBoxSimpleCommissionType, GetString(config, "commission_type"));
                    TextBoxSimpleCommissionValue.Text = GetDecimal(config, "commission_value").ToString(CultureInfo.InvariantCulture);
                    CheckBoxSimpleEmulator.IsChecked = GetBool(config, "emulator_is_on");
                }
                else if (_selectedTab.Type == "Screener")
                {
                    JsonElement config = await _client.CallToolAsync("bot_get_config_tab_screener",
                        new { bot_id = selected.Name, tab_name = _selectedTab.Name }).ConfigureAwait(true);

                    TextBoxScreenerServerType.Text = GetString(config, "server_type");
                    TextBoxScreenerServerName.Text = GetString(config, "server_name");
                    TextBoxScreenerPortfolio.Text = GetString(config, "portfolio_name");
                    ComboBoxScreenerTimeFrame.Text = GetString(config, "time_frame");
                    SetComboBoxItem(ComboBoxScreenerCommissionType, GetString(config, "commission_type"));
                    TextBoxScreenerCommissionValue.Text = GetDecimal(config, "commission_value").ToString(CultureInfo.InvariantCulture);
                    CheckBoxScreenerEmulator.IsChecked = GetBool(config, "emulator_is_on");
                    int tabsCount = GetInt(config, "tabs_count");
                    LabelScreenerSecuritiesCount.Content = tabsCount > 0 ? $"{tabsCount} securities (edit list in the terminal)" : "";
                }
            }
            catch (Exception ex)
            {
                LabelTabConfigStatus.Content = "Load failed: " + ex.Message;
            }
        }

        private async void ButtonSaveTabConfig_Click(object sender, RoutedEventArgs e)
        {
            BotRow selected = BotsDataGrid.SelectedItem as BotRow;

            if (_client == null || selected == null || _selectedTab == null)
            {
                return;
            }

            try
            {
                if (_selectedTab.Type == "Simple")
                {
                    Dictionary<string, object> args = new Dictionary<string, object>
                    {
                        ["bot_id"] = selected.Name,
                        ["tab_name"] = _selectedTab.Name,
                        ["emulator_is_on"] = CheckBoxSimpleEmulator.IsChecked == true
                    };
                    AddIfNotEmpty(args, "server_type", TextBoxSimpleServerType.Text);
                    AddIfNotEmpty(args, "server_full_name", TextBoxSimpleServerFullName.Text);
                    AddIfNotEmpty(args, "security_class", TextBoxSimpleSecurityClass.Text);
                    AddIfNotEmpty(args, "security_name", TextBoxSimpleSecurityName.Text);
                    AddIfNotEmpty(args, "portfolio_name", TextBoxSimplePortfolio.Text);
                    AddIfNotEmpty(args, "time_frame", ComboBoxSimpleTimeFrame.Text);
                    AddIfNotEmpty(args, "commission_type", (ComboBoxSimpleCommissionType.SelectedItem as ComboBoxItem)?.Content as string);
                    if (decimal.TryParse(TextBoxSimpleCommissionValue.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal commission)
                        || decimal.TryParse(TextBoxSimpleCommissionValue.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out commission))
                    {
                        args["commission_value"] = commission;
                    }

                    await _client.CallToolAsync("bot_set_config_tab_simple", args).ConfigureAwait(true);
                }
                else if (_selectedTab.Type == "Screener")
                {
                    Dictionary<string, object> args = new Dictionary<string, object>
                    {
                        ["bot_id"] = selected.Name,
                        ["tab_name"] = _selectedTab.Name,
                        ["emulator_is_on"] = CheckBoxScreenerEmulator.IsChecked == true
                    };
                    AddIfNotEmpty(args, "server_type", TextBoxScreenerServerType.Text);
                    AddIfNotEmpty(args, "server_name", TextBoxScreenerServerName.Text);
                    AddIfNotEmpty(args, "portfolio_name", TextBoxScreenerPortfolio.Text);
                    AddIfNotEmpty(args, "time_frame", ComboBoxScreenerTimeFrame.Text);
                    AddIfNotEmpty(args, "commission_type", (ComboBoxScreenerCommissionType.SelectedItem as ComboBoxItem)?.Content as string);
                    if (decimal.TryParse(TextBoxScreenerCommissionValue.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal commission)
                        || decimal.TryParse(TextBoxScreenerCommissionValue.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out commission))
                    {
                        args["commission_value"] = commission;
                    }

                    await _client.CallToolAsync("bot_set_config_tab_screener", args).ConfigureAwait(true);
                }
                else
                {
                    return;
                }

                LabelTabConfigStatus.Content = "Saved";
                AppendLog($"Saved tab config '{_selectedTab.Name}' on '{selected.Name}'");
                await LoadSelectedTabConfigAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LabelTabConfigStatus.Content = "Save failed: " + ex.Message;
            }
        }

        private static void AddIfNotEmpty(Dictionary<string, object> args, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                args[key] = value;
            }
        }

        private static void SetComboBoxItem(ComboBox box, string content)
        {
            foreach (ComboBoxItem item in box.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Content as string, content, StringComparison.OrdinalIgnoreCase))
                {
                    box.SelectedItem = item;
                    return;
                }
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

        private static bool GetBool(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v)
            && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

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

    /// <summary>tab from bot_get_sources — combo box item, shown as "Name (Type)".</summary>
    public class TabInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public override string ToString() => $"{Name} ({Type})";
    }

    /// <summary>
    /// One row of bot_get_params, editable in a WPF DataGrid via visibility-switched controls
    /// (see RobotsVpsUi.xaml, Parameters tab). Mirrors RobotsApi.SerializeParameter/SetParameterValue
    /// on the server: types match 1:1, ToWireValue() produces exactly what SetParameterValue expects.
    /// </summary>
    public class ParamRow
    {
        /// <summary>sentinel returned by ToWireValue() for types bot_set_params does not accept (Button, Label).</summary>
        public static readonly object NotSettable = new object();

        public string Name { get; set; }
        public string TypeStr { get; set; }

        /// <summary>enum values for a String parameter with a fixed choice list (ValuesString on the server).</summary>
        public List<string> Values { get; set; }

        public string StringValue { get; set; }
        public string TextValue { get; set; }
        public bool BoolValue { get; set; }

        public bool IsEnumString => TypeStr == "String" && Values != null && Values.Count > 0;
        public bool IsFreeText => TypeStr == "Int" || TypeStr == "Decimal" || TypeStr == "TimeOfDay"
                                   || (TypeStr == "String" && !IsEnumString);
        public bool IsBoolLike => TypeStr == "Bool" || TypeStr == "CheckBox";
        public bool IsDecimalCheckBox => TypeStr == "DecimalCheckBox";
        public bool IsButton => TypeStr == "Button";

        public static ParamRow FromJson(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("type", out JsonElement typeEl))
            {
                return null;
            }

            string type = typeEl.GetString();
            string name = p.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : "";
            ParamRow row = new ParamRow { Name = name, TypeStr = type };

            switch (type)
            {
                case "Int":
                    row.TextValue = GetRawNumber(p, "value");
                    break;

                case "Decimal":
                case "DecimalCheckBox":
                    row.TextValue = GetRawNumber(p, "value");
                    if (type == "DecimalCheckBox" && p.TryGetProperty("check_state", out JsonElement checkEl))
                    {
                        row.BoolValue = string.Equals(checkEl.GetString(), "Checked", StringComparison.OrdinalIgnoreCase);
                    }
                    break;

                case "String":
                    row.StringValue = p.TryGetProperty("value", out JsonElement sv) ? sv.GetString() : "";
                    row.TextValue = row.StringValue;
                    if (p.TryGetProperty("values", out JsonElement valuesEl) && valuesEl.ValueKind == JsonValueKind.Array)
                    {
                        row.Values = valuesEl.EnumerateArray().Select(v => v.GetString()).ToList();
                    }
                    break;

                case "Bool":
                    row.BoolValue = p.TryGetProperty("value", out JsonElement bv)
                        && (bv.ValueKind == JsonValueKind.True || bv.ValueKind == JsonValueKind.False) && bv.GetBoolean();
                    break;

                case "TimeOfDay":
                    row.TextValue = p.TryGetProperty("value", out JsonElement tv) ? tv.GetString() : "";
                    break;

                case "CheckBox":
                    string checkState = p.TryGetProperty("value", out JsonElement cv) ? cv.GetString() : "Unchecked";
                    row.BoolValue = string.Equals(checkState, "Checked", StringComparison.OrdinalIgnoreCase);
                    break;

                case "Button":
                case "Label":
                    break;

                default:
                    return null;
            }

            return row;
        }

        private static string GetRawNumber(JsonElement p, string prop) =>
            p.TryGetProperty(prop, out JsonElement v) ? v.GetRawText() : "0";

        /// <summary>
        /// Value to send in bot_set_params[Name], in the exact shape RobotsApi.SetParameterValue expects.
        /// Throws with a message safe to show the user when the typed text does not parse.
        /// Returns NotSettable for Button/Label (the server rejects them from this endpoint).
        /// </summary>
        public object ToWireValue()
        {
            switch (TypeStr)
            {
                case "Int":
                    if (!int.TryParse(TextValue, NumberStyles.Any, CultureInfo.InvariantCulture, out int i))
                    {
                        throw new FormatException("not a whole number: '" + TextValue + "'");
                    }
                    return i;

                case "Decimal":
                case "DecimalCheckBox":
                    if (!decimal.TryParse(TextValue, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d)
                        && !decimal.TryParse(TextValue, NumberStyles.Any, CultureInfo.CurrentCulture, out d))
                    {
                        throw new FormatException("not a number: '" + TextValue + "'");
                    }
                    return d;

                case "String":
                    return StringValue ?? TextValue ?? "";

                case "Bool":
                case "CheckBox":
                    return BoolValue;

                case "TimeOfDay":
                    if (!TimeSpan.TryParse(TextValue, CultureInfo.InvariantCulture, out _))
                    {
                        throw new FormatException("expected HH:MM:SS, got '" + TextValue + "'");
                    }
                    return TextValue;

                default:
                    return NotSettable;
            }
        }
    }
}
