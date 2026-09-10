using System;
using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// sch.select / pcb.select — highlight design objects in the open editor so a human can see what the agent is
/// talking about. Selection is editor state only: design data is not modified and the document is not marked dirty.
/// Coordinates in results are mils (sheet coordinates for SCH, board-origin-relative for PCB).
/// </summary>
/// <summary>workspace.getSelection — what the user has selected in the editor (read-only).</summary>
public sealed class GetSelectionParams
{
    /// <summary>Document to read the selection from. Omit for the active editor document.</summary>
    public string? DocumentPath { get; set; }
    /// <summary>Max non-component objects listed (default 100); counts are always complete.</summary>
    public int Limit { get; set; } = 100;
}

public sealed class SelectionResult
{
    public string? DocumentPath { get; set; }
    /// <summary>"SCH" | "PCB" | other document kind | null when nothing is active.</summary>
    public string? EditorKind { get; set; }
    public string? ProjectPath { get; set; }
    public int TotalSelected { get; set; }
    /// <summary>Selected components (SCH symbols / PCB footprints) with stable ids.</summary>
    public List<SelectedComponent>? Components { get; set; } = new();
    /// <summary>Distinct net names touched by the selection (PCB: nets of selected pads/tracks/vias; SCH: labels/ports/power objects/pins with a compiled net).</summary>
    public List<string>? Nets { get; set; } = new();
    /// <summary>Other selected objects (pads, pins, labels, ports, wires…), bounded by limit.</summary>
    public List<SelectedItem>? Objects { get; set; } = new();
    public Dictionary<string, int>? CountsByType { get; set; } = new();
    /// <summary>Hash of the selected object identities: equal revisions = same selection.</summary>
    public string Revision { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public List<string>? Notes { get; set; }
}

public sealed class SelectedComponent
{
    public string Designator { get; set; } = string.Empty;
    /// <summary>SCH: sheet component UniqueId; PCB: footprint UniqueId.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>PCB only: UniqueId path of the source schematic component (use with project.getComponent).</summary>
    public string? SourceUniqueId { get; set; }
    public string? Comment { get; set; }
    public string? LibraryReference { get; set; }
    public string? Footprint { get; set; }
    public string? Layer { get; set; }
    /// <summary>SCH multi-part symbols: the selected part (1-based).</summary>
    public int? PartId { get; set; }
    /// <summary>Pins with their nets ("3=GND"), when the editor can tell (PCB pads; SCH pins with a compiled/hidden net).</summary>
    public List<string>? PinNets { get; set; }
}

public sealed class SelectedItem
{
    public string Type { get; set; } = string.Empty;
    /// <summary>Designator / pad descriptor / label text / port name / string text.</summary>
    public string? Text { get; set; }
    public string? Id { get; set; }
    public string? Net { get; set; }
    public string? Owner { get; set; }
    public string? Layer { get; set; }
}

public sealed class SelectParams
{
    /// <summary>Full path of the .SchDoc / .PcbDoc. Omit for the active editor document.</summary>
    public string? DocumentPath { get; set; }

    /// <summary>Component designators (PCB: "U1"; SCH: logical "U2", part "U2A", physical "U2_1", or sheet UniqueId).</summary>
    public List<string>? Components { get; set; }

    /// <summary>
    /// Net names. PCB: selects the net's pads, tracks, arcs, vias, polygons and regions. SCH: selects net labels,
    /// ports, power objects, sheet entries and cross-sheet connectors carrying that name (wires are not net-aware
    /// on an uncompiled sheet).
    /// </summary>
    public List<string>? Nets { get; set; }

    /// <summary>PCB: pad descriptors "U1-3". SCH: object UniqueIds from sch.listObjects.</summary>
    public List<string>? Objects { get; set; }

    /// <summary>Deselect everything first (default true). With no targets and clearFirst=true the call just clears the selection.</summary>
    public bool ClearFirst { get; set; } = true;

    /// <summary>Zoom the editor view to the selection (default true).</summary>
    public bool ZoomTo { get; set; } = true;

    /// <summary>Open/show the document in its editor and focus it (default true). Selection on a hidden document is invisible.</summary>
    public bool Focus { get; set; } = true;
}

public sealed class SelectResult
{
    public string DocumentPath { get; set; } = string.Empty;
    /// <summary>Number of design objects now selected on the document.</summary>
    public int SelectedCount { get; set; }
    /// <summary>Targets that matched at least one object.</summary>
    public List<string> Matched { get; set; } = new();
    /// <summary>Targets that matched nothing (typos, wrong document, objects of another type).</summary>
    public List<string> NotFound { get; set; } = new();
    /// <summary>Bounding box [x1, y1, x2, y2] of the selection, mils.</summary>
    public double[]? Bounds { get; set; }
    public bool IsOpenInEditor { get; set; }
    public bool Zoomed { get; set; }
    public List<string>? Notes { get; set; }
}
