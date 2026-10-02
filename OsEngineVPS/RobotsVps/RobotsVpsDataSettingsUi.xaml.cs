using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using OsEngine.Candles;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;
using OsEngine.Market;
using OsEngine.Market.SupportTable;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote-data adaptation of ConnectorCandlesUi. Layout and controls come from its original XAML;
    /// all server, portfolio, security and connector values are read/written through the VPS MCP API.
    /// </summary>
    public partial class RobotsVpsDataSettingsUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private JsonElement _config;
        private readonly List<RemoteServer> _servers = new List<RemoteServer>();
        private readonly List<RemoteSecurity> _securities = new List<RemoteSecurity>();
        private readonly List<RemoteSeriesParameter> _seriesParameters = new List<RemoteSeriesParameter>();
        private DataGridView _securityGrid;
        private DataGridView _seriesGrid;
        private bool _loading;
        private string _selectedSecurity;
        private string _selectedClass;

        public RobotsVpsDataSettingsUi(RemoteMcpClient client, string botId, string tabName)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _tabName = tabName;
            OsEngine.Layout.StickyBorders.Listen(this);
            OsEngine.Layout.StartupLocation.Start_MouseInCentre(this);
            OsEngine.Layout.StartupLocation.Start_FitHeightToWorkArea(this);
            LocalizeControls();

            ButtonRightInSearchResults.Visibility = Visibility.Hidden;
            ButtonLeftInSearchResults.Visibility = Visibility.Hidden;
            LabelCurrentResultShow.Visibility = Visibility.Hidden;
            LabelCommasResultShow.Visibility = Visibility.Hidden;
            LabelCountResultsShow.Visibility = Visibility.Hidden;
            TextBoxSearchSecurity.MouseEnter += SearchSecurity_MouseEnter;
            TextBoxSearchSecurity.MouseLeave += SearchSecurity_MouseLeave;
            TextBoxSearchSecurity.LostKeyboardFocus += SearchSecurity_LostKeyboardFocus;
            ButtonMarketDepthBuildMaxSpread.Visibility = Visibility.Collapsed;
            CreateSecurityGrid();
            CreateSeriesGrid();

            ComboBoxTypeServer.SelectionChanged += ServerSelectionChanged;
            ComboBoxClass.SelectionChanged += ClassSelectionChanged;
            TextBoxSearchSecurity.TextChanged += SearchSecurityChanged;
            ComboBoxCandleMarketDataType.SelectionChanged += CandleMarketDataChanged;
            ComboBoxCandleCreateMethodType.SelectionChanged += CandleSeriesTypeChanged;
            CheckBoxSaveTradeArrayInCandle.Click += (s, e) => { };
            Loaded += async (s, e) => await LoadRemoteConfigurationAsync();
            Closed += (s, e) => ClearGrids();
        }

        private void LocalizeControls()
        {
            Title = OsLocalization.Market.TitleConnectorCandle;
            Label1.Content = OsLocalization.Market.Label1;
            Label2.Content = OsLocalization.Market.Label2;
            Label3.Content = OsLocalization.Market.Label3;
            CheckBoxIsEmulator.Content = OsLocalization.Market.Label4;
            Label5.Content = OsLocalization.Market.Label7;
            Label6.Content = OsLocalization.Market.Label6;
            Label8.Content = OsLocalization.Market.Label8;
            Label9.Content = OsLocalization.Market.Label9;
            ButtonAccept.Content = OsLocalization.Market.ButtonAccept;
            LabelCommissionType.Content = OsLocalization.Market.LabelCommissionType;
            LabelCommissionValue.Content = OsLocalization.Market.LabelCommissionValue;
            CheckBoxSaveTradeArrayInCandle.Content = OsLocalization.Market.Label59;
            TextBoxSearchSecurity.Text = OsLocalization.Market.Label64;
            LabelCandleType.Content = OsLocalization.Market.Label65;
            Label18.Content = OsLocalization.Market.Label316;
            Label19.Content = OsLocalization.Market.Label317;
        }

        private async System.Threading.Tasks.Task LoadRemoteConfigurationAsync()
        {
            _loading = true;
            try
            {
                _config = await _client.CallToolAsync("bot_get_config_tab_simple", new { bot_id = _botId, tab_name = _tabName });
                LoadConfigIntoControls();
                _selectedSecurity = GetString(_config, "security_name");
                _selectedClass = GetString(_config, "security_class");
                await LoadRemoteServersAsync();
                await LoadSelectedServerDataAsync();
                LoadSeriesParameters();
            }
            catch (Exception ex)
            {
                ShowError("Could not load remote data settings: " + ex.Message);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadConfigIntoControls()
        {
            ComboBoxCommissionType.Items.Clear();
            ComboBoxCommissionType.Items.Add(CommissionType.None.ToString());
            ComboBoxCommissionType.Items.Add(CommissionType.OneLotFix.ToString());
            ComboBoxCommissionType.Items.Add(CommissionType.Percent.ToString());
            ComboBoxCommissionType.SelectedItem = GetString(_config, "commission_type");
            TextBoxCommissionValue.Text = GetString(_config, "commission_value");
            CheckBoxIsEmulator.IsChecked = GetBool(_config, "emulator_is_on");
            CheckBoxSaveTradeArrayInCandle.IsChecked = GetBool(_config, "save_trades_in_candles");

            ComboBoxCandleMarketDataType.Items.Clear();
            foreach (string name in Enum.GetNames(typeof(CandleMarketDataType))) ComboBoxCandleMarketDataType.Items.Add(name);
            ComboBoxCandleMarketDataType.SelectedItem = GetString(_config, "candle_market_data_type");

            ComboBoxCandleCreateMethodType.Items.Clear();
            JsonElement types;
            if (_config.TryGetProperty("candle_create_method_types", out types) && types.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement type in types.EnumerateArray())
                    if (type.ValueKind == JsonValueKind.String) ComboBoxCandleCreateMethodType.Items.Add(type.GetString());
            }
            if (!ComboBoxCandleCreateMethodType.Items.Contains(GetString(_config, "candle_create_method_type")))
                ComboBoxCandleCreateMethodType.Items.Add(GetString(_config, "candle_create_method_type"));
            ComboBoxCandleCreateMethodType.SelectedItem = GetString(_config, "candle_create_method_type");
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
            string configuredServer = GetString(_config, "server_full_name");
            if (!string.IsNullOrWhiteSpace(configuredServer) && !ComboBoxTypeServer.Items.Contains(configuredServer))
                ComboBoxTypeServer.Items.Add(configuredServer);
            ComboBoxTypeServer.SelectedItem = configuredServer;
            if (ComboBoxTypeServer.SelectedItem == null && ComboBoxTypeServer.Items.Count > 0)
                ComboBoxTypeServer.SelectedIndex = 0;
        }

        private async System.Threading.Tasks.Task LoadSelectedServerDataAsync()
        {
            if (_loading && _servers.Count == 0) return;
            string selectedName = ComboBoxTypeServer.SelectedItem?.ToString();
            RemoteServer server = _servers.FirstOrDefault(s => s.Name == selectedName);
            ComboBoxPortfolio.Items.Clear();
            _securities.Clear();
            ComboBoxClass.Items.Clear();
            if (server == null)
            {
                string configuredClass = GetString(_config, "security_class");
                if (!string.IsNullOrWhiteSpace(configuredClass)) ComboBoxClass.Items.Add(configuredClass);
                ComboBoxClass.SelectedItem = configuredClass;
                string configuredPortfolio = GetString(_config, "portfolio_name");
                if (!string.IsNullOrWhiteSpace(configuredPortfolio)) ComboBoxPortfolio.Items.Add(configuredPortfolio);
                ComboBoxPortfolio.SelectedItem = configuredPortfolio;
                RenderSecurities();
                return;
            }

            try
            {
                JsonElement portfolios = await _client.CallToolAsync("server_instance_get_portfolios", new { type = server.Type, number = server.Number });
                if (portfolios.TryGetProperty("portfolios", out JsonElement portfolioList) && portfolioList.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in portfolioList.EnumerateArray())
                    {
                        string number = GetString(item, "number");
                        if (!string.IsNullOrWhiteSpace(number) && !ComboBoxPortfolio.Items.Contains(number)) ComboBoxPortfolio.Items.Add(number);
                    }
                }
                string configuredPortfolio = GetString(_config, "portfolio_name");
                if (!string.IsNullOrWhiteSpace(configuredPortfolio) && !ComboBoxPortfolio.Items.Contains(configuredPortfolio)) ComboBoxPortfolio.Items.Add(configuredPortfolio);
                ComboBoxPortfolio.SelectedItem = configuredPortfolio;
                if (ComboBoxPortfolio.SelectedItem == null && ComboBoxPortfolio.Items.Count > 0) ComboBoxPortfolio.SelectedIndex = 0;

                JsonElement securitiesResult = await _client.CallToolAsync("server_instance_get_securities", new { type = server.Type, number = server.Number });
                if (securitiesResult.TryGetProperty("securities", out JsonElement securities) && securities.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in securities.EnumerateArray())
                    {
                        RemoteSecurity security = RemoteSecurity.FromJson(item);
                        if (security != null) _securities.Add(security);
                    }
                }

                foreach (string className in _securities.Select(s => s.ClassName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().OrderBy(s => s))
                    ComboBoxClass.Items.Add(className);
                _selectedSecurity = GetString(_config, "security_name");
                _selectedClass = GetString(_config, "security_class");
                ComboBoxClass.SelectedItem = _selectedClass;
                if (ComboBoxClass.SelectedItem == null && ComboBoxClass.Items.Count > 0) ComboBoxClass.SelectedIndex = 0;
                RenderSecurities();
            }
            catch (Exception ex)
            {
                ShowError("Could not load server instruments/portfolios: " + ex.Message);
            }
        }

        private void CreateSecurityGrid()
        {
            _securityGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _securityGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _securityGrid.ScrollBars = ScrollBars.Vertical;
            AddTextColumn(_securityGrid, OsLocalization.Trader.Label165, 50);
            AddTextColumn(_securityGrid, OsLocalization.Trader.Label167, 100);
            AddTextColumn(_securityGrid, OsLocalization.Trader.Label169, 130);
            AddTextColumn(_securityGrid, OsLocalization.Trader.Label168, 130);
            _securityGrid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = OsLocalization.Trader.Label171, Width = 50 });
            _securityGrid.CellClick += SecurityGrid_CellClick;
            _securityGrid.DataError += (s, e) => { e.ThrowException = false; };
            SecurityTable.Child = _securityGrid;
        }

        private static void AddTextColumn(DataGridView grid, string header, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn { HeaderText = header, ReadOnly = true, Width = width };
            if (width >= 100) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Columns.Add(column);
        }

        private void RenderSecurities()
        {
            if (_securityGrid == null) return;
            _securityGrid.Rows.Clear();
            string className = ComboBoxClass.SelectedItem?.ToString();
            string search = TextBoxSearchSecurity.Text;
            if (search == OsLocalization.Market.Label64) search = string.Empty;
            foreach (RemoteSecurity security in _securities)
            {
                if (!string.IsNullOrWhiteSpace(className) && security.ClassName != className) continue;
                if (!string.IsNullOrWhiteSpace(search) && !security.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !security.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                int row = _securityGrid.Rows.Add(_securityGrid.Rows.Count + 1, security.Type, security.Name, security.FullName,
                    security.Name == _selectedSecurity && security.ClassName == _selectedClass);
                _securityGrid.Rows[row].Tag = security;
            }
        }

        private void SecurityGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 4) return;
            _securityGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            foreach (DataGridViewRow row in _securityGrid.Rows) row.Cells[4].Value = false;
            _securityGrid.Rows[e.RowIndex].Cells[4].Value = true;
            RemoteSecurity selected = _securityGrid.Rows[e.RowIndex].Tag as RemoteSecurity;
            if (selected == null) return;
            _selectedSecurity = selected.Name;
            _selectedClass = selected.ClassName;
            _loading = true;
            ComboBoxClass.SelectedItem = selected.ClassName;
            _loading = false;
        }

        private void CreateSeriesGrid()
        {
            _seriesGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.CellSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _seriesGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _seriesGrid.ScrollBars = ScrollBars.Vertical;
            AddTextColumn(_seriesGrid, "Parameter name", 160);
            _seriesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            _seriesGrid.CellEndEdit += SeriesGrid_CellEndEdit;
            _seriesGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_seriesGrid.IsCurrentCellDirty) _seriesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            HostCandleSeriesParameters.Child = _seriesGrid;
        }

        private void LoadSeriesParameters()
        {
            _seriesParameters.Clear();
            _seriesGrid.Rows.Clear();
            if (!_config.TryGetProperty("candle_series_parameters", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                SetSeriesPlaceholder("VPS did not return candle builder parameter values.");
                return;
            }
            foreach (JsonElement item in list.EnumerateArray())
            {
                RemoteSeriesParameter parameter = RemoteSeriesParameter.FromJson(item);
                if (parameter != null) _seriesParameters.Add(parameter);
            }
            if (_seriesParameters.Count == 0)
            {
                SetSeriesPlaceholder("This candle type has no additional parameters.");
                return;
            }
            HostCandleSeriesParameters.Child = _seriesGrid;
            foreach (RemoteSeriesParameter parameter in _seriesParameters)
            {
                int rowIndex = _seriesGrid.Rows.Add(parameter.Label, parameter.Value is bool ? (object)parameter.Value : parameter.Value?.ToString());
                DataGridViewRow row = _seriesGrid.Rows[rowIndex];
                row.Tag = parameter;
                if (parameter.Type == "Bool") row.Cells[1] = new DataGridViewCheckBoxCell { Value = parameter.Value };
                else if (parameter.Type == "StringCollection")
                {
                    DataGridViewComboBoxCell cell = new DataGridViewComboBoxCell();
                    foreach (string value in parameter.Values) cell.Items.Add(value);
                    if (parameter.Value != null && !cell.Items.Contains(parameter.Value.ToString())) cell.Items.Add(parameter.Value.ToString());
                    cell.Value = parameter.Value?.ToString();
                    row.Cells[1] = cell;
                }
            }
        }

        private void SetSeriesPlaceholder(string message)
        {
            HostCandleSeriesParameters.Child = new System.Windows.Forms.Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                ForeColor = System.Drawing.Color.DimGray
            };
        }

        private void SeriesGrid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _seriesGrid.Rows.Count) return;
            RemoteSeriesParameter parameter = _seriesGrid.Rows[e.RowIndex].Tag as RemoteSeriesParameter;
            object value = _seriesGrid.Rows[e.RowIndex].Cells[1].Value;
            if (parameter != null && value != null) parameter.Value = value;
        }

        private void ServerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) _ = LoadSelectedServerDataAsync();
        }
        private void ClassSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string selectedClass = ComboBoxClass.SelectedItem?.ToString();
            if (selectedClass != _selectedClass)
            {
                _selectedClass = selectedClass;
                if (!_securities.Any(s => s.Name == _selectedSecurity && s.ClassName == selectedClass)) _selectedSecurity = null;
            }
            RenderSecurities();
        }
        private void SearchSecurityChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loading) RenderSecurities();
        }
        private void SearchSecurity_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (TextBoxSearchSecurity.Text == OsLocalization.Market.Label64) TextBoxSearchSecurity.Text = string.Empty;
        }
        private void SearchSecurity_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (TextBoxSearchSecurity.Text == string.Empty && !TextBoxSearchSecurity.IsKeyboardFocused)
                TextBoxSearchSecurity.Text = OsLocalization.Market.Label64;
        }
        private void SearchSecurity_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (TextBoxSearchSecurity.Text == string.Empty) TextBoxSearchSecurity.Text = OsLocalization.Market.Label64;
        }
        private void CandleMarketDataChanged(object sender, SelectionChangedEventArgs e)
        {
            bool marketDepth = ComboBoxCandleMarketDataType.SelectedItem?.ToString() == CandleMarketDataType.MarketDepth.ToString();
            CheckBoxSaveTradeArrayInCandle.IsEnabled = !marketDepth;
            ButtonMarketDepthBuildMaxSpread.Visibility = marketDepth ? Visibility.Visible : Visibility.Collapsed;
        }
        private void CandleSeriesTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            string selectedType = ComboBoxCandleCreateMethodType.SelectedItem?.ToString();
            bool parametersBelongToSelection = selectedType == GetString(_config, "candle_create_method_type");
            if (!parametersBelongToSelection) SetSeriesPlaceholder("Parameters for a different candle type will be loaded after saving and reopening this window.");
            else LoadSeriesParameters();
        }

        private async void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _seriesGrid.EndEdit();
                Dictionary<string, object> arguments = new Dictionary<string, object>
                {
                    ["bot_id"] = _botId,
                    ["tab_name"] = _tabName,
                    ["server_type"] = FindSelectedServer()?.Type ?? GetString(_config, "server_type"),
                    ["server_full_name"] = ComboBoxTypeServer.Text,
                    ["portfolio_name"] = ComboBoxPortfolio.Text,
                    ["emulator_is_on"] = CheckBoxIsEmulator.IsChecked == true,
                    ["commission_type"] = ComboBoxCommissionType.Text,
                    ["commission_value"] = ParseDecimal(TextBoxCommissionValue.Text),
                    ["security_class"] = ComboBoxClass.Text,
                    ["security_name"] = _selectedSecurity ?? GetString(_config, "security_name"),
                    ["candle_market_data_type"] = ComboBoxCandleMarketDataType.Text,
                    ["candle_create_method_type"] = ComboBoxCandleCreateMethodType.Text,
                    ["save_trades_in_candles"] = CheckBoxSaveTradeArrayInCandle.IsChecked == true,
                    ["build_non_trading_candles"] = GetBool(_config, "build_non_trading_candles")
                };
                string selectedType = ComboBoxCandleCreateMethodType.Text;
                if (selectedType == GetString(_config, "candle_create_method_type"))
                {
                    List<object> parameterValues = new List<object>();
                    foreach (DataGridViewRow row in _seriesGrid.Rows)
                    {
                        RemoteSeriesParameter parameter = row.Tag as RemoteSeriesParameter;
                        if (parameter == null) continue;
                        object raw = row.Cells[1].Value;
                        if (raw == null) continue;
                        parameterValues.Add(new { sys_name = parameter.SysName, value = parameter.ConvertValue(raw) });
                    }
                    arguments["candle_series_parameters"] = parameterValues;
                    RemoteSeriesParameter timeFrame = _seriesParameters.FirstOrDefault(p => p.SysName == "TimeFrame");
                    string frameValue = timeFrame?.Value?.ToString() ?? GetString(_config, "time_frame");
                    foreach (DataGridViewRow row in _seriesGrid.Rows)
                    {
                        if ((row.Tag as RemoteSeriesParameter)?.SysName == "TimeFrame" && row.Cells[1].Value != null)
                        {
                            frameValue = row.Cells[1].Value.ToString();
                            break;
                        }
                    }
                    arguments["time_frame"] = frameValue;
                }
                else
                {
                    arguments["time_frame"] = GetString(_config, "time_frame");
                }

                await _client.CallToolAsync("bot_set_config_tab_simple", arguments);
                Close();
            }
            catch (Exception ex)
            {
                ShowError("Could not save remote data settings: " + ex.Message);
            }
        }

        private void ButtonMarketDepthBuildMaxSpread_Click(object sender, RoutedEventArgs e)
        {
            ShowError("Market-depth candle spread settings are not exposed by the VPS API yet.");
        }
        private void ButtonPostConnectorCandles_Click(object sender, RoutedEventArgs e)
        {
            ShowError("Help posts are not available in the remote settings window.");
        }

        private RemoteServer FindSelectedServer() => _servers.FirstOrDefault(s => s.Name == ComboBoxTypeServer.SelectedItem?.ToString());
        private void ShowError(string message) => System.Windows.MessageBox.Show(message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        private void ClearGrids()
        {
            if (_securityGrid != null) { _securityGrid.Dispose(); _securityGrid = null; }
            if (_seriesGrid != null) { _seriesGrid.Dispose(); _seriesGrid = null; }
            SecurityTable.Child = null;
            HostCandleSeriesParameters.Child = null;
        }
        private static decimal ParseDecimal(string value) => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result)
            ? result : decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out result) ? result : 0;
        private static string GetString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        private static bool GetBool(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

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
                Name = GetString(value, "name"), FullName = GetString(value, "nameFull"),
                ClassName = GetString(value, "nameClass"), Type = GetString(value, "securityType")
            };
        }
        private sealed class RemoteSeriesParameter
        {
            public string SysName;
            public string Label;
            public string Type;
            public object Value;
            public List<string> Values = new List<string>();
            public static RemoteSeriesParameter FromJson(JsonElement item)
            {
                if (!item.TryGetProperty("sys_name", out JsonElement name)) return null;
                JsonElement value = item.TryGetProperty("value", out JsonElement raw) ? raw : default;
                List<string> choices = new List<string>();
                if (item.TryGetProperty("values", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement option in options.EnumerateArray()) if (option.ValueKind == JsonValueKind.String) choices.Add(option.GetString());
                return new RemoteSeriesParameter
                {
                    SysName = name.GetString(), Label = GetString(item, "label"), Type = GetString(item, "type"),
                    Value = value.ValueKind == JsonValueKind.True ? (object)true : value.ValueKind == JsonValueKind.False ? false
                        : value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                    Values = choices
                };
            }
            public object ConvertValue(object value)
            {
                if (Type == "Int") return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                if (Type == "Decimal")
                {
                    string text = value.ToString();
                    if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal invariantValue)) return invariantValue;
                    if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out decimal localValue)) return localValue;
                    throw new FormatException("Invalid decimal value: " + text);
                }
                if (Type == "Bool") return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return value.ToString();
            }
        }
    }
}
