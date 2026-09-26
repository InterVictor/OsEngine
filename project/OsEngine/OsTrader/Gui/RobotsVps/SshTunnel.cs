// SSH tunnel lifecycle helper for Robots.VPS (in-process, SSH.NET).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Local port forward 127.0.0.1:localPort -> VPS 127.0.0.1:remotePort over one SSH connection, run inside
    // the terminal process (no external ssh.exe). Authenticates with a private key file and/or a password,
    // pins the server host key on first use (like ssh's known_hosts) and keeps the tunnel up by itself:
    // after a network drop or a VPS reboot it reconnects with a growing pause until Dispose.
    internal sealed class SshTunnel : IDisposable
    {
        private const string KnownHostsFile = @"Engine\RobotsVpsKnownHosts.txt";
        private static readonly object KnownHostsLocker = new object();

        private readonly string _host;
        private readonly int _port;
        private readonly string _user;
        private readonly string _keyPath;
        private readonly string _password;
        private readonly int _localPort;
        private readonly int _remotePort;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private SshClient _client;
        private ForwardedPortLocal _forward;
        private string _hostKeyError;
        private bool _ownsTunnel;

        // False when another program (e.g. a manually started ssh -L) already listens on the local port and
        // this tunnel simply reuses it — such a listener is never touched.
        public bool StartedByThisWindow => _ownsTunnel;

        private SshTunnel(string host, int port, string user, string keyPath, string password,
            int localPort, int remotePort, Action<string> log)
        {
            _host = host;
            _port = port;
            _user = user;
            _keyPath = keyPath;
            _password = password;
            _localPort = localPort;
            _remotePort = remotePort;
            _log = log;
        }

        public static async Task<SshTunnel> StartAsync(
            string host, string user, string keyPath, string password, int localPort, int remotePort,
            Action<string> log, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("SSH host is required");
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("SSH user is required");
            if (localPort < 1 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (remotePort < 1 || remotePort > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));

            keyPath = string.IsNullOrWhiteSpace(keyPath) ? null : Environment.ExpandEnvironmentVariables(keyPath.Trim().Trim('"'));
            if (keyPath != null && !Path.IsPathRooted(keyPath)) keyPath = Path.GetFullPath(keyPath);
            if (keyPath != null && !File.Exists(keyPath))
            {
                if (string.IsNullOrEmpty(password)) throw new FileNotFoundException("SSH private key file was not found", keyPath);
                log?.Invoke("SSH key file not found, using the password: " + keyPath);
                keyPath = null;
            }
            if (keyPath == null && string.IsNullOrEmpty(password))
                throw new ArgumentException("Enter an SSH key file or an SSH password");

            // host may be "ip" or "ip:port"
            int port = 22;
            string hostOnly = host.Trim();
            int colon = hostOnly.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostOnly.Substring(colon + 1), out int parsedPort))
            {
                port = parsedPort;
                hostOnly = hostOnly.Substring(0, colon);
            }

            SshTunnel tunnel = new SshTunnel(hostOnly, port, user.Trim(), keyPath, password, localPort, remotePort, log);

            // A manually started tunnel may already be listening. Reuse it, but never terminate a
            // process this window did not create.
            if (await IsListeningAsync(localPort, cancel).ConfigureAwait(false))
            {
                log?.Invoke($"Using existing local listener on 127.0.0.1:{localPort}");
                return tunnel;
            }

            tunnel._ownsTunnel = true;

            try
            {
                await tunnel.ConnectAsync(cancel).ConfigureAwait(false);
            }
            catch
            {
                tunnel.Dispose();
                throw;
            }

            log?.Invoke($"SSH tunnel ready: 127.0.0.1:{localPort} → {hostOnly}:127.0.0.1:{remotePort}");
            _ = tunnel.SuperviseAsync();
            return tunnel;
        }

        private async Task ConnectAsync(CancellationToken cancel)
        {
            CloseConnection();

            List<AuthenticationMethod> methods = new List<AuthenticationMethod>();
            if (_keyPath != null) methods.Add(new PrivateKeyAuthenticationMethod(_user, new PrivateKeyFile(_keyPath)));
            if (!string.IsNullOrEmpty(_password)) methods.Add(new PasswordAuthenticationMethod(_user, _password));

            ConnectionInfo info = new ConnectionInfo(_host, _port, _user, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(15)
            };

            SshClient client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            _hostKeyError = null;
            client.HostKeyReceived += Client_HostKeyReceived;
            client.ErrorOccurred += (s, e) => _log?.Invoke("SSH: " + e.Exception.Message);

            try
            {
                await client.ConnectAsync(cancel).ConfigureAwait(false);
            }
            catch (SshConnectionException) when (_hostKeyError != null)
            {
                client.Dispose();
                throw new InvalidOperationException(_hostKeyError);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            ForwardedPortLocal forward = new ForwardedPortLocal("127.0.0.1", (uint)_localPort, "127.0.0.1", (uint)_remotePort);
            forward.Exception += (s, e) => _log?.Invoke("SSH tunnel: " + e.Exception.Message);
            client.AddForwardedPort(forward);
            forward.Start();

            _client = client;
            _forward = forward;
        }

        // Runs a shell command on the VPS over the tunnel's SSH connection and returns its standard output.
        // Only available when this tunnel owns the connection (not when reusing someone else's listener).
        public async Task<string> RunCommandAsync(string command)
        {
            SshClient client = _client;

            if (client == null || !client.IsConnected)
            {
                throw new InvalidOperationException("SSH connection is not available");
            }

            return await Task.Run(() =>
            {
                using SshCommand result = client.RunCommand(command);

                if (result.ExitStatus != 0)
                {
                    throw new InvalidOperationException($"'{command}' failed ({result.ExitStatus}): {result.Error.Trim()}");
                }

                return result.Result;
            }).ConfigureAwait(false);
        }

        // Keeps the tunnel up: checks the connection every few seconds and reconnects after a drop
        // (network loss, VPS reboot) with a growing pause, 5 s up to 60 s.
        private async Task SuperviseAsync()
        {
            int delaySeconds = 5;

            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (_client != null && _client.IsConnected && _forward != null && _forward.IsStarted)
                {
                    delaySeconds = 5;
                    continue;
                }

                _log?.Invoke("SSH connection lost, reconnecting...");

                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await ConnectAsync(_stop.Token).ConfigureAwait(false);
                        _log?.Invoke("SSH tunnel restored");
                        delaySeconds = 5;
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"SSH reconnect failed ({ex.Message}), next try in {delaySeconds} s");
                    }

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    delaySeconds = Math.Min(delaySeconds * 2, 60);
                }
            }
        }

        // Trust on first use, like ssh's known_hosts: the first fingerprint seen for host:port is stored in
        // Engine\RobotsVpsKnownHosts.txt; later connections must present the same key.
        private void Client_HostKeyReceived(object sender, HostKeyEventArgs e)
        {
            string hostId = _host + ":" + _port;
            string fingerprint = e.FingerPrintSHA256;

            lock (KnownHostsLocker)
            {
                Dictionary<string, string> known = ReadKnownHosts();

                if (known.TryGetValue(hostId, out string pinned))
                {
                    e.CanTrust = string.Equals(pinned, fingerprint, StringComparison.Ordinal);

                    if (!e.CanTrust)
                    {
                        _hostKeyError = $"SSH host key of {hostId} has CHANGED (expected SHA256:{pinned}, got SHA256:{fingerprint}). "
                            + "Connection refused. If the server was reinstalled, remove its line from " + KnownHostsFile;
                    }
                    return;
                }

                known[hostId] = fingerprint;
                WriteKnownHosts(known);
                e.CanTrust = true;
                _log?.Invoke($"SSH host key of {hostId} saved: SHA256:{fingerprint}");
            }
        }

        private static Dictionary<string, string> ReadKnownHosts()
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

        private static void WriteKnownHosts(Dictionary<string, string> known)
        {
            Directory.CreateDirectory("Engine");
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> pair in known) lines.Add(pair.Key + " " + pair.Value);
            File.WriteAllLines(KnownHostsFile, lines);
        }

        private static async Task<bool> IsListeningAsync(int port, CancellationToken cancel)
        {
            try
            {
                using TcpClient client = new TcpClient();
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(500);
                await client.ConnectAsync("127.0.0.1", port, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { return false; }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }

        private void CloseConnection()
        {
            try { if (_forward != null && _forward.IsStarted) _forward.Stop(); } catch { /* already closed */ }
            try { _forward?.Dispose(); } catch { /* already closed */ }
            _forward = null;

            try { if (_client != null && _client.IsConnected) _client.Disconnect(); } catch { /* already closed */ }
            try { _client?.Dispose(); } catch { /* already closed */ }
            _client = null;
        }

        public void Dispose()
        {
            // _stop is only cancelled, not disposed: SuperviseAsync may still be reading its token.
            _stop.Cancel();
            CloseConnection();
        }
    }
}
