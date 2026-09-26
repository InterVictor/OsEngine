using System;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using OsEngine.Language;
using OsEngine.MCP.Client;
using OsEngine.OsTrader.RiskManager;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote copy of OsTrader.RiskManager.RiskManagerUi for a robot on the VPS (chart window -> Risk manager): same
    /// XAML and fields. Values come from bot_risk_manager_get; Accept sends bot_risk_manager_set, which sets them and
    /// calls RiskManager.Save() on the VPS, exactly like the original Accept button.
    /// </summary>
    public partial class RobotsVpsRiskManagerUi
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;

        public RobotsVpsRiskManagerUi(RemoteMcpClient client, string botId)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;

            OsEngine.Layout.StickyBorders.Listen(this);
            OsEngine.Layout.StartupLocation.Start_MouseInCentre(this);

            Title = OsLocalization.Trader.Label12 + " — " + botId;
            LabelMaxRisk.Content = OsLocalization.Trader.Label14;
            LabelMaxLossReactioin.Content = OsLocalization.Trader.Label15;
            CheckBoxIsOn.Content = OsLocalization.Trader.Label16;
            ButtonAccept.Content = OsLocalization.Trader.Label17;

            ComboBoxReaction.Items.Add(RiskManagerReactionType.CloseAndOff);
            ComboBoxReaction.Items.Add(RiskManagerReactionType.ShowDialog);
            ComboBoxReaction.Items.Add(RiskManagerReactionType.None);

            // nothing may be accepted until the current values have arrived from the VPS
            ButtonAccept.IsEnabled = false;
            Loaded += async (s, e) => await LoadAsync();
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            try
            {
                JsonElement data = await _client.CallToolAsync("bot_risk_manager_get", new { bot_id = _botId });

                CheckBoxIsOn.IsChecked = data.TryGetProperty("is_active", out JsonElement active) && active.ValueKind == JsonValueKind.True;
                decimal maxDd = data.TryGetProperty("max_drawdown_to_day_percent", out JsonElement dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetDecimal() : 0;
                TextBoxOpenMaxDd.Text = maxDd.ToString(new CultureInfo("ru-RU"));
                ComboBoxReaction.Text = data.TryGetProperty("reaction_type", out JsonElement reaction) ? reaction.GetString() : "";
                ButtonAccept.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not read the risk manager from the VPS: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }

        private async void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            decimal maxDd;

            try
            {
                maxDd = Convert.ToDecimal(TextBoxOpenMaxDd.Text);
            }
            catch (Exception)
            {
                MessageBox.Show(OsLocalization.Trader.Label13);
                return;
            }

            ButtonAccept.IsEnabled = false;

            try
            {
                await _client.CallToolAsync("bot_risk_manager_set", new
                {
                    bot_id = _botId,
                    is_active = CheckBoxIsOn.IsChecked == true,
                    max_drawdown_to_day_percent = maxDd,
                    reaction_type = ComboBoxReaction.Text
                });
                Close();
            }
            catch (Exception ex)
            {
                ButtonAccept.IsEnabled = true;
                MessageBox.Show("Could not save the risk manager on the VPS: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // the help post opens a web page on this computer — same as the original
        private void ButtonRiskManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InteractiveInstructions.BotStationLightPosts.Link2.ShowLinkInBrowser();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
