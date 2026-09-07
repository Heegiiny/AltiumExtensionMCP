using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>Parameters accepted by project.* methods.</summary>
public sealed class ProjectQueryParams
{
    /// <summary>Full path of the project. Null/empty = focused project.</summary>
    public string? ProjectPath { get; set; }

    /// <summary>
    /// If the compiled model is missing/stale, compile before reading. Default false: read-only tools must not
    /// change project state unless asked explicitly.
    /// </summary>
    public bool CompileIfNeeded { get; set; }
}

public sealed class ProjectStructure
{
    public ProjectSummary Project { get; set; } = new();
    /// <summary>Source documents as listed in the project (logical documents), in project order.</summary>
    public List<DocumentInfo> LogicalDocuments { get; set; } = new();
    /// <summary>Generated documents (outputs), if any.</summary>
    public List<DocumentRef> GeneratedDocuments { get; set; } = new();
    /// <summary>Compiled hierarchy (physical documents); empty if project is not compiled.</summary>
    public List<HierarchyNode> Hierarchy { get; set; } = new();
    public List<string> Variants { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
    /// <summary>Compile diagnostics summary from the project's violation list.</summary>
    public ViolationSummary? Violations { get; set; }
    public string? PrimaryImplementationDocument { get; set; }
    public bool IsCompiled { get; set; }
}

public sealed class HierarchyNode
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>Instance name of this sheet in the hierarchy (sheet symbol designator chain), if any.</summary>
    public string? InstanceName { get; set; }
    public int IndentLevel { get; set; }
    public int ComponentCount { get; set; }
    public int NetCount { get; set; }
    public List<HierarchyNode> Children { get; set; } = new();
}

public sealed class ViolationSummary
{
    public int Total { get; set; }
    public Dictionary<string, int> ByErrorLevel { get; set; } = new();
    public Dictionary<string, int> ByErrorKind { get; set; } = new();
    /// <summary>Up to 10 violations, errors/fatals first (warnings only when the total is small).</summary>
    public List<ViolationInfo> Sample { get; set; } = new();
}

public sealed class ViolationInfo
{
    public string? ErrorKind { get; set; }
    public string? ErrorLevel { get; set; }
    public string? Description { get; set; }
    public string? DocumentPath { get; set; }
}

public sealed class ListComponentsParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    /// <summary>
    /// Optional case-insensitive filter on designator, comment, library reference, footprint or description.
    /// Plain text = substring match; with '*' / '?' = wildcard match on the whole field (e.g. "R*", "*0402*").
    /// </summary>
    public string? Filter { get; set; }
    /// <summary>Include per-component parameters (can be large). Default false.</summary>
    public bool IncludeParameters { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class ComponentSummary
{
    /// <summary>Stable id: the component UniqueId (from schematic). Use it in project.getComponent.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Physical (board/BOM) designator, e.g. R1. Falls back to the logical designator for unannotated designs.</summary>
    public string Designator { get; set; } = string.Empty;
    /// <summary>Logical designator as drawn on the schematic sheet (differs from Designator in multi-channel designs).</summary>
    public string? LogicalDesignator { get; set; }
    public string? Comment { get; set; }
    public string? Description { get; set; }
    public string? LibraryReference { get; set; }
    public string? SourceLibraryName { get; set; }
    public string? Footprint { get; set; }
    public string? PartType { get; set; }
    public string? DocumentPath { get; set; }
    public int PinCount { get; set; }
    public int SubPartCount { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public sealed class ComponentListResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<ComponentSummary> Components { get; set; } = new();
}

public sealed class GetComponentParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    /// <summary>Component UniqueId (preferred) or designator.</summary>
    public string Component { get; set; } = string.Empty;
}

public sealed class ComponentDetail
{
    public ComponentSummary Summary { get; set; } = new();
    public List<PinInfo> Pins { get; set; } = new();
    public List<ImplementationInfo> Implementations { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public sealed class PinInfo
{
    public string Number { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Electrical { get; set; }
    /// <summary>Net connected to this pin in the compiled design (null if unconnected).</summary>
    public string? Net { get; set; }
    public int PartId { get; set; }
    public bool IsHidden { get; set; }
}

public sealed class ImplementationInfo
{
    public string? ModelType { get; set; }
    public string? ModelName { get; set; }
    public string? Description { get; set; }
    public bool IsCurrent { get; set; }
}

public sealed class ListNetsParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    public string? Filter { get; set; }
    /// <summary>Include pin list per net (can be large). Default false.</summary>
    public bool IncludePins { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class NetSummary
{
    /// <summary>Net name in the flattened design (stable id for project.getNet).</summary>
    public string Name { get; set; } = string.Empty;
    public int PinCount { get; set; }
    public int PortCount { get; set; }
    public int NetLabelCount { get; set; }
    public int PowerObjectCount { get; set; }
    public bool IsLocal { get; set; }
    public bool IsAutoGenerated { get; set; }
    public string? Electrical { get; set; }
    public string? DocumentPath { get; set; }
    public List<NetPinRef>? Pins { get; set; }
}

public sealed class NetPinRef
{
    public string Designator { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty;
    public string? PinName { get; set; }
    public string? Electrical { get; set; }
}

public sealed class NetListResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<NetSummary> Nets { get; set; } = new();
}

public sealed class GetNetParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    public string Net { get; set; } = string.Empty;
}
