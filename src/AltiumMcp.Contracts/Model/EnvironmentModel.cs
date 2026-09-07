using System;
using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>Result of system.ping — proves the bridge is alive inside Altium.</summary>
public sealed class PingResult
{
    public string Status { get; set; } = "ok";
    public string? BridgeVersion { get; set; }
    public int ProcessId { get; set; }
    public DateTimeOffset ServerTime { get; set; }
    public double UptimeSeconds { get; set; }
    public long RequestsServed { get; set; }
}

/// <summary>Result of system.getEnvironment — describes the Altium host.</summary>
public sealed class EnvironmentInfo
{
    public string? ProductName { get; set; }
    public string? ProductVersion { get; set; }
    /// <summary>DXP platform version string (Client.GetVersion()).</summary>
    public string? PlatformVersion { get; set; }
    public string? ExecutablePath { get; set; }
    public int ProcessId { get; set; }
    public string? ExtensionModuleName { get; set; }
    public string? BridgeVersion { get; set; }
    public string? DotNetRuntime { get; set; }
    public bool IsInitialized { get; set; }
    /// <summary>Loaded Altium server modules (names) — helps to diagnose which editors are available.</summary>
    public List<string> LoadedServerModules { get; set; } = new();
    /// <summary>Number of licensed technology sets (the full list is hundreds of entries; not returned to save tokens).</summary>
    public int TechnologySetCount { get; set; }
}
