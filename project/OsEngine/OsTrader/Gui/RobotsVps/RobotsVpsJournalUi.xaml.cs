using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Windows.Threading;
using OsEngine.Entity;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote, single-bot copy of Journal/JournalUi2 ("Journal" button in the parent chart window).
    /// Per the project rule: the window's structure (tabs, buttons, filter panel) is copied from the
    /// parent 1:1 — every control that exists there exists here too, under the same name. Each one is
    /// either wired to a real remote equivalent or left an explicit, visible stub (disabled + tooltip,
    /// or a message box on click) — nothing is silently dropped. See the per-member comments below for
    /// which is which; the summary is in D:\ff-research\docs\SERVER_SETUP.md / claude memory robots-vps-ui.
    /// </summary>
    public partial class RobotsVpsJournalUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;

        private Chart _equityChart;
        private Chart _drawdownChart;
        private DataGridView _statisticsGrid;
        private DataGridView _volumeGrid;
        private DataGridView _volumePortfolioGrid;
        private DataGridView _openPositionsGrid;
        private DataGridView _closedPositionsGrid;
        private DataGridView _botsFilterGrid;
        private DataGridView _securitiesFilterGrid;
        private DispatcherTimer _autoReloadTimer;
        private bool _loaded;
        private bool _leftPanelIsHide;
        private bool _showFailedClosed;

        private List<JsonElement> _allOpenPositions = new();
        private List<JsonElement> _allClosedPositions = new();
        private readonly HashSet<string> _securityFilter = new(StringComparer.OrdinalIgnoreCase);
        private int _openPageSize = 100, _closedPageSize = 100;

        public RobotsVpsJournalUi(RemoteMcpClient client, string botId)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            Title = "Journal — " + botId;

            ComboBoxChartType.Items.Add("DepositPercent");
            ComboBoxChartType.Items.Add("Absolute");
            ComboBoxChartType.Items.Add("Percent1Contract");
            ComboBoxChartType.SelectedIndex = 0;

            foreach (string label in new[] { "100 / page", "500 / page", "1000 / page" })
            {
                ComboBoxOpenPosesOnPage.Items.Add(label);
                ComboBoxClosePosesOnPage.Items.Add(label);
            }
            ComboBoxOpenPosesOnPage.SelectedIndex = ComboBoxClosePosesOnPage.SelectedIndex = 0;

            foreach (string label in new[] { "100", "500", "1000" }) VolumeShowNumbers.Items.Add(label);
            VolumeShowNumbers.SelectedIndex = 0;

            Loaded += Window_Loaded;
            Closed += Window_Closed;
        }

        // async void здесь и во всех остальных обработчиках в этом файле НАМЕРЕННО обёрнуты в try/catch
        // целиком: необработанное исключение в async void (в отличие от async Task) не может быть поймано
        // вызывающей стороной и в WPF валит процесс целиком — как и StackOverflowException. Это тот же
        // защитный стиль, что и в остальном коде OsEngine (каждый обработчик там обёрнут в try/catch).
        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _equityChart = CreateChart();
                HostEquity.Child = _equityChart;
                _drawdownChart = CreateChart();
                HostDrawdown.Child = _drawdownChart;
                _statisticsGrid = CreateTwoColumnGrid("Metric", "Value");
                HostStatistics.Child = _statisticsGrid;
                _volumeGrid = CreateVolumeGrid();
                HostVolume.Child = _volumeGrid;
                // "To portfolio": оригинал группирует объём по портфелю коннектора. У наших ботов портфель один
                // и bot_journal_get_volume его не отдаёт (только инструмент/время/объём/плечо) — сервер пока не
                // умеет разбивать по портфелю. Явная заглушка, а не тихая подмена.
                _volumePortfolioGrid = CreateTwoColumnGrid("Portfolio", "Volume");
                HostVolumePortfolio.Child = _volumePortfolioGrid;
                _volumePortfolioGrid.Rows.Add("(not available remotely yet)", "");

                _openPositionsGrid = DataGridFactory.GetDataGridPosition();
                HostOpenPosition.Child = _openPositionsGrid;
                _closedPositionsGrid = DataGridFactory.GetDataGridPosition();
                HostClosePosition.Child = _closedPositionsGrid;

                _botsFilterGrid = CreateCheckGrid("Bot");
                HostBotsSelected.Child = _botsFilterGrid;
                _botsFilterGrid.Rows.Add(true, _botId);
                _botsFilterGrid.Rows[0].Cells[0].ReadOnly = true; // единственный бот — фильтровать нечем, оставлен для структурного соответствия оригиналу
                _securitiesFilterGrid = CreateCheckGrid("Security");
                HostSecuritiesSelected.Child = _securitiesFilterGrid;
                _securitiesFilterGrid.CellValueChanged += SecuritiesFilterGrid_CellValueChanged;

                _loaded = true;
                await ReloadAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        #region Reload / auto-update / left panel (real)

        private async void ButtonReload_Click(object sender, RoutedEventArgs e)
        {
            try { await ReloadAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        private void ButtonAutoReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ButtonAutoReload.IsChecked == true)
                {
                    _autoReloadTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    _autoReloadTimer.Tick -= AutoReloadTimer_Tick;
                    _autoReloadTimer.Tick += AutoReloadTimer_Tick;
                    _autoReloadTimer.Start();
                }
                else
                {
                    _autoReloadTimer?.Stop();
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async void AutoReloadTimer_Tick(object sender, EventArgs e)
        {
            try { await ReloadAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        private void ButtonHideLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            try { SetLeftPanelVisible(false); } catch (Exception ex) { ShowError(ex); }
        }

        private void ButtonShowLeftPanel_Click(object sender, RoutedEventArgs e)
        {
            try { SetLeftPanelVisible(true); } catch (Exception ex) { ShowError(ex); }
        }

        private void SetLeftPanelVisible(bool visible)
        {
            GridActivBots.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            ButtonShowLeftPanel.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
            GridTabPrime.Margin = visible ? new Thickness(510, 0, -0.333, -0.333) : new Thickness(0, 0, -0.333, -0.333);
            _leftPanelIsHide = !visible;
        }

        // Community-инструкции — не относится к данным VPS, тут это просто карточка справки; в оригинале
        // открывает окно с постами сообщества. Явная заглушка.
        private void ButtonPostsJournal2_Click(object sender, RoutedEventArgs e)
        {
            try { System.Windows.MessageBox.Show("Community posts are not available remotely.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        #region Date range (From/To text — real; sliders are a visual stub, see XAML tooltip)

        private (DateTime? from, DateTime? to) ReadDateRange()
        {
            DateTime? from = DateTime.TryParse(TextBoxFrom.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime f) ? f : null;
            DateTime? to = DateTime.TryParse(TextBoxTo.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime t) ? t : null;
            return (from, to);
        }

        private bool InRange(DateTime time, DateTime? from, DateTime? to) =>
            (!from.HasValue || time >= from.Value) && (!to.HasValue || time <= to.Value);

        #endregion

        #region Load + filters

        private async System.Threading.Tasks.Task ReloadAsync()
        {
            if (!_loaded || _client == null || !_client.IsConnected) return;

            try
            {
                _allOpenPositions = await FetchAllPositions("bot_journal_get_open_positions", includeFailed: null);
                _allClosedPositions = await FetchAllPositions("bot_journal_get_closed_positions", includeFailed: _showFailedClosed);
                RefreshSecuritiesFilter();
            }
            catch (Exception ex) { ShowError(ex); return; }

            try { await RefreshEquityAndDrawdownAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshStatisticsAsync(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshVolumeAsync(); } catch (Exception ex) { ShowError(ex); }
            try { RenderOpenPositionsPage(); } catch (Exception ex) { ShowError(ex); }
            try { RenderClosedPositionsPage(); } catch (Exception ex) { ShowError(ex); }
        }

        private async System.Threading.Tasks.Task<List<JsonElement>> FetchAllPositions(string tool, bool? includeFailed)
        {
            Dictionary<string, object> args = new() { ["bot_name"] = _botId, ["limit"] = 5000 };
            if (includeFailed.HasValue) args["include_failed"] = includeFailed.Value;

            JsonElement result = await _client.CallToolAsync(tool, args);
            List<JsonElement> list = new();
            if (result.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in positions.EnumerateArray()) list.Add(p.Clone());
            return list;
        }

        private IEnumerable<JsonElement> FilteredPositions(bool closed)
        {
            (DateTime? from, DateTime? to) = ReadDateRange();
            IEnumerable<JsonElement> source = closed ? _allClosedPositions : _allOpenPositions;

            return source.Where(p =>
            {
                if (_securityFilter.Count > 0 && !_securityFilter.Contains(ReadString(p, "security_name"))) return false;
                DateTime time = ReadTime(p, closed ? "close_time" : "open_time");
                if (time == DateTime.MinValue) time = ReadTime(p, "open_time");
                return InRange(time, from, to);
            });
        }

        private static DataGridView CreateCheckGrid(string nameHeader)
        {
            DataGridView grid = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = 30 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = nameHeader, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            // DataGridViewCheckBoxColumn: клик коммитит значение в ячейку только на потере фокуса ячейки/CommitEdit;
            // без принудительного коммита CellValueChanged (и старый CellContentClick) видят ПРЕДЫДУЩЕЕ состояние
            // галочки — известная особенность WinForms, не ошибка выше по коду.
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            return grid;
        }

        private void RefreshSecuritiesFilter()
        {
            List<string> securities = _allOpenPositions.Concat(_allClosedPositions)
                .Select(p => ReadString(p, "security_name"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s)
                .ToList();

            _securitiesFilterGrid.CellValueChanged -= SecuritiesFilterGrid_CellValueChanged;
            _securitiesFilterGrid.Rows.Clear();
            foreach (string security in securities)
            {
                _securitiesFilterGrid.Rows.Add(!_securityFilter.Contains(security) || _securityFilter.Count == 0, security);
            }
            // до первого снятия галочки фильтр не сужает список (все инструменты видны)
            if (_securityFilter.Count == 0) _securityFilter.UnionWith(securities);
            _securitiesFilterGrid.CellValueChanged += SecuritiesFilterGrid_CellValueChanged;
        }

        private async void SecuritiesFilterGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                string security = _securitiesFilterGrid.Rows[e.RowIndex].Cells[1].Value?.ToString();
                if (string.IsNullOrEmpty(security)) return;
                bool isChecked = _securitiesFilterGrid.Rows[e.RowIndex].Cells[0].Value is bool b && b;

                if (isChecked) _securityFilter.Add(security); else _securityFilter.Remove(security);

                await RefreshEquityAndDrawdownAsync();
                await RefreshVolumeAsync();
                RenderOpenPositionsPage();
                RenderClosedPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        private void ShowError(Exception ex) => Title = "Journal — " + _botId + " / " + ex.Message;

        #region Equity / Drawdown

        // Total-линия и Drawdown — прямо с сервера (bot_journal_get_equity/get_drawdown), диапазон дат
        // и фильтр по инструментам на них НЕ влияют: сервер пока не принимает такие параметры.
        // Long/Short — считаются здесь же из открытых+закрытых позиций (profit_abs), поэтому диапазон
        // дат и фильтр по инструментам на них ДЕЙСТВУЮТ. Это единственная часть окна с таким расхождением;
        // явно проговорено, чтобы не выглядело как непонятное несовпадение цифр.
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

        private async System.Threading.Tasks.Task RefreshEquityAndDrawdownAsync()
        {
            string chartType = ComboBoxChartType.SelectedItem as string ?? "DepositPercent";
            (DateTime? from, DateTime? to) = ReadDateRange();

            JsonElement equity = await _client.CallToolAsync("bot_journal_get_equity", new { bot_name = _botId, chart_type = chartType });
            JsonElement drawdown = await _client.CallToolAsync("bot_journal_get_drawdown", new { bot_name = _botId });

            _equityChart.Series.Clear();
            AddSeries(_equityChart, "Total", equity, "value", Themes.ThemeManager.GetColorWinForms("JournalEquityTotalBrush"), RectangleEquity.Visibility);
            AddLongShortSeries(_equityChart, from, to);

            _drawdownChart.Series.Clear();
            AddSeries(_drawdownChart, "Drawdown", drawdown, "percent", Color.OrangeRed, Visibility.Visible);
        }

        private void AddSeries(Chart chart, string name, JsonElement result, string valueField, Color color, Visibility visibility)
        {
            if (visibility != Visibility.Visible) return;
            if (!result.TryGetProperty("points", out JsonElement points) || points.ValueKind != JsonValueKind.Array) return;

            Series series = new Series(name) { ChartType = SeriesChartType.Line, Color = color, BorderWidth = 2, XValueType = ChartValueType.DateTime };
            foreach (JsonElement p in points.EnumerateArray())
                series.Points.AddXY(ReadTime(p, "time"), (double)ReadDecimal(p, valueField));
            chart.Series.Add(series);
        }

        private void AddLongShortSeries(Chart chart, DateTime? from, DateTime? to)
        {
            void AddSide(string side, string name, Color color, Visibility visibility)
            {
                if (visibility != Visibility.Visible) return;

                List<JsonElement> deals = _allOpenPositions.Concat(_allClosedPositions)
                    .Where(p => string.Equals(ReadString(p, "side"), side, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(ReadString(p, "direction"), side, StringComparison.OrdinalIgnoreCase))
                    .Where(p => _securityFilter.Count == 0 || _securityFilter.Contains(ReadString(p, "security_name")))
                    .OrderBy(p => ReadTime(p, "open_time"))
                    .ToList();

                Series series = new Series(name) { ChartType = SeriesChartType.Line, Color = color, BorderWidth = 2, XValueType = ChartValueType.DateTime };
                decimal cumulative = 0;
                foreach (JsonElement p in deals)
                {
                    DateTime time = ReadTime(p, "close_time");
                    if (time == DateTime.MinValue) time = ReadTime(p, "open_time");
                    if (!InRange(time, from, to)) continue;
                    cumulative += ReadDecimal(p, "profit_abs");
                    series.Points.AddXY(time, (double)cumulative);
                }
                chart.Series.Add(series);
            }

            AddSide("Buy", "Long", Themes.ThemeManager.GetColorWinForms("JournalSwatchLongBrush"), RectangleLong.Visibility);
            AddSide("Sell", "Short", Themes.ThemeManager.GetColorWinForms("JournalSwatchShortBrush"), RectangleShort.Visibility);
        }

        private async void ComboBoxChartType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { await RefreshEquityAndDrawdownAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        private void RectangleEquity_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleEquity);
        private void RectangleLong_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleLong);
        private void RectangleShort_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleShort);

        private async void ToggleSeriesVisibility(System.Windows.Shapes.Rectangle rectangle)
        {
            rectangle.Opacity = rectangle.Opacity < 1 ? 1 : 0.25;
            rectangle.Visibility = Visibility.Visible; // само прямоугольник остаётся, тускнеет как индикатор выкл. серии
            try { await RefreshEquityAndDrawdownAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        // Бенчмарк (сравнение со сторонним индексом BTC/MCFTR/S&P500/IMOEX) — на сервере нет ни котировок
        // бенчмарка, ни соответствующего MCP-инструмента. Явная заглушка (ComboBox/кнопка отключены в XAML).
        private void ButtonRefreshBenchmark_Click(object sender, RoutedEventArgs e) { }

        #endregion

        #region Statistics / Volume tables

        private static DataGridView CreateTwoColumnGrid(string keyHeader, string valueHeader)
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

            (DateTime? from, DateTime? to) = ReadDateRange();
            int maxRows = int.TryParse(VolumeShowNumbers.SelectedItem as string, out int n) ? n : 100;

            int shown = 0;
            foreach (JsonElement p in points.EnumerateArray())
            {
                if (shown >= maxRows) break;
                string security = ReadString(p, "security_name");
                if (_securityFilter.Count > 0 && !_securityFilter.Contains(security)) continue;
                if (!InRange(ReadTime(p, "time"), from, to)) continue;

                _volumeGrid.Rows.Add(security, FormatRemoteTime(ReadString(p, "time")), FormatNumber(ReadDecimal(p, "volume")), FormatNumber(ReadDecimal(p, "leverage")));
                shown++;
            }
        }

        private async void VolumeShowNumbers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { await RefreshVolumeAsync(); } catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        #region Open / Closed positions (real client-side paging over the already-fetched list)

        private void RenderOpenPositionsPage()
        {
            List<JsonElement> filtered = FilteredPositions(closed: false).ToList();
            // ВАЖНО: отвязать обработчик перед Items.Clear()/переустановкой SelectedItem, иначе WPF ComboBox
            // поднимает SelectionChanged на обоих шагах (сброс выбора при Clear и новый выбор после) — это
            // рекурсивно вызывает этот же метод и уходит в StackOverflowException (валит процесс целиком,
            // try/catch не спасает). Ровно так же защищена пагинация в оригинале JournalUi2.xaml.cs.
            ComboBoxOpenPosesShowNumbers.SelectionChanged -= ComboBoxOpenPosesShowNumbers_SelectionChanged;
            PopulatePageCombo(ComboBoxOpenPosesShowNumbers, filtered.Count, _openPageSize);
            ComboBoxOpenPosesShowNumbers.SelectionChanged += ComboBoxOpenPosesShowNumbers_SelectionChanged;
            int page = ParsePageStart(ComboBoxOpenPosesShowNumbers.SelectedItem as string);
            RenderPositionRows(_openPositionsGrid, filtered.Skip(page).Take(_openPageSize), closed: false);
        }

        private void RenderClosedPositionsPage()
        {
            List<JsonElement> filtered = FilteredPositions(closed: true).ToList();
            ComboBoxClosePosesShowNumbers.SelectionChanged -= ComboBoxClosePosesShowNumbers_SelectionChanged;
            PopulatePageCombo(ComboBoxClosePosesShowNumbers, filtered.Count, _closedPageSize);
            ComboBoxClosePosesShowNumbers.SelectionChanged += ComboBoxClosePosesShowNumbers_SelectionChanged;
            int page = ParsePageStart(ComboBoxClosePosesShowNumbers.SelectedItem as string);
            RenderPositionRows(_closedPositionsGrid, filtered.Skip(page).Take(_closedPageSize), closed: true);
        }

        private static void PopulatePageCombo(System.Windows.Controls.ComboBox combo, int totalCount, int pageSize)
        {
            string previous = combo.SelectedItem as string;
            combo.Items.Clear();
            for (int start = 0; start < Math.Max(totalCount, 1); start += pageSize)
                combo.Items.Add(start + " > " + Math.Min(start + pageSize, totalCount));
            if (combo.Items.Count == 0) combo.Items.Add("0 > 0");
            combo.SelectedItem = combo.Items.Contains(previous) ? previous : combo.Items[combo.Items.Count - 1];
        }

        private static int ParsePageStart(string range) =>
            !string.IsNullOrEmpty(range) && int.TryParse(range.Split('>')[0].Trim(), out int start) ? start : 0;

        private void ComboBoxOpenPosesOnPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try
            {
                _openPageSize = ParsePageSize(ComboBoxOpenPosesOnPage.SelectedItem as string);
                RenderOpenPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ComboBoxOpenPosesShowNumbers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { RenderOpenPositionsPage(); } catch (Exception ex) { ShowError(ex); }
        }

        private void ComboBoxClosePosesOnPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try
            {
                _closedPageSize = ParsePageSize(ComboBoxClosePosesOnPage.SelectedItem as string);
                RenderClosedPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ComboBoxClosePosesShowNumbers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { RenderClosedPositionsPage(); } catch (Exception ex) { ShowError(ex); }
        }

        private static int ParsePageSize(string label) =>
            int.TryParse((label ?? "").Split('/')[0].Trim(), out int size) ? size : 100;

        // "показывать неоткрытые позиции" -> include_failed на сервере; требует новой выборки, поэтому
        // сразу перегружаем закрытые позиции целиком (полный, а не постраничный вызов).
        private async void CheckBoxShowDontOpenPoses_Click(object sender, RoutedEventArgs e)
        {
            _showFailedClosed = CheckBoxShowDontOpenPoses.IsChecked == true;
            try
            {
                _allClosedPositions = await FetchAllPositions("bot_journal_get_closed_positions", includeFailed: _showFailedClosed);
                RenderClosedPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void RenderPositionRows(DataGridView grid, IEnumerable<JsonElement> positions, bool closed)
        {
            grid.Rows.Clear();
            foreach (JsonElement p in positions)
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
            _autoReloadTimer?.Stop(); _autoReloadTimer = null;
            _equityChart?.Dispose(); _equityChart = null;
            _drawdownChart?.Dispose(); _drawdownChart = null;
            _statisticsGrid?.Dispose(); _statisticsGrid = null;
            _volumeGrid?.Dispose(); _volumeGrid = null;
            _volumePortfolioGrid?.Dispose(); _volumePortfolioGrid = null;
            _openPositionsGrid?.Dispose(); _openPositionsGrid = null;
            _closedPositionsGrid?.Dispose(); _closedPositionsGrid = null;
            _botsFilterGrid?.Dispose(); _botsFilterGrid = null;
            _securitiesFilterGrid?.Dispose(); _securitiesFilterGrid = null;
        }

        private static DateTime ReadTime(JsonElement e, string prop)
        {
            string s = ReadString(e, prop);
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t : DateTime.MinValue;
        }

        private static string FormatRemoteTime(string raw) =>
            DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) && t != DateTime.MinValue
                ? t.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentCulture) : string.Empty;

        private static string ReadString(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int ReadInt(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetInt32(out int i) ? i : 0;

        private static decimal ReadDecimal(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetDecimal(out decimal d) ? d : 0m;

        private static string FormatNumber(decimal value) => value.ToString("0.########", CultureInfo.CurrentCulture);
    }
}
