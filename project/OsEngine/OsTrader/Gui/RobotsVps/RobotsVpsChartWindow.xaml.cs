/*
 * VPS adaptation of BotPanelChartUi. The XAML is copied from that window so its layout,
 * resizing controls, themes and chart host remain native OsEngine UI. Only data/actions differ.
 */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using OsEngine.Charts.CandleChart;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Layout;
using OsEngine.MCP.Client;
using OsEngine.Market;
using OsEngine.OsTrader.Gui.BlockInterface;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public partial class RobotsVpsChartWindow : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _botName;
        private readonly string _tabName;
        private readonly string _layoutName;
        private ChartCandleMaster _chartMaster;
        private string _startTitle;
        private bool _settingsPanelIsHide;
        private bool _informPanelIsHide;
        private bool _lowPanelIsBig;
        private DataGridView _depthGrid;
        private DataGridView _alertsGrid;
        private DataGridView _gridsGrid;
        private DataGridView _openPositionsGrid;
        private DataGridView _stopLimitsGrid;
        private DataGridView _closedPositionsGrid;
        private DataGridView _botLogGrid;
        private DispatcherTimer _remotePollTimer;
        private bool _remoteRefreshInFlight;
        private bool _chartTimeFrameInitialized;
        private string _securityName;
        private RobotsVpsPositionOpenUi _remotePositionOpenWindow;

        public RobotsVpsChartWindow(RemoteMcpClient client, string botId, string botName, string tabName)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _botName = botName;
            _tabName = tabName;
            _layoutName = "Vps_" + botId;

            StickyBorders.Listen(this);
            StartupLocation.Start_FitHeightToWorkArea(this);
            Local();
            Title = string.IsNullOrWhiteSpace(botName) ? botId : botName + " / " + botId;
            _startTitle = Title;
            TabControlBotsName.Items[0] = botId;
            ((TabItem)TabControlBotTab.Items[0]).Header = tabName;
            ButtonShowInformPanel.Visibility = Visibility.Hidden;

            Loaded += ChartWindow_Loaded;
            Closed += ChartWindow_Closed;
            LocationChanged += ChartWindow_LocationChanged;
            rectToMove.MouseEnter += RectToMove_MouseEnter;
            rectToMove.MouseLeave += RectToMove_MouseLeave;
            rectToMove.MouseDown += RectToMove_MouseDown;
            TabControlBotTab.SelectionChanged += TabControlBotTab_SelectionChanged;
            TabControlControl.SelectionChanged += ChartPanels_SelectionChanged;
            TabControlPrime.SelectionChanged += ChartPanels_SelectionChanged;
            CheckPanels();
            GlobalGUILayout.Listen(this, "botPanelChartVps_" + botId);
        }

        private void Local()
        {
            TabPosition.Header = OsLocalization.Trader.Label18;
            TabItemClosedPos.Header = OsLocalization.Trader.Label19;
            TabItemLogBot.Header = OsLocalization.Trader.Label23;
            TabItemMarketDepth.Header = OsLocalization.Trader.Label25;
            TabItemAlerts.Header = OsLocalization.Trader.Label26;
            TabItemControl.Header = OsLocalization.Trader.Label27;
            TabItemStopLimits.Header = OsLocalization.Trader.Label193;
            ButtonBuyFast.Content = OsLocalization.Trader.Label28;
            ButtonSellFast.Content = OsLocalization.Trader.Label29;
            TextBoxVolumeInterText.Text = OsLocalization.Trader.Label30;
            TextBoxPriceText.Text = OsLocalization.Trader.Label31;
            ButtonBuyLimit.Content = OsLocalization.Trader.Label32;
            ButtonSellLimit.Content = OsLocalization.Trader.Label33;
            ButtonCloseLimit.Content = OsLocalization.Trader.Label34;
            LabelGeneralSettings.Content = OsLocalization.Trader.Label35;
            ButtonJournalCommunity.Content = OsLocalization.Trader.Label40;
            ButtonStrategyParameter.Content = OsLocalization.Trader.Label45;
            ButtonRiskManager.Content = OsLocalization.Trader.Label46;
            ButtonStrategySettings.Content = OsLocalization.Trader.Label47;
            ButtonStrategySettingsIndividual.Content = OsLocalization.Trader.Label43;
            ButtonRedactTab.Content = OsLocalization.Trader.Label44;
            ButtonMoreOpenPositionDetail.Content = OsLocalization.Trader.Label197;
            ButtonAddVisualAlert.Content = OsLocalization.Trader.Label440;
            ButtonAddPriceAlert.Content = OsLocalization.Trader.Label441;
            TabItemGrids.Header = OsLocalization.Trader.Label437;
        }

        private async void ChartWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CreateRemoteGrids();
            _chartMaster = new ChartCandleMaster(_layoutName + "_" + _tabName, StartProgram.IsOsTrader);
            _chartMaster.StartPaint(GridChart, ChartHostPanel, RectChart);
            await RefreshSelectedChartDataAsync();
            _remotePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _remotePollTimer.Tick += async (s, args) => await RefreshSelectedChartDataAsync();
            _remotePollTimer.Start();
        }

        private void CreateRemoteGrids()
        {
            _depthGrid = CreateMarketDepthGrid();
            HostGlass.Child = _depthGrid;

            _alertsGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddGridColumn(_alertsGrid, OsLocalization.Alerts.GridHeader0);
            AddGridColumn(_alertsGrid, OsLocalization.Alerts.GridHeader1);
            AddGridColumn(_alertsGrid, OsLocalization.Alerts.GridHeader2);
            HostAlert.Child = _alertsGrid;

            _gridsGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddGridColumn(_gridsGrid, "#");
            AddGridColumn(_gridsGrid, OsLocalization.Trader.Label467);
            AddGridColumn(_gridsGrid, OsLocalization.Trader.Label468);
            AddGridColumn(_gridsGrid, string.Empty);
            AddGridColumn(_gridsGrid, string.Empty);
            HostGrids.Child = _gridsGrid;

            _openPositionsGrid = DataGridFactory.GetDataGridPosition();
            _stopLimitsGrid = DataGridFactory.GetDataGridBuyAtStopPositions();
            _closedPositionsGrid = DataGridFactory.GetDataGridPosition();
            _botLogGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddGridColumn(_botLogGrid, OsLocalization.Logging.Column1, 200);
            AddGridColumn(_botLogGrid, OsLocalization.Logging.Column2, 100);
            AddGridColumn(_botLogGrid, OsLocalization.Logging.Column3);
            HostOpenPosition.Child = _openPositionsGrid;
            HostStopLimits.Child = _stopLimitsGrid;
            HostClosePosition.Child = _closedPositionsGrid;
            HostBotLog.Child = _botLogGrid;
        }

        private static void AddGridColumn(DataGridView grid, string header, int width = 0)
        {
            DataGridViewTextBoxCell cell = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn { CellTemplate = cell, HeaderText = header, ReadOnly = true };
            column.AutoSizeMode = width > 0 ? DataGridViewAutoSizeColumnMode.None : DataGridViewAutoSizeColumnMode.Fill;
            if (width > 0) column.Width = width;
            grid.Columns.Add(column);
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
                if (i == 0)
                {
                    cell.Style = grid.DefaultCellStyle;
                }
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

                DataGridViewCellStyle barStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleRight,
                    ForeColor = sideColor,
                    Font = new Font("Areal", 3)
                };
                grid.Rows[row].Cells[0].Style = barStyle;
                grid.Rows[row].Cells[1].Style = barStyle;
            }
            grid.Rows[22].Cells[0].Selected = true;
            grid.Rows[22].Cells[0].Selected = false;
            grid.CellClick += DepthGrid_CellClick;
            return grid;
        }

        private static void SetPlaceholder(WindowsFormsHost host, string text)
        {
            host.Child = new System.Windows.Forms.Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                ForeColor = System.Drawing.Color.DimGray,
                BackColor = System.Drawing.Color.FromArgb(24, 27, 33)
            };
        }

        private async System.Threading.Tasks.Task RefreshSelectedChartDataAsync()
        {
            if (_remoteRefreshInFlight || _client == null || !_client.IsConnected || _chartMaster == null)
                return;

            _remoteRefreshInFlight = true;
            try
            {
                JsonElement snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = 500 });
                List<Candle> candles = ReadCandles(snapshot);
                if (candles.Count > 0)
                {
                    TimeFrame timeFrame = ReadTimeFrame(snapshot);
                    if (!_chartTimeFrameInitialized)
                    {
                        _chartMaster.ChartCandle.SetNewTimeFrame(GetTimeSpan(timeFrame), timeFrame);
                        _chartTimeFrameInitialized = true;
                    }
                    _chartMaster.SetCandles(candles);
                    _securityName = ReadText(snapshot, "security_name");
                    string interval = ReadText(snapshot, "time_frame");
                    _startTitle = _botName + " / " + (string.IsNullOrWhiteSpace(_securityName) ? _tabName : _securityName + " / " + interval);
                    Title = _startTitle;
                }

                if (TabItemMarketDepth.IsSelected) await RefreshMarketDepthAsync();
                if (TabItemAlerts.IsSelected) await RefreshAlertsAsync();
                if (TabItemGrids.IsSelected) await RefreshGridsAsync();
                if (TabPosition.IsSelected) await RefreshOpenPositionsAsync();
                if (TabItemStopLimits.IsSelected) await RefreshStopLimitsAsync();
                if (TabItemClosedPos.IsSelected) await RefreshClosedPositionsAsync();
                if (TabItemLogBot.IsSelected) await RefreshBotLogAsync();
            }
            catch (Exception ex)
            {
                ShowGridStatus(_botLogGrid, "VPS chart data unavailable: " + ex.Message);
            }
            finally
            {
                _remoteRefreshInFlight = false;
            }
        }

        private void ChartPanels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) _ = RefreshSelectedChartDataAsync();
        }

        private async System.Threading.Tasks.Task RefreshMarketDepthAsync()
        {
            JsonElement data = await _client.CallToolAsync("bot_chart_get_market_depth", new { bot_id = _botId, tab_name = _tabName, level_count = 25 });
            RenderMarketDepth(data);
        }

        private void RenderMarketDepth(JsonElement data)
        {
            if (_depthGrid == null) return;
            ClearDepthGrid();

            string mode = ReadString(data, "mode");
            if (string.Equals(mode, "BidAsk", StringComparison.OrdinalIgnoreCase))
            {
                decimal ask = ReadDecimal(data, "best_ask");
                decimal bid = ReadDecimal(data, "best_bid");
                if (ask != 0) _depthGrid.Rows[24].Cells[2].Value = FormatRemoteNumber(ask);
                if (bid != 0) _depthGrid.Rows[25].Cells[2].Value = FormatRemoteNumber(bid);
                return;
            }

            if (!data.TryGetProperty("bids", out JsonElement bids) || !data.TryGetProperty("asks", out JsonElement asks)) return;

            decimal maxVolume = 0;
            foreach (JsonElement level in bids.EnumerateArray()) maxVolume = Math.Max(maxVolume, ReadDecimal(level, "volume"));
            foreach (JsonElement level in asks.EnumerateArray()) maxVolume = Math.Max(maxVolume, ReadDecimal(level, "volume"));
            decimal totalBid = bids.EnumerateArray().Sum(level => ReadDecimal(level, "volume"));
            decimal totalAsk = asks.EnumerateArray().Sum(level => ReadDecimal(level, "volume"));
            decimal scale = Math.Max(totalBid, totalAsk);
            decimal bidSum = 0;
            decimal askSum = 0;

            for (int i = 0; i < Math.Min(25, bids.GetArrayLength()); i++)
            {
                JsonElement level = bids[i];
                decimal volume = ReadDecimal(level, "volume");
                bidSum += volume;
                int row = 25 + i;
                _depthGrid.Rows[row].Cells[0].Value = Bar(bidSum, scale);
                _depthGrid.Rows[row].Cells[1].Value = Bar(volume, maxVolume);
                _depthGrid.Rows[row].Cells[2].Value = FormatRemoteNumber(ReadDecimal(level, "price"));
                _depthGrid.Rows[row].Cells[3].Value = FormatRemoteNumber(volume);
            }

            for (int i = 0; i < Math.Min(25, asks.GetArrayLength()); i++)
            {
                JsonElement level = asks[i];
                decimal volume = ReadDecimal(level, "volume");
                askSum += volume;
                int row = 24 - i;
                _depthGrid.Rows[row].Cells[0].Value = Bar(askSum, scale);
                _depthGrid.Rows[row].Cells[1].Value = Bar(volume, maxVolume);
                _depthGrid.Rows[row].Cells[2].Value = FormatRemoteNumber(ReadDecimal(level, "price"));
                _depthGrid.Rows[row].Cells[3].Value = FormatRemoteNumber(volume);
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

        private async System.Threading.Tasks.Task RefreshAlertsAsync()
        {
            JsonElement response = await _client.CallToolAsync("bot_chart_get_alerts", new { bot_id = _botId, tab_name = _tabName });
            int first = FirstVisible(_alertsGrid);
            _alertsGrid.Rows.Clear();
            if (response.TryGetProperty("alerts", out JsonElement alerts) && alerts.ValueKind == JsonValueKind.Array)
                foreach (JsonElement alert in alerts.EnumerateArray())
                    _alertsGrid.Rows.Add(ReadInt(alert, "number"), ReadString(alert, "type"), ReadBool(alert, "is_on") ? "On" : "Off");
            RestoreGridScroll(_alertsGrid, first);
        }

        private async System.Threading.Tasks.Task RefreshGridsAsync()
        {
            JsonElement response = await _client.CallToolAsync("bot_grid_get", new { bot_id = _botId, tab_name = _tabName });
            int first = FirstVisible(_gridsGrid);
            _gridsGrid.Rows.Clear();
            if (response.TryGetProperty("grids", out JsonElement grids) && grids.ValueKind == JsonValueKind.Array)
                foreach (JsonElement grid in grids.EnumerateArray())
                    _gridsGrid.Rows.Add(ReadInt(grid, "number"), ReadString(grid, "grid_type"), ReadString(grid, "regime"), OsLocalization.Trader.Label469, OsLocalization.Trader.Label470);
            _gridsGrid.Rows.Add(null, null, null, null, OsLocalization.Trader.Label471);
            RestoreGridScroll(_gridsGrid, first);
        }

        private async System.Threading.Tasks.Task RefreshOpenPositionsAsync()
        {
            JsonElement response = await _client.CallToolAsync("bot_journal_get_open_positions", new { bot_name = _botId, limit = 500 });
            RenderPositionRows(_openPositionsGrid, response, false);
        }

        private async System.Threading.Tasks.Task RefreshClosedPositionsAsync()
        {
            // The Lite shared positions tab shows closed positions from every robot.
            // Keep that same scope in the VPS chart's historical positions panel.
            JsonElement response = await _client.CallToolAsync("bot_journal_get_closed_positions", new { limit = 500 });
            RenderPositionRows(_closedPositionsGrid, response, true);
        }

        private async System.Threading.Tasks.Task RefreshStopLimitsAsync()
        {
            JsonElement response = await _client.CallToolAsync("bot_journal_get_stop_limit_positions", new { bot_name = _botId });
            int first = FirstVisible(_stopLimitsGrid);
            _stopLimitsGrid.Rows.Clear();
            if (response.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in positions.EnumerateArray())
                {
                    if (!string.Equals(ReadString(p, "tab_name"), _tabName, StringComparison.OrdinalIgnoreCase)) continue;
                    _stopLimitsGrid.Rows.Add(ReadInt(p, "number"), FormatRemoteTime(ReadString(p, "time_create")), ReadString(p, "tab_name"), ReadString(p, "security_name"), FormatRemoteNumber(ReadDecimal(p, "volume")), ReadString(p, "side"), ReadString(p, "activate_type"), FormatRemoteNumber(ReadDecimal(p, "price_red_line")), FormatRemoteNumber(ReadDecimal(p, "price_order")), ReadInt(p, "expires_bars"), ReadString(p, "lifetime_type"));
                }
            RestoreGridScroll(_stopLimitsGrid, first);
        }

        private async System.Threading.Tasks.Task RefreshBotLogAsync()
        {
            JsonElement response = await _client.CallToolAsync("bot_chart_get_log", new { bot_id = _botId, tab_name = _tabName, count = 200 });
            int first = FirstVisible(_botLogGrid);
            _botLogGrid.Rows.Clear();
            if (response.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
                foreach (JsonElement message in messages.EnumerateArray())
                    _botLogGrid.Rows.Add(FormatRemoteTime(ReadString(message, "time")), ReadString(message, "type"), ReadString(message, "message"));
            RestoreGridScroll(_botLogGrid, first);
        }

        private void RenderPositionRows(DataGridView grid, JsonElement response, bool closed)
        {
            int first = FirstVisible(grid);
            grid.Rows.Clear();
            if (!response.TryGetProperty("positions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement p in positions.EnumerateArray())
            {
                if (!closed && !string.IsNullOrEmpty(_securityName) && !string.Equals(ReadString(p, "security_name"), _securityName, StringComparison.OrdinalIgnoreCase)) continue;
                int rowIndex = grid.Rows.Add();
                DataGridViewRow row = grid.Rows[rowIndex];
                row.Cells[0].Value = ReadInt(p, "number");
                row.Cells[1].Value = FormatRemoteTime(ReadString(p, "open_time"));
                row.Cells[2].Value = closed ? FormatRemoteTime(ReadString(p, "close_time")) : string.Empty;
                row.Cells[3].Value = ReadString(p, "bot_name");
                row.Cells[4].Value = ReadString(p, "security_name");
                row.Cells[5].Value = ReadString(p, "side");
                row.Cells[6].Value = ReadString(p, "state");
                row.Cells[7].Value = FormatRemoteNumber(ReadDecimal(p, "volume"));
                row.Cells[8].Value = FormatRemoteNumber(ReadDecimal(p, "open_volume"));
                row.Cells[9].Value = FormatRemoteNumber(ReadDecimal(p, "wait_volume"));
                row.Cells[10].Value = FormatRemoteNumber(ReadDecimal(p, "entry_price"));
                row.Cells[11].Value = FormatRemoteNumber(ReadDecimal(p, "close_price"));
                row.Cells[12].Value = FormatRemoteNumber(ReadDecimal(p, "profit_abs"));
                row.Cells[13].Value = FormatRemoteNumber(ReadDecimal(p, "stop_order_red_line"));
                row.Cells[14].Value = FormatRemoteNumber(ReadDecimal(p, "stop_order_price"));
                row.Cells[15].Value = FormatRemoteNumber(ReadDecimal(p, "profit_order_red_line"));
                row.Cells[16].Value = FormatRemoteNumber(ReadDecimal(p, "profit_order_price"));
                row.Cells[17].Value = ReadString(p, "signal_type_open");
                row.Cells[18].Value = ReadString(p, "signal_type_close");
                grid.Rows.Add(row);
            }
            RestoreGridScroll(grid, first);
        }

        private static int FirstVisible(DataGridView grid) => grid != null && grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
        private static void RestoreGridScroll(DataGridView grid, int row) { if (grid != null && row >= 0 && row < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = row; }
        private static void ShowGridStatus(DataGridView grid, string text) { if (grid == null) return; grid.Rows.Clear(); if (grid.Columns.Count > 0) grid.Rows.Add(text); }
        private static int ReadInt(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
        private static decimal ReadDecimal(JsonElement item, string name) => TryDecimal(item, name, out decimal value) ? value : 0;
        private static string ReadString(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : string.Empty;
        private static bool ReadBool(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        private static string FormatRemoteNumber(decimal number) => number.ToString("0.########", CultureInfo.CurrentCulture);
        private static string FormatRemoteTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset date) ? date.LocalDateTime.ToString(CultureInfo.CurrentCulture) : value;

        private static List<Candle> ReadCandles(JsonElement result)
        {
            List<Candle> candles = new List<Candle>();
            if (!result.TryGetProperty("candles", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                return candles;

            foreach (JsonElement row in list.EnumerateArray())
            {
                if (!TryDecimal(row, "open", out decimal open) || !TryDecimal(row, "high", out decimal high)
                    || !TryDecimal(row, "low", out decimal low) || !TryDecimal(row, "close", out decimal close))
                    continue;

                DateTime time = DateTime.MinValue;
                if (row.TryGetProperty("time_utc", out JsonElement timeValue) && timeValue.ValueKind == JsonValueKind.String)
                    DateTime.TryParse(timeValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out time);
                TryDecimal(row, "volume", out decimal volume);
                string stateName = row.TryGetProperty("state", out JsonElement stateValue) ? stateValue.GetString() : null;
                CandleState state = stateName == "Started" ? CandleState.Started : CandleState.Finished;
                candles.Add(new Candle { TimeStart = time, Open = open, High = high, Low = low, Close = close, Volume = volume, State = state });
            }
            return candles;
        }

        private static bool TryDecimal(JsonElement row, string name, out decimal value)
        {
            value = 0;
            if (!row.TryGetProperty(name, out JsonElement item)) return false;
            if (item.ValueKind == JsonValueKind.Number) return item.TryGetDecimal(out value);
            return item.ValueKind == JsonValueKind.String && decimal.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static string ReadText(JsonElement result, string name)
        {
            return result.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() : string.Empty;
        }

        private static TimeFrame ReadTimeFrame(JsonElement result)
        {
            return Enum.TryParse(ReadText(result, "time_frame"), true, out TimeFrame timeFrame) ? timeFrame : TimeFrame.Min1;
        }

        private static TimeSpan GetTimeSpan(TimeFrame timeFrame)
        {
            string value = timeFrame.ToString();
            if (value.StartsWith("Sec", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(3), out int seconds)) return TimeSpan.FromSeconds(seconds);
            if (value.StartsWith("Min", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(3), out int minutes)) return TimeSpan.FromMinutes(minutes);
            if (value.StartsWith("Hour", StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(4), out int hours)) return TimeSpan.FromHours(hours);
            return timeFrame == TimeFrame.Day ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(1);
        }

        private void ShowStatus(string message)
        {
            SetPlaceholder(HostBotLog, message);
            TabControlPrime.SelectedItem = TabItemLogBot;
        }

        private void ChartWindow_Closed(object sender, EventArgs e)
        {
            Closed -= ChartWindow_Closed;
            Loaded -= ChartWindow_Loaded;
            LocationChanged -= ChartWindow_LocationChanged;
            rectToMove.MouseEnter -= RectToMove_MouseEnter;
            rectToMove.MouseLeave -= RectToMove_MouseLeave;
            rectToMove.MouseDown -= RectToMove_MouseDown;
            TabControlBotTab.SelectionChanged -= TabControlBotTab_SelectionChanged;
            TabControlControl.SelectionChanged -= ChartPanels_SelectionChanged;
            TabControlPrime.SelectionChanged -= ChartPanels_SelectionChanged;
            _chartMaster?.StopPaint();
            _chartMaster = null;
            if (_remotePollTimer != null)
            {
                _remotePollTimer.Stop();
                _remotePollTimer = null;
            }
            ClearHosts();
        }

        private void ClearHosts()
        {
            HostGlass.Child = null;
            HostAlert.Child = null;
            HostGrids.Child = null;
            HostOpenPosition.Child = null;
            HostStopLimits.Child = null;
            HostClosePosition.Child = null;
            HostBotLog.Child = null;
            ChartHostPanel.Child = null;
            foreach (DataGridView grid in new[] { _depthGrid, _alertsGrid, _gridsGrid, _openPositionsGrid, _stopLimitsGrid, _closedPositionsGrid, _botLogGrid })
            {
                if (grid != null) grid.Dispose();
            }
            _depthGrid = _alertsGrid = _gridsGrid = _openPositionsGrid = _stopLimitsGrid = _closedPositionsGrid = _botLogGrid = null;
        }

        private void ChartWindow_LocationChanged(object sender, EventArgs e)
        {
            WindowCoordinate.X = Convert.ToDecimal(Left);
            WindowCoordinate.Y = Convert.ToDecimal(Top);
        }

        private void TabControlBotTab_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_startTitle != null) Title = _startTitle;
        }

        private void DepthGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || _depthGrid?.Rows[e.RowIndex].Cells[2].Value == null) return;
            TextBoxPrice.Text = _depthGrid.Rows[e.RowIndex].Cells[2].Value.ToString();
        }

        #region Original BotPanelChartUi layout behavior
        private void CheckPanels()
        {
            string path = @"Engine\LayoutRobotUi" + _layoutName + ".txt";
            if (!File.Exists(path)) return;
            try
            {
                using (StreamReader reader = new StreamReader(path))
                {
                    _settingsPanelIsHide = Convert.ToBoolean(reader.ReadLine());
                    _informPanelIsHide = Convert.ToBoolean(reader.ReadLine());
                    _lowPanelIsBig = Convert.ToBoolean(reader.ReadLine());
                }
                if (_settingsPanelIsHide) HideSettingsPanel();
                if (_informPanelIsHide) HideInformPanel();
                if (!_informPanelIsHide && _lowPanelIsBig) DoBigLowPanel();
            }
            catch { }
        }

        private void SaveLeftPanelPosition()
        {
            try
            {
                using (StreamWriter writer = new StreamWriter(@"Engine\LayoutRobotUi" + _layoutName + ".txt", false))
                {
                    writer.WriteLine(_settingsPanelIsHide);
                    writer.WriteLine(_informPanelIsHide);
                    writer.WriteLine(_lowPanelIsBig);
                }
            }
            catch { }
        }

        private void ButtonHideInformPanel_Click(object sender, RoutedEventArgs e) { HideInformPanel(); SaveLeftPanelPosition(); }
        private void ButtonShowInformPanel_Click(object sender, RoutedEventArgs e) { ShowInformPanel(); SaveLeftPanelPosition(); }
        private void ButtonHideShowSettingsPanel_Click(object sender, RoutedEventArgs e)
        {
            if (ButtonHideShowSettingsPanel.Content.ToString() == ">") HideSettingsPanel(); else ShowSettingsPanel();
            SaveLeftPanelPosition();
        }

        private void HideInformPanel()
        {
            TabControlPrime.Visibility = Visibility.Hidden;
            GridPrime.RowDefinitions[1].Height = new GridLength(0);
            GreedTraderEngine.Margin = new Thickness(0);
            ButtonShowInformPanel.Visibility = Visibility.Visible;
            GreedChartPanel.Margin = GreedTraderEngine.Visibility == Visibility.Visible ? new Thickness(0, 26, 308, 0) : new Thickness(0, 26, 0, 0);
            TabControlBotsName.Margin = GreedTraderEngine.Visibility == Visibility.Visible ? new Thickness(28, 0, 315, 0) : new Thickness(28, 0, 7, 0);
            _informPanelIsHide = true;
        }

        private void ShowInformPanel()
        {
            ButtonShowInformPanel.Visibility = Visibility.Hidden;
            GridPrime.RowDefinitions[1].Height = new GridLength(190);
            GreedTraderEngine.Margin = new Thickness(0, 0, 0, 182);
            GreedPositionLogHost.Height = 167;
            TabControlPrime.Visibility = Visibility.Visible;
            GreedChartPanel.Margin = GreedTraderEngine.Visibility == Visibility.Visible ? new Thickness(0, 26, 308, 10) : new Thickness(0, 26, 0, 10);
            TabControlBotsName.Margin = GreedTraderEngine.Visibility == Visibility.Visible ? new Thickness(28, 0, 315, 0) : new Thickness(28, 0, 7, 0);
            _informPanelIsHide = false;
        }

        private void HideSettingsPanel()
        {
            ButtonHideShowSettingsPanel.Content = "<";
            GreedTraderEngine.Visibility = Visibility.Hidden;
            GreedChartPanel.Margin = TabControlPrime.Visibility == Visibility.Visible ? new Thickness(0, 26, 0, 10) : new Thickness(0, 26, 0, 0);
            TabControlBotsName.Margin = new Thickness(28, 0, 7, 0);
            _settingsPanelIsHide = true;
        }

        private void ShowSettingsPanel()
        {
            ButtonHideShowSettingsPanel.Content = ">";
            GreedTraderEngine.Visibility = Visibility.Visible;
            GreedChartPanel.Margin = TabControlPrime.Visibility == Visibility.Visible ? new Thickness(0, 26, 308, 10) : new Thickness(0, 26, 308, 0);
            TabControlBotsName.Margin = new Thickness(28, 0, 315, 0);
            _settingsPanelIsHide = false;
        }

        private void RectToMove_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GreedPositionLogHost.Cursor == System.Windows.Input.Cursors.ScrollN) { DoBigLowPanel(); _lowPanelIsBig = true; SaveLeftPanelPosition(); }
            else if (GreedPositionLogHost.Cursor == System.Windows.Input.Cursors.ScrollS) { DoSmallLowPanel(); _lowPanelIsBig = false; SaveLeftPanelPosition(); }
        }
        private void RectToMove_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { GreedPositionLogHost.Cursor = System.Windows.Input.Cursors.Arrow; }
        private void RectToMove_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (GridPrime.RowDefinitions[1].Height.Value == 190) GreedPositionLogHost.Cursor = System.Windows.Input.Cursors.ScrollN;
            if (GridPrime.RowDefinitions[1].Height.Value == 500) GreedPositionLogHost.Cursor = System.Windows.Input.Cursors.ScrollS;
        }
        private void DoBigLowPanel()
        {
            GridPrime.RowDefinitions[1].Height = new GridLength(500, GridUnitType.Pixel);
            GreedTraderEngine.Margin = new Thickness(0, 0, 0, 492);
            GreedPositionLogHost.Height = 475;
            MinHeight = 600;
        }
        private void DoSmallLowPanel()
        {
            GridPrime.RowDefinitions[1].Height = new GridLength(190, GridUnitType.Pixel);
            GreedTraderEngine.Margin = new Thickness(0, 0, 0, 182);
            GreedPositionLogHost.Height = 167;
            MinHeight = 300;
        }
        #endregion

        #region Original Lite controls; remote-only operations not yet available are placeholders
        private void ButtonStrategParametr_Click(object sender, RoutedEventArgs e)
        {
            RobotsVpsParametersUi window = new RobotsVpsParametersUi(_client, _botId) { Owner = this };
            window.Show();
            window.Activate();
        }
        private async void buttonBuyFast_Click_1(object sender, RoutedEventArgs e) => await SendChartOrderAsync("BuyAtMarket");
        private async void buttonSellFast_Click(object sender, RoutedEventArgs e) => await SendChartOrderAsync("SellAtMarket");
        private async void ButtonBuyLimit_Click(object sender, RoutedEventArgs e) => await SendChartOrderAsync("BuyAtLimit", true);
        private async void ButtonSellLimit_Click(object sender, RoutedEventArgs e) => await SendChartOrderAsync("SellAtLimit", true);
        private async void ButtonCloseLimit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _client.CallToolAsync("bot_chart_execute_action", new { bot_id = _botId, tab_name = _tabName, action = "CancelOrders" });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void ButtonAddVisualAlert_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void ButtonAddPriceAlert_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void ButtonMoreOpenPositionDetail_Click(object sender, RoutedEventArgs e)
        {
            if (_remotePositionOpenWindow != null && _remotePositionOpenWindow.IsVisible)
            {
                if (_remotePositionOpenWindow.WindowState == WindowState.Minimized)
                    _remotePositionOpenWindow.WindowState = WindowState.Normal;
                _remotePositionOpenWindow.Activate();
                return;
            }

            _remotePositionOpenWindow = new RobotsVpsPositionOpenUi(_client, _botId, _botName, _tabName, _securityName)
            {
                Owner = this
            };
            _remotePositionOpenWindow.Closed += (s, args) => _remotePositionOpenWindow = null;
            _remotePositionOpenWindow.Show();
        }
        private void ButtonStrategManualSettings_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void ButtonJournalCommunity_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void ButtonRiskManager_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void ButtonRedactTab_Click(object sender, RoutedEventArgs e)
        {
            RobotsVpsDataSettingsUi window = new RobotsVpsDataSettingsUi(_client, _botId, _tabName) { Owner = this };
            window.ShowDialog();
        }
        private void ButtonStrategIndividualSettings_Click(object sender, RoutedEventArgs e) => NotAvailableRemotely();
        private void NotAvailableRemotely()
        {
            System.Windows.MessageBox.Show("This remote action is not available yet.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async System.Threading.Tasks.Task SendChartOrderAsync(string action, bool useLimitPrice = false)
        {
            try
            {
                decimal volume = TextBoxVolumeFast.Text.ToDecimal();
                if (volume <= 0)
                {
                    System.Windows.MessageBox.Show(OsLocalization.Trader.Label49, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                object parameters = useLimitPrice
                    ? new { bot_id = _botId, tab_name = _tabName, action, volume, price = TextBoxPrice.Text.ToDecimal() }
                    : new { bot_id = _botId, tab_name = _tabName, action, volume };
                if (useLimitPrice && TextBoxPrice.Text.ToDecimal() == 0)
                {
                    System.Windows.MessageBox.Show(OsLocalization.Trader.Label50, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                await _client.CallToolAsync("bot_chart_execute_action", parameters);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        #endregion
    }
}
