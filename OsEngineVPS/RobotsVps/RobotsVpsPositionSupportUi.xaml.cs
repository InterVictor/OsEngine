using System;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote clone of OsTrader/Panels/Tab/Internal/BotManualControlUi ("Position support" button on the
    /// Control tab of the chart window). Fully wired: bot_get_position_support/bot_set_position_support
    /// already expose the whole BotManualControl DTO 1:1, so every field here is real, not a stub.
    /// Two things intentionally differ from the original, both because the underlying object lives only on
    /// the local connector, not over MCP:
    /// - Order lifetime type (ComboBoxOrdersTypeTime): original filters the list by
    ///   IServerPermission.OrdersLifeTimeRealization; here all three values are always offered, and the
    ///   server itself is the source of truth if one is rejected.
    /// - Maker-only checkbox: original disables it when the connector doesn't support
    ///   HaveOnlyMakerLimitsRealization; here it stays enabled.
    /// The "posts" help-link button is a local-only feature (InteractiveInstructions), kept visible per the
    /// project rule but stubbed — see ButtonBotManualControl_Click.
    /// </summary>
    public partial class RobotsVpsPositionSupportUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly string _tabName;
        private bool _loaded;

        public RobotsVpsPositionSupportUi(RemoteMcpClient client, string botId, string tabName)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;
            _tabName = tabName;

            Title = OsLocalization.Trader.Label85;
            LabelStop.Content = OsLocalization.Trader.Label86;
            LabelProfit.Content = OsLocalization.Trader.Label87;
            LabelPositionClosing.Content = OsLocalization.Trader.Label88;
            LabelPositionOpening.Content = OsLocalization.Trader.Label89;
            LabelCloseOrderReject.Content = OsLocalization.Trader.Label90;
            CheckBoxStopIsOn.Content = OsLocalization.Trader.Label91;
            CheckBoxProfitIsOn.Content = OsLocalization.Trader.Label91;
            LabelSlippage1.Content = OsLocalization.Trader.Label92;
            LabelSlippage2.Content = OsLocalization.Trader.Label92;
            LabelSlippage3.Content = OsLocalization.Trader.Label92;
            LabelFromEntryToStop.Content = OsLocalization.Trader.Label93;
            LabelFromEntryToProfit.Content = OsLocalization.Trader.Label94;
            CheckBoxSecondToCloseIsOn.Content = OsLocalization.Trader.Label95;
            CheckBoxSetbackToCloseIsOn.Content = OsLocalization.Trader.Label96;
            CheckBoxSetbackToOpenIsOn.Content = OsLocalization.Trader.Label96;
            CheckBoxSecondToOpenIsOn.Content = OsLocalization.Trader.Label97;
            ButtonAccept.Content = OsLocalization.Trader.Label17;
            CheckBoxDoubleExitIsOnIsOn.Content = OsLocalization.Trader.Label99;
            LabelValuesType.Content = OsLocalization.Trader.Label158;
            LabelOrdersTypeTime.Content = OsLocalization.Trader.Label422;
            CheckBoxLimitsMakerOnly.Content = OsLocalization.Trader.Label623;

            // 1:1 со значениями enum'ов оригинала (ManualControlValuesType/OrderPriceType/OrderTypeTime) —
            // серверный DTO (bot_get/set_position_support) использует те же строковые значения (enum.ToString()).
            ComboBoxValuesType.Items.Add("MinPriceStep");
            ComboBoxValuesType.Items.Add("Absolute");
            ComboBoxValuesType.Items.Add("Percent");

            ComboBoxTypeDoubleExitOrder.Items.Add("Limit");
            ComboBoxTypeDoubleExitOrder.Items.Add("Market");

            ComboBoxOrdersTypeTime.Items.Add("Specified");
            ComboBoxOrdersTypeTime.Items.Add("GTC");
            ComboBoxOrdersTypeTime.Items.Add("Day");

            Loaded += Window_Loaded;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                JsonElement s = await _client.CallToolAsync("bot_get_position_support", new { bot_id = _botId, tab_name = _tabName });

                CheckBoxStopIsOn.IsChecked = ReadBool(s, "stop_is_on");
                TextBoxStopPercentLength.Text = FormatNumber(ReadDecimal(s, "stop_distance"));
                TextBoxSlippageStop.Text = FormatNumber(ReadDecimal(s, "stop_slippage"));

                CheckBoxProfitIsOn.IsChecked = ReadBool(s, "profit_is_on");
                TextBoxProfitPercentLength.Text = FormatNumber(ReadDecimal(s, "profit_distance"));
                TextBoxSlippageProfit.Text = FormatNumber(ReadDecimal(s, "profit_slippage"));

                CheckBoxSecondToCloseIsOn.IsChecked = ReadBool(s, "second_to_close_is_on");
                TextBoxSecondToClose.Text = FormatNumber(ReadDecimal(s, "second_to_close"));

                CheckBoxSetbackToCloseIsOn.IsChecked = ReadBool(s, "setback_to_close_is_on");
                TextBoxSetbackToClose.Text = FormatNumber(ReadDecimal(s, "setback_to_close_position"));

                CheckBoxDoubleExitIsOnIsOn.IsChecked = ReadBool(s, "double_exit_is_on");
                string doubleExitType = ReadString(s, "type_double_exit_order");
                ComboBoxTypeDoubleExitOrder.SelectedItem = ComboBoxTypeDoubleExitOrder.Items.Contains(doubleExitType) ? doubleExitType : "Limit";
                TextBoxSlippageDoubleExit.Text = FormatNumber(ReadDecimal(s, "double_exit_slippage"));

                CheckBoxSecondToOpenIsOn.IsChecked = ReadBool(s, "second_to_open_is_on");
                TextBoxSecondToOpen.Text = FormatNumber(ReadDecimal(s, "second_to_open"));

                CheckBoxSetbackToOpenIsOn.IsChecked = ReadBool(s, "setback_to_open_is_on");
                TextBoxSetbackToOpen.Text = FormatNumber(ReadDecimal(s, "setback_to_open_position"));

                string valuesType = ReadString(s, "values_type");
                ComboBoxValuesType.SelectedItem = ComboBoxValuesType.Items.Contains(valuesType) ? valuesType : "MinPriceStep";

                string orderTypeTime = ReadString(s, "order_type_time");
                ComboBoxOrdersTypeTime.SelectedItem = ComboBoxOrdersTypeTime.Items.Contains(orderTypeTime) ? orderTypeTime : "Specified";

                CheckBoxLimitsMakerOnly.IsChecked = ReadBool(s, "limits_maker_only");

                _loaded = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not load position support settings: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }

        // Та же валидация, что и в оригинале (BotManualControlUi.ButtonAccept_Click).
        private async void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;

            try
            {
                if (Convert.ToInt32(TextBoxSecondToOpen.Text) <= 0 ||
                    Convert.ToInt32(TextBoxSecondToClose.Text) <= 0 ||
                    TextBoxStopPercentLength.Text.ToDecimal() <= 0 ||
                    TextBoxSlippageStop.Text.ToDecimal() <= 0 ||
                    TextBoxProfitPercentLength.Text.ToDecimal() <= 0 ||
                    TextBoxSlippageProfit.Text.ToDecimal() <= 0 ||
                    TextBoxSetbackToClose.Text.ToDecimal() <= 0 ||
                    TextBoxSetbackToOpen.Text.ToDecimal() <= 0 ||
                    TextBoxSlippageDoubleExit.Text.ToDecimal() < -100)
                {
                    throw new Exception();
                }
            }
            catch (Exception)
            {
                System.Windows.MessageBox.Show(OsLocalization.Trader.Label13);
                return;
            }

            try
            {
                await _client.CallToolAsync("bot_set_position_support", new
                {
                    bot_id = _botId,
                    tab_name = _tabName,
                    stop_is_on = CheckBoxStopIsOn.IsChecked == true,
                    stop_distance = TextBoxStopPercentLength.Text.ToDecimal(),
                    stop_slippage = TextBoxSlippageStop.Text.ToDecimal(),
                    profit_is_on = CheckBoxProfitIsOn.IsChecked == true,
                    profit_distance = TextBoxProfitPercentLength.Text.ToDecimal(),
                    profit_slippage = TextBoxSlippageProfit.Text.ToDecimal(),
                    second_to_open_is_on = CheckBoxSecondToOpenIsOn.IsChecked == true,
                    second_to_open = Convert.ToInt32(TextBoxSecondToOpen.Text),
                    second_to_close_is_on = CheckBoxSecondToCloseIsOn.IsChecked == true,
                    second_to_close = Convert.ToInt32(TextBoxSecondToClose.Text),
                    setback_to_open_is_on = CheckBoxSetbackToOpenIsOn.IsChecked == true,
                    setback_to_open_position = TextBoxSetbackToOpen.Text.ToDecimal(),
                    setback_to_close_is_on = CheckBoxSetbackToCloseIsOn.IsChecked == true,
                    setback_to_close_position = TextBoxSetbackToClose.Text.ToDecimal(),
                    double_exit_is_on = CheckBoxDoubleExitIsOnIsOn.IsChecked == true,
                    type_double_exit_order = ComboBoxTypeDoubleExitOrder.SelectedItem as string ?? "Limit",
                    double_exit_slippage = TextBoxSlippageDoubleExit.Text.ToDecimal(),
                    values_type = ComboBoxValuesType.SelectedItem as string ?? "MinPriceStep",
                    order_type_time = ComboBoxOrdersTypeTime.SelectedItem as string ?? "Specified",
                    limits_maker_only = CheckBoxLimitsMakerOnly.IsChecked == true
                });

                Close();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not save position support settings: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Карточка справки (InteractiveInstructions) — локальная фича, к данным VPS не относится. Явная заглушка.
        private void ButtonBotManualControl_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show("Instruction posts are not available remotely.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static string ReadString(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static bool ReadBool(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static decimal ReadDecimal(JsonElement e, string prop) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out JsonElement v) && v.TryGetDecimal(out decimal d) ? d : 0m;

        private static string FormatNumber(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);
    }
}
