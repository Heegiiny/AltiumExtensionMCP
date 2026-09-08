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
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchListObjects,
            new ListSheetObjectsParams { DocumentPath = documentPath, Types = types, Filter = filter, Offset = offset, Limit = limit, LoadIfClosed = loadIfClosed }, ct));

    [McpServerTool(Name = "altium_get_sheet_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get schematic component (sheet level)")]
    [Description("Returns one component as placed on a schematic sheet: designator (logical + physical), comment, description, library reference, design item id, managed-content GUIDs, position/rotation/mirroring in mils, part count, all parameters with visibility flags, every pin with number/name/electrical type/part id/position/hidden-net, and model implementations. Use altium_get_component (compiled model) instead when you need the nets a pin connects to.")]
    public Task<CallToolResult> GetComponent(
        [Description("Designator as drawn on the sheet (e.g. 'U1', 'U1A' for multi-part) or the sheet UniqueId (from altium_list_sheet_objects).")] string component,
        [Description("Full path of the .SchDoc. Omit for the active editor sheet.")] string? documentPath = null,
        [Description("Load the sheet hidden if it is not open (default true).")] bool loadIfClosed = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SchGetComponent,
            new GetSchComponentParams { DocumentPath = documentPath, Component = component, LoadIfClosed = loadIfClosed }, ct));
}
