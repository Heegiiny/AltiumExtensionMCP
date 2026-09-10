using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// sch.select / pcb.select — highlight design objects in the open editor so a human can see what the agent is
/// talking about. Selection is editor state only: design data is not modified and the document is not marked dirty.
/// Coordinates in results are mils (sheet coordinates for SCH, board-origin-relative for PCB).
/// </summary>
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
