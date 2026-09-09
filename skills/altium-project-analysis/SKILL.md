---
name: altium-project-analysis
description: Procedure for analysing an Altium Designer project through the altium-mcp tools (read-only). Use when asked to inspect, review, summarise, or answer questions about a project, its sheets (objects, positions), components, nets, parameters, compile violations, or its PCB (placement, layer stack, nets/routing, rules, DRC markers) in a running Altium Designer.
---

# Altium project analysis (read-only)

You are talking to a **live** Altium Designer through `altium_*` tools. Data comes from Altium's
compiled ("flattened") project model, not from parsing files, so it reflects what Altium itself
believes about the design. Nothing in this skill modifies the design.

## 0. Ground rules

- Never claim something about the design that you did not read through a tool in this session.
- Prefer small, targeted calls (filter + limit) over dumping whole lists. A 300-component BOM
  is thousands of tokens; a filtered query is tens.
- Identifiers: project = full file path; component = `id` (schematic UniqueId, stable) or
  `designator`; net = flattened net name. Reuse them verbatim.
- Absent boolean/number fields in results mean `false` / `0` (compact output).
- If a tool returns `isError`, read `error.code` and `details.hint` and follow the hint instead
  of retrying blindly.

## 1. Establish the connection (always first)

1. `altium_ping`.
   - OK: note `processId`; continue.
   - `BRIDGE_UNAVAILABLE`: call `altium_bridge_diagnostics`, then tell the user exactly what the
     hints say (Altium not running / extension not loaded / stale discovery file). Do not guess
     about the design while disconnected.
2. Optionally `altium_get_environment` when the Altium version or available editors matter
   (e.g. "is PCB editor loaded?").

## 2. Orient: what is open?

1. `altium_get_workspace` - focused project and document.
2. `altium_list_projects` - all projects in the project group. Pick the right `path`; do not
   assume the focused one is the one the user means if several are open. `Free Documents` is not
   a real project.
3. `altium_list_open_documents` only when the question is about editor state (which sheet is
   open, unsaved changes).

If no `.PrjPcb` is open, stop and ask the user to open one; the project tools need it.

You do not need to open sheets or boards to read them: `sch.*`/`pcb.*` load closed documents hidden.
Use `altium_open_document` only when you want the engineer to *see* something (or before a screenshot);
it changes editor state (tab focus), never design data. Pass `documentPath` explicitly to every
sheet/board tool instead of relying on "the active document".

## 3. Structure before detail

`altium_get_project_structure` once per project. Read from it:

- `isCompiled` - if false, the component/net tools will fail with `NOT_COMPILED`. Ask before
  using `compileIfNeeded=true` on a project you do not own (it changes project state), or tell
  the user to run *Project > Validate PCB Project*.
- `hierarchy` - sheet tree with `instanceName`; multi-channel designs repeat a sheet under
  different instance names.
- `logicalDocuments[*].kind` - `SCH`, `PCB`, `OUTPUTJOB`, `Harness`, `VirtualBOM`...
- `violations` - `total`, `byErrorLevel`, `byErrorKind`, and a sample biased towards errors.
  This is Altium's compiler output, not your own review.
- `primaryImplementationDocument` - the PcbDoc.

Summarise this to yourself (sheet count, component/net totals, violation counts) before drilling in.

## 4. Drill down with filters

Components:
- Class survey: `altium_list_components` with `filter="U*"`, `"R*"`, `"C*"`, `"J*"`, `limit=50`.
- Specific part search: `filter="LM358"` or `filter="*0402*"` (matches comment/libref/footprint/description too).
- Details (pins, nets on each pin, all parameters, footprint variants): `altium_get_component`
  by `id` or `designator`. `designator` is the physical (board) designator; `logicalDesignator`
  is what the sheet shows and differs in multi-channel designs.
- Only set `includeParameters=true` on a list call when you need parameters for many parts at once.

Nets:
- Power tree: `altium_list_nets` with `filter="VDD*"`, `"VCC*"`, `"*3V3*"`, `"GND"`.
- Connectivity of one net: `altium_get_net` - full pin list with electrical types.
- Suspicious nets: `pinCount` <= 1, auto-generated names (`NetU1_11`) on pins that should be connected,
  nets with `powerObjectCount` > 0 but no power pins.

## 5. Sheet-level questions (schematic object model)

Use the `altium_*sheet*` tools when the question is about *where* something is drawn or about
graphical objects that the compiled model does not carry (wires, net labels, ports, text notes).

- `altium_get_sheet` first: size, `objectCounts` (how many wires/labels/ports), sheet parameters
  (title, revision). `documentPath` comes from `altium_get_project_structure`; closed sheets are
  loaded hidden (`wasLoadedOnDemand=true`) - this does not modify anything.
- `altium_list_sheet_objects` with `types` narrowed to what you need (`["NetLabel","Port","PowerObject"]`
  for connectivity by name; `["Component"]` for placement; `["SheetSymbol","SheetEntry"]` for the
  hierarchy as drawn - entries come with `owner` = sheet symbol name and `ioType`/`side`; `["Wire"]` only
  when tracing geometry - wires carry `vertices` and are verbose). `filter` matches `text` (label/port
  name, designator). Results are in drawing order, containers first: request one type per call when you
  page, otherwise a page may hold only the first type.
- `altium_get_sheet_component` for one part as drawn. `component` accepts the designator as drawn
  (`U2`), a multi-part form (`U2A`, `U2B` - each part is a separate sheet object), the sheet `id`, or the
  **physical** designator / compiled `id` from the project tools when the project is compiled. The
  result carries `physicalDesignator` and `compiledIds` (several on multi-channel sheets - one per
  channel), pins with sheet coordinates and hidden-net names, all parameters with visibility flags,
  `managed` GUIDs when the part comes from a Workspace/Vault. `OBJECT_NOT_FOUND` lists
  `designatorsOnSheet` - use it instead of guessing.
- Designators differ between models on hierarchical designs: the sheet shows the logical designator
  (`U2`), the board/BOM the physical one (`U4`). Always say which one you mean; join the models through
  `compiledIds` / `id`, not through designator text.
- Coordinates are mils from the sheet's bottom-left corner. Sheets that only wire sub-sheets have zero
  components - that is normal, not an error.

## 6. Board-level questions (PCB object model)

- `altium_get_board` first: outline size, `layerStack` (copper layers with thickness; dielectrics are
  not reported yet), `objectCounts`, `classes`, `violationCount`. The `.PcbDoc` path is
  `primaryImplementationDocument` from the structure call. Expect ~1 s per PCB call on a board that is
  not open in the editor (first call ~6 s); do not treat that as a hang.
- `violationCount` and `Violation` primitives are **0 until a DRC has been run in Altium**. Zero
  violations therefore means "no DRC results present", not "the board passes DRC" - say so.
- Placement: `altium_list_pcb_components` (filter by designator/footprint, `layer="Bottom"` for
  bottom-side parts); `altium_get_pcb_component` for pads with nets and geometry. `sourceUniqueId`
  equals the compiled component `id` - use it, not the designator, to join schematic and PCB data
  in multi-channel designs.
- Connectivity/routing: `altium_list_pcb_nets` - `unroutedConnectionCount` is the number of remaining
  ratsnest lines (absent = fully routed as far as the editor knows), `routedLengthMils` is Altium's own
  length; `altium_get_pcb_net` for pads, layers used and track length from geometry. Nets present in
  `altium_list_nets` but absent here mean the PCB is out of sync with the schematic.
- Rules: `altium_list_pcb_rules` (filter `"Clearance"`, `"Width"`, `"Routing*"`); `summary` is the
  constraint text; higher `priority` number = lower priority.
- Primitives: `altium_list_pcb_primitives` is the heavy tool. Always give `layer` and/or `net`, keep
  `limit` <= 200 and page by `offset`. Use layer names from `altium_get_board` (`"Top Layer"`,
  `"Bottom Overlay"`); an unknown layer returns `INVALID_PARAMS` with the valid candidates.
  `types=["Violation"]` lists DRC markers with descriptions (after a DRC run).
  Coordinates are mils relative to the board origin - the same numbers the PCB editor shows.

## 7. Verify before you conclude

- Cross-check at least one claim two ways (e.g. a pin's net from `altium_get_component` and the
  same pin in `altium_get_net`; a PCB pad's net vs the compiled pin's net).
- Counts: `total` in list results is the authoritative count for the filter; do not count
  returned items when `returned < total`.
- Compile violations and DRC markers reported by Altium are evidence; your own inferences are
  hypotheses. Label them.
- Do not infer PCB facts from the schematic model or vice versa; read the model the question is about.

## 8. When you may not say "analysis complete"

Do not declare a project fully analysed if any of these hold:

- the project was not compiled (`isCompiled=false`) or you did not read `violations`;
- you paged through fewer items than `total` for a list you are drawing conclusions from;
- you are making a DRC claim while `violationCount` is 0 (no DRC run) - only Altium's DRC can clear a board;
- the question needs a visual judgement (silkscreen legibility, placement aesthetics, routing style) -
  no rendering tool exists yet; say that you reasoned from geometry only;
- the question touches library health, variants or Workspace/server state (not available yet);
- any tool call returned an error you did not resolve.

Say what you covered, what you did not, and which tool/data would be needed.

## 9. Output style for the user

Lead with the answer, then the evidence (designators, net names, counts) so the engineer can
check it in Altium. Keep raw JSON out of the reply unless asked.
