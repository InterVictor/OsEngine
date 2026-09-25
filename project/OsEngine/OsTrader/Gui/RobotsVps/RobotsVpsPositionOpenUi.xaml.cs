using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Windows.Forms.Integration;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Layout;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote copy of PositionOpenUi2. It keeps the parent's order-entry tabs and
    /// sends the selected native BotTabSimple action through the VPS MCP session.
    /// </summary>
    public partial class RobotsVpsPositionOpenUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private readonly string _securityName;
        private DataGridView _depthGrid;
        private DispatcherTimer _refreshTimer;
        private bool _stopOrdersSupported;
        private bool _serverStopOrdersOn;
        private bool _loadingState;

        public RobotsVpsPositionOpenUi(RemoteMcpClient client, string botId, string botName, string tabName, string securityName)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _tabName = tabName;
            _securityName = securityName;

            StickyBorders.Listen(this);
            Title = OsLocalization.Trader.Label196;
            LabelServerType.Content = OsLocalization.Trader.Label178 + ":";
            LabelSecurity.Content = OsLocalization.Trader.Label102 + ":";
            LabelTabName.Content = OsLocalization.Trader.Label194 + ":";
            LabelLimitPrice.Content = OsLocalization.Trader.Label205;
            LabelStopPrice.Content = OsLocalization.Trader.Label205;
            LabelFakePrice.Content = OsLocalization.Trader.Label205;
            LabelStopActivationPrice.Content = OsLocalization.Trader.Label206;
            LabelStopActivationType.Content = OsLocalization.Trader.Label207;
            LabelStopLifeTime.Content = OsLocalization.Trader.Label208;
            LabelStopLifeTimeType.Content = OsLocalization.Trader.Label212;
            LabelLimitVolume.Content = OsLocalization.Trader.Label30;
            LabelMarketVolume.Content = OsLocalization.Trader.Label30;
            LabelStopVolume.Content = OsLocalization.Trader.Label30;
            LabelFakeVolume.Content = OsLocalization.Trader.Label30;
            CheckBoxIsEmulator.Content = OsLocalization.Trader.Label204;
            ButtonBuy.Content = OsLocalization.Trader.Label198;
            ButtonSell.Content = OsLocalization.Trader.Label199;
            TabItemLimit.Header = OsLocalization.Trader.Label200;
            TabItemMarket.Header = OsLocalization.Trader.Label201;
            TabItemStopLimit.Header = OsLocalization.Trader.Label202;
            TabItemStopMarket.Header = OsLocalization.Trader.Label772;
            TabItemFake.Header = OsLocalization.Trader.Label203;
            LabelStopMarketActivationPrice.Content = OsLocalization.Trader.Label206;
            LabelStopMarketActivationType.Content = OsLocalization.Trader.Label207;
            LabelStopMarketLifeTime.Content = OsLocalization.Trader.Label208;
            LabelStopMarketLifeTimeType.Content = OsLocalization.Trader.Label212;
            LabelStopMarketVolume.Content = OsLocalization.Trader.Label30;
            CheckBoxServerStopOrder.Content = OsLocalization.Trader.Label771;
            CheckBoxServerStopMarket.Content = OsLocalization.Trader.Label771;
            CheckBoxIsEmulator.Click += CheckBoxIsEmulator_Click;
            CheckBoxServerStopOrder.Click += CheckBoxServerStopOrder_Click;
            CheckBoxServerStopMarket.Click += CheckBoxServerStopMarket_Click;
            LabelFakeOpenDate.Content = OsLocalization.Trader.Label209;
            LabelFakeOpenTime.Content = OsLocalization.Trader.Label210;
            ButtonFakeTimeOpenNow.Content = OsLocalization.Trader.Label211;

            ComboBoxStopLimitType.Items.Add(StopActivateType.LowerOrEqual.ToString());
            ComboBoxStopLimitType.Items.Add(StopActivateType.HigherOrEqual.ToString());
            ComboBoxStopLimitType.SelectedItem = StopActivateType.HigherOrEqual.ToString();
            ComboBoxStopLifetimeType.Items.Add(PositionOpenerToStopLifeTimeType.CandlesCount.ToString());
            ComboBoxStopLifetimeType.Items.Add(PositionOpenerToStopLifeTimeType.NoLifeTime.ToString());
            ComboBoxStopLifetimeType.SelectedItem = PositionOpenerToStopLifeTimeType.CandlesCount.ToString();
            ComboBoxStopMarketLimitType.Items.Add(StopActivateType.LowerOrEqual.ToString());
            ComboBoxStopMarketLimitType.Items.Add(StopActivateType.HigherOrEqual.ToString());
            ComboBoxStopMarketLimitType.SelectedItem = StopActivateType.HigherOrEqual.ToString();
            ComboBoxStopMarketLifetimeType.Items.Add(PositionOpenerToStopLifeTimeType.CandlesCount.ToString());
            ComboBoxStopMarketLifetimeType.Items.Add(PositionOpenerToStopLifeTimeType.NoLifeTime.ToString());
            ComboBoxStopMarketLifetimeType.SelectedItem = PositionOpenerToStopLifeTimeType.CandlesCount.ToString();

            TextBoxLimitVolume.Text = TextBoxMarketVolume.Text = TextBoxStopVolume.Text = "1";
            TextBoxStopLifeTime.Text = TextBoxFakeVolume.Text = TextBoxStopMarketVolume.Text = "1";
            TextBoxStopMarketLifeTime.Text = "1";
            SetFakeTime(DateTime.Now);
            Loaded += Window_Loaded;
            Closed += Window_Closed;
            GlobalGUILayout.Listen(this, "vpsPositionOpen_" + botId + "_" + tabName);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _depthGrid = CreateMarketDepthGrid();
            WinFormsHostMarketDepth.Child = _depthGrid;
            await RefreshRemoteStateAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (s, args) => await RefreshMarketDepthAsync();
            _refreshTimer.Start();
        }

        private DataGridView CreateMarketDepthGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            grid.AllowUserToResizeRows = false;
            grid.ScrollBars = ScrollBars.Vertical;
            string[] headers = { OsLocalization.Entity.ColumnMarketDepth1, OsLocalization.Entity.ColumnMarketDepth3, OsLocalization.Entity.ColumnMarketDepth2, OsLocalization.Entity.ColumnMarketDepth3 };
            for (int i = 0; i < headers.Length; i++)
            {
                DataGridViewTextBoxCell cell = new DataGridViewTextBoxCell();
                if (i == 0) cell.Style = grid.DefaultCellStyle;
                DataGridViewColumn column = new DataGridViewColumn { CellTemplate = cell, HeaderText = headers[i], ReadOnly = true };
                column.AutoSizeMode = i == 2 ? DataGridViewAutoSizeColumnMode.None : DataGridViewAutoSizeColumnMode.Fill;
                if (i == 2) column.Width = 90;
                grid.Columns.Add(column);
            }

            for (int i = 0; i < 50; i++)
            {
                int row = grid.Rows.Add(null, null, null, null);
                bool isAsk = i < 25;
                Color sideColor = Themes.ThemeManager.GetColorWinForms(isAsk ? "MarketDepthAskColor" : "MarketDepthBidColor");
                grid.Rows[row].DefaultCellStyle.BackColor = Themes.ThemeManager.GetColorWinForms(isAsk ? "MarketDepthAskBackColor" : "MarketDepthBidBackColor");
                grid.Rows[row].DefaultCellStyle.ForeColor = sideColor;
                grid.Rows[row].DefaultCellStyle.Font = new Font("New Times Roman", 10);
                DataGridViewCellStyle barStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = sideColor, Font = new Font("Areal", 3) };
                grid.Rows[row].Cells[0].Style = barStyle;
                grid.Rows[row].Cells[1].Style = barStyle;
            }

            grid.Rows[22].Cells[0].Selected = true;
            grid.Rows[22].Cells[0].Selected = false;
            grid.CellClick += DepthGrid_CellClick;
            return grid;
        }

        private async System.Threading.Tasks.Task RefreshRemoteStateAsync()
        {
            try
            {
                JsonElement snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = 1 });
                LabelServerTypeValue.Content = ReadString(snapshot, "server_type");
                LabelSecurityValue.Content = string.IsNullOrEmpty(_securityName) ? ReadString(snapshot, "security_name") : _securityName;
                LabelTabNameValue.Content = _tabName;
                _stopOrdersSupported = ReadBool(snapshot, "server_stop_orders_supported");
                _serverStopOrdersOn = ReadBool(snapshot, "server_stop_orders_is_on");
                _loadingState = true;
                CheckBoxIsEmulator.IsChecked = ReadBool(snapshot, "emulator_is_on");
                CheckBoxServerStopOrder.IsChecked = _serverStopOrdersOn;
                CheckBoxServerStopMarket.IsChecked = _serverStopOrdersOn;
                CheckBoxServerStopOrder.IsEnabled = _stopOrdersSupported;
                CheckBoxServerStopMarket.IsEnabled = _stopOrdersSupported;
                _loadingState = false;
                UpdateStopFieldsVisibility();
                UpdateStopMarketFieldsVisibility();
                await RefreshMarketDepthAsync();
            }
            catch (Exception ex)
            {
                _loadingState = false;
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async System.Threading.Tasks.Task RefreshMarketDepthAsync()
        {
            if (_client == null || !_client.IsConnected || _depthGrid == null) return;
            try
            {
                JsonElement data = await _client.CallToolAsync("bot_chart_get_market_depth", new { bot_id = _botId, tab_name = _tabName, level_count = 25 });
                ClearDepthGrid();
                if (string.Equals(ReadString(data, "mode"), "BidAsk", StringComparison.OrdinalIgnoreCase))
                {
                    decimal ask = ReadDecimal(data, "best_ask");
                    decimal bid = ReadDecimal(data, "best_bid");
                    if (ask != 0) _depthGrid.Rows[24].Cells[2].Value = FormatNumber(ask);
                    if (bid != 0) _depthGrid.Rows[25].Cells[2].Value = FormatNumber(bid);
                    return;
                }

                if (!data.TryGetProperty("bids", out JsonElement bids) || !data.TryGetProperty("asks", out JsonElement asks)) return;
                decimal maxVolume = 0;
                foreach (JsonElement level in bids.EnumerateArray()) maxVolume = Math.Max(maxVolume, ReadDecimal(level, "volume"));
                foreach (JsonElement level in asks.EnumerateArray()) maxVolume = Math.Max(maxVolume, ReadDecimal(level, "volume"));
                decimal totalBid = bids.EnumerateArray().Sum(level => ReadDecimal(level, "volume"));
                decimal totalAsk = asks.EnumerateArray().Sum(level => ReadDecimal(level, "volume"));
                decimal maxTotal = Math.Max(totalBid, totalAsk);
                decimal sum = 0;
                for (int i = 0; i < Math.Min(25, bids.GetArrayLength()); i++)
                {
                    JsonElement level = bids[i];
                    decimal volume = ReadDecimal(level, "volume");
                    sum += volume;
                    int row = 25 + i;
                    _depthGrid.Rows[row].Cells[0].Value = Bar(sum, maxTotal);
                    _depthGrid.Rows[row].Cells[1].Value = Bar(volume, maxVolume);
                    _depthGrid.Rows[row].Cells[2].Value = FormatNumber(ReadDecimal(level, "price"));
                    _depthGrid.Rows[row].Cells[3].Value = FormatNumber(volume);
                }
                sum = 0;
                for (int i = 0; i < Math.Min(25, asks.GetArrayLength()); i++)
                {
                    JsonElement level = asks[i];
                    decimal volume = ReadDecimal(level, "volume");
                    sum += volume;
                    int row = 24 - i;
                    _depthGrid.Rows[row].Cells[0].Value = Bar(sum, maxTotal);
                    _depthGrid.Rows[row].Cells[1].Value = Bar(volume, maxVolume);
                    _depthGrid.Rows[row].Cells[2].Value = FormatNumber(ReadDecimal(level, "price"));
                    _depthGrid.Rows[row].Cells[3].Value = FormatNumber(volume);
                }
            }
            catch (Exception ex)
            {
                Title = OsLocalization.Trader.Label196 + " / " + ex.Message;
            }
        }

        private static string Bar(decimal value, decimal max)
        {
            if (max <= 0 || value <= 0) return string.Empty;
            int count = Math.Max(1, Math.Min(50, (int)Math.Round(value / max * 50m)));
            return new string('|', count);
        }

        private void ClearDepthGrid()
        {
            for (int i = 0; i < _depthGrid.Rows.Count; i++)
                for (int j = 0; j < _depthGrid.Columns.Count; j++) _depthGrid.Rows[i].Cells[j].Value = null;
        }

        private void DepthGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _depthGrid.Rows[e.RowIndex].Cells[2].Value == null) return;
            string price = _depthGrid.Rows[e.RowIndex].Cells[2].Value.ToString();
            TextBoxLimitPrice.Text = price;
            TextBoxStopActivationPrice.Text = price;
            TextBoxStopPrice.Text = price;
            TextBoxStopMarketActivationPrice.Text = price;
            TextBoxFakePrice.Text = price;
        }

        private async void ButtonBuy_Click(object sender, RoutedEventArgs e) => await SendSelectedOrderAsync("Buy");
        private async void ButtonSell_Click(object sender, RoutedEventArgs e) => await SendSelectedOrderAsync("Sell");

        private async System.Threading.Tasks.Task SendSelectedOrderAsync(string side)
        {
            try
            {
                Dictionary<string, object> args = new Dictionary<string, object>
                {
                    ["bot_id"] = _botId,
                    ["tab_name"] = _tabName
                };
                string action;
                switch (TabControlTypePosition.SelectedIndex)
                {
                    case 0:
                        action = side + "AtLimit";
                        args["volume"] = ParseDecimal(TextBoxLimitVolume.Text);
                        args["price"] = ParseDecimal(TextBoxLimitPrice.Text);
                        break;
                    case 1:
                        action = side + "AtMarket";
                        args["volume"] = ParseDecimal(TextBoxMarketVolume.Text);
                        break;
                    case 2:
                        action = side + "AtStop";
                        args["volume"] = ParseDecimal(TextBoxStopVolume.Text);
                        args["price"] = ParseDecimal(TextBoxStopPrice.Text);
                        args["activation_price"] = ParseDecimal(TextBoxStopActivationPrice.Text);
                        args["stop_activate_type"] = ComboBoxStopLimitType.SelectedItem.ToString();
                        args["lifetime_bars"] = int.Parse(TextBoxStopLifeTime.Text, CultureInfo.CurrentCulture);
                        args["lifetime_type"] = ComboBoxStopLifetimeType.SelectedItem.ToString();
                        args["server_stop"] = CheckBoxServerStopOrder.IsChecked == true;
                        break;
                    case 3:
                        action = side + "AtStopMarket";
                        args["volume"] = ParseDecimal(TextBoxStopMarketVolume.Text);
                        args["activation_price"] = ParseDecimal(TextBoxStopMarketActivationPrice.Text);
                        args["stop_activate_type"] = ComboBoxStopMarketLimitType.SelectedItem.ToString();
                        args["lifetime_bars"] = int.Parse(TextBoxStopMarketLifeTime.Text, CultureInfo.CurrentCulture);
                        args["lifetime_type"] = ComboBoxStopMarketLifetimeType.SelectedItem.ToString();
                        args["server_stop"] = CheckBoxServerStopMarket.IsChecked == true;
                        break;
                    case 4:
                        action = side + "AtFake";
                        args["volume"] = ParseDecimal(TextBoxFakeVolume.Text);
                        args["price"] = ParseDecimal(TextBoxFakePrice.Text);
                        args["time_local"] = GetFakeDateTime().ToString("O", CultureInfo.InvariantCulture);
                        break;
                    default:
                        return;
                }
                args["action"] = action;
                foreach (KeyValuePair<string, object> pair in args)
                    if (pair.Key == "volume" && Convert.ToDecimal(pair.Value, CultureInfo.InvariantCulture) <= 0)
                        throw new ArgumentOutOfRangeException("volume", "Volume must be greater than zero");
                await _client.CallToolAsync("bot_chart_execute_action", args);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.CurrentCulture);

        private DateTime GetFakeDateTime()
        {
            if (!DatePickerFakeOpenDate.SelectedDate.HasValue) throw new ArgumentException("Fake open date is required");
            string[] time = TextBoxFakeOpenTime.Text.Split(':');
            if (time.Length != 2) throw new ArgumentException("Fake open time must use HH:mm");
            return DatePickerFakeOpenDate.SelectedDate.Value.Date.AddHours(int.Parse(time[0])).AddMinutes(int.Parse(time[1]));
        }

        private async void CheckBoxIsEmulator_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingState) return;
            try
            {
                await SendActionAsync("SetEmulator", new Dictionary<string, object> { ["emulator_is_on"] = CheckBoxIsEmulator.IsChecked == true });
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private async void CheckBoxServerStopOrder_Click(object sender, RoutedEventArgs e)
        {
            await SetServerStopOrdersAsync(CheckBoxServerStopOrder.IsChecked == true);
        }

        private async void CheckBoxServerStopMarket_Click(object sender, RoutedEventArgs e)
        {
            await SetServerStopOrdersAsync(CheckBoxServerStopMarket.IsChecked == true);
        }

        private async System.Threading.Tasks.Task SetServerStopOrdersAsync(bool enabled)
        {
            _serverStopOrdersOn = enabled;
            _loadingState = true;
            CheckBoxServerStopOrder.IsChecked = enabled;
            CheckBoxServerStopMarket.IsChecked = enabled;
            _loadingState = false;
            UpdateStopFieldsVisibility();
            UpdateStopMarketFieldsVisibility();
            try
            {
                await SendActionAsync("SetServerStopOrders", new Dictionary<string, object> { ["server_stop"] = enabled });
            }
            catch (Exception ex)
            {
                _loadingState = true;
                CheckBoxServerStopOrder.IsChecked = _serverStopOrdersOn = !enabled;
                CheckBoxServerStopMarket.IsChecked = _serverStopOrdersOn;
                _loadingState = false;
                UpdateStopFieldsVisibility();
                UpdateStopMarketFieldsVisibility();
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private System.Threading.Tasks.Task<JsonElement> SendActionAsync(string action, Dictionary<string, object> values)
        {
            values["bot_id"] = _botId;
            values["tab_name"] = _tabName;
            values["action"] = action;
            return _client.CallToolAsync("bot_chart_execute_action", values);
        }

        private void UpdateStopFieldsVisibility()
        {
            Visibility visibility = CheckBoxServerStopOrder.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            ComboBoxStopLimitType.Visibility = LabelStopActivationType.Visibility = ComboBoxStopLifetimeType.Visibility = LabelStopLifeTimeType.Visibility = visibility;
            TextBoxStopLifeTime.Visibility = LabelStopLifeTime.Visibility = visibility;
        }

        private void UpdateStopMarketFieldsVisibility()
        {
            Visibility visibility = CheckBoxServerStopMarket.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            ComboBoxStopMarketLimitType.Visibility = LabelStopMarketActivationType.Visibility = ComboBoxStopMarketLifetimeType.Visibility = LabelStopMarketLifeTimeType.Visibility = visibility;
            TextBoxStopMarketLifeTime.Visibility = LabelStopMarketLifeTime.Visibility = visibility;
        }

        private void ButtonFakeTimeOpenNow_Click(object sender, RoutedEventArgs e) => SetFakeTime(DateTime.Now);

        private void SetFakeTime(DateTime time)
        {
            DatePickerFakeOpenDate.SelectedDate = time.Date;
            TextBoxFakeOpenTime.Text = time.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
            if (WinFormsHostMarketDepth != null) WinFormsHostMarketDepth.Child = null;
            if (_depthGrid != null) { _depthGrid.Dispose(); _depthGrid = null; }
        }

        private static string ReadString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;
        }

        private static decimal ReadDecimal(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal() : 0m;
        }

        private static bool ReadBool(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        }

        private static string FormatNumber(decimal value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    }
}
