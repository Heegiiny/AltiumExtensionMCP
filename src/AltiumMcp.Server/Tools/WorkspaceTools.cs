using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "workspace": what is open in Altium right now.</summary>
[McpServerToolType]
public sealed class WorkspaceTools
{
    private readonly BridgeClient _bridge;

    public WorkspaceTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    [McpServerTool(Name = "altium_get_workspace", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get workspace state")]
    [Description("Returns the current Altium workspace (project group): workspace file, project count, focused project (path = stable id), focused document and the document shown in the active editor view. Cheap; call after altium_ping to orient yourself.")]
    public Task<CallToolResult> GetWorkspace(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.WorkspaceGetInfo, null, ct));

    [McpServerTool(Name = "altium_list_projects", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List open projects")]
    [Description("Lists all projects open in the workspace with kind (PrjPcb, PrjScr, LibPkg, FreeDocuments...), document counts, variant count, compile state and managed-project GUID. Use the returned 'path' as projectPath in project tools.")]
    public Task<CallToolResult> ListProjects(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.WorkspaceListProjects, null, ct));

    [McpServerTool(Name = "altium_list_open_documents", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "List open documents")]
    [Description("Lists documents currently open in Altium editors (schematic sheets, PCBs, libraries, text...), with kind, modified flag, owning project path and which one is focused.")]
    public Task<CallToolResult> ListOpenDocuments(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.WorkspaceListOpenDocuments, null, ct));
}
