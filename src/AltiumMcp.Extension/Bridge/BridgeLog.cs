using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AltiumMcp.Contracts.Bridge;

namespace AltiumMcp.Extension.Bridge;

/// <summary>
/// Tiny file + in-memory logger. File: %LOCALAPPDATA%\AltiumMcp\logs\bridge-YYYYMMDD.log.
/// Never throws — logging must not break the host.
/// </summary>
public static class BridgeLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new();
    private const int RecentCapacity = 300;

    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static string[] GetRecent()
    {
        lock (Gate)
        {
            return Recent.ToArray();
        }
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
        lock (Gate)
        {
            Recent.Enqueue(line);
            while (Recent.Count > RecentCapacity)
            {
                Recent.Dequeue();
            }

            try
            {
                Directory.CreateDirectory(BridgeDiscovery.LogDirectory);
                string path = Path.Combine(BridgeDiscovery.LogDirectory, $"bridge-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // ignore
        }
    }
}
