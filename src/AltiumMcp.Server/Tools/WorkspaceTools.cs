using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
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

    [McpServerTool(Name = "altium_open_document", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, Title = "Open document in editor")]
    [Description("Opens a schematic sheet, PCB or other document in its Altium editor (or brings an already open one forward). Changes editor state only — never modifies design data. Use it so that sch.*/pcb.* tools can default to the active document and before capturing images. Returns the document ref plus wasAlreadyOpen/isOpenInEditor/isFocused.")]
    public Task<CallToolResult> OpenDocument(
        [Description("Full path of the document (from project.getStructure / altium_list_open_documents).")] string documentPath,
        [Description("Focus the editor tab (default true). false shows the document without stealing focus.")] bool focus = true,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.WorkspaceOpenDocument,
            new OpenDocumentParams { DocumentPath = documentPath, Focus = focus }, ct));
}
