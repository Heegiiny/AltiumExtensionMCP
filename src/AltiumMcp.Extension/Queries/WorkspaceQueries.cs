using System;
using System.Collections.Generic;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
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

    /// <summary>
    /// Reads the user's editor selection (workspace.getSelection). Read-only. Selection is only meaningful when the
    /// user explicitly refers to it — the skill enforces that rule; this method just reports the facts.
    /// </summary>
    /// <summary>Full paths of all documents currently open in editor windows (any server module).</summary>
    private static List<string> OpenEditorDocumentPaths()
    {
        var paths = new List<string>();
        IClient client = Client;
        int moduleCount = Safe(() => client.GetCount());
        for (int i = 0; i < moduleCount; i++)
        {
            IServerModule? m = Safe(() => client.GetServerModule(i));
            if (m == null) continue;
            int docCount = Safe(() => m.GetDocumentCount());
            for (int j = 0; j < docCount; j++)
            {
                IServerDocument? sd = Safe(() => m.GetDocuments(j));
                string? path = sd == null ? null : Safe(() => sd.GetFileName());
                if (!string.IsNullOrEmpty(path) && !paths.Contains(path!, StringComparer.OrdinalIgnoreCase)) paths.Add(path!);
            }
        }

        return paths;
    }

    public SelectionResult GetSelection(GetSelectionParams? p)    {
        p ??= new GetSelectionParams();
        int limit = InputNormalizer.Limit(p.Limit, 100, 1000);
        var result = new SelectionResult { Timestamp = DateTimeOffset.Now };

        string path;
        string? given = InputNormalizer.Optional(p.DocumentPath);
        if (given != null)
        {
            if (InputNormalizer.IsBareFileName(given))
            {
                // A bare file name denotes an open editor document with that name (unique match required).
                List<string> hits = OpenEditorDocumentPaths()
                    .Where(d => string.Equals(System.IO.Path.GetFileName(d), given, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hits.Count == 1)
                {
                    given = hits[0];
                }
                else if (hits.Count > 1)
                {
                    throw new BridgeException(BridgeErrorCodes.AmbiguousObject, $"Several open documents are named '{given}'; pass the full path.",
                        new Dictionary<string, string> { ["candidates"] = string.Join(" | ", hits) });
                }
            }

            try
            {
                path = InputNormalizer.FilePath(given)!;
            }
            catch (ArgumentException ex)
            {
                throw new BridgeException(BridgeErrorCodes.InvalidParams, $"documentPath: {ex.Message}");
            }

            if (Safe(() => Client.GetDocumentByPath(path)) == null)
            {
                throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, $"'{path}' is not open in an editor, so it has no selection.",
                    new Dictionary<string, string> { ["hint"] = "Omit documentPath to read the active editor document, or pass an open document's full path (workspace.listOpenDocuments)." });
            }
        }
        else
        {
            IServerDocumentView? view = Safe(() => Client.GetCurrentView());
            IServerDocument? doc = view == null ? null : Safe(() => view.GetOwnerDocument());
            string? active = doc == null ? null : NullIfEmpty(Safe(() => doc.GetFileName()));
            if (active == null)
            {
                result.Notes = new List<string> { "No document is active in the editor; there is no selection to read." };
                result.Revision = "empty";
                return result;
            }

            path = active;
        }

        result.DocumentPath = path;
        string ext = System.IO.Path.GetExtension(path);
        result.EditorKind = ext.Equals(".SchDoc", StringComparison.OrdinalIgnoreCase) ? "SCH"
            : ext.Equals(".PcbDoc", StringComparison.OrdinalIgnoreCase) ? "PCB"
            : (Safe(() => Client.GetDocumentByPath(path)) is { } sd ? Safe(() => sd.GetKind()) : null) ?? ext.TrimStart('.');

        IWorkspace? ws = Safe(() => Workspace);
        IDocument? dm = ws == null ? null : Safe(() => ws.DM_GetDocumentFromPath(path));
        IProject? owner = dm == null ? null : Safe(() => dm.DM_Project());
        result.ProjectPath = owner == null ? null : NullIfEmpty(Safe(() => owner.DM_ProjectFullPath()));

        switch (result.EditorKind)
        {
            case "SCH":
                SchematicQueries.ReadSelection(path, result, limit);
                break;
            case "PCB":
                PcbQueries.ReadSelection(path, result, limit);
                break;
            default:
                result.Notes = new List<string> { $"Selection reading is supported for schematic sheets and PCBs only (active document kind: {result.EditorKind})." };
                result.Revision = "unsupported";
                break;
        }

        if (result.Components!.Count > 0)
        {
            result.Notes ??= new List<string>();
            result.Notes.Add(result.EditorKind == "PCB"
                ? "For compiled facts (pins→nets across the project) call project.getComponent with sourceUniqueId or designator."
                : "For compiled facts (pins→nets across the project) call project.getComponent with id or designator.");
        }

        // Empty collections are noise for the agent; omit them (serializer drops nulls).
        if (result.Components.Count == 0) result.Components = null;
        if (result.Nets!.Count == 0) result.Nets = null;
        if (result.Objects!.Count == 0) result.Objects = null;
        if (result.CountsByType!.Count == 0) result.CountsByType = null;
        return result;
    }

    /// <summary>
    /// Opens (or brings forward) a document in its editor. Editor-state only: no design data is touched.
    /// Uses IClient.OpenDocumentShowOrHide(kind, path, showInTree) + ShowDocument/ShowDocumentDontFocus —
    /// the same API the Projects panel uses, so it works from a non-interactive bridge call.
    /// </summary>
    public OpenDocumentResult OpenDocument(OpenDocumentParams? p)
    {
        string? given = p == null ? null : InputNormalizer.Optional(p.DocumentPath);
        if (p == null || given == null)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "documentPath is required (a placeholder such as \"None\" counts as missing).");
        }

        string full;
        if (InputNormalizer.IsBareFileName(given))
        {
            // Bare file name: resolve against the focused project's documents (any kind).
            IProject? focused = Safe(() => Workspace.DM_FocusedProject());
            string? match = null;
            int n = focused == null ? 0 : Safe(() => focused.DM_LogicalDocumentCount());
            for (int i = 0; i < n && match == null; i++)
            {
                IDocument? d = Safe(() => focused!.DM_LogicalDocuments(i));
                string? path = d == null ? null : Safe(() => d.DM_FullPath());
                if (path != null && string.Equals(System.IO.Path.GetFileName(path), given, StringComparison.OrdinalIgnoreCase))
                {
                    match = path;
                }
            }

            full = match ?? throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"'{given}' is not a document of the focused project. Pass the full path.",
                new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths." });
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

        IClient client = Client;
        IServerDocument? existing = Safe(() => client.GetDocumentByPath(full));
        bool wasOpen = existing != null && Safe(() => existing.GetIsShown());

        IServerDocument? doc = existing;
        if (doc == null)
        {
            if (!System.IO.File.Exists(full))
            {
                throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"Document file not found: '{full}'.",
                    new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths." });
            }

            string kind = Safe(() => client.GetDocumentKindFromDocumentPath(full)) ?? string.Empty;
            if (string.IsNullOrEmpty(kind))
            {
                throw new BridgeException(BridgeErrorCodes.Unsupported, $"Altium has no editor registered for '{System.IO.Path.GetExtension(full)}' files.");
            }

            doc = Safe(() => client.OpenDocumentShowOrHide(kind, full, true));
            if (doc == null)
            {
                throw new BridgeException(BridgeErrorCodes.AltiumApiError, $"Altium could not open '{full}' (kind {kind}).");
            }
        }

        if (p.Focus)
        {
            Safe(() => { client.ShowDocument(doc); return true; });
        }
        else
        {
            Safe(() => { client.ShowDocumentDontFocus(doc); return true; });
        }

        IWorkspace? ws = Safe(() => Workspace);
        string? projectPath = null;
        if (ws != null)
        {
            IDocument? dm = Safe(() => ws.DM_GetDocumentFromPath(full));
            IProject? owner = dm == null ? null : Safe(() => dm.DM_Project());
            projectPath = owner == null ? null : NullIfEmpty(Safe(() => owner.DM_ProjectFullPath()));
        }

        IServerDocumentView? view = Safe(() => client.GetCurrentView());
        IServerDocument? current = view == null ? null : Safe(() => view.GetOwnerDocument());
        string? currentPath = current == null ? null : Safe(() => current.GetFileName());

        return new OpenDocumentResult
        {
            Document = new DocumentRef
            {
                Path = Safe(() => doc.GetFileName()) ?? full,
                FileName = System.IO.Path.GetFileName(full),
                Kind = Safe(() => doc.GetKind()) ?? string.Empty,
                ProjectPath = projectPath,
            },
            WasAlreadyOpen = wasOpen,
            IsOpenInEditor = Safe(() => doc.GetIsShown()),
            IsFocused = PathEquals(full, currentPath),
        };
    }
}
