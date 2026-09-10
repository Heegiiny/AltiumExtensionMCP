using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AltiumMcp.Server.Tools;

/// <summary>Toolset "connection": prove the link to Altium and describe the host. Always call altium_ping first.</summary>
[McpServerToolType]
public sealed class ConnectionTools
{
    private readonly BridgeClient _bridge;

    public ConnectionTools(BridgeClient bridge)
    {
        _bridge = bridge;
    }

    [McpServerTool(Name = "altium_ping", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Ping Altium bridge")]
    [Description("Checks that the bridge inside the running Altium Designer answers. Returns bridge version, Altium pid, uptime. On failure returns BRIDGE_UNAVAILABLE with diagnostics and hints (is Altium running, is the extension loaded, how to load it). Call this first in a session.")]
    public Task<CallToolResult> Ping(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SystemPing, null, ct));

    [McpServerTool(Name = "altium_get_environment", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get Altium environment")]
    [Description("Describes the Altium Designer host: product name/version, platform (DXP) version, executable path, .NET runtime, loaded server modules (editors) and licensed technology sets.")]
    public Task<CallToolResult> GetEnvironment(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SystemGetEnvironment, null, ct));

    [McpServerTool(Name = "altium_get_settings", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Get bridge settings")]
    [Description("Returns the persistent bridge settings: followMcpQueries ('Follow MCP queries in Altium' — when true, component/net lookups cross-probe: the sheet/board is opened and the object selected and zoomed to) and where settings are stored. The same checkbox is on the MCP Bridge panel in Altium.")]
    public Task<CallToolResult> GetSettings(CancellationToken ct) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SystemGetSettings, null, ct));

    [McpServerTool(Name = "altium_set_settings", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, Title = "Set bridge settings")]
    [Description("Changes persistent bridge settings. followMcpQueries=true turns on cross probing for every later lookup (editor state only — documents are never modified); per-call 'crossProbe' arguments still override it. Only change this when the user asks to follow along in Altium.")]
    public Task<CallToolResult> SetSettings(
        [Description("New value of 'Follow MCP queries in Altium'.")] bool? followMcpQueries = null,
        CancellationToken ct = default) =>
        ToolResults.Run(() => _bridge.CallAsync(BridgeMethods.SystemSetSettings, new AltiumMcp.Contracts.Model.SetSettingsParams { FollowMcpQueries = followMcpQueries }, ct));

    [McpServerTool(Name = "altium_bridge_diagnostics", ReadOnly = true, Idempotent = true, OpenWorld = false, Title = "Bridge diagnostics")]
    [Description("Local diagnostics without calling Altium: how the bridge URL was resolved (env/discovery file/default), discovery descriptor, whether the Altium process it points to is alive, and running X2.EXE processes. Use when altium_ping fails.")]
    public CallToolResult Diagnostics() => ToolResults.Json(_bridge.Locate());
}
