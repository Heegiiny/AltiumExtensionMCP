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

/// <summary>pcb.select — highlight components / nets / pads in the PCB editor (editor state only).</summary>
internal sealed partial class PcbQueries
{
    public SelectResult Select(SelectParams? p)
    {
        p ??= new SelectParams();
        var notes = new List<string>();

        // Show the document first so the selection is visible and the board has a graphical view to zoom.
        string? path = string.IsNullOrWhiteSpace(p.DocumentPath) ? null : System.IO.Path.GetFullPath(p.DocumentPath);
        if (path != null && File.Exists(path) && p.Focus)
        {
            EditorCommands.Show(path, "PCB", true);
        }

        var ctx = BoardContext.Resolve(path, loadIfClosed: true);
        IPCB_Board board = ctx.Board;
        IServerDocument? serverDoc = Safe(() => Client.GetDocumentByPath(ctx.Path));
        if (serverDoc == null && p.Focus)
        {
            serverDoc = EditorCommands.Show(ctx.Path, "PCB", true);
        }

        if (serverDoc == null)
        {
            notes.Add("The board is not open in an editor window; the selection is applied but not visible until it is opened.");
        }

        var result = new SelectResult { DocumentPath = ctx.Path, IsOpenInEditor = serverDoc != null };
        var targets = new List<IPCB_Primitive>();

        Safe(() => { board.SelectedObjects_BeginUpdate(); return true; });
        try
        {
            if (p.ClearFirst)
            {
                Safe(() => { board.SelectedObjects_Clear(); return true; });
            }

            foreach (string designator in Distinct(p.Components))
            {
                IPCB_Component? c = FindComponent(ctx, designator);
                if (c == null)
                {
                    result.NotFound.Add(designator);
                    continue;
                }

                result.Matched.Add(designator);
                targets.Add(c);
            }

            if (p.Nets is { Count: > 0 })
            {
                var wanted = new HashSet<string>(Distinct(p.Nets), StringComparer.OrdinalIgnoreCase);
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var types = new TObjectSet(TObjectId.ePadObject, TObjectId.eTrackObject, TObjectId.eArcObject, TObjectId.eViaObject, TObjectId.ePolyObject, TObjectId.eRegionObject, TObjectId.eFillObject);
                foreach (IPCB_Primitive prim in ctx.Iterate(types, TIterationMethod.eProcessAll))
                {
                    IPCB_Net? n = Safe(() => prim.GetState_Net());
                    string? name = n == null ? null : Safe(() => n.GetState_Name());
                    if (name != null && wanted.Contains(name))
                    {
                        found.Add(name);
                        targets.Add(prim);
                    }
                }

                foreach (string w in wanted)
                {
                    (found.Contains(w) ? result.Matched : result.NotFound).Add(w);
                }
            }

            if (p.Objects is { Count: > 0 })
            {
                var wanted = new HashSet<string>(Distinct(p.Objects), StringComparer.OrdinalIgnoreCase);
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.ePadObject), TIterationMethod.eProcessAll))
                {
                    if (prim is not IPCB_Pad pad) continue;
                    string? desc = Safe(() => pad.GetState_PinDescriptorString());
                    if (desc != null && wanted.Contains(desc))
                    {
                        found.Add(desc);
                        targets.Add(prim);
                    }
                }

                foreach (string w in wanted)
                {
                    (found.Contains(w) ? result.Matched : result.NotFound).Add(w);
                }
            }

            foreach (IPCB_Primitive t in targets)
            {
                IPCB_Primitive prim = t;
                Safe(() => { prim.SetState_Selected(true); return true; });
                Safe(() => { board.SelectedObjects_Add(prim); return true; });
            }
        }
        finally
        {
            Safe(() => { board.SelectedObjects_EndUpdate(); return true; });
        }

        result.SelectedCount = Safe(() => board.SelectedObjectsCount());
        result.Bounds = SelectionBounds(ctx, board);

        Safe(() => { board.ViewManager_FullUpdate(); return true; });

        if (p.ZoomTo && result.Bounds != null && serverDoc != null)
        {
            double[] b = result.Bounds;
            double margin = Math.Max(100, 0.15 * Math.Max(b[2] - b[0], b[3] - b[1]));
            bool zoomed = Safe(() =>
            {
                board.GraphicalView_ZoomOnRect(ctx.ToAbsX(b[0] - margin), ctx.ToAbsY(b[1] - margin), ctx.ToAbsX(b[2] + margin), ctx.ToAbsY(b[3] + margin));
                return true;
            });
            if (!zoomed)
            {
                zoomed = EditorCommands.Run("PCB:Zoom", "Action=Selected", serverDoc);
            }

            Safe(() => { board.GraphicalView_ZoomRedraw(); return true; });
            result.Zoomed = zoomed;
        }

        if (result.NotFound.Count > 0)
        {
            notes.Add("Unmatched targets: check designators with pcb.listComponents, net names with pcb.listNets, pad descriptors look like 'U1-3'.");
        }

        if (!Distinct(p.Components).Any() && !Distinct(p.Nets).Any() && !Distinct(p.Objects).Any() && p.ClearFirst)
        {
            notes.Add("No targets given: selection cleared.");
        }

        result.Notes = notes.Count > 0 ? notes : null;
        return result;
    }

    private static IPCB_Component? FindComponent(BoardContext ctx, string key)
    {
        IPCB_Component? c = Safe(() => ctx.Board.GetPcbComponentByRefDes(key));
        if (c != null)
        {
            return c;
        }

        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eComponentObject), TIterationMethod.eProcessAll))
        {
            if (prim is not IPCB_Component cc) continue;
            IPCB_Text? name = Safe(() => cc.GetState_Name());
            string? des = name == null ? null : Safe(() => name.GetState_Text());
            if (Eq(des, key) || Eq(Safe(() => cc.GetState_UniqueId()), key) || Eq(Safe(() => cc.GetState_SourceUniqueId()), key))
            {
                return cc;
            }
        }

        return null;
    }

    private static double[]? SelectionBounds(BoardContext ctx, IPCB_Board board)
    {
        int count = Safe(() => board.SelectedObjectsCount());
        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            IPCB_Primitive? prim = Safe(() => board.GetState_SelectecObject(idx));
            CoordRect? r = prim == null ? null : Safe(() => prim.BoundingRectangle());
            if (r == null) continue;
            double[] b = ctx.Rect(r);
            x1 = Math.Min(x1, b[0]); y1 = Math.Min(y1, b[1]); x2 = Math.Max(x2, b[2]); y2 = Math.Max(y2, b[3]);
        }

        return x1 == double.MaxValue ? null : new[] { x1, y1, x2, y2 };
    }

    private static IEnumerable<string> Distinct(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}
