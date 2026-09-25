using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Controls;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;
using OsEngine.Market;
using OsEngine.Market.SupportTable;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote-data version of AServerParameterUi. Layout and grid formatting follow the native connector window.
    /// </summary>
    public partial class RobotsVpsServerParametersUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _serverType;
        private int _serverNumber;
        private string _serverName;
        private DataGridView _parametersGrid;
        private DataGridView _logGrid;
        private DataGridView _connectionsGrid;
        private List<RemoteParameter> _parameters = new List<RemoteParameter>();
        private bool _suspendSave;
        private bool _saveInFlight;
        private readonly SemaphoreSlim _remoteSettingsSaveLock = new SemaphoreSlim(1, 1);

        public RobotsVpsServerParametersUi(RemoteMcpClient client, string serverType, int serverNumber, string serverName)
        {
            InitializeComponent();
            _client = client;
            _serverType = serverType;
            _serverNumber = serverNumber;
            _serverName = serverName;
            OsEngine.Layout.StickyBorders.Listen(this);
            OsEngine.Layout.StartupLocation.Start_MouseInCentre(this);
            Title = OsLocalization.Market.TitleAServerParametrUi + " " + _serverType;
            TabItemParameters.Header = OsLocalization.Market.TabItem3;
            TabItemLog.Header = OsLocalization.Market.TabItem4;
            Label21.Content = OsLocalization.Market.Label21;
            ButtonConnect.Content = OsLocalization.Market.ButtonConnect;
            ButtonAbort.Content = OsLocalization.Market.ButtonDisconnect;
            LabelCurrentConnectionName.Content = OsLocalization.Market.Label164 + " " + _serverName;
            LabelPreConfiguredConnection.Content = OsLocalization.Market.Label166;
            ButtonConnect.Click += ButtonConnect_Click;
            ButtonAbort.Click += ButtonAbort_Click;
            TabControlSettings.SelectionChanged += TabControlSettings_SelectionChanged;
            Loaded += Window_Loaded;
            Closed += Window_Closed;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CreateParameterGrid();
            CreateLogGrid();
            CreateConnectionsGrid();
            _ = RefreshAllAsync();
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            ButtonConnect.Click -= ButtonConnect_Click;
            ButtonAbort.Click -= ButtonAbort_Click;
            TabControlSettings.SelectionChanged -= TabControlSettings_SelectionChanged;
            if (HostSettings != null) HostSettings.Child = null;
            if (HostLog != null) HostLog.Child = null;
            if (HostPreConfiguredConnections != null) HostPreConfiguredConnections.Child = null;
            DisposeGrid(ref _parametersGrid);
            DisposeGrid(ref _logGrid);
            DisposeGrid(ref _connectionsGrid);
        }

        private static void DisposeGrid(ref DataGridView grid)
        {
            if (grid == null) return;
            grid.Rows.Clear();
            grid.Columns.Clear();
            grid.Dispose();
            grid = null;
        }

        private void CreateParameterGrid()
        {
            _parametersGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.CellSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _parametersGrid.ScrollBars = ScrollBars.Vertical;
            AddParameterColumn(_parametersGrid, OsLocalization.Market.GridColumn1, 300, true);
            AddParameterColumn(_parametersGrid, OsLocalization.Market.GridColumn2, 0, false);
            AddParameterColumn(_parametersGrid, string.Empty, 100, true);
            AddParameterColumn(_parametersGrid, string.Empty, 100, true);
            _parametersGrid.CellValueChanged += ParametersGrid_CellValueChanged;
            _parametersGrid.CellClick += ParametersGrid_CellClick;
            _parametersGrid.CellDoubleClick += ParametersGrid_CellDoubleClick;
            _parametersGrid.CurrentCellDirtyStateChanged += ParametersGrid_CurrentCellDirtyStateChanged;
            _parametersGrid.DataError += (s, e) => e.ThrowException = false;
            HostSettings.Child = _parametersGrid;
        }

        private static void AddParameterColumn(DataGridView grid, string header, int width, bool readOnly)
        {
            DataGridViewTextBoxCell template = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn
            {
                CellTemplate = template,
                HeaderText = header,
                ReadOnly = readOnly
            };
            if (width == 0) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else column.Width = width;
            grid.Columns.Add(column);
        }

        private void CreateLogGrid()
        {
            _logGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _logGrid.ScrollBars = ScrollBars.Vertical;
            AddLogColumn(_logGrid, OsLocalization.Logging.Column1, 200);
            AddLogColumn(_logGrid, OsLocalization.Logging.Column2, 100);
            AddLogColumn(_logGrid, OsLocalization.Logging.Column3, 0);
            HostLog.Child = _logGrid;
        }

        private static void AddLogColumn(DataGridView grid, string header, int width)
        {
            DataGridViewTextBoxCell template = new DataGridViewTextBoxCell { Style = grid.DefaultCellStyle };
            DataGridViewColumn column = new DataGridViewColumn { CellTemplate = template, HeaderText = header, ReadOnly = true };
            if (width == 0) column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            else column.Width = width;
            grid.Columns.Add(column);
        }

        private void CreateConnectionsGrid()
        {
            _connectionsGrid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.CellSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _connectionsGrid.ScrollBars = ScrollBars.Vertical;
            string[] headers = { OsLocalization.Market.Label164, OsLocalization.Market.Label167, OsLocalization.Market.Label168, OsLocalization.Market.Label169, string.Empty, string.Empty, string.Empty };
            for (int i = 0; i < headers.Length; i++)
            {
                DataGridViewTextBoxCell template = new DataGridViewTextBoxCell { Style = _connectionsGrid.DefaultCellStyle };
                DataGridViewColumn column = new DataGridViewColumn { CellTemplate = template, HeaderText = headers[i], ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
                _connectionsGrid.Columns.Add(column);
            }
            _connectionsGrid.CellClick += ConnectionsGrid_CellClick;
            HostPreConfiguredConnections.Child = _connectionsGrid;
        }

        private async System.Threading.Tasks.Task RefreshAllAsync()
        {
            await RefreshParametersAsync();
            await RefreshStatusAsync();
            await RefreshConnectionsAsync();
            await RefreshInstanceSupportAsync();
            if (TabItemLog.IsSelected) await RefreshLogAsync();
        }

        private async System.Threading.Tasks.Task RefreshInstanceSupportAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_management_get_connector_permissions", new { type = _serverType });
                bool supportsMultiple = false;
                if (response.TryGetProperty("permissions", out JsonElement permissions)
                    && permissions.TryGetProperty("IsSupports_MultipleInstances", out JsonElement value)
                    && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                    supportsMultiple = value.GetBoolean();
                HostPreConfiguredConnections.Visibility = supportsMultiple ? Visibility.Visible : Visibility.Hidden;
                LabelPreConfiguredConnection.Visibility = supportsMultiple ? Visibility.Visible : Visibility.Hidden;
                LabelCurrentConnectionName.Visibility = supportsMultiple ? Visibility.Visible : Visibility.Hidden;
                if (!supportsMultiple) GridPrime.RowDefinitions[0].Height = new GridLength(0);
            }
            catch
            {
                // The parameter dialog stays usable if an older VPS build does not expose connector permissions.
            }
        }

        private async System.Threading.Tasks.Task RefreshParametersAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_instance_get_params", new { type = _serverType, number = _serverNumber });
                JsonElement items = response;
                string diagnostic = string.Empty;
                if (response.ValueKind == JsonValueKind.Object)
                {
                    items = response.TryGetProperty("parameters", out JsonElement parameterArray) ? parameterArray : default;
                    diagnostic = "Connector " + GetString(response, "server_name") + " (" + GetString(response, "type") + " #" + GetString(response, "number") + "), status: " + GetString(response, "status") + ". ";
                }
                if (items.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The VPS returned an unexpected parameter list.");
                _parameters.Clear();
                foreach (JsonElement item in items.EnumerateArray())
                {
                    _parameters.Add(new RemoteParameter
                    {
                        Name = GetString(item, "name"),
                        Type = GetString(item, "type"),
                        Comment = GetString(item, "comment"),
                        Value = item.TryGetProperty("value", out JsonElement value) ? value.Clone() : default,
                        EnumValues = item.TryGetProperty("enum_values", out JsonElement enums) && enums.ValueKind == JsonValueKind.Array
                            ? enums.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList()
                            : new List<string>()
                    ,
                        IsSecret = item.TryGetProperty("is_secret", out JsonElement secret) && secret.ValueKind == JsonValueKind.True,
                        ButtonAction = GetString(item, "button_action")
                    });
                }
                PaintParameters();
                if (_parameters.Count == 0)
                {
                    _parametersGrid.Rows.Add(diagnostic + "No connector parameters were returned by the VPS.");
                    _parametersGrid.Rows[0].ReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                ShowError("Could not load connector parameters: " + ex.Message);
            }
        }

        private void PaintParameters()
        {
            _suspendSave = true;
            _parametersGrid.Rows.Clear();
            foreach (RemoteParameter parameter in _parameters)
            {
                DataGridViewRow row = new DataGridViewRow();
                row.Cells.Add(new DataGridViewTextBoxCell { Value = string.Equals(parameter.Type, "Button", StringComparison.OrdinalIgnoreCase) ? string.Empty : parameter.Name });
                DataGridViewCell valueCell;
                if (string.Equals(parameter.Type, "Button", StringComparison.OrdinalIgnoreCase))
                {
                    valueCell = new DataGridViewButtonCell { Value = parameter.Name };
                }
                else if (string.Equals(parameter.Type, "Enum", StringComparison.OrdinalIgnoreCase) && parameter.EnumValues.Count > 0)
                {
                    DataGridViewComboBoxCell combo = new DataGridViewComboBoxCell();
                    foreach (string option in parameter.EnumValues) combo.Items.Add(option);
                    string current = parameter.Value.ValueKind == JsonValueKind.String ? parameter.Value.GetString() : parameter.Value.ToString();
                    if (!string.IsNullOrEmpty(current) && !combo.Items.Contains(current)) combo.Items.Add(current);
                    combo.Value = current;
                    valueCell = combo;
                }
                else if (string.Equals(parameter.Type, "Bool", StringComparison.OrdinalIgnoreCase))
                {
                    DataGridViewComboBoxCell combo = new DataGridViewComboBoxCell();
                    combo.Items.Add("True");
                    combo.Items.Add("False");
                    combo.Value = parameter.Value.ValueKind == JsonValueKind.True ? "True" : "False";
                    valueCell = combo;
                }
                else
                {
                    string value = parameter.Value.ValueKind == JsonValueKind.String ? parameter.Value.GetString()
                        : parameter.Value.ValueKind == JsonValueKind.Null || parameter.Value.ValueKind == JsonValueKind.Undefined ? string.Empty
                        : parameter.Value.ToString();
                    if (parameter.IsSecret && value.Length > 0)
                    {
                        parameter.MaskedValue = value;
                        value = new string('*', Math.Max(4, value.Length));
                    }
                    valueCell = new DataGridViewTextBoxCell { Value = value };
                }
                row.Cells.Add(valueCell);
                row.Cells.Add(new DataGridViewTextBoxCell { Value = "" });
                row.Cells.Add(new DataGridViewButtonCell
                {
                    Value = string.IsNullOrWhiteSpace(parameter.Comment) ? string.Empty : OsLocalization.Market.Label86
                });
                row.Tag = parameter;
                _parametersGrid.Rows.Add(row);
                row.Cells[0].ReadOnly = true;
                row.Cells[1].ReadOnly = false;
                row.Cells[2].ReadOnly = true;
            }
            _suspendSave = false;
        }

        private void ParametersGrid_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (_parametersGrid.IsCurrentCellDirty && _parametersGrid.CurrentCell is DataGridViewComboBoxCell)
                _parametersGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }

        private async void ParametersGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_suspendSave || e.RowIndex < 0 || e.ColumnIndex != 1 || _saveInFlight) return;
            RemoteParameter parameter = _parametersGrid.Rows[e.RowIndex].Tag as RemoteParameter;
            if (parameter == null) return;
            await SaveParameterAsync(parameter, _parametersGrid.Rows[e.RowIndex].Cells[1].Value);
        }

        private async System.Threading.Tasks.Task SaveParameterAsync(RemoteParameter parameter, object rawValue)
        {
            string text = rawValue?.ToString() ?? string.Empty;
            if (string.Equals(parameter.Type, "Button", StringComparison.OrdinalIgnoreCase)) return;
            if (parameter.IsSecret
                && (text.Length == 0 || text == new string('*', Math.Max(4, parameter.MaskedValue?.Length ?? 0)))) return;
            try
            {
                object value;
                if (string.Equals(parameter.Type, "Bool", StringComparison.OrdinalIgnoreCase))
                    value = string.Equals(text, "True", StringComparison.OrdinalIgnoreCase);
                else if (string.Equals(parameter.Type, "Int", StringComparison.OrdinalIgnoreCase))
                    value = int.Parse(text, NumberStyles.Integer, CultureInfo.CurrentCulture);
                else if (string.Equals(parameter.Type, "Decimal", StringComparison.OrdinalIgnoreCase))
                    value = decimal.Parse(text, NumberStyles.Number, CultureInfo.CurrentCulture);
                else value = text;

                _saveInFlight = true;
                await _client.CallToolAsync("server_instance_set_params", new
                {
                    type = _serverType,
                    number = _serverNumber,
                    parameters = new[] { new { name = parameter.Name, value = value } }
                });
                parameter.Value = JsonSerializer.SerializeToElement(value);
                if (parameter.IsSecret) parameter.MaskedValue = text;
            }
            catch (Exception ex)
            {
                ShowError("Could not save connector parameter '" + parameter.Name + "': " + ex.Message);
            }
            finally { _saveInFlight = false; }
        }
        private void ParametersGrid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
            RemoteParameter parameter = _parametersGrid.Rows[e.RowIndex].Tag as RemoteParameter;
            if (parameter == null || !parameter.IsSecret || string.IsNullOrEmpty(parameter.MaskedValue)) return;
            _suspendSave = true;
            _parametersGrid.Rows[e.RowIndex].Cells[1].Value = string.Empty;
            _parametersGrid.Rows[e.RowIndex].Cells[1].ReadOnly = false;
            _suspendSave = false;
            _parametersGrid.CurrentCell = _parametersGrid.Rows[e.RowIndex].Cells[1];
            _parametersGrid.BeginEdit(true);
        }

        private async void ParametersGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            RemoteParameter parameter = _parametersGrid.Rows[e.RowIndex].Tag as RemoteParameter;
            if (parameter == null) return;
            if (e.ColumnIndex == 1 && parameter.Type == "Button")
            {
                if (parameter.ButtonAction == "securities") await OpenRemoteSecuritiesAsync();
                else if (parameter.ButtonAction == "non_trade_periods") await OpenRemoteNonTradePeriodsAsync();
                return;
            }
            if (e.ColumnIndex == 3 && !string.IsNullOrWhiteSpace(parameter.Comment))
                new CustomMessageBoxUi(parameter.Comment).ShowDialog();
        }

        private async System.Threading.Tasks.Task OpenRemoteSecuritiesAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_instance_get_securities", new { type = _serverType, number = _serverNumber });
                if (!response.TryGetProperty("securities", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The VPS did not return connector securities.");
                List<Security> securities = rows.EnumerateArray().Select(SecurityFromJson).ToList();
                if (!Enum.TryParse(_serverType, true, out ServerType serverType)) throw new InvalidOperationException("Unknown connector type: " + _serverType);
                new SecuritiesUi(securities, serverType, security => _ = SaveRemoteSecurityAsync(security)) { Owner = this }.ShowDialog();
            }
            catch (Exception ex) { ShowError("Could not open connector securities: " + ex.Message); }
        }

        private async System.Threading.Tasks.Task SaveRemoteSecurityAsync(Security security)
        {
            await _remoteSettingsSaveLock.WaitAsync();
            try
            {
                await _client.CallToolAsync("server_instance_set_security", new
                {
                    type = _serverType, number = _serverNumber,
                    security = new { name = security.Name, nameFull = security.NameFull, nameId = security.NameId, nameClass = security.NameClass,
                        securityType = security.SecurityType.ToString(), lot = security.Lot, priceStep = security.PriceStep, priceStepCost = security.PriceStepCost,
                        decimals = security.Decimals, decimalsVolume = security.DecimalsVolume, minTradeAmountType = security.MinTradeAmountType.ToString(),
                        minTradeAmount = security.MinTradeAmount, volumeStep = security.VolumeStep, priceLimitHigh = security.PriceLimitHigh,
                        priceLimitLow = security.PriceLimitLow, marginBuy = security.MarginBuy, marginSell = security.MarginSell, strike = security.Strike }
                });
            }
            catch (Exception ex) { ShowError("Could not save remote security settings: " + ex.Message); }
            finally { _remoteSettingsSaveLock.Release(); }
        }

        private async System.Threading.Tasks.Task OpenRemoteNonTradePeriodsAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_instance_get_non_trade_periods", new { type = _serverType, number = _serverNumber });
                if (!response.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("The VPS did not return non-trading period settings.");
                NonTradePeriods periods = new NonTradePeriods("VpsRemote_" + _serverType + "_" + _serverNumber);
                periods.LoadFromSaveArray(values.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList());
                periods.SaveCallback = settings => _ = SaveRemotePeriodsAsync(settings);
                new NonTradePeriodsUi(periods) { Owner = this }.ShowDialog();
                await SaveRemotePeriodsAsync(periods.GetFullSaveArray());
            }
            catch (Exception ex) { ShowError("Could not open remote non-trading periods: " + ex.Message); }
        }

        private async System.Threading.Tasks.Task SaveRemotePeriodsAsync(List<string> values)
        {
            await _remoteSettingsSaveLock.WaitAsync();
            try { await _client.CallToolAsync("server_instance_set_non_trade_periods", new { type = _serverType, number = _serverNumber, values = values.ToArray() }); }
            catch (Exception ex) { ShowError("Could not save remote non-trading periods: " + ex.Message); }
            finally { _remoteSettingsSaveLock.Release(); }
        }

        private static Security SecurityFromJson(JsonElement item)
        {
            Security security = new Security { Name = GetString(item, "name"), NameFull = GetString(item, "nameFull"), NameId = GetString(item, "nameId"), NameClass = GetString(item, "nameClass"), Exchange = GetString(item, "exchange"), Lot = ReadDecimal(item, "lot"), PriceStep = ReadDecimal(item, "priceStep"), PriceStepCost = ReadDecimal(item, "priceStepCost"), Decimals = GetInt(item, "decimals"), DecimalsVolume = GetInt(item, "decimalsVolume"), VolumeStep = ReadDecimal(item, "volumeStep"), MinTradeAmount = ReadDecimal(item, "minTradeAmount"), MarginBuy = ReadDecimal(item, "marginBuy"), MarginSell = ReadDecimal(item, "marginSell"), PriceLimitLow = ReadDecimal(item, "priceLimitLow"), PriceLimitHigh = ReadDecimal(item, "priceLimitHigh"), Strike = ReadDecimal(item, "strike") };
            Enum.TryParse(GetString(item, "securityType"), true, out security.SecurityType);
            Enum.TryParse(GetString(item, "minTradeAmountType"), true, out security.MinTradeAmountType);
            Enum.TryParse(GetString(item, "optionType"), true, out security.OptionType);
            Enum.TryParse(GetString(item, "state"), true, out security.State);
            if (item.TryGetProperty("expiration", out JsonElement expiry) && expiry.ValueKind == JsonValueKind.String) DateTime.TryParse(expiry.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out security.Expiration);
            return security;
        }

        private static decimal ReadDecimal(JsonElement value, string property) => value.TryGetProperty(property, out JsonElement item) && item.TryGetDecimal(out decimal number) ? number : 0m;
        private async System.Threading.Tasks.Task RefreshStatusAsync()
        {
            try
            {
                JsonElement status = await _client.CallToolAsync("server_instance_get_status", new { type = _serverType, number = _serverNumber });
                LabelStatus.Content = GetString(status, "status");
            }
            catch (Exception ex) { LabelStatus.Content = ex.Message; }
        }

        private async System.Threading.Tasks.Task RefreshLogAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_instance_get_log", new { type = _serverType, number = _serverNumber, count = 200 });
                if (!response.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array) return;
                _logGrid.Rows.Clear();
                foreach (JsonElement message in messages.EnumerateArray())
                    _logGrid.Rows.Add(GetString(message, "time"), GetString(message, "type"), GetString(message, "message"));
            }
            catch (Exception ex) { ShowError("Could not load connector log: " + ex.Message); }
        }

        private async System.Threading.Tasks.Task RefreshConnectionsAsync()
        {
            try
            {
                JsonElement response = await _client.CallToolAsync("server_management_get_list", null);
                _connectionsGrid.Rows.Clear();
                if (response.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in response.EnumerateArray())
                    {
                        if (!string.Equals(GetString(item, "type"), _serverType, StringComparison.OrdinalIgnoreCase)) continue;
                        int number = GetInt(item, "number");
                        string status = GetString(item, "status");
                        string instanceName = GetString(item, "name");
                        string prefix = string.Empty;
                        string prefixMarker = _serverType + "_" + number + "_";
                        if (number > 0 && instanceName.StartsWith(prefixMarker, StringComparison.OrdinalIgnoreCase))
                            prefix = instanceName.Substring(prefixMarker.Length);
                        DataGridViewRow row = new DataGridViewRow();
                        row.Cells.Add(new DataGridViewTextBoxCell { Value = instanceName });
                        row.Cells.Add(new DataGridViewTextBoxCell { Value = number });
                        row.Cells.Add(new DataGridViewTextBoxCell { Value = prefix });
                        row.Cells.Add(new DataGridViewTextBoxCell { Value = status });
                        row.Cells.Add(new DataGridViewButtonCell { Value = OsLocalization.Market.ButtonConnect });
                        row.Cells.Add(new DataGridViewButtonCell { Value = OsLocalization.Market.ButtonDisconnect });
                        row.Cells.Add(number == 0 ? new DataGridViewTextBoxCell() : new DataGridViewButtonCell { Value = OsLocalization.Market.Label47 });
                        row.Tag = number;
                        _connectionsGrid.Rows.Add(row);
                        if (number == 0) row.Cells[6].ReadOnly = true;
                        row.Cells[3].Style.ForeColor = string.Equals(status, "Connect", StringComparison.OrdinalIgnoreCase)
                            ? System.Drawing.Color.Green : System.Drawing.Color.Red;
                    }
                }
                DataGridViewRow addRow = new DataGridViewRow();
                for (int i = 0; i < 6; i++) addRow.Cells.Add(new DataGridViewTextBoxCell());
                addRow.Cells.Add(new DataGridViewButtonCell { Value = OsLocalization.Market.Label170 });
                addRow.Tag = -1;
                _connectionsGrid.Rows.Add(addRow);
                for (int i = 0; i < 6; i++) addRow.Cells[i].ReadOnly = true;
            }
            catch (Exception ex) { ShowError("Could not load remote connector instances: " + ex.Message); }
        }

        private async void ConnectionsGrid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex > 6) return;
            int number = Convert.ToInt32(_connectionsGrid.Rows[e.RowIndex].Tag, CultureInfo.InvariantCulture);
            try
            {
                if (number == -1 && e.ColumnIndex == 6)
                {
                    JsonElement created = await _client.CallToolAsync("server_instance_create", new { type = _serverType });
                    _serverNumber = GetInt(created, "number");
                    _serverName = GetString(created, "name");
                    LabelCurrentConnectionName.Content = OsLocalization.Market.Label164 + " " + _serverName;
                    await RefreshAllAsync();
                    return;
                }
                if (number < 0) return;
                _serverNumber = number;
                _serverName = _connectionsGrid.Rows[e.RowIndex].Cells[0].Value?.ToString() ?? _serverType;
                LabelCurrentConnectionName.Content = OsLocalization.Market.Label164 + " " + _serverName;
                if (e.ColumnIndex < 4)
                {
                    await RefreshParametersAsync();
                    await RefreshStatusAsync();
                    return;
                }
                if (e.ColumnIndex == 4)
                    await _client.CallToolAsync("server_instance_connect", new { type = _serverType, number = number });
                else if (e.ColumnIndex == 5)
                    await _client.CallToolAsync("server_instance_disconnect", new { type = _serverType, number = number });
                else if (e.ColumnIndex == 6 && number > 0)
                {
                    MessageBoxResult answer = System.Windows.MessageBox.Show(OsLocalization.Market.Label47 + " " + _serverType + " #" + number + "?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes) return;
                    await _client.CallToolAsync("server_instance_delete", new { type = _serverType, number = number });
                    if (number == _serverNumber)
                    {
                        _serverNumber = 0;
                        _serverName = _serverType;
                        LabelCurrentConnectionName.Content = OsLocalization.Market.Label164 + " " + _serverName;
                    }
                }
                await RefreshAllAsync();
            }
            catch (Exception ex) { ShowError("Remote connector action failed: " + ex.Message); }
        }

        private async void ButtonConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _client.CallToolAsync("server_instance_connect", new { type = _serverType, number = _serverNumber });
                await RefreshStatusAsync();
                await RefreshConnectionsAsync();
            }
            catch (Exception ex) { ShowError("Could not connect the VPS server: " + ex.Message); }
        }

        private async void ButtonAbort_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _client.CallToolAsync("server_instance_disconnect", new { type = _serverType, number = _serverNumber });
                await RefreshStatusAsync();
                await RefreshConnectionsAsync();
            }
            catch (Exception ex) { ShowError("Could not disconnect the VPS server: " + ex.Message); }
        }

        private async void TabControlSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabItemLog.IsSelected && _logGrid != null) await RefreshLogAsync();
        }

        private static string GetString(JsonElement value, string property) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item)
                ? item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString()
                : string.Empty;

        private static int GetInt(JsonElement value, string property) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out JsonElement item) && item.TryGetInt32(out int number) ? number : 0;

        private void ShowError(string message) => System.Windows.MessageBox.Show(message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);

        private sealed class RemoteParameter
        {
            public string Name;
            public string Type;
            public string Comment;
            public JsonElement Value;
            public string MaskedValue;
            public List<string> EnumValues = new List<string>();
            public bool IsSecret;
            public string ButtonAction;
        }
    }
}
