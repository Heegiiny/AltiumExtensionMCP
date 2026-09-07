using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Server;
using Xunit;

namespace AltiumMcp.Tests;

public class BridgeClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
            return await _respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Call_posts_request_envelope_and_returns_result()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json("{\"ok\":true,\"result\":{\"status\":\"ok\",\"processId\":7},\"elapsedMs\":3}")));
        var client = new BridgeClient(handler, readEnv: () => "http://127.0.0.1:5555", readFile: _ => null);

        JsonElement result = await client.CallAsync(BridgeMethods.SystemPing);

        Assert.Equal("http://127.0.0.1:5555/rpc", handler.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"method\":\"system.ping\"", handler.LastBody);
        Assert.Equal(7, result.GetProperty("processId").GetInt32());
    }

    [Fact]
    public async Task Domain_error_becomes_BridgeCallException_with_code()
    {
        var handler = new FakeHandler(_ => Task.FromResult(Json("{\"ok\":false,\"error\":{\"code\":\"NO_ACTIVE_PROJECT\",\"message\":\"none\"},\"elapsedMs\":1}")));
        var client = new BridgeClient(handler, readEnv: () => "http://127.0.0.1:5555/", readFile: _ => null);

        var ex = await Assert.ThrowsAsync<BridgeCallException>(() => client.CallAsync(BridgeMethods.WorkspaceGetInfo));
        Assert.Equal(BridgeErrorCodes.NoActiveProject, ex.Error.Code);
        Assert.Equal(BridgeMethods.WorkspaceGetInfo, ex.Method);
    }

    [Fact]
    public async Task Connection_failure_becomes_BridgeUnavailableException_with_hints()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new BridgeClient(handler, readEnv: () => null, readFile: _ => null);

        var ex = await Assert.ThrowsAsync<BridgeUnavailableException>(() => client.CallAsync(BridgeMethods.SystemPing));
        Assert.Equal("defaultPort", ex.Diagnostics.ResolvedFrom);
        Assert.Equal(BridgeDiscovery.BaseUrlFor(BridgeDiscovery.DefaultPort), ex.Diagnostics.ResolvedBaseUrl);
        Assert.NotEmpty(ex.Diagnostics.Hints);
        Assert.Contains("connection refused", ex.Diagnostics.LastError);
    }

    [Fact]
    public void Locate_prefers_env_over_discovery_file_and_reads_descriptor()
    {
        string descriptor = BridgeJson.Serialize(new BridgeDescriptor { BaseUrl = "http://127.0.0.1:47121/", Port = 47121, ProcessId = int.MaxValue });
        var client = new BridgeClient(new FakeHandler(_ => Task.FromResult(Json("{}"))), readEnv: () => "http://127.0.0.1:9000", readFile: _ => descriptor);

        BridgeDiagnostics diag = client.Locate();

        Assert.Equal("http://127.0.0.1:9000/", diag.ResolvedBaseUrl);
        Assert.StartsWith("env:", diag.ResolvedFrom);
        Assert.True(diag.DiscoveryFileExists);
        Assert.Equal(47121, diag.Descriptor!.Port);
        Assert.False(diag.DescriptorProcessAlive);
    }

    [Fact]
    public void Locate_falls_back_to_discovery_file()
    {
        string descriptor = BridgeJson.Serialize(new BridgeDescriptor { BaseUrl = "http://127.0.0.1:47121/", Port = 47121, ProcessId = 1 });
        var client = new BridgeClient(new FakeHandler(_ => Task.FromResult(Json("{}"))), readEnv: () => null, readFile: _ => descriptor);

        BridgeDiagnostics diag = client.Locate();

        Assert.Equal("http://127.0.0.1:47121/", diag.ResolvedBaseUrl);
        Assert.Equal("discoveryFile", diag.ResolvedFrom);
    }
}
