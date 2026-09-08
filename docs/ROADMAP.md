# Roadmap

## Phase 1 — read-only inspection & analysis (in progress)

Done (2026-09-07):
- [x] Architecture: .NET 8 Altium extension ⇄ loopback HTTP bridge ⇄ stdio MCP server; shared Contracts.
- [x] Build/deploy pipeline (`dotnet build` deploys; `-p:DeployToAltium=false` to skip).
- [x] Extension auto-start via `-RAltiumExtensionMCP:StartBridge` and docked status panel.
- [x] Discovery file + env overrides + stale-file tolerance; structured errors with hints.
- [x] Tools: ping, environment, diagnostics, workspace, projects, open documents, project structure
      (hierarchy + violations), components (list/detail, pins, parameters, implementations), nets (list/detail).
- [x] Verified live on *Bluetooth Sentinel* (AD 26.3.0): all 11 tools, < 30 ms per call.
- [x] 27 unit tests (protocol, discovery, filter, error mapping). Skill `altium-project-analysis`.

Implemented 2026-09-08, **pending live verification** (needs the extension loaded in AD26 once):
- [~] **Schematic object model** (`SCH` server): `altium_get_sheet`, `altium_list_sheet_objects`
      (components, pins, wires with vertices, net labels, ports, power objects, sheet symbols/entries,
      buses, junctions, text; positions in mils), `altium_get_sheet_component` (parameters, pins, models).
- [~] **PCB read**: `altium_get_board` (outline, layer stack, counts, classes, violation count),
      `altium_list_pcb_components` / `altium_get_pcb_component` (placement, pads), `altium_list_pcb_nets` /
      `altium_get_pcb_net` (routing stats, pads, layers), `altium_list_pcb_rules`, `altium_list_pcb_primitives`
      (tracks/vias/polygons/text/violations with geometry, layer/net filters).
- [~] Menu entries via `.rcs`: *File > MCP Bridge* on the home page, *Tools > MCP Bridge* in SCH/PCB editors.

Next (ordered):
1. **Verify SCH/PCB tools live** on Bluetooth Sentinel; fix API surprises; add dielectric details, DRC run.
2. **Settings dialog** (Tools > MCP Bridge > Settings…) instead of panel-hosted settings — deferred while
   the user works on the machine (no UI automation allowed).
3. **Libraries & managed components**: project/installed libraries, symbol/footprint lookup, Workspace
   item/revision links per component, lifecycle state.
4. **Bulk & analysis helpers** (still primitive): parameters for N components in one call, cross-reference
   (net → components), unconnected-pin list, BOM-ish grouping by `libraryReference`.
5. **Toolsets / progressive disclosure**: default set (connection/workspace/project), opt-in `schematic`,
   `pcb`, `library`; `--toolsets` switch and/or a meta-tool.
6. **Change notifications**: subscribe to `INotification` in the extension; expose `altium_get_changes_since`
   so agents can cache safely.
7. Panel polish (copy URL, restart, open log), installer script for the extension folder.

## Phase 2 — safe parameter/property edits
- `Mutations/` in the extension; every write takes `dryRun` and returns a ChangeSet (before/after,
  affected objects). Start with component parameter/comment edits on schematic; undo via Altium's
  undo stack (`ISch_Document` transactions), explicit approval flow in skills.

## Phase 3 — schematic editing
- Place/move components, wires, net labels, ports; sheet creation; re-annotation; ERC run + result parsing.

## Phase 4 — PCB placement & simple edits
- Move/rotate components, layer swap, simple track/via placement; DRC run + parse.

## Phase 5 — routing, rules, polygons
- Rule CRUD, polygon pours, interactive/auto routing calls, layer stack edits.

## Phase 6 — verification layer
- ERC/DRC as first-class tools with structured findings; connectivity validation vs netlist;
  semantic diff between compile snapshots; manufacturing checks (footprint/courtyard/stackup);
  design-review skill with checklists.

## Phase 7 — Vibe-EE harness
- `IEdaBackend` abstraction over the bridge client; KiCad adapter (reuse Konnect/KiSkill patterns);
  agents + skills + project memory (`.vibe-ee/` in the project folder), retrieval over compiled
  snapshots, subagents (reviewer, librarian, layout).
