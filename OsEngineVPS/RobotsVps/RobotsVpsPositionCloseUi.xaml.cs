using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
    /// Remote copy of Journal.Internal.PositionCloseUi2: closes/arms protective orders for ONE
    /// already-open position, via the position-close MCP tools added alongside it
    /// (bot_position_close_at_limit/market/stop/stop_market/profit, bot_position_revoke_*).
    /// Opened from the Chart tab's open-positions context menu ("Close selected" / "Add to selected"
    /// reuse this same window — the parent dialog is identical for both, only the initial tab differs).
    /// </summary>
    public partial class RobotsVpsPositionCloseUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private readonly int _positionNumber;
        private string _securityName;
        private decimal _openVolume;
        private DataGridView _depthGrid;
        private DispatcherTimer _refreshTimer;
        private bool _stopOrdersSupported;
        private bool _loadingState;

        public int PositionNumber => _positionNumber;

        public RobotsVpsPositionCloseUi(RemoteMcpClient client, string botId, string tabName, string securityName, int positionNumber)
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
            LabelPosNumber.Content = "Pos num:";
            LabelOpenSide.Content = "Side:";
            LabelOpenVolume.Content = OsLocalization.Trader.Label30 + ":";
            LabelPosState.Content = "State:";
            LabelLimitPrice.Content = LabelStopPrice.Content = LabelFakePrice.Content = OsLocalization.Trader.Label205;
            LabelStopActivationPrice.Content = LabelStopMarketActivationPrice.Content = LabelProfitActivationPrice.Content = OsLocalization.Trader.Label206;
            LabelFakeOpenDate.Content = OsLocalization.Trader.Label209;
            LabelFakeOpenTime.Content = OsLocalization.Trader.Label210;
            ButtonFakeTimeOpenNow.Content = OsLocalization.Trader.Label211;
            CheckBoxServerStopOrder.Content = CheckBoxServerStopMarket.Content = OsLocalization.Trader.Label771;

            SetFakeTime(DateTime.Now);
            Loaded += Window_Loaded;
            Closed += Window_Closed;
            GlobalGUILayout.Listen(this, "vpsPositionClose_" + botId + "_" + tabName);
        }

        /// <summary>Switches the active tab after the window is already open (mirrors PositionCloseUi2.SelectTabIndx),
        /// used by the position context menu to re-focus an already-open dialog on "Swap stop"/"Swap profit".</summary>
        public void SelectTab(string tabName)
        {
            TabItem item = tabName switch
            {
                "Stop" => TabItemStop,
                "Profit" => TabItemProfit,
                _ => TabItemLimit
            };
            TabControlTypePosition.SelectedItem = item;
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

        private async System.Threading.Tasks.Task RefreshRemoteStateAsync()
        {
            try
            {
                JsonElement snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = 1 });
                LabelServerTypeValue.Content = ReadString(snapshot, "server_type");
                _stopOrdersSupported = ReadBool(snapshot, "server_stop_orders_supported");
                _loadingState = true;
                CheckBoxServerStopOrder.IsEnabled = _stopOrdersSupported;
                CheckBoxServerStopMarket.IsEnabled = _stopOrdersSupported;
                _loadingState = false;

                JsonElement open = await _client.CallToolAsync("bot_position_get_open", new { bot_id = _botId, tab_name = _tabName });
                JsonElement position = default;
                if (open.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                {
                    position = positions.EnumerateArray().FirstOrDefault(p => ReadInt(p, "position_number") == _positionNumber);
                }

                if (position.ValueKind != JsonValueKind.Object)
                {
                    Title = "Position close / position not found (already closed?)";
                    return;
                }

                _securityName = ReadString(position, "security_name");
                _openVolume = ReadDecimal(position, "open_volume");
                string side = ReadString(position, "direction");

                LabelSecurityValue.Content = _securityName;
                LabelPosNumberValue.Content = _positionNumber.ToString(CultureInfo.CurrentCulture);
                LabelOpenSideValue.Content = side;
                LabelOpenVolumeValue.Content = FormatNumber(_openVolume);
                LabelPosStateValue.Content = ReadString(position, "state");
                Title = $"Position close #{_positionNumber} / {_securityName} / {side}";

                string allOpenText = "All open volume: " + FormatNumber(_openVolume);
                LabelLimitAllOpenVolumeSend.Content = LabelMarketAllOpenVolumeSend.Content = allOpenText;
                LabelStopAllOpenVolumeSend.Content = LabelStopMarketAllOpenVolumeSend.Content = allOpenText;
                LabelFakeAllOpenVolume.Content = allOpenText;

                await RefreshMarketDepthAsync();
            }
            catch (Exception ex)
            {
                _loadingState = false;
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #region Market depth (same layout/behavior as RobotsVpsPositionOpenUi)

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
                Title = "Position close / " + ex.Message;
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
            TextBoxProfitActivationPrice.Text = price;
            TextBoxProfitPrice.Text = price;
            TextBoxFakePrice.Text = price;
        }

        #endregion

        #region Close actions

        private object Args(params (string key, object value)[] extra)
        {
            System.Collections.Generic.Dictionary<string, object> args = new()
            {
                ["bot_id"] = _botId,
                ["tab_name"] = _tabName,
                ["position_number"] = _positionNumber,
                ["security_name"] = _securityName
            };
            foreach ((string key, object value) in extra) args[key] = value;
            return args;
        }

        private async void ButtonCloseAtLimit_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_close_at_limit", Args(
                ("price", ParseDecimal(TextBoxLimitPrice.Text)),
                ("volume", ResolveVolume(TextBoxLimitVolumeToClose.Text))));
        }

        private async void ButtonCloseAtMarket_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_close_at_market", Args(
                ("volume", ResolveVolume(TextBoxMarketVolumeToClose.Text))));
        }

        private async void ButtonCloseAtStop_Click(object sender, RoutedEventArgs e)
        {
            bool serverSide = CheckBoxServerStopOrder.IsChecked == true;
            await RunAsync("bot_position_close_at_stop", Args(
                ("activation_price", ParseDecimal(TextBoxStopActivationPrice.Text)),
                ("price", ParseDecimal(TextBoxStopPrice.Text)),
                ("server_side", serverSide),
                ("volume", ResolveVolume(TextBoxStopVolumeToClose.Text))));
        }

        private async void ButtonCloseAtStopMarket_Click(object sender, RoutedEventArgs e)
        {
            bool serverSide = CheckBoxServerStopMarket.IsChecked == true;
            await RunAsync("bot_position_close_at_stop_market", Args(
                ("activation_price", ParseDecimal(TextBoxStopMarketActivationPrice.Text)),
                ("server_side", serverSide),
                ("volume", ResolveVolume(TextBoxStopMarketVolumeToClose.Text))));
        }

        private async void ButtonCloseAtProfit_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_close_at_profit", Args(
                ("activation_price", ParseDecimal(TextBoxProfitActivationPrice.Text)),
                ("price", ParseDecimal(TextBoxProfitPrice.Text))));
        }

        private async void ButtonCloseAtFake_Click(object sender, RoutedEventArgs e)
        {
            // bot_position_close_at_market(is_fake=true) фиксирует время закрытия как "сейчас" на сервере
            // (см. ClosePositionInternal) — как и оригинал, поле времени здесь только для отображения/будущего,
            // сервер его пока не принимает отдельно.
            await RunAsync("bot_position_close_at_market", Args(
                ("is_fake", true),
                ("price", ParseDecimal(TextBoxFakePrice.Text)),
                ("volume", ResolveVolume(TextBoxFakeVolume.Text))));
        }

        private async void ButtonRevokeLimit_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_revoke_close_orders", Args());
        }

        private async void ButtonRevokeStop_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_revoke_stop", Args(("server_side", CheckBoxServerStopOrder.IsChecked == true)));
        }

        private async void ButtonRevokeStopMarket_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_revoke_stop", Args(("server_side", CheckBoxServerStopMarket.IsChecked == true)));
        }

        private async void ButtonRevokeProfit_Click(object sender, RoutedEventArgs e)
        {
            await RunAsync("bot_position_revoke_profit", Args());
        }

        private async System.Threading.Tasks.Task RunAsync(string tool, object args)
        {
            try
            {
                await _client.CallToolAsync(tool, args);
                await RefreshRemoteStateAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private decimal ResolveVolume(string text) => string.IsNullOrWhiteSpace(text) ? _openVolume : ParseDecimal(text);

        private static decimal ParseDecimal(string value) => decimal.Parse(value, NumberStyles.Number, CultureInfo.CurrentCulture);

        #endregion

        #region "All open volume" quick-fill labels

        private void LabelLimitAllOpenVolumeSend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => TextBoxLimitVolumeToClose.Text = FormatNumber(_openVolume);
        private void LabelMarketAllOpenVolumeSend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => TextBoxMarketVolumeToClose.Text = FormatNumber(_openVolume);
        private void LabelStopAllOpenVolumeSend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => TextBoxStopVolumeToClose.Text = FormatNumber(_openVolume);
        private void LabelStopMarketAllOpenVolumeSend_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => TextBoxStopMarketVolumeToClose.Text = FormatNumber(_openVolume);
        private void LabelFakeAllOpenVolume_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => TextBoxFakeVolume.Text = FormatNumber(_openVolume);

        #endregion

        private void CheckBoxServerStopOrder_Click(object sender, RoutedEventArgs e) { }
        private void CheckBoxServerStopMarket_Click(object sender, RoutedEventArgs e) { }

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
