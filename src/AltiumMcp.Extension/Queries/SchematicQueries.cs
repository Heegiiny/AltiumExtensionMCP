using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using EDP;   // DM_* typed helpers (extension methods) for the compiled-model fallback in GetComponent
using SCH;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Schematic sheet reads through the SCH editor object model (ISch_Document + iterators).
/// Unlike the compiled model (ProjectQueries) this is per sheet and carries geometry.
/// Coordinates are converted from internal units (1/10000 mil) to mils.
/// </summary>
internal sealed partial class SchematicQueries
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

    /// <summary>
    /// Types that live inside a container object rather than directly on the sheet, mapped to their container type.
    /// A first-level sheet iteration never yields them (verified live: sheet entries were missing until this map
    /// existed), so the container is iterated and its children are reported with <c>owner</c> = container text.
    /// </summary>
    private static readonly Dictionary<TObjectId, TObjectId> ChildOwner = new()
    {
        [TObjectId.ePin] = TObjectId.eSchComponent,
        [TObjectId.eParameter] = TObjectId.eSchComponent,
        [TObjectId.eDesignator] = TObjectId.eSchComponent,
        [TObjectId.eImplementation] = TObjectId.eSchComponent,
        [TObjectId.eSheetEntry] = TObjectId.eSheetSymbol,
        [TObjectId.eHarnessEntry] = TObjectId.eHarnessConnector,
    };

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

        // Sheet-level objects requested directly, plus the containers whose children were requested.
        var wantedTopLevel = new HashSet<TObjectId>(wanted.Where(t => !ChildOwner.ContainsKey(t)));
        var childrenByOwner = new Dictionary<TObjectId, List<TObjectId>>();
        foreach (TObjectId child in wanted.Where(ChildOwner.ContainsKey))
        {
            TObjectId owner = ChildOwner[child];
            if (!childrenByOwner.TryGetValue(owner, out List<TObjectId>? list))
            {
                childrenByOwner[owner] = list = new List<TObjectId>();
            }

            list.Add(child);
        }

        var iterateSet = new TObjectSet(wantedTopLevel.Union(childrenByOwner.Keys).ToArray());

        var all = new List<SchObject>();
        if (iterateSet.Count > 0)
        {
            foreach (ISch_BasicContainer o in Iterate(doc, iterateSet, TIterationDepth.eIterateFirstLevel))
            {
                TObjectId id = Safe(() => o.GetState_ObjectId());
                SchObject obj = ToObject(o, id, null);
                if (wantedTopLevel.Contains(id) && Matches(p.Filter, obj))
                {
                    all.Add(obj);
                }

                if (childrenByOwner.TryGetValue(id, out List<TObjectId>? childTypes))
                {
                    foreach (ISch_BasicContainer child in Iterate(o, new TObjectSet(childTypes.ToArray()), TIterationDepth.eIterateAllLevels))
                    {
                        if (child is ISch_Pin pin && o is ISch_Component owner && !PinBelongsToActiveMode(pin, owner))
                        {
                            continue;
                        }

                        SchObject so = ToObject(child, Safe(() => child.GetState_ObjectId()), obj.Text);
                        if (Matches(p.Filter, so))
                        {
                            all.Add(so);
                        }
                    }
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
        if (p == null || InputNormalizer.Optional(p.Component) == null)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'component' (designator or sheet UniqueId) is required.");
        }

        p.Component = InputNormalizer.Optional(p.Component)!;
        string level;
        try
        {
            level = DetailLevel.Normalize(p.Detail);
        }
        catch (ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, ex.Message);
        }

        bool full = level == DetailLevel.Full;
        (ISch_Document doc, string path, _) = ResolveSheet(p.DocumentPath, p.LoadIfClosed);

        // Pass 1: everything on the sheet (needed for fallbacks and for a useful error message).
        var candidates = new List<(ISch_Component Comp, string? Logical, string? Physical, string? Uid)>();
        foreach (ISch_BasicContainer o in Iterate(doc, new TObjectSet(TObjectId.eSchComponent), TIterationDepth.eIterateFirstLevel))
        {
            if (o is not ISch_Component c)
            {
                continue;
            }

            ISch_Designator? d = Safe(() => c.GetState_SchDesignator());
            candidates.Add((c,
                d == null ? null : Safe(() => d.GetState_Text()),
                d == null ? null : NullIfEmpty(Safe(() => d.GetState_PhysicalDesignator())),
                Safe(() => c.GetState_UniqueId())));
        }

        string wanted = p.Component.Trim();

        // 1. Exact: logical designator as drawn, sheet-level physical designator, or sheet UniqueId.
        ISch_Component? found = candidates.FirstOrDefault(t => Eq(t.Logical, wanted) || Eq(t.Physical, wanted) || Eq(t.Uid, wanted)).Comp;

        // 2. Multi-part suffix ("U2A" → "U2", part 1). Live finding: each placed part of a multi-part component is its
        //    own ISch_Component on the sheet with its own UniqueId and the same designator text; pick the part whose
        //    CurrentPartID matches the letter, else the first one with that designator.
        if (found == null && wanted.Length > 1 && char.IsLetter(wanted[^1]) && char.IsDigit(wanted[^2]))
        {
            string baseDes = wanted[..^1];
            int partId = char.ToUpperInvariant(wanted[^1]) - 'A' + 1;
            var parts = candidates.Where(t => Eq(t.Logical, baseDes)).ToList();
            found = parts.FirstOrDefault(t => Safe(() => t.Comp.GetState_CurrentPartID()) == partId).Comp ?? parts.FirstOrDefault().Comp;
        }

        // 3. Physical designator / compiled UniqueId from the compiled project model (project.* ids). The sheet
        //    object model only knows the logical designator (live finding: GetState_PhysicalDesignator() is empty
        //    on a hierarchical design), so map physical → logical via the flattened document of the owning project.
        Dictionary<string, List<CompiledComponentRef>> compiled = CompiledComponentsOfSheet(path);
        if (found == null && compiled.Count > 0)
        {
            string wantedTail = wanted.Contains('\\') ? wanted[(wanted.LastIndexOf('\\') + 1)..] : wanted;
            CompiledComponentRef? hit = compiled.Values.SelectMany(l => l)
                .FirstOrDefault(r => Eq(r.PhysicalDesignator, wanted) || Eq(r.UniqueId, wanted));
            found = hit == null
                ? candidates.FirstOrDefault(t => Eq(t.Uid, wantedTail)).Comp
                : candidates.FirstOrDefault(t => Eq(t.Uid, hit.UniqueIdTail) || Eq(t.Logical, hit.LogicalDesignator)).Comp;
        }

        if (found == null)
        {
            string sample = string.Join(", ", candidates.Select(t => t.Logical).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).Take(20));
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Component '{p.Component}' not found on sheet '{Path.GetFileName(path)}'.",
                new Dictionary<string, string>
                {
                    ["designatorsOnSheet"] = sample,
                    ["hint"] = "Pass the logical designator as drawn on this sheet, its sheet UniqueId, or the physical designator / compiled id from project.listComponents (resolved when the project is compiled). Use sch.listObjects types=[\"Component\"] to list this sheet.",
                });
        }

        ISch_Designator? des = Safe(() => found.GetState_SchDesignator());
        ISch_Parameter? comment = Safe(() => found.GetState_SchComment());
        Point? loc = Safe(() => found.GetState_Location());
        string foundUid = Safe(() => found.GetState_UniqueId()) ?? string.Empty;
        string? foundLogical = des == null ? null : Safe(() => des.GetState_Text());
        string? physicalFromSheet = des == null ? null : NullIfEmpty(Safe(() => des.GetState_PhysicalDesignator()));
        // Compiled instances: by UniqueId tail (the normal case), else by logical designator — the compiled model keeps
        // only one UniqueId per multi-part component, so the other parts' sheet objects map through the designator.
        List<CompiledComponentRef>? compiledRefs = compiled.TryGetValue(foundUid, out List<CompiledComponentRef>? refs) ? refs : null;
        if ((compiledRefs == null || compiledRefs.Count == 0) && !string.IsNullOrEmpty(foundLogical))
        {
            compiledRefs = compiled.Values.SelectMany(l => l).Where(r => Eq(r.LogicalDesignator, foundLogical)).ToList();
            if (compiledRefs.Count == 0)
            {
                compiledRefs = null;
            }
        }
        var detail = new SchComponentDetail
        {
            DocumentPath = path,
            Id = foundUid,
            Designator = (des == null ? null : Safe(() => des.GetState_Text())) ?? string.Empty,
            // Sheet-level physical designator when Altium fills it; otherwise the compiled model's (one per channel
            // instance in multi-channel designs, joined with ", ").
            PhysicalDesignator = physicalFromSheet
                ?? (compiledRefs == null ? null : NullIfEmpty(string.Join(", ", compiledRefs.Select(r => r.PhysicalDesignator).Where(s => !string.IsNullOrEmpty(s)).Distinct()))),
            CompiledIds = compiledRefs?.Select(r => r.UniqueId).Distinct().ToList(),
            Comment = comment == null ? null : NullIfEmpty(Safe(() => comment.GetState_Text())),
            Description = NullIfEmpty(Safe(() => found.GetState_ComponentDescription())),
            LibReference = NullIfEmpty(Safe(() => found.GetState_LibReference())),
            SourceLibraryName = full ? NullIfEmpty(Safe(() => found.GetState_SourceLibraryName())) : null,
            DesignItemId = full ? NullIfEmpty(Safe(() => found.GetState_DesignItemId())) : null,
            ComponentKind = full ? EnumName(Safe(() => found.GetState_ComponentKind().ToString())) : null,
            X = Mils(loc?.X ?? 0),
            Y = Mils(loc?.Y ?? 0),
            Rotation = Degrees(Safe(() => found.GetState_Orientation())),
            IsMirrored = Safe(() => found.GetState_IsMirrored()),
            Bounds = full ? BoundsOf(found) : null,
            PartCount = Safe(() => found.GetState_PartCountNoPart0()),
            CurrentPartId = Safe(() => found.GetState_CurrentPartID()),
            DisplayMode = full ? Safe(() => found.GetState_DisplayMode()) : 0,
        };

        if (CrossProbeService.ShouldProbe(p.CrossProbe))
        {
            string probePath = path;
            string probeId = foundUid;
            detail.CrossProbe = CrossProbeService.Request($"sheet component {detail.Designator} on {Path.GetFileName(path)}",
                () => Select(new SelectParams { DocumentPath = probePath, Objects = new List<string> { probeId }, ClearFirst = true, ZoomTo = true, Focus = true }));
        }

        if (level == DetailLevel.Summary)
        {
            return detail;
        }

        string? vault = full ? NullIfEmpty(Safe(() => found.GetState_VaultGUID())) : null;
        string? item = full ? NullIfEmpty(Safe(() => found.GetState_ItemGUID())) : null;
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

        detail.Pins = new List<SchPinInfo>();
        foreach (ISch_BasicContainer o in Iterate(found, new TObjectSet(TObjectId.ePin), TIterationDepth.eIterateFirstLevel))
        {
            if (o is not ISch_Pin pin || !PinBelongsToActiveMode(pin, found))
            {
                continue;
            }

            var info = new SchPinInfo
            {
                Designator = Safe(() => pin.GetState_Designator()) ?? string.Empty,
                Name = NullIfEmpty(Safe(() => pin.GetState_Name())),
                Electrical = Electrical(pin),
                PartId = detail.PartCount > 1 ? Safe(() => pin.GetState_OwnerPartId()) : 0,
                IsHidden = Safe(() => pin.GetState_IsHidden()),
                HiddenNetName = NullIfEmpty(Safe(() => pin.GetState_HiddenNetName())),
            };
            if (full)
            {
                Point? pl = Safe(() => pin.GetState_Location());
                info.X = Mils(pl?.X ?? 0);
                info.Y = Mils(pl?.Y ?? 0);
                info.Rotation = Degrees(Safe(() => pin.GetState_Orientation()));
                info.LengthMils = Mils(Safe(() => pin.GetState_PinLength()));
                info.Description = NullIfEmpty(Safe(() => pin.GetState_Description()));
            }

            detail.Pins.Add(info);
        }

        detail.Pins.Sort((a, b) => NaturalCompare(a.Designator, b.Designator));

        if (!full)
        {
            return detail;
        }

        detail.Parameters = new Dictionary<string, SchParameterInfo>(StringComparer.OrdinalIgnoreCase);
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

        detail.Implementations = new List<ImplementationInfo>();
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
                Put(a, "ioType", EnumName(Safe(() => se.GetState_IOType().ToString()))?.Replace("Port", string.Empty));
                Put(a, "side", EnumName(Safe(() => se.GetState_Side().ToString())));
                break;

            case TObjectId.eHarnessEntry when o is ISch_HarnessEntry he:
                so.Text = Safe(() => he.GetState_Name());
                Put(a, "side", EnumName(Safe(() => he.GetState_Side().ToString())));
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

    private sealed record CompiledComponentRef(string UniqueId, string UniqueIdTail, string? LogicalDesignator, string? PhysicalDesignator);

    /// <summary>
    /// Compiled (flattened) components whose source sheet is <paramref name="sheetPath"/>, keyed by the last segment
    /// of the hierarchical compiled UniqueId (= the sheet-level UniqueId). Empty when the sheet belongs to no open
    /// project or the project is not compiled — callers must treat that as "no mapping", not as an error.
    /// </summary>
    private static Dictionary<string, List<CompiledComponentRef>> CompiledComponentsOfSheet(string sheetPath)
    {
        var map = new Dictionary<string, List<CompiledComponentRef>>(StringComparer.OrdinalIgnoreCase);
        EDP.IWorkspace? ws;
        try
        {
            ws = Workspace;
        }
        catch (BridgeException)
        {
            return map;
        }

        EDP.IDocument? dm = Safe(() => ws.DM_GetDocumentFromPath(sheetPath));
        EDP.IProject? project = dm == null ? null : Safe(() => dm.DM_Project());
        EDP.IDocument? flat = project == null ? null : Safe(() => project.DM_DocumentFlattened());
        if (flat == null)
        {
            return map;
        }

        int count = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < count; i++)
        {
            EDP.IComponent? c = Safe(() => flat.DM_Components(i));
            if (c == null || !PathEquals(Safe(() => c.DM_OwnerDocumentFullPath()), sheetPath))
            {
                continue;
            }

            string uid = Safe(() => c.DM_UniqueId()) ?? string.Empty;
            if (uid.Length == 0)
            {
                continue;
            }

            string tail = uid.Contains('\\') ? uid[(uid.LastIndexOf('\\') + 1)..] : uid;
            if (!map.TryGetValue(tail, out List<CompiledComponentRef>? list))
            {
                map[tail] = list = new List<CompiledComponentRef>();
            }

            list.Add(new CompiledComponentRef(uid, tail, Safe(() => c.DM_LogicalDesignator()), Safe(() => c.DM_PhysicalDesignator())));
        }

        return map;
    }

    /// <summary>
    /// A component stores one pin set per symbol display mode (alternate symbols); iterating a component yields all
    /// of them (verified live: a 2-pin capacitor returned 4 pins). Keep only the pins of the active display mode.
    /// </summary>
    private static bool PinBelongsToActiveMode(ISch_Pin pin, ISch_Component owner)
    {
        int mode = Safe(() => owner.GetState_DisplayMode());
        int pinMode = Safe(() => pin.GetState_OwnerPartDisplayMode());
        return pinMode == mode;
    }

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
        // Path validation / project-context resolution happens before any SCH server call (no modal dialogs).
        ResolvedDocument target = DocumentResolver.Resolve(documentPath, DocumentResolver.Sch);
        string full = target.FullPath;
        ISch_ServerInterface sch = SchServer;

        ISch_Document? doc = Safe(() => sch.GetSchDocumentByPath(full));
        if (doc != null)
        {
            return (doc, full, false);
        }

        if (!loadIfClosed)
        {
            throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, $"Sheet is not open in the editor: '{full}'. Pass loadIfClosed=true to load it hidden.");
        }

        doc = Safe(() => sch.LoadSchDocumentByPath(full));
        if (doc == null)
        {
            throw new BridgeException(BridgeErrorCodes.AltiumApiError, $"Altium could not load the schematic '{full}'.",
                new Dictionary<string, string> { ["hint"] = "The file exists but the SCH editor rejected it (corrupt, locked, or not a schematic)." });
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
