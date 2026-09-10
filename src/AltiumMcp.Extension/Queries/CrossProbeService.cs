using System;
using System.Windows.Forms;
using AltiumMcp.Extension.Bridge;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Cross Probe = "Follow MCP queries in Altium". Lookup tools call <see cref="Request"/> with a delegate that
/// performs the probe (EDP's native <c>DM_DoCrossProbe</c>, or our own editor selection). Requests are coalesced:
/// a short UI-thread timer runs only the latest one, so a burst of agent calls does not make the editor jump
/// around. Everything runs on the UI thread; exceptions are caught and logged (no modal dialogs, no crash).
/// A probe is a UI side effect, never a design edit — it must not mark documents modified.
/// </summary>
internal static class CrossProbeService
{
    private const int CoalesceMs = 250;

    private static Timer? _timer;
    private static Action? _pending;
    private static string? _pendingDescription;

    /// <summary>Effective decision: per-call override wins, else the persistent setting.</summary>
    public static bool ShouldProbe(bool? perCallOverride) => perCallOverride ?? BridgeSettings.FollowMcpQueries;

    /// <summary>Queues a probe; returns the status string to put into the result ("scheduled").</summary>
    public static string Request(string description, Action probe)
    {
        _pending = probe;
        _pendingDescription = description;

        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
        return "scheduled";
    }

    private static Timer CreateTimer()
    {
        var t = new Timer { Interval = CoalesceMs };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Action? probe = _pending;
            string? what = _pendingDescription;
            _pending = null;
            _pendingDescription = null;
            if (probe == null)
            {
                return;
            }

            try
            {
                probe();
                BridgeLog.Info($"Cross probe: {what}");
            }
            catch (Exception ex)
            {
                BridgeLog.Warn($"Cross probe failed ({what}): {ex.Message}");
            }
        };
        return t;
    }
}
