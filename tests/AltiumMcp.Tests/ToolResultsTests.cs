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
    public async Task Run_error_carries_correlation_id()
    {
        var err = new BridgeError(BridgeErrorCodes.AltiumApiError, "boom") { CorrelationId = "abcd1234" };
        CallToolResult r = await ToolResults.Run(() => throw new BridgeCallException("m", err));
        Assert.True(r.IsError);
        string text = ((TextContentBlock)r.Content[0]).Text;
        Assert.Contains("\"code\":\"ALTIUM_API_ERROR\"", text);
        Assert.Contains("\"correlationId\":\"abcd1234\"", text);
    }

    [Fact]
    public async Task Run_applies_field_projection()
    {
        CallToolResult r = await ToolResults.Run(
            () => Task.FromResult(JsonDocument.Parse("{\"total\":1,\"nets\":[{\"netId\":\"GND\",\"name\":\"GND\",\"pinCount\":9}]}").RootElement.Clone()),
            new[] { "netId" });
        Assert.Equal("{\"total\":1,\"nets\":[{\"netId\":\"GND\"}]}", ((TextContentBlock)r.Content[0]).Text);
    }

    [Fact]
    public async Task Run_rejects_oversized_results_with_hints()
    {
        string big = "{\"items\":[" + string.Join(",", System.Linq.Enumerable.Repeat("{\"x\":\"" + new string('a', 1000) + "\"}", ToolResults.DefaultMaxResultChars / 1000 + 5)) + "]}";
        CallToolResult r = await ToolResults.Run(() => Task.FromResult(JsonDocument.Parse(big).RootElement.Clone()));
        Assert.True(r.IsError);
        string text = ((TextContentBlock)r.Content[0]).Text;
        Assert.Contains("RESULT_TOO_LARGE", text);
        Assert.Contains("fields", text);
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
