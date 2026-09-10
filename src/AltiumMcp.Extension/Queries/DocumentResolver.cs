using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using EDP;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>Outcome of <see cref="DocumentResolver.Resolve"/>: a validated, existing document path.</summary>
internal sealed class ResolvedDocument
{
    public string FullPath { get; init; } = string.Empty;
    /// <summary>Loaded in an editor server (visible or hidden) at resolution time.</summary>
    public bool IsLoaded { get; init; }
    public string? ProjectPath { get; init; }
    /// <summary>How the path was chosen when the caller omitted it (for result notes / diagnostics).</summary>
    public string? ResolvedBy { get; init; }
}

/// <summary>
/// Single place that turns an agent-supplied <c>documentPath</c> into a real file the editor servers may load.
/// Rules (review 2026-09-10 §3):
///  - absent / placeholder ("None", "null", "") is treated as omitted — never handed to Altium;
///  - the project is the primary context: an omitted path resolves through the focused project's documents
///    (primary implementation PcbDoc; the single sheet; the active editor sheet only if it belongs to the project);
///  - a bare file name ("Bluetooth.SchDoc") resolves against the project's documents;
///  - nothing is passed to <c>Load…ByPath</c> unless it is already loaded or exists on disk, so Altium never gets
///    the chance to show a "file not found" dialog.
/// </summary>
internal static class DocumentResolver
{
    public const string Sch = "SCH";
    public const string Pcb = "PCB";

    public static ResolvedDocument Resolve(string? documentPath, string kind, string? projectPath = null)
    {
        string? given = InputNormalizer.Optional(documentPath);
        IProject? project = TryProject(projectPath);

        if (given == null)
        {
            return ResolveFromContext(kind, project);
        }

        string expected = kind == Sch ? ".SchDoc" : ".PcbDoc";
        if (!string.Equals(Path.GetExtension(given), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams,
                $"documentPath '{Path.GetFileName(given)}' is not a {expected} file; {(kind == Sch ? "sch" : "pcb")}.* methods only accept {expected}.",
                new Dictionary<string, string> { ["hint"] = kind == Sch ? "For boards use pcb.* / altium_*_pcb_* tools." : "For sheets use sch.* / altium_*_sheet_* tools." });
        }

        string full;
        if (InputNormalizer.IsBareFileName(given))
        {
            string? inProject = project == null ? null : FindInProject(project, given, kind);
            if (inProject == null)
            {
                throw new BridgeException(BridgeErrorCodes.DocumentNotFound,
                    $"'{given}' is not a document of the {(project == null ? "focused project (none)" : "focused project")}. Pass the full path.",
                    new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths.", ["candidates"] = string.Join("; ", ProjectDocuments(project, kind)) });
            }

            full = inProject;
        }
        else
        {
            try
            {
                full = InputNormalizer.FilePath(given)!;
            }
            catch (ArgumentException ex)
            {
                throw new BridgeException(BridgeErrorCodes.InvalidParams, $"documentPath: {ex.Message}");
            }
        }

        bool loaded = Safe(() => Client.GetDocumentByPath(full)) != null;
        if (!loaded && !File.Exists(full))
        {
            throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"Document file not found: '{full}'.",
                new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths.", ["candidates"] = string.Join("; ", ProjectDocuments(project, kind)) });
        }

        return new ResolvedDocument
        {
            FullPath = full,
            IsLoaded = loaded,
            ProjectPath = project == null ? null : Safe(() => project.DM_ProjectFullPath()),
            ResolvedBy = "explicit",
        };
    }

    private static ResolvedDocument ResolveFromContext(string kind, IProject? project)
    {
        string? projectPath = project == null ? null : Safe(() => project.DM_ProjectFullPath());
        List<string> docs = ProjectDocuments(project, kind);
        string? activePath = ActiveEditorDocument(kind);

        // 1. PCB: the project's primary implementation document, or its only board.
        if (kind == Pcb && project != null)
        {
            IDocument? primary = Safe(() => project.DM_PrimaryImplementationDocument());
            string? primaryPath = primary == null ? null : Safe(() => primary.DM_FullPath());
            if (!string.IsNullOrEmpty(primaryPath) && primaryPath.EndsWith(".PcbDoc", StringComparison.OrdinalIgnoreCase))
            {
                return Make(primaryPath, projectPath, "project primary implementation document");
            }

            if (docs.Count == 1)
            {
                return Make(docs[0], projectPath, "the project's only PcbDoc");
            }
        }

        // 2. The active editor document is a hint only: accepted when it belongs to the project (or there is no project).
        if (activePath != null && (project == null || docs.Any(d => PathEquals(d, activePath))))
        {
            return Make(activePath, projectPath, "active editor document");
        }

        // 3. SCH: a single-sheet project needs no path.
        if (docs.Count == 1)
        {
            return Make(docs[0], projectPath, kind == Sch ? "the project's only SchDoc" : "the project's only PcbDoc");
        }

        if (project == null)
        {
            throw new BridgeException(BridgeErrorCodes.NoActiveProject,
                $"documentPath is required: no project is focused and no {(kind == Sch ? "schematic sheet" : "PCB")} is active in the editor.",
                new Dictionary<string, string> { ["hint"] = "Use workspace.listProjects / project.getStructure and pass documentPath." });
        }

        throw new BridgeException(BridgeErrorCodes.InvalidParams,
            docs.Count == 0
                ? $"The focused project has no {(kind == Sch ? ".SchDoc" : ".PcbDoc")} documents."
                : $"documentPath is required: the focused project has {docs.Count} {(kind == Sch ? "sheets" : "boards")} and the active editor document is not one of them.",
            new Dictionary<string, string> { ["candidates"] = string.Join("; ", docs), ["hint"] = "Pass one of the candidates (full path or bare file name)." });
    }

    private static ResolvedDocument Make(string path, string? projectPath, string by)
    {
        bool loaded = Safe(() => Client.GetDocumentByPath(path)) != null;
        if (!loaded && !File.Exists(path))
        {
            throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"Project document is missing on disk: '{path}'.");
        }

        return new ResolvedDocument { FullPath = path, IsLoaded = loaded, ProjectPath = projectPath, ResolvedBy = by };
    }

    private static IProject? TryProject(string? projectPath)
    {
        string? p = InputNormalizer.Optional(projectPath);
        if (p != null)
        {
            return ResolveProject(p); // throws PROJECT_NOT_FOUND
        }

        IWorkspace? ws = Safe(() => Workspace);
        return ws == null ? null : Safe(() => ws.DM_FocusedProject());
    }

    /// <summary>Full paths of the project's logical documents of the given kind ("SCH" / "PCB").</summary>
    public static List<string> ProjectDocuments(IProject? project, string kind)
    {
        var result = new List<string>();
        if (project == null)
        {
            return result;
        }

        string ext = kind == Sch ? ".SchDoc" : ".PcbDoc";
        int n = Safe(() => project.DM_LogicalDocumentCount());
        for (int i = 0; i < n; i++)
        {
            IDocument? d = Safe(() => project.DM_LogicalDocuments(i));
            string? path = d == null ? null : Safe(() => d.DM_FullPath());
            if (!string.IsNullOrEmpty(path) && path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result;
    }

    private static string? FindInProject(IProject project, string fileName, string kind) =>
        ProjectDocuments(project, kind).FirstOrDefault(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Path of the document in the active editor view if it is of the requested kind, else null.</summary>
    private static string? ActiveEditorDocument(string kind)
    {
        IServerDocumentView? view = Safe(() => Client.GetCurrentView());
        IServerDocument? doc = view == null ? null : Safe(() => view.GetOwnerDocument());
        string? path = doc == null ? null : Safe(() => doc.GetFileName());
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string ext = kind == Sch ? ".SchDoc" : ".PcbDoc";
        return path.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
