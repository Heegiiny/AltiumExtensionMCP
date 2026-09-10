using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using AltiumMcp.Extension.Panel;
using AltiumMcp.Extension.Queries;
using DXP;

namespace AltiumMcp.Extension.Plugin;

/// <summary>
/// The Altium server module for the extension. Created once per Altium session (on the UI thread) when the host
/// loads the extension — which happens lazily: on the first command, when the MCP Bridge panel is restored/opened,
/// or via "X2.EXE -RAltiumExtensionMCP:StartBridge". The bridge HTTP listener is started in the constructor so that
/// merely loading the module makes the bridge available.
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class McpServerModule : ServerModule
{
    public const string ModuleNameConst = "AltiumExtensionMCP";

    public static readonly string BridgeVersion =
        typeof(McpServerModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(McpServerModule).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly UiThreadDispatcher _dispatcher;
    private readonly BridgeRouter _router;
    private BridgeHttpServer? _server;
    private BridgePanelView? _panel;

    public McpServerModule(IClient client, string moduleName)
        : base(client, moduleName)
    {
        BridgeLog.Info($"McpServerModule created (bridge {BridgeVersion}, pid {Environment.ProcessId}, thread {Environment.CurrentManagedThreadId})");
        _dispatcher = new UiThreadDispatcher();
        _router = new BridgeRouter(_dispatcher);
        RegisterMethods();
        TryStartBridge();

        // Altium gives modules no explicit shutdown callback. These hooks are best-effort: measured on AD 26.3
        // they do NOT fire when X2.EXE exits (the native host tears the runtime down without running them),
        // so bridge.json is usually left behind. BridgeClient tolerates that by checking the recorded pid.
        // TODO: look for a shutdown INotification in ReceiveNotificationImpl instead.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownBridge("ProcessExit");
        System.Windows.Forms.Application.ApplicationExit += (_, _) => ShutdownBridge("ApplicationExit");
    }

    private void ShutdownBridge(string reason)
    {
        try
        {
            if (_server != null)
            {
                BridgeLog.Info($"Stopping bridge ({reason})");
                _server.Stop();
                _server = null;
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Error("Bridge shutdown failed", ex);
        }
    }

    // ---- ServerModule overrides ----------------------------------------------------------------------------------

    protected override void InitializeCommands()
    {
        var launcher = (CommandLauncher)CommandLauncher;
        launcher.RegisterCommand("StartBridge", Command_StartBridge);
        launcher.RegisterCommand("StopBridge", Command_StopBridge);
        launcher.RegisterCommand("ShowBridgePanel", Command_ShowBridgePanel);
        launcher.RegisterCommand("BridgeStatus", Command_BridgeStatus);
    }

    protected override IServerDocument NewDocumentInstance(string kind, string fileName) => null!;

    protected override IServerView CreateServerViewImpl(string argName)
    {
        // The host asks us to create the panel view (by PanelInfo.Name). We own exactly one panel, so accept any name.
        BridgeLog.Info($"CreateServerView('{argName}')");
        return EnsurePanel();
    }

    // ---- commands -------------------------------------------------------------------------------------------------

    private void Command_StartBridge(IServerDocumentView view, ref string parameters)
    {
        TryStartBridge();
    }

    private void Command_StopBridge(IServerDocumentView view, ref string parameters)
    {
        _server?.Stop();
    }

    private void Command_ShowBridgePanel(IServerDocumentView view, ref string parameters)
    {
        try
        {
            BridgePanelView panel = EnsurePanel();
            if (GlobalVars.Client.GetServerViewFromName(BridgePanelView.ViewName) == null)
            {
                GlobalVars.Client.AddServerView(panel);
            }

            GlobalVars.Client.GetGUIManager().SetPanelVisibleInCurrentForm(BridgePanelView.ViewName, argIsVisible: true);
            panel.PanelForm.RefreshStatus();
        }
        catch (Exception ex)
        {
            BridgeLog.Error("ShowBridgePanel failed", ex);
            Utils.ShowMessage("MCP Bridge panel could not be shown: " + ex.Message);
        }
    }

    private void Command_BridgeStatus(IServerDocumentView view, ref string parameters)
    {
        Utils.ShowMessage(StatusText());
    }

    // ---- bridge lifecycle -----------------------------------------------------------------------------------------

    private void TryStartBridge()
    {
        if (_server?.IsRunning == true)
        {
            return;
        }

        try
        {
            _server = new BridgeHttpServer(_router, Describe);
            _server.Start(PreferredPort());
            _panel?.PanelForm.SetUrl(_server.BaseUrl ?? string.Empty);
            _panel?.PanelForm.RefreshStatus();
        }
        catch (Exception ex)
        {
            BridgeLog.Error("Bridge failed to start", ex);
            _server = null;
        }
    }

    private void RestartBridge()
    {
        _server?.Stop();
        _server = null;
        TryStartBridge();
    }

    private static int PreferredPort()
    {
        string? env = Environment.GetEnvironmentVariable(BridgeDiscovery.EnvPort);
        return int.TryParse(env, out int p) && p > 0 && p < 65536 ? p : BridgeDiscovery.DefaultPort;
    }

    private BridgeDescriptor Describe()
    {
        IClient? client = GlobalVars.Client;
        return new BridgeDescriptor
        {
            BaseUrl = _server?.BaseUrl ?? string.Empty,
            Port = _server?.Port ?? 0,
            ProcessId = Environment.ProcessId,
            ProductName = SafeString(() => client?.GetProductName()),
            ProductVersion = SafeString(() => client?.GetProductVersion()),
            ExecutablePath = SafeString(() => Process.GetCurrentProcess().MainModule?.FileName),
            BridgeVersion = BridgeVersion,
            StartedAt = _server?.StartedAt ?? DateTimeOffset.Now,
        };
    }

    private string StatusText()
    {
        if (_server?.IsRunning == true)
        {
            return $"MCP Bridge {BridgeVersion}: listening on {_server.BaseUrl}\nRequests served: {_server.RequestsServed}\nDiscovery file: {BridgeDiscovery.DiscoveryFilePath}\nLogs: {BridgeDiscovery.LogDirectory}";
        }

        return $"MCP Bridge {BridgeVersion}: NOT running.\nRun AltiumExtensionMCP:StartBridge or press 'Restart bridge'.\nLogs: {BridgeDiscovery.LogDirectory}";
    }

    private BridgePanelView EnsurePanel()
    {
        if (_panel == null)
        {
            var form = new BridgePanelForm(StatusText, RestartBridge);
            form.SetUrl(_server?.BaseUrl ?? string.Empty);
            _panel = new BridgePanelView(form);
            AddView(_panel);
        }

        return _panel;
    }

    private void RegisterMethods()
    {
        var system = new SystemQueries(() => _server, ModuleName, BridgeVersion);
        var workspace = new WorkspaceQueries();
        var project = new ProjectQueries();

        _router.Register(BridgeMethods.SystemPing, _ => system.Ping());
        _router.Register(BridgeMethods.SystemGetEnvironment, _ => system.GetEnvironment());

        _router.Register(BridgeMethods.WorkspaceGetInfo, _ => workspace.GetInfo());
        _router.Register(BridgeMethods.WorkspaceListProjects, _ => workspace.ListProjects());
        _router.Register(BridgeMethods.WorkspaceListOpenDocuments, _ => workspace.ListOpenDocuments());
        _router.Register<OpenDocumentParams>(BridgeMethods.WorkspaceOpenDocument, p => workspace.OpenDocument(p));

        _router.Register<ProjectQueryParams>(BridgeMethods.ProjectGetStructure, p => project.GetStructure(p));
        _router.Register<ListComponentsParams>(BridgeMethods.ProjectListComponents, p => project.ListComponents(p));
        _router.Register<GetComponentParams>(BridgeMethods.ProjectGetComponent, p => project.GetComponent(p));
        _router.Register<ListNetsParams>(BridgeMethods.ProjectListNets, p => project.ListNets(p));
        _router.Register<GetNetParams>(BridgeMethods.ProjectGetNet, p => project.GetNet(p));

        var sch = new SchematicQueries();
        _router.Register<SheetQueryParams>(BridgeMethods.SchGetSheet, p => sch.GetSheet(p));
        _router.Register<ListSheetObjectsParams>(BridgeMethods.SchListObjects, p => sch.ListObjects(p));
        _router.Register<GetSchComponentParams>(BridgeMethods.SchGetComponent, p => sch.GetComponent(p));
        _router.Register<SelectParams>(BridgeMethods.SchSelect, p => sch.Select(p));

        var pcb = new PcbQueries();
        _router.Register<BoardQueryParams>(BridgeMethods.PcbGetBoard, p => pcb.GetBoard(p));
        _router.Register<ListPcbComponentsParams>(BridgeMethods.PcbListComponents, p => pcb.ListComponents(p));
        _router.Register<GetPcbComponentParams>(BridgeMethods.PcbGetComponent, p => pcb.GetComponent(p));
        _router.Register<ListPcbNetsParams>(BridgeMethods.PcbListNets, p => pcb.ListNets(p));
        _router.Register<GetPcbNetParams>(BridgeMethods.PcbGetNet, p => pcb.GetNet(p));
        _router.Register<ListPcbRulesParams>(BridgeMethods.PcbListRules, p => pcb.ListRules(p));
        _router.Register<ListPcbPrimitivesParams>(BridgeMethods.PcbListPrimitives, p => pcb.ListPrimitives(p));
        _router.Register<ListViolationsParams>(BridgeMethods.PcbListViolations, p => pcb.ListViolations(p));
        _router.Register<RunDrcParams>(BridgeMethods.PcbRunDrc, p => pcb.RunDrc(p));
        _router.Register<SelectParams>(BridgeMethods.PcbSelect, p => pcb.Select(p));
    }

    private static string? SafeString(Func<string?> f)
    {
        try
        {
            return f();
        }
        catch
        {
            return null;
        }
    }
}
