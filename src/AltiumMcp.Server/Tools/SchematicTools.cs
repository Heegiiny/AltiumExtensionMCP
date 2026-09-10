using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "sch": per-sheet schematic object model with geometry (SCH editor), complementary to the compiled project model.</summary>
[McpServerToolType]
public sealed class SchematicTools
{
    private readonly BridgeClient _bridge;

    public SchematicTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    [McpServerTool(Name = "altium_get_sheet", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get schematic sheet info")]
    [Description("Returns a schematic sheet's summary: size in mils, sheet style, unit system, template, sheet-level parameters (Title, SheetNumber, ...) and object counts by type (Component, Wire, NetLabel, Port, PowerObject, SheetSymbol, ...). Closed sheets are loaded hidden on demand (no editor tab). Use before altium_list_sheet_objects to decide which types to request.")]
    public Task<CallToolResult> GetSheet(
        [Description("Full path of the .SchDoc (from altium_get_project_structure). Omit for the sheet currently active in the editor.")] string? documentPath = null,
        [Description("Load the sheet hidden if it is not open (default true). False returns DOCUMENT_NOT_OPEN instead.")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchGetSheet,
            new SheetQueryParams { DocumentPath = documentPath, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_list_sheet_objects", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List schematic sheet objects")]
    [Description("Lists objects drawn on one schematic sheet with positions in mils (origin bottom-left): id (sheet UniqueId), type, text (designator / net name / label text), x, y, rotation, bounds, polyline vertices for wires/buses, and type-specific attributes (component: comment, libReference, description; port: ioType, style; power object: style; sheet symbol: fileName). Default types are the electrical objects (Component, Wire, Bus, BusEntry, Junction, NetLabel, Port, PowerObject, SheetSymbol, SheetEntry, NoERC, harness objects, CrossSheetConnector, Blanket, CompileMask); pass 'types' to narrow or to add graphics (Label, TextFrame, Note, Line, Rectangle, ...) or component children (Pin, Parameter, Designator, Implementation — returned with 'owner' = component designator). The compiled component id from altium_list_components ends with the sheet component id, which links both models. Paginated; absent fields mean false/0/none.")]
    public Task<CallToolResult> ListObjects(
        [Description("Full path of the .SchDoc. Omit for the active editor sheet.")] string? documentPath = null,
        [Description("Object types to include, e.g. [\"Component\",\"NetLabel\"]. Omit for the electrical default set.")] List<string>? types = null,
        [Description("Case-insensitive filter on text/owner/comment/libReference/name/value: substring or wildcard ('U*', 'GND').")] string? filter = null,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items (default 200, max 2000). Wires with many vertices are verbose; page through.")] int limit = 200,
        [Description("Load the sheet hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection on the items (camelCase names, e.g. [\"id\",\"type\",\"text\"]); saves tokens.")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchListObjects,
            new ListSheetObjectsParams { DocumentPath = documentPath, Types = types, Filter = filter, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_get_sheet_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get schematic component (sheet level)")]
    [Description("Returns one component as placed on a schematic sheet: designator (logical + physical), compiledIds (the project.* ids of its compiled instances), comment, description, library reference, position/rotation in mils, part count and (detail=connectivity, default) pins with number/name/electrical type/hidden net. detail=full adds all parameters with visibility flags, model implementations, managed-content GUIDs, design item id, bounds and per-pin geometry. Use altium_get_component (compiled model) when you need the nets a pin connects to. documentPath may be omitted when the focused project has one sheet or the active editor sheet belongs to it; otherwise pass the .SchDoc path or bare file name.")]
    public Task<CallToolResult> GetComponent(
        [Description("Designator as drawn on the sheet (e.g. 'U2'; 'U2A' also works for multi-part symbols), the sheet UniqueId (from altium_list_sheet_objects), or — when the project is compiled — the physical designator / compiled id from altium_list_components.")] string component,
        [Description("Full path (or bare file name) of the .SchDoc. Omit for the project's sheet / active editor sheet.")] string? documentPath = null,
        [Description("'summary' | 'connectivity' (default) | 'full'.")] string? detail = null,
        [Description("Cross probe override: true = open the sheet, select and zoom to the symbol even if 'Follow MCP queries' is off; false = never; omit = follow the setting.")] bool? crossProbe = null,
        [Description("Load the sheet hidden if it is not open (default true).")] bool loadIfClosed = true,
        [Description("Optional field projection (camelCase names).")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchGetComponent,
            new GetSchComponentParams { DocumentPath = documentPath, Component = component, Detail = detail, CrossProbe = crossProbe, LoadIfClosed = loadIfClosed }, ct), fields);

    [McpServerTool(Name = "altium_select_on_sheet", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, Title = "Select objects in the schematic editor")]
    [Description("Highlights objects on a schematic sheet so the engineer sees what you refer to: components by designator ('U2' = all parts, 'U2A' = one part, physical 'U2_1', or sheet UniqueId), nets by name (selects the net labels, ports, power objects, sheet entries and cross-sheet connectors carrying that name — wires are not net-aware on the sheet), and any objects by UniqueId from altium_list_sheet_objects. Opens/focuses the sheet, clears the previous selection (unless clearFirst=false) and zooms to the selection. Returns matched/unmatched targets, selected count and bounds in mils. Editor state only — design data is not modified. Call with no targets to clear the selection.")]
    public Task<CallToolResult> Select(
        [Description("Full path of the .SchDoc. Omit for the active editor sheet.")] string? documentPath = null,
        [Description("Component designators, e.g. [\"U2\",\"R7\"] (or 'U2A', physical designator, UniqueId).")] List<string>? components = null,
        [Description("Net names, e.g. [\"SPI_CLK\",\"GND\"].")] List<string>? nets = null,
        [Description("Object UniqueIds from altium_list_sheet_objects.")] List<string>? objects = null,
        [Description("Deselect everything first (default true).")] bool clearFirst = true,
        [Description("Zoom the view to the selection (default true).")] bool zoomTo = true,
        [Description("Open/focus the sheet in the editor (default true).")] bool focus = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchSelect,
            new SelectParams { DocumentPath = documentPath, Components = components, Nets = nets, Objects = objects, ClearFirst = clearFirst, ZoomTo = zoomTo, Focus = focus }, ct));
}
