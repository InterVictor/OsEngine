using System;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>Shares the active remote MCP session with the independent Robots.VPS workspace.</summary>
    public static class VpsRemoteSession
    {
        public static RemoteMcpClient Client { get; private set; }
        public static event Action<RemoteMcpClient> ClientChanged;

        public static void SetClient(RemoteMcpClient client)
        {
            Client = client;
            ClientChanged?.Invoke(client);
        }
    }
}
