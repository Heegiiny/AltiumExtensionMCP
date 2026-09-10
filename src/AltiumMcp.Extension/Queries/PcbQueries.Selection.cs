using System;
using System.Collections.Generic;
using System.Linq;
using AltiumMcp.Contracts.Model;
using PCB;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

internal sealed partial class PcbQueries
{
    /// <summary>Reads the editor selection of a loaded board into the compact selection model.</summary>
    internal static void ReadSelection(string path, SelectionResult result, int limit)
    {
        IPCB_Board? board = Safe(() => BoardContext.Server.GetPCBBoardByPath(path));
        if (board == null)
        {
            result.Notes ??= new List<string>();
            result.Notes.Add("The board is not loaded in the PCB editor; nothing can be selected on it.");
            return;
        }

        var ctx = BoardContext.Resolve(path, loadIfClosed: false);
        result.Components ??= new List<SelectedComponent>();
        result.Objects ??= new List<SelectedItem>();
        result.CountsByType ??= new Dictionary<string, int>();
        var ids = new List<string>();
        var nets = new HashSet<string>(StringComparer.Ordinal);
        int count = Safe(() => board.SelectedObjectsCount());
        result.TotalSelected = count;
        for (int i = 0; i < count; i++)
        {
            IPCB_Primitive? prim = Safe(() => board.GetState_SelectecObject(i));
            if (prim == null)
            {
                continue;
            }

            TObjectId id = Safe(() => prim.GetState_ObjectID());
            string type = TypeName(id);
            result.CountsByType[type] = result.CountsByType.TryGetValue(type, out int n) ? n + 1 : 1;
            string uid = Safe(() => prim.GetState_UniqueId()) ?? string.Empty;
            ids.Add(type + ":" + (uid.Length > 0 ? uid : Safe(() => prim.GetState_Handle()) ?? i.ToString()));

            IPCB_Net? net = Safe(() => prim.GetState_Net());
            string? netName = net == null ? null : NullIfEmpty(Safe(() => net.GetState_Name()));
            if (netName != null)
            {
                nets.Add(netName);
            }

            if (prim is IPCB_Component c)
            {
                PcbComponentSummary s = ToSummary(ctx, c);
                var sc = new SelectedComponent
                {
                    Designator = s.Designator,
                    Id = s.Id,
                    SourceUniqueId = s.SourceUniqueId,
                    Comment = s.Comment,
                    LibraryReference = s.SourceLibReference,
                    Footprint = s.Footprint,
                    Layer = s.Layer,
                };

                var pinNets = new List<string>();
                foreach (IPCB_Primitive p in ctx.IterateGroup(c, new TObjectSet(TObjectId.ePadObject)))
                {
                    if (p is not IPCB_Pad pad) continue;
                    IPCB_Net? pn = Safe(() => pad.GetState_Net());
                    string? pnName = pn == null ? null : NullIfEmpty(Safe(() => pn.GetState_Name()));
                    pinNets.Add($"{Safe(() => pad.GetState_Name())}={pnName ?? "-"}");
                }

                sc.PinNets = pinNets.Count > 0 ? pinNets : null;
                result.Components.Add(sc);
                continue;
            }

            if (result.Objects.Count >= limit)
            {
                continue;
            }

            string? text = prim switch
            {
                IPCB_Pad pad => Safe(() => pad.GetState_PinDescriptorString()),
                IPCB_Text t => Safe(() => t.GetState_Text()),
                IPCB_Polygon poly => Safe(() => poly.GetState_Name()),
                IPCB_Via via => "via",
                _ => null,
            };
            IPCB_Primitive? owner = Safe(() => prim.GetState_Component());
            string? ownerName = null;
            if (owner is IPCB_Component oc)
            {
                IPCB_Text? nameText = Safe(() => oc.GetState_Name());
                ownerName = nameText == null ? null : Safe(() => nameText.GetState_Text());
            }

            result.Objects.Add(new SelectedItem
            {
                Type = type,
                Text = NullIfEmpty(text),
                Id = NullIfEmpty(uid),
                Net = netName,
                Owner = ownerName,
                Layer = ctx.LayerName(Safe(() => prim.GetState_Layer())),
            });
        }

        result.Nets = nets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        result.Revision = SchematicQueries.SelectionRevision(ids);
    }
}
