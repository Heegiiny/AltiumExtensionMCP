# Session handoff

Last updated: 2026-09-10 (session 4, Cursor, on the dedicated workstation). Next agent: read this file,
then `ENVIRONMENT.md`, `README.md`, `ARCHITECTURE.md`, `MCP_TOOLS.md`, `ALTIUM_API_NOTES.md`,
`ROADMAP.md`. No chat history is required; the repository is the project memory.

## State in one paragraph

The project runs on the dedicated agent machine `SOLDERING01` (Altium Designer **26.9.1.10**, single
install; all facts in `ENVIRONMENT.md`). Build, 27/27 unit tests, deploy, extension registration, cold
start with the bridge and the full redeploy loop (`tools\Redeploy-Extension.ps1`, ~40 s) all work. All
**22 bridge methods / MCP tools are verified live** on the *Bluetooth Sentinel* test copy, including the
schematic (`sch.*`) and PCB (`pcb.*`) families that were unverified after session 2. Live API
discrepancies found in sessions 3–4 are fixed and recorded in `ALTIUM_API_NOTES.md` ("Live findings").
Environment-specific paths are centralised (DECISIONS D11); the machine is dedicated, so restarting
Altium and UI automation are allowed (D12). Phase 1 continues with graphical representation and DRC.

## Sessions so far

1. 2026-09-07 (`ALTIUMSERVER0`, AD 26.3): architecture, bridge, 11 project/workspace tools, skill.
2. 2026-09-08 (same machine): `sch.*` + `pcb.*` implemented, not loadable into AD26 there.
3. 2026-09-08 (first session on `SOLDERING01`, interrupted; committed as `a90feeb "WIP"`): environment
   detection scripts, `Register-Extension.ps1`, `Redeploy-Extension.ps1`, `Invoke-Bridge.ps1`, first
   live pass over `sch.*`/`pcb.*` with fixes (sheet/harness entries as children, pins per display mode,
   `unroutedConnectionCount`, layer-typo error), `workspace.openDocument`. Docs were not updated.
4. 2026-09-10 (this session): migration audit — `ENVIRONMENT.md` created, all docs moved to symbolic
   paths, KiCad/multi-EDA removed from the plan, D11/D12 added, `WORKSPACE_API_NOTES.md` started; second
   live pass; fixes: `sch.getComponent` accepts multi-part (`U2A`/`U2B`), physical designator and
   compiled id, returns `physicalDesignator` + `compiledIds` (multi-channel aware), `OBJECT_NOT_FOUND`
   lists `designatorsOnSheet`; `ConnectivelyInvalid` dropped from `pcb.listNets` (always true live);
   tool descriptions, `MCP_TOOLS.md`, `ROADMAP.md`, skill updated.

## What works right now

- `dotnet build AltiumMcp.slnx -c Debug` (deploys; Altium must be closed) / `-p:DeployToAltium=false`.
- `dotnet test AltiumMcp.slnx` — 27/27.
- `powershell -File tools\Redeploy-Extension.ps1` — close Altium → build+deploy → register if needed →
  start with `-RAltiumExtensionMCP:StartBridge` → open the test project. Bridge is up ~15 s after launch.
- `. .\tools\Invoke-Bridge.ps1; Invoke-Bridge <method> @{...}` — call any bridge method without JSON quoting pain.
- All methods listed in `MCP_TOOLS.md`; timings: project/workspace < 30 ms, `sch.*` 40–500 ms, `pcb.*`
  ~1–1.5 s on a hidden-loaded board (6 s first call), ~0.5 s when the board is open in the editor.
- Altium is normally left running with the bridge; if `--diag` shows `descriptorProcessAlive=false`,
  run `tools\Start-AltiumWithBridge.ps1 -Restart`.

## Known problems / caveats

1. Auto-load on a plain Altium restart still does not happen; `-R` at cold start (scripts) is the path.
   `ProcessExit` does not fire in X2.EXE → stale `bridge.json` is handled client-side (pid check).
2. `violationCount` / `Violation` primitives are 0 until a DRC is run in Altium — no DRC tool yet.
3. Layer stack: no dielectric layers (no .NET getters on `IPCB_DielectricObject`); `V6_LayerID()` used.
4. `sch.listObjects` returns items in iteration order (containers first); a mixed-type page can be one type.
5. `DM_InstalledLibraryCount()` = 0; unsaved workspace path is a bare name; flattened nets report the top
   sheet as `documentPath` (from session 1, unchanged).
6. `AltiumMcp.Server.exe` may be locked by an MCP host (Cursor/Codex spawn it); `Redeploy-Extension.ps1`
   kills it before building.
7. Windows PowerShell 5.1: `Invoke-Bridge.ps1` sets `Set-StrictMode -Version Latest`; in ad-hoc scripts that
   read optional JSON fields call `Set-StrictMode -Off` after dot-sourcing, or you get
   `PropertyNotFoundStrict` noise (the bridge results are fine).
8. The new decompiled tree (`<DisasmRoot>`) is a 26.9.1 dump from the old machine's `ADAgile` install,
   not byte-identical to the installed build; no `docs\` folder travelled with it.

## Next concrete step

1. **Graphical representation** (ROADMAP phase 1, item 1): research in `<DisasmRoot>` how the SCH/PCB
   document views export images (`Altium.SCH.DocumentViews`, `Altium.PCB.DocumentViews`,
   `IServerDocumentView`, print/export commands such as `Sch:PrintPreview`/`Pcb:ExportToImage` in
   `<AltiumProgramsHome>\System\*.rcs`), then implement `sch.render` / `pcb.render` → PNG file path +
   bounds, and an `altium_render_*` tool returning the image. Fallback: own SVG/PNG painter over
   `sch.listObjects` / `pcb.listPrimitives` geometry (all needed data is already exposed).
2. `pcb.runDrc` (batch DRC) so `Violation` primitives and `violationCount` become meaningful; ERC run.
3. Then ROADMAP items 3–5 (dielectrics, analysis helpers, libraries/managed components) and the phase 2
   read-only Workspace discovery (`WORKSPACE_API_NOTES.md` questions 1–5).

## How to smoke-test in 60 seconds

```powershell
cd <ProjectRoot>
powershell -File tools\Start-AltiumWithBridge.ps1     # no-op if already up; -Restart if the bridge is dead
. .\tools\Invoke-Bridge.ps1; Set-StrictMode -Off
$prj = Get-TestProjectDir                                   # <TestProjectsRoot>\Bluetooth Sentinel
Invoke-Bridge system.ping
Invoke-Bridge project.getStructure @{ compileIfNeeded = $true } | Out-Null
Invoke-Bridge sch.getSheet      @{ documentPath = "$prj\Bluetooth_Sentinel.SchDoc" }
Invoke-Bridge sch.listObjects   @{ documentPath = "$prj\Bluetooth_Sentinel.SchDoc"; types = @('SheetSymbol','SheetEntry'); limit = 20 }
Invoke-Bridge sch.getComponent  @{ documentPath = "$prj\Microcontroller_STM32F101.SchDoc"; component = 'U4' }   # physical → U2 on sheet
Invoke-Bridge pcb.getBoard      @{ documentPath = "$prj\Bluetooth_Sentinel.PcbDoc" }
Invoke-Bridge pcb.listComponents @{ documentPath = "$prj\Bluetooth_Sentinel.PcbDoc"; filter = 'U*' }
Invoke-Bridge pcb.getNet        @{ documentPath = "$prj\Bluetooth_Sentinel.PcbDoc"; net = 'GND' }
```
Expected: 4 ICs (U1–U4), board 3149.6 × 2165.4 mils, 4 copper layers, 57 nets, 38 rules, GND 54 pads /
131 vias, `unroutedConnectionCount` absent everywhere (fully routed).
