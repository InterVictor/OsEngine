using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Layout;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote copy of OsTrader.Panels.Tab.Internal.PositionAddingUi2 ("Add to position"): same XAML, same five tabs
    /// (Limit / Market / Stop-Limit / Stop-Market / Fake). Each button calls bot_position_add, which runs the same
    /// BotTabSimple *ToPosition method on the VPS as the original (Buy* for a long position, Sell* for a short).
    /// </summary>
    public partial class RobotsVpsPositionAddingUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private readonly int _positionNumber;
        private string _securityName;
        private DataGridView _depthGrid;
        private DispatcherTimer _refreshTimer;
        private bool _loadingState;

        public int PositionNumber => _positionNumber;

        public RobotsVpsPositionAddingUi(RemoteMcpClient client, string botId, string tabName, string securityName, int positionNumber)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _tabName = tabName;
            _securityName = securityName;
            _positionNumber = positionNumber;

            StickyBorders.Listen(this);

            LabelServerType.Content = OsLocalization.Trader.Label178 + ":";
            LabelSecurity.Content = OsLocalization.Trader.Label102 + ":";
            LabelTabName.Content = OsLocalization.Trader.Label194 + ":";
            LabelOpenVolume.Content = OsLocalization.Trader.Label223 + ":";
            LabelPosState.Content = OsLocalization.Trader.Label224 + ":";
            LabelPosNumber.Content = OsLocalization.Trader.Label225 + ":";
            LabelOpenSide.Content = OsLocalization.Trader.Label228 + ":";

            LabelLimitPrice.Content = OsLocalization.Trader.Label205;
            LabelStopPrice.Content = OsLocalization.Trader.Label205;
            LabelFakePrice.Content = OsLocalization.Trader.Label205;

            LabelLimitVolume.Content = OsLocalization.Trader.Label30;
            LabelMarketVolume.Content = OsLocalization.Trader.Label30;
            LabelStopVolume.Content = OsLocalization.Trader.Label30;
            LabelFakeVolume.Content = OsLocalization.Trader.Label30;

            LabelStopActivationPrice.Content = OsLocalization.Trader.Label206;
            LabelStopActivationType.Content = OsLocalization.Trader.Label207;
            LabelStopLifeTime.Content = OsLocalization.Trader.Label208;
            LabelStopLifeTimeType.Content = OsLocalization.Trader.Label212;

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

            TextBoxStopLifeTime.Text = "1";
            TextBoxStopMarketLifeTime.Text = "1";

            TabItemLimit.Header = OsLocalization.Trader.Label200;
            TabItemMarket.Header = OsLocalization.Trader.Label201;
            TabItemStop.Header = OsLocalization.Trader.Label202;
            TabItemStopMarket.Header = OsLocalization.Trader.Label772;
            TabItemFake.Header = OsLocalization.Trader.Label203;

            LabelStopMarketActivationPrice.Content = OsLocalization.Trader.Label206;
            LabelStopMarketActivationType.Content = OsLocalization.Trader.Label207;
            LabelStopMarketLifeTime.Content = OsLocalization.Trader.Label208;
            LabelStopMarketLifeTimeType.Content = OsLocalization.Trader.Label212;
            LabelStopMarketVolume.Content = OsLocalization.Trader.Label30;

            CheckBoxServerStopOrder.Content = OsLocalization.Trader.Label771;
            CheckBoxServerStopMarket.Content = OsLocalization.Trader.Label771;
            CheckBoxServerStopOrder.Click += CheckBoxServerStopOrder_Click;
            CheckBoxServerStopMarket.Click += CheckBoxServerStopMarket_Click;

            LabelFakeOpenDate.Content = OsLocalization.Trader.Label229;
            LabelFakeOpenTime.Content = OsLocalization.Trader.Label230;
            ButtonFakeTimeOpenNow.Content = OsLocalization.Trader.Label211;

            LabelTabNameValue.Content = tabName;
            LabelSecurityValue.Content = securityName;
            LabelPosNumberValue.Content = positionNumber.ToString(CultureInfo.CurrentCulture);

            SetNowTimeInControlsFakeOpenPos();

            Loaded += Window_Loaded;
            Closed += Window_Closed;
            GlobalGUILayout.Listen(this, "vpsAddPos_" + botId + "_" + tabName + positionNumber);
        }

        // Mirrors PositionAddingUi2.SelectTabIndx: 0 Limit, 1 Market, 2 Stop, 3 StopMarket, 4 Fake
        public void SelectTab(int index)
        {
            TabControlTypePosition.SelectedIndex = index;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _depthGrid = CreateMarketDepthGrid();
            WinFormsHostMarketDepth.Child = _depthGrid;
            await RefreshRemoteStateAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (s, args) =>
            {
                await RefreshMarketDepthAsync();
                await RefreshPositionAsync();
            };
            _refreshTimer.Start();
        }

        private async System.Threading.Tasks.Task RefreshRemoteStateAsync()
        {
            try
            {
                JsonElement snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = 1 });
                LabelServerTypeValue.Content = ReadString(snapshot, "server_type");

                // as the original: server stops only when the connector supports them; the checkbox shows the tab's setting
                bool supported = ReadBool(snapshot, "server_stop_orders_supported");
                bool isOn = ReadBool(snapshot, "server_stop_orders_is_on");
                _loadingState = true;
                CheckBoxServerStopOrder.IsEnabled = supported;
                CheckBoxServerStopMarket.IsEnabled = supported;
                CheckBoxServerStopOrder.IsChecked = isOn;
                CheckBoxServerStopMarket.IsChecked = isOn;
                _loadingState = false;
                UpdateStopFieldsVisibility();
                UpdateStopMarketFieldsVisibility();

                await RefreshPositionAsync();
                await RefreshMarketDepthAsync();
            }
            catch (Exception ex)
            {
                _loadingState = false;
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // PositionAddingUi2.RepaintCurPosStatus: volume and state; the window closes when the position is closed
        private async System.Threading.Tasks.Task RefreshPositionAsync()
        {
            try
            {
                JsonElement open = await _client.CallToolAsync("bot_position_get_open", new { bot_id = _botId, tab_name = _tabName });
                JsonElement position = default;
                if (open.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                {
                    position = positions.EnumerateArray().FirstOrDefault(p => ReadInt(p, "position_number") == _positionNumber);
                }

                if (position.ValueKind != JsonValueKind.Object)
                {
                    // not among the open positions any more — like the original, which closes on PositionStateType.Done
                    Close();
                    return;
                }

                _securityName = ReadString(position, "security_name");
                string side = ReadString(position, "direction");
                LabelSecurityValue.Content = _securityName;
                LabelOpenSideValue.Content = side;
                LabelOpenVolumeValue.Content = FormatNumber(ReadDecimal(position, "open_volume"));
                LabelPosStateValue.Content = ReadString(position, "state");
                SetTitleAndButtons(side);
            }
            catch (Exception ex)
            {
                Title = "Position adding / " + ex.Message;
            }
        }

        private void SetTitleAndButtons(string side)
        {
            bool buy = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase);
            Title = buy ? OsLocalization.Trader.Label674 : OsLocalization.Trader.Label675;
            ButtonAddAtLimit.Content = buy ? OsLocalization.Trader.Label676 : OsLocalization.Trader.Label680;
            ButtonAddAtMarket.Content = buy ? OsLocalization.Trader.Label677 : OsLocalization.Trader.Label681;
            ButtonAddAtStop.Content = buy ? OsLocalization.Trader.Label678 : OsLocalization.Trader.Label682;
            ButtonAddAtStopMarket.Content = buy ? OsLocalization.Trader.Label773 : OsLocalization.Trader.Label774;
            ButtonAddAtFake.Content = buy ? OsLocalization.Trader.Label679 : OsLocalization.Trader.Label683;
        }

        #region Market depth (same grid as RobotsVpsPositionOpenUi / RobotsVpsPositionCloseUi)

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
                Title = "Position adding / " + ex.Message;
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

        // PositionAddingUi2.MarketDepthPainter_UserClickOnMDAndSelectPriceEvent: the clicked price goes to every price field
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

        #endregion

        #region Add actions (bot_position_add)

        private Dictionary<string, object> Args(string orderType, decimal volume)
        {
            return new Dictionary<string, object>
            {
                ["bot_id"] = _botId,
                ["tab_name"] = _tabName,
                ["position_number"] = _positionNumber,
                ["security_name"] = _securityName,
                ["order_type"] = orderType,
                ["volume"] = volume
            };
        }

        private async void ButtonAddAtLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TextBoxLimitPrice.Text, out decimal price) || !TryParse(TextBoxLimitVolume.Text, out decimal volume)) return;
            if (price == 0 || volume == 0) return;

            Dictionary<string, object> args = Args("Limit", volume);
            args["price"] = price;
            await RunAsync(args);
        }

        private async void ButtonAddAtMarket_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(TextBoxMarketVolume.Text))
            {
                System.Windows.MessageBox.Show(OsLocalization.Trader.Label389, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParse(TextBoxMarketVolume.Text, out decimal volume) || volume == 0) return;
            await RunAsync(Args("Market", volume));
        }

        private async void ButtonAddAtStop_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TextBoxStopActivationPrice.Text, out decimal activation)
                || !TryParse(TextBoxStopPrice.Text, out decimal price)
                || !TryParse(TextBoxStopVolume.Text, out decimal volume)) return;
            if (activation == 0 || price == 0 || volume == 0) return;

            Dictionary<string, object> args = Args("Stop", volume);
            args["price"] = price;
            args["activation_price"] = activation;
            args["server_stop"] = CheckBoxServerStopOrder.IsChecked == true;

            if (CheckBoxServerStopOrder.IsChecked != true)
            {
                if (!int.TryParse(TextBoxStopLifeTime.Text, out int lifeTime))
                {
                    System.Windows.MessageBox.Show("Lifetime candles must be a whole number", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                args["stop_activate_type"] = ComboBoxStopLimitType.SelectedItem?.ToString();
                args["lifetime_type"] = ComboBoxStopLifetimeType.SelectedItem?.ToString();
                args["lifetime_bars"] = lifeTime;
            }

            await RunAsync(args);
        }

        private async void ButtonAddAtStopMarket_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TextBoxStopMarketActivationPrice.Text, out decimal activation)
                || !TryParse(TextBoxStopMarketVolume.Text, out decimal volume)) return;
            if (activation == 0 || volume == 0) return;

            Dictionary<string, object> args = Args("StopMarket", volume);
            args["activation_price"] = activation;
            args["server_stop"] = CheckBoxServerStopMarket.IsChecked == true;

            if (CheckBoxServerStopMarket.IsChecked != true)
            {
                if (!int.TryParse(TextBoxStopMarketLifeTime.Text, out int lifeTime))
                {
                    System.Windows.MessageBox.Show("Lifetime candles must be a whole number", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                args["stop_activate_type"] = ComboBoxStopMarketLimitType.SelectedItem?.ToString();
                args["lifetime_type"] = ComboBoxStopMarketLifetimeType.SelectedItem?.ToString();
                args["lifetime_bars"] = lifeTime;
            }

            await RunAsync(args);
        }

        private async void ButtonAddAtFake_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParse(TextBoxFakePrice.Text, out decimal price) || !TryParse(TextBoxFakeVolume.Text, out decimal volume)) return;
            if (price == 0 || volume == 0 || DatePickerFakeOpenDate.SelectedDate == null) return;

            DateTime time;
            try
            {
                string[] parts = TextBoxFakeOpenTime.Text.Split(':');
                time = DatePickerFakeOpenDate.SelectedDate.Value.Date
                    .AddHours(Convert.ToInt32(parts[0]))
                    .AddMinutes(Convert.ToInt32(parts[1]));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Dictionary<string, object> args = Args("Fake", volume);
            args["price"] = price;
            args["time_local"] = time.ToString("O", CultureInfo.InvariantCulture);
            await RunAsync(args);
        }

        private async System.Threading.Tasks.Task RunAsync(Dictionary<string, object> args)
        {
            try
            {
                await _client.CallToolAsync("bot_position_add", args);
                await RefreshPositionAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static bool TryParse(string text, out decimal value)
        {
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
                || decimal.TryParse(text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            System.Windows.MessageBox.Show("Not a number: " + text, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        #endregion

        #region Server stop checkbox (PositionAddingUi2 stores it in Tab.ServerStopOrdersIsOn)

        private async void CheckBoxServerStopOrder_Click(object sender, RoutedEventArgs e) =>
            await SetServerStopOrdersAsync(CheckBoxServerStopOrder.IsChecked == true);

        private async void CheckBoxServerStopMarket_Click(object sender, RoutedEventArgs e) =>
            await SetServerStopOrdersAsync(CheckBoxServerStopMarket.IsChecked == true);

        private async System.Threading.Tasks.Task SetServerStopOrdersAsync(bool enabled)
        {
            if (_loadingState) return;

            _loadingState = true;
            CheckBoxServerStopOrder.IsChecked = enabled;
            CheckBoxServerStopMarket.IsChecked = enabled;
            _loadingState = false;
            UpdateStopFieldsVisibility();
            UpdateStopMarketFieldsVisibility();

            try
            {
                await _client.CallToolAsync("bot_chart_execute_action", new Dictionary<string, object>
                {
                    ["bot_id"] = _botId,
                    ["tab_name"] = _tabName,
                    ["action"] = "SetServerStopOrders",
                    ["server_stop"] = enabled
                });
            }
            catch (Exception ex)
            {
                _loadingState = true;
                CheckBoxServerStopOrder.IsChecked = !enabled;
                CheckBoxServerStopMarket.IsChecked = !enabled;
                _loadingState = false;
                UpdateStopFieldsVisibility();
                UpdateStopMarketFieldsVisibility();
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateStopFieldsVisibility()
        {
            Visibility vis = CheckBoxServerStopOrder.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            ComboBoxStopLimitType.Visibility = vis;
            LabelStopActivationType.Visibility = vis;
            ComboBoxStopLifetimeType.Visibility = vis;
            LabelStopLifeTimeType.Visibility = vis;
            TextBoxStopLifeTime.Visibility = vis;
            LabelStopLifeTime.Visibility = vis;
        }

        private void UpdateStopMarketFieldsVisibility()
        {
            Visibility vis = CheckBoxServerStopMarket.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            ComboBoxStopMarketLimitType.Visibility = vis;
            LabelStopMarketActivationType.Visibility = vis;
            ComboBoxStopMarketLifetimeType.Visibility = vis;
            LabelStopMarketLifeTimeType.Visibility = vis;
            TextBoxStopMarketLifeTime.Visibility = vis;
            LabelStopMarketLifeTime.Visibility = vis;
        }

        #endregion

        private void ButtonFakeTimeOpenNow_Click(object sender, RoutedEventArgs e) => SetNowTimeInControlsFakeOpenPos();

        private void SetNowTimeInControlsFakeOpenPos()
        {
            DateTime time = DateTime.Now;
            DatePickerFakeOpenDate.SelectedDate = time.Date;
            TextBoxFakeOpenTime.Text = time.Hour + ":" + time.Minute;
        }

        // the help post opens a web page on this computer — works the same as in the original
        private void ButtonPostPositionAdding_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InteractiveInstructions.BotStationLightPosts.Link34.ShowLinkInBrowser();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

        private static string ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;

        private static decimal ReadDecimal(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal() : 0m;

        private static int ReadInt(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int i)
                ? i : 0;

        private static bool ReadBool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static string FormatNumber(decimal value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    }
}
