using System;
using System.IO;

namespace AltiumMcp.Contracts.Bridge;

/// <summary>
/// Describes a running bridge instance. Written by the extension to <see cref="BridgeDiscovery.DiscoveryFilePath"/>
/// so that out-of-process clients (the MCP server) can locate it without configuration.
/// </summary>
public sealed class BridgeDescriptor
{
    public string BaseUrl { get; set; } = string.Empty;

    public int Port { get; set; }

    public int ProcessId { get; set; }

    public string? ProductName { get; set; }

    public string? ProductVersion { get; set; }

    public string? ExecutablePath { get; set; }

    public string? BridgeVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

public static class BridgeDiscovery
{
    public const string EnvBaseUrl = "ALTIUM_MCP_BRIDGE_URL";
    public const string EnvPort = "ALTIUM_MCP_BRIDGE_PORT";

    public const int DefaultPort = 47120;

    /// <summary>How many consecutive ports the extension may try if the default is taken (multiple Altium instances).</summary>
    public const int PortSearchRange = 10;

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltiumMcp");

    public static string DiscoveryFilePath => Path.Combine(DataDirectory, "bridge.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string BaseUrlFor(int port) => $"http://127.0.0.1:{port}/";
}
