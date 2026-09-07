# Altium Vibe-EE MCP

An MCP (Model Context Protocol) server that gives LLM agents (Claude, Cursor, GPT, ...) structured,
read-only access to a **running Altium Designer**: workspace, open projects, sheet hierarchy,
compiled components, nets, pins, parameters and compile violations.

Phase 1 (this repo today) is read/analysis. The architecture is built so that later phases can add
schematic/PCB edits, verification loops and a multi-EDA "Vibe-EE" harness on top.
See `docs/ROADMAP.md`.

```
MCP client (Claude Desktop / Cursor / Claude Code)
   │ stdio (JSON-RPC, MCP)
   ▼
AltiumMcp.Server.exe          ← out-of-process .NET 8 console, 11 tools
   │ HTTP JSON-RPC on 127.0.0.1:47120   (discovery: %LOCALAPPDATA%\AltiumMcp\bridge.json)
   ▼
AltiumExtensionMCP.dll        ← .NET 8 Altium extension loaded inside X2.EXE
   │ Altium SDK (COM), marshalled to the UI thread
   ▼
Altium Designer 26 (compiled project model, workspace manager)
```

## Requirements

- Windows, Altium Designer 26.x (tested: 26.3.0) installed at `C:\Program Files\Altium\AD26`.
  Other paths/versions: override `AltiumExe`, `AltiumProfileGuid`, `AltiumSdkHome` in
  `Directory.Build.props` or via `-p:` on the command line.
- Altium Developer SDK25 (ships with AD; provides `Altium.SDK.dll` / `Altium.SDK.Interfaces.dll`).
  Expected under `C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\Altium Developer\SDK25\CSharp`.
- .NET SDK 8 or newer (built with 10.0.x; targets net8.0 because AD26 hosts .NET 8).

## Build

```powershell
git clone <this repo> E:\AltiumExtensionMCP
cd E:\AltiumExtensionMCP
dotnet build AltiumMcp.slnx -c Debug            # builds + deploys the extension to Altium
dotnet test  AltiumMcp.slnx                      # 27 unit tests (no Altium needed)
```

`dotnet build` of `AltiumMcp.Extension` copies the extension into
`C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\AltiumExtensionMCP\`.
**Altium must be closed** during that copy (it locks the DLL once loaded). To build without deploying:
`dotnet build -p:DeployToAltium=false`.

## Install / load the extension in Altium

Altium loads extension DLLs lazily. Two reliable ways to load ours:

1. **Command-line switch at startup (recommended, scriptable):**
   ```powershell
   & "C:\Program Files\Altium\AD26\X2.EXE" -RAltiumExtensionMCP:StartBridge
   ```
   The bridge is up ~5 s after launch, before the main window finishes loading.
   `tools\Start-AltiumWithBridge.ps1` wraps this and waits for the bridge.
2. **Already-running Altium:** *DXP > Run Process* → `AltiumExtensionMCP:StartBridge` (or
   `AltiumExtensionMCP:ShowBridgePanel` to also open the *MCP Bridge* status panel). Loading the module
   starts the bridge. Note: a plain restart of Altium does **not** reload the extension by itself
   (panel restore did not trigger it in testing) — use the `-R` switch or the helper script.

Verify:

```powershell
src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe --health
src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe --call workspace.getInfo
```

## Connect an MCP client

Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`) / Cursor (`.cursor\mcp.json`) /
Claude Code (`claude mcp add ...`):

```json
{
  "mcpServers": {
    "altium": {
      "command": "E:\\AltiumExtensionMCP\\src\\AltiumMcp.Server\\bin\\Debug\\net8.0\\AltiumMcp.Server.exe"
    }
  }
}
```

Optional environment: `ALTIUM_MCP_BRIDGE_URL` (full base URL) or `ALTIUM_MCP_BRIDGE_PORT` to
override discovery (needed only when running several Altium instances with bridges).

Then give the agent the skill `skills/altium-project-analysis/SKILL.md` (copy it into your
client's skills folder) and ask, e.g. *"Which ICs are on the open project and what nets does U1's
power pins connect to?"*

## Minimal working example

With Altium running and a `.PrjPcb` open:

```powershell
$s = 'src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe'
& $s --call system.ping
& $s --call workspace.listProjects
& $s --call project.getStructure
& $s --call project.listComponents '{"filter":"U*","limit":10}'
& $s --call project.getComponent   '{"component":"U1"}'
& $s --call project.listNets       '{"filter":"VDD*"}'
& $s --call project.getNet         '{"net":"GND"}'
```

Tested against the Altium example *Bluetooth Sentinel* (72 components, 65 nets, 15 sheets):
all calls return in < 30 ms after the first compile.

## Tools (MCP names)

`altium_ping`, `altium_get_environment`, `altium_bridge_diagnostics`,
`altium_get_workspace`, `altium_list_projects`, `altium_list_open_documents`,
`altium_get_project_structure`, `altium_list_components`, `altium_get_component`,
`altium_list_nets`, `altium_get_net`. Details: `docs/MCP_TOOLS.md`.

## Repository layout

| Path | Purpose |
|------|---------|
| `src/AltiumMcp.Contracts` | Wire protocol + DTOs shared by extension and server (net8.0, no Altium deps) |
| `src/AltiumMcp.Extension` | Altium extension: plugin entry, UI-thread dispatcher, HTTP bridge, queries, status panel |
| `src/AltiumMcp.Server` | MCP stdio server (ModelContextProtocol 2.2.0) + `--call` debug CLI |
| `tests/AltiumMcp.Tests` | xunit tests for protocol, discovery, filter, error mapping |
| `skills/` | Agent skills (procedural knowledge) |
| `docs/` | Architecture, research, API notes, decisions, roadmap, session handoff |
| `tools/` | Helper scripts |

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `BRIDGE_UNAVAILABLE`, `altiumProcesses: []` | Altium not running. Start it with `-RAltiumExtensionMCP:StartBridge`. |
| `BRIDGE_UNAVAILABLE`, Altium running, no `bridge.json` | Extension not loaded. Use the `-R` switch or open the panel. Check `%LOCALAPPDATA%\AltiumMcp\logs\bridge-YYYYMMDD.log`. |
| `NOT_COMPILED` | Project has no compiled model: *Project > Validate PCB Project* in Altium, or pass `compileIfNeeded=true`. |
| Build error copying DLL | Altium is running and holds the DLL. Close it or build with `-p:DeployToAltium=false`. |
| Port 47120 busy | Bridge picks the next free port and writes it into `bridge.json`; the server follows the file. |

Logs: extension → `%LOCALAPPDATA%\AltiumMcp\logs\`; MCP server → stderr (stdout is the MCP transport).
