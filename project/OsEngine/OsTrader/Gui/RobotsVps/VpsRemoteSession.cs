using System;
using System.Collections.Generic;
using System.Linq;
using OsEngine.MCP.Client;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Shares the remote MCP sessions with the independent Robots.VPS workspaces. One VPS can run several
    /// independent OsEngine terminals (systemd services osengine, osengine-&lt;name&gt;); the VPS window keeps one
    /// MCP client per terminal here, and each Robots.VPS workspace is bound to one terminal by name.
    /// </summary>
    public static class VpsRemoteSession
    {
        /// <summary>the terminal of the original single-terminal layout (/opt/osengine, service "osengine")</summary>
        public const string MainInstance = "main";

        private static readonly object Locker = new object();
        private static Dictionary<string, RemoteMcpClient> _clients =
            new Dictionary<string, RemoteMcpClient>(StringComparer.OrdinalIgnoreCase);

        /// <summary>raised (on any thread) whenever the set of terminals or their clients change</summary>
        public static event Action InstancesChanged;

        /// <summary>connected terminals, the main one first</summary>
        public static IReadOnlyList<string> InstanceNames
        {
            get
            {
                lock (Locker)
                {
                    return _clients.Keys
                        .OrderBy(n => string.Equals(n, MainInstance, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        public static RemoteMcpClient GetClient(string instanceName)
        {
            lock (Locker)
            {
                return instanceName != null && _clients.TryGetValue(instanceName, out RemoteMcpClient client) ? client : null;
            }
        }

        public static void SetClients(IDictionary<string, RemoteMcpClient> clients)
        {
            lock (Locker)
            {
                _clients = clients == null
                    ? new Dictionary<string, RemoteMcpClient>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, RemoteMcpClient>(clients, StringComparer.OrdinalIgnoreCase);
            }

            InstancesChanged?.Invoke();
        }
    }
}
