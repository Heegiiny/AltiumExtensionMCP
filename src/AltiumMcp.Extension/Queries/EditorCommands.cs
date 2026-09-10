using System;
using DXP;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Editor-level (non-design) operations: showing a document and running Altium server processes
/// ("PCB:Zoom", "Sch:DeSelect", ...) the same way Altium's own managed servers do —
/// <c>(IClient as IProcessLauncher).SendMessage(process, parameters, view)</c>.
/// </summary>
internal static class EditorCommands
{
    /// <summary>Opens the document in its editor (or brings it forward). Returns the server document or null.</summary>
    public static IServerDocument? Show(string path, string kind, bool focus)
    {
        IClient client = Client;
        IServerDocument? doc = Safe(() => client.GetDocumentByPath(path)) ?? Safe(() => client.OpenDocumentShowOrHide(kind, path, true));
        if (doc == null)
        {
            return null;
        }

        if (focus)
        {
            Safe(() => { client.ShowDocument(doc); return true; });
        }
        else
        {
            Safe(() => { client.ShowDocumentDontFocus(doc); return true; });
        }

        return doc;
    }

    /// <summary>Runs a server process synchronously in the context of the document's first view. Returns false if the launcher is unavailable or the call threw.</summary>
    public static bool Run(string process, string parameters, IServerDocument? doc)
    {
        if (Client is not IProcessLauncher launcher)
        {
            return false;
        }

        IServerDocumentView? view = doc == null ? null : Safe(() => doc.GetView(0));
        view ??= Safe(() => Client.GetCurrentView());
        try
        {
            string p = parameters;
            launcher.SendMessage(process, ref p, view);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
