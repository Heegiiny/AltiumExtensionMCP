using System.Text.Json;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Server;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AltiumMcp.Tests;

public class ToolResultsTests
{
    [Fact]
    public async Task Run_returns_raw_json_on_success()
    {
        CallToolResult r = await ToolResults.Run(() => Task.FromResult(JsonDocument.Parse("{\"a\":1}").RootElement.Clone()));
        Assert.NotEqual(true, r.IsError);
        Assert.Equal("{\"a\":1}", ((TextContentBlock)r.Content[0]).Text);
    }

    [Fact]
    public async Task Run_maps_domain_error_to_isError_with_code()
    {
        CallToolResult r = await ToolResults.Run(() => throw new BridgeCallException("m", new BridgeError(BridgeErrorCodes.NotCompiled, "x")));
        Assert.True(r.IsError);
        string text = ((TextContentBlock)r.Content[0]).Text;
        Assert.Contains("\"code\":\"NOT_COMPILED\"", text);
    }

    [Fact]
    public async Task Run_maps_unavailable_bridge_to_diagnostics()
    {
        var diag = new BridgeDiagnostics { ResolvedFrom = "defaultPort" };
        diag.Hints.Add("start altium");
        CallToolResult r = await ToolResults.Run(() => throw new BridgeUnavailableException("down", diag));
        Assert.True(r.IsError);
        string text = ((TextContentBlock)r.Content[0]).Text;
        Assert.Contains("BRIDGE_UNAVAILABLE", text);
        Assert.Contains("start altium", text);
    }
}
