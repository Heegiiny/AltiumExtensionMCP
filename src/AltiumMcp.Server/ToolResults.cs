using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using ModelContextProtocol.Protocol;

namespace AltiumMcp.Server;

/// <summary>
/// Uniform tool result shaping. Success: JSON of the bridge result (optionally field-projected, always size-bounded).
/// Failure: JSON error envelope with stable code + hints + correlationId and IsError=true, so the LLM gets
/// actionable diagnostics instead of an opaque exception.
/// </summary>
public static class ToolResults
{
    public const string EnvMaxResultChars = "ALTIUM_MCP_MAX_RESULT_CHARS";
    public const int DefaultMaxResultChars = 200_000;

    public static int MaxResultChars
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable(EnvMaxResultChars);
            return int.TryParse(env, out int n) && n >= 10_000 ? n : DefaultMaxResultChars;
        }
    }

    public static CallToolResult Json(object? value) => Text(JsonSerializer.Serialize(value, BridgeJson.Options));

    public static CallToolResult Text(string text) =>
        new() { Content = new List<ContentBlock> { new TextContentBlock { Text = text } } };

    public static CallToolResult Error(string code, string message, object? details = null, string? correlationId = null) =>
        new()
        {
            IsError = true,
            Content = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new { ok = false, error = new { code, message, correlationId }, details }, BridgeJson.Options),
                },
            },
        };

    /// <summary>Runs a bridge call and converts exceptions into structured error results.</summary>
    public static Task<CallToolResult> Run(Func<Task<JsonElement>> call) => Run(call, null);

    /// <summary>Runs a bridge call, applies the optional field projection and the result size bound.</summary>
    public static async Task<CallToolResult> Run(Func<Task<JsonElement>> call, IReadOnlyCollection<string>? fields)
    {
        try
        {
            JsonElement result = await call().ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Undefined)
            {
                return Text("null");
            }

            result = JsonProjection.Apply(result, fields);
            string text = result.GetRawText();
            int max = MaxResultChars;
            if (text.Length > max)
            {
                return Error(BridgeErrorCodes.ResultTooLarge,
                    $"The result is {text.Length:N0} characters (limit {max:N0}). Narrow the request instead of reading it all.",
                    new
                    {
                        hints = new[]
                        {
                            "Use 'limit'/'offset' to page list results.",
                            "Use 'filter' (substring or wildcard) to select what you need.",
                            "Use 'fields' to keep only the properties you will read.",
                            "Use detail='summary' where the tool supports detail levels.",
                            $"The limit can be raised with the {EnvMaxResultChars} environment variable of the MCP server.",
                        },
                    });
            }

            return Text(text);
        }
        catch (BridgeCallException ex)
        {
            return Error(ex.Error.Code, ex.Error.Message, ex.Error.Details, ex.Error.CorrelationId);
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
