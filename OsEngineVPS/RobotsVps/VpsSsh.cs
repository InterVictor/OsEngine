// Shared SSH pieces for Robots.VPS: login data (key file / stored key / password) and host key pinning.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Where and how to log in over SSH. Several ways can be given at once; SSH.NET tries them in order:
    // the key registered for this computer (stored encrypted in the settings), a private key file, a password.
    internal sealed class VpsSshCredentials
    {
        public string Host { get; private set; }
        public int Port { get; private set; } = 22;
        public string User { get; private set; }
        public string KeyPath { get; private set; }
        public string PrivateKeyText { get; private set; }
        public string Password { get; private set; }

        public string HostId => Host + ":" + Port;

        // hostText may be "ip" or "ip:port"
        public static VpsSshCredentials Create(string hostText, string user, string keyPath, string privateKeyText,
            string password, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(hostText)) throw new ArgumentException("SSH host is required");
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("SSH user is required");

            VpsSshCredentials result = new VpsSshCredentials { User = user.Trim() };

            string host = hostText.Trim();
            int colon = host.LastIndexOf(':');
            if (colon > 0 && int.TryParse(host.Substring(colon + 1), out int port))
            {
                result.Port = port;
                host = host.Substring(0, colon);
            }
            result.Host = host;

            result.PrivateKeyText = string.IsNullOrWhiteSpace(privateKeyText) ? null : privateKeyText;
            result.Password = string.IsNullOrEmpty(password) ? null : password;

            keyPath = string.IsNullOrWhiteSpace(keyPath) ? null : Environment.ExpandEnvironmentVariables(keyPath.Trim().Trim('"'));
            if (keyPath != null && !Path.IsPathRooted(keyPath)) keyPath = Path.GetFullPath(keyPath);
            if (keyPath != null && !File.Exists(keyPath))
            {
                if (result.PrivateKeyText == null && result.Password == null)
                    throw new FileNotFoundException("SSH private key file was not found", keyPath);
                log?.Invoke("SSH key file not found, skipped: " + keyPath);
                keyPath = null;
            }
            result.KeyPath = keyPath;

            if (result.PrivateKeyText == null && result.KeyPath == null && result.Password == null)
                throw new ArgumentException("Enter an SSH password or an SSH key file");

            return result;
        }

        public ConnectionInfo CreateConnectionInfo()
        {
            List<AuthenticationMethod> methods = new List<AuthenticationMethod>();

            List<IPrivateKeySource> keys = new List<IPrivateKeySource>();
            if (PrivateKeyText != null) keys.Add(new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(PrivateKeyText))));
            if (KeyPath != null) keys.Add(new PrivateKeyFile(KeyPath));
            if (keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(User, keys.ToArray()));

            if (Password != null) methods.Add(new PasswordAuthenticationMethod(User, Password));

            return new ConnectionInfo(Host, Port, User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
        }

        // Connects an SSH.NET client with host key pinning; a changed server key refuses the connection.
        public async Task ConnectAsync(BaseClient client, Action<string> log, CancellationToken cancel)
        {
            string hostKeyError = null;
            client.HostKeyReceived += (s, e) => hostKeyError = VpsKnownHosts.Verify(HostId, e, log);

            try
            {
                await client.ConnectAsync(cancel).ConfigureAwait(false);
            }
            catch (SshConnectionException) when (hostKeyError != null)
            {
                throw new InvalidOperationException(hostKeyError);
            }
        }
    }

    // Trust on first use, like ssh's known_hosts: the first fingerprint seen for host:port is stored in
    // Engine\RobotsVpsKnownHosts.txt; later connections must present the same key.
    internal static class VpsKnownHosts
    {
        private const string KnownHostsFile = @"Engine\RobotsVpsKnownHosts.txt";
        private static readonly object Locker = new object();

        // Sets e.CanTrust; returns an error text when the key has changed, otherwise null.
        public static string Verify(string hostId, HostKeyEventArgs e, Action<string> log)
        {
            string fingerprint = e.FingerPrintSHA256;

            lock (Locker)
            {
                Dictionary<string, string> known = Read();

                if (known.TryGetValue(hostId, out string pinned))
                {
                    e.CanTrust = string.Equals(pinned, fingerprint, StringComparison.Ordinal);

                    return e.CanTrust ? null
                        : $"SSH host key of {hostId} has CHANGED (expected SHA256:{pinned}, got SHA256:{fingerprint}). "
                          + "Connection refused. If the server was reinstalled, remove its line from " + KnownHostsFile;
                }

                known[hostId] = fingerprint;
                Write(known);
                e.CanTrust = true;
                log?.Invoke($"SSH host key of {hostId} saved: SHA256:{fingerprint}");
                return null;
            }
        }

        private static Dictionary<string, string> Read()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(KnownHostsFile))
            {
                return result;
            }

            foreach (string line in File.ReadAllLines(KnownHostsFile))
            {
                string[] parts = line.Split(' ');
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    result[parts[0]] = parts[1];
                }
            }

            return result;
        }

        private static void Write(Dictionary<string, string> known)
        {
            Directory.CreateDirectory("Engine");
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> pair in known) lines.Add(pair.Key + " " + pair.Value);
            File.WriteAllLines(KnownHostsFile, lines);
        }
    }
}
