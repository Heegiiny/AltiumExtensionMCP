using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Server;
using AltiumMcp.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Usage:
//   AltiumMcp.Server                          → MCP server over stdio (for Claude Desktop / Cursor / Claude Code)
//   AltiumMcp.Server --health                 → print bridge health (debug)
//   AltiumMcp.Server --diag                   → print bridge discovery diagnostics (debug, does not touch Altium)
//   AltiumMcp.Server --call <method> [json]   → call a bridge method directly and print the JSON result (debug);
//                                              json may be inline, @file.json, or - (read from stdin)
//   AltiumMcp.Server --methods                → list bridge method names known to the contracts

if (args.Length > 0)
{
    return await DebugCli.RunAsync(args);
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP transport — all logs must go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<BridgeClient>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "altium-mcp", Version = McpServerInfo.Version };
        options.ServerInstructions = McpServerInfo.Instructions;
    })
    .WithStdioServerTransport()
    .WithTools<ConnectionTools>()
    .WithTools<WorkspaceTools>()
    .WithTools<ProjectTools>()
    .WithTools<SchematicTools>()
    .WithTools<PcbTools>();

await builder.Build().RunAsync();
return 0;

internal static class McpServerInfo
{
    public static string Version =>
        typeof(McpServerInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public const string Instructions =
        "Read-only access to the running Altium Designer via an in-process bridge. " +
        "Workflow: altium_ping → altium_get_workspace → altium_list_projects → altium_get_project_structure → " +
        "altium_list_components / altium_list_nets (use filter+limit) → altium_get_component / altium_get_net. " +
        "Project identifiers are full file paths; component ids are schematic UniqueIds; net ids are flattened net names. " +
        "If a tool returns BRIDGE_UNAVAILABLE, follow the hints (Altium must be running with the AltiumExtensionMCP extension loaded).";
}

internal static class DebugCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var client = new BridgeClient();
        try
        {
            switch (args[0])
            {
                case "--health":
                    Print(await client.HealthAsync());
                    return 0;
                case "--diag":
                    Console.WriteLine(JsonSerializer.Serialize(client.Locate(), BridgeJson.Pretty));
                    return 0;
                case "--methods":
                    Console.WriteLine(string.Join(Environment.NewLine, BridgeMethods.All));
                    return 0;
                case "--call":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("usage: --call <method> [jsonParams]");
                        return 2;
                    }

                    JsonElement? p = null;
                    if (args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]))
                    {
                        // Params: inline JSON, "@file.json", or "-" (stdin). The file/stdin forms avoid the
                        // shell quoting problems Windows PowerShell has with backslashes and quotes in JSON.
                        string raw = args[2];
                        if (raw == "-")
                        {
                            raw = await Console.In.ReadToEndAsync();
                        }
                        else if (raw.StartsWith('@'))
                        {
                            raw = await File.ReadAllTextAsync(raw.Substring(1));
                        }

                        try
                        {
                            p = JsonDocument.Parse(raw).RootElement.Clone();
                        }
                        catch (JsonException ex)
                        {
                            Console.Error.WriteLine($"Invalid JSON params: {ex.Message}");
                            Console.Error.WriteLine($"Received: {raw}");
                            Console.Error.WriteLine("Tip: pass @file.json or '-' (stdin) instead of inline JSON from PowerShell.");
                            return 2;
                        }
                    }

                    Print(await client.CallAsync(args[1], p));
                    return 0;
                default:
                    Console.Error.WriteLine("Unknown option. See the comment at the top of Program.cs.");
                    return 2;
            }
        }
        catch (BridgeUnavailableException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(JsonSerializer.Serialize(ex.Diagnostics, BridgeJson.Pretty));
            return 3;
        }
        catch (BridgeCallException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(JsonSerializer.Serialize(ex.Error, BridgeJson.Pretty));
            return 4;
        }
    }

    private static void Print(JsonElement element)
    {
        Console.WriteLine(JsonSerializer.Serialize(element, BridgeJson.Pretty));
    }
}
