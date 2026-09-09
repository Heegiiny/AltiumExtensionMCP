using System.Collections.Generic;

namespace AltiumMcp.Contracts.Model;

/// <summary>
/// Schematic sheet object model (SCH editor, per-sheet, with geometry). Coordinates are in mils
/// (1 mil = 1/1000 inch), origin bottom-left of the sheet, as in the Altium editor.
/// Object ids are the schematic UniqueId of the object; a compiled component's UniqueId
/// (project.listComponents) ends with the sheet component's UniqueId, which links the two models.
/// </summary>
public sealed class SheetQueryParams
{
    /// <summary>Full path of the .SchDoc (from project.getStructure). Omit for the active schematic editor sheet.</summary>
    public string? DocumentPath { get; set; }
    /// <summary>Load the sheet hidden if it is not open in an editor (default true). False = DOCUMENT_NOT_OPEN error.</summary>
    public bool LoadIfClosed { get; set; } = true;
}

public sealed class SheetInfo
{
    public DocumentRef Document { get; set; } = new();
    public bool IsOpenInEditor { get; set; }
    public bool WasLoadedOnDemand { get; set; }
    public string? SheetStyle { get; set; }
    public double WidthMils { get; set; }
    public double HeightMils { get; set; }
    public string? UnitSystem { get; set; }
    /// <summary>Object counts by type (first level only: component pins/parameters are not counted).</summary>
    public Dictionary<string, int> ObjectCounts { get; set; } = new();
    /// <summary>Sheet-level (document) parameters, e.g. Title, DrawnBy, SheetNumber.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? TemplateFileName { get; set; }
}

public sealed class ListSheetObjectsParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>
    /// Object types to include. Default (null/empty) = electrical objects: Component, Wire, Bus, BusEntry, Junction,
    /// NetLabel, Port, PowerObject, SheetSymbol, SheetEntry, NoERC, HarnessConnector, HarnessEntry, SignalHarness,
    /// CrossSheetConnector, Blanket, CompileMask. Also available: Label, TextFrame, Note, Line, Polyline, Rectangle,
    /// Arc, Ellipse, Image, Parameter, ParameterSet, Designator, Pin (pins/parameters/designators are listed
    /// as children of components only when explicitly requested).
    /// </summary>
    public List<string>? Types { get; set; }
    /// <summary>Case-insensitive substring/wildcard filter on text/name/designator.</summary>
    public string? Filter { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; } = 200;
}

public sealed class SchObject
{
    /// <summary>Schematic UniqueId (8 chars). Stable while the object exists; empty for some graphics.</summary>
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    /// <summary>Primary text: designator for components, net name for labels/power objects/ports, text for labels.</summary>
    public string? Text { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Rotation in degrees (0/90/180/270) where applicable.</summary>
    public int? Rotation { get; set; }
    public bool IsMirrored { get; set; }
    /// <summary>Bounding box [x1, y1, x2, y2] in mils.</summary>
    public double[]? Bounds { get; set; }
    /// <summary>Polyline vertices [[x,y],...] for wires/buses/polylines.</summary>
    public List<double[]>? Vertices { get; set; }
    /// <summary>Type-specific attributes (component: designator, libReference, comment...; port: ioType, style; etc.).</summary>
    public Dictionary<string, string>? Attributes { get; set; }
    /// <summary>For child objects (pins/parameters): the owner component's designator.</summary>
    public string? Owner { get; set; }
}

public sealed class SheetObjectListResult
{
    public string DocumentPath { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Offset { get; set; }
    public int Returned { get; set; }
    public List<SchObject> Objects { get; set; } = new();
}

public sealed class GetSchComponentParams
{
    public string? DocumentPath { get; set; }
    public bool LoadIfClosed { get; set; } = true;
    /// <summary>
    /// Designator as drawn on the sheet ("U2", also "U2A" for a multi-part symbol), the sheet component UniqueId,
    /// or — when the owning project is compiled — the physical designator / compiled UniqueId from project.*.
    /// </summary>
    public string Component { get; set; } = string.Empty;
}

public sealed class SchComponentDetail
{
    public string DocumentPath { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Designator { get; set; } = string.Empty;
    /// <summary>
    /// Physical designator (after annotation / channel expansion). Taken from the designator object when Altium fills
    /// it, otherwise from the compiled model of the owning project (several, comma-separated, for multi-channel sheets).
    /// Null when the project is not compiled.
    /// </summary>
    public string? PhysicalDesignator { get; set; }
    /// <summary>Compiled component ids (project.* <c>id</c>) that originate from this sheet component; null if not compiled.</summary>
    public List<string>? CompiledIds { get; set; }
    public string? Comment { get; set; }
    public string? Description { get; set; }
    public string? LibReference { get; set; }
    public string? SourceLibraryName { get; set; }
    public string? DesignItemId { get; set; }
    public string? ComponentKind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Rotation { get; set; }
    public bool IsMirrored { get; set; }
    public double[]? Bounds { get; set; }
    public int PartCount { get; set; }
    public int CurrentPartId { get; set; }
    public int DisplayMode { get; set; }
    /// <summary>Managed component (Workspace / Content Vault) links, if the part is managed.</summary>
    public ManagedLink? Managed { get; set; }
    public Dictionary<string, SchParameterInfo> Parameters { get; set; } = new();
    public List<SchPinInfo> Pins { get; set; } = new();
    public List<ImplementationInfo> Implementations { get; set; } = new();
}

public sealed class ManagedLink
{
    public string? VaultGuid { get; set; }
    public string? ItemGuid { get; set; }
    public string? RevisionGuid { get; set; }
    public string? SymbolItemGuid { get; set; }
    public string? SymbolRevisionGuid { get; set; }
}

public sealed class SchParameterInfo
{
    public string Value { get; set; } = string.Empty;
    public bool IsHidden { get; set; }
    public bool IsSystem { get; set; }
    public bool IsRule { get; set; }
}

public sealed class SchPinInfo
{
    public string Designator { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Electrical { get; set; }
    public int PartId { get; set; }
    public bool IsHidden { get; set; }
    /// <summary>Hidden power pins connect to this net implicitly.</summary>
    public string? HiddenNetName { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Rotation { get; set; }
    public double LengthMils { get; set; }
    public string? Description { get; set; }
}
