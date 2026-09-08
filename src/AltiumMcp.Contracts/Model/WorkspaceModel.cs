using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>Workspace (project group / *.DsnWrk) level info.</summary>
public sealed class WorkspaceInfo
{
    public string? WorkspaceFileName { get; set; }
    public string? WorkspaceFullPath { get; set; }
    public int ProjectCount { get; set; }
    public int InstalledLibraryCount { get; set; }
    /// <summary>Project that currently has focus in the Projects panel (null if none).</summary>
    public ProjectRef? FocusedProject { get; set; }
    /// <summary>Document that currently has focus (null if none).</summary>
    public DocumentRef? FocusedDocument { get; set; }
    /// <summary>Full path of the document displayed in the active editor view (Client.GetCurrentView), if any.</summary>
    public string? ActiveViewDocumentPath { get; set; }
}

/// <summary>Stable reference to a project: full path is the identifier.</summary>
public sealed class ProjectRef
{
    /// <summary>Full path of the project file; use as the stable id in other calls.</summary>
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Project kind derived from file extension: PrjPcb, PrjScr, LibPkg, PrjMbd, FreeDocuments, ...</summary>
    public string Kind { get; set; } = string.Empty;
    public bool IsFocused { get; set; }
}

public sealed class ProjectSummary
{
    public ProjectRef Ref { get; set; } = new();
    public int LogicalDocumentCount { get; set; }
    public int PhysicalDocumentCount { get; set; }
    public int GeneratedDocumentCount { get; set; }
    public int VariantCount { get; set; }
    public bool NeedsCompile { get; set; }
    public bool InCompilation { get; set; }
    public string? ManagedProjectGuid { get; set; }
    public string? OutputPath { get; set; }
}

/// <summary>Stable reference to a document: full path is the identifier.</summary>
public sealed class DocumentRef
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Altium document kind: SCH, PCB, PCBLIB, SCHLIB, OUTPUTJOB, TEXT, ...</summary>
    public string Kind { get; set; } = string.Empty;
    public string? ProjectPath { get; set; }
}

public sealed class DocumentInfo
{
    public DocumentRef Ref { get; set; } = new();
    /// <summary>True if the document is loaded into memory (open in an editor or loaded by the compiler).</summary>
    public bool IsLoaded { get; set; }
    /// <summary>True if the document is open in an editor tab.</summary>
    public bool IsOpenInEditor { get; set; }
    public bool IsModified { get; set; }
    public bool IsFocused { get; set; }
    public int? ComponentCount { get; set; }
    public int? NetCount { get; set; }
    public int? SheetSymbolCount { get; set; }
    public int? PortCount { get; set; }
    /// <summary>Hierarchy indent level in the compiled tree (0 = top).</summary>
    public int? IndentLevel { get; set; }
    public bool? IsPrimaryImplementationDocument { get; set; }
}

public sealed class OpenDocumentsResult
{
    public List<DocumentInfo> Documents { get; set; } = new();
    public string? FocusedDocumentPath { get; set; }
}

public sealed class ProjectListResult
{
    public List<ProjectSummary> Projects { get; set; } = new();
    public string? FocusedProjectPath { get; set; }
}

public sealed class OpenDocumentParams
{
    /// <summary>Full path of the document to open/show in its editor.</summary>
    public string DocumentPath { get; set; } = string.Empty;
    /// <summary>When true (default) the editor tab is focused; false shows it without stealing focus.</summary>
    public bool Focus { get; set; } = true;
}

public sealed class OpenDocumentResult
{
    public DocumentRef Document { get; set; } = new();
    /// <summary>True when the document was already open in an editor before this call.</summary>
    public bool WasAlreadyOpen { get; set; }
    public bool IsOpenInEditor { get; set; }
    public bool IsFocused { get; set; }
}
