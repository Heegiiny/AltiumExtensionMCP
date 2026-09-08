using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using SCH;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Schematic sheet reads through the SCH editor object model (ISch_Document + iterators).
/// Unlike the compiled model (ProjectQueries) this is per sheet and carries geometry.
/// Coordinates are converted from internal units (1/10000 mil) to mils.
/// </summary>
internal sealed class SchematicQueries
{
    private const int MaxLimit = 2000;

    /// <summary>Electrical/connectivity objects returned by sch.listObjects when no types are given.</summary>
    private static readonly TObjectId[] DefaultTypes =
    {
        TObjectId.eSchComponent, TObjectId.eWire, TObjectId.eBus, TObjectId.eBusEntry, TObjectId.eJunction,
        TObjectId.eNetLabel, TObjectId.ePort, TObjectId.ePowerObject, TObjectId.eSheetSymbol, TObjectId.eSheetEntry,
        TObjectId.eNoERC, TObjectId.eHarnessConnector, TObjectId.eHarnessEntry, TObjectId.eSignalHarness,
        TObjectId.eCrossSheetConnector, TObjectId.eBlanket, TObjectId.eCompileMask,
    };

    /// <summary>Types that live inside components rather than on the sheet.</summary>
    private static readonly HashSet<TObjectId> ChildTypes = new() { TObjectId.ePin, TObjectId.eParameter, TObjectId.eDesignator, TObjectId.eImplementation };

    private static readonly Dictionary<string, TObjectId> TypeByName = BuildTypeMap();

    // ---- sch.getSheet ---------------------------------------------------------------------------------------------

    public SheetInfo GetSheet(SheetQueryParams? p)
    {
        p ??= new SheetQueryParams();
        (ISch_Document doc, string path, bool loaded) = ResolveSheet(p.DocumentPath, p.LoadIfClosed);

        var info = new SheetInfo
        {
            Document = new DocumentRef { Path = path, FileName = Path.GetFileName(path), Kind = "SCH" },
            IsOpenInEditor = Safe(() => Client.GetDocumentByPath(path)) != null,
            WasLoadedOnDemand = loaded,
            SheetStyle = EnumName(Safe(() => doc.GetState_SheetStyle().ToString())),
            WidthMils = Mils(Safe(() => doc.GetState_SheetSizeX())),
            HeightMils = Mils(Safe(() => doc.GetState_SheetSizeY())),
            UnitSystem = EnumName(Safe(() => doc.GetState_UnitSystem().ToString())),
            TemplateFileName = NullIfEmpty(Safe(() => doc.GetState_TemplateFileName())),
        };

        // Counts by type, first level only.
        foreach (ISch_BasicContainer o in Iterate(doc, null, TIterationDepth.eIterateFirstLevel))
        {
            string type = TypeName(Safe(() => o.GetState_ObjectId()));
            info.ObjectCounts[type] = info.ObjectCounts.TryGetValue(type, out int n) ? n + 1 : 1;
            if (o is ISch_Parameter prm)
            {
                string? name = Safe(() => prm.GetState_Name());
                if (!string.IsNullOrEmpty(name))
                {
                    info.Parameters[name] = Safe(() => prm.GetState_Text()) ?? string.Empty;
                }
            }
        }

        return info;
    }

    // ---- sch.listObjects ------------------------------------------------------------------------------------------

    public SheetObjectListResult ListObjects(ListSheetObjectsParams? p)
    {
        p ??= new ListSheetObjectsParams();
        (ISch_Document doc, string path, _) = ResolveSheet(p.DocumentPath, p.LoadIfClosed);

        var wanted = new HashSet<TObjectId>();
        if (p.Types == null || p.Types.Count == 0)
        {
            wanted.UnionWith(DefaultTypes);
        }
        else
        {
            foreach (string t in p.Types)
            {
                if (!TypeByName.TryGetValue(NormalizeTypeName(t), out TObjectId id))
                {
                    throw new BridgeException(BridgeErrorCodes.InvalidParams, $"Unknown schematic object type '{t}'.",
                        new Dictionary<string, string> { ["knownTypes"] = string.Join(", ", TypeByName.Keys.OrderBy(k => k)) });
                }

                wanted.Add(id);
            }
        }

        var topLevel = new TObjectSet(wanted.Where(t => !ChildTypes.Contains(t)).ToArray());
        var children = new TObjectSet(wanted.Where(ChildTypes.Contains).ToArray());
        bool listComponents = wanted.Contains(TObjectId.eSchComponent);
        if (children.Count > 0)
        {
            topLevel.Add(TObjectId.eSchComponent);
        }

        var all = new List<SchObject>();
        if (topLevel.Count > 0)
        {
            foreach (ISch_BasicContainer o in Iterate(doc, topLevel, TIterationDepth.eIterateFirstLevel))
            {
                TObjectId id = Safe(() => o.GetState_ObjectId());
                if (id == TObjectId.eSchComponent && o is ISch_Component comp)
                {
                    SchObject c = ToObject(o, id, null);
                    if (listComponents && Matches(p.Filter, c))
                    {
                        all.Add(c);
                    }

                    if (children.Count > 0)
                    {
                        foreach (ISch_BasicContainer child in Iterate(comp, children, TIterationDepth.eIterateAllLevels))
                        {
                            SchObject so = ToObject(child, Safe(() => child.GetState_ObjectId()), c.Text);
                            if (Matches(p.Filter, so))
                            {
                                all.Add(so);
                            }
                        }
                    }

                    continue;
                }

                SchObject obj = ToObject(o, id, null);
                if (Matches(p.Filter, obj))
                {
                    all.Add(obj);
                }
            }
        }

        int limit = Math.Clamp(p.Limit <= 0 ? 200 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<SchObject> page = all.Skip(offset).Take(limit).ToList();
        return new SheetObjectListResult
        {
            DocumentPath = path,
            Total = all.Count,
            Offset = offset,
            Returned = page.Count,
            Objects = page,
        };
    }

    // ---- sch.getComponent -----------------------------------------------------------------------------------------

    public SchComponentDetail GetComponent(GetSchComponentParams? p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Component))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'component' (designator or sheet UniqueId) is required.");
        }

        (ISch_Document doc, string path, _) = ResolveSheet(p.DocumentPath, p.LoadIfClosed);

        ISch_Component? found = null;
        foreach (ISch_BasicContainer o in Iterate(doc, new TObjectSet(TObjectId.eSchComponent), TIterationDepth.eIterateFirstLevel))
        {
            if (o is not ISch_Component c)
            {
                continue;
            }

            ISch_Designator? d = Safe(() => c.GetState_SchDesignator());
            string? logical = d == null ? null : Safe(() => d.GetState_Text());
            string? physical = d == null ? null : Safe(() => d.GetState_PhysicalDesignator());
            string? uid = Safe(() => c.GetState_UniqueId());
            if (Eq(logical, p.Component) || Eq(physical, p.Component) || Eq(uid, p.Component))
            {
                found = c;
                break;
            }
        }

        if (found == null)
        {
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Component '{p.Component}' not found on sheet '{Path.GetFileName(path)}'.",
                new Dictionary<string, string> { ["hint"] = "Use sch.listObjects with types=[\"Component\"] to see designators on this sheet, or project.listComponents for the whole project." });
        }

        ISch_Designator? des = Safe(() => found.GetState_SchDesignator());
        ISch_Parameter? comment = Safe(() => found.GetState_SchComment());
        Point? loc = Safe(() => found.GetState_Location());
        var detail = new SchComponentDetail
        {
            DocumentPath = path,
            Id = Safe(() => found.GetState_UniqueId()) ?? string.Empty,
            Designator = (des == null ? null : Safe(() => des.GetState_Text())) ?? string.Empty,
            PhysicalDesignator = des == null ? null : NullIfEmpty(Safe(() => des.GetState_PhysicalDesignator())),
            Comment = comment == null ? null : NullIfEmpty(Safe(() => comment.GetState_Text())),
            Description = NullIfEmpty(Safe(() => found.GetState_ComponentDescription())),
            LibReference = NullIfEmpty(Safe(() => found.GetState_LibReference())),
            SourceLibraryName = NullIfEmpty(Safe(() => found.GetState_SourceLibraryName())),
            DesignItemId = NullIfEmpty(Safe(() => found.GetState_DesignItemId())),
            ComponentKind = EnumName(Safe(() => found.GetState_ComponentKind().ToString())),
            X = Mils(loc?.X ?? 0),
            Y = Mils(loc?.Y ?? 0),
            Rotation = Degrees(Safe(() => found.GetState_Orientation())),
            IsMirrored = Safe(() => found.GetState_IsMirrored()),
            Bounds = BoundsOf(found),
            PartCount = Safe(() => found.GetState_PartCountNoPart0()),
            CurrentPartId = Safe(() => found.GetState_CurrentPartID()),
            DisplayMode = Safe(() => found.GetState_DisplayMode()),
        };

        string? vault = NullIfEmpty(Safe(() => found.GetState_VaultGUID()));
        string? item = NullIfEmpty(Safe(() => found.GetState_ItemGUID()));
        if (vault != null || item != null)
        {
            detail.Managed = new ManagedLink
            {
                VaultGuid = vault,
                ItemGuid = item,
                RevisionGuid = NullIfEmpty(Safe(() => found.GetState_RevisionGUID())),
                SymbolItemGuid = NullIfEmpty(Safe(() => found.GetState_SymbolItemGUID())),
                SymbolRevisionGuid = NullIfEmpty(Safe(() => found.GetState_SymbolRevisionGUID())),
            };
        }

        foreach (ISch_BasicContainer o in Iterate(found, new TObjectSet(TObjectId.eParameter), TIterationDepth.eIterateFirstLevel))
        {
            if (o is not ISch_Parameter prm)
            {
                continue;
            }

            string? name = Safe(() => prm.GetState_Name());
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            detail.Parameters[name] = new SchParameterInfo
            {
                Value = Safe(() => prm.GetState_Text()) ?? string.Empty,
                IsHidden = Safe(() => prm.GetState_IsHidden()),
                IsSystem = Safe(() => prm.GetState_IsSystemParameter()),
                IsRule = Safe(() => prm.GetState_IsRule()),
            };
        }

        foreach (ISch_BasicContainer o in Iterate(found, new TObjectSet(TObjectId.ePin), TIterationDepth.eIterateFirstLevel))
        {
            if (o is not ISch_Pin pin)
            {
                continue;
            }

            Point? pl = Safe(() => pin.GetState_Location());
            detail.Pins.Add(new SchPinInfo
            {
                Designator = Safe(() => pin.GetState_Designator()) ?? string.Empty,
                Name = NullIfEmpty(Safe(() => pin.GetState_Name())),
                Electrical = Electrical(pin),
                PartId = Safe(() => pin.GetState_OwnerPartId()),
                IsHidden = Safe(() => pin.GetState_IsHidden()),
                HiddenNetName = NullIfEmpty(Safe(() => pin.GetState_HiddenNetName())),
                X = Mils(pl?.X ?? 0),
                Y = Mils(pl?.Y ?? 0),
                Rotation = Degrees(Safe(() => pin.GetState_Orientation())),
                LengthMils = Mils(Safe(() => pin.GetState_PinLength())),
                Description = NullIfEmpty(Safe(() => pin.GetState_Description())),
            });
        }

        detail.Pins.Sort((a, b) => NaturalCompare(a.Designator, b.Designator));

        foreach (ISch_BasicContainer o in Iterate(found, new TObjectSet(TObjectId.eImplementation), TIterationDepth.eIterateAllLevels))
        {
            if (o is not ISch_Implementation impl)
            {
                continue;
            }

            detail.Implementations.Add(new ImplementationInfo
            {
                ModelType = NullIfEmpty(Safe(() => impl.GetState_ModelType())),
                ModelName = NullIfEmpty(Safe(() => impl.GetState_ModelName())),
                Description = NullIfEmpty(Safe(() => impl.GetState_Description())),
                IsCurrent = Safe(() => impl.GetState_IsCurrent()),
            });
        }

        return detail;
    }

    // ---- object conversion ----------------------------------------------------------------------------------------

    private static SchObject ToObject(ISch_BasicContainer o, TObjectId id, string? owner)
    {
        var so = new SchObject
        {
            Id = Safe(() => o.GetState_UniqueId()) ?? string.Empty,
            Type = TypeName(id),
            Owner = owner,
        };

        if (o is ISch_GraphicalObject g)
        {
            Point? loc = Safe(() => g.GetState_Location());
            so.X = Mils(loc?.X ?? 0);
            so.Y = Mils(loc?.Y ?? 0);
        }

        Dictionary<string, string> a = new();
        switch (id)
        {
            case TObjectId.eSchComponent when o is ISch_Component c:
            {
                ISch_Designator? d = Safe(() => c.GetState_SchDesignator());
                so.Text = d == null ? null : Safe(() => d.GetState_Text());
                so.Rotation = Degrees(Safe(() => c.GetState_Orientation()));
                so.IsMirrored = Safe(() => c.GetState_IsMirrored());
                so.Bounds = BoundsOf(c);
                ISch_Parameter? cm = Safe(() => c.GetState_SchComment());
                Put(a, "comment", cm == null ? null : Safe(() => cm.GetState_Text()));
                Put(a, "libReference", Safe(() => c.GetState_LibReference()));
                Put(a, "description", Safe(() => c.GetState_ComponentDescription()));
                Put(a, "designItemId", Safe(() => c.GetState_DesignItemId()));
                int parts = Safe(() => c.GetState_PartCountNoPart0());
                if (parts > 1)
                {
                    a["partCount"] = parts.ToString();
                    a["currentPartId"] = Safe(() => c.GetState_CurrentPartID()).ToString();
                }

                break;
            }

            case TObjectId.ePin when o is ISch_Pin pin:
                so.Text = Safe(() => pin.GetState_Designator());
                so.Rotation = Degrees(Safe(() => pin.GetState_Orientation()));
                Put(a, "name", Safe(() => pin.GetState_Name()));
                Put(a, "electrical", Electrical(pin));
                if (Safe(() => pin.GetState_IsHidden())) a["hidden"] = "true";
                Put(a, "hiddenNetName", Safe(() => pin.GetState_HiddenNetName()));
                a["partId"] = Safe(() => pin.GetState_OwnerPartId()).ToString();
                break;

            case TObjectId.eParameter when o is ISch_Parameter prm:
                so.Text = Safe(() => prm.GetState_Name());
                Put(a, "value", Safe(() => prm.GetState_Text()));
                if (Safe(() => prm.GetState_IsHidden())) a["hidden"] = "true";
                break;

            case TObjectId.eDesignator when o is ISch_Designator des:
                so.Text = Safe(() => des.GetState_Text());
                Put(a, "physicalDesignator", Safe(() => des.GetState_PhysicalDesignator()));
                break;

            case TObjectId.eImplementation when o is ISch_Implementation impl:
                so.Text = Safe(() => impl.GetState_ModelName());
                Put(a, "modelType", Safe(() => impl.GetState_ModelType()));
                if (Safe(() => impl.GetState_IsCurrent())) a["current"] = "true";
                break;

            case TObjectId.ePort when o is ISch_Port port:
                so.Text = Safe(() => port.GetState_Name());
                Put(a, "ioType", EnumName(Safe(() => port.GetState_IOType().ToString()))?.Replace("Port", string.Empty));
                Put(a, "style", EnumName(Safe(() => port.GetState_Style().ToString())));
                a["widthMils"] = Mils(Safe(() => port.GetState_Width())).ToString("0.###");
                Put(a, "harnessType", Safe(() => port.GetState_CrossRef()));
                break;

            case TObjectId.eSheetEntry when o is ISch_SheetEntry se:
                so.Text = Safe(() => se.GetState_Name());
                break;

            case TObjectId.eSheetSymbol when o is ISch_SheetSymbol ss:
            {
                ISch_SheetName? sn = Safe(() => ss.GetState_SchSheetName());
                ISch_SheetFileName? sf = Safe(() => ss.GetState_SchSheetFileName());
                so.Text = sn == null ? null : Safe(() => sn.GetState_Text());
                so.Bounds = BoundsOf(ss);
                Put(a, "fileName", sf == null ? null : Safe(() => sf.GetState_Text()));
                Put(a, "designItemId", Safe(() => ss.GetState_DesignItemId()));
                Put(a, "symbolType", EnumName(Safe(() => ss.GetState_SymbolType().ToString())));
                break;
            }

            case TObjectId.ePowerObject when o is ISch_PowerObject po:
                so.Text = Safe(() => po.GetState_Text());
                so.Rotation = Degrees(Safe(() => ((ISch_Label)po).GetState_Orientation()));
                Put(a, "style", EnumName(Safe(() => po.GetState_Style().ToString()))?.Replace("Power", string.Empty));
                break;

            case TObjectId.eCrossSheetConnector when o is ISch_Label csl:
                so.Text = Safe(() => csl.GetState_Text());
                so.Rotation = Degrees(Safe(() => csl.GetState_Orientation()));
                break;

            case TObjectId.eNetLabel when o is ISch_Label nl:
                so.Text = Safe(() => nl.GetState_Text());
                so.Rotation = Degrees(Safe(() => nl.GetState_Orientation()));
                break;

            case TObjectId.eNoERC when o is ISch_NoERC noErc:
                if (Safe(() => noErc.GetSuppressAll())) a["suppressAll"] = "true";
                Put(a, "suppressedErrors", Safe(() => noErc.GetStrErrorKindSetToSuppress()));
                if (!Safe(() => noErc.GetIsActive())) a["inactive"] = "true";
                break;

            case TObjectId.eWire:
            case TObjectId.eBus:
            case TObjectId.ePolyline:
            case TObjectId.eSignalHarness:
            case TObjectId.eBezier:
            case TObjectId.ePolygon:
                so.Vertices = VerticesOf(o as ISch_Polygon);
                break;

            case TObjectId.eLine when o is ISch_Line line:
            {
                Point? c2 = Safe(() => line.GetState_Corner());
                so.Vertices = new List<double[]> { new[] { so.X, so.Y }, new[] { Mils(c2?.X ?? 0), Mils(c2?.Y ?? 0) } };
                break;
            }

            case TObjectId.eRectangle:
            case TObjectId.eRoundRectangle:
            case TObjectId.eTextFrame:
            case TObjectId.eNote:
            case TObjectId.eBlanket:
            case TObjectId.eCompileMask:
            case TObjectId.eHarnessConnector:
            case TObjectId.eImage:
                so.Bounds = BoundsOf(o as ISch_GraphicalObject);
                so.Text = NullIfEmpty(Safe(() => o.GetState_Text()));
                break;

            default:
                so.Text = NullIfEmpty(Safe(() => o.GetState_Text()));
                if (o is ISch_Label lbl)
                {
                    so.Rotation = Degrees(Safe(() => lbl.GetState_Orientation()));
                }

                break;
        }

        if (a.Count > 0)
        {
            so.Attributes = a;
        }

        return so;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static ISch_ServerInterface SchServer
    {
        get
        {
            ISch_ServerInterface? s = SCH.GlobalVars.SchServer;
            if (s == null)
            {
                Safe(() => Client.StartServer("SCH"));
                s = SCH.GlobalVars.SchServer;
            }

            return s ?? throw new BridgeException(BridgeErrorCodes.Unsupported, "The SCH editor module is not loaded in this Altium instance.");
        }
    }

    /// <summary>Resolves an ISch_Document by path (open or loaded hidden) or the current schematic editor document.</summary>
    private static (ISch_Document Doc, string Path, bool LoadedOnDemand) ResolveSheet(string? documentPath, bool loadIfClosed)
    {
        ISch_ServerInterface sch = SchServer;
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            ISch_Document? current = Safe(() => sch.GetCurrentSchDocument());
            if (current == null)
            {
                throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, "No schematic sheet is active in the editor. Pass documentPath explicitly.",
                    new Dictionary<string, string> { ["hint"] = "Use project.getStructure to list .SchDoc paths of the project." });
            }

            return (current, Safe(() => current.GetState_DocumentName()) ?? string.Empty, false);
        }

        string full = documentPath;
        try
        {
            full = Path.GetFullPath(documentPath);
        }
        catch
        {
            // keep as given
        }

        ISch_Document? doc = Safe(() => sch.GetSchDocumentByPath(full));
        if (doc != null)
        {
            return (doc, full, false);
        }

        if (!File.Exists(full))
        {
            throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"Schematic file not found: '{full}'.",
                new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths." });
        }

        if (!loadIfClosed)
        {
            throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, $"Sheet is not open in the editor: '{full}'. Pass loadIfClosed=true to load it hidden.");
        }

        doc = Safe(() => sch.LoadSchDocumentByPath(full));
        if (doc == null)
        {
            throw new BridgeException(BridgeErrorCodes.Internal, $"Altium could not load the schematic '{full}'.");
        }

        return (doc, full, true);
    }

    /// <summary>Iterates a container with create/filter/first-next/destroy semantics (see PinsPanel example).</summary>
    private static IEnumerable<ISch_BasicContainer> Iterate(ISch_BasicContainer container, TObjectSet? filter, TIterationDepth depth)
    {
        ISch_Iterator? it = Safe(() => container.SchIterator_Create());
        if (it == null)
        {
            yield break;
        }

        try
        {
            if (filter != null && filter.Count > 0)
            {
                it.AddFilter_ObjectSet(filter);
            }

            it.SetState_IterationDepth(depth);
            for (ISch_BasicContainer? o = Safe(() => it.FirstSchObject()); o != null; o = Safe(() => it.NextSchObject()))
            {
                yield return o;
            }
        }
        finally
        {
            try
            {
                container.SchIterator_Destroy(ref it);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static Dictionary<string, TObjectId> BuildTypeMap()
    {
        var map = new Dictionary<string, TObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (TObjectId id in Enum.GetValues(typeof(TObjectId)))
        {
            string name = TypeName(id);
            if (name.StartsWith("First", StringComparison.Ordinal) || name.StartsWith("Last", StringComparison.Ordinal))
            {
                continue;
            }

            map[name] = id;
        }

        return map;
    }

    private static string TypeName(TObjectId id)
    {
        string s = id.ToString();
        if (s.StartsWith("e", StringComparison.Ordinal))
        {
            s = s.Substring(1);
        }

        return s == "SchComponent" ? "Component" : s;
    }

    private static string NormalizeTypeName(string t)
    {
        string s = t.Trim();
        if (s.StartsWith("e", StringComparison.Ordinal) && s.Length > 1 && char.IsUpper(s[1]))
        {
            s = s.Substring(1);
        }

        return s.Equals("SchComponent", StringComparison.OrdinalIgnoreCase) ? "Component" : s;
    }

    private static bool Matches(string? filter, SchObject o)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        string? extra = null;
        if (o.Attributes != null)
        {
            o.Attributes.TryGetValue("comment", out extra);
            if (extra == null) o.Attributes.TryGetValue("libReference", out extra);
            if (extra == null) o.Attributes.TryGetValue("name", out extra);
            if (extra == null) o.Attributes.TryGetValue("value", out extra);
        }

        return FilterMatcher.Matches(filter, o.Text, o.Owner, extra);
    }

    private static double[]? BoundsOf(ISch_GraphicalObject? g)
    {
        if (g == null)
        {
            return null;
        }

        CoordRect? r = Safe(() => g.BoundingRectangle());
        return r == null ? null : new[] { Mils(r.X1), Mils(r.Y1), Mils(r.X2), Mils(r.Y2) };
    }

    private static List<double[]>? VerticesOf(ISch_Polygon? poly)
    {
        if (poly == null)
        {
            return null;
        }

        int n = Safe(() => poly.GetState_VerticesCount());
        var list = new List<double[]>(n);
        for (int i = 1; i <= n; i++)
        {
            int idx = i;
            Point? v = Safe(() => poly.GetState_Vertex(idx));
            if (v != null)
            {
                list.Add(new[] { Mils(v.X), Mils(v.Y) });
            }
        }

        return list.Count > 0 ? list : null;
    }

    private static string? Electrical(ISch_Pin pin)
    {
        string? e = Safe(() => pin.GetState_Electrical().ToString());
        return EnumName(e)?.Replace("Electric", string.Empty) switch
        {
            "IO" => "I/O",
            var other => other,
        };
    }

    private static int Degrees(TRotationBy90 r) => r switch
    {
        TRotationBy90.eRotate90 => 90,
        TRotationBy90.eRotate180 => 180,
        TRotationBy90.eRotate270 => 270,
        _ => 0,
    };

    private static string? EnumName(string? enumValue)
    {
        if (string.IsNullOrEmpty(enumValue))
        {
            return null;
        }

        return enumValue.Length > 1 && enumValue[0] == 'e' && char.IsUpper(enumValue[1]) ? enumValue.Substring(1) : enumValue;
    }

    private static void Put(Dictionary<string, string> a, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            a[key] = value;
        }
    }

    private static bool Eq(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Internal schematic/PCB coordinate (1/10000 mil) to mils, rounded to 0.001 mil.</summary>
    internal static double Mils(int coord) => Math.Round(coord / 10000.0, 3);

    internal static int NaturalCompare(string a, string b)
    {
        int ia = 0, ib = 0;
        while (ia < a.Length && ib < b.Length)
        {
            if (char.IsDigit(a[ia]) && char.IsDigit(b[ib]))
            {
                int sa = ia, sb = ib;
                while (ia < a.Length && char.IsDigit(a[ia])) ia++;
                while (ib < b.Length && char.IsDigit(b[ib])) ib++;
                string na = a.Substring(sa, ia - sa).TrimStart('0');
                string nb = b.Substring(sb, ib - sb).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToUpperInvariant(a[ia]).CompareTo(char.ToUpperInvariant(b[ib]));
                if (c != 0) return c;
                ia++;
                ib++;
            }
        }

        return (a.Length - ia).CompareTo(b.Length - ib);
    }
}
