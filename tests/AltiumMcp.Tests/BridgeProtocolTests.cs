using System.Text.Json;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using Xunit;

namespace AltiumMcp.Tests;

public class BridgeProtocolTests
{
    [Fact]
    public void Request_roundtrips_with_camelCase_and_params()
    {
        var req = new BridgeRequest
        {
            Method = BridgeMethods.ProjectListComponents,
            Id = "abc",
            Params = BridgeJson.ToElement(new ListComponentsParams { Filter = "R", Limit = 5 }),
        };

        string json = BridgeJson.Serialize(req);
        Assert.Contains("\"method\":\"project.listComponents\"", json);
        Assert.Contains("\"filter\":\"R\"", json);

        BridgeRequest? back = BridgeJson.Deserialize<BridgeRequest>(json);
        Assert.NotNull(back);
        Assert.Equal("abc", back!.Id);
        ListComponentsParams? p = BridgeJson.Deserialize<ListComponentsParams>(back.Params!.Value);
        Assert.Equal("R", p!.Filter);
        Assert.Equal(5, p.Limit);
    }

    [Fact]
    public void Failure_response_carries_error_code_and_omits_result()
    {
        var res = BridgeResponse.Failure("1", new BridgeError(BridgeErrorCodes.NotCompiled, "compile first"), 12);
        string json = BridgeJson.Serialize(res);
        // Default-valued members are omitted on the wire; ok=false must round-trip via the default.
        Assert.DoesNotContain("\"ok\"", json);
        Assert.Contains("\"code\":\"NOT_COMPILED\"", json);
        Assert.DoesNotContain("\"result\"", json);

        BridgeResponse? back = BridgeJson.Deserialize<BridgeResponse>(json);
        Assert.False(back!.Ok);
        Assert.Equal(BridgeErrorCodes.NotCompiled, back.Error!.Code);
        Assert.Equal(12, back.ElapsedMs);
    }

    [Fact]
    public void Success_response_result_is_a_json_element()
    {
        var payload = new PingResult { Status = "ok", ProcessId = 42 };
        var res = BridgeResponse.Success(null, BridgeJson.ToElement(payload), 1);
        BridgeResponse? back = BridgeJson.Deserialize<BridgeResponse>(BridgeJson.Serialize(res));
        Assert.True(back!.Ok);
        Assert.Equal(JsonValueKind.Object, back.Result!.Value.ValueKind);
        Assert.Equal(42, back.Result.Value.GetProperty("processId").GetInt32());
    }

    [Fact]
    public void All_method_names_are_unique_and_domain_prefixed()
    {
        Assert.Equal(BridgeMethods.All.Count, new System.Collections.Generic.HashSet<string>(BridgeMethods.All).Count);
        Assert.All(BridgeMethods.All, m => Assert.Matches("^(system|workspace|project|sch|pcb)\\.[a-zA-Z]+$", m));
    }
}
