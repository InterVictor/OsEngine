# Robots.VPS SSH tunnel

## 2026-09-23 — tunnel lifecycle moved into the application

The `feature/robots-vps` branch in `D:\OsEngine-fork` now creates its own SSH local-forward when the user clicks **Connect** in the Robots.VPS window. The tunnel connects the workstation's `127.0.0.1:<local port>` to `127.0.0.1:<VPS API port>` on the SSH host; the MCP endpoint is then set to `http://127.0.0.1:<local port>/api/v2/mcp`.

The window fields are SSH host, SSH user, OpenSSH private-key file path, local port (default 6510), and VPS API port (default 6500). Current workstation defaults are host `<VPS-IP>`, user `root`, and key path `%USERPROFILE%\.ssh\ff_server`. Settings are persisted in `Engine\RobotsVpsSettings.txt` along with the existing MCP URL/API key. The private-key contents are never read by OsEngine or copied to that settings file; OpenSSH reads the key file directly. The API key remains stored as it was before this change.

`ssh.exe` runs hidden with `BatchMode=yes`, `ExitOnForwardFailure=yes`, and keepalive options. The UI waits for the local forward and then initializes the remote MCP session. On MCP connection failure, explicit disconnect, or closing the Robots.VPS window, the UI terminates only the SSH process it started. If a process is already listening on the configured local port, Robots.VPS reuses that listener and does not terminate it.

To use another workstation, configure its own OpenSSH private key and add the corresponding public key to the VPS account's `authorized_keys`; do not copy the first workstation's private key. Each computer may use the same local port because it is local to that computer. Each computer needs its own saved SSH/API settings. The current implementation expects a passphrase-free OpenSSH key and an available `ssh.exe`; it does not prompt for SSH passwords or key passphrases.

Verification: `dotnet build D:\OsEngine-fork\project\OsEngine\OsEngine.csproj -c Debug -p:RestoreIgnoreFailedSources=true` succeeded (0 errors; existing unrelated warnings only). No tunnel was launched by the build.
