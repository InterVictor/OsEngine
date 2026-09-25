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
using OsEngine.Journal;
using OsEngine.Language;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote clone of Journal/JournalUi2 opened in "community" (all-bots) mode — the remote equivalent of
    /// OsTraderMaster.ShowCommunityJournal(), triggered by the "Journal common" (Label747) button on the
    /// service row of the bots table. Window structure (XAML) is 1:1 with RobotsVpsJournalUi/JournalUi2.
    /// Data-fetching (Equity/Drawdown/Volume/Positions/security filter/paging) reuses the exact methods
    /// already proven in RobotsVpsJournalUi, just called without a bot_name filter (server aggregate mode)
    /// and additionally client-side filtered to bots whose on/off checkbox is checked in the left panel —
    /// 1:1 with the original's GetActiveJournals(), which is what drives BotPanelJournal.IsOn there.
    /// The one real gap: Statistics is a single server-computed aggregate (bot_journal_get_statistics with
    /// no bot_name) — it does NOT re-run per the on/off checkboxes here, because there is no MCP endpoint
    /// that accepts an arbitrary bot-name list for that calculation (the original runs PositionStatisticGenerator
    /// locally over the already-filtered position list; that math is not exposed remotely). Flagged with a
    /// visible tooltip on the tab rather than silently mismatched — see Window_Loaded.
    /// </summary>
    public partial class RobotsVpsCommunityJournalUi : Window
    {
        private readonly RemoteMcpClient _client;

        private Chart _equityChart;
        private Chart _drawdownChart;
        private Chart _volumeChart;
        private DataGridView _statisticsGrid;
        private DataGridView _volumePortfolioGrid;
        private DataGridView _openPositionsGrid;
        private DataGridView _closedPositionsGrid;
        private DataGridView _botsFilterGrid;
        private DataGridView _securitiesFilterGrid;
        private DispatcherTimer _autoReloadTimer;
        private bool _loaded;
        private bool _leftPanelIsHide;
        private bool _showFailedClosed;
        private bool _showTotalLine = true, _showLongLine = true, _showShortLine = true;

        private List<JsonElement> _allOpenPositions = new();
        private List<JsonElement> _allClosedPositions = new();
        private readonly HashSet<string> _securityFilter = new(StringComparer.OrdinalIgnoreCase);
        private int _openPageSize = 100, _closedPageSize = 100;

        private sealed class BotEntry
        {
            public string Name = string.Empty;
            public string ClassName = string.Empty;
            public string Group = "none";
            public decimal Mult = 100m;
            public bool IsOn = true;
        }

        private List<BotEntry> _bots = new();
        // Множество включённых (чекбокс "вкл/выкл") ботов — 1:1 с фильтром IsOn в оригинальном
        // GetActiveJournals: позиции ботов, снятых с "вкл", не попадают в общий расчёт.
        private readonly HashSet<string> _enabledBots = new(StringComparer.OrdinalIgnoreCase);
        // Параллельно строкам _botsFilterGrid: бот для строки-бота, null для строки-заголовка группы.
        private readonly List<BotEntry> _rowBot = new();
        // Параллельно строкам _botsFilterGrid: имя группы для строки-заголовка, null для строки-бота.
        private readonly List<string> _rowGroup = new();

        public RobotsVpsCommunityJournalUi(RemoteMcpClient client)
        {
            InitializeComponent();
            _client = client;

            Title = OsLocalization.Journal.TitleJournalUi + " — " + OsLocalization.Trader.Label747;
            Label1.Content = OsLocalization.Journal.Label1;
            Label2.Content = OsLocalization.Journal.Label2;
            Label3.Content = OsLocalization.Journal.Label3;
            TabItem1.Header = OsLocalization.Journal.TabItem1;
            TabItem2.Header = OsLocalization.Journal.TabItem2;
            TabItem3.Header = OsLocalization.Journal.TabItem3;
            TabItem4.Header = OsLocalization.Journal.TabItem4;
            TabItem5.Header = OsLocalization.Journal.TabItem5;
            TabItem6.Header = OsLocalization.Journal.TabItem6;
            LabelFrom.Content = OsLocalization.Journal.Label5;
            LabelTo.Content = OsLocalization.Journal.Label6;
            ButtonReload.Content = OsLocalization.Journal.Label7;
            ButtonAutoReload.Content = OsLocalization.Journal.Label15;
            TabItemBotFilters.Header = OsLocalization.Journal.Label21;
            TabItemSecurityFilters.Header = OsLocalization.Journal.Label22;
            LabelVolumeShowNumbers.Content = OsLocalization.Journal.Label16;
            LabelEqutyCharteType.Content = OsLocalization.Journal.Label8;
            LabelBenchmark.Content = OsLocalization.Journal.Label23;
            TabItemSecurities.Header = OsLocalization.Journal.TabItemSecurities;
            TabItemPortfolio.Header = OsLocalization.Journal.TabItemPortfolio;
            CheckBoxShowDontOpenPoses.Content = OsLocalization.Journal.Label17;

            ComboBoxChartType.Items.Add("DepositPercent");
            ComboBoxChartType.Items.Add("Absolute");
            ComboBoxChartType.Items.Add("Percent1Contract");
            ComboBoxChartType.SelectedIndex = 0;

            foreach (string label in new[] { OsLocalization.Journal.Label18, OsLocalization.Journal.Label19, OsLocalization.Journal.Label20 })
            {
                ComboBoxOpenPosesOnPage.Items.Add(label);
                ComboBoxClosePosesOnPage.Items.Add(label);
            }
            ComboBoxOpenPosesOnPage.SelectedIndex = ComboBoxClosePosesOnPage.SelectedIndex = 0;

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
                _volumeChart = CreateChart();
                HostVolume.Child = _volumeChart;
                _statisticsGrid = CreateStatisticsGrid();
                HostStatistics.Child = _statisticsGrid;
                // Явная заглушка-предупреждение (видимая, не тихая): bot_journal_get_statistics агрегирует ВСЕ
                // боты сервера и не умеет принимать список включённых ботов — см. комментарий класса.
                HostStatistics.ToolTip = "Aggregated across ALL server bots — does not follow the on/off checkboxes in the left panel (no remote endpoint for a filtered aggregate yet).";
                // "To portfolio": оригинал группирует объём по портфелю коннектора. У наших ботов портфель один
                // и bot_journal_get_volume его не отдаёт (только инструмент/время/объём/плечо) — сервер пока не
                // умеет разбивать по портфелю. Явная заглушка, а не тихая подмена.
                _volumePortfolioGrid = CreateTwoColumnGrid("Portfolio", "Volume");
                HostVolumePortfolio.Child = _volumePortfolioGrid;
                AddGridRow(_volumePortfolioGrid, "(not available remotely yet)", "");

                _openPositionsGrid = DataGridFactory.GetDataGridPosition();
                HostOpenPosition.Child = _openPositionsGrid;
                _closedPositionsGrid = DataGridFactory.GetDataGridPosition();
                HostClosePosition.Child = _closedPositionsGrid;

                _botsFilterGrid = CreateBotsGrid();
                HostBotsSelected.Child = _botsFilterGrid;
                _botsFilterGrid.CellEndEdit += BotsFilterGrid_CellEndEdit;
                await RefreshBotsFilterGridAsync();
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

            try { RefreshEquityChart(); } catch (Exception ex) { ShowError(ex); }
            try { RefreshDrawdownChart(); } catch (Exception ex) { ShowError(ex); }
            try { await RefreshStatisticsAsync(); } catch (Exception ex) { ShowError(ex); }
            try { RefreshVolumeChart(); } catch (Exception ex) { ShowError(ex); }
            try { RenderOpenPositionsPage(); } catch (Exception ex) { ShowError(ex); }
            try { RenderClosedPositionsPage(); } catch (Exception ex) { ShowError(ex); }
        }

        // Без bot_name — сервер отдаёт агрегат по ВСЕМ ботам (та же схема ответа, что и для одного бота).
        // Фильтрация по включённым в левой панели ботам — в FilteredPositions*/FilteredPositionsChronological.
        private async System.Threading.Tasks.Task<List<JsonElement>> FetchAllPositions(string tool, bool? includeFailed)
        {
            Dictionary<string, object> args = new() { ["limit"] = 5000 };
            if (includeFailed.HasValue) args["include_failed"] = includeFailed.Value;

            JsonElement result = await _client.CallToolAsync(tool, args);
            List<JsonElement> list = new();
            if (result.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in positions.EnumerateArray()) list.Add(p.Clone());
            return list;
        }

        // Позиции, отфильтрованные по включённым ботам/датам/бумагам, в хронологическом порядке —
        // общий источник для Equity/Volume графиков и обеих таблиц позиций.
        private List<JsonElement> FilteredPositionsChronological()
        {
            (DateTime? from, DateTime? to) = ReadDateRange();

            return _allOpenPositions.Concat(_allClosedPositions)
                .Where(p => _enabledBots.Contains(ReadString(p, "bot_name")))
                .Where(p => _securityFilter.Count == 0 || _securityFilter.Contains(ReadString(p, "security_name")))
                .Where(p => InRange(ReadTime(p, "open_time"), from, to))
                .OrderBy(p => ReadTime(p, "open_time"))
                .ToList();
        }

        private IEnumerable<JsonElement> FilteredPositions(bool closed)
        {
            (DateTime? from, DateTime? to) = ReadDateRange();
            IEnumerable<JsonElement> source = closed ? _allClosedPositions : _allOpenPositions;

            return source.Where(p =>
            {
                if (!_enabledBots.Contains(ReadString(p, "bot_name"))) return false;
                if (_securityFilter.Count > 0 && !_securityFilter.Contains(ReadString(p, "security_name"))) return false;
                DateTime time = ReadTime(p, closed ? "close_time" : "open_time");
                if (time == DateTime.MinValue) time = ReadTime(p, "open_time");
                return InRange(time, from, to);
            });
        }

        // Полная копия колонок оригинального _gridLeftBotsPanel (JournalUi2.xaml.cs, CreateBotsGrid) —
        // тот же набор, что и в RobotsVpsJournalUi (single-bot), переиспользован без изменений.
        private static DataGridView CreateBotsGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.CellSelect, DataGridViewAutoSizeRowsMode.AllCells);
            grid.AllowUserToResizeRows = true;

            DataGridViewComboBoxColumn groupColumn = new DataGridViewComboBoxColumn
            {
                HeaderText = OsLocalization.Journal.Label9,
                Width = 90,
                FlatStyle = FlatStyle.Flat
            };
            groupColumn.DefaultCellStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("GridTextColor");
            grid.Columns.Add(groupColumn);                        // 0: Группа (dropdown, "new" = create group)

            AddColumn(grid, "#", 35);                              // 1: номер
            AddColumn(grid, OsLocalization.Journal.Label10, 110); // 2: Имя
            AddColumn(grid, OsLocalization.Journal.Label11, 110); // 3: Класс

            DataGridViewCheckBoxColumn onOff = new DataGridViewCheckBoxColumn { HeaderText = OsLocalization.Journal.Label12, Width = 55 };
            grid.Columns.Add(onOff);                               // 4: Вкл/выкл (editable)

            AddColumn(grid, "Mult %", 55);                         // 5: Mult % (editable)
            grid.Columns[5].ReadOnly = false;

            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            return grid;
        }

        // Полная многострочная версия левой панели — 1:1 с оригиналом (JournalUi2: GetGroups/GetGroupRowList/
        // GetPanelRowList): строка-заголовок группы (комбобокс только со своим именем, read-only, "вкл/выкл" =
        // состояние первого бота группы) + по строке на каждого бота (комбобокс со всеми группами + "new").
        // Группы строятся по порядку первого появления бота в bot_get_list — как оригинал строит их из
        // PanelsArray (см. GetGroups). Комбобоксы заполняются Items ДО того, как ячейка попадёт в грид
        // (как в оригинале) — иначе Value может не совпасть с ещё пустым списком Items.
        private async System.Threading.Tasks.Task RefreshBotsFilterGridAsync()
        {
            List<BotEntry> bots = new();

            try
            {
                JsonElement botsList = await _client.CallToolAsync("bot_get_list", new { });
                if (botsList.TryGetProperty("bots", out JsonElement botsArray) && botsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement b in botsArray.EnumerateArray())
                    {
                        string name = ReadString(b, "name");
                        if (string.IsNullOrEmpty(name)) continue;
                        bots.Add(new BotEntry { Name = name, ClassName = ReadString(b, "class_name") });
                    }
                }
            }
            catch (Exception ex) { ShowError(ex); }

            try
            {
                JsonElement settings = await _client.CallToolAsync("bot_journal_get_settings", new { });
                if (settings.TryGetProperty("robots", out JsonElement robots) && robots.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement r in robots.EnumerateArray())
                    {
                        string name = ReadString(r, "bot_name");
                        BotEntry entry = bots.Find(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (entry == null) continue;

                        string g = ReadString(r, "group");
                        entry.Group = string.IsNullOrEmpty(g) ? "none" : g;
                        decimal mult = ReadDecimal(r, "mult");
                        entry.Mult = mult == 0 ? 100m : mult;
                        entry.IsOn = r.TryGetProperty("is_on", out JsonElement onEl) && onEl.ValueKind == JsonValueKind.True;
                    }
                }
            }
            catch (Exception ex) { ShowError(ex); }

            _bots = bots;
            _enabledBots.Clear();
            foreach (BotEntry b in _bots) if (b.IsOn) _enabledBots.Add(b.Name);

            List<string> groupOrder = new();
            Dictionary<string, List<BotEntry>> groups = new(StringComparer.OrdinalIgnoreCase);
            foreach (BotEntry b in _bots)
            {
                if (!groups.TryGetValue(b.Group, out List<BotEntry> list))
                {
                    list = new List<BotEntry>();
                    groups[b.Group] = list;
                    groupOrder.Add(b.Group);
                }
                list.Add(b);
            }

            _botsFilterGrid.CellEndEdit -= BotsFilterGrid_CellEndEdit;
            _botsFilterGrid.Rows.Clear();
            _rowBot.Clear();
            _rowGroup.Clear();

            foreach (string groupName in groupOrder)
            {
                List<BotEntry> members = groups[groupName];

                DataGridViewRow headerRow = new DataGridViewRow();
                DataGridViewComboBoxCell headerCombo = new DataGridViewComboBoxCell();
                headerCombo.Items.Add(groupName);
                headerCombo.Value = groupName;
                headerCombo.ReadOnly = true;
                headerRow.Cells.Add(headerCombo);
                headerRow.Cells.Add(new DataGridViewTextBoxCell { Value = "", ReadOnly = true });
                headerRow.Cells.Add(new DataGridViewTextBoxCell { Value = "", ReadOnly = true });
                headerRow.Cells.Add(new DataGridViewTextBoxCell { Value = "", ReadOnly = true });
                headerRow.Cells.Add(new DataGridViewCheckBoxCell { Value = members[0].IsOn });
                headerRow.Cells.Add(new DataGridViewTextBoxCell { Value = "", ReadOnly = true });
                _botsFilterGrid.Rows.Add(headerRow);
                _rowBot.Add(null);
                _rowGroup.Add(groupName);

                for (int i = 0; i < members.Count; i++)
                {
                    BotEntry bot = members[i];

                    DataGridViewRow row = new DataGridViewRow();
                    DataGridViewComboBoxCell combo = new DataGridViewComboBoxCell();
                    foreach (string g in groupOrder) combo.Items.Add(g);
                    combo.Items.Add("new");
                    combo.Value = bot.Group;
                    row.Cells.Add(combo);
                    row.Cells.Add(new DataGridViewTextBoxCell { Value = i + 1, ReadOnly = true });
                    row.Cells.Add(new DataGridViewTextBoxCell { Value = bot.Name, ReadOnly = true });
                    row.Cells.Add(new DataGridViewTextBoxCell { Value = bot.ClassName, ReadOnly = true });
                    row.Cells.Add(new DataGridViewCheckBoxCell { Value = bot.IsOn });
                    row.Cells.Add(new DataGridViewTextBoxCell { Value = FormatNumber(bot.Mult) });
                    _botsFilterGrid.Rows.Add(row);
                    _rowBot.Add(bot);
                    _rowGroup.Add(null);
                }
            }

            _botsFilterGrid.CellEndEdit += BotsFilterGrid_CellEndEdit;
        }

        private async void BotsFilterGrid_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.RowIndex < 0 || e.RowIndex >= _rowBot.Count) return;
                if (e.ColumnIndex != 0 && e.ColumnIndex != 4 && e.ColumnIndex != 5) return;

                DataGridViewRow row = _botsFilterGrid.Rows[e.RowIndex];
                BotEntry bot = _rowBot[e.RowIndex];
                string groupHeader = _rowGroup[e.RowIndex];

                if (e.ColumnIndex == 0)
                {
                    // Комбобокс строки-заголовка группы read-only — сюда редактирование не попадёт.
                    if (bot == null) return;

                    string selected = row.Cells[0].Value?.ToString() ?? string.Empty;

                    if (selected == "new")
                    {
                        // 1:1 с JournalUi2.ChangeGroup: выбор "new" открывает тот же диалог создания группы,
                        // переиспользуем класс вместо дублирования (как и в RobotsVpsJournalUi).
                        List<string> existingGroups = _bots.Select(b => b.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        NewGroupAddInJournalUi ui = new NewGroupAddInJournalUi(existingGroups);
                        ui.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                        ui.ShowDialog();

                        if (!ui.IsAccepted || string.IsNullOrEmpty(ui.NewGroupName))
                        {
                            await RefreshBotsFilterGridAsync();
                            return;
                        }

                        selected = ui.NewGroupName;
                    }

                    await _client.CallToolAsync("bot_journal_set_settings", new
                    {
                        settings = new[] { new { bot_name = bot.Name, group = selected, mult = bot.Mult, is_on = bot.IsOn } }
                    });

                    await RefreshBotsFilterGridAsync();
                    await ReloadAsync();
                    return;
                }

                if (e.ColumnIndex == 4)
                {
                    bool newState = row.Cells[4].Value is bool b && b;

                    if (bot != null)
                    {
                        await _client.CallToolAsync("bot_journal_set_settings", new
                        {
                            settings = new[] { new { bot_name = bot.Name, group = bot.Group, mult = bot.Mult, is_on = newState } }
                        });
                    }
                    else if (groupHeader != null)
                    {
                        // 1:1 с ChangeOnOffByGroup: чекбокс заголовка группы переключает ВСЕХ ботов группы
                        // в новое (только что выставленное в самом заголовке) состояние.
                        List<BotEntry> members = _bots.Where(x => string.Equals(x.Group, groupHeader, StringComparison.OrdinalIgnoreCase)).ToList();
                        object[] settings = members.Select(m => (object)new { bot_name = m.Name, group = m.Group, mult = m.Mult, is_on = newState }).ToArray();
                        await _client.CallToolAsync("bot_journal_set_settings", new { settings });
                    }

                    await RefreshBotsFilterGridAsync();
                    await ReloadAsync();
                    return;
                }

                if (e.ColumnIndex == 5)
                {
                    // Мультипликатор — только у строк-ботов; у заголовка группы ячейка всегда пустая (как в оригинале).
                    if (bot == null) return;

                    decimal mult = decimal.TryParse(row.Cells[5].Value?.ToString(), NumberStyles.Any, CultureInfo.CurrentCulture, out decimal m) ? m : 100m;

                    await _client.CallToolAsync("bot_journal_set_settings", new
                    {
                        settings = new[] { new { bot_name = bot.Name, group = bot.Group, mult, is_on = bot.IsOn } }
                    });

                    // Mult влияет на кумулятивные суммы Equity/Drawdown/Statistics — перечитываем данные
                    await ReloadAsync();
                }
            }
            catch (Exception ex) { ShowError(ex); }
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
            grid.Columns[1].DefaultCellStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("GridTextColor");
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

        private void SecuritiesFilterGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            try
            {
                if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
                string security = _securitiesFilterGrid.Rows[e.RowIndex].Cells[1].Value?.ToString();
                if (string.IsNullOrEmpty(security)) return;
                bool isChecked = _securitiesFilterGrid.Rows[e.RowIndex].Cells[0].Value is bool b && b;

                if (isChecked) _securityFilter.Add(security); else _securityFilter.Remove(security);

                RefreshEquityChart();
                RefreshVolumeChart();
                RenderOpenPositionsPage();
                RenderClosedPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        private void ShowError(Exception ex) => Title = OsLocalization.Journal.TitleJournalUi + " — " + OsLocalization.Trader.Label747 + " / " + ex.Message;

        #region Equity (line + always-on per-trade bar chart + monthly/yearly bars once there is enough data)

        // Total/Long/Short и вся структура областей строятся здесь же из отфильтрованных позиций (не с сервера) —
        // это необходимо, чтобы линия, помесячная/годовая гистограмма и таблицы согласованно реагировали на
        // фильтр по включённым ботам/датам/бумагам. Формула количества и высоты областей — 1:1 с GetChartParameters
        // в оригинале JournalUi2.xaml.cs: <=30 дней данных -> 2 области (линия+сделки), <1 года -> +помесячная,
        // >=1 года -> +годовая. Идентично RobotsVpsJournalUi.
        private Chart CreateChart()
        {
            Chart chart = new Chart();
            chart.BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor");
            return chart;
        }

        private void RefreshEquityChart()
        {
            List<JsonElement> deals = FilteredPositionsChronological();
            string chartType = ComboBoxChartType.SelectedItem as string ?? "DepositPercent";

            _equityChart.Series.Clear();
            _equityChart.ChartAreas.Clear();
            // WindowsFormsHost не всегда перерисовывает уже размещённый WinForms-контрол сам по себе, когда
            // его содержимое ОПУСТЕЛО (Series/ChartAreas.Clear() без единой новой серии) — тот же трюк, что
            // и в оригинале для таблиц позиций (HostOpenPosition.Child = null; ...; = _openPositionGrid;),
            // иначе при отключении всех ботов (например галочкой у группы) старый график остаётся на экране.
            HostEquity.Child = null;
            HostEquity.Child = _equityChart;
            if (deals.Count == 0) return;

            DateTime minDate = deals.Min(p => ReadTime(p, "open_time"));
            DateTime maxDate = deals.Max(p => ReadTime(p, "open_time"));
            double totalDays = (maxDate - minDate).TotalDays;
            double totalYears = totalDays / 365.25;

            bool withMonthly = totalDays > 30;
            bool withYearly = totalYears >= 1;

            List<(float Height, float Y)> layout = !withMonthly
                ? new List<(float, float)> { (70f, 0f), (30f, 70f) }
                : !withYearly
                    ? new List<(float, float)> { (50f, 0f), (25f, 50f), (25f, 75f) }
                    : new List<(float, float)> { (40f, 0f), (20f, 40f), (20f, 60f), (20f, 80f) };

            ChartArea AddArea(string name, int index, bool axisXEnabled)
            {
                // 1:1 с оригиналом (JournalUi2.xaml.cs, PaintProfitOnChart): шкала — справа (Secondary/Y2),
                // все Equity-серии заведены на AxisType.Secondary, Y2 никак не отключается и не трогается отдельно.
                ChartArea area = new ChartArea(name) { Position = { Height = layout[index].Height, Width = 100, Y = layout[index].Y } };
                if (index > 0) area.AlignWithChartArea = "ChartAreaProfit";
                if (!axisXEnabled) area.AxisX.Enabled = AxisEnabled.False;
                area.CursorX.IsUserEnabled = true;
                area.BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor");
                area.BorderColor = Themes.ThemeManager.GetColorWinForms("JournalChartBorderColor");
                area.CursorX.LineColor = Themes.ThemeManager.GetColorWinForms("JournalChartCursorXColor");
                area.CursorY.LineColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                area.AxisX.TitleForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                area.AxisY.TitleForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                foreach (Axis axis in area.Axes)
                {
                    axis.LabelStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                    axis.LabelStyle.Font = Themes.ThemeManager.GetChartAxisFont();
                }
                _equityChart.ChartAreas.Add(area);
                return area;
            }

            AddArea("ChartAreaProfit", 0, axisXEnabled: true);
            AddArea("ChartAreaProfitBar", 1, axisXEnabled: false);
            if (withMonthly) AddArea("ChartAreaMonthlyBar", 2, axisXEnabled: false);
            if (withYearly) AddArea("ChartAreaYearlyBar", 3, axisXEnabled: false);

            // Тип линии влияет на то, какое поле берём под капотом и совпадает 1:1 с серверным
            // GetPositionProfitForChartType (RobotsApi.cs): Absolute -> profit_abs (ProfitPortfolioAbs),
            // Percent1Contract -> profit_operation_percent (ProfitOperationPercent),
            // DepositPercent -> profit_percent (ProfitPortfolioPercent). Масштабирование MultToJournal
            // тоже берём из позиции (как это делает сервер в GetJournalEquity/GetJournalDrawdown).
            decimal ProfitOf(JsonElement p)
            {
                decimal raw;
                if (string.Equals(chartType, "Percent1Contract", StringComparison.OrdinalIgnoreCase))
                    raw = ReadDecimal(p, "profit_operation_percent");
                else if (string.Equals(chartType, "DepositPercent", StringComparison.OrdinalIgnoreCase))
                    raw = ReadDecimal(p, "profit_percent");
                else
                    raw = ReadDecimal(p, "profit_abs");

                decimal mult = ReadDecimal(p, "mult_to_journal");
                if (mult == 0) mult = 100m;
                return raw * (mult / 100m);
            }

            Series total = new Series("Total") { ChartType = SeriesChartType.Line, ChartArea = "ChartAreaProfit", BorderWidth = 4, Color = Themes.ThemeManager.GetColorWinForms("JournalEquityTotalBrush"), YAxisType = AxisType.Secondary };
            Series longLine = new Series("Long") { ChartType = SeriesChartType.Line, ChartArea = "ChartAreaProfit", BorderWidth = 2, Color = Themes.ThemeManager.GetColorWinForms("JournalSwatchLongBrush"), YAxisType = AxisType.Secondary };
            Series shortLine = new Series("Short") { ChartType = SeriesChartType.Line, ChartArea = "ChartAreaProfit", BorderWidth = 2, Color = Themes.ThemeManager.GetColorWinForms("JournalSwatchShortBrush"), YAxisType = AxisType.Secondary };
            Series bar = new Series("PerTrade") { ChartType = SeriesChartType.Column, ChartArea = "ChartAreaProfitBar", YAxisType = AxisType.Secondary };
            Series monthlyBar = withMonthly ? new Series("Monthly") { ChartType = SeriesChartType.Column, ChartArea = "ChartAreaMonthlyBar", YAxisType = AxisType.Secondary } : null;
            Series yearlyBar = withYearly ? new Series("Yearly") { ChartType = SeriesChartType.Column, ChartArea = "ChartAreaYearlyBar", YAxisType = AxisType.Secondary } : null;

            Dictionary<DateTime, decimal> monthlySums = withMonthly
                ? deals.GroupBy(p => new DateTime(ReadTime(p, "open_time").Year, ReadTime(p, "open_time").Month, 1))
                       .ToDictionary(g => g.Key, g => g.Sum(ProfitOf))
                : null;
            Dictionary<int, decimal> yearlySums = withYearly
                ? deals.GroupBy(p => ReadTime(p, "open_time").Year).ToDictionary(g => g.Key, g => g.Sum(ProfitOf))
                : null;

            decimal cumulative = 0, cumulativeLong = 0, cumulativeShort = 0;

            for (int i = 0; i < deals.Count; i++)
            {
                JsonElement p = deals[i];
                decimal profit = ProfitOf(p);
                string side = ReadString(p, "side");
                string time = FormatRemoteTime(ReadString(p, "open_time"));

                cumulative += profit;
                if (string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase)) cumulativeLong += profit;
                else if (string.Equals(side, "Sell", StringComparison.OrdinalIgnoreCase)) cumulativeShort += profit;

                total.Points.AddXY(i, (double)Math.Round(cumulative, 3));
                total.Points[^1].AxisLabel = time;
                longLine.Points.AddXY(i, (double)Math.Round(cumulativeLong, 3));
                shortLine.Points.AddXY(i, (double)Math.Round(cumulativeShort, 3));

                bar.Points.AddXY(i, (double)Math.Round(profit, 3));
                bar.Points[^1].Color = ReadString(p, "state") != "Done"
                    ? Themes.ThemeManager.GetColorWinForms("JournalBarZeroColor")
                    : profit > 0 ? Themes.ThemeManager.GetColorWinForms("JournalBarPlusColor") : Themes.ThemeManager.GetColorWinForms("ChartBarMinusColor");
                bar.Points[^1].AxisLabel = ReadString(p, "security_name") + "\n" + Math.Round(profit, 3);

                if (monthlyBar != null)
                {
                    DateTime key = new DateTime(ReadTime(p, "open_time").Year, ReadTime(p, "open_time").Month, 1);
                    decimal sum = monthlySums[key];
                    monthlyBar.Points.AddXY(i, (double)Math.Round(sum, 3));
                    monthlyBar.Points[^1].Color = sum >= 0 ? Themes.ThemeManager.GetColorWinForms("JournalBarPlusColor") : Themes.ThemeManager.GetColorWinForms("ChartBarMinusColor");
                    monthlyBar.Points[^1].AxisLabel = key.ToString("MMM yyyy", CultureInfo.CurrentCulture) + "\n" + Math.Round(sum, 2);
                }

                if (yearlyBar != null)
                {
                    int key = ReadTime(p, "open_time").Year;
                    decimal sum = yearlySums[key];
                    yearlyBar.Points.AddXY(i, (double)Math.Round(sum, 3));
                    yearlyBar.Points[^1].Color = sum >= 0 ? Themes.ThemeManager.GetColorWinForms("JournalBarPlusColor") : Themes.ThemeManager.GetColorWinForms("ChartBarMinusColor");
                    yearlyBar.Points[^1].AxisLabel = key + "\n" + Math.Round(sum, 2);
                }
            }

            if (_showTotalLine) _equityChart.Series.Add(total);
            if (_showLongLine) _equityChart.Series.Add(longLine);
            if (_showShortLine) _equityChart.Series.Add(shortLine);
            _equityChart.Series.Add(bar);
            if (monthlyBar != null) _equityChart.Series.Add(monthlyBar);
            if (yearlyBar != null) _equityChart.Series.Add(yearlyBar);
        }

        private async void ComboBoxChartType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { RefreshEquityChart(); } catch (Exception ex) { ShowError(ex); }
            await System.Threading.Tasks.Task.CompletedTask;
        }

        private void RectangleEquity_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleEquity, ref _showTotalLine);
        private void RectangleLong_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleLong, ref _showLongLine);
        private void RectangleShort_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => ToggleSeriesVisibility(RectangleShort, ref _showShortLine);

        private void ToggleSeriesVisibility(System.Windows.Shapes.Rectangle rectangle, ref bool flag)
        {
            try
            {
                flag = !flag;
                rectangle.Opacity = flag ? 1 : 0.25;
                RefreshEquityChart();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        // Бенчмарк (сравнение со сторонним индексом BTC/MCFTR/S&P500/IMOEX) — на сервере нет ни котировок
        // бенчмарка, ни соответствующего MCP-инструмента. Явная заглушка (ComboBox/кнопка отключены в XAML).
        private void ButtonRefreshBenchmark_Click(object sender, RoutedEventArgs e) { }

        #endregion

        #region Drawdown (два графика: абсолют сверху, проценты снизу — как в оригинале)

        private void RefreshDrawdownChart()
        {
            List<JsonElement> deals = FilteredPositionsChronological();

            _drawdownChart.Series.Clear();
            _drawdownChart.ChartAreas.Clear();
            // См. комментарий в RefreshEquityChart — та же WindowsFormsHost-перерисовка при опустевших данных.
            HostDrawdown.Child = null;
            HostDrawdown.Child = _drawdownChart;
            if (deals.Count == 0) return;

            ChartArea absoluteArea = new ChartArea("ChartAreaDdPunct") { Position = { Height = 50, Width = 100, Y = 0 } };
            absoluteArea.AxisY2.Title = OsLocalization.Journal.Label25;
            absoluteArea.AxisY2.TitleForeColor = Themes.ThemeManager.GetColorWinForms("ChartEquityColor");
            _drawdownChart.ChartAreas.Add(absoluteArea);

            ChartArea percentArea = new ChartArea("ChartAreaDdPercent") { Position = { Height = 50, Width = 100, Y = 50 }, AlignWithChartArea = "ChartAreaDdPunct" };
            percentArea.AxisX.Enabled = AxisEnabled.False;
            percentArea.AxisY2.Title = OsLocalization.Journal.Label24;
            percentArea.AxisY2.TitleForeColor = Themes.ThemeManager.GetColorWinForms("JournalShortColor");
            _drawdownChart.ChartAreas.Add(percentArea);

            foreach (ChartArea area in _drawdownChart.ChartAreas)
            {
                area.CursorX.IsUserEnabled = true;
                area.BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor");
                area.BorderColor = Themes.ThemeManager.GetColorWinForms("JournalChartBorderColor");
                area.CursorX.LineColor = Themes.ThemeManager.GetColorWinForms("JournalChartCursorXColor");
                area.CursorY.LineColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                foreach (Axis axis in area.Axes)
                {
                    axis.TitleForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                    axis.LabelStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                }
            }

            // YAxisType=Secondary — как в оригинале (drowDownPunct/drowDownPercent там же заведены на Secondary):
            // без этого AxisY2.Title (вертикальная надпись АБСОЛЮТ/ПРОЦЕНТ) и сама правая шкала не отрисовываются,
            // т.к. Y2 показывается только когда на него ссылается хотя бы одна серия.
            Series absolute = new Series("Absolute") { ChartType = SeriesChartType.Line, ChartArea = "ChartAreaDdPunct", BorderWidth = 2, Color = Themes.ThemeManager.GetColorWinForms("ChartEquityColor"), YAxisType = AxisType.Secondary };
            Series percent = new Series("Percent") { ChartType = SeriesChartType.Line, ChartArea = "ChartAreaDdPercent", BorderWidth = 2, Color = Themes.ThemeManager.GetColorWinForms("JournalShortColor"), YAxisType = AxisType.Secondary };

            decimal cumulative = 0, maxEquity = 0;
            for (int i = 0; i < deals.Count; i++)
            {
                cumulative += ReadDecimal(deals[i], "profit_abs");
                if (cumulative > maxEquity) maxEquity = cumulative;
                decimal dd = cumulative - maxEquity;
                decimal ddPercent = maxEquity != 0 ? Math.Round(dd / maxEquity * 100, 6) : 0;

                string time = FormatRemoteTime(ReadString(deals[i], "open_time"));
                absolute.Points.AddXY(i, (double)dd);
                absolute.Points[^1].AxisLabel = time;
                percent.Points.AddXY(i, (double)ddPercent);
            }

            _drawdownChart.Series.Add(absolute);
            _drawdownChart.Series.Add(percent);
        }

        #endregion

        #region Statistics (31-строчная таблица All/Long/Short — та же структура, что в оригинале)

        private static DataGridView CreateStatisticsGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.None);
            grid.AllowUserToResizeRows = false;
            AddColumn(grid, "", 0);
            AddColumn(grid, OsLocalization.Journal.GridColumn1, 0);
            AddColumn(grid, OsLocalization.Journal.GridColumn2, 0);
            AddColumn(grid, OsLocalization.Journal.GridColumn3, 0);
            for (int i = 0; i < 31; i++) grid.Rows.Add();

            // Ключевая причина, по которой оригинал читаем, а первая версия этого окна — нет: каждой ячейке
            // нужен ЯВНЫЙ цвет текста темы. Без этого WinForms рисует чёрным по тёмному фону — не видно.
            Color textColor = Themes.ThemeManager.GetColorWinForms("GridTextColor");
            foreach (DataGridViewRow row in grid.Rows.Cast<DataGridViewRow>())
                foreach (DataGridViewCell cell in row.Cells.Cast<DataGridViewCell>())
                    cell.Style.ForeColor = textColor;

            string[] labels = new string[31];
            labels[0] = OsLocalization.Journal.GridRow1;
            labels[1] = OsLocalization.Journal.GridRow2;
            labels[2] = OsLocalization.Journal.GridRow3;
            labels[3] = OsLocalization.Journal.GridRow17;
            labels[4] = OsLocalization.Journal.GridRow18;
            labels[5] = OsLocalization.Journal.GridRow4;
            labels[6] = OsLocalization.Journal.GridRow5;
            labels[8] = OsLocalization.Journal.GridRow6;
            labels[9] = OsLocalization.Journal.GridRow7;
            labels[10] = OsLocalization.Journal.GridRow8;
            labels[11] = OsLocalization.Journal.GridRow9;
            labels[13] = OsLocalization.Journal.GridRow10;
            labels[14] = OsLocalization.Journal.GridRow11;
            labels[15] = OsLocalization.Journal.GridRow6;
            labels[16] = OsLocalization.Journal.GridRow7;
            labels[17] = OsLocalization.Journal.GridRow8;
            labels[18] = OsLocalization.Journal.GridRow9;
            labels[19] = OsLocalization.Journal.GridRow12;
            labels[21] = OsLocalization.Journal.GridRow13;
            labels[22] = OsLocalization.Journal.GridRow14;
            labels[23] = OsLocalization.Journal.GridRow6;
            labels[24] = OsLocalization.Journal.GridRow7;
            labels[25] = OsLocalization.Journal.GridRow8;
            labels[26] = OsLocalization.Journal.GridRow9;
            labels[27] = OsLocalization.Journal.GridRow12;
            labels[29] = OsLocalization.Journal.GridRow15;
            labels[30] = OsLocalization.Journal.GridRow16;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != null) grid.Rows[i].Cells[0].Value = labels[i];

            return grid;
        }

        // Без bot_name — сервер считает статистику по ВСЕМ ботам сразу (единственный доступный remote-режим
        // агрегата; см. комментарий класса про ограничение с чекбоксами "вкл/выкл").
        private async System.Threading.Tasks.Task RefreshStatisticsAsync()
        {
            JsonElement all = await _client.CallToolAsync("bot_journal_get_statistics", new { side = "All" });
            JsonElement longSide = await _client.CallToolAsync("bot_journal_get_statistics", new { side = "Long" });
            JsonElement shortSide = await _client.CallToolAsync("bot_journal_get_statistics", new { side = "Short" });

            void SetRow(int row, Func<JsonElement, string> format)
            {
                _statisticsGrid.Rows[row].Cells[1].Value = format(all);
                _statisticsGrid.Rows[row].Cells[2].Value = format(longSide);
                _statisticsGrid.Rows[row].Cells[3].Value = format(shortSide);
            }

            SetRow(0, s => FormatNumber(ReadDecimal(s, "net_profit")));
            SetRow(1, s => FormatNumber(ReadDecimal(s, "net_profit_percent")) + " %");
            SetRow(2, s => ReadInt(s, "deals_count").ToString(CultureInfo.CurrentCulture));
            SetRow(3, s => ReadString(s, "average_holding_time"));
            SetRow(4, s => FormatNumber(ReadDecimal(s, "sharpe")));
            SetRow(5, s => FormatNumber(ReadDecimal(s, "profit_factor")));
            SetRow(6, s => FormatNumber(ReadDecimal(s, "recovery")));
            SetRow(13, s => ReadInt(s, "profitable_deals").ToString(CultureInfo.CurrentCulture));
            SetRow(14, s => ReadInt(s, "deals_count") > 0 ? FormatNumber(100m * ReadInt(s, "profitable_deals") / ReadInt(s, "deals_count")) + " %" : "0 %");
            SetRow(21, s => ReadInt(s, "losing_deals").ToString(CultureInfo.CurrentCulture));
            SetRow(22, s => ReadInt(s, "deals_count") > 0 ? FormatNumber(100m * ReadInt(s, "losing_deals") / ReadInt(s, "deals_count")) + " %" : "0 %");
            SetRow(29, s => FormatNumber(ReadDecimal(s, "max_drawdown_percent")) + " %");
            SetRow(30, s => FormatNumber(ReadDecimal(s, "commission")));

            SetRow(8, s => FormatNumber(ReadDecimal(s, "avg_profit_abs_1_contract")));
            SetRow(9, s => FormatNumber(ReadDecimal(s, "avg_profit_percent_1_contract")) + " %");
            SetRow(10, s => FormatNumber(ReadDecimal(s, "avg_profit_abs_to_deposit")));
            SetRow(11, s => FormatNumber(ReadDecimal(s, "avg_profit_percent_to_deposit")) + " %");
            SetRow(15, s => FormatNumber(ReadDecimal(s, "avg_profit_abs_winning")));
            SetRow(16, s => FormatNumber(ReadDecimal(s, "avg_profit_percent_winning")) + " %");
            SetRow(17, s => FormatNumber(ReadDecimal(s, "avg_profit_abs_winning_to_deposit")));
            SetRow(18, s => FormatNumber(ReadDecimal(s, "avg_profit_percent_winning_to_deposit")) + " %");
            SetRow(19, s => ReadInt(s, "max_win_streak").ToString(CultureInfo.CurrentCulture));
            SetRow(23, s => FormatNumber(ReadDecimal(s, "avg_loss_abs")));
            SetRow(24, s => FormatNumber(ReadDecimal(s, "avg_loss_percent")) + " %");
            SetRow(25, s => FormatNumber(ReadDecimal(s, "avg_loss_abs_to_deposit")));
            SetRow(26, s => FormatNumber(ReadDecimal(s, "avg_loss_percent_to_deposit")) + " %");
            SetRow(27, s => ReadInt(s, "max_loss_streak").ToString(CultureInfo.CurrentCulture));
        }

        #endregion

        #region Volume (по графику на каждый инструмент — как в оригинале, а не единый Long/Short)

        private void RefreshVolumeChart()
        {
            List<JsonElement> deals = FilteredPositionsChronological();

            _volumeChart.Series.Clear();
            _volumeChart.ChartAreas.Clear();
            // См. комментарий в RefreshEquityChart — та же WindowsFormsHost-перерисовка при опустевших данных.
            HostVolume.Child = null;
            HostVolume.Child = _volumeChart;
            if (deals.Count == 0) { VolumeShowNumbers.Items.Clear(); return; }

            List<string> securities = deals.Select(p => ReadString(p, "security_name")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

            List<DateTime> changeTimes = deals.SelectMany(p => new[] { ReadTime(p, "open_time"), ReadTime(p, "close_time") })
                .Where(t => t != DateTime.MinValue).Distinct().OrderBy(t => t).ToList();
            if (changeTimes.Count == 0) return;

            VolumeShowNumbers.SelectionChanged -= VolumeShowNumbers_SelectionChanged;
            string previousPage = VolumeShowNumbers.SelectedItem as string;
            VolumeShowNumbers.Items.Clear();
            for (int start = 0; start < securities.Count; start += 10)
                VolumeShowNumbers.Items.Add((start + 1) + " - " + Math.Min(start + 10, securities.Count));
            if (VolumeShowNumbers.Items.Count == 0) VolumeShowNumbers.Items.Add("1 - 0");
            VolumeShowNumbers.SelectedItem = VolumeShowNumbers.Items.Contains(previousPage) ? previousPage : VolumeShowNumbers.Items[0];
            VolumeShowNumbers.SelectionChanged += VolumeShowNumbers_SelectionChanged;

            int pageStart = VolumeShowNumbers.SelectedIndex >= 0 ? VolumeShowNumbers.SelectedIndex * 10 : 0;
            List<string> page = securities.Skip(pageStart).Take(10).ToList();

            for (int s = 0; s < page.Count; s++)
            {
                string security = page[s];
                decimal[] netVolume = new decimal[changeTimes.Count];

                foreach (JsonElement p in deals.Where(p => ReadString(p, "security_name") == security))
                {
                    decimal volume = ReadDecimal(p, "volume");
                    if (volume == 0) continue;
                    bool isBuy = string.Equals(ReadString(p, "side"), "Buy", StringComparison.OrdinalIgnoreCase);
                    int openIndex = changeTimes.IndexOf(ReadTime(p, "open_time"));
                    int closeIndex = changeTimes.IndexOf(ReadTime(p, "close_time"));

                    if (openIndex >= 0)
                        for (int i2 = openIndex; i2 < netVolume.Length; i2++) netVolume[i2] += isBuy ? volume : -volume;
                    if (ReadString(p, "state") == "Done" && closeIndex >= 0)
                        for (int i2 = closeIndex; i2 < netVolume.Length; i2++) netVolume[i2] += isBuy ? -volume : volume;
                }

                ChartArea area = new ChartArea("ChartArea" + s)
                {
                    BackColor = Themes.ThemeManager.GetColorWinForms("JournalChartBackColor"),
                    BorderColor = Themes.ThemeManager.GetColorWinForms("JournalChartBorderColor")
                };
                area.CursorX.IsUserEnabled = true;
                area.AxisY2.Enabled = AxisEnabled.False;
                foreach (Axis axis in area.Axes)
                {
                    axis.TitleForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                    axis.LabelStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("JournalChartTextColor");
                }
                _volumeChart.ChartAreas.Add(area);

                Color color = s % 2 == 0 ? Themes.ThemeManager.GetColorWinForms("ChartEquityColor") : Themes.ThemeManager.GetColorWinForms("JournalShortColor");
                Series line = new Series("Series" + s) { ChartType = SeriesChartType.Line, ChartArea = area.Name, Color = color, BorderWidth = 3 };
                for (int i = 0; i < netVolume.Length; i++)
                {
                    line.Points.AddXY(i, (double)netVolume[i]);
                    line.Points[^1].AxisLabel = FormatRemoteTime(changeTimes[i].ToString("O"));
                }
                _volumeChart.Series.Add(line);

                Series label = new Series("Name" + s) { ChartType = SeriesChartType.Point, ChartArea = area.Name, Color = Themes.ThemeManager.GetColorWinForms("JournalEquityTotalBrush"), MarkerStyle = MarkerStyle.Square, MarkerSize = 4 };
                label.Points.AddXY(0, (double)netVolume.Max());
                label.Points[0].Label = security;
                _volumeChart.Series.Add(label);
            }
        }

        private void VolumeShowNumbers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            try { RefreshVolumeChart(); } catch (Exception ex) { ShowError(ex); }
        }

        #endregion

        #region Small table helper (Volume->To portfolio stub)

        private static DataGridView CreateTwoColumnGrid(string keyHeader, string valueHeader)
        {
            DataGridView grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            AddColumn(grid, keyHeader, 220);
            AddColumn(grid, valueHeader, 0);
            return grid;
        }

        private static void AddGridRow(DataGridView grid, params object[] values) => grid.Rows.Add(values);

        private static void AddColumn(DataGridView grid, string header, int width)
        {
            DataGridViewTextBoxCell cell = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn { CellTemplate = cell, HeaderText = header, ReadOnly = true };
            column.AutoSizeMode = width > 0 ? DataGridViewAutoSizeColumnMode.None : DataGridViewAutoSizeColumnMode.Fill;
            if (width > 0) column.Width = width;
            column.DefaultCellStyle.ForeColor = Themes.ThemeManager.GetColorWinForms("GridTextColor");
            grid.Columns.Add(column);
        }

        #endregion

        #region Open / Closed positions (real client-side paging over the already-fetched list; profit/side coloring)

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

        private static int ParsePageSize(string label)
        {
            string digits = new string((label ?? "").TakeWhile(c => char.IsDigit(c)).ToArray());
            return int.TryParse(digits, out int size) ? size : 100;
        }

        // "показывать неоткрытые позиции" -> include_failed на сервере; требует новой выборки, поэтому
        // сразу перегружаем закрытые позиции целиком (полный, а не постраничный вызов).
        private async void CheckBoxShowDontOpenPoses_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _showFailedClosed = CheckBoxShowDontOpenPoses.IsChecked == true;
                _allClosedPositions = await FetchAllPositions("bot_journal_get_closed_positions", includeFailed: _showFailedClosed);
                RenderClosedPositionsPage();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        // Раскраска строки — 1:1 с Journal/Internal/PositionController.GetRow: цвет всей строки по знаку
        // прибыли (MarketDepthBidColor/MarketDepthAskColor — обычно зелёный/красный темы), а колонка
        // "Напр." дополнительно перекрашена отдельно (DodgerBlue/ChartBarMinusColor по стороне сделки).
        // Колонка "Имя" (Cells[3], bot_name) здесь особенно полезна — показывает, какому боту принадлежит
        // строка, чего не было видно в single-bot окне.
        private void RenderPositionRows(DataGridView grid, IEnumerable<JsonElement> positions, bool closed)
        {
            grid.Rows.Clear();
            Color profitColor = Themes.ThemeManager.GetColorWinForms("MarketDepthBidColor");
            Color lossColor = Themes.ThemeManager.GetColorWinForms("MarketDepthAskColor");
            Color buyColor = Themes.ThemeManager.GetColorWinForms("JournalDodgerBlueColor");
            Color sellColor = Themes.ThemeManager.GetColorWinForms("ChartBarMinusColor");

            foreach (JsonElement p in positions)
            {
                // 1:1 с Journal/Internal/PositionController.GetRow: каждая ячейка — DataGridViewTextBoxCell,
                // включая колонки 0-4 (в DataGridFactory.GetDataGridPosition это DataGridViewButtonColumn).
                // Обычный grid.Rows.Add() берёт CellTemplate колонки и создал бы там настоящие
                // DataGridViewButtonCell — те рисуются системной 3D-кнопкой (светлая рамка), а не темой
                // грида, из-за чего первые 5 столбцов визуально отличались от остальных.
                DataGridViewRow row = new DataGridViewRow();
                for (int c = 0; c < grid.Columns.Count; c++)
                {
                    row.Cells.Add(new DataGridViewTextBoxCell());
                }
                grid.Rows.Add(row);
                decimal profit = ReadDecimal(p, "profit_abs");
                string side = ReadString(p, "side");
                Color rowColor = profit > 0 ? profitColor : lossColor;

                row.DefaultCellStyle.ForeColor = rowColor;
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    row.Cells[c].Style.ForeColor = rowColor;
                }

                row.Cells[0].Value = ReadInt(p, "number");
                row.Cells[1].Value = FormatRemoteTime(ReadString(p, "open_time"));
                row.Cells[2].Value = closed ? FormatRemoteTime(ReadString(p, "close_time")) : string.Empty;
                row.Cells[3].Value = ReadString(p, "bot_name");
                row.Cells[4].Value = ReadString(p, "security_name");
                row.Cells[5].Value = side;
                row.Cells[5].Style.ForeColor = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ? buyColor : sellColor;
                row.Cells[6].Value = ReadString(p, "state");
                row.Cells[7].Value = FormatNumber(ReadDecimal(p, "volume"));
                row.Cells[8].Value = FormatNumber(ReadDecimal(p, "open_volume"));
                row.Cells[9].Value = FormatNumber(ReadDecimal(p, "wait_volume"));
                row.Cells[10].Value = FormatNumber(ReadDecimal(p, "entry_price"));
                row.Cells[11].Value = FormatNumber(ReadDecimal(p, "close_price"));
                row.Cells[12].Value = FormatNumber(profit);
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
            _volumeChart?.Dispose(); _volumeChart = null;
            _statisticsGrid?.Dispose(); _statisticsGrid = null;
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
