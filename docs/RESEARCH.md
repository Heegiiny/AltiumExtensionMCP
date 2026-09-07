# Research

Sources surveyed for session 1 (2026-09-07). Two parts: (A) existing EDA agent tooling (what they do,
what to borrow, what to avoid) and (B) where Altium API knowledge came from. Detailed API signatures
are in `ALTIUM_API_NOTES.md`; decisions derived from this research are in `DECISIONS.md`.

## A. Existing MCP / agent projects

### Altium

**coffeenmusic/altium-mcp** (Python, FastMCP, MIT, ~150★)
- Bridge: server writes `request.json`, launches `X2.EXE -RScriptingSystem:RunScript` on a DelphiScript
  project, script writes `response.json`; file polling.
- Tools: designators/properties/pins, nets, layers, stackup, rules, `get_net_connections`,
  symbol/footprint primitive dumps; writes: batch `place_components` (one undo step), `move_components`,
  net classes, OutJob runner, symbol/footprint creation; dev: `run_altium_script` sandbox, `get_screenshot(zoom_to)`.
- Good ideas: verification tools separate from mutations (`check_placement`, `check_orientation`) with the note
  "a screenshot is not verification"; batch spec-file tools; dump→recreate→diff round-trip tests; a companion
  DelphiScript API skill.
- Pain points (issues): a script runtime error leaves the debugger paused and later calls silently no-op;
  focused-document dependence; locale decimal-comma JSON bug; no headless mode.

**flaco-source/altium-mcp** (TypeScript, official SDK, MIT)
- Same RunScript/JSON-file bridge plus a `.bridge.lock` to serialize calls; JSON schemas for the bridge contract.
- Tools: `get_server_status`, `altium_ping`, `get_workspace_projects`, `get_schematic_data(include_queries[])`
  (bucketed reads), `edit_schematic` (move/rotate/params/place/wires/labels), PCB reads.
- Good ideas: MCP `instructions` + bundled `SKILL.md`; per-tool "which document must be focused" table;
  `ToolAnnotations` read-only hints; "call the read tool again after an edit to verify".

**embedded-society/altium-designer-mcp** (Rust, GPL-3)
- No Altium process: reads/writes `.PcbLib`/`.SchLib` OLE compound files directly; 34 tools (list/get/search,
  ASCII render, diff, in-place updates, merge, validate/repair, backups).
- Good ideas: `dry_run` on every destructive op; automatic timestamped backups; `allowed_paths` allow-list;
  audit log; rate-limit only mutating ops; `AGENT_GUIDE.md`; golden fixtures + external parser as CI oracle.
- Limits: libraries only; undocumented binary format risk.

**10xgenomicsEng/altium-mcp-unified** (Python + Rust)
- Skeleton only: planned router between "live" (DelphiScript bridge) and "file" (Rust parser) backends with
  auto-detection of a running Altium. Idea worth keeping; no code to reuse.

### KiCad (more mature agent tooling)

**mixelpixx/KiCAD-MCP-Server** (TS + Python, 2.1k★)
- 233 tools in 16 categories; keyword discovery (`search_tools`, `get_category_tools`, 32 "essential" first);
  MCP resources (`kicad://project/current/...`); `snapshot_project` checkpoints; `validate_*` verified via `kicad-cli`.
- Lessons they learned the hard way: bridge without request IDs → stale/off-by-one responses; a single
  `execute_tool` dispatcher was removed because the model invented parameters it never saw; discovery did not
  actually reduce context because all schemas were still sent; four process boundaries made errors opaque.

**mixelpixx/Konnect** (Rust, native KiCad plugin, 499★)
- 221 tools in 20 **on-demand toolsets** + 7 always-visible meta-tools (`list_toolboxes`, `load_toolset`,
  `unload_toolset`, `get_active_toolsets`, `get_recent_calls`, `server_stats`, `get_installation_info`);
  `tools/list_changed`; starter set ≈2K tokens vs ≈23K for everything; error for an unloaded tool names
  its toolset; observability ring buffer + JSONL so the LLM can self-diagnose; ships 6 skills + 2 agents + hooks.
- Open issues show the usual gaps ("no tool for X") and file-vs-IPC path inconsistencies.

**AvatarSD/KiSkill** (Python stdlib CLI + Claude Code skills)
- No MCP: 9 skills over a small atomic op set; mandatory session state machine
  CLEAN→PROBED→STAGED→VERIFIED→RENDERED→REVIEWED→COMMITTED; geometric verifier before every write;
  triple diff (pixel / semantic tree / ERC set-diff vs baseline); two-tier rule canon (machine-checked vs
  render-judged); a self-improvement skill.

### Patterns adopted or planned for this project

Adopted in phase 1:
- Thin primitive tools, methodology in a skill (Konnect/KiSkill/flaco). → `skills/altium-project-analysis`.
- MCP `instructions` + `ToolAnnotations` (ReadOnly/Idempotent) on every tool (flaco).
- Explicit `projectPath` on every project tool instead of relying on the focused document (flaco's lesson).
- Filtered/paginated reads with `total/offset/returned`; parameters/pins opt-in (flaco `include_queries`).
- Structured errors with stable codes and hints rather than silent partial results (KiCAD-MCP lesson).
- Single in-process code path per operation, no file polling, no script engine (avoids coffeenmusic's
  stuck-debugger and stale-response classes entirely).
- Request correlation `id` in the bridge envelope; per-request log line with elapsed ms.

Planned (see ROADMAP):
- Toolsets with meta-tools and `tools/list_changed` (Konnect).
- `get_recent_calls` / `server_stats` observability tools (Konnect).
- Verification tools separate from mutations; `dry_run` + ChangeSet on every write; snapshots before
  mutation (coffeenmusic, embedded-society, KiCAD-MCP).
- Batch writes as one Altium undo transaction (coffeenmusic).
- Screenshot / rendered preview with `zoom_to` for lightweight visual confirmation (coffeenmusic).
- Hybrid backend (live extension vs. file reader for libraries) with the router reporting which backend served
  the call (10x unified, embedded-society).
- Session state machine for edit workflows in skills (KiSkill).

Anti-patterns to avoid:
- One `execute_tool`/`run_query` mega-dispatcher with hidden schemas.
- Multi-hop bridges with per-hop timeouts and stdout parsing.
- Reading implicit state (focused doc, selection) in read tools.
- Divergent behaviour between backends for the same tool.
- Pre-baked generators for fixed package types instead of primitive writes + agent reasoning.

## B. Altium API knowledge sources

Priority order used: official docs → working local extensions → decompiled SDK → open-source MCPs → experiment.

- Local working extensions: `C:\Users\minat.ALTIUMSERVER0\AltiumExtensions` (TestExtension2026 for the .NET 8
  csproj/deploy pattern, ReplicationBOM for a document-kind extension, `tools\altium-session\AltiumSession.ps1`
  for lifecycle automation, `.cursor\skills\extension-lifecycle` and `ad-disasm` skills for workflow).
- Decompiled Altium code: `D:\AD_Disasm`. Two trees: `Code\System\...` (current) and `Altium Developer\...`
  (older mirror, same layout; use it when ILSpy mangled identifiers in the other). Key files:
  `Altium.SDK\DXP\GlobalVars.cs`, `ServerModule.cs`, `Altium.SDK.Interfaces\{DXP,EDP,SCH,PCB}\*.cs`,
  `docs\Altium.DotNetSupport_Extension_Loading_Algorithm.md`, and real plugins `Code\System\Altium.PinsPanel`,
  `Altium.PCB.FullComponents`, `Altium.DesignReuse.ServerModule`, `Altium.Edp.PartSearch.Plugin`.
- Structural fact that unlocks the SDK: every COM interface exposes `Internal_Xxx()` members returning `object`;
  the typed API lives in sibling static extension classes `<IName>Helper.cs` (e.g. `IClientHelper.GetCurrentView`).
  Always look in the Helper file first.
- Official docs (Altium Designer SDK / scripting reference) were used for naming conventions of the object
  models (`ISch_*`, `IPCB_*`, `DM_*`) but are thin for the .NET SDK; the decompiled helpers are the ground truth.
- Experiments performed this session are recorded in `ALTIUM_API_NOTES.md` ("Loading behaviour (measured)").
