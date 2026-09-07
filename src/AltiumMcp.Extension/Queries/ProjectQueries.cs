using System;
using System.Collections.Generic;
using System.Linq;
using AltiumMcp.Contracts.Bridge;
using AltiumMcp.Contracts.Model;
using AltiumMcp.Extension.Bridge;
using DXP;
using EDP;
using static AltiumMcp.Extension.Queries.AltiumAccess;

namespace AltiumMcp.Extension.Queries;

/// <summary>
/// Project-level reads based on the Workspace Manager compiled data model (EDP.IProject / IDocument / IComponent / INet).
/// The flattened document (IProject.DM_DocumentFlattened) is the single source for components and nets across
/// the whole hierarchy; it exists only after the project has been compiled (Altium 20+ compiles dynamically,
/// but a fresh project may still need an explicit compile).
/// </summary>
internal sealed class ProjectQueries
{
    private const int MaxLimit = 2000;
    private const int ViolationSampleSize = 10;

    public ProjectStructure GetStructure(ProjectQueryParams? p)
    {
        p ??= new ProjectQueryParams();
        IProject project = ResolveProject(p.ProjectPath);
        IProject? focused = Safe(() => Workspace.DM_FocusedProject());
        IDocument? focusedDoc = Safe(() => Workspace.DM_FocusedDocument());
        string? focusedDocPath = focusedDoc == null ? null : Safe(() => focusedDoc.DM_FullPath());

        EnsureCompiled(project, p.CompileIfNeeded, required: false);

        ProjectSummary summary = ToSummary(project, focused);
        var structure = new ProjectStructure { Project = summary };

        int logical = Safe(() => project.DM_LogicalDocumentCount());
        for (int i = 0; i < logical; i++)
        {
            IDocument? d = Safe(() => project.DM_LogicalDocuments(i));
            if (d != null)
            {
                structure.LogicalDocuments.Add(ToInfo(d, summary.Ref.Path, focusedDocPath, includeCounts: true));
            }
        }

        int generated = Safe(() => project.DM_GeneratedDocumentCount());
        for (int i = 0; i < generated; i++)
        {
            IDocument? d = Safe(() => project.DM_GeneratedDocuments(i));
            if (d != null)
            {
                structure.GeneratedDocuments.Add(ToRef(d, summary.Ref.Path));
            }
        }

        IDocument? top = Safe(() => project.DM_TopLevelPhysicalDocument());
        if (top != null)
        {
            structure.Hierarchy.Add(BuildHierarchy(top, depth: 0));
        }

        IDocument? flat = Safe(() => project.DM_DocumentFlattened());
        structure.IsCompiled = flat != null && Safe(() => flat.DM_ComponentCount()) >= 0 && !Safe(() => project.DM_NeedsCompile());

        int variants = Safe(() => project.DM_ProjectVariantCount());
        for (int i = 0; i < variants; i++)
        {
            IProjectVariant? v = Safe(() => project.DM_ProjectVariants(i));
            string? name = v == null ? null : Safe(() => v.DM_Name());
            if (!string.IsNullOrEmpty(name))
            {
                structure.Variants.Add(name);
            }
        }

        structure.Parameters = ReadParameters(project);

        IDocument? primary = Safe(() => project.DM_PrimaryImplementationDocument());
        structure.PrimaryImplementationDocument = primary == null ? null : NullIfEmpty(Safe(() => primary.DM_FullPath()));

        structure.Violations = ReadViolations(project);
        return structure;
    }

    public ComponentListResult ListComponents(ListComponentsParams? p)
    {
        p ??= new ListComponentsParams();
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        string projectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty;

        var all = new List<ComponentSummary>();
        int count = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < count; i++)
        {
            IComponent? c = Safe(() => flat.DM_Components(i));
            if (c == null)
            {
                continue;
            }

            ComponentSummary s = ToComponentSummary(c, includeParameters: p.IncludeParameters);
            if (!Matches(p.Filter, s.Designator, s.LogicalDesignator, s.Comment, s.LibraryReference, s.Footprint, s.Description))
            {
                continue;
            }

            all.Add(s);
        }

        all.Sort((a, b) => NaturalCompare(a.Designator, b.Designator));
        int limit = Math.Clamp(p.Limit <= 0 ? 200 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<ComponentSummary> page = all.Skip(offset).Take(limit).ToList();
        return new ComponentListResult
        {
            ProjectPath = projectPath,
            Total = all.Count,
            Offset = offset,
            Returned = page.Count,
            Components = page,
        };
    }

    public ComponentDetail GetComponent(GetComponentParams? p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Component))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'component' (UniqueId or designator) is required.");
        }

        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);

        IComponent? found = null;
        int count = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < count && found == null; i++)
        {
            IComponent? c = Safe(() => flat.DM_Components(i));
            if (c == null)
            {
                continue;
            }

            if (string.Equals(Safe(() => c.DM_UniqueId()), p.Component, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Safe(() => c.DM_LogicalDesignator()), p.Component, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Safe(() => c.DM_PhysicalDesignator()), p.Component, StringComparison.OrdinalIgnoreCase))
            {
                found = c;
            }
        }

        if (found == null)
        {
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Component '{p.Component}' not found in the compiled project.",
                new Dictionary<string, string> { ["hint"] = "Use project.listComponents (optionally with filter) to find valid ids/designators." });
        }

        var detail = new ComponentDetail
        {
            Summary = ToComponentSummary(found, includeParameters: false),
            Parameters = ReadParameters(found),
        };

        int pins = Safe(() => found.DM_PinCount());
        for (int i = 0; i < pins; i++)
        {
            INetItem? pin = Safe(() => found.DM_Pins(i));
            if (pin == null)
            {
                continue;
            }

            detail.Pins.Add(new PinInfo
            {
                Number = Safe(() => pin.DM_PinNumber()) ?? string.Empty,
                Name = NullIfEmpty(Safe(() => pin.DM_PinName())),
                Electrical = NullIfEmpty(Safe(() => pin.DM_ElectricalString())),
                Net = NullIfEmpty(Safe(() => pin.DM_FlattenedNetName())),
                PartId = Safe(() => pin.DM_PartID()),
                IsHidden = Safe(() => pin.DM_IsHidden()),
            });
        }

        int impls = Safe(() => found.DM_ImplementationCount());
        for (int i = 0; i < impls; i++)
        {
            IComponentImplementation? impl = Safe(() => found.DM_Implementations(i));
            if (impl == null)
            {
                continue;
            }

            var info = new ImplementationInfo
            {
                ModelType = NullIfEmpty(Safe(() => impl.DM_ModelType())),
                ModelName = NullIfEmpty(Safe(() => impl.DM_ModelName())),
                Description = NullIfEmpty(Safe(() => impl.DM_Description())),
                IsCurrent = Safe(() => impl.DM_IsCurrent()),
            };

            // Multi-part components report the same implementation once per sub-part; collapse duplicates.
            bool duplicate = detail.Implementations.Any(x =>
                string.Equals(x.ModelType, info.ModelType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ModelName, info.ModelName, StringComparison.OrdinalIgnoreCase)
                && x.IsCurrent == info.IsCurrent);
            if (!duplicate)
            {
                detail.Implementations.Add(info);
            }
        }

        return detail;
    }

    public NetListResult ListNets(ListNetsParams? p)
    {
        p ??= new ListNetsParams();
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        string projectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty;

        var all = new List<NetSummary>();
        int count = Safe(() => flat.DM_NetCount());
        for (int i = 0; i < count; i++)
        {
            INet? n = Safe(() => flat.DM_Nets(i));
            if (n == null)
            {
                continue;
            }

            NetSummary s = ToNetSummary(n, p.IncludePins);
            if (!Matches(p.Filter, s.Name))
            {
                continue;
            }

            all.Add(s);
        }

        all.Sort((a, b) => NaturalCompare(a.Name, b.Name));
        int limit = Math.Clamp(p.Limit <= 0 ? 200 : p.Limit, 1, MaxLimit);
        int offset = Math.Max(0, p.Offset);
        List<NetSummary> page = all.Skip(offset).Take(limit).ToList();
        return new NetListResult
        {
            ProjectPath = projectPath,
            Total = all.Count,
            Offset = offset,
            Returned = page.Count,
            Nets = page,
        };
    }

    public NetSummary GetNet(GetNetParams? p)
    {
        if (p == null || string.IsNullOrWhiteSpace(p.Net))
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'net' (net name) is required.");
        }

        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        int count = Safe(() => flat.DM_NetCount());
        for (int i = 0; i < count; i++)
        {
            INet? n = Safe(() => flat.DM_Nets(i));
            if (n == null)
            {
                continue;
            }

            if (string.Equals(Safe(() => n.DM_NetName()), p.Net, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Safe(() => n.DM_CalculatedNetName()), p.Net, StringComparison.OrdinalIgnoreCase))
            {
                return ToNetSummary(n, includePins: true);
            }
        }

        throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Net '{p.Net}' not found in the compiled project.",
            new Dictionary<string, string> { ["hint"] = "Use project.listNets (optionally with filter) to find valid net names." });
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static IDocument RequireFlattened(IProject project, bool compileIfNeeded)
    {
        EnsureCompiled(project, compileIfNeeded, required: true);
        IDocument? flat = Safe(() => project.DM_DocumentFlattened());
        if (flat == null)
        {
            throw new BridgeException(BridgeErrorCodes.NotCompiled,
                "The project has no compiled (flattened) data model yet.",
                new Dictionary<string, string> { ["hint"] = "Call again with compileIfNeeded=true, or compile/validate the project in Altium (Project > Validate PCB Project)." });
        }

        return flat;
    }

    private static void EnsureCompiled(IProject project, bool compileIfNeeded, bool required)
    {
        bool needs = Safe(() => project.DM_NeedsCompile());
        IDocument? flat = Safe(() => project.DM_DocumentFlattened());
        if ((needs || flat == null) && compileIfNeeded)
        {
            BridgeLog.Info("Compiling project on request (compileIfNeeded=true)");
            bool ok = Safe(() => project.DM_Compile());
            if (!ok && required)
            {
                BridgeLog.Warn("DM_Compile returned false");
            }
        }
    }

    private static HierarchyNode BuildHierarchy(IDocument doc, int depth)
    {
        var node = new HierarchyNode
        {
            Path = Safe(() => doc.DM_FullPath()) ?? string.Empty,
            FileName = Safe(() => doc.DM_FileName()) ?? string.Empty,
            InstanceName = NullIfEmpty(Safe(() => doc.DM_PhysicalInstanceName())),
            IndentLevel = Safe(() => doc.DM_IndentLevel()),
            ComponentCount = Safe(() => doc.DM_ComponentCount()),
            NetCount = Safe(() => doc.DM_NetCount()),
        };

        if (depth > 32)
        {
            return node; // safety against cyclic models
        }

        int children = Safe(() => doc.DM_ChildDocumentCount());
        for (int i = 0; i < children; i++)
        {
            IDocument? child = Safe(() => doc.DM_ChildDocuments(i));
            if (child != null)
            {
                node.Children.Add(BuildHierarchy(child, depth + 1));
            }
        }

        return node;
    }

    private static ViolationSummary? ReadViolations(IProject project)
    {
        int total = Safe(() => project.DM_ViolationCount());
        var summary = new ViolationSummary { Total = total };
        for (int i = 0; i < total; i++)
        {
            IViolation? v = Safe(() => project.DM_Violations(i));
            if (v == null)
            {
                continue;
            }

            string level = Safe(() => v.DM_ErrorLevel().ToString()) ?? "unknown";
            string kind = Safe(() => v.DM_ErrorKind().ToString()) ?? "unknown";
            summary.ByErrorLevel[level] = summary.ByErrorLevel.TryGetValue(level, out int n) ? n + 1 : 1;
            summary.ByErrorKind[kind] = summary.ByErrorKind.TryGetValue(kind, out int k) ? k + 1 : 1;

            // Sample prefers errors/fatals over warnings so the LLM sees the important ones first.
            bool isSevere = level.Contains("Error", StringComparison.OrdinalIgnoreCase) || level.Contains("Fatal", StringComparison.OrdinalIgnoreCase);
            if (summary.Sample.Count < ViolationSampleSize && (isSevere || total <= ViolationSampleSize))
            {
                IDMObject? related = Safe(() => v.DM_RelatedObjectCount()) > 0 ? Safe(() => v.DM_RelatedObjects(0)) : null;
                summary.Sample.Add(new ViolationInfo
                {
                    ErrorKind = kind,
                    ErrorLevel = level,
                    Description = NullIfEmpty(Safe(() => v.DM_DescriptorString())),
                    DocumentPath = related == null ? null : NullIfEmpty(Safe(() => related.DM_OwnerDocumentFullPath())),
                });
            }
        }

        return summary;
    }

    private static ComponentSummary ToComponentSummary(IComponent c, bool includeParameters)
    {
        var s = new ComponentSummary
        {
            Id = Safe(() => c.DM_UniqueId()) ?? string.Empty,
            // Physical designator is what appears on the board / BOM; fall back to logical for unannotated designs.
            Designator = NullIfEmpty(Safe(() => c.DM_PhysicalDesignator())) ?? Safe(() => c.DM_LogicalDesignator()) ?? string.Empty,
            LogicalDesignator = NullIfEmpty(Safe(() => c.DM_LogicalDesignator())),
            Comment = NullIfEmpty(Safe(() => c.DM_Comment())),
            Description = NullIfEmpty(Safe(() => c.DM_Description())),
            LibraryReference = NullIfEmpty(Safe(() => c.DM_LibraryReference())),
            SourceLibraryName = NullIfEmpty(Safe(() => c.DM_SourceLibraryName())),
            Footprint = NullIfEmpty(Safe(() => c.DM_Footprint())),
            PartType = NullIfEmpty(Safe(() => c.DM_PartType())),
            DocumentPath = NullIfEmpty(Safe(() => c.DM_OwnerDocumentFullPath())),
            PinCount = Safe(() => c.DM_PinCount()),
            SubPartCount = Safe(() => c.DM_SubPartCount()),
        };

        if (includeParameters)
        {
            s.Parameters = ReadParameters(c);
        }

        return s;
    }

    private static NetSummary ToNetSummary(INet n, bool includePins)
    {
        var s = new NetSummary
        {
            Name = Safe(() => n.DM_NetName()) ?? string.Empty,
            PinCount = Safe(() => n.DM_PinCount()),
            PortCount = Safe(() => n.DM_PortCount()),
            NetLabelCount = Safe(() => n.DM_NetLabelCount()),
            PowerObjectCount = Safe(() => n.DM_PowerObjectCount()),
            IsLocal = Safe(() => n.DM_IsLocal()),
            IsAutoGenerated = Safe(() => n.DM_IsAutoGenerated()),
            Electrical = NullIfEmpty(Safe(() => n.DM_ElectricalString())),
            DocumentPath = NullIfEmpty(Safe(() => n.DM_OwnerDocumentFullPath())),
        };

        if (includePins)
        {
            s.Pins = new List<NetPinRef>();
            for (int i = 0; i < s.PinCount; i++)
            {
                INetItem? pin = Safe(() => n.DM_Pins(i));
                if (pin == null)
                {
                    continue;
                }

                s.Pins.Add(new NetPinRef
                {
                    Designator = Safe(() => pin.DM_PhysicalPartDesignator()) ?? Safe(() => pin.DM_LogicalPartDesignator()) ?? string.Empty,
                    Pin = Safe(() => pin.DM_PinNumber()) ?? string.Empty,
                    PinName = NullIfEmpty(Safe(() => pin.DM_PinName())),
                    Electrical = NullIfEmpty(Safe(() => pin.DM_ElectricalString())),
                });
            }
        }

        return s;
    }

    private static bool Matches(string? filter, params string?[] fields) => FilterMatcher.Matches(filter, fields);

    /// <summary>Natural sort so R2 &lt; R10.</summary>
    private static int NaturalCompare(string a, string b)
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
