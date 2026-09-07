using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Extension.Bridge;

/// <summary>
/// Maps bridge method names to handlers and executes them on the Altium UI thread.
/// Handlers receive raw JSON params and return a POCO that is serialized with <see cref="BridgeJson"/>.
/// </summary>
public sealed class BridgeRouter
{
    public delegate object? Handler(JsonElement? args);

    private readonly Dictionary<string, Handler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly UiThreadDispatcher _dispatcher;

    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public BridgeRouter(UiThreadDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public IReadOnlyCollection<string> Methods => _handlers.Keys;

    public void Register(string method, Handler handler) => _handlers[method] = handler;

    /// <summary>Registers a typed handler; params are deserialized to <typeparamref name="TParams"/> (null allowed).</summary>
    public void Register<TParams>(string method, Func<TParams?, object?> handler) where TParams : class
    {
        _handlers[method] = args =>
        {
            TParams? p = null;
            if (args is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
            {
                try
                {
                    p = BridgeJson.Deserialize<TParams>(args.Value);
                }
                catch (JsonException ex)
                {
                    throw new BridgeException(BridgeErrorCodes.InvalidParams, $"Invalid params for {method}: {ex.Message}");
                }
            }

            return handler(p);
        };
    }

    public BridgeResponse Execute(BridgeRequest request)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return BridgeResponse.Failure(request.Id, new BridgeError(BridgeErrorCodes.InvalidParams, "Missing 'method'."), sw.ElapsedMilliseconds);
        }

        if (!_handlers.TryGetValue(request.Method, out Handler? handler))
        {
            return BridgeResponse.Failure(
                request.Id,
                new BridgeError(BridgeErrorCodes.UnknownMethod, $"Unknown method '{request.Method}'.",
                    new Dictionary<string, string> { ["knownMethods"] = string.Join(",", _handlers.Keys) }),
                sw.ElapsedMilliseconds);
        }

        try
        {
            object? result = _dispatcher.Invoke(() => handler(request.Params), DefaultTimeout);
            JsonElement element = BridgeJson.ToElement(result);
            return BridgeResponse.Success(request.Id, element, sw.ElapsedMilliseconds);
        }
        catch (BridgeException ex)
        {
            BridgeLog.Warn($"{request.Method} -> {ex.Code}: {ex.Message}");
            return BridgeResponse.Failure(request.Id, ex.ToError(), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            BridgeLog.Error($"{request.Method} failed", ex);
            var details = new Dictionary<string, string>
            {
                ["exception"] = ex.GetType().FullName ?? ex.GetType().Name,
            };
            if (ex.StackTrace is { } st)
            {
                details["stackTrace"] = st.Length > 2000 ? st.Substring(0, 2000) : st;
            }

            return BridgeResponse.Failure(request.Id, new BridgeError(BridgeErrorCodes.Internal, ex.Message, details), sw.ElapsedMilliseconds);
        }
    }
}
