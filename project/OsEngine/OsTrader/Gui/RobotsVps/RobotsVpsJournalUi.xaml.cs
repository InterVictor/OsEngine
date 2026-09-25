using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using OsEngine.Entity;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote, single-bot copy of Journal/JournalUi2 ("Journal community" button in the parent chart
    /// window). Scoped to ONE robot (this window is always opened for a specific bot), so the original's
    /// multi-bot/security filter panel and date-range slider are not reproduced — there is nothing to
    /// filter by when only one bot is shown. Equity/Drawdown use a single line series each instead of
    /// the original's long/short breakdown + monthly/yearly histogram bars; that finer detail is not
    /// ported yet. Positions tables reuse the same rendering as the Bots/Chart tabs.
    /// </summary>
    public partial class RobotsVpsJournalUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;

        private Chart _equityChart;
        private Chart _drawdownChart;
        private DataGridView _statisticsGrid;
        private DataGridView _volumeGrid;
        private DataGridView _openPositionsGrid;
        private DataGridView _closedPositionsGrid;
        private bool _loaded;

        public RobotsVpsJournalUi(RemoteMcpClient client, string botId)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            Title = "Journal — " + botId;

            ComboBoxEquityChartType.Items.Add("DepositPercent");
            ComboBoxEquityChartType.Items.Add("Absolute");
            ComboBoxEquityChartType.Items.Add("Percent1Contract");
            ComboBoxEquityChartType.SelectedIndex = 0;

            Loaded += Window_Loaded;
            Closed += Window_Closed;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _equityChart = CreateChart();
            HostEquity.Child = _equityChart;
            _drawdownChart = CreateChart();
            HostDrawdown.Child = _drawdownChart;
            _statisticsGrid = CreateKeyValueGrid("Metric", "Value");
            HostStatistics.Child = _statisticsGrid;
            _volumeGrid = CreateVolumeGrid();
            HostVolume.Child = _volumeGrid;
            _openPositionsGrid = DataGridFactory.GetDataGridPosition();
            HostOpenPositions.Child = _openPositionsGrid;
            _closedPositionsGrid = DataGridFactory.GetDataGridPosition();
            HostClosedPositions.Child = _closedPositionsGrid;

            _loaded = true;
            await RefreshAllAsync();
        }

        private async System.Threading.Tasks.Task RefreshAllAsync()
        {
            if (!_loaded || _client == null || !_client.IsConnected) return;

            try { await RefreshEquityAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshDrawdownAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshStatisticsAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshVolumeAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshPositionsAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        private void ShowError(Exception ex) => Title = "Journal — " + _botId + " / " + ex.Message;

        #region Charts (single line each — see class remarks for what's not reproduced)

        private Chart CreateChart()
        {
            Chart chart = new Chart();
            chart.BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor");
            ChartArea area = new ChartArea("Main");
            area.BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor");
            area.AxisX.LabelStyle.ForeColor = area.AxisY.LabelStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
            area.AxisX.LineColor = area.AxisY.LineColor = Themes.ThemeManager.GetColorWinForms("JournalChartBorderColor");
            area.CursorX.IsUserSelectionEnabled = area.CursorX.IsUserEnabled = true;
            area.AxisX.LabelStyle.Format = "dd.MM HH:mm";
            chart.ChartAreas.Add(area);
            return chart;
        }

        private async System.Threading.Tasks.Task RefreshEquityAsync()
        {
            string chartType = ComboBoxEquityChartType.SelectedItem as string ?? "DepositPercent";
            JsonElement result = await _client.CallToolAsync("bot_journal_get_equity", new { bot_name = _botId, chart_type = chartType });
            FillLineSeries(_equityChart, "Equity", result, "value", Color.DodgerBlue);
        }

        private async System.Threading.Tasks.Task RefreshDrawdownAsync()
        {
            JsonElement result = await _client.CallToolAsync("bot_journal_get_drawdown", new { bot_name = _botId });
            FillLineSeries(_drawdownChart, "Drawdown", result, "percent", Color.OrangeRed);
        }

        private void FillLineSeries(Chart chart, string name, JsonElement result, string valueField, Color color)
        {
            chart.Series.Clear();
            if (!result.TryGetProperty("points", out JsonElement points) || points.ValueKind != JsonValueKind.Array) return;

            Series series = new Series(name) { ChartType = SeriesChartType.Line, Color = color, BorderWidth = 2 };
            series.XValueType = ChartValueType.DateTime;

            foreach (JsonElement p in points.EnumerateArray())
            {
                DateTime time = ReadTime(p, "time");
                decimal value = ReadDecimal(p, valueField);
                series.Points.AddXY(time, (double)value);
            }

            chart.Series.Add(series);
        }

        private async void ComboBoxEquityChartType_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { await RefreshEquityAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        #region Statistics / Volume tables

        private static DataGridView CreateKeyValueGrid(string keyHeader, string valueHeader)
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddColumn(grid, keyHeader, 220);
            AddColumn(grid, valueHeader, 0);
            return grid;
        }

        private static DataGridView CreateVolumeGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddColumn(grid, "Security", 160);
            AddColumn(grid, "Time", 150);
            AddColumn(grid, "Volume", 120);
            AddColumn(grid, "Leverage", 0);
            return grid;
        }

        private static void AddColumn(DataGridView grid, string header, int width)
        {
            DataGridViewTextBoxCell cell = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn { CellTemplate = cell, HeaderText = header, ReadOnly = true };
            column.AutoSizeMode = width > 0 ? DataGridViewAutoSizeColumnMode.None : DataGridViewAutoSizeColumnMode.Fill;
            if (width > 0) column.Width = width;
            grid.Columns.Add(column);
        }

        private async System.Threading.Tasks.Task RefreshStatisticsAsync()
        {
            JsonElement s = await _client.CallToolAsync("bot_journal_get_statistics", new { bot_name = _botId });
            _statisticsGrid.Rows.Clear();
            _statisticsGrid.Rows.Add("Net profit", FormatNumber(ReadDecimal(s, "net_profit")));
            _statisticsGrid.Rows.Add("Net profit %", FormatNumber(ReadDecimal(s, "net_profit_percent")) + " %");
            _statisticsGrid.Rows.Add("Deals count", ReadInt(s, "deals_count").ToString(CultureInfo.CurrentCulture));
            _statisticsGrid.Rows.Add("Average holding time", ReadString(s, "average_holding_time"));
            _statisticsGrid.Rows.Add("Sharpe", FormatNumber(ReadDecimal(s, "sharpe")));
            _statisticsGrid.Rows.Add("Profit factor", FormatNumber(ReadDecimal(s, "profit_factor")));
            _statisticsGrid.Rows.Add("Recovery", FormatNumber(ReadDecimal(s, "recovery")));
            _statisticsGrid.Rows.Add("Profitable deals", ReadInt(s, "profitable_deals").ToString(CultureInfo.CurrentCulture));
            _statisticsGrid.Rows.Add("Losing deals", ReadInt(s, "losing_deals").ToString(CultureInfo.CurrentCulture));
            _statisticsGrid.Rows.Add("Max drawdown %", FormatNumber(ReadDecimal(s, "max_drawdown_percent")) + " %");
            _statisticsGrid.Rows.Add("Commission", FormatNumber(ReadDecimal(s, "commission")));
        }

        private async System.Threading.Tasks.Task RefreshVolumeAsync()
        {
            JsonElement result = await _client.CallToolAsync("bot_journal_get_volume", new { bot_name = _botId });
            _volumeGrid.Rows.Clear();
            if (!result.TryGetProperty("points", out JsonElement points) || points.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement p in points.EnumerateArray())
            {
                _volumeGrid.Rows.Add(
                    ReadString(p, "security_name"),
                    FormatRemoteTime(ReadString(p, "time")),
                    FormatNumber(ReadDecimal(p, "volume")),
                    FormatNumber(ReadDecimal(p, "leverage")));
            }
        }

        #endregion

        #region Positions tables (same layout/columns as Bots/Chart tabs — DataGridFactory.GetDataGridPosition)

        private async System.Threading.Tasks.Task RefreshPositionsAsync()
        {
            JsonElement open = await _client.CallToolAsync("bot_journal_get_open_positions", new { bot_name = _botId, limit = 500 });
            RenderPositionRows(_openPositionsGrid, open, false);

            JsonElement closed = await _client.CallToolAsync("bot_journal_get_closed_positions", new { bot_name = _botId, limit = 500 });
            RenderPositionRows(_closedPositionsGrid, closed, true);
        }

        private void RenderPositionRows(DataGridView grid, JsonElement response, bool closed)
        {
            grid.Rows.Clear();
            if (!response.TryGetProperty("positions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement p in positions.EnumerateArray())
            {
                int rowIndex = grid.Rows.Add();
                DataGridViewRow row = grid.Rows[rowIndex];
                row.Cells[0].Value = ReadInt(p, "number");
                row.Cells[1].Value = FormatRemoteTime(ReadString(p, "open_time"));
                row.Cells[2].Value = closed ? FormatRemoteTime(ReadString(p, "close_time")) : string.Empty;
                row.Cells[3].Value = ReadString(p, "bot_name");
                row.Cells[4].Value = ReadString(p, "security_name");
                row.Cells[5].Value = ReadString(p, "side");
                row.Cells[6].Value = ReadString(p, "state");
                row.Cells[7].Value = FormatNumber(ReadDecimal(p, "volume"));
                row.Cells[8].Value = FormatNumber(ReadDecimal(p, "open_volume"));
                row.Cells[9].Value = FormatNumber(ReadDecimal(p, "wait_volume"));
                row.Cells[10].Value = FormatNumber(ReadDecimal(p, "entry_price"));
                row.Cells[11].Value = FormatNumber(ReadDecimal(p, "close_price"));
                row.Cells[12].Value = FormatNumber(ReadDecimal(p, "profit_abs"));
                row.Cells[13].Value = FormatNumber(ReadDecimal(p, "stop_order_red_line"));
                row.Cells[14].Value = FormatNumber(ReadDecimal(p, "stop_order_price"));
                row.Cells[15].Value = FormatNumber(ReadDecimal(p, "profit_order_red_line"));
                row.Cells[16].Value = FormatNumber(ReadDecimal(p, "profit_order_price"));
                row.Cells[17].Value = ReadString(p, "signal_type_open");
                row.Cells[18].Value = ReadString(p, "signal_type_close");
            }
        }

        #endregion

        private void Window_Closed(object sender, EventArgs e)
        {
            _equityChart?.Dispose(); _equityChart = null;
            _drawdownChart?.Dispose(); _drawdownChart = null;
            _statisticsGrid?.Dispose(); _statisticsGrid = null;
            _volumeGrid?.Dispose(); _volumeGrid = null;
            _openPositionsGrid?.Dispose(); _openPositionsGrid = null;
            _closedPositionsGrid?.Dispose(); _closedPositionsGrid = null;
        }

        private static DateTime ReadTime(JsonElement e, string prop)
        {
            string s = ReadString(e, prop);
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t : DateTime.MinValue;
        }

        private static string FormatRemoteTime(string raw)
        {
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) && t != DateTime.MinValue
                ? t.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture) : string.Empty;
        }

        private static string ReadString(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int ReadInt(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

        private static decimal ReadDecimal(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetDecimal(out decimal d) ? d : 0m;

        private static string FormatNumber(decimal value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    }
}
