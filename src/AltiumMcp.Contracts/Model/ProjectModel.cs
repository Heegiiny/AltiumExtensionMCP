using System;
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

/// <summary>project.getAgentDocs — project-shipped agent instructions (AGENTS.md and friends).</summary>
public sealed class GetAgentDocsParams
{
    public string? ProjectPath { get; set; }
    /// <summary>Max characters of content returned per file (default 20000); longer files are truncated with a marker.</summary>
    public int MaxCharsPerFile { get; set; } = 20000;
    /// <summary>Include README.md / CLAUDE.md / .cursorrules found next to the project (default true). AGENTS.md is always included.</summary>
    public bool IncludeReadme { get; set; } = true;
}

public sealed class AgentDocsResult
{
    public string ProjectPath { get; set; } = string.Empty;
    /// <summary>Directory of the project file: the root under which project documentation may live.</summary>
    public string ProjectRoot { get; set; } = string.Empty;
    /// <summary>Files found, nearest to the project first (project dir, then parent directories for AGENTS.md).</summary>
    public List<AgentDocFile> Files { get; set; } = new();
    /// <summary>Directories that were searched for AGENTS.md, in order.</summary>
    public List<string> SearchedDirectories { get; set; } = new();
    public List<string>? Notes { get; set; }
}

public sealed class AgentDocFile
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>"agents" (AGENTS.md), "readme", "rules" (CLAUDE.md / .cursorrules).</summary>
    public string Kind { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    /// <summary>True when the file is a member of the Altium project (listed in the .PrjPcb).</summary>
    public bool IsProjectMember { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool Truncated { get; set; }
}

/// <summary>Response detail levels shared by project.getComponent(s) / project.getNet(s).</summary>
public static class DetailLevel
{
    /// <summary>Identity and descriptive fields only (what list tools return).</summary>
    public const string Summary = "summary";
    /// <summary>Summary + connectivity (pins→nets for components; per-sheet segments for nets). Default.</summary>
    public const string Connectivity = "connectivity";
    /// <summary>Everything: parameters, implementations/models, source library, per-item electrical types.</summary>
    public const string Full = "full";

    public static string Normalize(string? value)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v switch
        {
            "" or "default" or "connectivity" or "pins" => Connectivity,
            "summary" or "brief" or "short" => Summary,
            "full" or "all" or "detail" or "verbose" => Full,
            _ => throw new System.ArgumentException($"Unknown detail level '{value}'. Use summary | connectivity | full."),
        };
    }

    public static int Rank(string level) => level == Summary ? 0 : level == Full ? 2 : 1;
}

public sealed class GetComponentParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    /// <summary>Component UniqueId (preferred) or designator (physical or logical).</summary>
    public string? Component { get; set; }
    /// <summary>Batch form: several ids/designators in one call (project.getComponents). Unknown ones land in notFound.</summary>
    public List<string>? Components { get; set; }
    /// <summary>summary | connectivity (default) | full. See <see cref="DetailLevel"/>.</summary>
    public string? Detail { get; set; }
    /// <summary>Per-call override of the "Follow MCP queries in Altium" setting (cross probe to the found object).</summary>
    public bool? CrossProbe { get; set; }
}

public sealed class ComponentDetail
{
    public ComponentSummary Summary { get; set; } = new();
    /// <summary>Pins with the compiled net per pin (connectivity, full).</summary>
    public List<PinInfo>? Pins { get; set; }
    /// <summary>Models (footprint, simulation, 3D…) — full only.</summary>
    public List<ImplementationInfo>? Implementations { get; set; }
    /// <summary>Component parameters — full only.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
    /// <summary>"scheduled" when the call queued an Altium cross probe to this component; null otherwise.</summary>
    public string? CrossProbe { get; set; }
}

public sealed class ComponentBatchResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public List<ComponentDetail> Components { get; set; } = new();
    public List<string> NotFound { get; set; } = new();
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
    /// <summary>
    /// Stable identity of the compiled net: the flattened full net name, made unique when two distinct nets share
    /// a display name (e.g. local nets on different sheets: "NetR1_1@Power.SchDoc"). Use it in project.getNet.
    /// </summary>
    public string NetId { get; set; } = string.Empty;
    /// <summary>Display name of the net in the flattened design.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Compiled scope: Global | Local | Hierarchical | Flat | … (from the net identifier scope rules).</summary>
    public string? Scope { get; set; }
    public int PinCount { get; set; }
    public int PortCount { get; set; }
    public int NetLabelCount { get; set; }
    public int PowerObjectCount { get; set; }
    public int SheetEntryCount { get; set; }
    public bool IsLocal { get; set; }
    public bool IsAutoGenerated { get; set; }
    public string? Electrical { get; set; }
    /// <summary>Every sheet that carries part of this net (from the owner documents of all net items).</summary>
    public List<string> DocumentPaths { get; set; } = new();
    /// <summary>Per-sheet participation (connectivity, full). The caller's documentPath, if any, is listed first.</summary>
    public List<NetSegment>? Segments { get; set; }
    /// <summary>"scheduled" when the call queued an Altium cross probe to this net; null otherwise.</summary>
    public string? CrossProbe { get; set; }
}

/// <summary>What a net looks like on one sheet: pins, ports, labels, power objects, sheet entries, off-sheet connectors.</summary>
public sealed class NetSegment
{
    public string DocumentPath { get; set; } = string.Empty;
    /// <summary>Component pins on this sheet; absent when the sheet only carries the net through ports/power objects.</summary>
    public List<NetPinRef>? Pins { get; set; }
    /// <summary>Port names on this sheet that carry the net (sheet-to-sheet transitions via ports).</summary>
    public List<string>? Ports { get; set; }
    public List<string>? NetLabels { get; set; }
    /// <summary>Power port texts (exact) on this sheet, e.g. "GND", "+3V3".</summary>
    public List<string>? PowerObjects { get; set; }
    /// <summary>Sheet entries (on sheet symbols placed on this sheet) that carry the net into child sheets.</summary>
    public List<string>? SheetEntries { get; set; }
    public List<string>? CrossSheetConnectors { get; set; }
}

public sealed class NetPinRef
{
    public string Designator { get; set; } = string.Empty;
    public string Pin { get; set; } = string.Empty;
    public string? PinName { get; set; }
    /// <summary>Pin electrical type — full detail only.</summary>
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
    /// <summary>Net id (netId from list/get results) or display name.</summary>
    public string? Net { get; set; }
    /// <summary>Batch form: several ids/names in one call (project.getNets). Unknown ones land in notFound.</summary>
    public List<string>? Nets { get; set; }
    /// <summary>summary | connectivity (default) | full.</summary>
    public string? Detail { get; set; }
    /// <summary>
    /// Optional sheet: puts that sheet's segment first and, for ambiguous names, prefers the net present on that
    /// sheet. It never restricts the result to one sheet — project nets are always returned whole.
    /// </summary>
    public string? DocumentPath { get; set; }
    /// <summary>Per-call override of the "Follow MCP queries in Altium" setting.</summary>
    public bool? CrossProbe { get; set; }
}

public sealed class NetBatchResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public List<NetSummary> Nets { get; set; } = new();
    public List<string> NotFound { get; set; } = new();
}

/// <summary>Candidate returned with AMBIGUOUS_OBJECT when several distinct compiled nets share a display name.</summary>
public sealed class NetCandidate
{
    public string NetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Scope { get; set; }
    public int PinCount { get; set; }
    public List<string> DocumentPaths { get; set; } = new();
}

/// <summary>Bounded connectivity walk over the compiled model (project.trace).</summary>
public sealed class TraceParams
{
    public string? ProjectPath { get; set; }
    public bool CompileIfNeeded { get; set; }
    /// <summary>Start object: component designator / UniqueId, or a net id / name.</summary>
    public string? Start { get; set; }
    /// <summary>"component" | "net" | null (auto: component first, then net).</summary>
    public string? StartKind { get; set; }
    /// <summary>Hops from the start: 1 = the start's immediate nets and the components on them (default); max 3.</summary>
    public int Depth { get; set; } = 1;
    /// <summary>Nets never expanded (by name or id), e.g. ["GND", "+3V3"]. Power nets are auto-excluded unless includePowerNets.</summary>
    public List<string>? ExcludeNets { get; set; }
    /// <summary>Expand nets that carry power objects (default false: they are listed but not walked through).</summary>
    public bool IncludePowerNets { get; set; }
    /// <summary>Nets with more pins than this are listed but not expanded (default 30).</summary>
    public int MaxFanout { get; set; } = 30;
    /// <summary>Hard bounds on the result size (defaults 60 nets / 120 components).</summary>
    public int MaxNets { get; set; } = 60;
    public int MaxComponents { get; set; } = 120;
}

public sealed class TraceResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string StartKind { get; set; } = string.Empty;
    public int Depth { get; set; }
    /// <summary>Nets reached, with the hop level (0 = start net) and compact pin list "U1.3".</summary>
    public List<TraceNet> Nets { get; set; } = new();
    /// <summary>Components reached, with the hop level (0 = start component) and their pins on traced nets as "pin=net".</summary>
    public List<TraceComponent> Components { get; set; } = new();
    /// <summary>True when a bound (maxNets / maxComponents) cut the walk short.</summary>
    public bool Truncated { get; set; }
    public List<string>? Notes { get; set; }
}

public sealed class TraceNet
{
    public string NetId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public int PinCount { get; set; }
    /// <summary>"U1.3" style references (all pins when expanded; absent when the net was listed but not expanded).</summary>
    public List<string>? Pins { get; set; }
    /// <summary>Why the net was not expanded: "power" | "excluded" | "fanout" | "depth"; null when expanded.</summary>
    public string? NotExpanded { get; set; }
}

public sealed class TraceComponent
{
    public string Designator { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public string? LibraryReference { get; set; }
    public string? DocumentPath { get; set; }
    public int Level { get; set; }
    /// <summary>"pinNumber=netName" for the pins on traced nets.</summary>
    public List<string> Pins { get; set; } = new();
}
