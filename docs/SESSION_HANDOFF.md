# Session handoff

Last updated: 2026-09-07 (session 1, Cursor). Next agent: read this file, then `README.md`,
`docs/ARCHITECTURE.md`, `docs/MCP_TOOLS.md`, `docs/ALTIUM_API_NOTES.md`. No chat history is required.

## State in one paragraph

A working vertical prototype exists and was verified live: `AltiumMcp.Server.exe` (MCP stdio, 11 read-only
tools) → HTTP bridge → `AltiumExtensionMCP.dll` loaded inside Altium Designer 26.3.0 → Altium SDK. Against
the *Bluetooth Sentinel* example (copied to `E:\AltiumMcpTestProjects\Bluetooth Sentinel\`) every tool
returns real data (72 components, 65 nets, 15-sheet hierarchy, 64 compile violations) in < 30 ms.
Build is reproducible (`dotnet build AltiumMcp.slnx`), 27 unit tests pass, the extension deploys via
MSBuild, and Altium auto-loads it when started with `-RAltiumExtensionMCP:StartBridge`.

## What was done last

1. Solution assembled (`AltiumMcp.slnx` — the .NET 10 SDK emits `.slnx`; works with `dotnet build/test`).
2. Deployed, launched Altium cold with `-R`, confirmed bridge up in ~5 s, opened the test project via
   `AltiumSession.ps1 open`, exercised all bridge methods through `--call` and through a real MCP
   stdio handshake (initialize → tools/list → tools/call).
3. Fixed from live testing: wildcard filters (`U*`) now work (`FilterMatcher` in Contracts, tested);
   physical designator is primary (`designator`), `logicalDesignator` secondary; implementation list
   deduplicated (multi-part components); `technologySets` blob replaced by a count; compact JSON
   (defaults omitted); violation sample reduced to 10 and biased to errors, `byErrorKind` added;
   `OBJECT_NOT_FOUND` for missing component/net; best-effort `ProcessExit` hook (redeployed; found not
   to fire in Altium — see caveats).
4. Wrote skill `skills/altium-project-analysis/SKILL.md`, all docs, `tools/Start-AltiumWithBridge.ps1`.

## What works right now

- `dotnet build AltiumMcp.slnx -c Debug` (deploys extension; Altium must be closed) — 0 errors.
- `dotnet test AltiumMcp.slnx` — 27/27.
- `X2.EXE -RAltiumExtensionMCP:StartBridge` → `%LOCALAPPDATA%\AltiumMcp\bridge.json` appears, `/health` ok.
- `AltiumMcp.Server.exe --health | --diag | --methods | --call <method> [json]`.
- MCP stdio: `initialize`, `tools/list` (11 tools), `tools/call` incl. `isError` results with hints.
- Extension log: `%LOCALAPPDATA%\AltiumMcp\logs\bridge-YYYYMMDD.log`.

## Known problems / caveats

- **Auto-load on plain restart does not happen.** `-RAltiumExtensionMCP:ShowBridgePanel` at cold start
  works (panel shown, bridge up, screenshot-verified), but after closing and starting Altium *without*
  `-R` the extension was not loaded (no panel, no bridge). Hypotheses: floating (not docked) panels are
  not persisted; `.ins` PanelInfo not registered in Altium's extension registry until a rescan; layout
  not saved on WM_CLOSE. Investigate `D:\AD_Disasm\Code\System\Altium.PinsPanel` and how AD persists
  panel layout (`%AppData%\Altium\...\*.xml`). Workaround: always start via `tools\Start-AltiumWithBridge.ps1`.
- **`ProcessExit`/`ApplicationExit` do not fire** inside X2.EXE, so `bridge.json` stays after Altium exits.
  Client handles it (pid check → `BRIDGE_UNAVAILABLE` with a stale-file hint). Proper fix: find a shutdown
  `INotification` in `ReceiveNotificationImpl`.
- `-R` does not load the module into an *already running* Altium. Fallback there: *DXP > Run Process*
  `AltiumExtensionMCP:StartBridge` (standard Altium mechanism; not yet exercised by hand this session).
- `DM_InstalledLibraryCount()` returned 0 although managed content is used; library discovery needs research.
- `workspace.getInfo` reports `workspaceFullPath` as just `Project Group 1.DsnWrk` when the group is unsaved.
- Net `documentPath` is the top sheet for all flattened nets (Altium semantics), not the sheet where the
  net label lives.
- The debug CLI prints Russian OS error text for socket failures (locale); harmless.
- Two Altium installs exist on this machine (AD26 26.3.0 = target; another 26.9.1 instance may run other
  projects). Discovery is per-user, so only one bridge is tracked; use `ALTIUM_MCP_BRIDGE_PORT` if needed.
- The deployed DLL matches the committed source (built 16:06, verified live at 16:10).

## Environment facts

- Altium: `C:\Program Files\Altium\AD26\X2.EXE`, profile GUID `{295B10CB-19D8-40A7-B7A6-BA8034539C1F}`,
  extensions folder `C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\`, SDK25 under
  `...\Extensions\Altium Developer\SDK25\CSharp`.
- Helper for Altium lifecycle: `C:\Users\minat.ALTIUMSERVER0\AltiumExtensions\tools\altium-session\AltiumSession.ps1`
  (`status|start|focus|open -Document|close`). Workflow skill:
  `C:\Users\minat.ALTIUMSERVER0\AltiumExtensions\.cursor\skills\extension-lifecycle\SKILL.md`.
- Decompiled Altium code for API research: `D:\AD_Disasm` (SDK in `Altium Developer\Altium.SDK*`,
  system extensions in `Code\System\*`). See `docs/ALTIUM_API_NOTES.md` for what is already known.
- Test project (safe to modify): `E:\AltiumMcpTestProjects\Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb`
  (copy of `D:\AD\Docs\Examples\Bluetooth Sentinel`). Never use the user's real projects under
  `C:\Users\minat.ALTIUMSERVER0\Documents\...` for experiments.
- .NET SDK 10.0.400; projects target net8.0 (Altium hosts .NET 8.0.15).

## Next concrete step

1. Resolve auto-load on plain restart (caveat 1) or accept the `-R` launcher as the official path and
   document it as such in README (already done provisionally).
2. Start ROADMAP item 1 (schematic object model). The API research is already done — see
   `ALTIUM_API_NOTES.md` § "Researched, not yet implemented" (iterator pattern, `TObjectId`s, units).
   Add `SchematicQueries` + DTOs + `SchematicTools` (`altium_get_sheet`, `altium_list_sheet_objects` with type
   filter and positions in mils). Use `SchServer.GetSchDocumentByPath` for open sheets and
   `LoadSchDocumentByPath` (hidden) otherwise.
3. Then PCB read (ROADMAP item 2).

## How to smoke-test in 60 seconds

```powershell
cd E:\AltiumExtensionMCP
.\tools\Start-AltiumWithBridge.ps1 -Project 'E:\AltiumMcpTestProjects\Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb'
$s='src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe'
& $s --call workspace.listProjects
& $s --call project.listComponents '{"filter":"U*","limit":5}'
& $s --call project.getNet '{"net":"GND"}'
```
