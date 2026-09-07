using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Server;

/// <summary>Thrown when the bridge inside Altium cannot be reached. Carries a full diagnostic report.</summary>
public sealed class BridgeUnavailableException : Exception
{
    public BridgeDiagnostics Diagnostics { get; }

    public BridgeUnavailableException(string message, BridgeDiagnostics diagnostics, Exception? inner = null)
        : base(message, inner)
    {
        Diagnostics = diagnostics;
    }
}

/// <summary>Thrown when the bridge answered with a domain error (ok=false).</summary>
public sealed class BridgeCallException : Exception
{
    public BridgeError Error { get; }

    public string Method { get; }

    public BridgeCallException(string method, BridgeError error)
        : base($"{method}: [{error.Code}] {error.Message}")
    {
        Method = method;
        Error = error;
    }
}

public sealed class BridgeDiagnostics
{
    public string? ResolvedBaseUrl { get; set; }
    public string ResolvedFrom { get; set; } = "none";
    public string DiscoveryFilePath { get; set; } = string.Empty;
    public bool DiscoveryFileExists { get; set; }
    public BridgeDescriptor? Descriptor { get; set; }
    public bool? DescriptorProcessAlive { get; set; }
    public List<string> AltiumProcesses { get; set; } = new();
    public string? LastError { get; set; }
    public List<string> Hints { get; set; } = new();
}

/// <summary>
/// HTTP client for the in-process bridge. Locates the bridge in this order:
/// 1) env ALTIUM_MCP_BRIDGE_URL, 2) discovery file %LOCALAPPDATA%\AltiumMcp\bridge.json, 3) default port.
/// </summary>
public sealed class BridgeClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _readEnv;
    private readonly Func<string, string?> _readFile;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public BridgeClient(HttpMessageHandler? handler = null, Func<string?>? readEnv = null, Func<string, string?>? readFile = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _readEnv = readEnv ?? (() => Environment.GetEnvironmentVariable(BridgeDiscovery.EnvBaseUrl));
        _readFile = readFile ?? (p => File.Exists(p) ? File.ReadAllText(p) : null);
    }

    public BridgeDiagnostics Locate()
    {
        var diag = new BridgeDiagnostics { DiscoveryFilePath = BridgeDiscovery.DiscoveryFilePath };

        string? env = _readEnv();
        if (!string.IsNullOrWhiteSpace(env))
        {
            diag.ResolvedBaseUrl = env.EndsWith('/') ? env : env + "/";
            diag.ResolvedFrom = $"env:{BridgeDiscovery.EnvBaseUrl}";
        }

        string? json = _readFile(diag.DiscoveryFilePath);
        if (json != null)
        {
            diag.DiscoveryFileExists = true;
            try
            {
                diag.Descriptor = BridgeJson.Deserialize<BridgeDescriptor>(json);
                if (diag.Descriptor != null)
                {
                    diag.DescriptorProcessAlive = IsProcessAlive(diag.Descriptor.ProcessId);
                    if (diag.ResolvedBaseUrl == null && !string.IsNullOrEmpty(diag.Descriptor.BaseUrl))
                    {
                        diag.ResolvedBaseUrl = diag.Descriptor.BaseUrl;
                        diag.ResolvedFrom = "discoveryFile";
                    }
                }
            }
            catch (JsonException ex)
            {
                diag.LastError = "Discovery file is not valid JSON: " + ex.Message;
            }
        }

        if (diag.ResolvedBaseUrl == null)
        {
            diag.ResolvedBaseUrl = BridgeDiscovery.BaseUrlFor(BridgeDiscovery.DefaultPort);
            diag.ResolvedFrom = "defaultPort";
        }

        try
        {
            diag.AltiumProcesses = Process.GetProcessesByName("X2")
                .Select(p =>
                {
                    try { return $"pid {p.Id}: {p.MainModule?.FileName} ({p.MainWindowTitle})"; }
                    catch { return $"pid {p.Id}"; }
                })
                .ToList();
        }
        catch
        {
            // ignore
        }

        return diag;
    }

    public async Task<JsonElement> HealthAsync(CancellationToken ct = default)
    {
        BridgeDiagnostics diag = Locate();
        try
        {
            using var res = await _http.GetAsync(diag.ResolvedBaseUrl + "health", ct).ConfigureAwait(false);
            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw Unavailable(diag, ex);
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        BridgeDiagnostics diag = Locate();
        var request = new BridgeRequest
        {
            Method = method,
            Params = parameters == null ? null : BridgeJson.ToElement(parameters),
            Id = Guid.NewGuid().ToString("N"),
        };

        string json = BridgeJson.Serialize(request);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);

        string body;
        try
        {
            using var res = await _http.PostAsync(diag.ResolvedBaseUrl + "rpc", content, timeoutCts.Token).ConfigureAwait(false);
            body = await res.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode && string.IsNullOrWhiteSpace(body))
            {
                throw new HttpRequestException($"HTTP {(int)res.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw Unavailable(diag, ex);
        }

        BridgeResponse? response;
        try
        {
            response = BridgeJson.Deserialize<BridgeResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new BridgeCallException(method, new BridgeError(BridgeErrorCodes.Internal, "Bridge returned malformed JSON: " + ex.Message));
        }

        if (response == null)
        {
            throw new BridgeCallException(method, new BridgeError(BridgeErrorCodes.Internal, "Bridge returned an empty response."));
        }

        if (!response.Ok)
        {
            throw new BridgeCallException(method, response.Error ?? new BridgeError(BridgeErrorCodes.Internal, "Unknown bridge error."));
        }

        return response.Result ?? default;
    }

    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken ct = default)
    {
        JsonElement element = await CallAsync(method, parameters, ct).ConfigureAwait(false);
        return BridgeJson.Deserialize<T>(element) ?? throw new BridgeCallException(method, new BridgeError(BridgeErrorCodes.Internal, "Result could not be deserialized."));
    }

    private static BridgeUnavailableException Unavailable(BridgeDiagnostics diag, Exception ex)
    {
        diag.LastError = ex.GetType().Name + ": " + ex.Message;
        bool altiumRunning = diag.AltiumProcesses.Count > 0;

        if (!altiumRunning)
        {
            diag.Hints.Add("Altium Designer (X2.EXE) is not running. Start it and open a project.");
        }
        else if (!diag.DiscoveryFileExists)
        {
            diag.Hints.Add("Altium is running but the bridge extension has not been loaded yet. In Altium: View » Panels » AltiumMcpBridge (or DXP » Run Process » AltiumExtensionMCP:StartBridge). Keep the panel docked so the bridge auto-starts next time.");
            diag.Hints.Add("If the panel/command does not exist, the extension is not installed: build with `dotnet build src/AltiumMcp.Extension` (deploys to the Altium Extensions folder) and restart Altium.");
        }
        else if (diag.DescriptorProcessAlive == false)
        {
            diag.Hints.Add($"The discovery file points to Altium pid {diag.Descriptor?.ProcessId}, which is no longer running (stale file). Load the extension in the running Altium (View » Panels » AltiumMcpBridge).");
        }
        else
        {
            diag.Hints.Add("The bridge should be running but did not answer. Check the MCP Bridge panel / logs in %LOCALAPPDATA%\\AltiumMcp\\logs and press 'Restart bridge'.");
        }

        return new BridgeUnavailableException($"Altium bridge not reachable at {diag.ResolvedBaseUrl} ({diag.ResolvedFrom}): {ex.Message}", diag, ex);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
