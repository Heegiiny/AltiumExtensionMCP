using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using ModelContextProtocol.Protocol;

namespace AltiumMcp.Server;

/// <summary>
/// Uniform tool result shaping. Success: JSON of the bridge result. Failure: JSON error envelope with stable
/// code + hints and IsError=true, so the LLM gets actionable diagnostics instead of an opaque exception.
/// </summary>
public static class ToolResults
{
    public static CallToolResult Json(object? value) => Text(JsonSerializer.Serialize(value, BridgeJson.Options));

    public static CallToolResult Text(string text) =>
        new() { Content = new List<ContentBlock> { new TextContentBlock { Text = text } } };

    public static CallToolResult Error(string code, string message, object? details = null) =>
        new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { ok = false, error = new { code, message }, details }, BridgeJson.Options),
                },
            },
        };

    /// <summary>Runs a bridge call and converts exceptions into structured error results.</summary>
    public static async Task<CallToolResult> Run(Func<Task<JsonElement>> call)
    {
        try
        {
            JsonElement result = await call().ConfigureAwait(false);
            return Text(result.ValueKind == JsonValueKind.Undefined ? "null" : result.GetRawText());
        }
        catch (BridgeCallException ex)
        {
            return Error(ex.Error.Code, ex.Error.Message, ex.Error.Details);
        }
        catch (BridgeUnavailableException ex)
        {
            return Error("BRIDGE_UNAVAILABLE", ex.Message, ex.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Error("CANCELLED", "The request was cancelled.");
        }
        catch (Exception ex)
        {
            return Error("SERVER_INTERNAL", ex.Message, new { exception = ex.GetType().FullName });
        }
    }
}
