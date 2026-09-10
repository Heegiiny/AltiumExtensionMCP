using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "project": compiled-project data (components, nets, hierarchy, connectivity) from the Workspace Manager model.</summary>
[McpServerToolType]
public sealed class ProjectTools
{
    private const string DetailDoc = "Detail level: 'summary' (identity only), 'connectivity' (default: + pins→nets / per-sheet net segments), 'full' (+ parameters, models, source library, electrical types).";
    private const string FieldsDoc = "Optional field projection: only these properties are returned (camelCase; applies to list items, or to the object itself; 'a.b' keeps a reduced nested object). Saves tokens.";
    private const string CrossProbeDoc = "Cross probe override: true = select and zoom to the object in Altium even if 'Follow MCP queries' is off; false = never; omit = follow the setting (altium_get_settings). Editor state only.";

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
        [Description(FieldsDoc)] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetStructure,
            new ProjectQueryParams { ProjectPath = projectPath, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_get_project_agent_docs", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get project AGENTS.md / docs")]
    [Description("Finds agent instructions shipped with the design: AGENTS.md in the project directory and every parent directory (nearest first), plus README.md / CLAUDE.md / .cursorrules next to the project. Returns their content (bounded) and whether each file is a member of the Altium project. Call this at the start of a session and FOLLOW the instructions found. Read-only: it never creates files.")]
    public Task<CallToolResult> GetAgentDocs(
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Max characters returned per file (default 20000).")] int maxCharsPerFile = 20000,
        [Description("Also include README.md / CLAUDE.md / .cursorrules from the project directory (default true).")] bool includeReadme = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetAgentDocs,
            new GetAgentDocsParams { ProjectPath = projectPath, MaxCharsPerFile = maxCharsPerFile, IncludeReadme = includeReadme }, ct));

    [McpServerTool(Name = "altium_list_components", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List components")]
    [Description("Lists components of the compiled project (flattened across all sheets) sorted by designator, paginated. Each item has a stable 'id' (schematic UniqueId), designator, comment, description, library reference, footprint, owning sheet, pin count. Use 'filter' to narrow (substring or wildcard on designator/comment/libref/footprint/description), 'fields' to keep only what you read, and keep 'limit' small. Absent boolean/number fields mean false/0. Requires a compiled project (NOT_COMPILED error otherwise).")]
    public Task<CallToolResult> ListComponents(
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Case-insensitive filter. Plain text = substring ('LM358', '0603'); with * or ? = wildcard on the whole field ('R*' = all resistors, 'U1?' = U10..U19).")] string? filter = null,
        [Description("Include component parameters (Value, Manufacturer, ...). Increases size; default false.")] bool includeParameters = false,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items to return (default 100, max 2000).")] int limit = 100,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc + " Example: [\"designator\",\"comment\",\"libraryReference\"].")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectListComponents,
            new ListComponentsParams { ProjectPath = projectPath, Filter = filter, IncludeParameters = includeParameters, Offset = offset, Limit = limit, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_get_component", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get component details")]
    [Description("Returns one compiled component: summary and (detail=connectivity, default) pins with number, name, electrical type and connected net; detail=full adds parameters and model implementations (footprint, simulation, 3D). Identify it by UniqueId (preferred, from altium_list_components) or designator. Resolves across the whole project — no sheet needs to be open. For several components at once use altium_get_components.")]
    public Task<CallToolResult> GetComponent(
        [Description("Component UniqueId or designator (e.g. 'U1').")] string component,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description(DetailDoc)] string? detail = null,
        [Description(CrossProbeDoc)] bool? crossProbe = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc)] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetComponent,
            new GetComponentParams { ProjectPath = projectPath, Component = component, Detail = detail, CrossProbe = crossProbe, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_get_components", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get several components")]
    [Description("Batch form of altium_get_component: up to 100 UniqueIds/designators in one call, one scan of the compiled model. Returns components[] (same shape as altium_get_component) and notFound[]. Prefer this over repeated single lookups when inventorying ICs, connectors or regulators.")]
    public Task<CallToolResult> GetComponents(
        [Description("Component UniqueIds or designators, e.g. [\"U1\",\"U2\",\"J3\"].")] string[] components,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description(DetailDoc)] string? detail = null,
        [Description(CrossProbeDoc + " Probes the first component of the batch.")] bool? crossProbe = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc)] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetComponents,
            new GetComponentParams { ProjectPath = projectPath, Components = new List<string>(components), Detail = detail, CrossProbe = crossProbe, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_list_nets", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List nets")]
    [Description("Lists nets of the compiled (flattened) project, paginated and sorted by name. Each net has a stable 'netId', display name, scope, pin/port/label/power/sheet-entry counts and documentPaths = every sheet that carries it. includePins=true adds per-sheet segments (pins, ports, labels, power objects, sheet entries) — larger. Use 'filter' (substring or wildcard like 'VCC*') and 'fields'.")]
    public Task<CallToolResult> ListNets(
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Case-insensitive filter on the net name or id: substring ('USB') or wildcard ('VCC*', '*_N').")] string? filter = null,
        [Description("Include per-sheet segments with pins per net (default false).")] bool includePins = false,
        [Description("Pagination offset (default 0).")] int offset = 0,
        [Description("Max items to return (default 100, max 2000).")] int limit = 100,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc + " Example: [\"netId\",\"name\",\"pinCount\",\"documentPaths\"].")] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectListNets,
            new ListNetsParams { ProjectPath = projectPath, Filter = filter, IncludePins = includePins, Offset = offset, Limit = limit, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_get_net", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get net details")]
    [Description("Returns one compiled project net as a whole, across all sheets: netId, name, scope, counts, documentPaths and (detail=connectivity, default) segments[] — for every sheet the pins (designator.pin), ports, net labels, power objects, sheet entries and off-sheet connectors that carry the net. Identify it by netId (preferred) or name; if the same name denotes several distinct nets (e.g. local nets on different sheets) the tool returns AMBIGUOUS_OBJECT with candidates — pass a netId or documentPath. documentPath only orders the segments (that sheet first); it never hides the other sheets.")]
    public Task<CallToolResult> GetNet(
        [Description("Net id (netId from altium_list_nets) or display name (e.g. 'GND', 'USB_D+').")] string net,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Optional sheet (.SchDoc path or file name): list its segment first and prefer the net present on it when the name is ambiguous.")] string? documentPath = null,
        [Description(DetailDoc)] string? detail = null,
        [Description(CrossProbeDoc)] bool? crossProbe = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc)] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetNet,
            new GetNetParams { ProjectPath = projectPath, Net = net, DocumentPath = documentPath, Detail = detail, CrossProbe = crossProbe, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_get_nets", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get several nets")]
    [Description("Batch form of altium_get_net: up to 100 net ids/names in one call. Returns nets[] (same shape as altium_get_net) and notFound[]. An ambiguous name fails the whole call with AMBIGUOUS_OBJECT — pass netIds for those.")]
    public Task<CallToolResult> GetNets(
        [Description("Net ids or names, e.g. [\"GND\",\"+3V3\",\"SPI_CLK\"].")] string[] nets,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Optional sheet: order segments with that sheet first and use it to disambiguate names.")] string? documentPath = null,
        [Description(DetailDoc)] string? detail = null,
        [Description(CrossProbeDoc + " Probes the first net of the batch.")] bool? crossProbe = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        [Description(FieldsDoc)] string[]? fields = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectGetNets,
            new GetNetParams { ProjectPath = projectPath, Nets = new List<string>(nets), DocumentPath = documentPath, Detail = detail, CrossProbe = crossProbe, CompileIfNeeded = compileIfNeeded }, ct), fields);

    [McpServerTool(Name = "altium_trace_connectivity", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Trace connectivity")]
    [Description("Bounded connectivity walk over the compiled project: start at a component (designator/id) or a net (id/name) and follow pins→nets→components for 'depth' hops (1–3). Returns compact edges: nets[] with level and pins as 'U1.3', components[] with level, comment, library reference, sheet and their pins on traced nets as '3=GND'. Power nets, excluded nets and nets with more than maxFanout pins are listed but not expanded (notExpanded says why), so the answer stays small. Use it to answer 'what is around U5', 'where does this signal go', 'what does this regulator feed'.")]
    public Task<CallToolResult> Trace(
        [Description("Start object: component designator/UniqueId or net id/name.")] string start,
        [Description("'component' | 'net' | omit for auto (component first, then net).")] string? startKind = null,
        [Description("Hops from the start (default 1, max 3).")] int depth = 1,
        [Description("Nets never expanded, by name or id (e.g. [\"GND\",\"+3V3\"]).")] string[]? excludeNets = null,
        [Description("Expand nets that carry power objects (default false).")] bool includePowerNets = false,
        [Description("Nets with more pins than this are listed but not expanded (default 30).")] int maxFanout = 30,
        [Description("Max nets in the result (default 60).")] int maxNets = 60,
        [Description("Max components in the result (default 120).")] int maxComponents = 120,
        [Description("Full path of the project. Omit for the focused project.")] string? projectPath = null,
        [Description("Compile first if the compiled model is missing (default false).")] bool compileIfNeeded = false,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.ProjectTrace,
            new TraceParams
            {
                ProjectPath = projectPath,
                Start = start,
                StartKind = startKind,
                Depth = depth,
                ExcludeNets = excludeNets == null ? null : new List<string>(excludeNets),
                IncludePowerNets = includePowerNets,
                MaxFanout = maxFanout,
                MaxNets = maxNets,
                MaxComponents = maxComponents,
                CompileIfNeeded = compileIfNeeded,
            }, ct));
}
