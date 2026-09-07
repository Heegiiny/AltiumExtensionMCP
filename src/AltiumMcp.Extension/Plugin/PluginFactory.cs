using System.Runtime.InteropServices;
using AltiumMcp.Extension.Plugin;
using DXP;

// The Altium host (via Altium.DotNetSupport) instantiates the type named "CSharpPlugin.PluginFactory"
// from the DLL referenced by EditorExePath in the .ins file, then calls InvokePluginFactory(IClient).
// See docs/ALTIUM_API_NOTES.md → "Extension loading".
namespace CSharpPlugin
{
    public interface IPluginFactory
    {
        object InvokePluginFactory(IClient a);
    }

    [ClassInterface(ClassInterfaceType.None)]
    public class PluginFactory : IPluginFactory
    {
        public object InvokePluginFactory(IClient a)
        {
            return new McpServerModule(a, McpServerModule.ModuleNameConst);
        }
    }
}
