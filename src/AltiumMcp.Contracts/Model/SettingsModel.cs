namespace AltiumMcp.Contracts.Model;

/// <summary>Persistent bridge settings (system.getSettings / system.setSettings; also the MCP Bridge panel).</summary>
public sealed class BridgeSettingsInfo
{
    /// <summary>
    /// "Follow MCP queries in Altium": when true, component/net lookups cross-probe to the object in the editor
    /// (open/activate the sheet or board, replace the selection). Editor state only — documents are never modified.
    /// </summary>
    public bool FollowMcpQueries { get; set; }

    /// <summary>Where the settings are stored (informational).</summary>
    public string? SettingsPath { get; set; }
}

public sealed class SetSettingsParams
{
    /// <summary>New value for "Follow MCP queries in Altium"; null leaves it unchanged.</summary>
    public bool? FollowMcpQueries { get; set; }
}
