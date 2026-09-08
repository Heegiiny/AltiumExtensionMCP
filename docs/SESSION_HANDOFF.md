# Session handoff

Last updated: 2026-09-08 (session 2, Cursor). Next agent: read this file, then `README.md`,
`docs/ARCHITECTURE.md`, `docs/MCP_TOOLS.md`, `docs/ALTIUM_API_NOTES.md`. No chat history is required.

## State in one paragraph

Session 1 delivered a verified vertical prototype (11 read-only tools: connection/workspace/compiled
project model). Session 2 added the **schematic sheet object model** (`sch.*`, 3 tools) and the **PCB
board object model** (`pcb.*`, 7 tools) — contracts, extension queries, MCP tools, docs — all compiling
(0 warnings) with 27/27 tests green, **but none of the new tools has been exercised against a live
Altium yet**: the updated DLL could not be loaded into AD26 during this session (see caveat 1).
The user is working on this machine; **no mouse/keyboard automation** is allowed (opening/closing
windows is acceptable). UI work (settings dialog) is deferred for the same reason.

## What was done last

1. `SchematicModel.cs`, `SchematicQueries.cs`, `SchematicTools.cs`: `sch.getSheet`, `sch.listObjects`
   (type filter, wires with vertices, pins with owner, mils), `sch.getComponent` (parameters, pins,
   implementations, managed GUIDs). Resolution: active SCH doc → `GetSchDocumentByPath` → hidden
   `LoadSchDocumentByPath`.
2. `PcbModel.cs`, `PcbQueries.cs`, `PcbTools.cs`: `pcb.getBoard`, `pcb.listComponents`, `pcb.getComponent`,
   `pcb.listNets`, `pcb.getNet`, `pcb.listRules`, `pcb.listPrimitives`. Coordinates relative to board origin
   in mils. `BoardContext` wraps board resolution, layer-name cache, board/group iterators
   (`BoardIterator_Create` + `AddFilter_ObjectSet/AllLayers/Method` + `FirstPCBObject/NextPCBObject`,
   destroyed in `finally`).
3. `.rcs` now defines PLs and inserts a *MCP Bridge* submenu (Start / Status / Panel) into the home-page
   *File* menu (`MNNoDocument_File` after `_File134` Run Script) and into SCH/PCB *Tools* menus.
   Whether the insertion renders is unverified.
4. `.ins`: `Updates = 'WorkspaceManager'` added as an auto-load experiment — **no effect observed**;
   remove if it turns out harmful.
5. Docs: `MCP_TOOLS.md` (SCH/PCB sections, new error codes), `ROADMAP.md`, this file.

## What works right now

- `dotnet build AltiumMcp.slnx -c Debug -p:DeployToAltium=false` — 0 errors/0 warnings (with deploy on,
  Altium must not have the DLL loaded).
- `dotnet test AltiumMcp.slnx` — 27/27.
- Previously verified (session 1): `-RAltiumExtensionMCP:StartBridge` cold start, all `system.*`,
  `workspace.*`, `project.*` methods on Bluetooth Sentinel.
- `AltiumMcp.Server.exe --health | --diag | --methods | --call <method> [json]`.

## Known problems / caveats

1. **Could not load the new DLL into AD26 this session.** `X2.EXE -RAltiumExtensionMCP:StartBridge`
   exited immediately without creating a new instance while *another* Altium (26.9.1, `ADAgile\X2.EXE`,
   the user's) was running — Altium's single-instance forwarding appears to be global across
   installs, and the target instance (AD26) had no extension loaded. No extension log for the day was
   written, i.e. the module constructor never ran. Investigated and ruled out: `WM_COPYDATA` to
   `TDocumentForm`/`TApplication` spy windows (nothing received), COM `LocalServer32` registrations of
   X2.EXE (none), `.ins Updates=` auto-load (no effect). **Not** ruled out: whether the new DLL loads at
   all (last verified DLL was session 1's). Resolution paths: (a) user runs *File > MCP Bridge > Start*
   or *DXP > Run Process > `AltiumExtensionMCP:StartBridge`* in AD26 once; (b) run `-R` when no other
   Altium instance is running; (c) accept manual start as the documented path.
2. **Auto-load on plain restart does not happen** (from session 1). `-R` at cold start works when it
   creates the instance. `ProcessExit` does not fire in X2.EXE → stale `bridge.json` handled client-side.
3. `AltiumMcp.Server.exe` gets locked by the user's Cursor/Codex MCP host (`codex.exe` spawns it on
   demand). Before `dotnet build` of the server: `Stop-Process -Name AltiumMcp.Server -Force`.
4. In this shell `Get-ChildItem "$env:LOCALAPPDATA\AltiumMcp"` sometimes prints nothing while the explicit
   path `C:\Users\minat.ALTIUMSERVER0\AppData\Local\AltiumMcp` works — use explicit paths in scripts.
5. PCB: `IPCB_DielectricObject` has no .NET getters → no dielectric thickness/material yet.
   `IPCB_LayerObject_V7Helper.LayerID` is missing from the SDK25 assembly → `V6_LayerID()` used instead.
6. Unverified API assumptions worth checking first when testing live: `IPCB_Rule.Priority()`,
   `IPCB_ObjectClass.GetState_MemberName(i)` loop termination (empty string), `GetState_LayerStack_V7`
   `FirstLayer/NextLayer` including dielectrics, `IPCB_Component.GetState_Name()` returning the designator
   `IPCB_Text`, `Sch` pin owner mapping, hidden `LoadSchDocumentByPath` not stealing focus.
7. Session 1 caveats still apply: `DM_InstalledLibraryCount()` = 0, unsaved workspace path is a bare name,
   flattened nets report the top sheet as `documentPath`.

## Environment facts

- Altium: `C:\Program Files\Altium\AD26\X2.EXE` (26.3.0, target), profile GUID
  `{295B10CB-19D8-40A7-B7A6-BA8034539C1F}`, extensions folder
  `C:\ProgramData\Altium\Altium Designer {GUID}\Extensions\AltiumExtensionMCP\`. Second install:
  `C:\Program Files\Altium\ADAgile\X2.EXE` (26.9.1) — the user's; never close it.
- Bridge: `http://127.0.0.1:47120`, discovery `C:\Users\minat.ALTIUMSERVER0\AppData\Local\AltiumMcp\bridge.json`,
  logs in `...\AltiumMcp\logs\bridge-YYYYMMDD.log`.
- Decompiled Altium for API research: `D:\AD_Disasm` (SDK interfaces in
  `Altium Developer\Altium.SDK.Interfaces\{SCH,PCB,DXP,WorkspaceManager}`; `*Helper.cs` files hold the
  typed extension methods wrapping `Internal_*`). System menus: `C:\Program Files\Altium\AD26\System\*.rcs`.
- Test project (safe to modify): `E:\AltiumMcpTestProjects\Bluetooth Sentinel\Bluetooth_Sentinel.PrjPcb`.
  Never use the user's real projects under `C:\Users\minat.ALTIUMSERVER0\Documents\...`.
- .NET SDK 10.0.400; projects target net8.0 (Altium hosts .NET 8.0.15).

## Next concrete step

1. Get the extension loaded once in AD26 (caveat 1), then run the smoke test below for `sch.*` and
   `pcb.*`; fix whatever the live API disagrees with; flip the "not yet verified live" markers in
   `MCP_TOOLS.md`/`ROADMAP.md`.
2. Update `skills/altium-project-analysis/SKILL.md` with the SCH/PCB workflow (cross-reference by
   `sourceUniqueId`, primitives paging guidance).
3. Then ROADMAP items 2–3 (settings dialog when UI work is allowed again; libraries/managed content).

## How to smoke-test in 60 seconds

```powershell
cd E:\AltiumExtensionMCP
$s='src\AltiumMcp.Server\bin\Debug\net8.0\AltiumMcp.Server.exe'
& $s --health
$prj='E:\AltiumMcpTestProjects\Bluetooth Sentinel'
& $s --call sch.getSheet "{""documentPath"":""$prj\Bluetooth_Sentinel.SchDoc""}"
& $s --call sch.listObjects "{""documentPath"":""$prj\Bluetooth_Sentinel.SchDoc"",""types"":[""Component"",""Port""],""limit"":20}"
& $s --call pcb.getBoard "{""documentPath"":""$prj\Bluetooth_Sentinel.PcbDoc""}"
& $s --call pcb.listComponents "{""documentPath"":""$prj\Bluetooth_Sentinel.PcbDoc"",""filter"":""U*""}"
& $s --call pcb.getNet "{""documentPath"":""$prj\Bluetooth_Sentinel.PcbDoc"",""net"":""GND""}"
```
(Adjust the .SchDoc/.PcbDoc names from `project.getStructure` if they differ.)
