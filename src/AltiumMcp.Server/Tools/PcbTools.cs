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
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListComponents,
            new ListPcbComponentsParams { DocumentPath = documentPath, Filter = filter, Layer = layer, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_get_pcb_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get PCB component details")]
    [Description("Returns one placed footprint in detail: summary (position, rotation, layer, bounds), footprint description, source libraries and design item id, default 3D model, managed-content GUIDs, swap flags, and every pad with name, net, x/y (mils), rotation, layer, SMD flag, shape, size, hole and plating, plus counts of other primitives in the footprint (tracks, regions, component bodies...). Identify by designator or PCB UniqueId (also accepts the schematic sourceUniqueId).")]
    public Task<CallToolResult> GetComponent(
        [Description("Designator (e.g. 'U1'), PCB UniqueId or schematic source UniqueId.")] string component,
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbGetComponent,
            new GetPcbComponentParams { DocumentPath = documentPath, Component = component, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_list_pcb_nets", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List PCB nets")]
    [Description("Lists nets on the board sorted by name, paginated: pin count, via count, routed length (mils), differential-pair membership and the editor's 'connectively invalid' flag (unrouted/broken). Filter by substring or wildcard ('VCC*'). Compare with altium_list_nets (schematic/compiled) to find nets missing on the PCB.")]
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
    [Description("Returns one net with all its pads (pinDescriptor 'U1-3', position, layer, SMD/hole), track/arc/polygon/region counts, layers used for routing and total track length in mils computed from geometry. Use for connectivity and routing analysis of a specific net.")]
    public Task<CallToolResult> GetNet(
        [Description("Exact net name (from altium_list_pcb_nets).")] string net,
        [Description("Full path of the .PcbDoc. Omit for the active PCB document.")] string? documentPath = null,
        [Description("Load the board hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbGetNet,
            new GetPcbNetParams { DocumentPath = documentPath, Net = net, LoadIfClosed = loadIfClosed }, ct));

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
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.PcbListPrimitives,
            new ListPcbPrimitivesParams { DocumentPath = documentPath, Types = types, Layer = layer, Net = net, FreeOnly = freeOnly, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct));
}
