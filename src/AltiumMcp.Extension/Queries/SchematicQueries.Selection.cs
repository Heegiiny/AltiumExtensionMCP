using System;
using System.Collections.Generic;
using System.Linq;
using AltiumMcp.Contracts.Model;
using SCH;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

internal sealed partial class SchematicQueries
{
    /// <summary>Reads the editor selection of an open sheet into the compact selection model.</summary>
    internal static void ReadSelection(string path, SelectionResult result, int limit)
    {
        ISch_Document? doc = Safe(() => SchServer.GetSchDocumentByPath(path));
        if (doc == null)
        {
            result.Notes ??= new List<string>();
            result.Notes.Add("The sheet is not loaded in the SCH editor; nothing can be selected on it.");
            return;
        }

        result.Components ??= new List<SelectedComponent>();
        result.Objects ??= new List<SelectedItem>();
        result.CountsByType ??= new Dictionary<string, int>();
        var ids = new List<string>();
        var nets = new HashSet<string>(StringComparer.Ordinal);
        int wires = 0;
        foreach (ISch_BasicContainer o in Iterate(doc, null, TIterationDepth.eIterateAllLevels))
        {
            if (o is not ISch_GraphicalObject g || !Safe(() => g.GetState_Selection()))
            {
                continue;
            }

            TObjectId id = Safe(() => o.GetState_ObjectId());
            string type = TypeName(id);
            result.TotalSelected++;
            result.CountsByType[type] = result.CountsByType.TryGetValue(type, out int n) ? n + 1 : 1;
            string uid = Safe(() => o.GetState_UniqueId()) ?? string.Empty;
            ids.Add(type + ":" + uid);

            switch (id)
            {
                case TObjectId.eSchComponent when o is ISch_Component c:
                {
                    ISch_Designator? d = Safe(() => c.GetState_SchDesignator());
                    ISch_Parameter? cm = Safe(() => c.GetState_SchComment());
                    int parts = Safe(() => c.GetState_PartCountNoPart0());
                    var sc = new SelectedComponent
                    {
                        Designator = (d == null ? null : Safe(() => d.GetState_Text())) ?? string.Empty,
                        Id = uid,
                        Comment = cm == null ? null : NullIfEmpty(Safe(() => cm.GetState_Text())),
                        LibraryReference = NullIfEmpty(Safe(() => c.GetState_LibReference())),
                        PartId = parts > 1 ? Safe(() => c.GetState_CurrentPartID()) : null,
                    };

                    // Pin → net where the sheet knows it (hidden power pins carry their net name).
                    var pinNets = new List<string>();
                    foreach (ISch_BasicContainer child in Iterate(c, new TObjectSet(TObjectId.ePin), TIterationDepth.eIterateFirstLevel))
                    {
                        if (child is not ISch_Pin pin) continue;
                        string? hidden = NullIfEmpty(Safe(() => pin.GetState_HiddenNetName()));
                        if (hidden != null)
                        {
                            pinNets.Add($"{Safe(() => pin.GetState_Designator())}={hidden}");
                            nets.Add(hidden);
                        }
                    }

                    sc.PinNets = pinNets.Count > 0 ? pinNets : null;
                    result.Components.Add(sc);
                    break;
                }

                case TObjectId.eWire or TObjectId.eBus:
                    wires++;
                    break;

                default:
                {
                    string? text = null;
                    string? owner = null;
                    string? net = null;
                    switch (id)
                    {
                        case TObjectId.eNetLabel or TObjectId.ePowerObject or TObjectId.eCrossSheetConnector when o is ISch_Label lbl:
                            text = Safe(() => lbl.GetState_Text());
                            net = text;
                            break;
                        case TObjectId.ePort when o is ISch_Port port:
                            text = Safe(() => port.GetState_Name());
                            net = text;
                            break;
                        case TObjectId.eSheetEntry when o is ISch_SheetEntry se:
                            text = Safe(() => se.GetState_Name());
                            net = text;
                            ISch_SheetSymbol? ss = Safe(() => se.GetState_OwnerSchSheetSymbol());
                            if (ss != null)
                            {
                                ISch_SheetName? sn = Safe(() => ss.GetState_SchSheetName());
                                owner = sn == null ? null : Safe(() => sn.GetState_Text());
                            }
                            break;
                        case TObjectId.ePin when o is ISch_Pin pin:
                            text = Safe(() => pin.GetState_Designator());
                            net = NullIfEmpty(Safe(() => pin.GetState_HiddenNetName()));
                            ISch_Component? oc = Safe(() => pin.OwnerSchComponent());
                            if (oc != null)
                            {
                                ISch_Designator? od = Safe(() => oc.GetState_SchDesignator());
                                owner = od == null ? null : Safe(() => od.GetState_Text());
                            }
                            break;
                        case TObjectId.eSheetSymbol when o is ISch_SheetSymbol ss2:
                            ISch_SheetName? name = Safe(() => ss2.GetState_SchSheetName());
                            text = name == null ? null : Safe(() => name.GetState_Text());
                            break;
                        case TObjectId.eParameter when o is ISch_Parameter prm:
                            text = $"{Safe(() => prm.GetState_Name())}={Safe(() => prm.GetState_Text())}";
                            break;
                        case TObjectId.eDesignator when o is ISch_Designator des:
                            text = Safe(() => des.GetState_Text());
                            break;
                        default:
                            if (o is ISch_Label anyLabel)
                            {
                                text = Safe(() => anyLabel.GetState_Text());
                            }
                            break;
                    }

                    if (net != null)
                    {
                        nets.Add(net);
                    }

                    if (result.Objects.Count < limit)
                    {
                        result.Objects.Add(new SelectedItem { Type = type, Text = NullIfEmpty(text), Id = NullIfEmpty(uid), Net = net, Owner = owner });
                    }

                    break;
                }
            }
        }

        if (wires > 0)
        {
            result.Notes ??= new List<string>();
            result.Notes.Add($"{wires} wire/bus segment(s) selected: wires carry no name on the sheet; use the nets of the selected labels/pins or project.getNet.");
        }

        result.Nets = nets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        result.Revision = SelectionRevision(ids);
    }

    /// <summary>Order-independent short hash of the selected identities.</summary>
    internal static string SelectionRevision(List<string> ids)
    {
        ids.Sort(StringComparer.Ordinal);
        byte[] hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", ids)));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
