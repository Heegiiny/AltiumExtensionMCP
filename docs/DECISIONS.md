# Architecture decisions

Format: context → options → decision → consequences. Newest last.

## D1. Integration path: .NET 8 Altium extension (not DelphiScript, not file polling)

- Context: existing open-source Altium MCPs mostly drive Altium via DelphiScript scripts and files
  (request/response through the file system) or via the Altium scripting engine. Locally we have
  a working .NET-extension workflow (`C:\Users\...\AltiumExtensions`, TestExtension2026) and the
  decompiled SDK (`D:\AD_Disasm`).
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
