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
using OsEngine.Journal;
using OsEngine.Indicators;
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
        private readonly Dictionary<int, RobotsVpsPositionCloseUi> _remotePositionCloseWindows = new();
        private RobotsVpsPositionSupportUi _remotePositionSupportWindow;
        // Индикаторы, которые бот уже настроил на своей вкладке (bot_chart_get_indicators) и которые этот
        // клиент уже воссоздал локально в _chartMaster — по имени, чтобы не пересоздавать на каждый тик.
        private readonly HashSet<string> _syncedServerIndicators = new(StringComparer.OrdinalIgnoreCase);

        // Родительская вкладка держит ВЕСЬ график бота (tab.CandlesAll) без ограничения длины, поэтому
        // первая свеча в её массиве никогда не меняется — WinFormsChartPainter.PaintCandles всегда попадает
        // в лёгкий путь (AddCandleInArray/RePaintToIndex, апдейт последней точки) без рывков. У нас запрос
        // с фиксированным candle_count=500 — СКОЛЬЗЯЩЕЕ окно: как только у бота накопилось больше 500 свечей,
        // самая старая свеча окна на каждый опрос СДВИГАЕТСЯ, PaintCandles видит "другой массив свечек" и
        // делает полный PaintAllCandles + ResizeYAxisOnArea(..., full: true) — отсюда дёрганье графика (и,
        // как следствие, тот же сброс истории у индикаторов, которые не пересчитываются из свечей).
        // Решение: не скользить — расширять запрашиваемое окно так, чтобы самая первая увиденная свеча
        // оставалась в ответе (как у родителя), пока не упрёмся в серверный максимум bot_chart_get_snapshot.
        private const int InitialCandleCount = 500;
        private const int MaxCandleCount = 2000;
        private int _requestedCandleCount = InitialCandleCount;
        private DateTime _anchorCandleTimeUtc = DateTime.MinValue;
        private TimeSpan _knownTimeFrameSpan = TimeSpan.Zero;

        // Индикаторы для EmptyIndicator-подобных типов не пересчитываются из свечей — их надо тянуть с
        // сервера (см. RefreshChartIndicatorsAsync/ApplyServerDataSeries), но полная перерисовка серии на
        // каждый опрос (раз в несколько секунд, из-за одного лишь обновления цены последней свечи) даёт
        // дёрганье линий индикатора без какой-либо новой информации — исторические 500-2000 значений между
        // опросами не меняются. Обновляем индикаторы только когда сменилась ПОСЛЕДНЯЯ свеча (число она же
        // TimeStart), плюс всегда один раз сразу при открытии чарта (MinValue != любое реальное время).
        private DateTime _lastIndicatorRefreshCandleTimeUtc = DateTime.MinValue;

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
            _openPositionsGrid.Click += OpenPositionsGrid_Click;
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
                JsonElement snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = _requestedCandleCount });
                List<Candle> candles = ReadCandles(snapshot);

                if (candles.Count > 0 && _anchorCandleTimeUtc != DateTime.MinValue
                    && candles[0].TimeStart > _anchorCandleTimeUtc && _requestedCandleCount < MaxCandleCount)
                {
                    // Окно свечей сдвинулось бы (самая старая свеча стала новее нашего якоря) и вызвала бы
                    // рывок в PaintCandles/ProcessAll — расширяем окно и перезапрашиваем в этом же цикле, до
                    // того как эти свечи попадут в _chartMaster, чтобы пользователь вообще не увидел скачок.
                    _requestedCandleCount = Math.Min(MaxCandleCount, _requestedCandleCount * 2);
                    snapshot = await _client.CallToolAsync("bot_chart_get_snapshot", new { bot_id = _botId, tab_name = _tabName, candle_count = _requestedCandleCount });
                    candles = ReadCandles(snapshot);
                }

                if (candles.Count > 0)
                {
                    // Минимум, а не "только если ещё не задан": окно могло вырасти НАЗАД (см.
                    // GrowCandleWindowForPositions) — тогда новая candles[0] раньше старого якоря, и
                    // якорь должен сдвинуться вместе с ней, иначе последующее сравнение в блоке выше
                    // (candles[0] > _anchorCandleTimeUtc) не будет отражать реальную границу окна.
                    if (_anchorCandleTimeUtc == DateTime.MinValue || candles[0].TimeStart < _anchorCandleTimeUtc)
                        _anchorCandleTimeUtc = candles[0].TimeStart;

                    TimeFrame timeFrame = ReadTimeFrame(snapshot);
                    if (!_chartTimeFrameInitialized)
                    {
                        _knownTimeFrameSpan = GetTimeSpan(timeFrame);
                        _chartMaster.ChartCandle.SetNewTimeFrame(_knownTimeFrameSpan, timeFrame);
                        _chartTimeFrameInitialized = true;
                    }
                    _chartMaster.SetCandles(candles);
                    _securityName = ReadText(snapshot, "security_name");
                    string interval = ReadText(snapshot, "time_frame");
                    _startTitle = _botName + " / " + (string.IsNullOrWhiteSpace(_securityName) ? _tabName : _securityName + " / " + interval);
                    Title = _startTitle;

                    // Значки входа/выхода и индикаторы, настроенные ботом, — на графике всегда, вне
                    // зависимости от того, какая боковая вкладка сейчас выбрана (в отличие от таблиц ниже).
                    await RefreshChartPositionsAsync();

                    DateTime lastCandleTimeUtc = candles[candles.Count - 1].TimeStart;
                    if (lastCandleTimeUtc != _lastIndicatorRefreshCandleTimeUtc)
                    {
                        // Первый опрос (MinValue) — рисуем сразу; иначе только когда появилась новая
                        // свеча, а не на каждое обновление цены текущей.
                        _lastIndicatorRefreshCandleTimeUtc = lastCandleTimeUtc;
                        await RefreshChartIndicatorsAsync();
                    }
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

        #region Chart positions (entry/exit markers) — ChartCandleMaster.SetPosition, fed from journal data

        // ChartCandleMaster/WinFormsChartPainter уже умеют рисовать значки входа/выхода — это стандартная
        // функциональность родного графика (WinFormsChartPainter.PaintPositions), просто окно раньше никогда
        // не вызывало SetPosition. bot_journal_get_open/closed_positions не отдают отдельные сделки
        // (MyTrades/Order), поэтому здесь собирается МИНИМАЛЬНАЯ позиция: один ордер на открытие
        // (+ один на закрытие) с одной сделкой каждый — этого достаточно для треугольников входа/выхода,
        // но не для интрадей-заявок на несколько частичных исполнений и для линий live-стопа/профита
        // (те строятся из полноценных Order в позиции, которых MCP пока не отдаёт).
        private async System.Threading.Tasks.Task RefreshChartPositionsAsync()
        {
            try
            {
                JsonElement openResponse = await _client.CallToolAsync("bot_journal_get_open_positions", new { bot_name = _botId, limit = 500 });
                JsonElement closedResponse = await _client.CallToolAsync("bot_journal_get_closed_positions", new { bot_name = _botId, limit = 500 });

                List<Position> positions = new List<Position>();
                DateTime oldestNeededTimeUtc = DateTime.MaxValue;
                CollectChartPositions(openResponse, closed: false, positions, ref oldestNeededTimeUtc);
                CollectChartPositions(closedResponse, closed: true, positions, ref oldestNeededTimeUtc);

                _chartMaster.SetPosition(positions);
                GrowCandleWindowForPositions(oldestNeededTimeUtc);
            }
            catch
            {
                // значки на графике — необязательная надстройка; сбой не должен ронять обновление свечей/таблиц
            }
        }

        private void CollectChartPositions(JsonElement response, bool closed, List<Position> target, ref DateTime oldestNeededTimeUtc)
        {
            if (!response.TryGetProperty("positions", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;

            foreach (JsonElement p in list.EnumerateArray())
            {
                // bot_name в ответе bot_journal_get_*_positions — имя ВКЛАДКИ (см. комментарий в RenderPositionRows),
                // однозначно совпадает с _tabName этого окна.
                if (!string.Equals(ReadString(p, "bot_name"), _tabName, StringComparison.OrdinalIgnoreCase)) continue;

                Position position = BuildChartPosition(p, closed);
                if (position == null) continue;

                target.Add(position);

                // Значок сделки становится видимым, только если её время попадает в уже загруженное окно
                // свечей (WinFormsChartPainter.GetTimeIndex ищет индекс СРЕДИ _myCandles — сделка старше
                // самой первой загруженной свечи резолвится в 0 и молча пропускается PaintPositions).
                // Запоминаем самое старое время сделки здесь, чтобы окно могло дорасти НАЗАД до него.
                DateTime tradeTimeUtc = position.MyTrades.Count > 0 ? position.MyTrades[0].Time : DateTime.MaxValue;
                if (tradeTimeUtc != DateTime.MinValue && tradeTimeUtc < oldestNeededTimeUtc) oldestNeededTimeUtc = tradeTimeUtc;
            }
        }

        // Родительская вкладка видит все сделки, потому что её окно свечей не ограничено. Наше растёт
        // только ВПЕРЁД, за скользящий якорь (см. комментарий у _anchorCandleTimeUtc) — сделку старше
        // самой первой когда-либо увиденной свечи оно само по себе никогда не захватит. Если такая
        // сделка нашлась, считаем недостающее число свечей по времени и заранее расширяем окно —
        // подействует на следующий опрос (в этом уже отрисованы текущие, более узкие свечи).
        private void GrowCandleWindowForPositions(DateTime oldestNeededTimeUtc)
        {
            if (oldestNeededTimeUtc == DateTime.MaxValue) return;
            if (_anchorCandleTimeUtc == DateTime.MinValue) return;
            if (oldestNeededTimeUtc >= _anchorCandleTimeUtc) return;
            if (_requestedCandleCount >= MaxCandleCount) return;
            if (_knownTimeFrameSpan <= TimeSpan.Zero) return;

            double missingCandles = (_anchorCandleTimeUtc - oldestNeededTimeUtc).TotalSeconds / _knownTimeFrameSpan.TotalSeconds;
            int neededCount = _requestedCandleCount + (int)Math.Ceiling(missingCandles) + 20;
            _requestedCandleCount = Math.Min(MaxCandleCount, Math.Max(_requestedCandleCount, neededCount));
        }

        private Position BuildChartPosition(JsonElement p, bool closed)
        {
            decimal volume = ReadDecimal(p, "volume");
            decimal entryPrice = ReadDecimal(p, "entry_price");
            DateTime openTime = ReadDateTime(p, "open_time");
            if (openTime == DateTime.MinValue) openTime = ReadDateTime(p, "time_create");
            if (volume <= 0 || entryPrice <= 0 || openTime == DateTime.MinValue) return null;

            string securityName = ReadString(p, "security_name");
            Side openSide = string.Equals(ReadString(p, "side"), "Sell", StringComparison.OrdinalIgnoreCase) ? Side.Sell : Side.Buy;

            Position position = new Position
            {
                Number = ReadInt(p, "number"),
                NameBot = ReadString(p, "bot_name"),
                Direction = openSide
            };

            position.AddNewOpenOrder(BuildChartOrder(securityName, openSide, entryPrice, volume, openTime, OrderPositionConditionType.Open, "open_" + position.Number));

            if (closed)
            {
                decimal closePrice = ReadDecimal(p, "close_price");
                DateTime closeTime = ReadDateTime(p, "close_time");
                if (closePrice > 0 && closeTime != DateTime.MinValue)
                {
                    Side closeSide = openSide == Side.Buy ? Side.Sell : Side.Buy;
                    position.AddNewCloseOrder(BuildChartOrder(securityName, closeSide, closePrice, volume, closeTime, OrderPositionConditionType.Close, "close_" + position.Number));
                }
            }

            if (Enum.TryParse(ReadString(p, "state"), out PositionStateType stateType)) position.State = stateType;

            position.StopOrderPrice = ReadDecimal(p, "stop_order_price");
            position.StopOrderRedLine = ReadDecimal(p, "stop_order_red_line");
            position.StopOrderIsActive = position.StopOrderPrice != 0;
            position.ProfitOrderPrice = ReadDecimal(p, "profit_order_price");
            position.ProfitOrderRedLine = ReadDecimal(p, "profit_order_red_line");
            position.ProfitOrderIsActive = position.ProfitOrderPrice != 0;

            return position;
        }

        private static Order BuildChartOrder(string securityName, Side side, decimal price, decimal volume, DateTime time, OrderPositionConditionType conditionType, string idSuffix)
        {
            string orderNumberMarket = "vps_order_" + idSuffix;
            Order order = new Order
            {
                SecurityNameCode = securityName,
                Side = side,
                Price = price,
                Volume = volume,
                VolumeExecute = volume,
                State = OrderStateType.Done,
                PositionConditionType = conditionType,
                TimeCreate = time,
                TimeDone = time,
                // Order.SetTrade отвергает сделку, если MyTrade.NumberOrderParent != Order.NumberMarket
                // (проверка принадлежности сделки ордеру) — без этого NumberMarket остаётся "" (дефолт
                // конструктора Order) и НИКОГДА не совпадает с NumberOrderParent ниже, SetTrade молча
                // ничего не добавляет в _trades, Position.MyTrades всегда пуст и PaintPositions нечего
                // рисовать — это и есть причина, по которой значки входа/выхода не появлялись вообще.
                NumberMarket = orderNumberMarket
            };
            order.SetTrade(new MyTrade
            {
                Volume = volume,
                Price = price,
                Time = time,
                Side = side,
                SecurityNameCode = securityName,
                NumberTrade = "vps_" + idSuffix,
                NumberOrderParent = orderNumberMarket
            });
            return order;
        }

        private static DateTime ReadDateTime(JsonElement e, string prop)
        {
            string s = ReadString(e, prop);
            return !string.IsNullOrEmpty(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t : DateTime.MinValue;
        }

        #endregion

        #region Chart indicators — reconstruct the bot's own configured indicators locally and let
        // ChartCandleMaster compute/paint them from the candles it already streams (no server-side math)

        // bot_chart_get_indicators — перепись УЖЕ настроенных индикаторов вкладки (тип/область/параметры)
        // плюс data_series (то же окно последних значений, что _requestedCandleCount у bot_chart_get_snapshot).
        // Каждый новый (по имени) индикатор реконструируется через ту же IndicatorsFactory, что и родной
        // "Create indicator" в правой кнопке мыши, и добавляется в ту же область (Prime = поверх цены,
        // любое другое имя = отдельное окно/область под графиком — ChartCandleMaster создаёт её сам).
        //
        // Для формульных индикаторов (Sma/Rsi/...) локальный Process(candles) и так даёт тот же результат —
        // data_series тут избыточны, но применяются единообразно. А вот для EmptyIndicator и подобных, у
        // которых OnProcess — no-op (значения пишет сам код бота), это ЕДИНСТВЕННЫЙ источник истории: каждый
        // ChartCandleMaster.SetCandles(...) выше (см. RefreshSelectedChartDataAsync) заново гоняет
        // Process(candles) по всем уже добавленным индикаторам и для EmptyIndicator обнуляет DataSeries —
        // поэтому серверные значения накатываются здесь ПОСЛЕ CreateIndicator, на каждый опрос, а не только
        // при первом создании.
        private async System.Threading.Tasks.Task RefreshChartIndicatorsAsync()
        {
            try
            {
                // candle_count здесь должен совпадать с тем, что сейчас запрошен для свечей
                // (_requestedCandleCount, растущее окно — см. RefreshSelectedChartDataAsync), иначе
                // data_series и candles разъедутся по длине/якорю и индексная привязка PaintLikeLine собьётся.
                JsonElement response = await _client.CallToolAsync("bot_chart_get_indicators", new { bot_id = _botId, tab_name = _tabName, candle_count = _requestedCandleCount });
                if (!response.TryGetProperty("indicators", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return;

                foreach (JsonElement ind in list.EnumerateArray())
                {
                    string name = ReadString(ind, "name");
                    if (string.IsNullOrEmpty(name)) continue;

                    if (!_syncedServerIndicators.Contains(name))
                    {
                        // Отмечаем сразу, даже если реконструкция не удастся (легаси/неизвестный тип) —
                        // чтобы не пытаться пересоздавать один и тот же индикатор на каждый тик.
                        _syncedServerIndicators.Add(name);

                        if (!ReadBool(ind, "is_supported")) continue;

                        string typeName = ReadString(ind, "type_name");
                        string area = ReadString(ind, "area");
                        if (string.IsNullOrEmpty(area)) area = "Prime";

                        Aindicator newIndicator = IndicatorsFactory.CreateIndicatorByName(typeName, name, canDelete: false, StartProgram.IsOsTrader);
                        if (newIndicator == null) continue;

                        if (ind.TryGetProperty("parameters", out JsonElement newParameters) && newParameters.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement param in newParameters.EnumerateArray())
                            {
                                ApplyIndicatorParameter(newIndicator, param);
                            }
                        }

                        _chartMaster.CreateIndicator(newIndicator, area);
                    }

                    ApplyServerDataSeries(name, ind);
                }
            }
            catch
            {
                // индикаторы бота — необязательная надстройка; сбой не должен ронять обновление свечей/таблиц
            }
        }

        private void ApplyServerDataSeries(string indicatorName, JsonElement ind)
        {
            if (!ind.TryGetProperty("data_series", out JsonElement seriesList) || seriesList.ValueKind != JsonValueKind.Array) return;

            IIndicator indicator = _chartMaster.Indicators?.Find(i => i.Name == indicatorName);
            if (indicator is not Aindicator aIndicator) return;

            List<IndicatorDataSeries> localSeries = aIndicator.DataSeries;
            if (localSeries == null) return;

            bool changed = false;
            int seriesIndex = 0;

            foreach (JsonElement s in seriesList.EnumerateArray())
            {
                if (seriesIndex >= localSeries.Count) break;

                if (s.TryGetProperty("values", out JsonElement valuesEl) && valuesEl.ValueKind == JsonValueKind.Array)
                {
                    List<decimal> values = new List<decimal>();
                    foreach (JsonElement v in valuesEl.EnumerateArray())
                    {
                        values.Add(v.TryGetDecimal(out decimal d) ? d : 0m);
                    }

                    localSeries[seriesIndex].Values.Clear();
                    localSeries[seriesIndex].Values.AddRange(values);
                    changed = true;
                }

                // Реальный цвет серии на сервере (мог быть переопределён пользователем в настройках
                // индикатора на самом боте) — иначе клиент красит жёстким дефолтом из OnStateChange,
                // одинаковым для всех экземпляров одного класса (отсюда "все линии зелёные" у EmptyIndicator).
                if (s.TryGetProperty("color_argb", out JsonElement colorEl) && colorEl.ValueKind == JsonValueKind.Number
                    && colorEl.TryGetInt32(out int argb))
                {
                    localSeries[seriesIndex].Color = System.Drawing.Color.FromArgb(argb);
                    changed = true;
                }

                seriesIndex++;
            }

            // Только перерисовать (ChartCandle.RePaintIndicator), а не aIndicator.RePaint()/Reload() —
            // те снова вызовут Process(candles) и затрут только что применённые серверные значения.
            if (changed) _chartMaster.ChartCandle.RePaintIndicator(indicator);
        }

        private static void ApplyIndicatorParameter(Aindicator indicator, JsonElement param)
        {
            string paramName = ReadString(param, "name");
            IndicatorParameter target = indicator.Parameters?.Find(x => x.Name == paramName);
            if (target == null) return;

            string paramType = ReadString(param, "type");
            if (paramType == "Int" && target is IndicatorParameterInt intParam)
                intParam.ValueInt = ReadInt(param, "value_int");
            else if (paramType == "Decimal" && target is IndicatorParameterDecimal decimalParam)
                decimalParam.ValueDecimal = ReadDecimal(param, "value_decimal");
            else if (paramType == "Bool" && target is IndicatorParameterBool boolParam)
                boolParam.ValueBool = ReadBool(param, "value_bool");
            else if (paramType == "String" && target is IndicatorParameterString stringParam)
                stringParam.ValueString = ReadString(param, "value_string");
        }

        #endregion

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

        // Right-click menu on the open-positions grid — same 7 items, order and labels as the parent
        // Bot Station's Journal\Internal\PositionController._gridOpenDeal_Click. "Close selected",
        // "Add to selected", "Swap stop" and "Swap profit" still need their own remote dialogs
        // (PositionCloseUi2 / PositionAddingUi2 / stop-profit editors are not ported yet) — they show
        // the same "not available yet" notice already used elsewhere in this window rather than fake it.
        private void OpenPositionsGrid_Click(object sender, EventArgs e)
        {
            if (!(e is MouseEventArgs mouse) || mouse.Button != MouseButtons.Right) return;
            if (_openPositionsGrid.Rows.Count == 0 || _openPositionsGrid.CurrentCell == null) return;

            int rowIndex = _openPositionsGrid.CurrentCell.RowIndex;
            if (rowIndex < 0 || rowIndex >= _openPositionsGrid.Rows.Count) return;
            DataGridViewRow row = _openPositionsGrid.Rows[rowIndex];

            int positionNumber;
            try { positionNumber = Convert.ToInt32(row.Cells[0].Value); }
            catch { return; }
            string securityName = row.Cells[4].Value as string;

            ToolStripMenuItem[] items = new ToolStripMenuItem[7];

            items[0] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem1 };
            items[0].Click += (s, args) => _ = CloseAllPositionsAtMarketAsync();

            items[1] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem2 };
            items[1].Click += (s, args) => ButtonMoreOpenPositionDetail_Click(null, null);

            items[2] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem3 };
            items[2].Click += (s, args) => OpenPositionCloseDialog(positionNumber, "Limit");

            items[3] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem14 };
            items[3].Click += (s, args) => NotAvailableRemotely(); // PositionAddingUi2 — не портирован

            items[4] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem5 };
            items[4].Click += (s, args) => OpenPositionCloseDialog(positionNumber, "Stop");

            items[5] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem6 };
            items[5].Click += (s, args) => OpenPositionCloseDialog(positionNumber, "Profit");

            items[6] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem7 };
            items[6].Click += (s, args) => _ = DeleteSelectedPositionAsync(positionNumber, securityName);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(items);
            _openPositionsGrid.ContextMenuStrip = menu;
            _openPositionsGrid.ContextMenuStrip.Show(_openPositionsGrid, new System.Drawing.Point(mouse.X, mouse.Y));
        }

        // "Закрыть выбранную" / "Переставить стоп" / "Переставить профит" в оригинале — один и тот же
        // диалог (PositionCloseUi2) с разной начальной вкладкой; повторный вызов для той же позиции
        // активирует уже открытое окно и переключает вкладку, как и оригинал.
        private void OpenPositionCloseDialog(int positionNumber, string initialTab)
        {
            if (_remotePositionCloseWindows.TryGetValue(positionNumber, out RobotsVpsPositionCloseUi existing) && existing.IsVisible)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                existing.SelectTab(initialTab);
                return;
            }

            RobotsVpsPositionCloseUi window = new RobotsVpsPositionCloseUi(_client, _botId, _tabName, _securityName, positionNumber)
            {
                Owner = this
            };
            window.SelectTab(initialTab);
            window.Closed += (s, args) => _remotePositionCloseWindows.Remove(positionNumber);
            _remotePositionCloseWindows[positionNumber] = window;
            window.Show();
        }

        private async System.Threading.Tasks.Task CloseAllPositionsAtMarketAsync()
        {
            if (System.Windows.MessageBox.Show(OsLocalization.Journal.Message5, "VPS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                JsonElement response = await _client.CallToolAsync("bot_position_get_open", new { bot_id = _botId, tab_name = _tabName });

                if (response.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement p in positions.EnumerateArray())
                    {
                        int number = ReadInt(p, "position_number");
                        await _client.CallToolAsync("bot_position_close_at_market", new { bot_id = _botId, tab_name = _tabName, position_number = number });
                    }
                }

                await RefreshOpenPositionsAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async System.Threading.Tasks.Task DeleteSelectedPositionAsync(int positionNumber, string securityName)
        {
            if (System.Windows.MessageBox.Show(OsLocalization.Journal.Message3, "VPS", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            try
            {
                object args = string.IsNullOrEmpty(securityName)
                    ? new { bot_id = _botId, tab_name = _tabName, position_number = positionNumber }
                    : (object)new { bot_id = _botId, tab_name = _tabName, position_number = positionNumber, security_name = securityName };

                await _client.CallToolAsync("bot_position_delete", args);
                await RefreshOpenPositionsAsync();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
                // Раньше фильтровали по совпадению security_name с _securityName из bot_chart_get_snapshot —
                // сервер иногда возвращает их по-разному (например, у позиции "AAVEUSDT TestPaper", у вкладки
                // просто "AAVEUSDT"), и открытая позиция пропадала из графика, хотя оставалась в общем списке
                // (Роботы.ВПС → Bots). Правильный признак "эта позиция — с этой вкладки" — поле bot_name в
                // ответе bot_journal_get_open_positions: сервер кладёт туда имя ВКЛАДКИ (tab.TabName), а не
                // общее имя бота, и оно однозначно совпадает с _tabName этого окна.
                if (!closed && !string.Equals(ReadString(p, "bot_name"), _tabName, StringComparison.OrdinalIgnoreCase)) continue;
                // 1:1 с Journal/Internal/PositionController.GetRow (и с тем же фиксом в RobotsVpsJournalUi):
                // каждая ячейка — DataGridViewTextBoxCell, включая колонки 0-4 (в DataGridFactory.GetDataGridPosition
                // это DataGridViewButtonColumn). Обычный grid.Rows.Add() берёт CellTemplate колонки и создал бы там
                // настоящие DataGridViewButtonCell — те рисуются системной 3D-кнопкой (светлая рамка), а не темой.
                DataGridViewRow row = new DataGridViewRow();
                for (int c = 0; c < grid.Columns.Count; c++)
                {
                    row.Cells.Add(new DataGridViewTextBoxCell());
                }
                grid.Rows.Add(row);
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
        // 1:1 с OsTraderMaster.BotManualSettingsDialog -> BotTabSimple.ShowManualControlDialog: сервер уже
        // полностью отдаёт/принимает этот DTO (bot_get_position_support/bot_set_position_support), поэтому
        // это реальное окно, а не заглушка — одно окно на вкладку, повторный клик активирует уже открытое.
        private void ButtonStrategManualSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_remotePositionSupportWindow != null && _remotePositionSupportWindow.IsVisible)
            {
                if (_remotePositionSupportWindow.WindowState == WindowState.Minimized)
                    _remotePositionSupportWindow.WindowState = WindowState.Normal;
                _remotePositionSupportWindow.Activate();
                return;
            }

            _remotePositionSupportWindow = new RobotsVpsPositionSupportUi(_client, _botId, _tabName) { Owner = this };
            _remotePositionSupportWindow.Closed += (s, args) => _remotePositionSupportWindow = null;
            _remotePositionSupportWindow.Show();
        }
        private RobotsVpsJournalUi _remoteJournalWindow;

        private async void ButtonJournalCommunity_Click(object sender, RoutedEventArgs e)
        {
            if (_remoteJournalWindow != null && _remoteJournalWindow.IsVisible)
            {
                if (_remoteJournalWindow.WindowState == WindowState.Minimized) _remoteJournalWindow.WindowState = WindowState.Normal;
                _remoteJournalWindow.Activate();
                return;
            }

            try
            {
                List<BotPanelJournal> panelsJournal = await RobotsVpsJournalData.LoadAsync(_client, _botId).ConfigureAwait(true);

                if (_remoteJournalWindow != null)
                {
                    return;
                }

                _remoteJournalWindow = new RobotsVpsJournalUi(panelsJournal, StartProgram.IsOsTrader) { Owner = this };
                RemoteMcpClient journalClient = _client;
                string journalBotName = _botId;
                _remoteJournalWindow.ReloadDataAsync = () => RobotsVpsJournalData.RefreshAsync(journalClient, journalBotName, panelsJournal);
                _remoteJournalWindow.DeletePositionOnServer = (name, tabNum, number) => RobotsVpsJournalData.DeleteOnServer(journalClient, name, tabNum, number);
                _remoteJournalWindow.Closed += (s, args) => _remoteJournalWindow = null;
                _remoteJournalWindow.Show();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open the robot journal: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        // OsTraderMaster.BotShowRiskManager -> BotPanel.ShowPanelRiskManagerDialog: открывает RiskManager,
        // объект, который живёт только локально на сервере и не проброшен ни одним MCP-инструментом
        // (в MCP/Modules/RobotsApi.cs таких tools нет вовсе). Явная заглушка, а не тихая подмена.
        private void ButtonRiskManager_Click(object sender, RoutedEventArgs e) =>
            NotAvailableRemotely("Risk manager settings are not exposed over MCP yet");
        private void ButtonRedactTab_Click(object sender, RoutedEventArgs e)
        {
            RobotsVpsDataSettingsUi window = new RobotsVpsDataSettingsUi(_client, _botId, _tabName) { Owner = this };
            window.ShowDialog();
        }
        // OsTraderMaster.BotIndividualSettings -> BotPanel.ShowIndividualSettingsDialog: виртуальный метод,
        // который переопределяет КАЖДАЯ стратегия своим собственным, полностью произвольным WPF-диалогом
        // (см. переопределения в OsEngine/Robots/*.cs) — это не единый настраиваемый DTO, а код конкретного
        // робота, выполняющийся локально. Генерическим MCP-инструментом это принципиально не пробросить без
        // отдельной серверной реализации на каждую стратегию. Явная заглушка.
        private void ButtonStrategIndividualSettings_Click(object sender, RoutedEventArgs e) =>
            NotAvailableRemotely("Bot trade settings are strategy-specific local dialogs, not a generic MCP endpoint");
        private void NotAvailableRemotely(string reason = null)
        {
            string message = "This remote action is not available yet.";
            if (!string.IsNullOrEmpty(reason)) message += " " + reason + ".";
            System.Windows.MessageBox.Show(message, "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
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
