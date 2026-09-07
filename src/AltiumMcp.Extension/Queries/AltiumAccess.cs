using System;
using System.Collections.Generic;
using System.IO;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using EDP;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Thin, defensive access layer over the Altium SDK objects. Everything here must run on the UI thread.
/// COM calls are wrapped so that a single failing property never aborts a whole listing.
/// </summary>
internal static class AltiumAccess
{
    public static IClient Client =>
        GlobalVars.Client ?? throw new BridgeException(BridgeErrorCodes.Internal, "IClient is not available (module not initialised).");

    /// <summary>
    /// The Workspace Manager data model. GlobalVars.DXPWorkSpace (IDXPWorkSpace) is the same COM object that also
    /// implements EDP.IWorkspace — the cast pattern used by Altium's own SDK utilities.
    /// </summary>
    public static IWorkspace Workspace
    {
        get
        {
            IDXPWorkSpace? ws = GlobalVars.DXPWorkSpace;
            if (ws is IWorkspace typed)
            {
                return typed;
            }

            throw new BridgeException(BridgeErrorCodes.NoWorkspace, "Workspace Manager is not available (IDXPWorkSpace is null or does not implement EDP.IWorkspace).");
        }
    }

    /// <summary>Resolves a project by full path (case-insensitive) or returns the focused project when path is empty.</summary>
    public static IProject ResolveProject(string? projectPath)
    {
        IWorkspace ws = Workspace;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            IProject? focused = Safe(() => ws.DM_FocusedProject());
            if (focused == null)
            {
                throw new BridgeException(BridgeErrorCodes.NoActiveProject,
                    "No focused project. Open a project in Altium or pass projectPath explicitly (see workspace.listProjects).");
            }

            return focused;
        }

        IProject? byPath = Safe(() => ws.DM_GetProjectFromPath(projectPath));
        if (byPath != null)
        {
            return byPath;
        }

        int count = Safe(() => ws.DM_ProjectCount());
        for (int i = 0; i < count; i++)
        {
            IProject? p = Safe(() => ws.DM_Projects(i));
            if (p == null)
            {
                continue;
            }

            string? full = Safe(() => p.DM_ProjectFullPath());
            string? name = Safe(() => p.DM_ProjectFileName());
            if (PathEquals(full, projectPath) || string.Equals(name, projectPath, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        throw new BridgeException(BridgeErrorCodes.ProjectNotFound, $"Project not found in the workspace: '{projectPath}'.",
            new Dictionary<string, string> { ["hint"] = "Use workspace.listProjects to get valid project paths." });
    }

    public static ProjectRef ToRef(IProject project, IProject? focused)
    {
        string full = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty;
        string name = Safe(() => project.DM_ProjectFileName()) ?? Path.GetFileName(full);
        return new ProjectRef
        {
            Path = full,
            Name = name,
            Kind = ProjectKind(full, name),
            IsFocused = focused != null && PathEquals(full, Safe(() => focused.DM_ProjectFullPath())),
        };
    }

    public static ProjectSummary ToSummary(IProject project, IProject? focused)
    {
        return new ProjectSummary
        {
            Ref = ToRef(project, focused),
            LogicalDocumentCount = Safe(() => project.DM_LogicalDocumentCount()),
            PhysicalDocumentCount = Safe(() => project.DM_PhysicalDocumentCount()),
            GeneratedDocumentCount = Safe(() => project.DM_GeneratedDocumentCount()),
            VariantCount = Safe(() => project.DM_ProjectVariantCount()),
            NeedsCompile = Safe(() => project.DM_NeedsCompile()),
            InCompilation = Safe(() => project.DM_InCompilation()),
            ManagedProjectGuid = NullIfEmpty(Safe(() => project.DM_ManagedProjectGUID())),
            OutputPath = NullIfEmpty(Safe(() => project.DM_GetOutputPath())),
        };
    }

    public static DocumentRef ToRef(IDocument doc, string? projectPath)
    {
        string full = Safe(() => doc.DM_FullPath()) ?? string.Empty;
        return new DocumentRef
        {
            Path = full,
            FileName = Safe(() => doc.DM_FileName()) ?? Path.GetFileName(full),
            Kind = Safe(() => doc.DM_DocumentKind()) ?? string.Empty,
            ProjectPath = projectPath,
        };
    }

    public static DocumentInfo ToInfo(IDocument doc, string? projectPath, string? focusedDocPath, bool includeCounts)
    {
        DocumentRef r = ToRef(doc, projectPath);
        IServerDocument? open = string.IsNullOrEmpty(r.Path) ? null : Safe(() => Client.GetDocumentByPath(r.Path));
        var info = new DocumentInfo
        {
            Ref = r,
            IsLoaded = Safe(() => doc.DM_DocumentIsLoaded()),
            IsOpenInEditor = open != null,
            IsModified = open != null && Safe(() => open.GetModified()),
            IsFocused = PathEquals(r.Path, focusedDocPath),
            IndentLevel = Safe(() => doc.DM_IndentLevel()),
            IsPrimaryImplementationDocument = Safe(() => doc.DM_IsPrimaryImplementationDocument()),
        };

        if (includeCounts)
        {
            info.ComponentCount = Safe(() => doc.DM_ComponentCount());
            info.NetCount = Safe(() => doc.DM_NetCount());
            info.SheetSymbolCount = Safe(() => doc.DM_SheetSymbolCount());
            info.PortCount = Safe(() => doc.DM_PortCount());
        }

        return info;
    }

    public static Dictionary<string, string> ReadParameters(IDMObject obj)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int count = Safe(() => obj.DM_ParameterCount());
        for (int i = 0; i < count; i++)
        {
            IParameter? p = Safe(() => obj.DM_Parameters(i));
            if (p == null)
            {
                continue;
            }

            string? name = Safe(() => p.DM_Name());
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result[name] = Safe(() => p.DM_Value()) ?? string.Empty;
        }

        return result;
    }

    public static string ProjectKind(string fullPath, string fileName)
    {
        string ext = Path.GetExtension(string.IsNullOrEmpty(fullPath) ? fileName : fullPath);
        if (string.IsNullOrEmpty(ext))
        {
            return fileName.Contains("Free Documents", StringComparison.OrdinalIgnoreCase) ? "FreeDocuments" : "Unknown";
        }

        return ext.TrimStart('.');
    }

    public static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>Executes a COM getter; returns default on any exception (logged at debug granularity only).</summary>
    public static T? Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch (BridgeException)
        {
            throw;
        }
        catch (Exception)
        {
            return default;
        }
    }
}
