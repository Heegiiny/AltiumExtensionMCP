using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltiumMcp.Contracts.Bridge;

/// <summary>
/// Request sent by the MCP server to the in-process Altium bridge.
/// Transport: HTTP POST {baseUrl}/rpc with a JSON body.
/// </summary>
public sealed class BridgeRequest
{
    /// <summary>Method name, see <see cref="BridgeMethods"/>.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>Method parameters (method-specific object, may be null).</summary>
    public JsonElement? Params { get; set; }

    /// <summary>Optional correlation id echoed back in the response.</summary>
    public string? Id { get; set; }
}

/// <summary>Response envelope returned by the bridge for every RPC call.</summary>
public sealed class BridgeResponse
{
    public bool Ok { get; set; }

    public string? Id { get; set; }

    /// <summary>Method result when <see cref="Ok"/> is true.</summary>
    public JsonElement? Result { get; set; }

    /// <summary>Error details when <see cref="Ok"/> is false.</summary>
    public BridgeError? Error { get; set; }

    /// <summary>Wall-clock time the bridge spent servicing the request (including UI-thread marshaling).</summary>
    public long ElapsedMs { get; set; }

    public static BridgeResponse Success(string? id, JsonElement result, long elapsedMs) =>
        new() { Ok = true, Id = id, Result = result, ElapsedMs = elapsedMs };

    public static BridgeResponse Failure(string? id, BridgeError error, long elapsedMs) =>
        new() { Ok = false, Id = id, Error = error, ElapsedMs = elapsedMs };
}

public sealed class BridgeError
{
    public string Code { get; set; } = BridgeErrorCodes.Internal;

    public string Message { get; set; } = string.Empty;

    /// <summary>Optional structured details (e.g. exception type, stack, hints).</summary>
    public Dictionary<string, string>? Details { get; set; }

    public BridgeError() { }

    public BridgeError(string code, string message, Dictionary<string, string>? details = null)
    {
        Code = code;
        Message = message;
        Details = details;
    }
}

/// <summary>Stable error codes. The MCP server maps these to actionable diagnostics for the LLM.</summary>
public static class BridgeErrorCodes
{
    public const string Internal = "INTERNAL";
    public const string UnknownMethod = "UNKNOWN_METHOD";
    public const string InvalidParams = "INVALID_PARAMS";
    public const string AltiumBusy = "ALTIUM_BUSY";
    public const string NoWorkspace = "NO_WORKSPACE";
    public const string NoActiveProject = "NO_ACTIVE_PROJECT";
    public const string ProjectNotFound = "PROJECT_NOT_FOUND";
    public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
    public const string DocumentNotOpen = "DOCUMENT_NOT_OPEN";
    /// <summary>A design object (component, net, ...) was not found in the compiled model.</summary>
    public const string ObjectNotFound = "OBJECT_NOT_FOUND";
    public const string NotCompiled = "NOT_COMPILED";
    public const string Unsupported = "UNSUPPORTED";
    public const string ResultTooLarge = "RESULT_TOO_LARGE";
}

/// <summary>
/// Bridge method names. Grouped by domain so they can later be exposed as MCP toolsets.
/// Naming: {domain}.{operation}. All methods in v0.1 are read-only.
/// </summary>
public static class BridgeMethods
{
    // system.*  — bridge and host diagnostics
    public const string SystemPing = "system.ping";
    public const string SystemGetEnvironment = "system.getEnvironment";

    // workspace.* — Altium workspace (project group) level
    public const string WorkspaceGetInfo = "workspace.getInfo";
    public const string WorkspaceListProjects = "workspace.listProjects";
    public const string WorkspaceListOpenDocuments = "workspace.listOpenDocuments";

    // project.* — a single project (default: focused project)
    public const string ProjectGetStructure = "project.getStructure";
    public const string ProjectListComponents = "project.listComponents";
    public const string ProjectGetComponent = "project.getComponent";
    public const string ProjectListNets = "project.listNets";
    public const string ProjectGetNet = "project.getNet";

    public static readonly IReadOnlyList<string> All = new[]
    {
        SystemPing, SystemGetEnvironment,
        WorkspaceGetInfo, WorkspaceListProjects, WorkspaceListOpenDocuments,
        ProjectGetStructure, ProjectListComponents, ProjectGetComponent, ProjectListNets, ProjectGetNet,
    };
}

/// <summary>
/// Shared JSON options so the extension and the server agree on the wire format.
/// Default-valued members (null, false, 0, empty) are omitted to keep LLM-facing payloads compact:
/// a missing boolean means false, a missing number means 0.
/// </summary>
public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);
}
