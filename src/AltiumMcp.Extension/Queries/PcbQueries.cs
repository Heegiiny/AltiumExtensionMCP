using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using PCB;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// PCB board reads through the PCB editor object model (IPCB_Board + iterators).
/// Coordinates are converted from internal units (1/10000 mil) to mils relative to the board origin.
/// </summary>
internal sealed partial class PcbQueries
{
    private const int MaxLimit = 2000;

    private static readonly TObjectId[] DefaultPrimitiveTypes =
    {
        TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eViaObject, TObjectId.ePolyObject,
        TObjectId.eRegionObject, TObjectId.eFillObject, TObjectId.eTextObject,
    };

    private static readonly Dictionary<string, TObjectId> TypeByName = BuildTypeMap();

    // ---- pcb.getBoard ---------------------------------------------------------------------------------------------

    public BoardInfo GetBoard(BoardQueryParams? p)
    {
        p ??= new BoardQueryParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        IPCB_Board board = ctx.Board;

        var info = new BoardInfo
        {
            Document = new DocumentRef { Path = ctx.Path, FileName = Path.GetFileName(ctx.Path), Kind = "PCB" },
            IsOpenInEditor = Safe(() => Client.GetDocumentByPath(ctx.Path)) != null,
            WasLoadedOnDemand = ctx.LoadedOnDemand,
            DisplayUnit = EnumName(Safe(() => board.GetState_DisplayUnit().ToString())),
            OriginXMils = Mils(ctx.OriginX),
            OriginYMils = Mils(ctx.OriginY),
        };

        IPCB_BoardOutline? outline = Safe(() => board.GetState_BoardOutline());
        if (outline != null)
        {
            CoordRect? r = Safe(() => ((IPCB_Primitive)outline).BoundingRectangle());
            if (r != null)
            {
                info.OutlineBounds = ctx.Rect(r);
                info.BoardWidthMils = Math.Round(Mils(r.X2) - Mils(r.X1), 3);
                info.BoardHeightMils = Math.Round(Mils(r.Y2) - Mils(r.Y1), 3);
            }
        }

        IPCB_LayerStack_V7? stack = Safe(() => board.GetState_LayerStack_V7());
        if (stack != null)
        {
            info.SignalLayerCount = Safe(() => stack.SignalLayerCount());
            int guard = 0;
            for (IPCB_LayerObject_V7? l = Safe(() => stack.FirstLayer()); l != null && guard++ < 256; l = Safe(() => stack.NextLayer(l)))
            {
                IPCB_LayerObject_V7 layer = l;
                TV6_Layer id = Safe(() => ((IPCB_LayerObject)layer).V6_LayerID());
                string kind = LayerKind(id, layer);
                int copper = kind is "Signal" or "Plane" ? Safe(() => ((IPCB_ElectricalLayer)layer).GetState_CopperThickness()) : 0;
                info.LayerStack.Add(new LayerInfo
                {
                    Name = Safe(() => layer.GetState_LayerName()) ?? string.Empty,
                    Id = LayerId(id),
                    Kind = kind,
                    CopperThicknessMils = copper > 0 ? Mils(copper) : null,
                    IsUsed = Safe(() => layer.GetState_UsedByPrims()),
                });
            }
        }

        // One pass over all primitives for counts (components' children are counted too: eProcessAll).
        foreach (IPCB_Primitive prim in ctx.Iterate(null, TIterationMethod.eProcessAll))
        {
            string type = TypeName(Safe(() => prim.GetState_ObjectID()));
            info.ObjectCounts[type] = info.ObjectCounts.TryGetValue(type, out int n) ? n + 1 : 1;
            if (prim is IPCB_ObjectClass cls && type == "Class")
            {
                info.Classes.Add(new PcbClassInfo
                {
                    Name = Safe(() => cls.GetState_Name()) ?? string.Empty,
                    Kind = EnumName(Safe(() => cls.GetState_MemberKind().ToString()))?.Replace("ClassMemberKind_", string.Empty) ?? string.Empty,
                    IsSuperClass = Safe(() => cls.GetState_SuperClass()),
                    MemberCount = CountMembers(cls),
                });
            }
        }

        info.RuleCount = info.ObjectCounts.TryGetValue("Rule", out int rules) ? rules : 0;
        info.ViolationCount = info.ObjectCounts.TryGetValue("Violation", out int viol) ? viol : 0;
        return info;
    }

    // ---- pcb.listComponents / pcb.getComponent --------------------------------------------------------------------

    public PcbComponentListResult ListComponents(ListPcbComponentsParams? p)
    {
        p ??= new ListPcbComponentsParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        string? side = string.IsNullOrWhiteSpace(p.Layer) ? null : p.Layer.Trim();

        var all = new List<PcbComponentSummary>();
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eComponentObject), TIterationMethod.eProcessAll))
        {
            if (prim is not IPCB_Component c)
            {
                continue;
            }

            PcbComponentSummary s = ToSummary(ctx, c);
            if (side != null && !s.Layer.StartsWith(side, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (FilterMatcher.Matches(p.Filter, s.Designator, s.Footprint, s.Comment, s.SourceLibReference))
            {
                all.Add(s);
            }
        }

        all.Sort((a, b) => SchematicQueries.NaturalCompare(a.Designator, b.Designator));
        int limit = Math.Clamp(p.Limit <= 0 ? 100 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<PcbComponentSummary> page = all.Skip(offset).Take(limit).ToList();
        return new PcbComponentListResult { DocumentPath = ctx.Path, Total = all.Count, Offset = offset, Returned = page.Count, Items = page };
    }

    public PcbComponentDetail GetComponent(GetPcbComponentParams? p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Component))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'component' (designator or PCB UniqueId) is required.");
        }

        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        IPCB_Component? found = Safe(() => ctx.Board.GetPcbComponentByRefDes(p.Component));
        if (found == null)
        {
            foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eComponentObject), TIterationMethod.eProcessAll))
            {
                if (prim is IPCB_Component c && (Eq(Safe(() => c.GetState_UniqueId()), p.Component) || Eq(Safe(() => c.GetState_SourceUniqueId()), p.Component)))
                {
                    found = c;
                    break;
                }
            }
        }

        if (found == null)
        {
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"PCB component '{p.Component}' not found on '{Path.GetFileName(ctx.Path)}'.",
                new Dictionary<string, string> { ["hint"] = "Use pcb.listComponents to see designators on this board." });
        }

        var d = new PcbComponentDetail
        {
            DocumentPath = ctx.Path,
            Summary = ToSummary(ctx, found),
            FootprintDescription = NullIfEmpty(Safe(() => found.GetState_FootprintDescription())),
            SourceFootprintLibrary = NullIfEmpty(Safe(() => found.GetState_SourceFootprintLibrary())),
            SourceComponentLibrary = NullIfEmpty(Safe(() => found.GetState_SourceComponentLibrary())),
            SourceDesignItemId = NullIfEmpty(Safe(() => found.GetState_SourceCompDesignItemID())),
            Default3DModel = NullIfEmpty(Safe(() => found.GetState_DefaultPCB3DModel())),
            IsBga = Safe(() => found.GetState_IsBGA()),
            EnablePinSwapping = Safe(() => found.GetState_EnablePinSwapping()),
            EnablePartSwapping = Safe(() => found.GetState_EnablePartSwapping()),
            DesignatorVisible = Safe(() => found.GetState_NameOn()),
            CommentVisible = Safe(() => found.GetState_CommentOn()),
        };

        string? vault = NullIfEmpty(Safe(() => found.GetState_VaultGUID()));
        string? item = NullIfEmpty(Safe(() => found.GetState_ItemGUID()));
        if (vault != null || item != null)
        {
            d.Managed = new ManagedLink { VaultGuid = vault, ItemGuid = item, RevisionGuid = NullIfEmpty(Safe(() => found.GetState_ItemRevisionGUID())) };
        }

        foreach (IPCB_Primitive child in ctx.IterateGroup(found, null))
        {
            TObjectId id = Safe(() => child.GetState_ObjectID());
            if (id == TObjectId.ePadObject && child is IPCB_Pad pad)
            {
                d.Pads.Add(ToPad(ctx, pad, false));
                continue;
            }

            string type = TypeName(id);
            d.PrimitiveCounts[type] = d.PrimitiveCounts.TryGetValue(type, out int n) ? n + 1 : 1;
        }

        d.Pads.Sort((a, b) => SchematicQueries.NaturalCompare(a.Name, b.Name));
        return d;
    }

    // ---- pcb.listNets / pcb.getNet --------------------------------------------------------------------------------

    public PcbNetListResult ListNets(ListPcbNetsParams? p)
    {
        p ??= new ListPcbNetsParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);

        Dictionary<string, int> unrouted = CountUnroutedByNet(ctx);
        var all = new List<PcbNetSummary>();
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eNetObject), TIterationMethod.eProcessAll))
        {
            if (prim is not IPCB_Net net)
            {
                continue;
            }

            PcbNetSummary s = ToNetSummary(net, unrouted);
            if (FilterMatcher.Matches(p.Filter, s.Name))
            {
                all.Add(s);
            }
        }

        all.Sort((a, b) => SchematicQueries.NaturalCompare(a.Name, b.Name));
        int limit = Math.Clamp(p.Limit <= 0 ? 100 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<PcbNetSummary> page = all.Skip(offset).Take(limit).ToList();
        return new PcbNetListResult { DocumentPath = ctx.Path, Total = all.Count, Offset = offset, Returned = page.Count, Items = page };
    }

    public PcbNetDetail GetNet(GetPcbNetParams? p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Net))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'net' is required.");
        }

        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        IPCB_Net? net = null;
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eNetObject), TIterationMethod.eProcessAll))
        {
            if (prim is IPCB_Net n && Eq(Safe(() => n.GetState_Name()), p.Net))
            {
                net = n;
                break;
            }
        }

        if (net == null)
        {
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Net '{p.Net}' not found on '{Path.GetFileName(ctx.Path)}'.",
                new Dictionary<string, string> { ["hint"] = "Use pcb.listNets (supports wildcards) to find the exact net name." });
        }

        string name = Safe(() => net.GetState_Name()) ?? p.Net;
        var d = new PcbNetDetail { DocumentPath = ctx.Path, Summary = ToNetSummary(net, CountUnroutedByNet(ctx)) };
        var layers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        double length = 0;

        var types = new TObjectSet(TObjectId.ePadObject, TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eViaObject, TObjectId.ePolyObject, TObjectId.eRegionObject);
        foreach (IPCB_Primitive prim in ctx.Iterate(types, TIterationMethod.eProcessAll))
        {
            IPCB_Net? pn = Safe(() => prim.GetState_Net());
            if (pn == null || !Eq(Safe(() => pn.GetState_Name()), name))
            {
                continue;
            }

            switch (Safe(() => prim.GetState_ObjectID()))
            {
                case TObjectId.ePadObject when prim is IPCB_Pad pad:
                    d.Pads.Add(ToPad(ctx, pad, true));
                    break;
                case TObjectId.eTrackObject when prim is IPCB_Track t:
                    d.TrackCount++;
                    layers.Add(ctx.LayerName(Safe(() => prim.GetState_Layer())));
                    length += Math.Sqrt(Math.Pow(Safe(() => t.GetState_X2()) - Safe(() => t.GetState_X1()), 2) + Math.Pow(Safe(() => t.GetState_Y2()) - Safe(() => t.GetState_Y1()), 2));
                    break;
                case TObjectId.eArcObject when prim is IPCB_Arc a:
                    d.ArcCount++;
                    layers.Add(ctx.LayerName(Safe(() => prim.GetState_Layer())));
                    double sweep = Math.Abs(Safe(() => a.GetState_EndAngle()) - Safe(() => a.GetState_StartAngle()));
                    length += Safe(() => a.GetState_Radius()) * sweep * Math.PI / 180.0;
                    break;
                case TObjectId.eViaObject:
                    break;
                case TObjectId.ePolyObject:
                    d.PolygonCount++;
                    layers.Add(ctx.LayerName(Safe(() => prim.GetState_Layer())));
                    break;
                case TObjectId.eRegionObject:
                    d.RegionCount++;
                    layers.Add(ctx.LayerName(Safe(() => prim.GetState_Layer())));
                    break;
            }
        }

        d.Pads.Sort((a, b) => SchematicQueries.NaturalCompare(a.PinDescriptor ?? a.Name, b.PinDescriptor ?? b.Name));
        d.Layers = layers.ToList();
        d.TrackLengthMils = Math.Round(length / 10000.0, 3);
        return d;
    }

    // ---- pcb.listRules --------------------------------------------------------------------------------------------

    public PcbRuleListResult ListRules(ListPcbRulesParams? p)
    {
        p ??= new ListPcbRulesParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        var result = new PcbRuleListResult { DocumentPath = ctx.Path };
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eRuleObject), TIterationMethod.eProcessAll))
        {
            if (prim is not IPCB_Rule rule)
            {
                continue;
            }

            bool enabled = Safe(() => rule.GetState_DRCEnabled());
            if (!enabled && !p.IncludeDisabled)
            {
                continue;
            }

            var r = new PcbRuleInfo
            {
                Name = Safe(() => rule.GetState_Name()) ?? string.Empty,
                Kind = EnumName(Safe(() => rule.GetState_RuleKind().ToString()))?.Replace("Rule_", string.Empty) ?? string.Empty,
                Enabled = enabled,
                Priority = Safe(() => (int)rule.Priority()),
                Scope1 = NullIfEmpty(Safe(() => rule.GetState_Scope1Expression())),
                Scope2 = Safe(() => rule.IsUnary()) ? null : NullIfEmpty(Safe(() => rule.GetState_Scope2Expression())),
                Summary = NullIfEmpty(Safe(() => rule.GetState_DataSummaryString())),
                Comment = NullIfEmpty(Safe(() => rule.GetState_Comment())),
            };

            if (FilterMatcher.Matches(p.Filter, r.Kind, r.Name))
            {
                result.Rules.Add(r);
            }
        }

        result.Rules.Sort((a, b) => string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : a.Priority.CompareTo(b.Priority));
        result.Total = result.Rules.Count;
        return result;
    }

    // ---- pcb.listPrimitives ---------------------------------------------------------------------------------------

    public PcbPrimitiveListResult ListPrimitives(ListPcbPrimitivesParams? p)
    {
        p ??= new ListPcbPrimitivesParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);

        var wanted = new TObjectSet();
        if (p.Types == null || p.Types.Count == 0)
        {
            foreach (TObjectId t in DefaultPrimitiveTypes) wanted.Add(t);
        }
        else
        {
            foreach (string t in p.Types)
            {
                if (!TypeByName.TryGetValue(NormalizeTypeName(t), out TObjectId id))
                {
                    throw new BridgeException(BridgeErrorCodes.InvalidParams, $"Unknown PCB object type '{t}'.",
                        new Dictionary<string, string> { ["knownTypes"] = string.Join(", ", TypeByName.Keys.OrderBy(k => k)) });
                }

                wanted.Add(id);
            }
        }

        string? layerFilter = string.IsNullOrWhiteSpace(p.Layer) ? null : Normalize(p.Layer);
        string? netFilter = string.IsNullOrWhiteSpace(p.Net) ? null : p.Net.Trim();

        var all = new List<PcbPrimitive>();
        var seenLayers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool layerMatched = false;
        foreach (IPCB_Primitive prim in ctx.Iterate(wanted, p.FreeOnly ? TIterationMethod.eProcessFree : TIterationMethod.eProcessAll))
        {
            TV6_Layer layer = Safe(() => prim.GetState_Layer());
            string layerName = ctx.LayerName(layer);
            seenLayers.Add($"{layerName} ({LayerId(layer)})");
            if (layerFilter != null && Normalize(layerName) != layerFilter && Normalize(LayerId(layer)) != layerFilter)
            {
                continue;
            }

            layerMatched = true;

            IPCB_Net? net = Safe(() => prim.GetState_Net());
            string? netName = net == null ? null : NullIfEmpty(Safe(() => net.GetState_Name()));
            if (netFilter != null && !Eq(netName, netFilter))
            {
                continue;
            }

            all.Add(ToPrimitive(ctx, prim, layerName, netName));
        }

        if (layerFilter != null && !layerMatched)
        {
            // Distinguish "valid layer, nothing on it for these types" from a typo: a name that matches no layer
            // carrying any of the requested primitive types is almost always a typo (live finding: silently
            // returning an empty list misled the analysis).
            throw new BridgeException(BridgeErrorCodes.InvalidParams,
                $"Layer '{p.Layer}' matched none of the layers that carry the requested primitive types on this board.",
                new Dictionary<string, string>
                {
                    ["layersWithMatchingPrimitives"] = string.Join(", ", seenLayers),
                    ["hint"] = "Use a layer name from pcb.getBoard (e.g. 'Top Layer') or an id (e.g. 'TopLayer', 'TopOverlay', 'Mechanical1').",
                });
        }

        int limit = Math.Clamp(p.Limit <= 0 ? 200 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<PcbPrimitive> page = all.Skip(offset).Take(limit).ToList();
        return new PcbPrimitiveListResult { DocumentPath = ctx.Path, Total = all.Count, Offset = offset, Returned = page.Count, Items = page };
    }

    // ---- conversions ----------------------------------------------------------------------------------------------

    private static PcbComponentSummary ToSummary(BoardContext ctx, IPCB_Component c)
    {
        IPCB_Text? name = Safe(() => c.GetState_Name());
        IPCB_Text? comment = Safe(() => c.GetState_Comment());
        CoordRect? r = Safe(() => c.BoundingRectangleNoNameComment());
        int height = Safe(() => c.GetState_Height());
        return new PcbComponentSummary
        {
            Id = Safe(() => c.GetState_UniqueId()) ?? string.Empty,
            Designator = (name == null ? null : Safe(() => name.GetState_Text())) ?? string.Empty,
            SourceUniqueId = NullIfEmpty(Safe(() => c.GetState_SourceUniqueId())),
            Footprint = NullIfEmpty(Safe(() => c.GetState_Pattern())),
            Comment = comment == null ? null : NullIfEmpty(Safe(() => comment.GetState_Text())),
            Layer = ctx.LayerName(Safe(() => c.GetState_Layer())),
            X = ctx.X(Safe(() => c.GetState_XLocation())),
            Y = ctx.Y(Safe(() => c.GetState_YLocation())),
            Rotation = Math.Round(Safe(() => c.GetState_Rotation()), 3),
            HeightMils = height > 0 ? Mils(height) : null,
            Bounds = r == null ? null : ctx.Rect(r),
            PadCount = Safe(() => c.GetPrimitiveCount(new TObjectSet(TObjectId.ePadObject))),
            SourceLibReference = NullIfEmpty(Safe(() => c.GetState_SourceLibReference())),
            SourceDescription = NullIfEmpty(Safe(() => c.GetState_SourceDescription())),
            SourceHierarchicalPath = NullIfEmpty(Safe(() => c.GetState_SourceHierarchicalPath())),
            IsLocked = !Safe(() => c.GetState_Moveable()),
            HasDrcError = Safe(() => c.GetState_DRCError()),
        };
    }

    private static PcbPadInfo ToPad(BoardContext ctx, IPCB_Pad pad, bool includeDescriptor)
    {
        IPCB_Net? net = Safe(() => pad.GetState_Net());
        int hole = Safe(() => pad.GetState_HoleSize());
        TV6_Layer layer = Safe(() => pad.GetState_Layer());
        return new PcbPadInfo
        {
            Name = Safe(() => pad.GetState_Name()) ?? string.Empty,
            Net = net == null ? null : NullIfEmpty(Safe(() => net.GetState_Name())),
            X = ctx.X(Safe(() => pad.GetState_XLocation())),
            Y = ctx.Y(Safe(() => pad.GetState_YLocation())),
            Rotation = Math.Round(Safe(() => pad.GetState_Rotation()), 3),
            Layer = ctx.LayerName(layer),
            IsSurfaceMount = Safe(() => pad.IsSurfaceMount()),
            Shape = EnumName(Safe(() => pad.GetState_TopShape().ToString())),
            SizeXMils = Mils(Safe(() => pad.GetState_TopXSize())),
            SizeYMils = Mils(Safe(() => pad.GetState_TopYSize())),
            HoleSizeMils = hole > 0 ? Mils(hole) : null,
            Plated = hole > 0 && Safe(() => pad.GetState_Plated()),
            PinDescriptor = includeDescriptor ? NullIfEmpty(Safe(() => pad.GetState_PinDescriptorString())) : null,
        };
    }

    private static PcbNetSummary ToNetSummary(IPCB_Net net, IReadOnlyDictionary<string, int> unroutedByNet)
    {
        string name = Safe(() => net.GetState_Name()) ?? string.Empty;
        return new PcbNetSummary
        {
            Name = name,
            PinCount = Safe(() => net.GetState_PinCount()),
            ViaCount = Safe(() => net.GetState_ViaCount()),
            RoutedLengthMils = Mils(Safe(() => net.GetState_RoutedLength())),
            InDifferentialPair = Safe(() => net.GetState_InDifferentialPair()),
            UnroutedConnectionCount = unroutedByNet.TryGetValue(name, out int u) ? u : 0,
        };
    }

    /// <summary>
    /// Ratsnest lines (<see cref="TObjectId.eConnectionObject"/>) per net name = unrouted connections. Live finding:
    /// <c>IPCB_Net.GetState_ConnectivelyInvalid()</c> is true for every net of a board loaded hidden, so it cannot be
    /// used as an "unrouted" indicator; connection objects can.
    /// </summary>
    private static Dictionary<string, int> CountUnroutedByNet(BoardContext ctx)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eConnectionObject), TIterationMethod.eProcessAll))
        {
            IPCB_Net? n = Safe(() => prim.GetState_Net());
            string? name = n == null ? null : Safe(() => n.GetState_Name());
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            result[name] = result.TryGetValue(name, out int c) ? c + 1 : 1;
        }

        return result;
    }

    private static PcbPrimitive ToPrimitive(BoardContext ctx, IPCB_Primitive prim, string layerName, string? netName)
    {
        TObjectId id = Safe(() => prim.GetState_ObjectID());
        var r = new PcbPrimitive { Type = TypeName(id), Layer = layerName, Net = netName, HasDrcError = Safe(() => prim.GetState_DRCError()) };
        if (Safe(() => prim.GetState_InComponent()))
        {
            IPCB_Component? owner = Safe(() => prim.GetState_Component());
            IPCB_Text? n = owner == null ? null : Safe(() => owner.GetState_Name());
            r.Component = n == null ? null : NullIfEmpty(Safe(() => n.GetState_Text()));
        }

        switch (id)
        {
            case TObjectId.eTrackObject when prim is IPCB_Track t:
                r.Geometry = new[] { ctx.X(Safe(() => t.GetState_X1())), ctx.Y(Safe(() => t.GetState_Y1())), ctx.X(Safe(() => t.GetState_X2())), ctx.Y(Safe(() => t.GetState_Y2())) };
                r.WidthMils = Mils(Safe(() => t.GetState_Width()));
                break;
            case TObjectId.eArcObject when prim is IPCB_Arc a:
                r.Geometry = new[] { ctx.X(Safe(() => a.GetState_CenterX())), ctx.Y(Safe(() => a.GetState_CenterY())), Mils(Safe(() => a.GetState_Radius())), Math.Round(Safe(() => a.GetState_StartAngle()), 3), Math.Round(Safe(() => a.GetState_EndAngle()), 3) };
                r.WidthMils = Mils(Safe(() => a.GetState_LineWidth()));
                break;
            case TObjectId.eViaObject when prim is IPCB_Via v:
                r.Geometry = new[] { ctx.X(Safe(() => v.GetState_XLocation())), ctx.Y(Safe(() => v.GetState_YLocation())) };
                r.SizeMils = Mils(Safe(() => v.GetState_Size()));
                r.HoleSizeMils = Mils(Safe(() => v.GetState_HoleSize()));
                IPCB_LayerObject? lo = Safe(() => v.GetState_StartLayer());
                IPCB_LayerObject? hi = Safe(() => v.GetState_StopLayer());
                string? loN = lo == null ? null : Safe(() => lo.GetState_LayerName());
                string? hiN = hi == null ? null : Safe(() => hi.GetState_LayerName());
                r.Detail = loN != null && hiN != null ? $"{loN} - {hiN}" : null;
                break;
            case TObjectId.ePadObject when prim is IPCB_Pad pad:
                r.Geometry = new[] { ctx.X(Safe(() => pad.GetState_XLocation())), ctx.Y(Safe(() => pad.GetState_YLocation())) };
                r.SizeMils = Mils(Safe(() => pad.GetState_TopXSize()));
                int hole = Safe(() => pad.GetState_HoleSize());
                r.HoleSizeMils = hole > 0 ? Mils(hole) : null;
                r.Text = Safe(() => pad.GetState_Name());
                break;
            case TObjectId.eTextObject when prim is IPCB_Text text:
                r.Geometry = new[] { ctx.X(Safe(() => text.GetState_XLocation())), ctx.Y(Safe(() => text.GetState_YLocation())) };
                r.SizeMils = Mils(Safe(() => text.GetState_Size()));
                r.Text = Safe(() => text.GetState_ConvertedString()) ?? Safe(() => text.GetState_Text());
                break;
            case TObjectId.eFillObject when prim is IPCB_Fill fill:
                r.Geometry = new[] { ctx.X(Safe(() => fill.GetState_LocationX())), ctx.Y(Safe(() => fill.GetState_LocationY())) };
                r.WidthMils = Mils(Safe(() => fill.GetState_Width()));
                r.SizeMils = Mils(Safe(() => fill.GetState_Length()));
                break;
            case TObjectId.ePolyObject when prim is IPCB_Polygon poly:
            {
                CoordRect? b = Safe(() => prim.BoundingRectangle());
                r.Geometry = b == null ? null : ctx.Rect(b);
                r.Text = NullIfEmpty(Safe(() => poly.GetState_Name()));
                string? pt = EnumName(Safe(() => poly.GetState_PolygonType().ToString()));
                r.Detail = pt == null ? null : (Safe(() => poly.GetState_Poured()) ? pt + ", poured" : pt + ", not poured");
                break;
            }

            default:
            {
                CoordRect? b = Safe(() => prim.BoundingRectangle());
                r.Geometry = b == null ? null : ctx.Rect(b);
                if (prim is IPCB_Region region)
                {
                    r.Text = NullIfEmpty(Safe(() => region.GetState_Name()));
                }

                if (id == TObjectId.eViolationObject)
                {
                    r.Text = NullIfEmpty(Safe(() => prim.GetState_DescriptorString()));
                }

                break;
            }
        }

        return r;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static int CountMembers(IPCB_ObjectClass cls)
    {
        int n = 0;
        for (; n < 100000; n++)
        {
            int i = n;
            string? m = Safe(() => cls.GetState_MemberName(i));
            if (string.IsNullOrEmpty(m))
            {
                break;
            }
        }

        return n;
    }

    private static string LayerKind(TV6_Layer id, IPCB_LayerObject_V7 layer)
    {
        string s = id.ToString();
        if (s is "eV6_TopLayer" or "eV6_BottomLayer" || s.StartsWith("eV6_MidLayer", StringComparison.Ordinal))
        {
            return "Signal";
        }

        if (s.StartsWith("eV6_InternalPlane", StringComparison.Ordinal))
        {
            return "Plane";
        }

        return layer is IPCB_DielectricLayer ? "Dielectric" : "Other";
    }

    internal static string LayerId(TV6_Layer id)
    {
        string s = id.ToString();
        return s.StartsWith("eV6_", StringComparison.Ordinal) ? s.Substring(4) : s;
    }

    private static Dictionary<string, TObjectId> BuildTypeMap()
    {
        var map = new Dictionary<string, TObjectId>(StringComparer.OrdinalIgnoreCase);
        foreach (TObjectId id in Enum.GetValues(typeof(TObjectId)))
        {
            if (id == TObjectId.eNoObject)
            {
                continue;
            }

            map[TypeName(id)] = id;
        }

        return map;
    }

    /// <summary>eTrackObject -> "Track", ePolyObject -> "Polygon", eComponentBodyObject -> "ComponentBody".</summary>
    private static string TypeName(TObjectId id)
    {
        string s = id.ToString();
        if (s.StartsWith("e", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.EndsWith("Object", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 6);
        return s == "Poly" ? "Polygon" : s;
    }

    private static string NormalizeTypeName(string t)
    {
        string s = t.Trim();
        if (s.StartsWith("e", StringComparison.Ordinal) && s.Length > 1 && char.IsUpper(s[1])) s = s.Substring(1);
        if (s.EndsWith("Object", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 6);
        return s.Equals("Poly", StringComparison.OrdinalIgnoreCase) ? "Polygon" : s;
    }

    private static string Normalize(string s) => s.Replace(" ", string.Empty).Replace("_", string.Empty).ToUpperInvariant();

    private static string? EnumName(string? enumValue)
    {
        if (string.IsNullOrEmpty(enumValue)) return null;
        return enumValue.Length > 1 && enumValue[0] == 'e' && char.IsUpper(enumValue[1]) ? enumValue.Substring(1) : enumValue;
    }

    private static bool Eq(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static double Mils(int coord) => Math.Round(coord / 10000.0, 3);

    /// <summary>Resolved board plus origin and a layer-name cache; all iteration goes through here.</summary>
    private sealed class BoardContext
    {
        private readonly Dictionary<TV6_Layer, string> _layerNames = new();

        public IPCB_Board Board { get; private init; } = null!;
        public string Path { get; private init; } = string.Empty;
        public bool LoadedOnDemand { get; private init; }
        public int OriginX { get; private init; }
        public int OriginY { get; private init; }

        public static BoardContext Resolve(string? documentPath, bool loadIfClosed)
        {
            IPCB_ServerInterface pcb = PcbServer;
            IPCB_Board? board;
            string path;
            bool loaded = false;

            if (string.IsNullOrWhiteSpace(documentPath))
            {
                board = Safe(() => pcb.GetCurrentPCBBoard());
                if (board == null)
                {
                    throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, "No PCB document is active in the editor. Pass documentPath explicitly.",
                        new Dictionary<string, string> { ["hint"] = "Use project.getStructure to list .PcbDoc paths of the project." });
                }

                path = Safe(() => board.GetState_FileName()) ?? string.Empty;
            }
            else
            {
                path = documentPath;
                try
                {
                    path = System.IO.Path.GetFullPath(documentPath);
                }
                catch
                {
                    // keep as given
                }

                board = Safe(() => pcb.GetPCBBoardByPath(path));
                if (board == null)
                {
                    if (!File.Exists(path))
                    {
                        throw new BridgeException(BridgeErrorCodes.DocumentNotFound, $"PCB file not found: '{path}'.",
                            new Dictionary<string, string> { ["hint"] = "Use project.getStructure for exact document paths." });
                    }

                    if (!loadIfClosed)
                    {
                        throw new BridgeException(BridgeErrorCodes.DocumentNotOpen, $"Board is not open in the editor: '{path}'. Pass loadIfClosed=true to load it hidden.");
                    }

                    board = Safe(() => pcb.LoadPCBBoardByPath(path));
                    loaded = board != null;
                    if (board == null)
                    {
                        throw new BridgeException(BridgeErrorCodes.Internal, $"Altium could not load the PCB '{path}'.");
                    }
                }
            }

            return new BoardContext
            {
                Board = board,
                Path = path,
                LoadedOnDemand = loaded,
                OriginX = Safe(() => board.GetState_XOrigin()),
                OriginY = Safe(() => board.GetState_YOrigin()),
            };
        }

        public double X(int coord) => Math.Round((coord - OriginX) / 10000.0, 3);
        public double Y(int coord) => Math.Round((coord - OriginY) / 10000.0, 3);
        public double[] Rect(CoordRect r) => new[] { X(r.X1), Y(r.Y1), X(r.X2), Y(r.Y2) };

        /// <summary>Inverse of <see cref="X"/>/<see cref="Y"/>: mils relative to origin -> absolute internal units.</summary>
        public int ToAbsX(double mils) => (int)Math.Round(mils * 10000.0) + OriginX;
        public int ToAbsY(double mils) => (int)Math.Round(mils * 10000.0) + OriginY;

        public string LayerName(TV6_Layer layer)
        {
            if (_layerNames.TryGetValue(layer, out string? name))
            {
                return name;
            }

            IPCB_LayerStack? stack = Safe(() => Board.GetState_LayerStack());
            IPCB_LayerObject? obj = stack == null ? null : Safe(() => stack.LayerObject(layer));
            name = (obj == null ? null : NullIfEmpty(Safe(() => obj.GetState_LayerName()))) ?? LayerId(layer);
            _layerNames[layer] = name;
            return name;
        }

        public IEnumerable<IPCB_Primitive> Iterate(TObjectSet? filter, TIterationMethod method)
        {
            IPCB_BoardIterator? it = Safe(() => Board.BoardIterator_Create());
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
                else
                {
                    it.SetState_FilterAll();
                }

                it.AddFilter_AllLayers();
                it.AddFilter_Method(method);
                for (IPCB_Primitive? o = Safe(() => it.FirstPCBObject()); o != null; o = Safe(() => it.NextPCBObject()))
                {
                    yield return o;
                }
            }
            finally
            {
                try
                {
                    Board.BoardIterator_Destroy(ref it);
                }
                catch
                {
                    // ignore
                }
            }
        }

        public IEnumerable<IPCB_Primitive> IterateGroup(IPCB_Group group, TObjectSet? filter)
        {
            IPCB_GroupIterator? it = Safe(() => group.GroupIterator_Create());
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
                else
                {
                    it.SetState_FilterAll();
                }

                it.AddFilter_AllLayers();
                for (IPCB_Primitive? o = Safe(() => it.FirstPCBObject()); o != null; o = Safe(() => it.NextPCBObject()))
                {
                    yield return o;
                }
            }
            finally
            {
                try
                {
                    group.GroupIterator_Destroy(ref it);
                }
                catch
                {
                    // ignore
                }
            }
        }

        public static IPCB_ServerInterface Server => PcbServer;

        private static IPCB_ServerInterface PcbServer
        {
            get
            {
                IPCB_ServerInterface? s = PCB.GlobalVars.PCBServer;
                if (s == null)
                {
                    Safe(() => Client.StartServer("PCB"));
                    s = PCB.GlobalVars.PCBServer;
                }

                return s ?? throw new BridgeException(BridgeErrorCodes.Unsupported, "The PCB editor module is not loaded in this Altium instance.");
            }
        }
    }
}
