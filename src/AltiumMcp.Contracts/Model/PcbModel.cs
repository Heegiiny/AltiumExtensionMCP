using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// PCB object model (PCB editor). All coordinates are in mils relative to the board origin
/// (the same values the PCB editor displays); rotations in degrees counter-clockwise.
/// Component ids are PCB UniqueIds; SourceUniqueId links a PCB component to its compiled
/// schematic component (project.listComponents 'id' / sch.listObjects 'id' for the last segment).
/// </summary>
public sealed class BoardQueryParams
{
    /// <summary>Full path of the .PcbDoc. Omit for the active PCB editor document.</summary>
    public string? DocumentPath { get; set; }
    /// <summary>Load the board hidden if it is not open in an editor (default true).</summary>
    public bool LoadIfClosed { get; set; } = true;
}

public sealed class BoardInfo
{
    public DocumentRef Document { get; set; } = new();
    public bool IsOpenInEditor { get; set; }
    public bool WasLoadedOnDemand { get; set; }
    public string? DisplayUnit { get; set; }
    /// <summary>Board origin in absolute internal coordinates converted to mils (for reference only).</summary>
    public double OriginXMils { get; set; }
    public double OriginYMils { get; set; }
    /// <summary>Board outline bounding box [x1, y1, x2, y2] in mils relative to origin.</summary>
    public double[]? OutlineBounds { get; set; }
    public double? BoardWidthMils { get; set; }
    public double? BoardHeightMils { get; set; }
    public List<LayerInfo> LayerStack { get; set; } = new();
    public int SignalLayerCount { get; set; }
    /// <summary>Primitive counts by object type over the whole board (Component, Pad, Via, Track, Arc, Polygon, Region, Fill, Text, Net, Rule, Class, Violation, ...).</summary>
    public Dictionary<string, int> ObjectCounts { get; set; } = new();
    public List<PcbClassInfo> Classes { get; set; } = new();
    public int RuleCount { get; set; }
    public int ViolationCount { get; set; }
}

public sealed class LayerInfo
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Internal layer id (e.g. TopLayer, MidLayer1, BottomLayer, InternalPlane1, Dielectric...).</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Signal | Plane | Dielectric | Other.</summary>
    public string Kind { get; set; } = string.Empty;
    public double? CopperThicknessMils { get; set; }
    public double? DielectricThicknessMils { get; set; }
    public string? DielectricMaterial { get; set; }
    public double? DielectricConstant { get; set; }
    public bool IsUsed { get; set; }
}

public sealed class PcbClassInfo
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Net | Component | Layer | Pad | FromTo | DifferentialPair | Design channel | Polygon | Structure | xSignal.</summary>
    public string Kind { get; set; } = string.Empty;
    public bool IsSuperClass { get; set; }
    public int MemberCount { get; set; }
}

public sealed class ListPcbComponentsParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>Case-insensitive substring/wildcard on designator, footprint, comment, source lib reference.</summary>
    public string? Filter { get; set; }
    /// <summary>"Top" or "Bottom" to restrict to one side.</summary>
    public string? Layer { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class PcbComponentSummary
{
    public string Id { get; set; } = string.Empty;
    public string Designator { get; set; } = string.Empty;
    /// <summary>UniqueId path of the source schematic component (links to project.listComponents 'id').</summary>
    public string? SourceUniqueId { get; set; }
    public string? Footprint { get; set; }
    public string? Comment { get; set; }
    public string Layer { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double? HeightMils { get; set; }
    /// <summary>Bounding box [x1, y1, x2, y2] of the footprint (excluding designator/comment strings), mils.</summary>
    public double[]? Bounds { get; set; }
    public int PadCount { get; set; }
    public string? SourceLibReference { get; set; }
    public string? SourceDescription { get; set; }
    public string? SourceHierarchicalPath { get; set; }
    public bool IsLocked { get; set; }
    public bool HasDrcError { get; set; }
}

public sealed class PcbComponentListResult
{
    public string DocumentPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<PcbComponentSummary> Items { get; set; } = new();
}

public sealed class GetPcbComponentParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>Designator (e.g. "U1") or PCB UniqueId.</summary>
    public string Component { get; set; } = string.Empty;
}

public sealed class PcbComponentDetail
{
    public string DocumentPath { get; set; } = string.Empty;
    public PcbComponentSummary Summary { get; set; } = new();
    public string? FootprintDescription { get; set; }
    public string? SourceFootprintLibrary { get; set; }
    public string? SourceComponentLibrary { get; set; }
    public string? SourceDesignItemId { get; set; }
    public string? Default3DModel { get; set; }
    public ManagedLink? Managed { get; set; }
    public bool IsBga { get; set; }
    public bool EnablePinSwapping { get; set; }
    public bool EnablePartSwapping { get; set; }
    public bool DesignatorVisible { get; set; }
    public bool CommentVisible { get; set; }
    public List<PcbPadInfo> Pads { get; set; } = new();
    /// <summary>Primitive counts inside the footprint by type (Track, Arc, Region, Fill, Text, ComponentBody ...).</summary>
    public Dictionary<string, int> PrimitiveCounts { get; set; } = new();
}

public sealed class PcbPadInfo
{
    public string Name { get; set; } = string.Empty;
    public string? Net { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public string Layer { get; set; } = string.Empty;
    public bool IsSurfaceMount { get; set; }
    public string? Shape { get; set; }
    public double SizeXMils { get; set; }
    public double SizeYMils { get; set; }
    public double? HoleSizeMils { get; set; }
    public bool Plated { get; set; }
    /// <summary>Designator.Pad, e.g. "U1-3".</summary>
    public string? PinDescriptor { get; set; }
}

public sealed class ListPcbNetsParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    public string? Filter { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class PcbNetSummary
{
    public string Name { get; set; } = string.Empty;
    public int PinCount { get; set; }
    public int ViaCount { get; set; }
    public double RoutedLengthMils { get; set; }
    public bool InDifferentialPair { get; set; }
    /// <summary>
    /// Number of ratsnest connection lines (<c>eConnectionObject</c>) still on this net: 0 = fully routed as far as the
    /// PCB editor knows. Live finding: Altium's <c>IPCB_Net.ConnectivelyInvalid</c> is true for every net whether the
    /// board is hidden-loaded or open in the editor, so it is not exposed; connection objects are the usable signal.
    /// </summary>
    public int UnroutedConnectionCount { get; set; }
}

public sealed class PcbNetListResult
{
    public string DocumentPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<PcbNetSummary> Items { get; set; } = new();
}

public sealed class GetPcbNetParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    public string Net { get; set; } = string.Empty;
}

public sealed class PcbNetDetail
{
    public string DocumentPath { get; set; } = string.Empty;
    public PcbNetSummary Summary { get; set; } = new();
    public List<PcbPadInfo> Pads { get; set; } = new();
    public int TrackCount { get; set; }
    public int ArcCount { get; set; }
    public int PolygonCount { get; set; }
    public int RegionCount { get; set; }
    /// <summary>Layers that carry routing for this net.</summary>
    public List<string> Layers { get; set; } = new();
    /// <summary>Total length of tracks (+ arcs) in mils computed from geometry.</summary>
    public double TrackLengthMils { get; set; }
}

public sealed class ListPcbRulesParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>Filter on rule kind (e.g. "Clearance", "MaxMinWidth", "RoutingVia*") or name; substring/wildcard.</summary>
    public string? Filter { get; set; }
    public bool IncludeDisabled { get; set; } = true;
}

public sealed class PcbRuleInfo
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Priority { get; set; }
    public string? Scope1 { get; set; }
    public string? Scope2 { get; set; }
    /// <summary>Human-readable constraint summary as shown in the rule editor (e.g. "Clearance = 8mil").</summary>
    public string? Summary { get; set; }
    public string? Comment { get; set; }
}

public sealed class PcbRuleListResult
{
    public string DocumentPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public List<PcbRuleInfo> Rules { get; set; } = new();
}

public sealed class ListPcbPrimitivesParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>Types: Track, Arc, Via, Pad, Polygon, Region, Fill, Text, ComponentBody, Dimension, Coordinate, Violation. Default: Track, Arc, Via, Polygon, Region, Fill, Text.</summary>
    public List<string>? Types { get; set; }
    /// <summary>Restrict to one layer by name or id (e.g. "Top Layer", "TopLayer", "Bottom Overlay").</summary>
    public string? Layer { get; set; }
    /// <summary>Restrict to primitives of this net.</summary>
    public string? Net { get; set; }
    /// <summary>Only free primitives (not inside components) when true.</summary>
    public bool FreeOnly { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class PcbPrimitive
{
    public string Type { get; set; } = string.Empty;
    public string Layer { get; set; } = string.Empty;
    public string? Net { get; set; }
    /// <summary>Owning component designator if the primitive belongs to a footprint.</summary>
    public string? Component { get; set; }
    /// <summary>Geometry: track [x1,y1,x2,y2]; via/pad/text/fill [x,y]; arc [cx,cy,r,startAngle,endAngle]; polygon/region bounds [x1,y1,x2,y2].</summary>
    public double[]? Geometry { get; set; }
    public double? WidthMils { get; set; }
    /// <summary>Via: outer diameter; pad: x size.</summary>
    public double? SizeMils { get; set; }
    public double? HoleSizeMils { get; set; }
    public string? Text { get; set; }
    /// <summary>Via span or polygon name etc.</summary>
    public string? Detail { get; set; }
    public bool HasDrcError { get; set; }
}

public sealed class PcbPrimitiveListResult
{
    public string DocumentPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<PcbPrimitive> Items { get; set; } = new();
}
