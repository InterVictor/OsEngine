/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Strategy picker for "Create..." on the Bots tab of Роботы. VPS. Loads wiki_robots_list from
    /// the remote MCP API once, filters client-side (the remote server may host hundreds of scripts).
    /// </summary>
    public partial class CreateBotDialog : Window
    {
        private readonly RemoteMcpClient _client;
        private List<RobotListItem> _all = new List<RobotListItem>();

        public string SelectedStrategy { get; private set; }
        public string BotName => TextBoxBotName.Text;

        public CreateBotDialog(RemoteMcpClient client)
        {
            InitializeComponent();
            _client = client;
            Loaded += CreateBotDialog_Loaded;
        }

        private async void CreateBotDialog_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // include_engines=false: Engine/ScreenerEngine/ClusterEngine are base classes for scripts,
                // not something you create a bot from directly
                JsonElement result = await _client.CallToolAsync("wiki_robots_list", new { include_engines = false })
                    .ConfigureAwait(true);

                if (result.TryGetProperty("robots", out JsonElement robots) && robots.ValueKind == JsonValueKind.Array)
                {
                    _all = robots.EnumerateArray()
                        .Where(r => !(r.TryGetProperty("error", out JsonElement err) && err.ValueKind == JsonValueKind.String))
                        .Select(r => new RobotListItem
                        {
                            ClassName = GetString(r, "class_name"),
                            Location = GetString(r, "location"),
                            Description = GetString(r, "description")
                        })
                        .OrderBy(r => r.ClassName)
                        .ToList();
                }

                ApplyFilter();
                LabelStatus.Content = $"{_all.Count} strategies";
            }
            catch (Exception ex)
            {
                LabelStatus.Content = "wiki_robots_list failed: " + ex.Message;
            }
        }

        private void TextBoxFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string filter = TextBoxFilter.Text?.Trim();

            IEnumerable<RobotListItem> items = _all;

            if (!string.IsNullOrEmpty(filter))
            {
                items = _all.Where(r =>
                    (r.ClassName?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (r.Description?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            StrategiesDataGrid.ItemsSource = items.ToList();
        }

        private void StrategiesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ButtonOk.IsEnabled = StrategiesDataGrid.SelectedItem is RobotListItem;
        }

        private void StrategiesDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (StrategiesDataGrid.SelectedItem is RobotListItem)
            {
                ButtonOk_Click(sender, e);
            }
        }

        private void ButtonOk_Click(object sender, RoutedEventArgs e)
        {
            if (!(StrategiesDataGrid.SelectedItem is RobotListItem item))
            {
                return;
            }

            SelectedStrategy = item.ClassName;
            DialogResult = true;
        }

        private void ButtonCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private static string GetString(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : "";
    }

    public class RobotListItem
    {
        public string ClassName { get; set; }
        public string Location { get; set; }
        public string Description { get; set; }
    }
}
