---
name: altium-project-analysis
description: Procedure for analysing an Altium Designer project through the altium-mcp tools (read-only). Use when asked to inspect, review, summarise, or answer questions about a project, its sheets, components, nets, parameters or compile violations that is open in a running Altium Designer.
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

## 5. Verify before you conclude

- Cross-check at least one claim two ways (e.g. a pin's net from `altium_get_component` and the
  same pin in `altium_get_net`).
- Counts: `total` in list results is the authoritative count for the filter; do not count
  returned items when `returned < total`.
- Compile violations reported by Altium are evidence; your own inferences are hypotheses. Label them.
- When the answer depends on the PCB (placement, routing, layers, DRC) say so: **PCB data is not
  exposed yet** in this MCP version; do not infer PCB facts from the schematic model.

## 6. When you may not say "analysis complete"

Do not declare a project fully analysed if any of these hold:

- the project was not compiled (`isCompiled=false`) or you did not read `violations`;
- you paged through fewer items than `total` for a list you are drawing conclusions from;
- the question touches PCB, library health, variants or Workspace/server state (not available yet);
- any tool call returned an error you did not resolve.

Say what you covered, what you did not, and which tool/data would be needed.

## 7. Output style for the user

Lead with the answer, then the evidence (designators, net names, counts) so the engineer can
check it in Altium. Keep raw JSON out of the reply unless asked.
