using System.Runtime.InteropServices;
using DXP;

namespace AltiumMcp.Extension.Panel;

[ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class BridgePanelView : ServerPanelView
{
    /// <summary>Must match PanelInfo.Name in the .ins file; used with IGUIManager.SetPanelVisibleInCurrentForm.</summary>
    public const string ViewName = "AltiumMcpBridge";

    public const string Caption = "MCP Bridge";

    public BridgePanelForm PanelForm { get; }

    public BridgePanelView(BridgePanelForm form)
        : base(form, ViewName, Caption)
    {
        PanelForm = form;
    }
}
