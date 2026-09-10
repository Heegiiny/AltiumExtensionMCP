using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "pcb": PCB board object model (PCB editor) — layers, components with placement, nets with routing, rules, primitives.</summary>
[McpServerToolType]
public sealed class PcbTools
{
    private readonly BridgeClient _bridge;

    public PcbTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    [McpServerTool(Name = "altium_get_board", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get PCB board info")]
    [Description("Returns a PCB document summary: board outline bounds and size (mils, relative to board origin), display unit, layer stack (name, id, kind Signal/Plane/Dielectric/Other, copper thickness, used), signal layer count, primitive counts by type (Component, Pad, Via, Track, Arc, Polygon, Region, Text, Rule, Violation...), object classes (net/component classes with member counts), rule count and current DRC violation count. Closed boards are loaded hidden on demand. Start here before other pcb tools.")]
    public Task<CallToolResult> GetBoard(
        [Description("Full path of the .PcbDoc (from altium_get_project_structure). Omit for the active PCB editor document.")] string? documentPath = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbGetBoard, new BoardQueryParams { DocumentPath = documentPath, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_list_pcb_components", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List PCB components")]
    [Description("Lists placed footprints sorted by designator, paginated: id (PCB UniqueId), designator, sourceUniqueId (= compiled schematic component id, links to altium_list_components / altium_get_component), footprint, comment, layer (Top/Bottom), x/y/rotation (mils, degrees), height, bounds, pad count, source library reference/description/hierarchical path, locked and DRC-error flags. Filter by substring/wildcard on designator/footprint/comment/libref and optionally by side.")]
    public Task<CallToolResult> ListComponents(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Case-insensitive filter: substring ('0603') or wildcard ('C*', 'U1?').")] string? filter = null,
        [Description("'Top' or 'Bottom' to restrict to one side.")] string? layer = null,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items (default 100, max 2000).")] int limit = 100,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection on the items (camelCase names, e.g. [\"designator\",\"footprint\",\"layer\",\"x\",\"y\"]); saves tokens.")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListComponents,
            new ListPcbComponentsParams { DocumentPath = documentPath, Filter = filter, Layer = layer, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_get_pcb_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get PCB component details")]
    [Description("Returns one placed footprint: summary (designator, ids, footprint, comment, layer, position, rotation) and (detail=connectivity, default) pads with name and net. detail=full adds bounds, footprint description, source libraries and design item id, default 3D model, managed-content GUIDs, swap flags, per-pad geometry (x/y, layer, shape, size, hole, plating) and counts of other primitives in the footprint. Identify by designator, PCB UniqueId or schematic sourceUniqueId. documentPath may be omitted: the focused project's PCB is used.")]
    public Task<CallToolResult> GetComponent(
        [Description("Designator (e.g. 'U1'), PCB UniqueId or schematic source UniqueId.")] string component,
        [Description("Full path (or bare file name) of the .PcbDoc. Omit for the focused project's board.")] string? documentPath = null,
        [Description("'summary' | 'connectivity' (default) | 'full'.")] string? detail = null,
        [Description("Cross probe override: true = open the board, select and zoom to the footprint even if 'Follow MCP queries' is off; false = never; omit = follow the setting.")] bool? crossProbe = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection (camelCase names).")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbGetComponent,
            new GetPcbComponentParams { DocumentPath = documentPath, Component = component, Detail = detail, CrossProbe = crossProbe, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_list_pcb_nets", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List PCB nets")]
    [Description("Lists nets on the board sorted by name, paginated: pin count, via count, routed length (mils), differential-pair membership and unroutedConnectionCount (remaining ratsnest lines; absent = fully routed). Filter by substring or wildcard ('VCC*'). Compare with altium_list_nets (schematic/compiled) to find nets missing on the PCB.")]
    public Task<CallToolResult> ListNets(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Case-insensitive filter on net name: substring or wildcard.")] string? filter = null,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items (default 100, max 2000).")] int limit = 100,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListNets,
            new ListPcbNetsParams { DocumentPath = documentPath, Filter = filter, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_get_pcb_net", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get PCB net details")]
    [Description("Returns one board net: pads (pinDescriptor 'U1-3' + net; detail=full adds position, layer, SMD/hole), track/arc/polygon/region counts, layers used for routing and total track length in mils computed from geometry. Use for connectivity and routing analysis of a specific net on the PCB; for the schematic-side net across sheets use altium_get_net.")]
    public Task<CallToolResult> GetNet(
        [Description("Exact net name (from altium_list_pcb_nets).")] string net,
        [Description("Full path (or bare file name) of the .PcbDoc. Omit for the focused project's board.")] string? documentPath = null,
        [Description("'summary' | 'connectivity' (default) | 'full'.")] string? detail = null,
        [Description("Cross probe override: true = open the board and select the net's copper even if 'Follow MCP queries' is off; false = never; omit = follow the setting.")] bool? crossProbe = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection (camelCase names).")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbGetNet,
            new GetPcbNetParams { DocumentPath = documentPath, Net = net, Detail = detail, CrossProbe = crossProbe, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_list_pcb_rules", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List PCB design rules")]
    [Description("Lists design rules sorted by kind then priority: name, kind (Clearance, MaxMinWidth, RoutingViaStyle, SolderMaskExpansion, ComponentClearance, ...), enabled flag, priority, scope expressions (e.g. 'All', 'InNetClass(\\'Power\\')'), the constraint summary string as shown in the rule editor and comment. Filter by kind or name (substring/wildcard).")]
    public Task<CallToolResult> ListRules(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Filter on rule kind or name: substring ('Width') or wildcard ('Routing*').")] string? filter = null,
        [Description("Include rules that are disabled for DRC (default true).")] bool includeDisabled = true,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListRules,
            new ListPcbRulesParams { DocumentPath = documentPath, Filter = filter, IncludeDisabled = includeDisabled, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_list_pcb_primitives", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List PCB primitives")]
    [Description("Lists low-level PCB primitives with geometry in mils (relative to board origin), paginated: type, layer, net, owning component, geometry (track [x1,y1,x2,y2]; via/pad/text/fill [x,y]; arc [cx,cy,r,startDeg,endDeg]; polygon/region bounds), width, size, hole, text, detail (via span, polygon type/poured) and DRC-error flag. Default types: Track, Arc, Via, Polygon, Region, Fill, Text; also Pad, ComponentBody, Dimension, Coordinate, Violation (DRC markers with descriptions). Always narrow by layer and/or net and page with small limits — boards have tens of thousands of primitives.")]
    public Task<CallToolResult> ListPrimitives(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Types to include, e.g. [\"Via\",\"Track\"] or [\"Violation\"]. Omit for the default set.")] List<string>? types = null,
        [Description("Restrict to one layer by name or id ('Top Layer', 'TopLayer', 'Bottom Overlay').")] string? layer = null,
        [Description("Restrict to primitives of this exact net name.")] string? net = null,
        [Description("Only free primitives (not part of a footprint), default false.")] bool freeOnly = false,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items (default 200, max 2000).")] int limit = 200,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection on the items (camelCase names, e.g. [\"type\",\"layer\",\"net\",\"geometry\"]); saves tokens.")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListPrimitives,
            new ListPcbPrimitivesParams { DocumentPath = documentPath, Types = types, Layer = layer, Net = net, FreeOnly = freeOnly, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_list_drc_violations", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List DRC violations")]
    [Description("Reads the design-rule violations currently stored on the board (from the last batch or online DRC) without running a check: total count, counts by rule and by kind, and a paginated list with rule name, kind, Altium's description, layer, marker bounds (mils), the two offending primitives and the net. Zero violations may mean 'clean' or 'DRC not run yet' — use altium_run_drc to be sure.")]
    public Task<CallToolResult> ListViolations(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Filter on rule name, kind or description: substring or wildcard ('Clearance*').")] string? filter = null,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items (default 200, max 2000).")] int limit = 200,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListViolations,
            new ListViolationsParams { DocumentPath = documentPath, Filter = filter, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_run_drc", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, Title = "Run batch DRC")]
    [Description("Runs Altium's batch Design Rule Check on the board and returns the result as structured data: run status, report file path, duration, total violation count, counts by rule and by kind, and the first N violations (rule, kind, description, layer, bounds, primitives, net). This refreshes the violation markers on the board (editor state) and writes a report file; copper and geometry are not modified. May take seconds to minutes on large boards.")]
    public Task<CallToolResult> RunDrc(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Max violations returned inline (default 200, max 2000); counts are always complete.")] int limit = 200,
        [Description("Report file path (.txt or .html). Default: %LOCALAPPDATA%\\AltiumMcp\\reports\\{board}-DRC-{stamp}.txt")] string? reportPath = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbRunDrc,
            new RunDrcParams { DocumentPath = documentPath, Limit = limit, ReportPath = reportPath, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_select_on_pcb", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, Title = "Select objects in the PCB editor")]
    [Description("Highlights objects in the PCB editor so the engineer sees what you refer to: components by designator, whole nets (pads, tracks, arcs, vias, polygons, regions) by name, and pads by descriptor 'U1-3'. Opens/focuses the board, clears the previous selection (unless clearFirst=false) and zooms to the selection. Returns matched/unmatched targets, selected object count and the selection bounds in mils. Editor state only — design data is not modified. Call with no targets to clear the selection.")]
    public Task<CallToolResult> Select(
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Component designators, e.g. [\"U1\",\"C12\"].")] List<string>? components = null,
        [Description("Net names, e.g. [\"GND\",\"VCC_3V3\"].")] List<string>? nets = null,
        [Description("Pad descriptors, e.g. [\"U1-3\",\"J2-1\"].")] List<string>? pads = null,
        [Description("Deselect everything first (default true).")] bool clearFirst = true,
        [Description("Zoom the view to the selection (default true).")] bool zoomTo = true,
        [Description("Open/focus the board in the editor (default true).")] bool focus = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbSelect,
            new SelectParams { DocumentPath = documentPath, Components = components, Nets = nets, Objects = pads, ClearFirst = clearFirst, ZoomTo = zoomTo, Focus = focus }, ct));
}
