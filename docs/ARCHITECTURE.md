# Architecture

Status: phase 1 (read-only), working end-to-end against Altium Designer 26.3 (2026-09-07) and 26.9.1
(2026-09-08/10, dedicated agent workstation — see `ENVIRONMENT.md`). Target EDA: Altium Designer only.

## Why three processes/layers

Altium Designer (X2.EXE) is a native host that loads .NET 8 extensions through
`Altium.DotNetSupport.dll`. Every SDK call is COM on the UI (STA) thread. MCP clients, on the
other hand, expect to *spawn* a stdio server process. Those two facts force a split:

```
┌──────────────────────────────┐   stdio JSON-RPC (MCP)   ┌──────────────────────────────┐
│ MCP client                   │◀────────────────────────▶│ AltiumMcp.Server (console)   │
│ Claude Desktop/Cursor/Code   │                          │ ModelContextProtocol 2.2.0   │
└──────────────────────────────┘                          │ Tools: altium_*              │
                                                          │ BridgeClient (HTTP)          │
                                                          └──────────────┬───────────────┘
                                                                         │ POST /rpc {method, params}
                                                                         │ GET  /health, /methods
                                                                         │ 127.0.0.1:47120 (+discovery file)
┌────────────────────────────────────────────────────────────────────────▼───────────────┐
│ X2.EXE (Altium Designer)                                                               │
│  └ Altium.DotNetSupport → AltiumExtensionMCP.dll (AltiumMcp.Extension)                 │
│       PluginFactory → McpServerModule (DXP.ServerModule)                               │
│         ├ BridgeHttpServer (HttpListener, background threads)                          │
│         ├ BridgeRouter  (method name → handler, JSON in/out, error wrapping)            │
│         ├ UiThreadDispatcher (Control.BeginInvoke → UI thread, timeout)                │
│         ├ Queries: System, Workspace, Project (EDP compiled model),                     │
│         │          Schematic (SCH sheet object model), Pcb (PCB board object model)     │
│         └ BridgePanelView/Form (status panel) + .rcs menu entries (File/Tools > MCP Bridge) │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

`AltiumMcp.Contracts` (net8.0, no Altium references) holds the wire protocol and every DTO,
so the extension and the server can never disagree about JSON shape, and tests run without Altium.

## Components

### AltiumMcp.Contracts
- `Bridge/BridgeProtocol.cs` — `BridgeRequest {method, params, id}`, `BridgeResponse {ok, result | error, elapsedMs}`,
  `BridgeError {code, message, details}`, `BridgeErrorCodes` (stable string codes), `BridgeMethods` (method names),
  `BridgeJson` (shared `JsonSerializerOptions`: camelCase, enums as strings, **defaults omitted**).
- `Bridge/BridgeDiscovery.cs` — `BridgeDescriptor` written to `%LOCALAPPDATA%\AltiumMcp\bridge.json`,
  default port 47120, env overrides `ALTIUM_MCP_BRIDGE_URL` / `ALTIUM_MCP_BRIDGE_PORT`, log dir.
- `Model/*.cs` — `EnvironmentInfo`, `WorkspaceInfo`, `ProjectSummary`, `DocumentInfo`, `ProjectStructure`,
  `HierarchyNode`, `ViolationSummary`, `ComponentSummary/Detail`, `PinInfo`, `NetSummary`, `NetPinRef`,
  `SchematicModel.cs` (`SheetInfo`, `SchObject`, `SchComponentDetail`), `PcbModel.cs` (`BoardInfo`, `PcbComponent*`,
  `PcbNet*`, `PcbRule`, `PcbPrimitive`), params classes.
- `Model/FilterMatcher.cs` — list-filter semantics (substring or `*`/`?` wildcard).

### AltiumMcp.Extension (assembly `AltiumExtensionMCP.dll`)
- `Plugin/PluginFactory.cs` — `CSharpPlugin.IPluginFactory`; Altium's entry point.
- `Plugin/McpServerModule.cs` — `DXP.ServerModule`: registers commands (`StartBridge`, `StopBridge`,
  `ShowBridgePanel`, `BridgeStatus`), builds the router with all query handlers, starts the HTTP server
  **in the module constructor** (so any load path — `-R` switch, panel restore, command — starts the bridge).
- `Bridge/UiThreadDispatcher.cs` — hidden WinForms `Control` created on the UI thread at module init;
  `Invoke(Func<T>, timeout)` marshals via `BeginInvoke` and waits with a timeout → `ALTIUM_BUSY`
  instead of deadlocking when Altium is inside a long operation.
- `Bridge/BridgeHttpServer.cs` — `HttpListener` on `http://127.0.0.1:{port}/`; tries the default port
  then the next free ones; writes/removes the discovery file; request counter; per-request logging.
- `Bridge/BridgeRouter.cs` — `Dictionary<string, Handler>`; deserializes params, runs handler on the UI
  thread, serializes result; maps `BridgeException` → structured error, anything else → `INTERNAL` with
  exception type/message/stack in `details`.
- `Queries/AltiumAccess.cs` — the only place that touches `DXP.GlobalVars`/`IClient`/`IWorkspace`
  resolution; `Safe(() => comCall)` swallows per-property COM failures so a single bad property never
  kills a whole result; project lookup by path with `PROJECT_NOT_FOUND`.
- `Queries/*Queries.cs` — pure "read SDK → DTO" code, one class per method family.
  `SchematicQueries` resolves a sheet (active → `GetSchDocumentByPath` → hidden `LoadSchDocumentByPath`) and
  iterates `ISch_Iterator`; `PcbQueries` wraps board resolution, layer-name cache and `BoardIterator` in
  `BoardContext` (iterators destroyed in `finally`). Partial-class files split the big families:
  `PcbQueries.Drc.cs` (`pcb.runDrc`, `pcb.listViolations`), `PcbQueries.Select.cs`, `SchematicQueries.Select.cs`.
- `Queries/EditorCommands.cs` — the *editor-state* layer (DECISIONS D13): show a document
  (`OpenDocumentShowOrHide` + `ShowDocument`) and run Altium processes (`PCB:Zoom`, `Sch:Zoom`, …) through
  `(IClient as IProcessLauncher).SendMessage(process, params, view)`. Handlers that change editor state
  (`workspace.openDocument`, `sch.select`, `pcb.select`, `pcb.runDrc`) go through here or through explicit
  selection/DRC APIs; none touches design data or marks a document modified.
- `Panel/*` — `ServerPanelView` + WinForms user control showing URL, pid, request count and the
  in-memory log tail.
- `Installation/AltiumExtensionMCP.ins` — server registration, `PanelInfo`, commands;
  `AltiumExtensionMCP.rcs` — menu entries (*File > MCP Bridge* on the home page, *Tools > MCP Bridge* in
  SCH/PCB editors). Both copied next to the DLL.

### Build / environment plumbing
- `Directory.Build.props` imports the git-ignored `Environment.local.props` (machine-specific: Altium install,
  profile GUID, optional roots) and derives SDK/deploy paths; `Directory.Build.targets` fails fast with a hint
  when the SDK is missing (only for projects with `RequiresAltiumSdk=true`).
- `tools/AltiumEnvironment.ps1` resolves the same facts for scripts (props → registry → defaults).
  See `ENVIRONMENT.md` and DECISIONS D11.

### AltiumMcp.Server
- `Program.cs` — no args → MCP stdio host (`Host.CreateApplicationBuilder`, logs to **stderr**);
  `--health | --diag | --methods | --call <method> [json]` → debug CLI.
- `BridgeClient.cs` — locate (env → discovery file with live-pid check → default port), `CallAsync`,
  `HealthAsync`, `Locate()` diagnostics; throws `BridgeUnavailableException` (with `hints`) or
  `BridgeCallException` (carrying `BridgeError`).
- `ToolResults.cs` — turns results/exceptions into `CallToolResult` (`isError=true` + JSON `{error, details}`).
- `Tools/*.cs` — thin `[McpServerTool]` methods; descriptions are written for the LLM (what, when, cost).

## Cross-cutting rules

- **Read-only.** No handler mutates the design. The state-changing options are opt-in and documented:
  `compileIfNeeded` (compiles the project), `loadIfClosed` (loads a sheet/board hidden — no editor tab,
  nothing saved) and `workspace.openDocument` (editor tab only).
- **Stable identifiers.** Project = full path; document = full path; component = schematic `UniqueId`
  (`\XXXX\YYYY\ZZZZ` hierarchical) with designator as a convenience; net = flattened net name.
  Sheet objects = sheet-level `UniqueId`; PCB components = PCB `UniqueId` plus `sourceUniqueId` (= compiled id).
- **Compact JSON.** Null/false/0/empty are omitted on the wire; list tools paginate with
  `total/offset/returned`; big blobs (technology sets, parameters) are opt-in or summarised.
- **Errors are data.** Every failure has a stable `code`, a human message and usually a `hint`
  telling the agent what to do next. The MCP layer never throws to the client; it returns `isError`.
- **Threading.** HTTP threads never touch COM. The dispatcher is the single choke point.
- **Robustness.** `Safe()` around every COM property read; the bridge survives closed projects,
  half-loaded documents and missing compiled models.

## Extension points (for later phases)

- New read family: add DTOs in Contracts, `XxxQueries` in Extension, register in `McpServerModule`,
  add `XxxTools` in Server. Nothing else changes.
- Write operations: add a `Mutations/` folder in Extension; every mutation handler should accept
  `dryRun` and return a `ChangeSet` DTO (`before/after`) so preview/undo/diff can be built on top.
- Toolsets: tools are already grouped per class (`ConnectionTools`, `WorkspaceTools`, `ProjectTools`,
  `SchematicTools`, `PcbTools`); progressive disclosure can be implemented by registering classes
  conditionally or by a `altium_enable_toolset` meta-tool that toggles `ListChanged`.
- Graphics: a `Render/` family in the extension (sheet/board → PNG/SVG via Altium's own print/export
  or a custom painter over the object model) feeding an `altium_render_*` tool for VLM analysis.
- Workspace/Server (phase 2): `WorkspaceServerQueries` over `EDP.Utils.GetDXPServerManager()` /
  `GetVaultManager()` — connection state, managed project GUIDs, revisions; read-only first.
- Not planned: multi-EDA abstractions (KiCad etc.). Contracts stay Altium-shaped; any generality is
  incidental, not a design goal (decision recorded in `ROADMAP.md`, 2026-09-10).

## Data flow example — `altium_get_component U1`

1. Client → server: `tools/call altium_get_component {component:"U1"}`.
2. `ProjectTools.GetComponent` → `BridgeClient.CallAsync("project.getComponent", {component:"U1"})`.
3. HTTP POST → `BridgeHttpServer` → `BridgeRouter.Execute` → `UiThreadDispatcher.Invoke`.
4. UI thread: `ProjectQueries.GetComponent` → `IWorkspace.DM_FocusedProject()` →
   `IProject.DM_DocumentFlattened()` → iterate `DM_Components(i)` matching `DM_UniqueId`/`DM_PhysicalDesignator`/
   `DM_LogicalDesignator` → read pins (`DM_Pins(i)`, `DM_FlattenedNetName`), implementations, parameters.
5. `ComponentDetail` serialized → `BridgeResponse.ok` → `CallToolResult` text content (JSON).
Measured: 9–25 ms on a 72-component project.
