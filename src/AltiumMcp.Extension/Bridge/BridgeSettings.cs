using System;
using System.IO;
using System.Text.Json;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;

namespace AltiumMcp.Extension.Bridge;

/// <summary>
/// Persistent settings of the bridge, stored as JSON in %LOCALAPPDATA%\AltiumMcp\settings.json so they survive
/// Altium restarts and redeploys. Access is UI-thread only (like everything else in the extension).
/// </summary>
public static class BridgeSettings
{
    public static string FilePath => Path.Combine(BridgeDiscovery.DataDirectory, "settings.json");

    private static BridgeSettingsInfo? _current;

    /// <summary>Raised on the UI thread after a change (panel checkbox ↔ bridge method stay in sync).</summary>
    public static event Action<BridgeSettingsInfo>? Changed;

    public static BridgeSettingsInfo Current
    {
        get
        {
            if (_current == null)
            {
                _current = Load();
            }

            return _current;
        }
    }

    public static bool FollowMcpQueries
    {
        get => Current.FollowMcpQueries;
        set
        {
            if (Current.FollowMcpQueries == value)
            {
                return;
            }

            Current.FollowMcpQueries = value;
            Save();
            BridgeLog.Info($"Setting changed: followMcpQueries={value}");
            Changed?.Invoke(Current);
        }
    }

    private static BridgeSettingsInfo Load()
    {
        var info = new BridgeSettingsInfo { SettingsPath = FilePath };
        try
        {
            if (File.Exists(FilePath))
            {
                BridgeSettingsInfo? stored = JsonSerializer.Deserialize<BridgeSettingsInfo>(File.ReadAllText(FilePath), BridgeJson.Options);
                if (stored != null)
                {
                    info.FollowMcpQueries = stored.FollowMcpQueries;
                }
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"Settings could not be read ({ex.Message}); using defaults.");
        }

        return info;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(BridgeDiscovery.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, BridgeJson.Pretty));
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"Settings could not be saved: {ex.Message}");
        }
    }
}
