using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using OsEngine.Language;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote clone of OsTrader/Gui/BotsMigrationUi (the "Migration" button on the service row of the main
    /// bots grid, BotTabsPainter coluIndex == 10). Layout is a 1:1 copy of BotsMigrationUi.xaml.
    /// OsTraderMaster.SaveBotsPreset/LoadBotsPreset read/write a file path directly — on the desktop app that
    /// path is local because the app and the engine are the same process. Over MCP, the preset itself
    /// (robots + their saved parameter files, as plain text) lives only on the server, but the user explicitly
    /// wants Save/Load to go through a file on THEIR OWN PC, not one on the VPS — so the server tools
    /// (bots_migration_export/bots_migration_import) exchange the preset as text, and this window does the
    /// actual file I/O locally via WinForms SaveFileDialog/OpenFileDialog, exactly where the original puts it.
    /// </summary>
    public partial class RobotsVpsMigrationUi : Window
    {
        private readonly RemoteMcpClient _client;

        public RobotsVpsMigrationUi(RemoteMcpClient client)
        {
            InitializeComponent();
            _client = client;

            Title = OsLocalization.Trader.Label762;
            TextBoxDescription.Text = OsLocalization.Trader.Label763;
            TabItemSave.Header = OsLocalization.Trader.Label766;
            TabItemLoad.Header = OsLocalization.Trader.Label767;
            TextBlockPrefix.Text = OsLocalization.Trader.Label768;
            ButtonSaveBots.Content = OsLocalization.Trader.Label748;
            ButtonLoadBots.Content = OsLocalization.Trader.Label749;
            ButtonClose.Content = OsLocalization.Trader.Label764;

            ButtonSaveBots.Click += ButtonSaveBots_Click;
            ButtonLoadBots.Click += ButtonLoadBots_Click;
            ButtonHint.Click += ButtonHint_Click;
            ButtonClose.Click += ButtonClose_Click;
        }

        private async void ButtonSaveBots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    System.Windows.MessageBox.Show("Connect to VPS first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                string prefix = (TextBoxPrefix.Text ?? string.Empty).Trim();

                // Та же проверка, что в оригинале (BotsMigrationUi.ButtonSaveBots_Click) — до похода на сервер,
                // чтобы не тратить круг на заведомо отклонённый префикс.
                if (!string.IsNullOrEmpty(prefix) && (prefix.Contains("@") || prefix.Contains(":")))
                {
                    System.Windows.MessageBox.Show(OsLocalization.Trader.Label769, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Диалог сохранения — локальный, на машине пользователя (не на сервере/VPS), как и просили.
                System.Windows.Forms.SaveFileDialog dialog = new System.Windows.Forms.SaveFileDialog { Filter = "Txt files|*.txt" };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                JsonElement result = await _client.CallToolAsync("bots_migration_export", new { prefix });
                string content = result.TryGetProperty("content", out JsonElement contentEl) && contentEl.ValueKind == JsonValueKind.String
                    ? contentEl.GetString() : null;

                if (string.IsNullOrEmpty(content))
                {
                    System.Windows.MessageBox.Show("Server returned an empty preset.", "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                File.WriteAllText(dialog.FileName, content);

                int botCount = result.TryGetProperty("bot_count", out JsonElement countEl) && countEl.TryGetInt32(out int c) ? c : 0;
                System.Windows.MessageBox.Show($"Saved {botCount} bot(s) to {dialog.FileName}.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Save failed: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void ButtonLoadBots_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_client == null || !_client.IsConnected)
                {
                    System.Windows.MessageBox.Show("Connect to VPS first.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Диалог открытия — тоже локальный; файл читаем с диска пользователя и отправляем
                // содержимым на сервер, а не путём (пути клиента серверу ничего не говорят).
                System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog { Filter = "Txt files|*.txt" };
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                string content = File.ReadAllText(dialog.FileName);

                JsonElement result = await _client.CallToolAsync("bots_migration_import", new { content });
                int botsAdded = result.TryGetProperty("bots_added", out JsonElement addedEl) && addedEl.TryGetInt32(out int a) ? a : 0;

                System.Windows.MessageBox.Show($"Loaded. {botsAdded} bot(s) added — see the OsEngine log on the server for any per-bot warnings.", "VPS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Load failed: " + ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ButtonHint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Entity.CustomMessageBoxUi ui = new Entity.CustomMessageBoxUi(OsLocalization.Trader.Label770);
                ui.TextBoxMessage.TextAlignment = System.Windows.TextAlignment.Left;
                ui.Height = 290;
                ui.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "VPS", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ButtonClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
