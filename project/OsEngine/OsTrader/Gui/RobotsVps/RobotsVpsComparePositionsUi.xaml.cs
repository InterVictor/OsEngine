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

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>Robots.Lite comparison window backed by the remote VPS compare-positions API.</summary>
    public partial class RobotsVpsComparePositionsUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _serverType;
        private readonly int _serverNumber;
        private readonly string _portfolioName;
        private readonly DispatcherTimer _refreshTimer;
        private DataGridView _grid;
        private bool _refreshInFlight;
        private bool _updatingControls;
        private bool _renderingGrid;
        private List<string> _watchedPortfolios = new List<string>();
        private List<string> _ignoredSecurities = new List<string>();
        private JsonElement _lastSecurities;

        public RobotsVpsComparePositionsUi(RemoteMcpClient client, string serverType, int serverNumber, string portfolioName)
        {
            InitializeComponent();
            OsEngine.Layout.StickyBorders.Listen(this);
            OsEngine.Layout.StartupLocation.Start_MouseInCentre(this);
            _client = client;
            _serverType = serverType;
            _serverNumber = serverNumber;
            _portfolioName = portfolioName;
            Title = OsLocalization.Market.Label137;
            LabelConnectionName.Content = OsLocalization.Market.Label136 + " " + serverType + "-" + serverNumber;
            CheckBoxAutoLogMessageOnError.Content = OsLocalization.Market.Label138;
            LabelVerificationPeriod.Content = OsLocalization.Market.Label139;
            LabelTimeDelaySeconds.Content = OsLocalization.Market.Label236;
            ButtonSyncAll.Content = OsLocalization.ConvertToLocString("Eng:Synchronize_Ru:Синхронизировать_");
            ComboBoxVerificationPeriod.Items.Add("Min1");
            ComboBoxVerificationPeriod.Items.Add("Min5");
            ComboBoxVerificationPeriod.Items.Add("Min10");
            ComboBoxVerificationPeriod.Items.Add("Min30");
            CreateTable();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _refreshTimer.Tick += async (s, e) => await RefreshAsync();
        }

        private void CreateTable()
        {
            _grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.CellSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _grid.ScrollBars = ScrollBars.Vertical;
            string[] headers = { OsLocalization.Market.Label140, OsLocalization.Market.Message14, OsLocalization.Market.Label141,
                OsLocalization.Market.Label142, OsLocalization.Market.Label143, OsLocalization.Market.Label144,
                OsLocalization.Market.Label145, OsLocalization.Market.Label146, OsLocalization.Market.Label147,
                OsLocalization.Market.Label158, "" };
            for (int i = 0; i < headers.Length; i++)
            {
                DataGridViewCell template = i == 9 ? (DataGridViewCell)new DataGridViewCheckBoxCell()
                    : i == 10 ? new DataGridViewButtonCell() : new DataGridViewTextBoxCell();
                DataGridViewColumn column = new DataGridViewColumn { CellTemplate = template,
                    HeaderText = headers[i], ReadOnly = i != 9, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
                _grid.Columns.Add(column);
            }
            _grid.CurrentCellDirtyStateChanged += Grid_CurrentCellDirtyStateChanged;
            _grid.CellValueChanged += Grid_CellValueChanged;
            _grid.CellClick += Grid_CellClick;
            Host.Child = _grid;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshAsync();
            _refreshTimer.Start();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _refreshTimer.Stop();
            if (_grid != null)
            {
                _grid.CurrentCellDirtyStateChanged -= Grid_CurrentCellDirtyStateChanged;
                _grid.CellValueChanged -= Grid_CellValueChanged;
                _grid.CellClick -= Grid_CellClick;
            }
            if (Host != null) Host.Child = null;
            _grid?.Dispose();
            _grid = null;
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            if (_refreshInFlight || _client == null || !_client.IsConnected || _grid == null) return;
            _refreshInFlight = true;
            try
            {
                JsonElement result = await _client.CallToolAsync("compare_positions_get", new { server_type = _serverType, number = _serverNumber });
                if (_grid == null) return;
                if (!result.TryGetProperty("portfolios", out JsonElement portfolios) || portfolios.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("VPS returned an unexpected compare-positions response.");
                JsonElement portfolio = portfolios.EnumerateArray().FirstOrDefault(p =>
                    p.TryGetProperty("portfolio_name", out JsonElement name) && name.GetString() == _portfolioName);
                RenderPortfolio(portfolio);

                JsonElement settings = await _client.CallToolAsync("compare_positions_get_settings", new { server_type = _serverType, number = _serverNumber });
                if (_grid == null) return;
                _updatingControls = true;
                if (settings.TryGetProperty("verification_period", out JsonElement period)) ComboBoxVerificationPeriod.SelectedItem = period.GetString();
                if (settings.TryGetProperty("time_delay_seconds", out JsonElement delay)) TextBoxTimeDelaySeconds.Text = delay.ToString();
                _watchedPortfolios = ReadStringArray(settings, "portfolios_to_watch");
                _ignoredSecurities = ReadStringArray(settings, "ignored_securities");
                CheckBoxAutoLogMessageOnError.IsChecked = _watchedPortfolios.Contains(_portfolioName);
                _updatingControls = false;
            }
            catch (Exception ex)
            {
                if (_grid != null)
                {
                    _grid.Rows.Clear();
                    _grid.Rows.Add(ex.Message);
                }
            }
            finally { _refreshInFlight = false; }
        }

        private void RenderPortfolio(JsonElement portfolio)
        {
            _renderingGrid = true;
            _grid.Rows.Clear();
            if (portfolio.ValueKind != JsonValueKind.Object || !portfolio.TryGetProperty("securities", out JsonElement securities)
                || securities.ValueKind != JsonValueKind.Array)
            {
                _renderingGrid = false;
                return;
            }
            _lastSecurities = securities.Clone();
            int longCount = 0, shortCount = 0;
            foreach (JsonElement security in securities.EnumerateArray())
            {
                decimal robotLong = Number(security, "robots_long");
                decimal robotShort = Number(security, "robots_short");
                if (robotLong > 0) longCount++;
                if (robotShort < 0) shortCount++;
            }
            _grid.Rows.Add(_portfolioName, "", "", longCount, shortCount, "", "", "", "", false, "");
            foreach (JsonElement security in securities.EnumerateArray())
            {
                bool isIgnored = _ignoredSecurities.Contains(Text(security, "security"));
                string status = Text(security, "status");
                string syncLabel = status == "Error" && !isIgnored
                    ? OsLocalization.ConvertToLocString("Eng:Sync_Ru:Синх._") : "";
                int index = _grid.Rows.Add("", Text(security, "security"), status,
                    Number(security, "robots_long").ToString(CultureInfo.InvariantCulture),
                    Number(security, "robots_short").ToString(CultureInfo.InvariantCulture),
                    Number(security, "robots_common").ToString(CultureInfo.InvariantCulture),
                    Number(security, "portfolio_long").ToString(CultureInfo.InvariantCulture),
                    Number(security, "portfolio_short").ToString(CultureInfo.InvariantCulture),
                    Number(security, "portfolio_common").ToString(CultureInfo.InvariantCulture),
                    isIgnored, syncLabel);
                if (!isIgnored)
                    _grid.Rows[index].Cells[2].Style.ForeColor = status == "Normal" ? System.Drawing.Color.Green : System.Drawing.Color.Red;
                if (isIgnored)
                    foreach (DataGridViewCell cell in _grid.Rows[index].Cells) cell.Style.ForeColor = System.Drawing.Color.Gray;
            }
            _renderingGrid = false;
        }

        private void Grid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private async void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_renderingGrid || e.RowIndex < 1 || e.ColumnIndex != 9 || e.RowIndex >= _grid.Rows.Count)
                return;
            string security = Convert.ToString(_grid.Rows[e.RowIndex].Cells[1].Value);
            bool ignored = Convert.ToBoolean(_grid.Rows[e.RowIndex].Cells[9].Value ?? false);
            if (string.IsNullOrWhiteSpace(security)) return;
            if (ignored && !_ignoredSecurities.Contains(security)) _ignoredSecurities.Add(security);
            if (!ignored) _ignoredSecurities.Remove(security);
            await SaveIgnoredAsync();
        }

        private async void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 1 || e.ColumnIndex != 10 || e.RowIndex >= _grid.Rows.Count)
                return;
            string action = Convert.ToString(_grid.Rows[e.RowIndex].Cells[10].Value);
            string security = Convert.ToString(_grid.Rows[e.RowIndex].Cells[1].Value);
            if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(security)) return;
            JsonElement position = FindSecurity(security);
            string securityConfirm = OsLocalization.ConvertToLocString("Eng:Synchronize position for_Ru:Синхронизировать позицию по_") + " " + security + "?\n"
                + OsLocalization.ConvertToLocString("Eng:The following market order will be sent:_Ru:Будет отправлен следующий рыночный ордер:_")
                + "\n" + FormatOrder(position);
            if (!ConfirmAction(securityConfirm))
                return;
            try
            {
                await _client.CallToolAsync("compare_positions_sync_this", new
                {
                    server_type = _serverType, number = _serverNumber, portfolio_name = _portfolioName, security_name = security
                });
                await RefreshAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async void CheckBoxAutoLogMessageOnError_Click(object sender, RoutedEventArgs e)
        {
            if (_updatingControls) return;
            if (CheckBoxAutoLogMessageOnError.IsChecked == true)
            {
                if (!_watchedPortfolios.Contains(_portfolioName)) _watchedPortfolios.Add(_portfolioName);
            }
            else _watchedPortfolios.Remove(_portfolioName);
            await SaveSettingsAsync(new { portfolios_to_watch = _watchedPortfolios });
        }

        private async void ComboBoxVerificationPeriod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingControls || ComboBoxVerificationPeriod.SelectedItem == null) return;
            await SaveSettingsAsync(new { verification_period = ComboBoxVerificationPeriod.SelectedItem.ToString() });
        }

        private async void TextBoxTimeDelaySeconds_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_updatingControls) return;
            if (!int.TryParse(TextBoxTimeDelaySeconds.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) || delay <= 0)
            {
                await RefreshAsync();
                return;
            }
            await SaveSettingsAsync(new { time_delay_seconds = delay });
        }

        private async void ButtonSyncAll_Click(object sender, RoutedEventArgs e)
        {
            List<string> mismatches = _lastSecurities.ValueKind == JsonValueKind.Array
                ? _lastSecurities.EnumerateArray().Where(item => Text(item, "status") == "Error"
                    && !_ignoredSecurities.Contains(Text(item, "security"))).Select(FormatOrder).ToList()
                : new List<string>();
            if (mismatches.Count == 0)
            {
                System.Windows.MessageBox.Show(OsLocalization.ConvertToLocString(
                    "Eng:No mismatches. All positions are synchronized._Ru:Расхождений нет. Все позиции синхронизированы._"),
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string prompt = OsLocalization.ConvertToLocString("Eng:The following positions will be synchronized with market orders:_Ru:Следующие позиции будут синхронизированы рыночными ордерами:_")
                + "\n" + string.Join("\n", mismatches) + "\n\n"
                + OsLocalization.ConvertToLocString("Eng:Continue?_Ru:Продолжить?_");
            if (!ConfirmAction(prompt))
                return;
            try
            {
                JsonElement response = await _client.CallToolAsync("compare_positions_sync_all", new
                {
                    server_type = _serverType, number = _serverNumber, portfolio_name = _portfolioName
                });
                int sent = response.TryGetProperty("sent_count", out JsonElement count) && count.TryGetInt32(out int sentCount) ? sentCount : 0;
                System.Windows.MessageBox.Show(OsLocalization.ConvertToLocString("Eng:Orders sent:_Ru:Отправлено ордеров:_") + " " + sent,
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async System.Threading.Tasks.Task SaveIgnoredAsync()
        {
            try
            {
                await _client.CallToolAsync("compare_positions_set_ignored", new
                {
                    server_type = _serverType, number = _serverNumber, securities = _ignoredSecurities
                });
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async System.Threading.Tasks.Task SaveSettingsAsync(object values)
        {
            try
            {
                var arguments = new Dictionary<string, object>
                {
                    ["server_type"] = _serverType,
                    ["number"] = _serverNumber
                };
                foreach (System.Reflection.PropertyInfo property in values.GetType().GetProperties())
                    arguments[property.Name] = property.GetValue(values);
                await _client.CallToolAsync("compare_positions_set_settings", arguments);
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowError(Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private bool ConfirmAction(string message)
        {
            AcceptDialogUi dialog = new AcceptDialogUi(message);
            dialog.ShowDialog();
            return dialog.UserAcceptAction;
        }

        private JsonElement FindSecurity(string securityName)
        {
            if (_lastSecurities.ValueKind == JsonValueKind.Array)
                return _lastSecurities.EnumerateArray().FirstOrDefault(item => Text(item, "security") == securityName);
            return default;
        }

        private static string FormatOrder(JsonElement security)
        {
            decimal difference = Number(security, "robots_common") - Number(security, "portfolio_common");
            string direction = difference > 0
                ? OsLocalization.ConvertToLocString("Eng:Buy_Ru:Купить_")
                : OsLocalization.ConvertToLocString("Eng:Sell_Ru:Продать_");
            string action = Number(security, "portfolio_common") == 0
                || (difference > 0 && Number(security, "portfolio_common") > 0)
                || (difference < 0 && Number(security, "portfolio_common") < 0)
                    ? OsLocalization.ConvertToLocString("Eng:Position will be opened or increased_Ru:Позиция будет открыта или увеличена_")
                    : OsLocalization.ConvertToLocString("Eng:Position will be reduced or closed_Ru:Позиция будет сокращена или закрыта_");
            return Text(security, "security") + ": " + direction + " " + Math.Abs(difference).ToString(CultureInfo.CurrentCulture)
                + ". " + action + " (robots " + Number(security, "robots_common").ToString(CultureInfo.CurrentCulture)
                + ", portfolio " + Number(security, "portfolio_common").ToString(CultureInfo.CurrentCulture) + ")";
        }

        private static List<string> ReadStringArray(JsonElement item, string name)
        {
            List<string> values = new List<string>();
            if (item.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array)
                values.AddRange(array.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()));
            return values;
        }

        private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) ? value.ToString() : "";
        private static bool Boolean(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        private static decimal Number(JsonElement item, string name) => item.TryGetProperty(name, out JsonElement value) && value.TryGetDecimal(out decimal number) ? number : 0;
    }
}
