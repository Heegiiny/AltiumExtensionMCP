# MCP optimization, error handling, and Cross Probe

Status: implementation input for Fable/Opus agents. No source-code change is requested by this note.

## 1. Project nets must be followed across sheets

Correction to the earlier optimization proposal: a project net that appears on two or more sheets is
one logical net and must be analysed across all of those sheets. `documentPath` must not silently cut a
project net down to one sheet.

Required behaviour:

- Resolve connectivity from the compiled project net identity and Altium scope rules, not from text
  matching alone.
- Return every participating sheet, port, pin, label, and sheet-to-sheet transition for a project net.
- Replace the misleading singular `documentPath` in project-level net results with `documentPaths`
  and/or per-sheet `segments`.
- An optional `documentPath` may identify the starting segment or narrow presentation, but the result
  must retain links to all connected sheets unless the caller explicitly requests a local-only view.
- Identically spelled labels that are genuinely separate local nets must remain separate. Return stable
  net identities/candidates instead of arbitrarily choosing the first textual match.
- A single compiled project net spanning several sheets is not ambiguous: aggregate every participating
  sheet into one result. `AMBIGUOUS_OBJECT` applies only when the caller supplied too little context to
  choose between two or more genuinely distinct compiled net identities that happen to have the same
  displayed name. In that case, return the candidate net identities, scopes, and sheets rather than
  silently choosing one.

Suggested result shape:

```json
{
  "netId": "compiled-stable-id",
  "name": "RESET-0",
  "scope": "project",
  "documentPaths": ["SheetA.SchDoc", "SheetB.SchDoc"],
  "segments": [
    { "documentPath": "SheetA.SchDoc", "pins": [], "ports": [] },
    { "documentPath": "SheetB.SchDoc", "pins": [], "ports": [] }
  ]
}
```

Acceptance check: querying a shared project net from either participating sheet produces the same
logical `netId` and exposes both sheets.

## 2. Divide responsibilities between MCP primitives and the analysis skill

The MCP provides generic search/filter operations and compact, objective schematic facts:

- ports and their directions;
- symbols/components, designators, values, library references, and pin-to-net relationships;
- power objects and their exact text/style;
- power pins and all connected nets;
- net scope and cross-sheet connectivity;
- optional compact counts and grouping keys.

Create and evolve an analysis skill that uses those primitives as an engineering investigation workflow.
Its initial guidance can stay broad and improve with real projects. A useful first pass is:

- Start by locating likely ICs (`U?`), transistors (`Q?`), connectors, regulators, references, and other
  functionally important parts. Allow for projects that use different designator conventions.
- Work out importance from function, connectivity, pin count, surrounding circuitry, and the user's
  question. The MCP only filters and returns facts; it does not decide which ICs are "major".
- Inspect how power is generated, referenced, distributed, filtered, and consumed. Infer rails from
  power objects, regulator/reference components, power pins, connected nets, names, and circuit context.
- Look for programmable devices and components likely to contain firmware or configuration data, then
  report this as an evidence-backed hypothesis rather than assuming every `U?` is programmable.
- Follow relevant nets across every participating sheet and relate schematic findings to the PCB when
  the question requires it.
- Begin with a compact inventory, then expand only the relevant components and nets.

Recommended server primitives are field projection, `detail = summary|connectivity|full`, batch
component/net lookup, and bounded connectivity tracing. The skill composes those primitives into an
analysis workflow.

## 3. Use the project as context and never show MCP exceptions as Altium modal dialogs

Observed defect: MCP activity intermittently causes Altium exception windows similar to "file None not
found". This is important agent-facing diagnostic information, but it must not interrupt the user's UI.

Required behaviour:

- Treat missing values, JSON `null`, empty strings, and accidental strings such as `"None"`/`"null"`
  as invalid or absent input before calling any Altium document-loading API.
- Treat the selected/resolved Altium project as the primary context. The active document is only a UI
  hint and may be an unrelated file; it must not bound project analysis or silently become the target.
- Resolve requested objects across the whole project. If a required schematic or PCB document is closed,
  the MCP may load/open it itself. Read-only project inspection may use a hidden load where supported;
  a visible Cross Probe may deliberately open and activate the target document.
- If no project or requested document can be resolved, return a structured context error. Never attempt
  to load a file literally named `None`.
- Validate paths before `LoadSchDocumentByPath`, `LoadPCBBoardByPath`, or equivalent calls that may
  cause Altium itself to display a dialog.
- Catch exceptions at tool, router, UI-thread-dispatch, and extension notification boundaries. Do not
  let an exception escape into an Altium event loop or COM callback.
- Return stable errors such as `INVALID_ARGUMENT`, `DOCUMENT_NOT_FOUND`, `DOCUMENT_NOT_OPEN`,
  `ALTIUM_API_ERROR`, and a correlation ID. Put full exception details in the bridge log, not a modal.
- Failed queries must not dirty a document or leave an accidental half-open document behind. A successful
  explicit open or Cross Probe may change the active document as part of its intended UI behaviour.

Add tests for omitted path, JSON null, empty path, `"None"`, nonexistent path, closed document, no active
document, no resolvable project, and an injected Altium API exception. A closed document and no active
document should still succeed when the project and requested object are resolvable. Invalid/unresolvable
cases must return an MCP error and produce no modal window.

## 4. Cross Probe for visible MCP activity

Goal: when an MCP query addresses a component or net, Altium can visibly highlight the same object so
the user sees what the agent is examining.

Required UI and behaviour:

- Add a persistent checkbox such as **Follow MCP queries in Altium**, analogous to normal schematic/PCB
  Cross Probe behaviour.
- When enabled, component and net lookup/trace tools cross-probe to the correct document and object.
- Resolve targets by document path plus stable object/net identity; a bare designator is insufficient
  when several sheets contain `U201`, `K201`, and similar names.
- Project-net probing must make cross-sheet participation visible. Select the requested or most relevant
  segment first and expose the other participating sheets in the result/status panel.
- Match normal Altium Cross Probe semantics: open/activate the target document if necessary and move or
  replace the current Altium selection with the newly probed component or net. Preserving an older user
  selection is not required; the visible selection should show what the agent is looking at now.
- Cross Probe is a UI side effect, not a design edit: it must not mark documents modified.
- Coalesce rapid calls so repeated internal lookups do not cause flicker or pointless focus changes.
- Allow a per-call override (`crossProbe: true|false`) while retaining the checkbox as the default.
- Run all Altium UI interaction on the UI thread and apply the no-modal exception policy above.

## 5. Reading the user's current selection

Add a compact selection query, for example `altium_get_selection`, that returns:

- active document and editor type;
- selected component/net/primitive identities;
- designators, values, pin/net context, and stable object IDs;
- selection revision and timestamp when practical.

Critical skill rule: do not inspect or interpret selection merely because something happens to be
selected. Selection is meaningful only when the user explicitly points to it, for example:

> "Вот выделил транзистор, он не слишком слабый?"

Phrases such as "выделил", "выбранный", "вот этот", or an explicit request to inspect the current
selection authorize the selection query. Otherwise incidental selection—including the current selection
left by an earlier MCP Cross Probe—must not be treated as user intent.

Acceptance checks:

1. A user-selected transistor can be resolved from the active sheet and analysed without requiring its
   designator in the prompt.
2. Cross Probe replaces/moves the Altium selection in the same manner as normal schematic/PCB probing.
3. With no explicit reference to selection, the analysis workflow ignores the current selection.

## 6. Token-efficiency requirements

- Default responses must omit managed GUIDs, hidden parameters, footprint implementation lists, bounds,
  and coordinates unless requested.
- Support field projection and batch queries for components and nets.
- Return compact connectivity edges instead of requiring the agent to reconstruct every wire vertex.
- Estimate or bound large responses and paginate before output grows beyond the useful context budget.
- Server instructions and the analysis skill should prescribe: structure -> compact IC inventory ->
  relevant connectivity -> targeted full detail.

The emitter-test analysis demonstrated the target outcome: the useful explanation came primarily from
compact `component pin -> net` relationships. Broad object dumps and repeated full component records
were expensive and should not be the default workflow.

## 7. Treat PCB/project agent documentation like code documentation

Project-level agent documentation such as `AGENTS.md` and design notes should live with the Altium project
and be maintained as part of the engineering work, just as agent and architecture documentation lives
with a software repository.

Required workflow and future capability:

- At the start of project work, look for `AGENTS.md` from the project directory upward and read the
  applicable instructions even if the file is not currently listed inside the `.PrjPcb` project.
- Allow an authorized agent to create or edit Markdown/text documentation on disk under the resolved
  project root and then add the existing file to the Altium project. The simplest implementation may be
  a project-aware `add existing document` operation rather than building a Markdown editor into MCP.
- Provide project/document operations such as opening a requested project document and adding an existing
  documentation file to the project. These operations must accept an explicit project/document identity;
  they must not depend on whichever editor happens to be active.
- Restrict file creation and project insertion to the resolved project root (or another explicitly
  authorized path), return the resulting path/project membership, and read back the project state after
  the operation.
- Documentation writes are explicit mutations: analysis-only requests do not authorize creating,
  editing, saving, or adding files.
- Opening a needed schematic, PCB, or documentation file is normal project navigation and should be
  supported even when that file was initially closed.

The currently created `C:\Altium Projects\MY PROJECTS\Main Board\AGENTS.md` is intentionally only a
placeholder for now; its actual project guidance can be drafted and refined later.
