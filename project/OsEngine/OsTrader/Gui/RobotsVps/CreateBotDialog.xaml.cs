/*
 * VPS strategy picker. Layout and table behavior follow Robots/BotCreateUi2;
 * robot metadata and creation are supplied by the remote MCP server.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Layout;
using OsEngine.Market;
using OsEngine.MCP.Client;
using OsEngine.Logging;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public partial class CreateBotDialog : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly List<RemoteRobotDescription> _robots = new List<RemoteRobotDescription>();
        private readonly HashSet<string> _existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private DataGridView _grid;
        private RemoteRobotDescription _selected;
        private readonly List<int> _searchResults = new List<int>();
        private int _searchIndex;

        public string SelectedStrategy => _selected?.ClassName;
        public string BotName { get; private set; }

        public CreateBotDialog(RemoteMcpClient client)
        {
            InitializeComponent();
            _client = client;
            StickyBorders.Listen(this);
            StartupLocation.Start_MouseInCentre(this);
            Title = OsLocalization.Trader.Label59;
            LabelName.Content = OsLocalization.Trader.Label61;
            ButtonAccept.Content = OsLocalization.Trader.Label17;
            LabelLocation.Content = OsLocalization.Trader.Label295;
            ButtonUpdateRobots.Content = OsLocalization.Trader.Label303;
            TextBoxSearchSecurity.Text = OsLocalization.Market.Label64;

            ComboBoxLocation.Items.Add("All");
            ComboBoxLocation.Items.Add("Include");
            ComboBoxLocation.Items.Add("Script");
            ComboBoxLocation.SelectedItem = "All";
            TextBoxName.Text = "MyNewBot";
            TextBoxName.MaxLength = 50;
            ButtonRightInSearchResults.Visibility = Visibility.Hidden;
            ButtonLeftInSearchResults.Visibility = Visibility.Hidden;
            LabelCurrentResultShow.Visibility = Visibility.Hidden;
            LabelCommasResultShow.Visibility = Visibility.Hidden;
            LabelCountResultsShow.Visibility = Visibility.Hidden;
            CreateTable();
            Loaded += Dialog_Loaded;
            Closed += Dialog_Closed;
        }

        private async void Dialog_Loaded(object sender, RoutedEventArgs e) => await LoadRemoteData(false);

        private async System.Threading.Tasks.Task LoadRemoteData(bool refresh)
        {
            ButtonUpdateRobots.IsEnabled = false;
            try
            {
                JsonElement result = await _client.CallToolAsync("wiki_robots_list",
                    new { include_engines = false, refresh = refresh }).ConfigureAwait(true);
                _robots.Clear();
                if (result.TryGetProperty("robots", out JsonElement robots) && robots.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in robots.EnumerateArray())
                    {
                        if (item.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                            continue;
                        _robots.Add(RemoteRobotDescription.FromJson(item));
                    }
                }

                JsonElement bots = await _client.CallToolAsync("bot_get_list", new { }).ConfigureAwait(true);
                _existingNames.Clear();
                if (bots.TryGetProperty("bots", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement bot in list.EnumerateArray())
                        _existingNames.Add(GetString(bot, "name"));
                UpdateTable();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("wiki_robots_list failed: " + ex.Message);
            }
            finally { ButtonUpdateRobots.IsEnabled = true; }
        }

        private void CreateTable()
        {
            _grid = DataGridFactory.GetDataGridView(DataGridViewSelectionMode.FullRowSelect, DataGridViewAutoSizeRowsMode.AllCells);
            _grid.ScrollBars = ScrollBars.Vertical;
            _grid.DefaultCellStyle.SelectionBackColor = Themes.ThemeManager.GetColorWinForms("GridSelectionBackColor");
            _grid.DefaultCellStyle.SelectionForeColor = Themes.ThemeManager.GetColorWinForms("ColorForeground");
            DataGridViewTextBoxCell cell = new DataGridViewTextBoxCell { Style = _grid.DefaultCellStyle };
            AddColumn("#", 30, DataGridViewAutoSizeColumnMode.None, cell);
            AddColumn(OsLocalization.Trader.Label60, 0, DataGridViewAutoSizeColumnMode.Fill, cell);
            AddColumn(OsLocalization.Trader.Label295, 110, DataGridViewAutoSizeColumnMode.None, cell);
            AddColumn(OsLocalization.Trader.Label296, 90, DataGridViewAutoSizeColumnMode.None, cell);
            AddColumn(OsLocalization.Trader.Label298, 0, DataGridViewAutoSizeColumnMode.Fill, cell);
            AddColumn(OsLocalization.Trader.Label297, 90, DataGridViewAutoSizeColumnMode.None, cell);
            HostBots.Child = _grid;
            _grid.CellClick += Grid_CellClick;
            _grid.DataError += Grid_DataError;
        }

        private void AddColumn(string header, int width, DataGridViewAutoSizeColumnMode mode, DataGridViewCell cell)
        {
            _grid.Columns.Add(new DataGridViewColumn { CellTemplate = cell, HeaderText = header, ReadOnly = true,
                Width = width, AutoSizeMode = mode, SortMode = DataGridViewColumnSortMode.NotSortable });
        }

        private void UpdateTable()
        {
            if (_grid == null) return;
            _grid.Rows.Clear();
            _selected = null;
            string location = ComboBoxLocation.SelectedItem?.ToString() ?? "All";
            int number = 0;
            foreach (RemoteRobotDescription robot in _robots)
            {
                if (location != "All" && !string.Equals(location, robot.Location, StringComparison.OrdinalIgnoreCase)) continue;
                DataGridViewRow row = new DataGridViewRow();
                row.Cells.Add(new DataGridViewTextBoxCell { Value = (++number).ToString() });
                row.Cells.Add(new DataGridViewTextBoxCell { Value = robot.ClassName });
                row.Cells.Add(new DataGridViewTextBoxCell { Value = robot.Location });
                row.Cells.Add(new DataGridViewTextBoxCell { Value = robot.Sources });
                row.Cells.Add(new DataGridViewTextBoxCell { Value = robot.Indicators });
                row.Cells.Add(new DataGridViewButtonCell { Value = string.IsNullOrWhiteSpace(robot.Description) ? "" : "?" });
                row.Tag = robot;
                _grid.Rows.Add(row);
            }
            ApplySearch();
        }

        private void ApplySearch()
        {
            _searchResults.Clear();
            string search = TextBoxSearchSecurity.Text?.Trim();
            if (!string.IsNullOrEmpty(search) && search != OsLocalization.Market.Label64)
            {
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    RemoteRobotDescription robot = _grid.Rows[i].Tag as RemoteRobotDescription;
                    if (robot != null && (Has(robot.ClassName, search) || Has(robot.Sources, search) || Has(robot.Indicators, search)))
                        _searchResults.Add(i);
                }
            }
            if (_searchResults.Count > 0) { _searchIndex = 0; SelectSearchResult(); }
            bool visible = _searchResults.Count > 1;
            ButtonRightInSearchResults.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            ButtonLeftInSearchResults.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            LabelCurrentResultShow.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            LabelCommasResultShow.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            LabelCountResultsShow.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (visible)
            {
                LabelCurrentResultShow.Content = (_searchIndex + 1).ToString();
                LabelCountResultsShow.Content = _searchResults.Count.ToString();
            }
        }

        private void SelectSearchResult()
        {
            if (_searchResults.Count == 0) return;
            int index = _searchResults[_searchIndex];
            _grid.ClearSelection();
            _grid.Rows[index].Selected = true;
            _grid.FirstDisplayedScrollingRowIndex = index;
            _selected = _grid.Rows[index].Tag as RemoteRobotDescription;
        }

        private static bool Has(string value, string search) => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        private void Grid_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _grid.Rows.Count) return;
            _selected = _grid.Rows[e.RowIndex].Tag as RemoteRobotDescription;
            if (e.ColumnIndex == 5 && !string.IsNullOrWhiteSpace(_selected?.Description))
                new CustomMessageBoxUi(_selected.Description).ShowDialog();
            // Script files live on the VPS; the original local Explorer action is not applicable here.
        }

        private void Grid_DataError(object sender, DataGridViewDataErrorEventArgs e) =>
            ServerMaster.SendNewLogMessage(e.ToString(), LogMessageType.Error);

        private void ComboBoxLocation_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTable();
        private void TextBoxSearchSecurity_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();
        private void TextBoxSearchSecurity_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        { if (TextBoxSearchSecurity.Text == OsLocalization.Market.Label64) TextBoxSearchSecurity.Text = ""; }
        private void TextBoxSearchSecurity_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        { if (TextBoxSearchSecurity.Text == "" && !TextBoxSearchSecurity.IsKeyboardFocused) TextBoxSearchSecurity.Text = OsLocalization.Market.Label64; }
        private void TextBoxSearchSecurity_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        { if (TextBoxSearchSecurity.Text == "") TextBoxSearchSecurity.Text = OsLocalization.Market.Label64; }

        private void ButtonRightInSearchResults_Click(object sender, RoutedEventArgs e)
        { if (_searchResults.Count > 0) { _searchIndex = (_searchIndex + 1) % _searchResults.Count; SelectSearchResult(); ApplySearchControls(); } }
        private void ButtonLeftInSearchResults_Click(object sender, RoutedEventArgs e)
        { if (_searchResults.Count > 0) { _searchIndex = (_searchIndex - 1 + _searchResults.Count) % _searchResults.Count; SelectSearchResult(); ApplySearchControls(); } }
        private void ApplySearchControls()
        {
            LabelCurrentResultShow.Content = (_searchIndex + 1).ToString();
            LabelCountResultsShow.Content = _searchResults.Count.ToString();
        }

        private async void ButtonUpdateRobots_Click(object sender, RoutedEventArgs e)
        {
            AcceptDialogUi confirm = new AcceptDialogUi(OsLocalization.Trader.Label305);
            confirm.ShowDialog();
            if (confirm.UserAcceptAction) await LoadRemoteData(true);
        }

        private void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) { new CustomMessageBoxUi(OsLocalization.Trader.Label304).ShowDialog(); return; }
            string name = SanitizeName(TextBoxName.Text?.Trim() ?? "");
            if (string.IsNullOrWhiteSpace(name) || _existingNames.Contains(name))
            { new CustomMessageBoxUi(OsLocalization.Trader.Label436).ShowDialog(); return; }
            BotName = name;
            DialogResult = true;
        }

        private static string SanitizeName(string name)
        {
            char[] invalid = { '/', '\\', '*', '-', '+', ':', '@', ';', '%', '>', '<', '^', '{', '}', '[', ']', '_', '$', '#', '!', '&', '?', '=', ',', '.', '\'', '|', '~', '№', '"', '`', '(' , ')' };
            return new string(name.Where(c => !invalid.Contains(c)).ToArray());
        }

        private void ButtonWhyNeedName_Click(object sender, RoutedEventArgs e) => new CustomMessageBoxUi(OsLocalization.Trader.Label301).ShowDialog();
        private void ButtonWhyLocation_Click(object sender, RoutedEventArgs e) => new CustomMessageBoxUi(OsLocalization.Trader.Label302).ShowDialog();
        private void ButtonRobots_Click(object sender, RoutedEventArgs e)
        { try { InteractiveInstructions.TesterLightPosts.Link11.ShowLinkInBrowser(); } catch (Exception ex) { ServerMaster.SendNewLogMessage(ex.ToString(), LogMessageType.Error); } }

        private void Dialog_Closed(object sender, EventArgs e)
        {
            try
            {
                if (HostBots != null) HostBots.Child = null;
                if (_grid != null)
                {
                    _grid.CellClick -= Grid_CellClick;
                    _grid.DataError -= Grid_DataError;
                    DataGridFactory.ClearLinks(_grid);
                    _grid.Rows.Clear();
                    _grid.Columns.Clear();
                    _grid.Dispose();
                    _grid = null;
                }
                Closed -= Dialog_Closed;
            }
            catch (Exception ex) { ServerMaster.SendNewLogMessage(ex.ToString(), LogMessageType.Error); }
        }

        private static string GetString(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

        private class RemoteRobotDescription
        {
            public string ClassName, Location, Description, Sources, Indicators;
            public static RemoteRobotDescription FromJson(JsonElement e)
            {
                return new RemoteRobotDescription
                {
                    ClassName = GetString(e, "class_name"),
                    Location = GetString(e, "location"),
                    Description = GetString(e, "description"),
                    Sources = ReadArray(e, "sources", x => x.ValueKind == JsonValueKind.Object ?
                        GetString(x, "type") + (x.TryGetProperty("count", out JsonElement count) && count.ToString() != "1" ? " " + count.ToString() : "") : x.ToString()),
                    Indicators = ReadArray(e, "indicators", x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.ToString())
                };
            }
            private static string ReadArray(JsonElement e, string name, Func<JsonElement, string> convert)
            {
                if (!e.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array) return "";
                return string.Join(Environment.NewLine, list.EnumerateArray().Select(convert).Where(x => !string.IsNullOrWhiteSpace(x)));
            }
        }
    }
}
