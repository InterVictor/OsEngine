using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using OsEngine.Candles;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote-data adaptation of BotTabScreenerUi: server, portfolio, commission, candles, time frame and the list of
    /// securities of a Screener tab on the VPS, read and written through bot_get/set_config_tab_screener.
    /// </summary>
    public partial class RobotsVpsScreenerSettingsUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private JsonElement _config;
        private readonly List<RemoteServer> _servers = new List<RemoteServer>();
        private readonly List<RemoteSecurity> _securities = new List<RemoteSecurity>();

        // selected securities: "name|class" -> (name, class); kept across classes and searches
        private readonly Dictionary<string, (string Name, string Class)> _selected = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

        private DataGridView _securityGrid;
        private bool _loading;

        public RobotsVpsScreenerSettingsUi(RemoteMcpClient client, string botId, string tabName)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _tabName = tabName;
            OsEngine.Layout.StickyBorders.Listen(this);
            OsEngine.Layout.StartupLocation.Start_MouseInCentre(this);
            LocalizeControls();

            CreateSecurityGrid();
            ComboBoxTypeServer.SelectionChanged += (s, e) => { if (!_loading) _ = LoadSelectedServerDataAsync(); };
            ComboBoxClass.SelectionChanged += (s, e) => { if (!_loading) RenderSecurities(); };
            TextBoxSearchSecurity.TextChanged += (s, e) => { if (!_loading) RenderSecurities(); };
            TextBoxSearchSecurity.GotKeyboardFocus += (s, e) => { if (TextBoxSearchSecurity.Text == OsLocalization.Market.Label64) TextBoxSearchSecurity.Text = string.Empty; };
            TextBoxSearchSecurity.LostKeyboardFocus += (s, e) => { if (TextBoxSearchSecurity.Text == string.Empty) TextBoxSearchSecurity.Text = OsLocalization.Market.Label64; };
            CheckBoxSelectAllCheckBox.Click += CheckBoxSelectAll_Click;
            ComboBoxCandleMarketDataType.SelectionChanged += (s, e) =>
                CheckBoxSaveTradeArrayInCandle.IsEnabled = ComboBoxCandleMarketDataType.SelectedItem?.ToString() != CandleMarketDataType.MarketDepth.ToString();

            Loaded += async (s, e) => await LoadRemoteConfigurationAsync();
            Closed += (s, e) =>
            {
                _securityGrid?.Dispose();
                _securityGrid = null;
                SecuritiesHost.Child = null;
            };
        }

        private void LocalizeControls()
        {
            Title = OsLocalization.Market.TitleConnectorCandle + " — " + _tabName;
            Label1.Content = OsLocalization.Market.Label1;
            Label2.Content = OsLocalization.Market.Label2;
            Label3.Content = OsLocalization.Market.Label3;
            CheckBoxIsEmulator.Content = OsLocalization.Market.Label4;
            Label5.Content = OsLocalization.Market.Label5;
            Label6.Content = OsLocalization.Market.Label6;
            Label8.Content = OsLocalization.Market.Label8;
            Label9.Content = OsLocalization.Market.Label9;
            ButtonAccept.Content = OsLocalization.Market.ButtonAccept;
            LabelCommissionType.Content = OsLocalization.Market.LabelCommissionType;
            LabelCommissionValue.Content = OsLocalization.Market.LabelCommissionValue;
            CheckBoxSaveTradeArrayInCandle.Content = OsLocalization.Market.Label59;
            CheckBoxSelectAllCheckBox.Content = OsLocalization.Trader.Label173;
            TextBoxSearchSecurity.Text = OsLocalization.Market.Label64;
            LabelSecurities.Content = OsLocalization.Market.Label66;
        }

        #region Load

        private async System.Threading.Tasks.Task LoadRemoteConfigurationAsync()
        {
            _loading = true;
            TextBlockStatus.Text = "Reading the screener settings from the VPS...";

            try
            {
                _config = await _client.CallToolAsync("bot_get_config_tab_screener", new { bot_id = _botId, tab_name = _tabName });
                LoadConfigIntoControls();
                await LoadRemoteServersAsync();
                await LoadSelectedServerDataAsync();
                TextBlockStatus.Text = "";
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = "";
                ShowError("Could not load the screener settings from the VPS: " + ex.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadConfigIntoControls()
        {
            ComboBoxCommissionType.Items.Clear();
            foreach (string name in Enum.GetNames(typeof(CommissionType))) ComboBoxCommissionType.Items.Add(name);
            ComboBoxCommissionType.SelectedItem = GetString(_config, "commission_type");
            TextBoxCommissionValue.Text = GetString(_config, "commission_value");
            CheckBoxIsEmulator.IsChecked = GetBool(_config, "emulator_is_on");
            CheckBoxSaveTradeArrayInCandle.IsChecked = GetBool(_config, "save_trades_in_candles");

            ComboBoxCandleMarketDataType.Items.Clear();
            foreach (string name in Enum.GetNames(typeof(CandleMarketDataType))) ComboBoxCandleMarketDataType.Items.Add(name);
            ComboBoxCandleMarketDataType.SelectedItem = GetString(_config, "candle_market_data_type");

            ComboBoxCandleCreateMethodType.Items.Clear();
            foreach (string name in CandleFactory.GetCandlesNames()) ComboBoxCandleCreateMethodType.Items.Add(name);
            string method = GetString(_config, "candle_create_method_type");
            if (!string.IsNullOrWhiteSpace(method) && !ComboBoxCandleCreateMethodType.Items.Contains(method)) ComboBoxCandleCreateMethodType.Items.Add(method);
            ComboBoxCandleCreateMethodType.SelectedItem = method;

            ComboBoxTimeFrame.Items.Clear();
            foreach (string name in Enum.GetNames(typeof(TimeFrame))) ComboBoxTimeFrame.Items.Add(name);
            ComboBoxTimeFrame.SelectedItem = GetString(_config, "time_frame");

            _selected.Clear();
            if (_config.TryGetProperty("securities", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string name = GetString(item, "name");
                    string className = GetString(item, "class_name");
                    if (string.IsNullOrWhiteSpace(name) || !GetBool(item, "is_on")) continue;
                    _selected[Key(name, className)] = (name, className);
                }
            }
        }

        private async System.Threading.Tasks.Task LoadRemoteServersAsync()
        {
            JsonElement list = await _client.CallToolAsync("server_management_get_list", new { });
            _servers.Clear();
            if (list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    RemoteServer server = RemoteServer.FromJson(item);
                    if (server != null) _servers.Add(server);
                }
            }

            ComboBoxTypeServer.Items.Clear();
            foreach (RemoteServer server in _servers) ComboBoxTypeServer.Items.Add(server.Name);
            string configured = GetString(_config, "server_name");
            if (!string.IsNullOrWhiteSpace(configured) && !ComboBoxTypeServer.Items.Contains(configured)) ComboBoxTypeServer.Items.Add(configured);
            ComboBoxTypeServer.SelectedItem = configured;
            if (ComboBoxTypeServer.SelectedItem == null && ComboBoxTypeServer.Items.Count > 0) ComboBoxTypeServer.SelectedIndex = 0;
        }

        private async System.Threading.Tasks.Task LoadSelectedServerDataAsync()
        {
            RemoteServer server = FindSelectedServer();
            ComboBoxPortfolio.Items.Clear();
            ComboBoxClass.Items.Clear();
            _securities.Clear();

            string configuredPortfolio = GetString(_config, "portfolio_name");
            string configuredClass = GetString(_config, "securities_class");

            if (server != null)
            {
                try
                {
                    TextBlockStatus.Text = "Reading the securities of " + server.Name + "...";

                    JsonElement portfolios = await _client.CallToolAsync("server_instance_get_portfolios", new { type = server.Type, number = server.Number });
                    if (portfolios.TryGetProperty("portfolios", out JsonElement portfolioList) && portfolioList.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in portfolioList.EnumerateArray())
                        {
                            string number = GetString(item, "number");
                            if (!string.IsNullOrWhiteSpace(number) && !ComboBoxPortfolio.Items.Contains(number)) ComboBoxPortfolio.Items.Add(number);
                        }
                    }

                    JsonElement securitiesResult = await _client.CallToolAsync("server_instance_get_securities", new { type = server.Type, number = server.Number });
                    if (securitiesResult.TryGetProperty("securities", out JsonElement securities) && securities.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in securities.EnumerateArray()) _securities.Add(RemoteSecurity.FromJson(item));
                    }

                    TextBlockStatus.Text = _securities.Count == 0
                        ? "The server has no securities yet — is the connector connected on the VPS?"
                        : "";
                }
                catch (Exception ex)
                {
                    TextBlockStatus.Text = "";
                    ShowError("Could not load the server portfolios and securities: " + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(configuredPortfolio) && !ComboBoxPortfolio.Items.Contains(configuredPortfolio)) ComboBoxPortfolio.Items.Add(configuredPortfolio);
            ComboBoxPortfolio.SelectedItem = configuredPortfolio;
            if (ComboBoxPortfolio.SelectedItem == null && ComboBoxPortfolio.Items.Count > 0) ComboBoxPortfolio.SelectedIndex = 0;

            bool wasLoading = _loading;
            _loading = true;
            foreach (string className in _securities.Select(s => s.ClassName).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().OrderBy(c => c))
                ComboBoxClass.Items.Add(className);
            if (!string.IsNullOrWhiteSpace(configuredClass) && !ComboBoxClass.Items.Contains(configuredClass)) ComboBoxClass.Items.Add(configuredClass);
            ComboBoxClass.SelectedItem = configuredClass;
            if (ComboBoxClass.SelectedItem == null && ComboBoxClass.Items.Count > 0) ComboBoxClass.SelectedIndex = 0;
            _loading = wasLoading;

            RenderSecurities();
        }

        #endregion

        #region Securities grid

        private void CreateSecurityGrid()
        {
            _securityGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _securityGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _securityGrid.ScrollBars = ScrollBars.Vertical;
            AddTextColumn(OsLocalization.Trader.Label165, 50);
            AddTextColumn(OsLocalization.Trader.Label167, 100);
            AddTextColumn(OsLocalization.Trader.Label169, 130);
            AddTextColumn(OsLocalization.Trader.Label168, 130);
            _securityGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = OsLocalization.Trader.Label171, Width = 50 });
            _securityGrid.CellClick += SecurityGrid_CellClick;
            _securityGrid.DataError += (s, e) => { e.ThrowException = false; };
            SecuritiesHost.Child = _securityGrid;
        }

        private void AddTextColumn(string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = true, Width = width };
            if (width >= 100) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            _securityGrid.Columns.Add(column);
        }

        private IEnumerable<RemoteSecurity> VisibleSecurities()
        {
            string className = ComboBoxClass.SelectedItem?.ToString();
            string search = TextBoxSearchSecurity.Text;
            if (search == OsLocalization.Market.Label64) search = string.Empty;

            return _securities.Where(s =>
                (string.IsNullOrWhiteSpace(className) || s.ClassName == className)
                && (string.IsNullOrWhiteSpace(search)
                    || s.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || s.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        private void RenderSecurities()
        {
            if (_securityGrid == null) return;
            _securityGrid.Rows.Clear();

            foreach (RemoteSecurity security in VisibleSecurities())
            {
                int row = _securityGrid.Rows.Add(_securityGrid.Rows.Count + 1, security.Type, security.Name, security.FullName,
                    _selected.ContainsKey(Key(security.Name, security.ClassName)));
                _securityGrid.Rows[row].Tag = security;
            }

            UpdateSelectedCount();
        }

        private void SecurityGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 4) return;
            if (!(_securityGrid.Rows[e.RowIndex].Tag is RemoteSecurity security)) return;

            string key = Key(security.Name, security.ClassName);
            bool on = !_selected.ContainsKey(key);
            if (on) _selected[key] = (security.Name, security.ClassName);
            else _selected.Remove(key);

            _securityGrid.Rows[e.RowIndex].Cells[4].Value = on;
            UpdateSelectedCount();
        }

        private void CheckBoxSelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool on = CheckBoxSelectAllCheckBox.IsChecked == true;

            foreach (RemoteSecurity security in VisibleSecurities())
            {
                string key = Key(security.Name, security.ClassName);
                if (on) _selected[key] = (security.Name, security.ClassName);
                else _selected.Remove(key);
            }

            RenderSecurities();
        }

        private void UpdateSelectedCount()
        {
            LabelSelectedCount.Content = "selected: " + _selected.Count;
        }

        #endregion

        private async void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            RemoteServer server = FindSelectedServer();

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                ["bot_id"] = _botId,
                ["tab_name"] = _tabName,
                ["server_type"] = server?.Type ?? GetString(_config, "server_type"),
                ["server_name"] = ComboBoxTypeServer.Text,
                ["portfolio_name"] = ComboBoxPortfolio.Text,
                ["emulator_is_on"] = CheckBoxIsEmulator.IsChecked == true,
                ["commission_type"] = ComboBoxCommissionType.Text,
                ["commission_value"] = ParseDecimal(TextBoxCommissionValue.Text),
                ["candle_market_data_type"] = ComboBoxCandleMarketDataType.Text,
                ["candle_create_method_type"] = ComboBoxCandleCreateMethodType.Text,
                ["save_trades_in_candles"] = CheckBoxSaveTradeArrayInCandle.IsChecked == true,
                ["time_frame"] = ComboBoxTimeFrame.Text,
                ["securities_class"] = ComboBoxClass.Text,
                ["securities"] = _selected.Values.Select(s => new { name = s.Name, class_name = s.Class, is_on = true }).ToList()
            };

            try
            {
                ButtonAccept.IsEnabled = false;
                TextBlockStatus.Text = "Saving on the VPS...";
                await _client.CallToolAsync("bot_set_config_tab_screener", arguments);
                Close();
            }
            catch (Exception ex)
            {
                TextBlockStatus.Text = "";
                ButtonAccept.IsEnabled = true;
                ShowError("Could not save the screener settings on the VPS: " + ex.Message);
            }
        }

        private RemoteServer FindSelectedServer() => _servers.FirstOrDefault(s => s.Name == ComboBoxTypeServer.SelectedItem?.ToString());

        private void ShowError(string message) => System.Windows.MessageBox.Show(message, Title, MessageBoxButton.OK, MessageBoxImage.Information);

        private static string Key(string name, string className) => name + "|" + className;

        private static decimal ParseDecimal(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result)
            ? result : decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result) ? result : 0;

        private static string GetString(JsonElement element, string name)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private static bool GetBool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private sealed class RemoteServer
        {
            public string Name;
            public string Type;
            public int Number;

            public static RemoteServer FromJson(JsonElement value)
            {
                if (!value.TryGetProperty("name", out JsonElement name)) return null;
                int.TryParse(GetString(value, "number"), out int number);
                return new RemoteServer { Name = name.GetString(), Type = GetString(value, "type"), Number = number };
            }
        }

        private sealed class RemoteSecurity
        {
            public string Name;
            public string FullName;
            public string ClassName;
            public string Type;

            public static RemoteSecurity FromJson(JsonElement value) => new RemoteSecurity
            {
                Name = GetString(value, "name"),
                FullName = GetString(value, "nameFull"),
                ClassName = GetString(value, "nameClass"),
                Type = GetString(value, "securityType")
            };
        }
    }
}
