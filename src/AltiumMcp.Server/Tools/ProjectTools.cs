using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "project": compiled-project data (components, nets, hierarchy) from the Workspace Manager model.</summary>
[McpServerToolType]
public sealed class ProjectTools
{
    private readonly BridgeClient _bridge;

    public ProjectTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    [McpServerTool(Name = "altium_get_project_structure", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get project structure")]
    [Description("Returns a project's structure: summary, source (logical) documents with per-sheet component/net counts, generated documents, compiled sheet hierarchy, variants, project parameters, compile-violation summary and whether the compiled model exists. Start here before listing components or nets.")]
    public Task<CallToolResult> GetStructure(
        [Description("Full path of the project (from altium_list_projects). Omit for the focused project.")] string? projectPath = null,
        [Description("If true and the project has no compiled model, compile it first (modifies project state; default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetStructure,
            new ProjectQueryParams { ProjectPath = projectPath, CompileIfNeeded = compileIfNeeded }, ct));

    [McpServerTool(Name = "altium_list_components", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List components")]
    [Description("Lists components of the compiled project (flattened across all sheets) sorted by designator, paginated. Each item has a stable 'id' (schematic UniqueId), designator, comment, description, library reference, footprint, part type, owning sheet, pin count. Use 'filter' to narrow (substring or wildcard on designator/comment/libref/footprint/description) and keep 'limit' small to save context. Absent boolean/number fields mean false/0. Requires a compiled project (NOT_COMPILED error otherwise).")]
    public Task<CallToolResult> ListComponents(
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Case-insensitive filter. Plain text = substring ('LM358', '0603'); with * or ? = wildcard on the whole field ('R*' = all resistors, 'U1?' = U10..U19).")] string? filter = null,
        [Description("Include component parameters (Value, Manufacturer, ...). Increases size; default false.")] bool includeParameters = false,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items to return (default 100, max 2000).")] int limit = 100,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectListComponents,
            new ListComponentsParams { ProjectPath = projectPath, Filter = filter, IncludeParameters = includeParameters, Offset = offset, Limit = limit, CompileIfNeeded = compileIfNeeded }, ct));

    [McpServerTool(Name = "altium_get_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get component details")]
    [Description("Returns one component in detail: summary, all parameters, pins (number, name, electrical type, connected net, part id) and model implementations (footprint, simulation, 3D). Identify it by UniqueId (preferred, from altium_list_components) or designator.")]
    public Task<CallToolResult> GetComponent(
        [Description("Component UniqueId or designator (e.g. 'U1').")] string component,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetComponent,
            new GetComponentParams { ProjectPath = projectPath, Component = component, CompileIfNeeded = compileIfNeeded }, ct));

    [McpServerTool(Name = "altium_list_nets", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List nets")]
    [Description("Lists nets of the compiled (flattened) project with pin/port/label/power counts, paginated and sorted by name. Set includePins=true to get the designator/pin list per net (larger). Use 'filter' to narrow by name (substring or wildcard like 'VCC*').")]
    public Task<CallToolResult> ListNets(
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Case-insensitive filter on the net name: substring ('USB') or wildcard ('VCC*', '*_N').")] string? filter = null,
        [Description("Include pins per net (default false).")] bool includePins = false,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items to return (default 100, max 2000).")] int limit = 100,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectListNets,
            new ListNetsParams { ProjectPath = projectPath, Filter = filter, IncludePins = includePins, Offset = offset, Limit = limit, CompileIfNeeded = compileIfNeeded }, ct));

    [McpServerTool(Name = "altium_get_net", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get net details")]
    [Description("Returns one net with its full pin list (designator, pin number, pin name, electrical type), counts and flags.")]
    public Task<CallToolResult> GetNet(
        [Description("Net name (from altium_list_nets).")] string net,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetNet,
            new GetNetParams { ProjectPath = projectPath, Net = net, CompileIfNeeded = compileIfNeeded }, ct));
}
