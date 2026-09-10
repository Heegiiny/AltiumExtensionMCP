# Architecture decisions

Format: context → options → decision → consequences. Newest last.

## D1. Integration path: .NET 8 Altium extension (not DelphiScript, not file polling)

- Context: existing open-source Altium MCPs mostly drive Altium via DelphiScript scripts and files
  (request/response through the file system) or via the Altium scripting engine. Locally we have
  a working .NET-extension workflow (`C:\Users\...\AltiumExtensions`, TestExtension2026) and the
  decompiled SDK (`D:\AD_Disasm`). *(Paths as of the first machine; current locations are
  `<ExtensionExamplesRoot>` / `<DisasmRoot>` in `ENVIRONMENT.md` — see D11.)*
- Options: (a) DelphiScript + file mailbox; (b) COM automation from outside (not exposed by Altium);
  (c) in-process .NET extension using the official SDK.
- Decision: (c). Full typed access to `EDP.IWorkspace/IProject/IDocument/IComponent/INet`,
  compiled model, later SCH/PCB object models, no script-engine dialogs, sub-30 ms calls.
- Consequences: must build against SDK25, target net8.0-windows, deploy into
  `%ProgramData%\Altium\...\Extensions\`, Altium must be closed to redeploy the DLL, calls must be
  marshalled to the UI thread.

## D2. Transport between MCP server and extension: HTTP JSON-RPC on loopback

- Options: named pipes; raw TCP; HTTP via `HttpListener`; hosting the MCP server itself inside Altium
  (HTTP/SSE MCP transport).
- Decision: `HttpListener` on `http://127.0.0.1:47120/` (falls back to next free port) with a tiny
  JSON-RPC envelope. Verified that loopback `HttpListener` needs no URL ACL / admin rights.
- Why not MCP-in-process: MCP clients today spawn a stdio process; ASP.NET Core inside X2.EXE is
  heavy and version-fragile; a thin bridge keeps the extension small and lets the MCP layer evolve
  (tool names, toolsets, multi-EDA) without redeploying into Altium.
- Consequences: two processes; discovery file needed; trivially debuggable with `curl`/`--call`.

## D3. Discovery: `%LOCALAPPDATA%\AltiumMcp\bridge.json` + env override

- Contains `baseUrl, port, processId, productVersion, startedAt`. Client validates that `processId`
  is alive (stale files after a crash are ignored) and falls back to the default port.
- `ALTIUM_MCP_BRIDGE_URL` / `ALTIUM_MCP_BRIDGE_PORT` override for multi-instance setups.

## D4. Loading the extension: `-R<Server>:<Command>` at startup + docked panel

- Finding: Altium loads extension DLLs lazily (on first command, document kind or panel). The `-R`
  switch does **not** load a module into an already-running instance (tested), but at cold start
  `X2.EXE -RAltiumExtensionMCP:StartBridge` loads the module and the bridge is up within ~5 s.
- The `McpServerModule` constructor starts the bridge, so *any* load path works. The `AltiumMcpBridge`
  panel (verified via `-RAltiumExtensionMCP:ShowBridgePanel`) is a status UI; the hoped-for
  "Altium restores the panel and thereby loads us" behaviour did **not** occur on a plain restart in
  testing (floating panel, WM_CLOSE exit). Until that is understood, the `-R` switch / helper script is
  the supported auto-start path.
- Rejected: custom document kind + dummy file (would flash an editor tab and can pop error dialogs).

## D5. UI-thread marshalling with timeout (`ALTIUM_BUSY`) instead of blocking `Invoke`

- Altium can be inside a long compile/modal operation; a blocking `Invoke` from the HTTP thread
  would hang the MCP call forever. `BeginInvoke` + wait-with-timeout returns a structured
  `ALTIUM_BUSY` error the agent can act on.

## D6. Compact JSON: omit default values

- `JsonIgnoreCondition.WhenWritingDefault` on the shared options. Cuts list payloads ~35 %.
  Contract: absent bool = false, absent number = 0. Envelope `ok` relies on this too (`ok=false` is omitted;
  deserialises back to false). Tool descriptions and the skill state this rule.

## D7. Identifiers

- Component `id` = schematic `DM_UniqueId()` (hierarchical `\A\B\C`) — stable across compiles and
  designator changes. `designator` = **physical** designator (what the board/BOM shows);
  `logicalDesignator` kept alongside for multi-channel designs. Net id = flattened net name
  (Altium has no better stable net id in the compiled model).

## D8. Filters: substring by default, wildcard when `*`/`?` present

- Agents naturally write `R*` or `*0402*`; plain substring would silently return nothing.
  Implemented once in Contracts (`FilterMatcher`) and unit-tested; used by all list tools.

## D9. Tools are thin; methodology lives in skills

- Tool code only maps params → bridge method → result. No "analyse power tree" mega-tools.
  `skills/altium-project-analysis/SKILL.md` carries the procedure (connect → orient → structure →
  filtered drill-down → verify → what not to claim).

## D10. Test boundary

- `AltiumMcp.Tests` references Contracts + Server only (no Altium SDK) so `dotnet test` runs on
  any machine/CI. Anything that must be testable offline (filter, protocol, discovery, error
  mapping) lives in Contracts/Server, not in the Extension.

## D11. Portability: environment-specific paths live in one generated file + one document (2026-09-08/10)

- Context: the project moved from a shared machine (`ALTIUMSERVER0`: repo on `E:`, decompiled code on
  `D:`, AD 26.3 + a second Altium install that must not be touched) to a dedicated agent workstation
  (`SOLDERING01`: everything under the user profile, single AD 26.9.1). Absolute paths and
  machine-specific assumptions were spread over `Directory.Build.props`, scripts and six docs; more
  moves are expected.
- Options: (a) keep editing paths in place on every move; (b) environment variables only; (c) one
  git-ignored MSBuild props file generated from the Altium registry, read by both MSBuild and the
  PowerShell tools, plus one human-readable document listing the current values.
- Decision: (c). `tools\Detect-AltiumEnvironment.ps1` writes `Environment.local.props`
  (`AltiumProgramsHome`, `AltiumProfileGuid`, optional roots); `Directory.Build.props` derives
  `AltiumExe`/`AltiumSdkHome`/`AltiumExtensionDeployDir`; `tools\AltiumEnvironment.ps1` gives scripts the
  same view (props → registry → defaults); `Directory.Build.targets` fails fast with the fix hint.
  `docs/ENVIRONMENT.md` is the only document allowed to hold absolute paths; other docs use symbolic
  names (`<ProjectRoot>`, `<DisasmRoot>`, ...) or link to it. Historical decisions keep their original
  paths with a migration note instead of being rewritten. Registration in Altium's
  `ExtensionsRegistry.xml` is scripted (`Register-Extension.ps1`) because a copied DLL alone is not
  loaded on a fresh profile.
- Consequences: a move = run the detect script, fix `[!!]` lines, register, redeploy, update
  `ENVIRONMENT.md` + `SESSION_HANDOFF.md`. Runtime components already had no configured paths
  (`%LOCALAPPDATA%` discovery + env overrides). Costs: one more file to know about; the props file
  must be regenerated after an Altium upgrade (profile GUID changes).

## D12. Dedicated workstation: Altium restarts and UI automation are allowed (2026-09-08)

- Context: sessions 1–2 ran on the user's own machine, so the extension could not be redeployed
  without coordinating (the DLL is locked while loaded) and no UI automation was permitted; several
  items (settings dialog, live verification) were deferred because of that.
- Decision: the agent machine is dedicated. `Redeploy-Extension.ps1` closes/restarts Altium as part
  of the normal dev loop; UI automation and long experiments are fine. The remaining limits are about
  data, not the machine: no destructive changes to Workspace items, managed components, libraries or
  production project revisions; write tools (phase 3+) still need dry-run/preview/undo.
- Consequences: deferred-for-machine-reasons items are back on the roadmap; the "user is working on
  this machine" caveats in older notes are historical.

## D13. Working MCP first: defer expensive features, allow editor-state tools (2026-09-10)

- Context: session 5 started implementing sheet/board rendering (native PCB `RenderToDC` + an own GDI
  painter for schematics). The user redirected: aim at a working, useful MCP; when a requirement is
  expensive to implement, postpone it instead of sinking the session into it. The immediate need —
  "show the engineer what the agent means" — is met by selecting objects in the open editor.
- Decision: (a) rendering is deferred with its research recorded (`MCP_TOOLS.md` Planned,
  `ALTIUM_API_NOTES.md`); (b) tools that change **editor state only** (open tab, selection, zoom, DRC
  markers/report) are acceptable in phase 1 and are flagged `ReadOnly=false, Destructive=false` in MCP
  annotations and documented as such. Design data (schematic/PCB objects, files) is still never
  modified before phase 3, and none of these tools marks a document dirty.
- Consequences: `sch.select`, `pcb.select`, `pcb.runDrc`, `pcb.listViolations` shipped in one session;
  the roadmap keeps rendering under "Deferred" rather than "Next".
