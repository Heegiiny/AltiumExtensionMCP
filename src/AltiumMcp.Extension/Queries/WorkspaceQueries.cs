using System;
using System.Collections.Generic;
using AltiumMcp.Contracts.Model;
using DXP;
using EDP;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

internal sealed class WorkspaceQueries
{
    public WorkspaceInfo GetInfo()
    {
        IWorkspace ws = Workspace;
        IProject? focused = Safe(() => ws.DM_FocusedProject());
        IDocument? focusedDoc = Safe(() => ws.DM_FocusedDocument());

        var info = new WorkspaceInfo
        {
            WorkspaceFileName = NullIfEmpty(Safe(() => ws.DM_WorkspaceFileName())),
            WorkspaceFullPath = NullIfEmpty(Safe(() => ws.DM_WorkspaceFullPath())),
            ProjectCount = Safe(() => ws.DM_ProjectCount()),
            InstalledLibraryCount = Safe(() => ws.DM_InstalledLibraryCount()),
            FocusedProject = focused == null ? null : ToRef(focused, focused),
        };

        if (focusedDoc != null)
        {
            IProject? owner = Safe(() => focusedDoc.DM_Project());
            info.FocusedDocument = ToRef(focusedDoc, owner == null ? null : Safe(() => owner.DM_ProjectFullPath()));
        }

        IServerDocumentView? view = Safe(() => Client.GetCurrentView());
        if (view != null)
        {
            IServerDocument? doc = Safe(() => view.GetOwnerDocument());
            info.ActiveViewDocumentPath = doc == null ? null : NullIfEmpty(Safe(() => doc.GetFileName()));
        }

        return info;
    }

    public ProjectListResult ListProjects()
    {
        IWorkspace ws = Workspace;
        IProject? focused = Safe(() => ws.DM_FocusedProject());
        var result = new ProjectListResult
        {
            FocusedProjectPath = focused == null ? null : NullIfEmpty(Safe(() => focused.DM_ProjectFullPath())),
        };

        int count = Safe(() => ws.DM_ProjectCount());
        for (int i = 0; i < count; i++)
        {
            IProject? p = Safe(() => ws.DM_Projects(i));
            if (p != null)
            {
                result.Projects.Add(ToSummary(p, focused));
            }
        }

        return result;
    }

    /// <summary>
    /// Documents currently open in editors, enumerated through every loaded server module
    /// (Client.GetServerModule(i).GetDocuments(j)) and mapped back to their owning projects.
    /// </summary>
    public OpenDocumentsResult ListOpenDocuments()
    {
        IClient client = Client;
        IWorkspace? ws = Safe(() => Workspace);
        IDocument? focusedDoc = ws == null ? null : Safe(() => ws.DM_FocusedDocument());
        string? focusedPath = focusedDoc == null ? null : Safe(() => focusedDoc.DM_FullPath());

        var result = new OpenDocumentsResult { FocusedDocumentPath = NullIfEmpty(focusedPath) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int moduleCount = Safe(() => client.GetCount());
        for (int i = 0; i < moduleCount; i++)
        {
            IServerModule? m = Safe(() => client.GetServerModule(i));
            if (m == null)
            {
                continue;
            }

            int docCount = Safe(() => m.GetDocumentCount());
            for (int j = 0; j < docCount; j++)
            {
                IServerDocument? sd = Safe(() => m.GetDocuments(j));
                if (sd == null)
                {
                    continue;
                }

                string path = Safe(() => sd.GetFileName()) ?? string.Empty;
                if (string.IsNullOrEmpty(path) || !seen.Add(path))
                {
                    continue;
                }

                string? projectPath = null;
                IDocument? dm = ws == null ? null : Safe(() => ws.DM_GetDocumentFromPath(path));
                if (dm != null)
                {
                    IProject? owner = Safe(() => dm.DM_Project());
                    projectPath = owner == null ? null : NullIfEmpty(Safe(() => owner.DM_ProjectFullPath()));
                }

                result.Documents.Add(new DocumentInfo
                {
                    Ref = new DocumentRef
                    {
                        Path = path,
                        FileName = System.IO.Path.GetFileName(path),
                        Kind = Safe(() => sd.GetKind()) ?? string.Empty,
                        ProjectPath = projectPath,
                    },
                    IsLoaded = true,
                    IsOpenInEditor = Safe(() => sd.GetIsShown()),
                    IsModified = Safe(() => sd.GetModified()),
                    IsFocused = PathEquals(path, focusedPath),
                });
            }
        }

        return result;
    }
}
