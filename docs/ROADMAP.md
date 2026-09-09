# Roadmap

Target EDA: **Altium Designer only**. No KiCad adapter, no multi-EDA backend abstraction (removed from
the plan 2026-09-10; KiCad projects remain reference material for agent/skill/toolset patterns only, see
`RESEARCH.md`). Machine-specific facts live in `ENVIRONMENT.md`.

## Phase 1 — read-only inspection, schematic/PCB analysis, visual representations (in progress)

Done:
- [x] 2026-09-07 Architecture: .NET 8 Altium extension ⇄ loopback HTTP bridge ⇄ stdio MCP server; shared Contracts.
- [x] Build/deploy pipeline (`dotnet build` deploys; `-p:DeployToAltium=false` to skip).
- [x] Extension auto-start via `-RAltiumExtensionMCP:StartBridge`, status panel, `.rcs` menu entries.
- [x] Discovery file + env overrides + stale-file tolerance; structured errors with hints.
- [x] Tools: ping, environment, diagnostics, workspace, projects, open documents, project structure
      (hierarchy + violations), components (list/detail, pins, parameters, implementations), nets (list/detail).
      Verified live on *Bluetooth Sentinel* (AD 26.3.0 and 26.9.1), < 30 ms per call.
- [x] 2026-09-08 **Schematic object model** (`sch.*`, 3 tools) and **PCB board object model** (`pcb.*`, 7 tools).
- [x] 2026-09-08/10 **Migration to the dedicated workstation** (`SOLDERING01`): environment detection
      (`Environment.local.props`, `tools\*.ps1`), extension registration script, full redeploy loop
      (`Redeploy-Extension.ps1`, ~40 s incl. Altium restart), `ENVIRONMENT.md`, DECISIONS D11/D12.
- [x] 2026-09-08/10 **SCH/PCB tools verified live** on AD 26.9.1 and fixed where the API disagreed:
      sheet entries/harness entries as children of their containers; pins filtered to the active display mode;
      multi-part parts are separate sheet objects (`U2A`/`U2B`); physical-designator / compiled-id lookup and
      `compiledIds` on `sch.getComponent` (multi-channel: N ids); `unroutedConnectionCount` from connection
      objects instead of the always-true `ConnectivelyInvalid`; layer typo → `INVALID_PARAMS` with candidates;
      `workspace.openDocument` (editor tab only). 27 unit tests green.
- [x] Skill `altium-project-analysis` covering project → sheet → board drill-down and cross-referencing.

Next (ordered):
1. **Graphical representation** for LLM/VLM analysis: `altium_render_sheet` / `altium_render_board`
   (PNG/SVG, region/zoom-to-object, layer selection). Research `IServerDocumentView` print/export and
   PCB `GraphicalView_*` APIs in `<DisasmRoot>` (`Altium.PCB.DocumentViews`, `Altium.SCH.DocumentViews`);
   fallback: own renderer over `sch.listObjects` / `pcb.listPrimitives` geometry.
2. **Verification data**: `pcb.runDrc` (batch DRC → violations with descriptions and locations) and an ERC
   run tool (`DM_CompileEx`), so `violationCount`/`Violation` primitives are populated on demand.
3. **Layer stack details**: dielectrics (thickness, material, Dk) via `IPCB_LayerStack_V7` / stackup
   document; plane layers.
4. **Analysis helpers** (still primitives): bulk component parameters (BOM view), net → components
   cross-reference, unconnected-pin list, `altium_search`.
5. **Libraries & managed components**: project/installed libraries, symbol/footprint lookup, Workspace
   item/revision links per component (`managed{}` GUIDs are already exposed), lifecycle state.
6. **Toolsets / progressive disclosure**: default `connection+workspace+project`, opt-in `schematic`,
   `pcb`, `library`, `render`, `verify`; `--toolsets` switch and/or a meta-tool.
7. **Change notifications**: `INotification` in the extension → `altium_get_changes_since` for safe caching.
8. Settings dialog (Tools > MCP Bridge > Settings…), panel polish (copy URL, restart, open log).

## Phase 2 — Workspace-aware synchronization, version inspection, collaboration (read-only first)
- Connection/session state (`IEDMS_DXPServerManager`), current Workspace, managed projects and their
  GUIDs, revisions/history, compare/diff entry points, comments/design reviews, project states.
  Research base: `Altium.WorkspaceManager.*`, `Altium.Dxp.Edms.*` in `<DisasmRoot>`; `WORKSPACE_API_NOTES.md`.
- Live analysis keeps working directly on the open project (no Save-to-Server required).

## Phase 3 — safe parameter/property edits
- `Mutations/` in the extension; every write takes `dryRun` and returns a ChangeSet (before/after,
  affected objects). Start with component parameter/comment edits on schematic; undo via Altium's
  undo stack, explicit approval flow in skills.

## Phase 4 — schematic editing
- Place/move components, wires, net labels, ports; sheet creation; re-annotation; ERC run + result parsing.

## Phase 5 — PCB placement & simple edits
- Move/rotate components, layer swap, simple track/via placement; DRC run + parse.

## Phase 6 — routing, rules, polygons
- Rule CRUD, polygon pours, interactive/auto routing calls, layer stack edits.

## Phase 7 — verification layer
- ERC/DRC as first-class tools with structured findings; connectivity validation vs netlist;
  semantic diff between compile snapshots; manufacturing checks (footprint/courtyard/stackup);
  design-review skill with checklists.

## Phase 8 — agent collaboration workflow
- update/sync → modify → verify → diff → commit → human review, through Altium Workspace mechanisms
  (project commits, revisions, comments, review workflows).

## Phase 9 — Altium Vibe-EE harness
- Agents + skills + project memory (`.vibe-ee/` in the project folder), retrieval over compiled
  snapshots, specialised subagents (reviewer, librarian, layout), autonomous engineering workflows.
