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
        "Structured, read-only access to the design open in the running Altium Designer (schematic + PCB) via an in-process bridge. " +
        "The PROJECT is the primary context: component and net lookups resolve across the whole compiled project, and closed sheets/boards are loaded on demand — you never need to open documents to read them. " +
        "Workflow: altium_ping → altium_get_workspace → altium_get_project_agent_docs (follow AGENTS.md if present) → altium_get_project_structure → " +
        "compact inventory first (altium_list_components with filter/fields, e.g. filter 'U*' then 'J*', 'Q*') → relevant connectivity (altium_get_components / altium_get_nets / altium_trace_connectivity) → targeted full detail (detail='full') only where needed. " +
        "Token discipline: use 'fields', 'limit', 'filter', detail='summary'; default responses already omit GUIDs, hidden parameters, models and geometry. " +
        "Identities: project = full .PrjPcb path; component id = schematic UniqueId (also accepts designator); net id = 'netId' (also accepts name; AMBIGUOUS_OBJECT lists candidates when a name is not unique). " +
        "A project net is one object across sheets: altium_get_net returns documentPaths + per-sheet segments; never treat a net as belonging to a single sheet. " +
        "Sheet-level (altium_*_sheet_*) and board-level (altium_*_pcb_*) tools give geometry/placement; use them after the compiled facts. " +
        "Selection: call altium_get_selection ONLY when the user explicitly refers to what they selected ('this', 'the selected part'); an incidental selection means nothing. " +
        "Cross probe: with the 'Follow MCP queries in Altium' setting (altium_get_settings / panel checkbox) or crossProbe=true, lookups select and zoom to the object in the editor; this is editor state only and never modifies documents. " +
        "Errors are structured (code, message, hints, correlationId): INVALID_ARGUMENT, DOCUMENT_NOT_FOUND, DOCUMENT_NOT_OPEN, OBJECT_NOT_FOUND, AMBIGUOUS_OBJECT, NOT_COMPILED, ALTIUM_API_ERROR, RESULT_TOO_LARGE, BRIDGE_UNAVAILABLE — follow the hints; never pass placeholder strings like 'None' for omitted arguments. " +
        "Analysis-only requests never authorize creating, editing or saving files; documentation writes need an explicit request.";
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
