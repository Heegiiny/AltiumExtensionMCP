using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using PCB;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>pcb.runDrc / pcb.listViolations — design rule check results as structured data.</summary>
internal sealed partial class PcbQueries
{
    // ---- pcb.listViolations ---------------------------------------------------------------------------------------

    public PcbDrcResult ListViolations(ListViolationsParams? p)
    {
        p ??= new ListViolationsParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);
        var result = new PcbDrcResult { DocumentPath = ctx.Path, Ran = false };
        CollectViolations(ctx, result, p.Filter, p.Offset, p.Limit);
        if (result.ViolationCount == 0)
        {
            result.Notes = new List<string>
            {
                "No violation markers are stored on this board. Either the design is clean or no DRC has been run since the last change — use pcb.runDrc to know for sure.",
            };
        }

        return result;
    }

    // ---- pcb.runDrc -----------------------------------------------------------------------------------------------

    public PcbDrcResult RunDrc(RunDrcParams? p)
    {
        p ??= new RunDrcParams();
        var ctx = BoardContext.Resolve(p.DocumentPath, p.LoadIfClosed);

        string report = ResolveReportPath(p.ReportPath, ctx.Path);
        TDRCReportFileFormat format = report.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || report.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            ? TDRCReportFileFormat.eDRC_HTML
            : TDRCReportFileFormat.eDRC_Text;

        var sw = Stopwatch.StartNew();
        bool ok;
        try
        {
            ok = ctx.Board.RunBatchDesignRuleCheck(report, format, false, false);
        }
        catch (Exception ex)
        {
            throw new BridgeException(BridgeErrorCodes.Internal, $"Batch DRC failed: {ex.Message}",
                new Dictionary<string, string> { ["exception"] = ex.GetType().FullName ?? "Exception" });
        }

        sw.Stop();

        var result = new PcbDrcResult
        {
            DocumentPath = ctx.Path,
            Ran = true,
            RunSucceeded = ok,
            ReportPath = File.Exists(report) ? report : null,
            DurationMs = sw.ElapsedMilliseconds,
        };

        CollectViolations(ctx, result, null, 0, p.Limit);

        var notes = new List<string>();
        if (!ok)
        {
            notes.Add("Altium reported the DRC run as unsuccessful (RunBatchDesignRuleCheck returned false); violation counts below may be stale.");
        }

        if (result.ReportPath == null)
        {
            notes.Add("No report file was written; the violation list comes from the board's violation objects.");
        }

        if (ctx.LoadedOnDemand)
        {
            notes.Add("The board was loaded hidden; DRC ran on the file as saved on disk.");
        }

        notes.Add("The DRC refreshed the violation markers on the board (editor state). Design geometry was not modified.");
        result.Notes = notes;
        return result;
    }

    // ---- shared ---------------------------------------------------------------------------------------------------

    private static void CollectViolations(BoardContext ctx, PcbDrcResult result, string? filter, int offset, int limit)
    {
        var all = new List<PcbViolationInfo>();
        foreach (IPCB_Primitive prim in ctx.Iterate(new TObjectSet(TObjectId.eViolationObject), TIterationMethod.eProcessAll))
        {
            if (prim is not IPCB_Violation v)
            {
                continue;
            }

            PcbViolationInfo info = ToViolation(ctx, prim, v);
            result.ViolationCount++;
            result.ByRule[info.Rule] = result.ByRule.TryGetValue(info.Rule, out int r) ? r + 1 : 1;
            result.ByKind[info.Kind] = result.ByKind.TryGetValue(info.Kind, out int k) ? k + 1 : 1;
            if (FilterMatcher.Matches(filter, info.Rule, info.Kind, info.Description))
            {
                all.Add(info);
            }
        }

        all.Sort((a, b) =>
        {
            int c = string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.Rule, b.Rule, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Description, b.Description, StringComparison.OrdinalIgnoreCase);
        });

        int lim = Math.Clamp(limit <= 0 ? 200 : limit, 1, MaxLimit);
        int off = Math.Max(0, offset);
        result.Offset = off;
        result.Violations = all.Skip(off).Take(lim).ToList();
        result.Returned = result.Violations.Count;
    }

    private static PcbViolationInfo ToViolation(BoardContext ctx, IPCB_Primitive prim, IPCB_Violation v)
    {
        IPCB_Primitive? ruleObj = Safe(() => v.GetState_Rule());
        var rule = ruleObj as IPCB_Rule;
        IPCB_Primitive? p1 = Safe(() => v.GetState_Primitive1());
        IPCB_Primitive? p2 = Safe(() => v.GetState_Primitive2());
        CoordRect? b = Safe(() => prim.BoundingRectangle());
        TV6_Layer layer = Safe(() => prim.GetState_Layer());

        string? net = null;
        foreach (IPCB_Primitive? q in new[] { p1, p2 })
        {
            IPCB_Net? n = q == null ? null : Safe(() => q.GetState_Net());
            net = n == null ? null : NullIfEmpty(Safe(() => n.GetState_Name()));
            if (net != null) break;
        }

        return new PcbViolationInfo
        {
            Rule = (rule == null ? null : NullIfEmpty(Safe(() => rule.GetState_Name()))) ?? NullIfEmpty(Safe(() => v.GetState_Name())) ?? "(unknown rule)",
            Kind = rule == null ? "Unknown" : EnumName(Safe(() => rule.GetState_RuleKind().ToString()))?.Replace("Rule_", string.Empty) ?? "Unknown",
            Description = NullIfEmpty(Safe(() => v.GetState_Description())) ?? NullIfEmpty(Safe(() => prim.GetState_DescriptorString())),
            Layer = layer == TV6_Layer.eV6_NoLayer ? null : ctx.LayerName(layer),
            Bounds = b == null ? null : ctx.Rect(b),
            Primitive1 = p1 == null ? null : NullIfEmpty(Safe(() => p1.GetState_DescriptorString())),
            Primitive2 = p2 == null ? null : NullIfEmpty(Safe(() => p2.GetState_DescriptorString())),
            Net = net,
        };
    }

    private static string ResolveReportPath(string? requested, string documentPath)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            string full = Path.GetFullPath(requested);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            return full;
        }

        string dir = Path.Combine(BridgeDiscovery.DataDirectory, "reports");
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(documentPath);
        if (string.IsNullOrEmpty(name)) name = "board";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return Path.Combine(dir, $"{name}-DRC-{stamp}.txt");
    }
}
