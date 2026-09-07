using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Extension.Bridge;

/// <summary>
/// Loopback-only HTTP/JSON endpoint hosted inside Altium.
///   GET  /health   → BridgeDescriptor + stats
///   GET  /methods  → list of RPC methods
///   POST /rpc      → BridgeRequest → BridgeResponse
/// Requests are parsed on thread-pool threads; Altium API access happens on the UI thread via <see cref="BridgeRouter"/>.
/// </summary>
public sealed class BridgeHttpServer : IDisposable
{
    private readonly BridgeRouter _router;
    private readonly Func<BridgeDescriptor> _describe;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private long _requestsServed;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    public BridgeHttpServer(BridgeRouter router, Func<BridgeDescriptor> describe)
    {
        _router = router;
        _describe = describe;
    }

    public bool IsRunning => _listener?.IsListening == true;

    public int Port { get; private set; }

    public string? BaseUrl { get; private set; }

    public long RequestsServed => Interlocked.Read(ref _requestsServed);

    public DateTimeOffset StartedAt => _startedAt;

    /// <summary>Starts listening. Tries the preferred port then the next <see cref="BridgeDiscovery.PortSearchRange"/> ports.</summary>
    public void Start(int preferredPort)
    {
        if (IsRunning)
        {
            return;
        }

        Exception? last = null;
        for (int port = preferredPort; port < preferredPort + BridgeDiscovery.PortSearchRange; port++)
        {
            var listener = new HttpListener();
            string prefix = BridgeDiscovery.BaseUrlFor(port);
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                BaseUrl = prefix;
                break;
            }
            catch (HttpListenerException ex)
            {
                last = ex;
                listener.Close();
            }
        }

        if (_listener == null)
        {
            throw new InvalidOperationException($"Could not bind the bridge on ports {preferredPort}..{preferredPort + BridgeDiscovery.PortSearchRange - 1}: {last?.Message}", last);
        }

        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        WriteDiscoveryFile();
        BridgeLog.Info($"Bridge listening on {BaseUrl}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"Stop: {ex.Message}");
        }
        finally
        {
            _listener = null;
            RemoveDiscoveryFile();
            BridgeLog.Info("Bridge stopped");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        HttpListener listener = _listener!;
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                BridgeLog.Warn($"Accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleRequestSafe(ctx), CancellationToken.None);
        }
    }

    private void HandleRequestSafe(HttpListenerContext ctx)
    {
        try
        {
            HandleRequest(ctx);
        }
        catch (Exception ex)
        {
            BridgeLog.Error("Unhandled request error", ex);
            try
            {
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        HttpListenerRequest req = ctx.Request;
        HttpListenerResponse res = ctx.Response;
        Interlocked.Increment(ref _requestsServed);

        // Loopback only — refuse anything else even if the prefix somehow allowed it.
        if (req.RemoteEndPoint is { } ep && !IPAddress.IsLoopback(ep.Address))
        {
            res.StatusCode = 403;
            res.Close();
            return;
        }

        string path = req.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        if (req.HttpMethod == "GET" && (path == "/health" || path == string.Empty))
        {
            var d = _describe();
            WriteJson(res, 200, new
            {
                status = "ok",
                descriptor = d,
                requestsServed = RequestsServed,
                uptimeSeconds = (DateTimeOffset.Now - _startedAt).TotalSeconds,
            });
            return;
        }

        if (req.HttpMethod == "GET" && path == "/methods")
        {
            WriteJson(res, 200, new { methods = _router.Methods });
            return;
        }

        if (req.HttpMethod == "POST" && path == "/rpc")
        {
            string body;
            using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            {
                body = reader.ReadToEnd();
            }

            BridgeRequest? request;
            try
            {
                request = BridgeJson.Deserialize<BridgeRequest>(body);
            }
            catch (JsonException ex)
            {
                WriteJson(res, 400, BridgeResponse.Failure(null, new BridgeError(BridgeErrorCodes.InvalidParams, $"Malformed JSON: {ex.Message}"), 0));
                return;
            }

            if (request == null)
            {
                WriteJson(res, 400, BridgeResponse.Failure(null, new BridgeError(BridgeErrorCodes.InvalidParams, "Empty request."), 0));
                return;
            }

            var sw = Stopwatch.StartNew();
            BridgeResponse response = _router.Execute(request);
            BridgeLog.Info($"{request.Method} -> {(response.Ok ? "ok" : response.Error?.Code)} in {sw.ElapsedMilliseconds}ms");
            // Domain errors are still HTTP 200: the envelope carries the status. Only transport problems use 4xx/5xx.
            WriteJson(res, 200, response);
            return;
        }

        res.StatusCode = 404;
        WriteJson(res, 404, new { error = "Not found. Endpoints: GET /health, GET /methods, POST /rpc" });
    }

    private static void WriteJson(HttpListenerResponse res, int status, object payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, BridgeJson.Options);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        using Stream s = res.OutputStream;
        s.Write(bytes, 0, bytes.Length);
    }

    private void WriteDiscoveryFile()
    {
        try
        {
            Directory.CreateDirectory(BridgeDiscovery.DataDirectory);
            BridgeDescriptor d = _describe();
            File.WriteAllText(BridgeDiscovery.DiscoveryFilePath, JsonSerializer.Serialize(d, BridgeJson.Pretty), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"Could not write discovery file: {ex.Message}");
        }
    }

    private void RemoveDiscoveryFile()
    {
        try
        {
            if (File.Exists(BridgeDiscovery.DiscoveryFilePath))
            {
                // Only remove if it is ours (same pid), so a second Altium instance's file survives.
                var existing = BridgeJson.Deserialize<BridgeDescriptor>(File.ReadAllText(BridgeDiscovery.DiscoveryFilePath));
                if (existing == null || existing.ProcessId == Process.GetCurrentProcess().Id)
                {
                    File.Delete(BridgeDiscovery.DiscoveryFilePath);
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose() => Stop();
}
