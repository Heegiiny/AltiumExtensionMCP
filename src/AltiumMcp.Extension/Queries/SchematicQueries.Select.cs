using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Model;
using DXP;
using SCH;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>sch.select — highlight components / net-carrying objects / arbitrary objects on a sheet (editor state only).</summary>
internal sealed partial class SchematicQueries
{
    private static readonly TObjectSet NetCarrierTypes = new(TObjectId.eNetLabel, TObjectId.ePort, TObjectId.ePowerObject, TObjectId.eCrossSheetConnector, TObjectId.eSheetSymbol);

    public SelectResult Select(SelectParams? p)
    {
        p ??= new SelectParams();
        var notes = new List<string>();

        string path = DocumentResolver.Resolve(p.DocumentPath, DocumentResolver.Sch).FullPath;
        if (p.Focus)
        {
            EditorCommands.Show(path, "SCH", true);
        }

        (ISch_Document doc, string docPath, _) = ResolveSheet(path, loadIfClosed: true);
        IServerDocument? serverDoc = Safe(() => Client.GetDocumentByPath(docPath));
        if (serverDoc == null && p.Focus)
        {
            serverDoc = EditorCommands.Show(docPath, "SCH", true);
        }

        if (serverDoc == null)
        {
            notes.Add("The sheet is not open in an editor window; the selection is applied but not visible until it is opened.");
        }

        var result = new SelectResult { DocumentPath = docPath, IsOpenInEditor = serverDoc != null };
        var targets = new List<ISch_GraphicalObject>();

        if (p.ClearFirst)
        {
            ClearSelection(doc);
        }

        // Components: logical designator (all parts), part designator (U2A), physical designator, UniqueId.
        var compKeys = Distinct(p.Components).ToList();
        if (compKeys.Count > 0)
        {
            // Physical designators (post-annotation "U2" while the sheet says "U3") and compiled hierarchical ids
            // ("\A\B\PGMTOHJG") live in the compiled model only, so map each key to the sheet-level aliases it denotes.
            Dictionary<string, List<CompiledComponentRef>> compiled = CompiledComponentsOfSheet(docPath);
            var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in compKeys)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
                if (key.Contains('\\')) set.Add(key[(key.LastIndexOf('\\') + 1)..]);
                foreach (CompiledComponentRef r in compiled.Values.SelectMany(l => l))
                {
                    if (Eq(r.PhysicalDesignator, key) || Eq(r.UniqueId, key))
                    {
                        set.Add(r.UniqueIdTail);
                        if (!string.IsNullOrEmpty(r.LogicalDesignator)) set.Add(r.LogicalDesignator!);
                    }
                }

                aliases[key] = set;
            }

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ISch_BasicContainer o in Iterate(doc, new TObjectSet(TObjectId.eSchComponent), TIterationDepth.eIterateFirstLevel))
            {
                if (o is not ISch_Component c) continue;
                ISch_Designator? d = Safe(() => c.GetState_SchDesignator());
                string? logical = d == null ? null : Safe(() => d.GetState_Text());
                string? physical = d == null ? null : Safe(() => d.GetState_PhysicalDesignator());
                int partId = Safe(() => c.GetState_CurrentPartID());
                string? part = Safe(() => c.FullPartDesignator(partId));
                // Fallback for multi-part symbols: "U2" + part letter (part 1 = A) when the SDK does not compose it.
                string? partAlt = logical != null && partId >= 1 && partId <= 26 ? logical + (char)('A' + partId - 1) : null;
                string? uid = Safe(() => c.GetState_UniqueId());
                bool added = false;
                foreach (string key in compKeys)
                {
                    HashSet<string> keys = aliases[key];
                    if (keys.Any(k => Eq(logical, k) || Eq(physical, k) || Eq(part, k) || Eq(partAlt, k) || Eq(uid, k)))
                    {
                        found.Add(key);
                        if (!added)
                        {
                            targets.Add(c);
                            added = true;
                        }
                    }
                }
            }

            foreach (string k in compKeys)
            {
                (found.Contains(k) ? result.Matched : result.NotFound).Add(k);
            }
        }

        // Nets: objects whose text is the net name (labels, ports, power objects, cross-sheet connectors, sheet entries).
        var netKeys = new HashSet<string>(Distinct(p.Nets), StringComparer.OrdinalIgnoreCase);
        if (netKeys.Count > 0)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ISch_BasicContainer o in Iterate(doc, NetCarrierTypes, TIterationDepth.eIterateFirstLevel))
            {
                TObjectId id = Safe(() => o.GetState_ObjectId());
                if (id == TObjectId.eSheetSymbol)
                {
                    foreach (ISch_BasicContainer child in Iterate(o, new TObjectSet(TObjectId.eSheetEntry), TIterationDepth.eIterateAllLevels))
                    {
                        string? name = child is ISch_SheetEntry se ? Safe(() => se.GetState_Name()) : null;
                        if (name != null && netKeys.Contains(name) && child is ISch_GraphicalObject g)
                        {
                            found.Add(name);
                            targets.Add(g);
                        }
                    }

                    continue;
                }

                string? text = o is ISch_Port port ? Safe(() => port.GetState_Name()) : Safe(() => o.GetState_Text());
                if (text != null && netKeys.Contains(text) && o is ISch_GraphicalObject go)
                {
                    found.Add(text);
                    targets.Add(go);
                }
            }

            foreach (string k in netKeys)
            {
                (found.Contains(k) ? result.Matched : result.NotFound).Add(k);
            }
        }

        // Objects by UniqueId (first level + children).
        var idKeys = new HashSet<string>(Distinct(p.Objects), StringComparer.OrdinalIgnoreCase);
        if (idKeys.Count > 0)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ISch_BasicContainer o in Iterate(doc, null, TIterationDepth.eIterateAllLevels))
            {
                string? uid = Safe(() => o.GetState_UniqueId());
                if (uid != null && idKeys.Contains(uid) && o is ISch_GraphicalObject g)
                {
                    found.Add(uid);
                    targets.Add(g);
                }
            }

            foreach (string k in idKeys)
            {
                (found.Contains(k) ? result.Matched : result.NotFound).Add(k);
            }
        }

        double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
        foreach (ISch_GraphicalObject t in targets)
        {
            ISch_GraphicalObject g = t;
            Safe(() => { g.SetState_Selection(true); return true; });
            Safe(() => { g.GraphicallyInvalidate(); return true; });
            CoordRect? r = Safe(() => g.BoundingRectangle());
            if (r == null) continue;
            x1 = Math.Min(x1, Mils(r.X1)); y1 = Math.Min(y1, Mils(r.Y1)); x2 = Math.Max(x2, Mils(r.X2)); y2 = Math.Max(y2, Mils(r.Y2));
        }

        result.SelectedCount = CountSelected(doc);
        result.Bounds = x1 == double.MaxValue ? null : new[] { x1, y1, x2, y2 };

        Safe(() => { doc.UpdateDisplayForCurrentSheet(); return true; });

        if (p.ZoomTo && targets.Count > 0 && serverDoc != null)
        {
            result.Zoomed = EditorCommands.Run("Sch:Zoom", "Object=Selected", serverDoc);
        }
        else if (serverDoc != null)
        {
            EditorCommands.Run("Sch:Zoom", "Action=Redraw", serverDoc);
        }

        if (result.NotFound.Count > 0)
        {
            notes.Add("Unmatched targets: check designators with sch.listObjects (types [\"Component\"]) or sch.getComponent; nets match the text of net labels/ports/power objects/sheet entries on this sheet.");
        }

        if (compKeys.Count == 0 && netKeys.Count == 0 && idKeys.Count == 0 && p.ClearFirst)
        {
            notes.Add("No targets given: selection cleared.");
        }

        result.Notes = notes.Count > 0 ? notes : null;
        return result;
    }

    private static void ClearSelection(ISch_Document doc)
    {
        foreach (ISch_BasicContainer o in Iterate(doc, null, TIterationDepth.eIterateAllLevels))
        {
            if (o is ISch_GraphicalObject g && Safe(() => g.GetState_Selection()))
            {
                Safe(() => { g.SetState_Selection(false); return true; });
                Safe(() => { g.GraphicallyInvalidate(); return true; });
            }
        }
    }

    private static int CountSelected(ISch_Document doc)
    {
        int n = 0;
        foreach (ISch_BasicContainer o in Iterate(doc, null, TIterationDepth.eIterateAllLevels))
        {
            if (o is ISch_GraphicalObject g && Safe(() => g.GetState_Selection()))
            {
                n++;
            }
        }

        return n;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string>? items) =>
        (items ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
}
