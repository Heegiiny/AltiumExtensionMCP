using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

internal sealed class SystemQueries
{
    private readonly Func<BridgeHttpServer?> _server;
    private readonly string _moduleName;
    private readonly string _bridgeVersion;

    public SystemQueries(Func<BridgeHttpServer?> server, string moduleName, string bridgeVersion)
    {
        _server = server;
        _moduleName = moduleName;
        _bridgeVersion = bridgeVersion;
    }

    public PingResult Ping()
    {
        BridgeHttpServer? s = _server();
        return new PingResult
        {
            Status = "ok",
            BridgeVersion = _bridgeVersion,
            ProcessId = Environment.ProcessId,
            ServerTime = DateTimeOffset.Now,
            UptimeSeconds = s == null ? 0 : (DateTimeOffset.Now - s.StartedAt).TotalSeconds,
            RequestsServed = s?.RequestsServed ?? 0,
        };
    }

    public EnvironmentInfo GetEnvironment()
    {
        IClient client = Client;
        var info = new EnvironmentInfo
        {
            ProductName = Safe(() => client.GetProductName()),
            ProductVersion = Safe(() => client.GetProductVersion()),
            PlatformVersion = Safe(() => client.GetVersion()),
            ExecutablePath = Safe(() => Process.GetCurrentProcess().MainModule?.FileName),
            ProcessId = Environment.ProcessId,
            ExtensionModuleName = _moduleName,
            BridgeVersion = _bridgeVersion,
            DotNetRuntime = RuntimeInformation.FrameworkDescription,
            IsInitialized = Safe(() => client.IsInitialized()),
        };

        uint validAt = 0;
        string? techSets = Safe(() => client.GetTechnologySets(ref validAt));
        info.TechnologySetCount = string.IsNullOrWhiteSpace(techSets) ? 0 : techSets.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        int count = Safe(() => client.GetCount());
        for (int i = 0; i < count; i++)
        {
            IServerModule? m = Safe(() => client.GetServerModule(i));
            string? name = m == null ? null : Safe(() => m.GetModuleName());
            if (!string.IsNullOrEmpty(name))
            {
                info.LoadedServerModules.Add(name);
            }
        }

        return info;
    }
}
