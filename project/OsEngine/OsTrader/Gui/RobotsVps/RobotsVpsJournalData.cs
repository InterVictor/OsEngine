using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OsEngine.Entity;
using OsEngine.Journal;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Builds the same List<BotPanelJournal> that OsTraderMaster.ShowCommunityJournal / BotPanel.ShowJournal
    // pass to JournalUi2, but from the VPS: bot_journal_get_panels returns every position in its native
    // save format, and each one is rebuilt here into a real Position via SetDealFromString. The journal
    // windows (verbatim JournalUi2 clones) then render it with the parent's own code, unchanged.
    internal static class RobotsVpsJournalData
    {
        private sealed class RemoteTab
        {
            public int TabNum;
            public List<Position> Positions = new List<Position>();
        }

        private sealed class RemoteBot
        {
            public string BotName;
            public string BotClass;
            public List<RemoteTab> Tabs = new List<RemoteTab>();
        }

        // botName == null -> all robots (community journal).
        public static async Task<List<BotPanelJournal>> LoadAsync(RemoteMcpClient client, string botName)
        {
            List<RemoteBot> bots = await FetchAsync(client, botName).ConfigureAwait(true);
            List<BotPanelJournal> panels = new List<BotPanelJournal>();

            foreach (RemoteBot bot in bots)
            {
                BotPanelJournal panel = new BotPanelJournal();
                panel.BotName = bot.BotName;
                panel.BotClass = bot.BotClass;
                panel._Tabs = new List<BotTabJournal>();

                foreach (RemoteTab tab in bot.Tabs)
                {
                    // IsOsOptimizer: PositionController neither loads from nor saves to local disk in this mode,
                    // so this journal is a pure in-memory copy of the remote one.
                    Journal.Journal journal = new Journal.Journal("RobotsVps_" + bot.BotName + "_" + tab.TabNum, StartProgram.IsOsOptimizer);
                    FillJournal(journal, tab.Positions);
                    panel._Tabs.Add(new BotTabJournal { TabNum = tab.TabNum, Journal = journal });
                }

                panels.Add(panel);
            }

            return panels;
        }

        // Re-reads positions from the VPS into the journals the window already holds, so the parent's own
        // Reload button / auto-reload (both just call RePaint, which re-reads Journal.AllPosition) show fresh
        // data. Like the parent, the set of robots and tabs stays as it was when the window was opened.
        public static async Task RefreshAsync(RemoteMcpClient client, string botName, List<BotPanelJournal> panels)
        {
            if (client == null || !client.IsConnected || panels == null)
            {
                return;
            }

            List<RemoteBot> bots = await FetchAsync(client, botName).ConfigureAwait(true);

            foreach (BotPanelJournal panel in panels)
            {
                RemoteBot bot = bots.Find(b => b.BotName == panel.BotName);

                if (bot == null || panel._Tabs == null)
                {
                    continue;
                }

                foreach (BotTabJournal tab in panel._Tabs)
                {
                    RemoteTab remoteTab = bot.Tabs.Find(t => t.TabNum == tab.TabNum);

                    if (remoteTab == null || tab.Journal == null)
                    {
                        continue;
                    }

                    tab.Journal.Clear();
                    FillJournal(tab.Journal, remoteTab.Positions);
                }
            }
        }

        // The window has already removed the position from its local copy (as the parent does); mirror that
        // on the VPS. On failure the next reload brings the position back, so the user sees it wasn't deleted.
        public static async void DeleteOnServer(RemoteMcpClient client, string botName, int tabNum, int positionNumber)
        {
            try
            {
                await client.CallToolAsync("bot_journal_delete_position",
                    new { bot_name = botName, tab_num = tabNum, position_number = positionNumber }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Роботы.VPS: не удалось удалить позицию " + positionNumber + " на сервере: " + ex.Message,
                    "VPS", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        private static void FillJournal(Journal.Journal journal, List<Position> positions)
        {
            foreach (Position position in positions)
            {
                // SetNewDeal overwrites the position's commission with the journal's own settings,
                // so hand it the position's values first to keep them as they are on the server.
                journal.CommissionType = position.CommissionType;
                journal.CommissionValue = position.CommissionValue;
                journal.SetNewDeal(position);
            }
        }

        private static async Task<List<RemoteBot>> FetchAsync(RemoteMcpClient client, string botName)
        {
            object args = botName == null ? null : new { bot_name = botName };
            JsonElement response = await client.CallToolAsync("bot_journal_get_panels", args).ConfigureAwait(true);

            List<RemoteBot> result = new List<RemoteBot>();

            if (!response.TryGetProperty("bots", out JsonElement bots) || bots.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement bot in bots.EnumerateArray())
            {
                RemoteBot remoteBot = new RemoteBot
                {
                    BotName = ReadString(bot, "bot_name"),
                    BotClass = ReadString(bot, "bot_class")
                };

                if (bot.TryGetProperty("tabs", out JsonElement tabs) && tabs.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tab in tabs.EnumerateArray())
                    {
                        RemoteTab remoteTab = new RemoteTab
                        {
                            TabNum = tab.TryGetProperty("tab_num", out JsonElement num) && num.TryGetInt32(out int n) ? n : remoteBot.Tabs.Count
                        };

                        if (tab.TryGetProperty("positions", out JsonElement positions) && positions.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement saved in positions.EnumerateArray())
                            {
                                string saveString = saved.GetString();

                                if (string.IsNullOrEmpty(saveString))
                                {
                                    continue;
                                }

                                Position position = new Position();
                                position.SetDealFromString(saveString);
                                remoteTab.Positions.Add(position);
                            }
                        }

                        remoteBot.Tabs.Add(remoteTab);
                    }
                }

                result.Add(remoteBot);
            }

            return result;
        }

        private static string ReadString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : string.Empty;
        }
    }
}
