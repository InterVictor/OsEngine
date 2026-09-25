using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;
using OsEngine.Market;
using OsEngine.Market.SupportTable;
using OsEngine.OsTrader.Gui.BlockInterface;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Independent Lite-layout copy for remote VPS robots. It never creates the local OsTrader engine.
    /// </summary>
    public partial class RobotsVpsLiteClone : System.Windows.Controls.UserControl
    {
        private DataGridView _robotsGrid;
        private DataGridView _portfolioGrid;
        private DataGridView _activePositionsGrid;
        private DataGridView _stopLimitPositionsGrid;
        private DataGridView _closedPositionsGrid;
        private DataGridView _activeOrdersGrid;
        private DataGridView _historicalOrdersGrid;
        private DataGridView _serversGrid;
        private DataGridView _serverLogGrid;
        private DataGridView _primeLogGrid;
        private DispatcherTimer _pollTimer;
        private DispatcherTimer _portfolioPollTimer;
        private DispatcherTimer _positionsPollTimer;
        private DispatcherTimer _ordersPollTimer;
        private DispatcherTimer _serversPollTimer;
        private DispatcherTimer _primeLogPollTimer;
        private RemoteMcpClient _client;
        private RobotsVpsCommunityJournalUi _communityJournalWindow;
        private RobotsVpsMigrationUi _migrationWindow;
        private readonly Dictionary<string, RobotsVpsJournalUi> _botJournalWindows = new(StringComparer.OrdinalIgnoreCase);
        // bot_id -> первый "Simple" tab_name из bot_get_sources. Позиции с агрегированных вкладок
        // (bot_journal_get_open/closed_positions) не несут tab_name — тот же упрощающий приём,
        // что и в OpenChartAsync (единственная торговая вкладка на бота).
        private readonly Dictionary<string, string> _tabNameCache = new(StringComparer.OrdinalIgnoreCase);
        // Ключ bot_id+":"+positionNumber — на вкладке "Positions" номера позиций не уникальны между ботами.
        private readonly Dictionary<string, RobotsVpsPositionCloseUi> _positionCloseWindows = new(StringComparer.OrdinalIgnoreCase);
        private bool _pollInFlight;
        private bool _portfolioPollInFlight;
        private bool _positionsPollInFlight;
        private bool _ordersPollInFlight;
        private bool _serversPollInFlight;
        private bool _primeLogPollInFlight;
        private readonly HashSet<string> _attachedRemoteServerTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> _serverSearchResults = new List<int>();
        private string _portfolioStatus;
        private bool _layoutRegistered;
        private bool _loadingAutoConnect;
        public RobotsVpsLiteClone()
        {
            InitializeComponent();
        }

        private void Clone_Loaded(object sender, RoutedEventArgs e)
        {
            Local();
            if (_robotsGrid == null)
            {
                _robotsGrid = CreateRobotsGrid();
            }
            BotsHost.Child = _robotsGrid;
            if (_portfolioGrid == null)
            {
                _portfolioGrid = DataGridFactory.GetDataGridPortfolios();
                _portfolioGrid.CellClick += PortfolioGrid_CellClick;
            }
            HostPortfolios.Child = _portfolioGrid;
            CreatePositionTables();
            CreateOrderTables();
            CreatePrimeLogGrid();
            AddRemoteDataPlaceholders();
            CreateRemoteServersGrid();
            TextBoxSearchSource.TextChanged += ServerSearch_TextChanged;
            ButtonLeftInSearchResults.Click += ServerSearchPrevious_Click;
            ButtonRightInSearchResults.Click += ServerSearchNext_Click;
            rectToMove.MouseEnter += GreedChartPanel_MouseEnter;
            rectToMove.MouseLeave += GreedChartPanel_MouseLeave;
            rectToMove.MouseDown += GreedChartPanel_MouseDown;
            ImagePadlock.MouseEnter += ImagePadlock_MouseEnter;
            ImagePadlock.MouseLeave += ImagePadlock_MouseLeave;
            ImagePadlock.MouseDown += ImagePadlock_MouseDown;
            ImagePadlockOpen.MouseEnter += ImagePadlockOpen_MouseEnter;
            ImagePadlockOpen.MouseLeave += ImagePadlockOpen_MouseLeave;
            ImagePadlockOpen.MouseDown += ImagePadlockOpen_MouseDown;
            ButtonSupportTable.Click += ButtonSupportTable_Click;
            ButtonProxy.Click += ButtonProxy_Click;
            ButtonSystemStress.Click += ButtonSystemStress_Click;
            ButtonServerAvailability.Click += ButtonServerAvailability_Click;
            TabControlPrime.SelectionChanged += TabControlPrime_SelectionChanged;
            HistoricalListPanel.Visibility = Visibility.Collapsed;
            ActiveListPanel.Visibility = Visibility.Collapsed;
            if (!_layoutRegistered && Window.GetWindow(this) is Window owner)
            {
                OsEngine.Layout.GlobalGUILayout.Listen(owner, "botStationVpsUi");
                _layoutRegistered = true;
            }
            VpsRemoteSession.ClientChanged += VpsRemoteSession_ClientChanged;
            SetClient(VpsRemoteSession.Client);
        }

        private void Clone_Unloaded(object sender, RoutedEventArgs e)
        {
            VpsRemoteSession.ClientChanged -= VpsRemoteSession_ClientChanged;
            rectToMove.MouseEnter -= GreedChartPanel_MouseEnter;
            rectToMove.MouseLeave -= GreedChartPanel_MouseLeave;
            rectToMove.MouseDown -= GreedChartPanel_MouseDown;
            ImagePadlock.MouseEnter -= ImagePadlock_MouseEnter;
            ImagePadlock.MouseLeave -= ImagePadlock_MouseLeave;
            ImagePadlock.MouseDown -= ImagePadlock_MouseDown;
            ImagePadlockOpen.MouseEnter -= ImagePadlockOpen_MouseEnter;
            ImagePadlockOpen.MouseLeave -= ImagePadlockOpen_MouseLeave;
            ImagePadlockOpen.MouseDown -= ImagePadlockOpen_MouseDown;
            ButtonSupportTable.Click -= ButtonSupportTable_Click;
            ButtonProxy.Click -= ButtonProxy_Click;
            ButtonSystemStress.Click -= ButtonSystemStress_Click;
            ButtonServerAvailability.Click -= ButtonServerAvailability_Click;
            TabControlPrime.SelectionChanged -= TabControlPrime_SelectionChanged;
            TextBoxSearchSource.TextChanged -= ServerSearch_TextChanged;
            ButtonLeftInSearchResults.Click -= ServerSearchPrevious_Click;
            ButtonRightInSearchResults.Click -= ServerSearchNext_Click;
            _pollTimer?.Stop();
            _pollTimer = null;
            _portfolioPollTimer?.Stop();
            _portfolioPollTimer = null;
            _positionsPollTimer?.Stop();
            _positionsPollTimer = null;
            _ordersPollTimer?.Stop();
            _ordersPollTimer = null;
            _serversPollTimer?.Stop();
            _serversPollTimer = null;
            _primeLogPollTimer?.Stop();
            _primeLogPollTimer = null;
            if (BotsHost != null) BotsHost.Child = null;
            ClearHost(HostActivePoses);
            ClearHost(HostStopLimitPoses);
            ClearHost(HostHistoricalPoses);
            ClearHost(HostActiveOrders);
            ClearHost(HostHistoricalOrders);
            ClearHost(HostActivePoses);
            ClearHost(HostStopLimitPoses);
            ClearHost(HostHistoricalPoses);
            if (_activePositionsGrid != null)
            {
                _activePositionsGrid.Dispose();
                _activePositionsGrid = null;
            }
            if (_stopLimitPositionsGrid != null)
            {
                _stopLimitPositionsGrid.Dispose();
                _stopLimitPositionsGrid = null;
            }
            if (_closedPositionsGrid != null)
            {
                _closedPositionsGrid.Dispose();
                _closedPositionsGrid = null;
            }
            if (_activeOrdersGrid != null)
            {
                _activeOrdersGrid.Dispose();
                _activeOrdersGrid = null;
            }
            if (_historicalOrdersGrid != null)
            {
                _historicalOrdersGrid.Dispose();
                _historicalOrdersGrid = null;
            }
            if (_portfolioGrid != null)
            {
                _portfolioGrid.CellClick -= PortfolioGrid_CellClick;
                HostPortfolios.Child = null;
                _portfolioGrid.Dispose();
                _portfolioGrid = null;
            }
            ClearHost(HostBotLogPrime);
            if (_primeLogGrid != null)
            {
                _primeLogGrid.Dispose();
                _primeLogGrid = null;
            }
            ClearHost(HostServers);
            ClearHost(HostServerLog);
            if (_serversGrid != null)
            {
                _serversGrid.CellMouseClick -= ServersGrid_CellMouseClick;
                _serversGrid.DoubleClick -= ServersGrid_DoubleClick;
                _serversGrid.Dispose();
                _serversGrid = null;
            }
            if (_serverLogGrid != null)
            {
                _serverLogGrid.Dispose();
                _serverLogGrid = null;
            }
        }

        private void Local()
        {
            Window owner = Window.GetWindow(this);
            if (owner != null)
                owner.Title = "Robots.VPS " + OsEngine.PrimeSettings.PrimeSettingsMaster.LabelInHeaderBotStation;
            LabelOsa.Content = "V_" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            TabItemAllPos.Header = OsLocalization.Trader.Label20;
            TabPortfolios.Header = OsLocalization.Trader.Label21;
            TabAllOrders.Header = OsLocalization.Trader.Label22;
            TabItemLogPrime.Header = OsLocalization.Trader.Label24;
            TabItemControl.Header = OsLocalization.Trader.Label37;
            CheckBoxServerAutoOpen.Content = OsLocalization.Market.Label20;
            TabActivePos.Header = OsLocalization.Trader.Label187;
            TabHistoricalPos.Header = OsLocalization.Trader.Label188;
            TabActiveOrders.Header = OsLocalization.Trader.Label189;
            TabHistoricalOrders.Header = OsLocalization.Trader.Label190;
            TabStopLimitPoses.Header = OsLocalization.Trader.Label193;
            ButtonSupportTable.Content = OsLocalization.Market.Label81;
            ButtonProxy.Content = OsLocalization.Market.Label172;
            ButtonSystemStress.Content = OsLocalization.Trader.Label560;
            ButtonServerAvailability.Content = OsLocalization.Trader.Label605;
            TextBoxSearchSource.Text = OsLocalization.Market.Label64;
            LabelPageActive.Content = OsLocalization.Trader.Label576;
            LabelFromActive.Content = OsLocalization.Trader.Label577;
            LabelCountActive.Content = OsLocalization.Trader.Label578;
            LabelPageHistorical.Content = OsLocalization.Trader.Label576;
            LabelFromHistorical.Content = OsLocalization.Trader.Label577;
            LabelCountHistorical.Content = OsLocalization.Trader.Label578;
        }

        private void AddRemoteDataPlaceholders()
        {

            AddPlaceholder(HostActiveOrders, OsLocalization.Trader.Label189);
            AddPlaceholder(HostHistoricalOrders, OsLocalization.Trader.Label190);
            AddPlaceholder(HostServerLog, OsLocalization.Trader.Label37);
        }

        private void CreatePrimeLogGrid()
        {
            if (_primeLogGrid == null)
            {
                _primeLogGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
                _primeLogGrid.ScrollBars = ScrollBars.Vertical;
                AddServerLogColumn(_primeLogGrid, OsLocalization.Logging.Column1, 200);
                AddServerLogColumn(_primeLogGrid, OsLocalization.Logging.Column2, 100);
                AddServerLogColumn(_primeLogGrid, OsLocalization.Logging.Column3, 0);
            }
            HostBotLogPrime.Child = _primeLogGrid;
        }

        private void CreateRemoteServersGrid()
        {
            if (_serversGrid == null)
            {
                _serversGrid = DataGridFactory.GetDataGridServers();
                _serversGrid.ScrollBars = ScrollBars.Vertical;
                _serversGrid.CellMouseClick += ServersGrid_CellMouseClick;
                _serversGrid.DoubleClick += ServersGrid_DoubleClick;
            }
            HostServers.Child = _serversGrid;
            if (_serverLogGrid == null)
            {
                _serverLogGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
                _serverLogGrid.ScrollBars = ScrollBars.Vertical;
                AddServerLogColumn(_serverLogGrid, OsLocalization.Logging.Column1, 200);
                AddServerLogColumn(_serverLogGrid, OsLocalization.Logging.Column2, 100);
                AddServerLogColumn(_serverLogGrid, OsLocalization.Logging.Column3, 400);
            }
            HostServerLog.Child = _serverLogGrid;
            CheckBoxServerAutoOpen.IsEnabled = false;
            CheckBoxServerAutoOpen.ToolTip = "Connect saved VPS connectors automatically when the server starts.";
            CheckBoxServerAutoOpen.Click -= CheckBoxServerAutoOpen_Click;
            CheckBoxServerAutoOpen.Click += CheckBoxServerAutoOpen_Click;
            LoadAttachedRemoteServers();
        }

        private static void AddServerLogColumn(DataGridView grid, string header, int width)
        {
            DataGridViewTextBoxCell template = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn { CellTemplate = template, HeaderText = header, ReadOnly = true };
            if (grid.Columns.Count == 2) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else column.Width = width;
            grid.Columns.Add(column);
        }

        private void LoadAttachedRemoteServers()
        {
            _attachedRemoteServerTypes.Clear();
            try
            {
                string path = System.IO.Path.Combine("Engine", "AttachedServers.txt");
                if (!System.IO.File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllLines(path))
                    if (!string.IsNullOrWhiteSpace(line)) _attachedRemoteServerTypes.Add(line.Trim());
            }
            catch { }
        }

        private void SaveAttachedRemoteServers()
        {
            try
            {
                System.IO.Directory.CreateDirectory("Engine");
                System.IO.File.WriteAllLines(System.IO.Path.Combine("Engine", "AttachedServers.txt"), _attachedRemoteServerTypes);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "VPS"); }
        }

        private static void AddPlaceholder(System.Windows.Forms.Integration.WindowsFormsHost host, string title)
        {
            if (host == null || host.Child != null) return;
            System.Windows.Forms.Label label = new System.Windows.Forms.Label
            {
                Text = title + " — VPS data source is not connected yet",
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                ForeColor = System.Drawing.Color.Gray
            };
            host.Child = label;
        }

        private static void ClearHost(System.Windows.Forms.Integration.WindowsFormsHost host)
        {
            if (host != null) host.Child = null;
        }

        private void GreedChartPanel_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (GreedChartPanel.Cursor == System.Windows.Input.Cursors.ScrollN)
            {
                GridPrime.RowDefinitions[1].Height = new GridLength(500, GridUnitType.Pixel);
                HistoricalListPanel.Visibility = Visibility.Visible;
                ActiveListPanel.Visibility = Visibility.Visible;
                HostActiveOrders.Margin = new Thickness(0, 28, 0, 0);
                HostHistoricalOrders.Margin = new Thickness(0, 28, 0, 0);
            }
            else if (GreedChartPanel.Cursor == System.Windows.Input.Cursors.ScrollS)
            {
                GridPrime.RowDefinitions[1].Height = new GridLength(190, GridUnitType.Pixel);
                HistoricalListPanel.Visibility = Visibility.Collapsed;
                ActiveListPanel.Visibility = Visibility.Collapsed;
                HostActiveOrders.Margin = new Thickness(0);
                HostHistoricalOrders.Margin = new Thickness(0);
            }
        }

        private void GreedChartPanel_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            GreedChartPanel.Cursor = GridPrime.RowDefinitions[1].Height.Value == 190
                ? System.Windows.Input.Cursors.ScrollN : System.Windows.Input.Cursors.ScrollS;
        }

        private void GreedChartPanel_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            GreedChartPanel.Cursor = System.Windows.Input.Cursors.Arrow;
        }

        private void ImagePadlock_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RobotUiLightBlock dialog = new RobotUiLightBlock();
            dialog.ShowDialog();
            if (dialog.InterfaceIsBlock)
            {
                GreedChartPanel.IsEnabled = false;
                TabControlPrime.IsEnabled = false;
                ImagePadlock.Visibility = Visibility.Hidden;
                ImagePadlockOpen.Visibility = Visibility.Visible;
            }
        }

        private void ImagePadlockOpen_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            RobotsUiLightUnblock dialog = new RobotsUiLightUnblock();
            dialog.ShowDialog();
            if (dialog.IsUnBlocked)
            {
                GreedChartPanel.IsEnabled = true;
                TabControlPrime.IsEnabled = true;
                ImagePadlock.Visibility = Visibility.Visible;
                ImagePadlockOpen.Visibility = Visibility.Hidden;
            }
        }

        private void ImagePadlock_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ImagePadlock.Cursor = System.Windows.Input.Cursors.Hand;
        private void ImagePadlock_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ImagePadlock.Cursor = System.Windows.Input.Cursors.Arrow;
        private void ImagePadlockOpen_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => ImagePadlockOpen.Cursor = System.Windows.Input.Cursors.Hand;
        private void ImagePadlockOpen_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => ImagePadlockOpen.Cursor = System.Windows.Input.Cursors.Arrow;

        private void ButtonSupportTable_Click(object sender, RoutedEventArgs e) => new SupportTableUi().ShowDialog();
        private void ButtonProxy_Click(object sender, RoutedEventArgs e) => OsEngine.Market.ServerMaster.ShowProxyDialog();
        private void ButtonSystemStress_Click(object sender, RoutedEventArgs e) => OsEngine.OsTrader.SystemAnalyze.SystemUsageAnalyzeMaster.ShowDialog();
        private void ButtonServerAvailability_Click(object sender, RoutedEventArgs e) => OsEngine.OsTrader.ServerAvailability.ServerAvailabilityMaster.ShowDialog();

        private void VpsRemoteSession_ClientChanged(RemoteMcpClient client)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => SetClient(client)));
                return;
            }
            SetClient(client);
        }

        private void SetClient(RemoteMcpClient client)
        {
            _client = client;
            _tabNameCache.Clear();
            CheckBoxServerAutoOpen.IsEnabled = client != null && client.IsConnected;
            CheckBoxServerAutoOpen.IsChecked = false;
            if (client != null && client.IsConnected) _ = LoadAutoConnectAsync(client);
            _pollTimer?.Stop();
            _pollTimer = null;
            _portfolioPollTimer?.Stop();
            _portfolioPollTimer = null;
            _positionsPollTimer?.Stop();
            _positionsPollTimer = null;
            _ordersPollTimer?.Stop();
            _ordersPollTimer = null;
            _serversPollTimer?.Stop();
            _serversPollTimer = null;
            _primeLogPollTimer?.Stop();
            _primeLogPollTimer = null;
            if (_robotsGrid != null) RenderBots(new List<VpsBotRow>());
            if (_portfolioGrid != null) _portfolioGrid.Rows.Clear();
            ShowGridStatus(_activePositionsGrid, "VPS is not connected.");
            ShowGridStatus(_stopLimitPositionsGrid, "VPS is not connected.");
            ShowGridStatus(_closedPositionsGrid, "VPS is not connected.");
            ShowGridStatus(_activeOrdersGrid, "VPS is not connected.");
            ShowGridStatus(_historicalOrdersGrid, "VPS is not connected.");
            ShowGridStatus(_serversGrid, "VPS is not connected.");
            ShowGridStatus(_serverLogGrid, "VPS is not connected.");
            ShowGridStatus(_primeLogGrid, "VPS is not connected.");

            if (client == null || !client.IsConnected) return;
            _serversPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _serversPollTimer.Tick += async (s, args) => await RefreshRemoteServersAsync().ConfigureAwait(true);
            _serversPollTimer.Start();
            _ = RefreshRemoteServersAsync();
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _pollTimer.Tick += async (s, args) => await RefreshBotsAsync().ConfigureAwait(true);
            _pollTimer.Start();
            _ = RefreshBotsAsync();
            _portfolioPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _portfolioPollTimer.Tick += async (s, args) => await RefreshPortfoliosAsync().ConfigureAwait(true);
            _portfolioPollTimer.Start();
            _ = RefreshPortfoliosAsync();
            _positionsPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _positionsPollTimer.Tick += async (s, args) => await RefreshPositionsAsync().ConfigureAwait(true);
            _positionsPollTimer.Start();
            _ = RefreshPositionsAsync();
            _ordersPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _ordersPollTimer.Tick += async (s, args) =>
            {
                if (TabAllOrders.IsSelected) await RefreshOrdersAsync().ConfigureAwait(true);
            };
            _ordersPollTimer.Start();
            if (TabAllOrders.IsSelected) _ = RefreshOrdersAsync();
            _primeLogPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _primeLogPollTimer.Tick += async (s, args) =>
            {
                if (TabItemLogPrime.IsSelected) await RefreshPrimeLogAsync().ConfigureAwait(true);
            };
            _primeLogPollTimer.Start();
            if (TabItemLogPrime.IsSelected) _ = RefreshPrimeLogAsync();
        }

        private async System.Threading.Tasks.Task LoadAutoConnectAsync(RemoteMcpClient client)
        {
            try
            {
                JsonElement response = await client.CallToolAsync("server_management_get_auto_connect", null).ConfigureAwait(true);
                if (client != _client) return;
                _loadingAutoConnect = true;
                CheckBoxServerAutoOpen.IsChecked = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
                CheckBoxServerAutoOpen.IsEnabled = true;
            }
            catch (Exception ex)
            {
                if (client == _client) { CheckBoxServerAutoOpen.IsEnabled = false; CheckBoxServerAutoOpen.ToolTip = "Could not read remote auto-connect setting: " + ex.Message; }
            }
            finally { _loadingAutoConnect = false; }
        }

        private async void CheckBoxServerAutoOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_loadingAutoConnect || _client == null || !_client.IsConnected) return;
            bool requested = CheckBoxServerAutoOpen.IsChecked == true;
            CheckBoxServerAutoOpen.IsEnabled = false;
            try
            {
                JsonElement response = await _client.CallToolAsync("server_management_set_auto_connect", new { enabled = requested }).ConfigureAwait(true);
                CheckBoxServerAutoOpen.IsChecked = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex) { CheckBoxServerAutoOpen.IsChecked = !requested; System.Windows.MessageBox.Show("Could not save VPS auto-connect setting: " + ex.Message, "VPS"); }
            finally { CheckBoxServerAutoOpen.IsEnabled = _client != null && _client.IsConnected; }
        }

        private void TabControlPrime_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabPortfolios.IsSelected)
                _ = RefreshPortfoliosAsync();
            if (TabAllOrders.IsSelected)
                _ = RefreshOrdersAsync();
            if (TabItemControl.IsSelected)
                _ = RefreshRemoteServersAsync();
            if (TabItemLogPrime.IsSelected)
                _ = RefreshPrimeLogAsync();
        }

        private async System.Threading.Tasks.Task RefreshPrimeLogAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _primeLogGrid == null || _primeLogPollInFlight) return;
            _primeLogPollInFlight = true;
            try
            {
                JsonElement result = await client.CallToolAsync("log_get_prime_log", new { count = 500 }).ConfigureAwait(true);
                if (client != _client || result.ValueKind != JsonValueKind.Array) return;
                int firstVisible = _primeLogGrid.Rows.Count > 0 ? _primeLogGrid.FirstDisplayedScrollingRowIndex : -1;
                _primeLogGrid.Rows.Clear();
                foreach (JsonElement message in result.EnumerateArray())
                    _primeLogGrid.Rows.Add(FormatRemoteTime(ReadString(message, "time")), ReadString(message, "type"), ReadString(message, "message"));
                RestoreGridScroll(_primeLogGrid, firstVisible);
            }
            catch (Exception ex)
            {
                if (client == _client) ShowGridStatus(_primeLogGrid, "VPS prime log unavailable: " + ex.Message);
            }
            finally { _primeLogPollInFlight = false; }
        }

        private async System.Threading.Tasks.Task RefreshRemoteServersAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _serversGrid == null || _serversPollInFlight) return;
            _serversPollInFlight = true;
            try
            {
                JsonElement instances = await client.CallToolAsync("server_management_get_list", null).ConfigureAwait(true);
                JsonElement tradeTypes = await client.CallToolAsync("server_management_get_trade_connectors", null).ConfigureAwait(true);
                if (client != _client) return;
                RenderRemoteServers(instances, tradeTypes);
                if (TabItemControl.IsSelected) await RefreshRemoteServerLogAsync(client, instances).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (client == _client) ShowGridStatus(_serversGrid, "VPS servers unavailable: " + ex.Message);
            }
            finally { _serversPollInFlight = false; }
        }

        private async System.Threading.Tasks.Task RefreshRemoteServerLogAsync(RemoteMcpClient client, JsonElement instances)
        {
            if (_serverLogGrid == null || instances.ValueKind != JsonValueKind.Array) return;
            JsonElement server = instances.EnumerateArray().FirstOrDefault(item =>
                string.Equals(ReadString(item, "status"), "Connect", StringComparison.OrdinalIgnoreCase));
            if (server.ValueKind != JsonValueKind.Object) server = instances.EnumerateArray().FirstOrDefault();
            string type = ReadString(server, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                _serverLogGrid.Rows.Clear();
                return;
            }
            JsonElement result = await client.CallToolAsync("server_instance_get_log", new
            {
                type = type,
                number = ReadInt(server, "number"),
                count = 200
            }).ConfigureAwait(true);
            if (client != _client || !result.TryGetProperty("messages", out JsonElement messages)
                || messages.ValueKind != JsonValueKind.Array) return;
            int firstVisible = _serverLogGrid.Rows.Count > 0 ? _serverLogGrid.FirstDisplayedScrollingRowIndex : -1;
            _serverLogGrid.Rows.Clear();
            foreach (JsonElement message in messages.EnumerateArray())
                _serverLogGrid.Rows.Add(ReadString(message, "time"), ReadString(message, "type"), ReadString(message, "message"));
            if (firstVisible >= 0 && firstVisible < _serverLogGrid.Rows.Count)
                _serverLogGrid.FirstDisplayedScrollingRowIndex = firstVisible;
        }

        private void RenderRemoteServers(JsonElement instances, JsonElement tradeTypes)
        {
            int firstVisible = _serversGrid.Rows.Count > 0 ? _serversGrid.FirstDisplayedScrollingRowIndex : -1;
            _serversGrid.Rows.Clear();
            List<(string Type, string Name)> rows = new List<(string, string)>();
            if (instances.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in instances.EnumerateArray())
                {
                    string type = ReadString(item, "type");
                    string name = ReadString(item, "name");
                    if (string.Equals(type, "Optimizer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(type)) rows.Add((type, string.IsNullOrWhiteSpace(name) ? type : name));
                }
            }
            foreach (JsonElement types in new[] { tradeTypes })
            {
                if (types.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement item in types.EnumerateArray())
                {
                    string type = item.ValueKind == JsonValueKind.String ? item.GetString() : string.Empty;
                    if (string.Equals(type, "Optimizer", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(type) && !rows.Exists(row => string.Equals(row.Type, type, StringComparison.OrdinalIgnoreCase)))
                        rows.Add((type, type));
                }
            }
            rows = rows.OrderByDescending(row => _attachedRemoteServerTypes.Contains(row.Type)).ToList();
            foreach (var item in rows)
            {
                JsonElement instance = instances.ValueKind == JsonValueKind.Array
                    ? instances.EnumerateArray().FirstOrDefault(server => string.Equals(ReadString(server, "type"), item.Type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ReadString(server, "name"), item.Name, StringComparison.OrdinalIgnoreCase)) : default;
                bool exists = instance.ValueKind == JsonValueKind.Object;
                string status = exists ? ReadString(instance, "status") : "Disabled";
                DataGridViewRow row = new DataGridViewRow();
                row.Cells.Add(new DataGridViewTextBoxCell { Value = item.Name });
                row.Cells.Add(new DataGridViewTextBoxCell { Value = status });
                if (_attachedRemoteServerTypes.Contains(item.Type))
                {
                    try
                    {
                        string pin = System.IO.Path.Combine(System.Windows.Forms.Application.StartupPath, "Images", "pinBar.png");
                        if (System.IO.File.Exists(pin))
                        {
                            DataGridViewImageCell image = new DataGridViewImageCell { Value = new System.Drawing.Bitmap(pin) };
                            image.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                            row.Cells.Add(image);
                        }
                    }
                    catch { }
                }
                row.Tag = new RemoteServerRow { Type = item.Type, Name = item.Name, Number = exists ? ReadInt(instance, "number") : 0, Exists = exists };
                _serversGrid.Rows.Add(row);
                if (exists)
                {
                    System.Drawing.Color color = string.Equals(status, "Connect", StringComparison.OrdinalIgnoreCase)
                        ? System.Drawing.Color.MediumSeaGreen : System.Drawing.Color.Coral;
                    row.Cells[0].Style.BackColor = color;
                    row.Cells[0].Style.ForeColor = System.Drawing.Color.Black;
                    row.Cells[1].Style.BackColor = color;
                    row.Cells[1].Style.ForeColor = System.Drawing.Color.Black;
                    row.Cells[0].Style.SelectionBackColor = color;
                    row.Cells[1].Style.SelectionBackColor = color;
                }
            }
            if (firstVisible >= 0 && firstVisible < _serversGrid.Rows.Count)
                _serversGrid.FirstDisplayedScrollingRowIndex = firstVisible;
            UpdateServerSearchResults();
        }

        private void ServerSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateServerSearchResults(true);

        private void UpdateServerSearchResults(bool resetSelection = false)
        {
            _serverSearchResults.Clear();
            string query = TextBoxSearchSource.Text?.Trim() ?? string.Empty;
            if (string.Equals(query, OsLocalization.Market.Label64, StringComparison.CurrentCultureIgnoreCase)) query = string.Empty;
            if (query.Length == 0)
            {
                ButtonLeftInSearchResults.Visibility = Visibility.Hidden;
                ButtonRightInSearchResults.Visibility = Visibility.Hidden;
                LabelCurrentResultShow.Visibility = Visibility.Hidden;
                LabelCommasResultShow.Visibility = Visibility.Hidden;
                LabelCountResultsShow.Visibility = Visibility.Hidden;
                return;
            }
            for (int i = 0; i < (_serversGrid?.Rows.Count ?? 0); i++)
            {
                string name = _serversGrid.Rows[i].Cells[0].Value?.ToString() ?? string.Empty;
                if (name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    _serverSearchResults.Add(i);
            }
            if (_serverSearchResults.Count == 0)
            {
                ButtonLeftInSearchResults.Visibility = Visibility.Hidden;
                ButtonRightInSearchResults.Visibility = Visibility.Hidden;
                LabelCurrentResultShow.Visibility = Visibility.Hidden;
                LabelCommasResultShow.Visibility = Visibility.Hidden;
                LabelCountResultsShow.Visibility = Visibility.Hidden;
                return;
            }
            bool many = _serverSearchResults.Count > 1;
            ButtonLeftInSearchResults.Visibility = many ? Visibility.Visible : Visibility.Hidden;
            ButtonRightInSearchResults.Visibility = many ? Visibility.Visible : Visibility.Hidden;
            LabelCurrentResultShow.Visibility = many ? Visibility.Visible : Visibility.Hidden;
            LabelCommasResultShow.Visibility = many ? Visibility.Visible : Visibility.Hidden;
            LabelCountResultsShow.Visibility = many ? Visibility.Visible : Visibility.Hidden;
            int current = resetSelection || !int.TryParse(LabelCurrentResultShow.Content?.ToString(), out int resultIndex)
                ? 0 : Math.Min(Math.Max(0, resultIndex - 1), _serverSearchResults.Count - 1);
            LabelCurrentResultShow.Content = (current + 1).ToString();
            LabelCountResultsShow.Content = _serverSearchResults.Count.ToString();
            SelectServerSearchResult(current);
        }

        private void SelectServerSearchResult(int index)
        {
            if (index < 0 || index >= _serverSearchResults.Count) return;
            int row = _serverSearchResults[index];
            _serversGrid.ClearSelection();
            _serversGrid.Rows[row].Selected = true;
            if (row < _serversGrid.Rows.Count && row >= 0) _serversGrid.FirstDisplayedScrollingRowIndex = row;
            LabelCurrentResultShow.Content = (index + 1).ToString();
        }

        private void ServerSearchPrevious_Click(object sender, RoutedEventArgs e)
        {
            int current = int.TryParse(LabelCurrentResultShow.Content?.ToString(), out int value) ? value - 1 : 0;
            SelectServerSearchResult((current - 1 + _serverSearchResults.Count) % Math.Max(1, _serverSearchResults.Count));
        }

        private void ServerSearchNext_Click(object sender, RoutedEventArgs e)
        {
            int current = int.TryParse(LabelCurrentResultShow.Content?.ToString(), out int value) ? value - 1 : 0;
            SelectServerSearchResult((current + 1) % Math.Max(1, _serverSearchResults.Count));
        }

        private sealed class RemoteServerRow
        {
            public string Type;
            public string Name;
            public int Number;
            public bool Exists;
        }

        private void ServersGrid_CellMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= _serversGrid.Rows.Count) return;
            RemoteServerRow row = _serversGrid.Rows[e.RowIndex].Tag as RemoteServerRow;
            if (row == null) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem title = new ToolStripMenuItem(row.Name) { Enabled = false };
            menu.Items.Add(title);
            ToolStripMenuItem settings = new ToolStripMenuItem(OsLocalization.Market.Label119);
            settings.Click += async (s, args) => await OpenRemoteServerParametersAsync(row).ConfigureAwait(true);
            menu.Items.Add(settings);
            ToolStripMenuItem attach = new ToolStripMenuItem(_attachedRemoteServerTypes.Contains(row.Type) ? OsLocalization.Market.Label118 : OsLocalization.Market.Label117);
            attach.Click += (s, args) =>
            {
                if (!_attachedRemoteServerTypes.Add(row.Type)) _attachedRemoteServerTypes.Remove(row.Type);
                SaveAttachedRemoteServers();
                _ = RefreshRemoteServersAsync();
            };
            menu.Items.Add(attach);
            ToolStripMenuItem command = new ToolStripMenuItem(row.Exists && string.Equals(_serversGrid.Rows[e.RowIndex].Cells[1].Value?.ToString(), "Connect", StringComparison.OrdinalIgnoreCase)
                ? OsLocalization.Market.ButtonDisconnect : OsLocalization.Market.ButtonConnect);
            command.Click += async (s, args) => await RunRemoteServerCommandAsync(row);
            menu.Items.Add(command);
            menu.Show(_serversGrid, _serversGrid.PointToClient(System.Windows.Forms.Control.MousePosition));
        }

        private async System.Threading.Tasks.Task RunRemoteServerCommandAsync(RemoteServerRow row)
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected) return;
            try
            {
                if (!row.Exists)
                    await client.CallToolAsync("server_management_activate", new { type = row.Type }).ConfigureAwait(true);
                else
                {
                    string status = await GetRemoteServerStatusAsync(client, row).ConfigureAwait(true);
                    await client.CallToolAsync(string.Equals(status, "Connect", StringComparison.OrdinalIgnoreCase)
                        ? "server_instance_disconnect" : "server_instance_connect", new { type = row.Type, number = row.Number }).ConfigureAwait(true);
                }
                await RefreshRemoteServersAsync().ConfigureAwait(true);
            }
            catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "VPS"); }
        }

        private static async System.Threading.Tasks.Task<string> GetRemoteServerStatusAsync(RemoteMcpClient client, RemoteServerRow row)
        {
            JsonElement status = await client.CallToolAsync("server_instance_get_status", new { type = row.Type, number = row.Number }).ConfigureAwait(true);
            return ReadString(status, "status");
        }

        private async void ServersGrid_DoubleClick(object sender, EventArgs e)
        {
            if (_serversGrid?.CurrentRow?.Tag is RemoteServerRow row)
                await OpenRemoteServerParametersAsync(row).ConfigureAwait(true);
        }

        private async System.Threading.Tasks.Task OpenRemoteServerParametersAsync(RemoteServerRow row)
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected) return;
            try
            {
                if (!row.Exists)
                {
                    JsonElement activated = await client.CallToolAsync("server_management_activate", new { type = row.Type }).ConfigureAwait(true);
                    JsonElement instance = activated.ValueKind == JsonValueKind.Array ? activated.EnumerateArray().FirstOrDefault() : default;
                    if (instance.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("The VPS did not return the activated server instance.");
                    row.Number = ReadInt(instance, "number");
                    row.Name = ReadString(instance, "name");
                    row.Exists = true;
                }

                if (client != _client) return;
                RobotsVpsServerParametersUi window = new RobotsVpsServerParametersUi(client, row.Type, row.Number, row.Name)
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
                await RefreshRemoteServersAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open VPS server settings: " + ex.Message, "VPS");
            }
        }

        private void CreateOrderTables()
        {
            if (_activeOrdersGrid == null)
                _activeOrdersGrid = CreateOrderGrid();
            if (_historicalOrdersGrid == null)
                _historicalOrdersGrid = CreateOrderGrid();
            HostActiveOrders.Child = _activeOrdersGrid;
            HostHistoricalOrders.Child = _historicalOrdersGrid;
        }

        private static DataGridView CreateOrderGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridOrder();
            grid.ScrollBars = ScrollBars.Vertical;
            for (int i = 1; i < grid.Columns.Count; i++)
                grid.Columns[i].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            return grid;
        }

        private async System.Threading.Tasks.Task RefreshOrdersAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _ordersPollInFlight
                || _activeOrdersGrid == null || _historicalOrdersGrid == null)
                return;

            _ordersPollInFlight = true;
            try
            {
                JsonElement servers = await client.CallToolAsync("server_management_get_list", null).ConfigureAwait(true);
                JsonElement server = servers.ValueKind == JsonValueKind.Array
                    ? servers.EnumerateArray().FirstOrDefault(item =>
                        ReadString(item, "status") == "Connect")
                    : default;
                if (server.ValueKind != JsonValueKind.Object && servers.ValueKind == JsonValueKind.Array)
                    server = servers.EnumerateArray().FirstOrDefault();
                string serverType = ReadString(server, "type");
                int serverNumber = ReadInt(server, "number");
                if (string.IsNullOrEmpty(serverType))
                {
                    ShowGridStatus(_activeOrdersGrid, "No VPS trade server is available.");
                    ShowGridStatus(_historicalOrdersGrid, "No VPS trade server is available.");
                    return;
                }

                JsonElement active = await client.CallToolAsync("server_instance_get_active_orders",
                    new { type = serverType, number = serverNumber, offset = 0, limit = 100 }).ConfigureAwait(true);
                if (client != _client) return;
                JsonElement historical = await client.CallToolAsync("server_instance_get_historical_orders",
                    new { type = serverType, number = serverNumber, offset = 0, limit = 100 }).ConfigureAwait(true);
                if (client != _client) return;
                RenderOrderRows(_activeOrdersGrid, active);
                RenderOrderRows(_historicalOrdersGrid, historical);
            }
            catch (Exception ex)
            {
                if (client == _client)
                {
                    ShowGridStatus(_activeOrdersGrid, "VPS active orders unavailable: " + ex.Message);
                    ShowGridStatus(_historicalOrdersGrid, "VPS historical orders unavailable: " + ex.Message);
                }
            }
            finally { _ordersPollInFlight = false; }
        }

        private static void RenderOrderRows(DataGridView grid, JsonElement response)
        {
            int firstVisible = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
            grid.Rows.Clear();
            if (!response.TryGetProperty("orders", out JsonElement orders) || orders.ValueKind != JsonValueKind.Array)
            {
                ShowGridStatus(grid, "VPS returned an unexpected orders response.");
                return;
            }
            foreach (JsonElement order in orders.EnumerateArray())
            {
                DataGridViewRow row = CreateTextRow(grid.Columns.Count);
                row.Cells[0].Value = ReadInt(order, "number_user");
                row.Cells[1].Value = ReadString(order, "number_market");
                row.Cells[2].Value = FormatRemoteTime(ReadString(order, "time_create"));
                row.Cells[3].Value = ReadString(order, "security");
                row.Cells[4].Value = ReadString(order, "portfolio");
                row.Cells[5].Value = ReadString(order, "side");
                row.Cells[6].Value = ReadString(order, "state");
                row.Cells[7].Value = FormatRemoteNumber(ReadDecimal(order, "price"));
                row.Cells[8].Value = FormatRemoteNumber(ReadDecimal(order, "price_real"));
                row.Cells[9].Value = FormatRemoteNumber(ReadDecimal(order, "volume"));
                row.Cells[10].Value = ReadString(order, "type");
                row.Cells[11].Value = ReadString(order, "round_trip");
                grid.Rows.Add(row);
            }
            RestoreGridScroll(grid, firstVisible);
        }

        private void CreatePositionTables()
        {
            if (_activePositionsGrid == null)
            {
                _activePositionsGrid = DataGridFactory.GetDataGridPosition();
                // 1:1 с OsTrader/GlobalPositionViewer._gridAllPositions_Click (6 пунктов, тот же порядок/подписи).
                _activePositionsGrid.Click += ActivePositionsGrid_Click;
            }
            if (_stopLimitPositionsGrid == null)
            {
                _stopLimitPositionsGrid = DataGridFactory.GetDataGridBuyAtStopPositions();
                // 1:1 с OsTrader/BuyAtStopPositionsViewer._grid_Click (2 пункта).
                _stopLimitPositionsGrid.Click += StopLimitPositionsGrid_Click;
            }
            if (_closedPositionsGrid == null)
            {
                _closedPositionsGrid = DataGridFactory.GetDataGridPosition();
                // 1:1 с OsTrader/GlobalPositionViewer._gridClosePoses_Click (1 пункт).
                _closedPositionsGrid.Click += ClosedPositionsGrid_Click;
            }

            _activePositionsGrid.ScrollBars = ScrollBars.Vertical;
            _stopLimitPositionsGrid.ScrollBars = ScrollBars.Vertical;
            _closedPositionsGrid.ScrollBars = ScrollBars.Vertical;
            HostActivePoses.Child = _activePositionsGrid;
            HostStopLimitPoses.Child = _stopLimitPositionsGrid;
            HostHistoricalPoses.Child = _closedPositionsGrid;
        }

        private async System.Threading.Tasks.Task RefreshPositionsAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _positionsPollInFlight
                || _activePositionsGrid == null || _stopLimitPositionsGrid == null || _closedPositionsGrid == null)
                return;

            _positionsPollInFlight = true;
            try
            {
                JsonElement active = await client.CallToolAsync("bot_journal_get_open_positions", null).ConfigureAwait(true);
                if (client != _client) return;
                JsonElement stops = await client.CallToolAsync("bot_journal_get_stop_limit_positions", null).ConfigureAwait(true);
                if (client != _client) return;
                JsonElement closed = await client.CallToolAsync("bot_journal_get_closed_positions", null).ConfigureAwait(true);
                if (client != _client) return;

                RenderPositionRows(_activePositionsGrid, active, false);
                RenderStopLimitRows(_stopLimitPositionsGrid, stops);
                RenderPositionRows(_closedPositionsGrid, closed, true);
            }
            catch (Exception ex)
            {
                string status = "VPS positions unavailable: " + ex.Message;
                if (client == _client)
                {
                    ShowGridStatus(_activePositionsGrid, status);
                    ShowGridStatus(_stopLimitPositionsGrid, status);
                    ShowGridStatus(_closedPositionsGrid, status);
                }
            }
            finally
            {
                _positionsPollInFlight = false;
            }
        }

        private static void RenderPositionRows(DataGridView grid, JsonElement response, bool closed)
        {
            if (grid == null) return;
            int firstVisible = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
            grid.Rows.Clear();
            if (!response.TryGetProperty("positions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array)
            {
                ShowGridStatus(grid, "VPS returned an unexpected positions response.");
                return;
            }

            foreach (JsonElement position in positions.EnumerateArray())
            {
                DataGridViewRow row = CreateTextRow(grid.Columns.Count);
                string opened = FormatRemoteTime(ReadString(position, "time_create"));
                string closedAt = closed ? FormatRemoteTime(ReadString(position, "close_time")) : string.Empty;
                row.Cells[0].Value = ReadInt(position, "number");
                row.Cells[1].Value = opened;
                row.Cells[2].Value = closedAt;
                row.Cells[3].Value = ReadString(position, "bot_name");
                row.Cells[4].Value = ReadString(position, "security_name");
                string side = ReadString(position, "side");
                if (string.IsNullOrEmpty(side))
                {
                    string direction = ReadString(position, "direction");
                    side = direction == "Long" ? "Buy" : direction == "Short" ? "Sell" : direction;
                }
                row.Cells[5].Value = side;
                row.Cells[6].Value = ReadString(position, "state");
                row.Cells[7].Value = FormatRemoteNumber(ReadDecimal(position, "volume"));
                row.Cells[8].Value = FormatRemoteNumber(ReadDecimal(position, "open_volume"));
                row.Cells[9].Value = FormatRemoteNumber(ReadDecimal(position, "wait_volume"));
                row.Cells[10].Value = FormatPositionPrice(position, "entry_price");
                row.Cells[11].Value = FormatPositionPrice(position, "close_price");
                row.Cells[12].Value = FormatPositionPrice(position, "profit_abs");
                row.Cells[13].Value = FormatPositionPrice(position, "stop_order_red_line");
                row.Cells[14].Value = FormatPositionPrice(position, "stop_order_price");
                row.Cells[15].Value = FormatPositionPrice(position, "profit_order_red_line");
                row.Cells[16].Value = FormatPositionPrice(position, "profit_order_price");
                row.Cells[17].Value = ReadString(position, "signal_type_open");
                row.Cells[18].Value = ReadString(position, "signal_type_close");
                grid.Rows.Add(row);
            }
            RestoreGridScroll(grid, firstVisible);
        }

        private static void RenderStopLimitRows(DataGridView grid, JsonElement response)
        {
            if (grid == null) return;
            int firstVisible = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
            grid.Rows.Clear();
            if (!response.TryGetProperty("positions", out JsonElement positions) || positions.ValueKind != JsonValueKind.Array)
            {
                ShowGridStatus(grid, "VPS returned an unexpected stop-limit response.");
                return;
            }

            foreach (JsonElement position in positions.EnumerateArray())
            {
                DataGridViewRow row = CreateTextRow(grid.Columns.Count);
                row.Cells[0].Value = ReadInt(position, "number");
                row.Cells[1].Value = FormatRemoteTime(ReadString(position, "time_create"));
                row.Cells[2].Value = ReadString(position, "tab_name");
                row.Cells[3].Value = ReadString(position, "security_name");
                row.Cells[4].Value = FormatRemoteNumber(ReadDecimal(position, "volume"));
                string side = ReadString(position, "side");
                if (string.IsNullOrEmpty(side))
                {
                    string direction = ReadString(position, "direction");
                    side = direction == "Long" ? "Buy" : direction == "Short" ? "Sell" : direction;
                }
                row.Cells[5].Value = side;
                row.Cells[6].Value = ReadString(position, "activate_type");
                row.Cells[7].Value = FormatRemoteNumber(ReadDecimal(position, "price_red_line"));
                row.Cells[8].Value = FormatRemoteNumber(ReadDecimal(position, "price_order"));
                row.Cells[9].Value = ReadInt(position, "expires_bars");
                row.Cells[10].Value = ReadString(position, "lifetime_type");
                grid.Rows.Add(row);
            }
            RestoreGridScroll(grid, firstVisible);
        }

        private static void ShowGridStatus(DataGridView grid, string status)
        {
            if (grid == null) return;
            grid.Rows.Clear();
            int rowIndex = grid.Rows.Add();
            grid.Rows[rowIndex].Cells[0].Value = status;
        }

        private static DataGridViewRow CreateTextRow(int cellCount)
        {
            DataGridViewRow row = new DataGridViewRow();
            for (int i = 0; i < cellCount; i++)
            {
                row.Cells.Add(new DataGridViewTextBoxCell());
            }
            return row;
        }

        private static void RestoreGridScroll(DataGridView grid, int firstVisible)
        {
            if (firstVisible >= 0 && firstVisible < grid.Rows.Count)
                grid.FirstDisplayedScrollingRowIndex = firstVisible;
        }

        #region Positions tab — right-click actions (1:1 с OsTrader/GlobalPositionViewer и BuyAtStopPositionsViewer)

        // 1:1 с GlobalPositionViewer._gridAllPositions_Click: 6 пунктов, тот же порядок/подписи.
        // RenderPositionRows кладёт bot_name в Cells[3] — этого достаточно (плюс ResolveTabNameAsync),
        // security_name не нужен: bot_position_* требует его только для Screener-вкладок, а
        // ResolveTabNameAsync намеренно берёт только "Simple" источник.
        private void ActivePositionsGrid_Click(object sender, EventArgs e)
        {
            if (!(e is MouseEventArgs mouse) || mouse.Button != MouseButtons.Right) return;
            if (_activePositionsGrid.Rows.Count == 0 || _activePositionsGrid.CurrentCell == null) return;

            int rowIndex = _activePositionsGrid.CurrentCell.RowIndex;
            if (rowIndex < 0 || rowIndex >= _activePositionsGrid.Rows.Count) return;
            DataGridViewRow row = _activePositionsGrid.Rows[rowIndex];

            int positionNumber;
            try { positionNumber = Convert.ToInt32(row.Cells[0].Value); }
            catch { return; }
            string botId = row.Cells[3].Value as string;
            string securityName = row.Cells[4].Value as string;

            ToolStripMenuItem[] items = new ToolStripMenuItem[6];

            items[0] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem1 };
            items[0].Click += (s, args) => _ = ActivePositionCloseAllAsync();

            items[1] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem3 };
            items[1].Click += (s, args) => _ = ActivePositionCloseOneAsync(botId, positionNumber);

            items[2] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem14 };
            items[2].Click += (s, args) => NotAvailableRemotely("Adding to a position"); // PositionAddingUi2 — не портирован

            items[3] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem5 };
            items[3].Click += (s, args) => OpenPositionCloseDialogAsync(botId, securityName, positionNumber, "Stop");

            items[4] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem6 };
            items[4].Click += (s, args) => OpenPositionCloseDialogAsync(botId, securityName, positionNumber, "Profit");

            items[5] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem7 };
            items[5].Click += (s, args) => _ = DeletePositionAsync(botId, positionNumber);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(items);
            _activePositionsGrid.ContextMenuStrip = menu;
            _activePositionsGrid.ContextMenuStrip.Show(_activePositionsGrid, new System.Drawing.Point(mouse.X, mouse.Y));
        }

        // 1:1 с GlobalPositionViewer._gridClosePoses_Click: один пункт — удалить из журнала.
        private void ClosedPositionsGrid_Click(object sender, EventArgs e)
        {
            if (!(e is MouseEventArgs mouse) || mouse.Button != MouseButtons.Right) return;
            if (_closedPositionsGrid.Rows.Count == 0 || _closedPositionsGrid.CurrentCell == null) return;

            int rowIndex = _closedPositionsGrid.CurrentCell.RowIndex;
            if (rowIndex < 0 || rowIndex >= _closedPositionsGrid.Rows.Count) return;
            DataGridViewRow row = _closedPositionsGrid.Rows[rowIndex];

            int positionNumber;
            try { positionNumber = Convert.ToInt32(row.Cells[0].Value); }
            catch { return; }
            string botId = row.Cells[3].Value as string;

            ToolStripMenuItem[] items = new ToolStripMenuItem[1];
            items[0] = new ToolStripMenuItem { Text = OsLocalization.Journal.PositionMenuItem7 };
            items[0].Click += (s, args) => _ = DeletePositionAsync(botId, positionNumber);

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(items);
            _closedPositionsGrid.ContextMenuStrip = menu;
            _closedPositionsGrid.ContextMenuStrip.Show(_closedPositionsGrid, new System.Drawing.Point(mouse.X, mouse.Y));
        }

        // BuyAtStopPositionsViewer._grid_Click в оригинале: 2 пункта ("Удалить все"/"Удалить выбранную").
        // Явная заглушка на ОБА пункта: bot_journal_get_stop_limit_positions не отдаёт bot_name на строку
        // (см. GetJournalStopLimitPositions в RobotsApi.cs — только number/tab_name/security_name/...), а
        // bot_position_*/bot_chart_execute_action требуют bot_id. Без правки серверного DTO бота для
        // конкретной строки надёжно не определить — показываем видимое сообщение, а не гадаем/бьём не туда.
        private void StopLimitPositionsGrid_Click(object sender, EventArgs e)
        {
            if (!(e is MouseEventArgs mouse) || mouse.Button != MouseButtons.Right) return;
            if (_stopLimitPositionsGrid.Rows.Count == 0 || _stopLimitPositionsGrid.CurrentCell == null) return;

            ToolStripMenuItem[] items = new ToolStripMenuItem[2];
            items[0] = new ToolStripMenuItem { Text = OsLocalization.Trader.Label213 };
            items[0].Click += (s, args) => NotAvailableRemotely("Cancelling stop-limit orders");
            items[1] = new ToolStripMenuItem { Text = OsLocalization.Trader.Label214 };
            items[1].Click += (s, args) => NotAvailableRemotely("Cancelling stop-limit orders");

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.AddRange(items);
            _stopLimitPositionsGrid.ContextMenuStrip = menu;
            _stopLimitPositionsGrid.ContextMenuStrip.Show(_stopLimitPositionsGrid, new System.Drawing.Point(mouse.X, mouse.Y));
        }

        private async System.Threading.Tasks.Task ActivePositionCloseAllAsync()
        {
            AcceptDialogUi ui = new AcceptDialogUi(OsLocalization.Journal.Message5) { Owner = Window.GetWindow(this) };
            ui.ShowDialog();
            if (ui.UserAcceptAction == false) return;

            try
            {
                List<(string BotId, int Number)> targets = new();
                foreach (DataGridViewRow row in _activePositionsGrid.Rows)
                {
                    if (row.Cells[0].Value == null || !int.TryParse(row.Cells[0].Value.ToString(), out int number)) continue;
                    string botId = row.Cells[3].Value as string;
                    if (string.IsNullOrWhiteSpace(botId)) continue;
                    targets.Add((botId, number));
                }

                foreach ((string botId, int number) in targets)
                {
                    string tabName = await ResolveTabNameAsync(botId).ConfigureAwait(true);
                    if (string.IsNullOrWhiteSpace(tabName)) continue;
                    await _client.CallToolAsync("bot_position_close_at_market", new { bot_id = botId, tab_name = tabName, position_number = number }).ConfigureAwait(true);
                }

                await RefreshPositionsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not close all positions: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async System.Threading.Tasks.Task ActivePositionCloseOneAsync(string botId, int positionNumber)
        {
            if (string.IsNullOrWhiteSpace(botId)) return;
            try
            {
                string tabName = await ResolveTabNameAsync(botId).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(tabName))
                {
                    System.Windows.MessageBox.Show("For this robot the VPS API did not return a Simple trading source.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await _client.CallToolAsync("bot_position_close_at_market", new { bot_id = botId, tab_name = tabName, position_number = positionNumber }).ConfigureAwait(true);
                await RefreshPositionsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not close the position: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async System.Threading.Tasks.Task DeletePositionAsync(string botId, int positionNumber)
        {
            if (string.IsNullOrWhiteSpace(botId)) return;

            AcceptDialogUi ui = new AcceptDialogUi(OsLocalization.Journal.Message3) { Owner = Window.GetWindow(this) };
            ui.ShowDialog();
            if (ui.UserAcceptAction == false) return;

            try
            {
                string tabName = await ResolveTabNameAsync(botId).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(tabName))
                {
                    System.Windows.MessageBox.Show("For this robot the VPS API did not return a Simple trading source.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await _client.CallToolAsync("bot_position_delete", new { bot_id = botId, tab_name = tabName, position_number = positionNumber }).ConfigureAwait(true);
                await RefreshPositionsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not delete the position: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // "Переставить стоп"/"Переставить профит" — тот же приём, что уже проверен в RobotsVpsChartWindow.
        // OpenPositionCloseDialog: один и тот же диалог (RobotsVpsPositionCloseUi) с разной начальной вкладкой,
        // одно окно на позицию (ключ bot_id+number — на этой вкладке номера позиций не уникальны между ботами).
        private async void OpenPositionCloseDialogAsync(string botId, string securityName, int positionNumber, string initialTab)
        {
            if (string.IsNullOrWhiteSpace(botId)) return;
            string key = botId + ":" + positionNumber;

            if (_positionCloseWindows.TryGetValue(key, out RobotsVpsPositionCloseUi existing) && existing.IsVisible)
            {
                if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                existing.Activate();
                existing.SelectTab(initialTab);
                return;
            }

            try
            {
                string tabName = await ResolveTabNameAsync(botId).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(tabName))
                {
                    System.Windows.MessageBox.Show("For this robot the VPS API did not return a Simple trading source.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                RobotsVpsPositionCloseUi window = new RobotsVpsPositionCloseUi(_client, botId, tabName, securityName, positionNumber) { Owner = Window.GetWindow(this) };
                window.SelectTab(initialTab);
                window.Closed += (s, args) => _positionCloseWindows.Remove(key);
                _positionCloseWindows[key] = window;
                window.Show();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open the position dialog: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void NotAvailableRemotely(string what)
        {
            System.Windows.MessageBox.Show(what + " is not available remotely yet.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        private static string FormatRemoteTime(string value)
        {
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time))
                return time.LocalDateTime.ToString(CultureInfo.CurrentCulture);
            return value;
        }

        private static string FormatRemoteNumber(decimal value) => value.ToString("0.############################", CultureInfo.CurrentCulture);

        private static string FormatPositionPrice(JsonElement position, string propertyName)
        {
            decimal price = ReadDecimal(position, propertyName);
            decimal priceStep = ReadDecimal(position, "price_step");
            if (priceStep <= 0)
                return FormatRemoteNumber(price);

            string step = FormatRemoteNumber(priceStep);
            int decimalIndex = step.IndexOf(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
            int decimals = decimalIndex < 0 ? 0 : step.Length - decimalIndex - CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator.Length;
            return Math.Round(price, decimals + 1).ToString("0.############################", CultureInfo.CurrentCulture);
        }

        private async System.Threading.Tasks.Task RefreshPortfoliosAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _portfolioGrid == null || _portfolioPollInFlight)
                return;

            _portfolioPollInFlight = true;
            try
            {
                JsonElement serverList = await client.CallToolAsync("server_management_get_list", null).ConfigureAwait(true);
                if (client != _client)
                    return;
                if (serverList.ValueKind != JsonValueKind.Array)
                {
                    _portfolioStatus = "VPS returned an unexpected server list response.";
                    RenderPortfolios(new List<RemotePortfolio>());
                    return;
                }

                List<RemotePortfolio> portfolios = new List<RemotePortfolio>();
                List<string> errors = new List<string>();
                int serverCount = 0;
                foreach (JsonElement server in serverList.EnumerateArray())
                {
                    string type = ReadString(server, "type");
                    int number = ReadInt(server, "number");
                    if (string.IsNullOrWhiteSpace(type))
                        continue;
                    serverCount++;

                    try
                    {
                        JsonElement response = await client.CallToolAsync("server_instance_get_portfolios",
                            new { type = type, number = number }).ConfigureAwait(true);
                        if (client != _client || !response.TryGetProperty("portfolios", out JsonElement items)
                            || items.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (JsonElement item in items.EnumerateArray())
                        {
                            RemotePortfolio portfolio = RemotePortfolio.FromJson(item, type, number);
                            if (portfolio.Number != "FinamVirtual")
                                portfolios.Add(portfolio);
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(type + "#" + number + ": " + ex.Message);
                    }
                }

                if (client == _client)
                {
                    _portfolioStatus = portfolios.Count > 0
                        ? null
                        : serverCount == 0
                            ? "VPS returned no server instances."
                            : errors.Count > 0
                                ? "Could not load VPS portfolios: " + string.Join("; ", errors)
                                : "VPS returned no portfolio records.";
                    RenderPortfolios(portfolios.OrderBy(x => x.ServerUniqueName, StringComparer.OrdinalIgnoreCase).ToList());
                }
            }
            catch (Exception ex)
            {
                _portfolioStatus = "Could not load VPS portfolios: " + ex.Message;
                if (client == _client)
                    RenderPortfolios(new List<RemotePortfolio>());
            }
            finally
            {
                _portfolioPollInFlight = false;
            }
        }

        private void RenderPortfolios(List<RemotePortfolio> portfolios)
        {
            if (_portfolioGrid == null)
                return;

            int firstVisibleRow = _portfolioGrid.RowCount > 0 ? _portfolioGrid.FirstDisplayedScrollingRowIndex : -1;
            _portfolioGrid.Rows.Clear();
            foreach (RemotePortfolio portfolio in portfolios)
            {
                bool hasNonZeroPosition = portfolio.Positions.Any(p => p.ValueCurrent != 0);
                if (portfolio.ValueBegin == 0 && portfolio.ValueCurrent == 0 && portfolio.ValueBlocked == 0
                    && !hasNonZeroPosition)
                    continue;

                AddPortfolioSummaryRow(portfolio);
                List<RemotePortfolioPosition> visiblePositions = portfolio.Positions
                    .Where(p => p.ValueBegin != 0 || p.ValueCurrent != 0 || p.ValueBlocked != 0)
                    .ToList();

                if (visiblePositions.Count == 0)
                {
                    DataGridViewRow emptyRow = NewPortfolioRow(7);
                    emptyRow.Cells[6].Value = "No positions";
                    _portfolioGrid.Rows.Add(emptyRow);
                    continue;
                }

                foreach (RemotePortfolioPosition position in visiblePositions)
                {
                    DataGridViewRow row = NewPortfolioRow(12);
                    row.Cells[6].Value = position.SecurityName;
                    row.Cells[7].Value = position.ValueBegin;
                    row.Cells[8].Value = position.ValueCurrent;
                    row.Cells[9].Value = position.ValueBlocked;
                    row.Cells[10].Value = position.UnrealizedPnl;
                    DataGridViewButtonCell action = new DataGridViewButtonCell
                    {
                        Value = OsLocalization.Market.Label82,
                        ToolTipText = "Remote close action is not available in the current VPS UI API."
                    };
                    action.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    row.Cells[11] = action;
                    _portfolioGrid.Rows.Add(row);
                }
            }

            if (_portfolioGrid.Rows.Count == 0 && !string.IsNullOrWhiteSpace(_portfolioStatus))
            {
                DataGridViewRow statusRow = NewPortfolioRow(12);
                statusRow.Cells[0].Value = _portfolioStatus;
                statusRow.Cells[0].ToolTipText = _portfolioStatus;
                _portfolioGrid.Rows.Add(statusRow);
            }

            if (firstVisibleRow >= 0 && firstVisibleRow < _portfolioGrid.Rows.Count)
                _portfolioGrid.FirstDisplayedScrollingRowIndex = firstVisibleRow;
        }

        private void AddPortfolioSummaryRow(RemotePortfolio portfolio)
        {
            DataGridViewRow row = NewPortfolioRow(12);
            row.Cells[0].Value = portfolio.ServerUniqueName;
            row.Cells[1].Value = portfolio.Number;
            row.Cells[2].Value = portfolio.ValueBegin;
            row.Cells[3].Value = portfolio.ValueCurrent;
            row.Cells[4].Value = portfolio.ValueBlocked;
            row.Cells[5].Value = portfolio.UnrealizedPnl;
            DataGridViewButtonCell compare = new DataGridViewButtonCell
            {
                Value = OsLocalization.Market.Label135,
                ToolTipText = OsLocalization.Market.Label135
            };
            compare.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            row.Cells[11] = compare;
            row.Tag = portfolio;
            _portfolioGrid.Rows.Add(row);
        }

        private static DataGridViewRow NewPortfolioRow(int cellCount)
        {
            DataGridViewRow row = new DataGridViewRow();
            for (int i = 0; i < cellCount; i++)
                row.Cells.Add(new DataGridViewTextBoxCell());
            return row;
        }

        private void PortfolioGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 11 || _client == null || !_client.IsConnected)
                return;

            DataGridViewRow row = _portfolioGrid.Rows[e.RowIndex];
            if (row.Tag is RemotePortfolio portfolio && row.Cells[e.ColumnIndex].Value?.ToString() == OsLocalization.Market.Label135)
            {
                RobotsVpsComparePositionsUi window = new RobotsVpsComparePositionsUi(
                    _client, portfolio.ServerType, portfolio.ServerNumber, portfolio.Number);
                window.Show();
            }
        }

        private async System.Threading.Tasks.Task RefreshBotsAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected || _pollInFlight) return;
            _pollInFlight = true;
            try
            {
                JsonElement result = await client.CallToolAsync("bot_get_list", null).ConfigureAwait(true);
                if (client != _client || !result.TryGetProperty("bots", out JsonElement bots)
                    || bots.ValueKind != JsonValueKind.Array) return;

                List<VpsBotRow> rows = new List<VpsBotRow>();
                foreach (JsonElement item in bots.EnumerateArray())
                {
                    rows.Add(new VpsBotRow
                    {
                        Number = ReadInt(item, "number"),
                        Name = ReadString(item, "public_name"),
                        InternalName = ReadString(item, "name"),
                        Type = ReadString(item, "class_name"),
                        FirstSecurity = ReadString(item, "first_security"),
                        OpenPositions = ReadInt(item, "open_positions_count"),
                        ClosedPositions = ReadInt(item, "closed_positions_count"),
                        IsOn = ReadBool(item, "is_on"),
                        EmulatorIsOn = ReadBool(item, "emulator_is_on")
                    });
                    if (string.IsNullOrWhiteSpace(rows[rows.Count - 1].Name))
                        rows[rows.Count - 1].Name = rows[rows.Count - 1].InternalName;
                }
                RenderBots(rows);
            }
            catch
            {
                // Keep the last successful snapshot visible during a temporary tunnel/API interruption.
            }
            finally
            {
                _pollInFlight = false;
            }
        }

        private void RenderBots(List<VpsBotRow> bots)
        {
            if (_robotsGrid == null) return;
            _robotsGrid.Rows.Clear();
            foreach (VpsBotRow bot in bots)
            {
                DataGridViewRow row = new DataGridViewRow();
                row.Cells.Add(new DataGridViewTextBoxCell());
                row.Cells.Add(new DataGridViewTextBoxCell());
                row.Cells.Add(new DataGridViewTextBoxCell());
                row.Cells.Add(new DataGridViewTextBoxCell());
                row.Cells.Add(new DataGridViewTextBoxCell());
                row.Cells.Add(new DataGridViewCheckBoxCell());
                row.Cells.Add(new DataGridViewCheckBoxCell());
                row.Cells.Add(new DataGridViewButtonCell());
                row.Cells.Add(new DataGridViewButtonCell());
                row.Cells.Add(new DataGridViewButtonCell());
                row.Cells.Add(new DataGridViewButtonCell());
                row.Cells[0].Value = bot.Number;
                row.Cells[1].Value = bot.Name;
                row.Cells[2].Value = bot.Type;
                row.Cells[3].Value = bot.FirstSecurity;
                row.Cells[4].Value = bot.OpenPositions + "/" + bot.ClosedPositions;
                row.Cells[5].Value = bot.IsOn;
                row.Cells[6].Value = bot.EmulatorIsOn;
                row.Cells[7].Value = OsLocalization.Trader.Label172;
                row.Cells[8].Value = OsLocalization.Trader.Label45;
                row.Cells[9].Value = OsLocalization.Trader.Label39;
                row.Cells[10].Value = OsLocalization.Trader.Label40;
                row.Tag = bot;
                _robotsGrid.Rows.Add(row);
            }
            AddCommandsRow();
        }

        private void RobotsGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;

            // The final grid row is the same command row used by Robots.Lite.
            // Column 9 is the original "Add New..." action.
            if (e.RowIndex == _robotsGrid.Rows.Count - 1 && e.ColumnIndex == 9)
            {
                AddRemoteBotAsync();
                return;
            }

            // Column 8 on the service row is "Journal common" (Label747) — 1:1 with
            // BotTabsPainter.RobotsGrid_CellContentClick (coluIndex == 8 -> _master.ShowCommunityJournal(2, 0, 0)).
            if (e.RowIndex == _robotsGrid.Rows.Count - 1 && e.ColumnIndex == 8)
            {
                ShowCommunityJournal();
                return;
            }

            // Column 10 on the service row is "Migration" — 1:1 with BotTabsPainter.RobotsGrid_CellContentClick
            // (rowTag == _addRowTag, coluIndex == 10 -> new BotsMigrationUi(_master)).
            if (e.RowIndex == _robotsGrid.Rows.Count - 1 && e.ColumnIndex == 10)
            {
                ShowMigration();
                return;
            }

            if (_client == null || !_client.IsConnected) return;

            VpsBotRow bot = _robotsGrid.Rows[e.RowIndex].Tag as VpsBotRow;
            if (bot == null) return;

            if (e.ColumnIndex == 5 || e.ColumnIndex == 6)
            {
                bool enabled = Convert.ToBoolean(_robotsGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? false);
                SetBotStateAsync(bot, e.ColumnIndex == 5, enabled);
                return;
            }

            if (string.IsNullOrWhiteSpace(bot.InternalName)) return;

            if (e.ColumnIndex == 7)
            {
                OpenChartAsync(bot);
                return;
            }

            if (e.ColumnIndex == 8)
            {
                RobotsVpsParametersUi window = new RobotsVpsParametersUi(_client, bot.InternalName);
                window.Owner = Window.GetWindow(this);
                window.Show();
                window.Activate();
                return;
            }

            // Column 9 — 1:1 с BotTabsPainter._grid_Click (coluIndex == 9): подтверждение через тот же
            // диалог AcceptDialogUi(Label4), затем удаление робота.
            if (e.ColumnIndex == 9)
            {
                DeleteRemoteBotAsync(bot);
                return;
            }

            // Column 10 — 1:1 с BotTabsPainter._grid_Click (coluIndex == 10 -> bot.ShowJournalDialog()):
            // журнал ОДНОГО робота — уже построенное окно RobotsVpsJournalUi, здесь просто открываем его,
            // одно окно на бота (повторный клик активирует уже открытое), как оригинал.
            if (e.ColumnIndex == 10)
            {
                ShowBotJournal(bot);
            }
        }

        private async void DeleteRemoteBotAsync(VpsBotRow bot)
        {
            try
            {
                AcceptDialogUi ui = new AcceptDialogUi(OsLocalization.Trader.Label4) { Owner = Window.GetWindow(this) };
                ui.ShowDialog();

                if (ui.UserAcceptAction == false) return;

                await _client.CallToolAsync("bot_delete", new { bot_id = bot.InternalName }).ConfigureAwait(true);
                _botJournalWindows.Remove(bot.InternalName);
                await RefreshBotsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not delete VPS robot: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowBotJournal(VpsBotRow bot)
        {
            try
            {
                if (_botJournalWindows.TryGetValue(bot.InternalName, out RobotsVpsJournalUi existing))
                {
                    if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                    existing.Activate();
                    return;
                }

                RobotsVpsJournalUi journalWindow = new RobotsVpsJournalUi(_client, bot.InternalName);
                journalWindow.Owner = Window.GetWindow(this);
                string internalName = bot.InternalName;
                journalWindow.Closed += (s, e) => _botJournalWindows.Remove(internalName);
                _botJournalWindows[internalName] = journalWindow;
                journalWindow.Show();
                journalWindow.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open the robot journal: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void AddRemoteBotAsync()
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected)
            {
                System.Windows.MessageBox.Show("Connect to VPS first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                CreateBotDialog dialog = new CreateBotDialog(client) { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() != true)
                    return;

                JsonElement result = await client.CallToolAsync("bot_create", new
                {
                    strategy_name = dialog.SelectedStrategy,
                    name = dialog.BotName
                }).ConfigureAwait(true);

                string createdName = ReadString(result, "name");
                await RefreshBotsAsync().ConfigureAwait(true);

                if (_robotsGrid != null && !string.IsNullOrWhiteSpace(createdName))
                {
                    for (int i = 0; i < _robotsGrid.Rows.Count - 1; i++)
                    {
                        VpsBotRow row = _robotsGrid.Rows[i].Tag as VpsBotRow;
                        if (row != null && string.Equals(row.InternalName, createdName, StringComparison.OrdinalIgnoreCase))
                        {
                            _robotsGrid.ClearSelection();
                            _robotsGrid.Rows[i].Selected = true;
                            _robotsGrid.FirstDisplayedScrollingRowIndex = i;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not create VPS robot: " + ex.Message,
                    "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 1:1 с OsTraderMaster.ShowCommunityJournal: одно окно на всё время жизни родительского окна,
        // повторный клик активирует уже открытое, а не плодит дубликаты.
        private void ShowCommunityJournal()
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    System.Windows.MessageBox.Show("Connect to VPS first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_communityJournalWindow != null)
                {
                    if (_communityJournalWindow.WindowState == WindowState.Minimized)
                        _communityJournalWindow.WindowState = WindowState.Normal;
                    _communityJournalWindow.Activate();
                    return;
                }

                _communityJournalWindow = new RobotsVpsCommunityJournalUi(_client);
                _communityJournalWindow.Owner = Window.GetWindow(this);
                _communityJournalWindow.Closed += (s, e) => _communityJournalWindow = null;
                _communityJournalWindow.Show();
                _communityJournalWindow.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open the community journal: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // 1:1 с BotTabsPainter (rowTag == _addRowTag, coluIndex == 10 -> new BotsMigrationUi(_master)):
        // одно окно на всё время жизни родительского окна, повторный клик активирует уже открытое.
        private void ShowMigration()
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    System.Windows.MessageBox.Show("Connect to VPS first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (_migrationWindow != null)
                {
                    if (_migrationWindow.WindowState == WindowState.Minimized)
                        _migrationWindow.WindowState = WindowState.Normal;
                    _migrationWindow.Activate();
                    return;
                }

                _migrationWindow = new RobotsVpsMigrationUi(_client);
                _migrationWindow.Owner = Window.GetWindow(this);
                _migrationWindow.Closed += (s, e) => _migrationWindow = null;
                _migrationWindow.Show();
                _migrationWindow.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open migration: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void OpenChartAsync(VpsBotRow bot)
        {
            try
            {
                string tabName = await ResolveTabNameAsync(bot.InternalName).ConfigureAwait(true);

                if (string.IsNullOrWhiteSpace(tabName))
                {
                    System.Windows.MessageBox.Show("For this robot the VPS API did not return a Simple chart source.", "VPS chart", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                RobotsVpsChartWindow window = new RobotsVpsChartWindow(_client, bot.InternalName, bot.Name, tabName);
                window.Owner = Window.GetWindow(this);
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not open the VPS chart: " + ex.Message, "VPS chart", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Первый "Simple" источник бота — тот же упрощающий приём, что был раньше только в OpenChartAsync;
        // теперь общий, с кэшем на время жизни подключения (см. SetClient), нужен и для действий над
        // позициями на вкладке "Positions" (bot_position_* требуют tab_name, а агрегированные списки
        // bot_journal_get_open/closed_positions его не отдают).
        private async System.Threading.Tasks.Task<string> ResolveTabNameAsync(string botId)
        {
            if (string.IsNullOrWhiteSpace(botId)) return null;
            if (_tabNameCache.TryGetValue(botId, out string cached)) return cached;

            JsonElement result = await _client.CallToolAsync("bot_get_sources", new { bot_id = botId }).ConfigureAwait(true);
            JsonElement sources = result.TryGetProperty("sources", out JsonElement sourceList) ? sourceList : default;
            string tabName = null;
            if (sources.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement source in sources.EnumerateArray())
                {
                    if (source.TryGetProperty("type", out JsonElement type)
                        && type.GetString() == "Simple"
                        && source.TryGetProperty("name", out JsonElement name))
                    {
                        tabName = name.GetString();
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(tabName)) _tabNameCache[botId] = tabName;
            return tabName;
        }

        private async void SetBotStateAsync(VpsBotRow bot, bool setTrading, bool enabled)
        {
            RemoteMcpClient client = _client;
            if (client == null || !client.IsConnected) return;
            try
            {
                object parameters = setTrading
                    ? (object)new { bot_id = bot.InternalName, is_on = enabled }
                    : new { bot_id = bot.InternalName, emulator_is_on = enabled };
                await client.CallToolAsync("bot_set_state", parameters).ConfigureAwait(true);
                await RefreshBotsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                await RefreshBotsAsync().ConfigureAwait(true);
                System.Windows.MessageBox.Show("Could not update VPS robot state: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void AddCommandsRow()
        {
            DataGridViewRow row = new DataGridViewRow();
            for (int i = 0; i < _robotsGrid.Columns.Count; i++)
            {
                row.Cells.Add(i >= 7
                    ? (DataGridViewCell)new DataGridViewButtonCell()
                    : i == 5 || i == 6
                        ? new DataGridViewCheckBoxCell()
                        : new DataGridViewTextBoxCell());
            }
            row.Cells[7].Value = OsLocalization.Trader.Label570;
            row.Cells[8].Value = OsLocalization.Trader.Label747;
            row.Cells[9].Value = OsLocalization.Trader.Label38;
            row.Cells[10].Value = OsLocalization.Trader.Label762;
            _robotsGrid.Rows.Add(row);
        }

        private DataGridView CreateRobotsGrid()
        {
            DataGridView grid = DataGridFactory.GetDataGridView(
                DataGridViewSelectionMode.CellSelect,
                DataGridViewAutoSizeRowsMode.AllCells);
            grid.ScrollBars = ScrollBars.Vertical;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AllowUserToAddRows = false;
            grid.DataError += (sender, args) => args.ThrowException = false;
            grid.CellContentClick += RobotsGrid_CellContentClick;
            grid.CurrentCellDirtyStateChanged += (sender, args) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            AddTextColumn(grid, "#", DataGridViewAutoSizeColumnMode.AllCells, true);
            AddTextColumn(grid, OsLocalization.Trader.Label175, DataGridViewAutoSizeColumnMode.Fill, true);
            AddTextColumn(grid, OsLocalization.Trader.Label167, DataGridViewAutoSizeColumnMode.Fill, true);
            AddTextColumn(grid, OsLocalization.Trader.Label176, DataGridViewAutoSizeColumnMode.Fill, true);
            AddTextColumn(grid, "Поз (откр/закр)", DataGridViewAutoSizeColumnMode.AllCells, true);
            grid.Columns[4].MinimumWidth = 125;
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = OsLocalization.Trader.Label184,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                ReadOnly = false
            });
            grid.Columns.Add(new DataGridViewCheckBoxColumn
            {
                HeaderText = OsLocalization.Trader.Label185,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
                ReadOnly = false
            });
            AddButtonColumn(grid);
            AddButtonColumn(grid);
            AddButtonColumn(grid);
            AddButtonColumn(grid);
            return grid;
        }

        private static void AddTextColumn(DataGridView grid, string header,
            DataGridViewAutoSizeColumnMode sizeMode, bool readOnly, int width = 100)
        {
            DataGridViewTextBoxCell template = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            grid.Columns.Add(new DataGridViewColumn
            {
                CellTemplate = template,
                HeaderText = header,
                ReadOnly = readOnly,
                AutoSizeMode = sizeMode,
                Width = width
            });
        }

        private static void AddButtonColumn(DataGridView grid)
        {
            grid.Columns.Add(new DataGridViewButtonColumn
            {
                ReadOnly = true,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
        }

        private static int ReadInt(JsonElement item, string name) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : 0;

        private static bool ReadBool(JsonElement item, string name) =>
            item.TryGetProperty(name, out JsonElement value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            && value.GetBoolean();

        private static string ReadString(JsonElement item, string name) =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty : string.Empty;

        private static decimal ReadDecimal(JsonElement item, string name)
        {
            if (!item.TryGetProperty(name, out JsonElement value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
                return number;
            return decimal.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)
                ? number : 0;
        }

        private sealed class RemotePortfolio
        {
            public string ServerType;
            public int ServerNumber;
            public string ServerUniqueName;
            public string Number;
            public decimal ValueBegin;
            public decimal ValueCurrent;
            public decimal ValueBlocked;
            public decimal UnrealizedPnl;
            public readonly List<RemotePortfolioPosition> Positions = new List<RemotePortfolioPosition>();

            public static RemotePortfolio FromJson(JsonElement item, string serverType, int serverNumber)
            {
                RemotePortfolio portfolio = new RemotePortfolio
                {
                    ServerType = serverType,
                    ServerNumber = serverNumber,
                    ServerUniqueName = ReadString(item, "serverUniqueName"),
                    Number = ReadString(item, "number"),
                    ValueBegin = ReadDecimal(item, "valueBegin"),
                    ValueCurrent = ReadDecimal(item, "valueCurrent"),
                    ValueBlocked = ReadDecimal(item, "valueBlocked"),
                    UnrealizedPnl = ReadDecimal(item, "unrealizedPnl")
                };
                if (string.IsNullOrWhiteSpace(portfolio.ServerUniqueName))
                    portfolio.ServerUniqueName = serverType + "_" + serverNumber;

                if (item.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement position in positions.EnumerateArray())
                    {
                        portfolio.Positions.Add(new RemotePortfolioPosition
                        {
                            SecurityName = ReadString(position, "securityNameCode"),
                            ValueBegin = ReadDecimal(position, "valueBegin"),
                            ValueCurrent = ReadDecimal(position, "valueCurrent"),
                            ValueBlocked = ReadDecimal(position, "valueBlocked"),
                            UnrealizedPnl = ReadDecimal(position, "unrealizedPnl")
                        });
                    }
                }
                return portfolio;
            }
        }

        private sealed class RemotePortfolioPosition
        {
            public string SecurityName;
            public decimal ValueBegin;
            public decimal ValueCurrent;
            public decimal ValueBlocked;
            public decimal UnrealizedPnl;
        }

        private sealed class VpsBotRow
        {
            public int Number;
            public string Name;
            public string InternalName;
            public string Type;
            public string FirstSecurity;
            public int OpenPositions;
            public int ClosedPositions;
            public bool IsOn;
            public bool EmulatorIsOn;
        }
    }
}
