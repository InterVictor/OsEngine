// SSH tunnel lifecycle helper for Robots.VPS (in-process, SSH.NET).
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    // Local port forward 127.0.0.1:localPort -> VPS 127.0.0.1:remotePort over one SSH connection, run inside
    // the terminal process (no external ssh.exe). Logs in with VpsSshCredentials (stored key / key file /
    // password), pins the server host key on first use (VpsKnownHosts) and keeps the tunnel up by itself:
    // after a network drop or a VPS reboot it reconnects with a growing pause until Dispose.
    internal sealed class SshTunnel : IDisposable
    {
        private readonly VpsSshCredentials _credentials;
        private readonly int _localPort;
        private readonly int _remotePort;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private SshClient _client;
        private ForwardedPortLocal _forward;
        private bool _ownsTunnel;

        // False when another program (e.g. a manually started ssh -L) already listens on the local port and
        // this tunnel simply reuses it — such a listener is never touched.
        public bool StartedByThisWindow => _ownsTunnel;

        private SshTunnel(VpsSshCredentials credentials, int localPort, int remotePort, Action<string> log)
        {
            _credentials = credentials;
            _localPort = localPort;
            _remotePort = remotePort;
            _log = log;
        }

        public static async Task<SshTunnel> StartAsync(VpsSshCredentials credentials, int localPort, int remotePort,
            Action<string> log, CancellationToken cancel = default)
        {
            if (localPort < 1 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (remotePort < 1 || remotePort > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));

            SshTunnel tunnel = new SshTunnel(credentials, localPort, remotePort, log);

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

            log?.Invoke($"SSH tunnel ready: 127.0.0.1:{localPort} → {credentials.Host}:127.0.0.1:{remotePort}");
            _ = tunnel.SuperviseAsync();
            return tunnel;
        }

        private async Task ConnectAsync(CancellationToken cancel)
        {
            CloseConnection();

            SshClient client = new SshClient(_credentials.CreateConnectionInfo()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            client.ErrorOccurred += (s, e) => _log?.Invoke("SSH: " + e.Exception.Message);

            try
            {
                await _credentials.ConnectAsync(client, _log, cancel).ConfigureAwait(false);
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
