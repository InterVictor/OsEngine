using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using OsEngine.Alerts;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Charts.CandleChart;
namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public partial class RobotsVpsChartWindow
    {
        private readonly List<JsonElement> _remoteAlerts = new List<JsonElement>();
        private readonly List<IIAlert> _paintedRemoteAlerts = new List<IIAlert>();
        private List<Candle> _remoteAlertCandles;
        private RobotsVpsChartAlertUi _remoteAlertEditor;
        private AlertToChart _remoteAlertPreview;
        private string _paintedAlertsJson;
        private JsonElement? SelectedRemoteAlert()
        {
            int index = _alertsGrid?.CurrentCell?.RowIndex ?? -1;
            return index >= 0 && index < _remoteAlerts.Count ? _remoteAlerts[index] : null;
        }
        private async void RemoteAlerts_MouseClick(object sender, MouseEventArgs e)
        {
            try
            {
                if (e.Button != MouseButtons.Right) return;
                int row = _alertsGrid.HitTest(e.X, e.Y).RowIndex;
                if (row >= 0) _alertsGrid.CurrentCell = _alertsGrid.Rows[row].Cells[0];
                JsonElement? selected = row >= 0 ? SelectedRemoteAlert() : null;
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add(OsLocalization.Alerts.ContextMenu1, null, async (s, a) => await DeleteRemoteAlertAsync(selected));
                menu.Items.Add(OsLocalization.Alerts.ContextMenu2, null, async (s, a) => await EditRemoteAlertAsync(selected));
                menu.Items.Add(OsLocalization.Alerts.ContextMenu3, null, (s, a) => OpenRemoteChartAlert(null));
                menu.Items.Add(OsLocalization.Alerts.ContextMenu4, null, async (s, a) => await OpenRemotePriceAlertAsync(null));
                menu.Items[0].Enabled = menu.Items[1].Enabled = selected.HasValue;
                _alertsGrid.ContextMenuStrip?.Dispose();
                _alertsGrid.ContextMenuStrip = menu;
                menu.Show(_alertsGrid, e.Location);
            }
            catch (Exception ex) { RemoteActionError(ex); }
        }
        private async Task EditRemoteAlertAsync(JsonElement? source = null)
        {
            source ??= SelectedRemoteAlert();
            if (!source.HasValue) return;
            if (ReadString(source.Value, "type") == "ChartAlert") OpenRemoteChartAlert(source);
            else await OpenRemotePriceAlertAsync(source);
        }
        private async Task DeleteRemoteAlertAsync(JsonElement? source)
        {
            if (!source.HasValue) return;
            try { await ChangeRemoteAlertAsync(source, null, "delete"); }
            catch (Exception ex) { RemoteActionError(ex); }
        }
        private async Task ChangeRemoteAlertAsync(JsonElement? source, IIAlert alert, string operation = null)
        {
            await _client.CallToolAsync("bot_chart_change_alert", new {
                bot_id = _botId, tab_name = _tabName,
                operation = operation ?? (source.HasValue ? "update" : "create"),
                name = source.HasValue ? ReadString(source.Value, "name") : null,
                expected = source.HasValue ? ReadString(source.Value, "revision") : null,
                alert_type = alert?.TypeAlert.ToString(),
                settings = alert == null ? null : AlertRemoteSettings.Export(alert)
            });
            await RefreshAlertsAsync();
        }
        private async Task OpenRemotePriceAlertAsync(JsonElement? source)
        {
            try
            {
                AlertToPrice alert = source.HasValue
                    ? (AlertToPrice)AlertRemoteSettings.Create("PriceAlert", source.Value.GetProperty("settings"))
                    : new AlertToPrice("VpsDraft_" + Guid.NewGuid().ToString("N")) { VolumeReaction = 1 };
                RobotsVpsPriceAlertUi ui = new RobotsVpsPriceAlertUi(alert) { Owner = this };
                ui.ShowDialog();
                if (ui.Accepted) await ChangeRemoteAlertAsync(source, alert);
            }
            catch (Exception ex) { RemoteActionError(ex); }
        }
        private void OpenRemoteChartAlert(JsonElement? source)
        {
            try
            {
                if (_remoteAlertEditor != null) { _remoteAlertEditor.Activate(); return; }
                AlertToChart alert = source.HasValue
                    ? (AlertToChart)AlertRemoteSettings.Create("ChartAlert", source.Value.GetProperty("settings")) : null;
                _remoteAlertEditor = new RobotsVpsChartAlertUi(alert, preview => {
                    _remoteAlertPreview = preview;
                    PaintRemoteAlerts(true);
                }) { Owner = this };
                _remoteAlertEditor.Closed += async (s, e) => {
                    RobotsVpsChartAlertUi editor = _remoteAlertEditor;
                    _remoteAlertEditor = null;
                    _chartMaster.ChartClickEvent -= RemoteAlertChartClick;
                    _remoteAlertPreview = null;
                    try {
                        if (editor != null && editor.NeedToSave && editor.MyAlert != null)
                            await ChangeRemoteAlertAsync(source, editor.MyAlert);
                        PaintRemoteAlerts(true);
                    } catch (Exception ex) { RemoteActionError(ex); }
                };
                _chartMaster.ChartClickEvent += RemoteAlertChartClick;
                _remoteAlertEditor.Show();
            }
            catch (Exception ex) { RemoteActionError(ex); }
        }
        private void RemoteAlertChartClick(ChartClickType type)
        {
            try
            {
                if (_remoteAlertEditor == null || _remoteAlertCandles == null || _remoteAlertCandles.Count == 0) return;
                int candle = _chartMaster.GetSelectCandleNumber();
                decimal price = _chartMaster.GetCursorSelectPrice();
                _chartMaster.RemoveCursor();
                if (price != 0 && candle >= 0 && candle < _remoteAlertCandles.Count)
                    _remoteAlertEditor.SetFormChart(_remoteAlertCandles, candle, price);
            }
            catch (Exception ex) { RemoteActionError(ex); }
        }
        private void PaintRemoteAlerts(bool force = false)
        {
            if (_chartMaster == null) return;
            string state = string.Join("|", _remoteAlerts.Select(a => a.GetRawText()));
            if (!force && state == _paintedAlertsJson) return;
            _paintedAlertsJson = state;
            foreach (IIAlert old in _paintedRemoteAlerts) _chartMaster.DeleteAlert(old);
            _paintedRemoteAlerts.Clear();
            foreach (JsonElement item in _remoteAlerts)
            {
                if (ReadString(item, "type") != "ChartAlert" || !item.TryGetProperty("settings", out JsonElement settings)) continue;
                IIAlert alert = AlertRemoteSettings.Create("ChartAlert", settings);
                alert.Name = "Vps_" + ReadString(item, "name");
                _paintedRemoteAlerts.Add(alert);
            }
            if (_remoteAlertPreview != null) {
                _remoteAlertPreview.Name = "Vps_Preview";
                _paintedRemoteAlerts.Add(_remoteAlertPreview);
            }
            _chartMaster.PaintAlerts(_paintedRemoteAlerts.ToList(), false);
        }
        private void CloseRemoteAlertEditor()
        {
            if (_remoteAlertEditor != null) { _remoteAlertEditor.NeedToSave = false; _remoteAlertEditor.Close(); }
        }
        private bool _remoteGridAction;
        private async void RemoteGrids_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 3 || _remoteGridAction) return;
            _remoteGridAction = true;
            try
            {
                object numberValue = _gridsGrid.Rows[e.RowIndex].Cells[0].Value;
                int? number = numberValue == null ? null : Convert.ToInt32(numberValue);
                if (number.HasValue && e.ColumnIndex == 4)
                {
                    AcceptDialogUi confirm = new AcceptDialogUi(OsLocalization.Trader.Label443);
                    confirm.ShowDialog();
                    if (!confirm.UserAcceptAction) return;
                    await _client.CallToolAsync("bot_grid_delete", new { bot_id = _botId, tab_name = _tabName, grid_number = number.Value });
                }
                else if (e.ColumnIndex == 3 && number.HasValue || e.ColumnIndex == 4 && !number.HasValue)
                {
                    RobotsVpsTradeGridUi ui = new RobotsVpsTradeGridUi(_client, _botId, _tabName, number) { Owner = this };
                    ui.ShowDialog();
                }
                await RefreshGridsAsync();
            }
            catch (Exception ex) { RemoteActionError(ex); }
            finally { _remoteGridAction = false; }
        }
        private void RemoteActionError(Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
