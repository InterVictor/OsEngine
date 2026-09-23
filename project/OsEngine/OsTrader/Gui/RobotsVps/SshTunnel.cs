// SSH tunnel lifecycle helper for Robots.VPS.
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    internal sealed class SshTunnel : IDisposable
    {
        private readonly Process _process;
        private readonly bool _ownsProcess;
        private string _lastError = "";

        public bool StartedByThisWindow => _ownsProcess;

        private SshTunnel(Process process, bool ownsProcess)
        {
            _process = process;
            _ownsProcess = ownsProcess;
        }

        public static async Task<SshTunnel> StartAsync(
            string host, string user, string keyPath, int localPort, int remotePort,
            Action<string> log, CancellationToken cancel = default)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("SSH host is required");
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("SSH user is required");
            if (string.IsNullOrWhiteSpace(keyPath)) throw new ArgumentException("SSH private key path is required");

            keyPath = Environment.ExpandEnvironmentVariables(keyPath.Trim().Trim('"'));
            if (!Path.IsPathRooted(keyPath)) keyPath = Path.GetFullPath(keyPath);
            if (!File.Exists(keyPath)) throw new FileNotFoundException("SSH private key file was not found", keyPath);
            if (localPort < 1 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (remotePort < 1 || remotePort > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));

            // A manually started tunnel may already be listening. Reuse it, but never terminate a
            // process this window did not create.
            if (await IsListeningAsync(localPort, cancel).ConfigureAwait(false))
            {
                log?.Invoke($"Using existing local listener on 127.0.0.1:{localPort}");
                return new SshTunnel(null, false);
            }

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false
            };

            string[] args =
            {
                "-i", keyPath,
                "-o", "BatchMode=yes",
                "-o", "ExitOnForwardFailure=yes",
                "-o", "ServerAliveInterval=30",
                "-o", "ServerAliveCountMax=3",
                "-N",
                "-L", $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}",
                $"{user}@{host}"
            };
            foreach (string arg in args) start.ArgumentList.Add(arg);

            Process process = new Process { StartInfo = start, EnableRaisingEvents = true };
            SshTunnel tunnel = new SshTunnel(process, true);
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    tunnel._lastError = e.Data;
                    log?.Invoke("SSH: " + e.Data);
                }
            };

            try
            {
                if (!process.Start()) throw new InvalidOperationException("Could not start ssh.exe");
                process.BeginErrorReadLine();

                // OpenSSH can take a moment to authenticate and establish the forward.
                for (int i = 0; i < 40; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        process.WaitForExit(500);
                        int exitCode = process.ExitCode;
                        string detail = tunnel._lastError;
                        tunnel.Dispose();
                        throw new InvalidOperationException($"SSH exited with code {exitCode}. {detail}".Trim());
                    }
                    if (await IsListeningAsync(localPort, cancel).ConfigureAwait(false))
                    {
                        log?.Invoke($"SSH tunnel ready: 127.0.0.1:{localPort} → {host}:127.0.0.1:{remotePort}");
                        return tunnel;
                    }
                    await Task.Delay(250, cancel).ConfigureAwait(false);
                }

                string error = tunnel._lastError;
                tunnel.Dispose();
                throw new TimeoutException("SSH tunnel did not open the local port within 10 seconds. " + error);
            }
            catch
            {
                tunnel.Dispose();
                throw;
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

        public void Dispose()
        {
            if (!_ownsProcess || _process == null) return;
            try
            {
                if (!_process.HasExited) _process.Kill(true);
                if (!_process.HasExited) _process.WaitForExit(2000);
            }
            catch { /* process may have exited between checks */ }
            _process.Dispose();
        }
    }
}
