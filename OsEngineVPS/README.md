# OsEngineVPS

Remote control of OsEngine terminals running on a VPS: an SSH tunnel to the server and the OsEngine MCP API of each
terminal. A separate program, built on the OsEngine of this fork (`../project/OsEngine`) as a library.

## Layout

| Path | What |
|---|---|
| `Program.cs` | entry point: runs `OsEngine.App` (themes, window styles) with `MainUi` as the start window |
| `MainUi.xaml(.cs)` | main window: **Settings** button + one tab per VPS terminal, each with the Robots.VPS workspace |
| `RobotsVps/` | the VPS client: connection and administration window (`RobotsVpsUi`), workspace (`RobotsVpsLiteClone`), chart, journals, files, MCP access… — see `RobotsVps/ARCHITECTURE.md` |
| `RobotsVps/Provisioning/` | `osengine-setup.sh`, `osengine-update.sh` — copied to `bin\Debug\VpsServer\` |

OsEngine itself (`project/OsEngine`) keeps the server side used by the VPS terminals (MCP tools) and is otherwise the
official program; `OsEngineVps.InternalsVisibleTo.cs` there lets this program use OsEngine's internal window parts.

## Build and run

```bash
dotnet build OsEngineVPS/OsEngineVPS.csproj
```

Output: `OsEngineVPS\bin\Debug\OsEngineVPS.exe`. Its own `Engine\` folder holds the settings (VPS address, the SSH key
of this computer — DPAPI-encrypted, MCP access folders), `VpsServer\` the server build package
(`D:\ff-research\headless\build-package.sh` puts it there), `VpsData\` / `VpsBackups\` data taken from the VPS.

The SSH tunnel to the VPS lives inside this program: AI agents reach the VPS terminals (`.mcp.json`, MCP access) only
while it runs.

## History

Until 2026-10-02 the client lived inside OsEngine (`project/OsEngine/OsTrader/Gui/RobotsVps`, "VPS" and
"Bot Station VPS" buttons of the main menu) — git tag `robots-vps-before-split`, branch `feature/robots-vps`.
