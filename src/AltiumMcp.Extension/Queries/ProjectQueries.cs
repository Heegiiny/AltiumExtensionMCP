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

    // ---- agent docs ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds AGENTS.md (project directory upward to the drive root) plus README/CLAUDE.md/.cursorrules next to the
    /// project. Read-only: the agent must follow these before analysing; writing them is a phase-2 mutation.
    /// </summary>
    public AgentDocsResult GetAgentDocs(GetAgentDocsParams? p)
    {
        p ??= new GetAgentDocsParams();
        int maxChars = Math.Clamp(p.MaxCharsPerFile <= 0 ? 20000 : p.MaxCharsPerFile, 500, 200000);
        IProject project = ResolveProject(p.ProjectPath);
        string projectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty;
        string root = System.IO.Path.GetDirectoryName(projectPath) ?? string.Empty;
        var result = new AgentDocsResult { ProjectPath = projectPath, ProjectRoot = root };
        if (root.Length == 0 || !System.IO.Directory.Exists(root))
        {
            result.Notes = new List<string> { "Project directory is not accessible; no documentation files could be searched." };
            return result;
        }

        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = Safe(() => project.DM_LogicalDocumentCount());
        for (int i = 0; i < n; i++)
        {
            IDocument? d = Safe(() => project.DM_LogicalDocuments(i));
            string? path = d == null ? null : Safe(() => d.DM_FullPath());
            if (!string.IsNullOrEmpty(path)) members.Add(path);
        }

        void Add(string file, string kind)
        {
            if (!System.IO.File.Exists(file) || result.Files.Any(f => PathEquals(f.Path, file))) return;
            try
            {
                var fi = new System.IO.FileInfo(file);
                string content = System.IO.File.ReadAllText(file);
                bool truncated = content.Length > maxChars;
                if (truncated) content = content.Substring(0, maxChars) + $"\n… [truncated: {fi.Length} bytes total; read the file directly for the rest]";
                result.Files.Add(new AgentDocFile
                {
                    Path = file,
                    FileName = fi.Name,
                    Kind = kind,
                    SizeBytes = fi.Length,
                    ModifiedAt = fi.LastWriteTime,
                    IsProjectMember = members.Contains(file),
                    Content = content,
                    Truncated = truncated,
                });
            }
            catch (Exception ex)
            {
                result.Notes ??= new List<string>();
                result.Notes.Add($"Could not read {file}: {ex.Message}");
            }
        }

        // AGENTS.md: project dir first, then upward (nearest wins but all are returned so the agent sees the chain).
        string? dir = root;
        int guard = 0;
        while (!string.IsNullOrEmpty(dir) && guard++ < 32)
        {
            result.SearchedDirectories.Add(dir);
            Add(System.IO.Path.Combine(dir, "AGENTS.md"), "agents");
            string? parent = System.IO.Path.GetDirectoryName(dir);
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase)) break;
            dir = parent;
        }

        if (p.IncludeReadme)
        {
            Add(System.IO.Path.Combine(root, "README.md"), "readme");
            Add(System.IO.Path.Combine(root, "CLAUDE.md"), "rules");
            Add(System.IO.Path.Combine(root, ".cursorrules"), "rules");
        }

        if (result.Files.Count == 0)
        {
            result.Notes ??= new List<string>();
            result.Notes.Add("No AGENTS.md (or README) found for this project. Creating one is a documentation write and needs an explicit request.");
        }

        return result;
    }

    // ---- components ----------------------------------------------------------------------------------------------

    /// <summary>Single lookup (project.getComponent). Batch form: <see cref="GetComponents"/>.</summary>
    public ComponentDetail GetComponent(GetComponentParams? p)
    {
        string? key = p == null ? null : InputNormalizer.Optional(p.Component);
        if (p == null || key == null)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'component' (UniqueId or designator) is required.");
        }

        string detail = ParseDetail(p.Detail);
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        IComponent found = FindComponent(flat, key) ?? throw NotFoundComponent(key);
        ComponentDetail result = ToComponentDetail(found, detail);
        if (CrossProbeService.ShouldProbe(p.CrossProbe))
        {
            result.CrossProbe = CrossProbeService.Request($"component {result.Summary.Designator}", () => found.DM_DoCrossProbe());
        }

        return result;
    }

    /// <summary>Batch lookup (project.getComponents): one compiled-model scan for all keys; unknown keys → notFound.</summary>
    public ComponentBatchResult GetComponents(GetComponentParams? p)
    {
        p ??= new GetComponentParams();
        List<string> keys = InputNormalizer.OptionalList(p.Components);
        string? single = InputNormalizer.Optional(p.Component);
        if (single != null && !keys.Contains(single, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(single);
        }

        if (keys.Count == 0)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'components' (list of UniqueIds or designators) is required.");
        }

        if (keys.Count > 100)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, $"Too many components in one call ({keys.Count}); max 100. Page the request.");
        }

        string detail = ParseDetail(p.Detail);
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        var result = new ComponentBatchResult { ProjectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty };

        var pending = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        IComponent? first = null;
        int count = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < count && pending.Count > 0; i++)
        {
            IComponent? c = Safe(() => flat.DM_Components(i));
            if (c == null)
            {
                continue;
            }

            string? hit = ComponentKeys(c).FirstOrDefault(pending.Contains);
            if (hit == null)
            {
                continue;
            }

            pending.Remove(hit);
            first ??= c;
            result.Components.Add(ToComponentDetail(c, detail));
        }

        result.NotFound.AddRange(keys.Where(pending.Contains));
        if (first != null && CrossProbeService.ShouldProbe(p.CrossProbe))
        {
            IComponent target = first;
            string status = CrossProbeService.Request($"component {result.Components[0].Summary.Designator} (first of batch)", () => target.DM_DoCrossProbe());
            result.Components[0].CrossProbe = status;
        }

        return result;
    }

    private static IEnumerable<string> ComponentKeys(IComponent c)
    {
        string? id = Safe(() => c.DM_UniqueId());
        if (!string.IsNullOrEmpty(id)) yield return id;
        string? phys = Safe(() => c.DM_PhysicalDesignator());
        if (!string.IsNullOrEmpty(phys)) yield return phys;
        string? logical = Safe(() => c.DM_LogicalDesignator());
        if (!string.IsNullOrEmpty(logical)) yield return logical;
    }

    private static IComponent? FindComponent(IDocument flat, string key)
    {
        int count = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < count; i++)
        {
            IComponent? c = Safe(() => flat.DM_Components(i));
            if (c != null && ComponentKeys(c).Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            {
                return c;
            }
        }

        return null;
    }

    private static BridgeException NotFoundComponent(string key) =>
        new(BridgeErrorCodes.ObjectNotFound, $"Component '{key}' not found in the compiled project.",
            new Dictionary<string, string> { ["hint"] = "Use project.listComponents (optionally with filter) to find valid ids/designators." });

    private static ComponentDetail ToComponentDetail(IComponent found, string detail)
    {
        bool full = detail == DetailLevel.Full;
        var result = new ComponentDetail { Summary = ToComponentSummary(found, includeParameters: false, full: full) };
        if (detail == DetailLevel.Summary)
        {
            return result;
        }

        result.Pins = new List<PinInfo>();
        int pins = Safe(() => found.DM_PinCount());
        for (int i = 0; i < pins; i++)
        {
            INetItem? pin = Safe(() => found.DM_Pins(i));
            if (pin == null)
            {
                continue;
            }

            result.Pins.Add(new PinInfo
            {
                Number = Safe(() => pin.DM_PinNumber()) ?? string.Empty,
                Name = NullIfEmpty(Safe(() => pin.DM_PinName())),
                Electrical = NullIfEmpty(Safe(() => pin.DM_ElectricalString())),
                Net = NullIfEmpty(Safe(() => pin.DM_FlattenedNetName())),
                PartId = result.Summary.SubPartCount > 1 ? Safe(() => pin.DM_PartID()) : 0,
                IsHidden = Safe(() => pin.DM_IsHidden()),
            });
        }

        if (!full)
        {
            return result;
        }

        result.Parameters = ReadParameters(found);
        result.Implementations = new List<ImplementationInfo>();
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
            bool duplicate = result.Implementations.Any(x =>
                string.Equals(x.ModelType, info.ModelType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ModelName, info.ModelName, StringComparison.OrdinalIgnoreCase)
                && x.IsCurrent == info.IsCurrent);
            if (!duplicate)
            {
                result.Implementations.Add(info);
            }
        }

        return result;
    }

    // ---- nets ----------------------------------------------------------------------------------------------------

    public NetListResult ListNets(ListNetsParams? p)
    {
        p ??= new ListNetsParams();
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        string projectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty;

        List<NetEntry> nets = IndexNets(flat);
        var all = new List<NetSummary>();
        foreach (NetEntry e in nets)
        {
            if (!Matches(p.Filter, e.Name, e.NetId))
            {
                continue;
            }

            all.Add(ToNetSummary(e, p.IncludePins ? DetailLevel.Connectivity : DetailLevel.Summary, preferredSheet: null));
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
        string? key = p == null ? null : InputNormalizer.Optional(p.Net);
        if (p == null || key == null)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'net' (net id or name) is required.");
        }

        string detail = ParseDetail(p.Detail);
        string? sheet = InputNormalizer.Optional(p.DocumentPath);
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        List<NetEntry> nets = IndexNets(flat);
        NetEntry entry = ResolveNet(nets, key, sheet);
        NetSummary result = ToNetSummary(entry, detail, sheet);
        if (CrossProbeService.ShouldProbe(p.CrossProbe))
        {
            INet target = entry.Net;
            result.CrossProbe = CrossProbeService.Request($"net {entry.Name}", () => target.DM_DoCrossProbe());
        }

        return result;
    }

    public NetBatchResult GetNets(GetNetParams? p)
    {
        p ??= new GetNetParams();
        List<string> keys = InputNormalizer.OptionalList(p.Nets);
        string? single = InputNormalizer.Optional(p.Net);
        if (single != null && !keys.Contains(single, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(single);
        }

        if (keys.Count == 0)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'nets' (list of net ids or names) is required.");
        }

        if (keys.Count > 100)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, $"Too many nets in one call ({keys.Count}); max 100. Page the request.");
        }

        string detail = ParseDetail(p.Detail);
        string? sheet = InputNormalizer.Optional(p.DocumentPath);
        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        List<NetEntry> nets = IndexNets(flat);
        var result = new NetBatchResult { ProjectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty };
        NetEntry? first = null;
        foreach (string key in keys)
        {
            try
            {
                NetEntry e = ResolveNet(nets, key, sheet);
                first ??= e;
                result.Nets.Add(ToNetSummary(e, detail, sheet));
            }
            catch (BridgeException ex) when (ex.Code == BridgeErrorCodes.ObjectNotFound)
            {
                result.NotFound.Add(key);
            }
            // AMBIGUOUS_OBJECT propagates: the caller must disambiguate that key.
        }

        if (first != null && CrossProbeService.ShouldProbe(p.CrossProbe))
        {
            INet target = first.Net;
            result.Nets[0].CrossProbe = CrossProbeService.Request($"net {first.Name} (first of batch)", () => target.DM_DoCrossProbe());
        }

        return result;
    }

    /// <summary>
    /// Finds the compiled net for a key. Exact netId wins; otherwise all nets whose display/calculated/full name
    /// matches are candidates. Several distinct candidates → AMBIGUOUS_OBJECT (unless <paramref name="preferredSheet"/>
    /// picks exactly one by participation).
    /// </summary>
    private static NetEntry ResolveNet(List<NetEntry> nets, string key, string? preferredSheet)
    {
        NetEntry? byId = nets.FirstOrDefault(n => string.Equals(n.NetId, key, StringComparison.OrdinalIgnoreCase));
        if (byId != null)
        {
            return byId;
        }

        List<NetEntry> candidates = nets.Where(n => n.NameKeys.Contains(key)).ToList();
        if (candidates.Count == 0)
        {
            throw new BridgeException(BridgeErrorCodes.ObjectNotFound, $"Net '{key}' not found in the compiled project.",
                new Dictionary<string, string> { ["hint"] = "Use project.listNets (optionally with filter) to find valid net ids/names." });
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (preferredSheet != null)
        {
            List<NetEntry> onSheet = candidates.Where(c => c.DocumentPaths.Any(d => PathEquals(d, preferredSheet) || string.Equals(System.IO.Path.GetFileName(d), preferredSheet, StringComparison.OrdinalIgnoreCase))).ToList();
            if (onSheet.Count == 1)
            {
                return onSheet[0];
            }
        }

        string candidatesJson = BridgeJson.Serialize(candidates.Select(c => new NetCandidate
        {
            NetId = c.NetId,
            Name = c.Name,
            Scope = c.Scope,
            PinCount = c.PinCount,
            DocumentPaths = c.DocumentPaths,
        }).ToList());
        throw new BridgeException(BridgeErrorCodes.AmbiguousObject,
            $"'{key}' names {candidates.Count} distinct compiled nets (same spelling, different scope/sheets). Pass a netId, or documentPath to pick the one on that sheet.",
            new Dictionary<string, string> { ["candidates"] = candidatesJson });
    }

    /// <summary>One compiled net with the identity/participation facts precomputed (one pass over its items).</summary>
    private sealed class NetEntry
    {
        public INet Net { get; init; } = null!;
        public string Name { get; init; } = string.Empty;
        public string NetId { get; set; } = string.Empty;
        public string? Scope { get; init; }
        public int PinCount { get; init; }
        public HashSet<string> NameKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DocumentPaths { get; } = new();
    }

    /// <summary>
    /// Scans the flattened nets once and assigns stable ids. NetId = full net name; when two distinct compiled nets
    /// still share the id (locals with equal names on different sheets), the sheet file name is appended.
    /// </summary>
    private static List<NetEntry> IndexNets(IDocument flat)
    {
        var entries = new List<NetEntry>();
        int count = Safe(() => flat.DM_NetCount());
        for (int i = 0; i < count; i++)
        {
            INet? n = Safe(() => flat.DM_Nets(i));
            if (n == null)
            {
                continue;
            }

            string name = Safe(() => n.DM_NetName()) ?? string.Empty;
            string? full = NullIfEmpty(Safe(() => n.DM_FullNetName()));
            string? calc = NullIfEmpty(Safe(() => n.DM_CalculatedNetName()));
            TNetScope scope = Safe(() => n.DM_Scope());
            var e = new NetEntry
            {
                Net = n,
                Name = name,
                NetId = full ?? name,
                Scope = ScopeName(scope),
                PinCount = Safe(() => n.DM_PinCount()),
            };
            e.NameKeys.Add(name);
            if (full != null) e.NameKeys.Add(full);
            if (calc != null) e.NameKeys.Add(calc);

            int items = Safe(() => n.DM_AllNetItemCount());
            for (int j = 0; j < items; j++)
            {
                INetItem? item = Safe(() => n.DM_AllNetItems(j));
                string? doc = item == null ? null : NullIfEmpty(Safe(() => item.DM_OwnerDocumentFullPath()));
                if (doc != null && !e.DocumentPaths.Any(d => PathEquals(d, doc)))
                {
                    e.DocumentPaths.Add(doc);
                }
            }

            entries.Add(e);
        }

        foreach (IGrouping<string, NetEntry> dup in entries.GroupBy(x => x.NetId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (NetEntry e in dup)
            {
                string sheet = e.DocumentPaths.Count > 0 ? System.IO.Path.GetFileName(e.DocumentPaths[0]) : "?";
                e.NetId = $"{e.NetId}@{sheet}";
            }
        }

        return entries;
    }

    /// <summary>TNetScope → "Local" (one sheet) | "Interface" (crosses sheets via ports/sheet entries or power ports) | "Global".</summary>
    private static string? ScopeName(TNetScope scope)
    {
        string s = scope.ToString();
        return s.StartsWith("eScope", StringComparison.Ordinal) ? s.Substring(6) : s;
    }

    private static NetSummary ToNetSummary(NetEntry e, string detail, string? preferredSheet)
    {
        INet n = e.Net;
        var s = new NetSummary
        {
            NetId = e.NetId,
            Name = e.Name,
            Scope = e.Scope,
            PinCount = e.PinCount,
            PortCount = Safe(() => n.DM_PortCount()),
            NetLabelCount = Safe(() => n.DM_NetLabelCount()),
            PowerObjectCount = Safe(() => n.DM_PowerObjectCount()),
            SheetEntryCount = Safe(() => n.DM_SheetEntryCount()),
            IsLocal = Safe(() => n.DM_IsLocal()),
            IsAutoGenerated = Safe(() => n.DM_IsAutoGenerated()),
            Electrical = NullIfEmpty(Safe(() => n.DM_ElectricalString())),
            DocumentPaths = new List<string>(e.DocumentPaths),
        };

        if (detail == DetailLevel.Summary)
        {
            return s;
        }

        bool full = detail == DetailLevel.Full;
        var segments = new Dictionary<string, NetSegment>(StringComparer.OrdinalIgnoreCase);
        NetSegment SegmentFor(INetItem item)
        {
            string doc = NullIfEmpty(Safe(() => item.DM_OwnerDocumentFullPath())) ?? "(unknown sheet)";
            if (!segments.TryGetValue(doc, out NetSegment? seg))
            {
                seg = new NetSegment { DocumentPath = doc };
                segments[doc] = seg;
            }

            return seg;
        }

        void AddText(Func<int> count, Func<int, INetItem?> get, Func<INetItem, string?> text, Func<NetSegment, List<string>> list, Action<NetSegment, List<string>> set)
        {
            int c = Safe(count);
            for (int i = 0; i < c; i++)
            {
                INetItem? item = Safe(() => get(i));
                if (item == null) continue;
                string? t = NullIfEmpty(Safe(() => text(item)));
                if (t == null) continue;
                NetSegment seg = SegmentFor(item);
                List<string>? l = list(seg);
                if (l == null)
                {
                    l = new List<string>();
                    set(seg, l);
                }

                if (!l.Contains(t, StringComparer.Ordinal)) l.Add(t);
            }
        }

        for (int i = 0; i < s.PinCount; i++)
        {
            INetItem? pin = Safe(() => n.DM_Pins(i));
            if (pin == null) continue;
            string number = Safe(() => pin.DM_PinNumber()) ?? string.Empty;
            string? pinName = NullIfEmpty(Safe(() => pin.DM_PinName()));
            NetSegment seg = SegmentFor(pin);
            seg.Pins ??= new List<NetPinRef>();
            seg.Pins.Add(new NetPinRef
            {
                Designator = Safe(() => pin.DM_PhysicalPartDesignator()) ?? Safe(() => pin.DM_LogicalPartDesignator()) ?? string.Empty,
                Pin = number,
                PinName = string.Equals(pinName, number, StringComparison.Ordinal) ? null : pinName, // "2"/"2" is noise
                Electrical = full ? NullIfEmpty(Safe(() => pin.DM_ElectricalString())) : null,
            });
        }

        AddText(() => n.DM_PortCount(), i => n.DM_Ports(i), it => it.DM_PortName() is { Length: > 0 } pn ? pn : it.DM_NetLabelText(), seg => seg.Ports!, (seg, l) => seg.Ports = l);
        AddText(() => n.DM_NetLabelCount(), i => n.DM_NetLabels(i), it => it.DM_NetLabelText() is { Length: > 0 } nl ? nl : it.DM_NetName(), seg => seg.NetLabels!, (seg, l) => seg.NetLabels = l);
        AddText(() => n.DM_PowerObjectCount(), i => n.DM_PowerObjects(i), it => it.DM_PowerText() is { Length: > 0 } pw ? pw : it.DM_NetName(), seg => seg.PowerObjects!, (seg, l) => seg.PowerObjects = l);
        AddText(() => n.DM_SheetEntryCount(), i => n.DM_SheetEntrys(i), it => it.DM_ParentSheetSymbolName() is { Length: > 0 } ss ? $"{ss}:{it.DM_NetLabelText()}" : it.DM_NetLabelText(), seg => seg.SheetEntries!, (seg, l) => seg.SheetEntries = l);
        AddText(() => n.DM_CrossSheetConnectorCount(), i => n.DM_CrossSheetConnectors(i) as INetItem, it => it.DM_CrossSheetText() is { Length: > 0 } cs ? cs : it.DM_NetName(), seg => seg.CrossSheetConnectors!, (seg, l) => seg.CrossSheetConnectors = l);

        // Sheets known from the index but without items in the typed lists (e.g. wires only) still get a segment.
        foreach (string doc in e.DocumentPaths.Where(d => !segments.ContainsKey(d)))
        {
            segments[doc] = new NetSegment { DocumentPath = doc };
        }

        s.Segments = segments.Values
            .OrderByDescending(seg => preferredSheet != null && (PathEquals(seg.DocumentPath, preferredSheet) || string.Equals(System.IO.Path.GetFileName(seg.DocumentPath), preferredSheet, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(seg => seg.DocumentPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (NetSegment seg in s.Segments)
        {
            seg.Pins?.Sort((a, b) => NaturalCompare(a.Designator + "." + a.Pin, b.Designator + "." + b.Pin));
        }

        return s;
    }

    // ---- trace ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Bounded breadth-first walk: component → its nets → components on those nets → … Power nets, excluded nets
    /// and high-fanout nets are listed but not expanded, so the result stays small and readable.
    /// </summary>
    public TraceResult Trace(TraceParams? p)
    {
        string? start = p == null ? null : InputNormalizer.Optional(p.Start);
        if (p == null || start == null)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, "'start' (component designator/id or net id/name) is required.");
        }

        int depth = Math.Clamp(p.Depth <= 0 ? 1 : p.Depth, 1, 3);
        int maxNets = Math.Clamp(p.MaxNets <= 0 ? 60 : p.MaxNets, 1, 500);
        int maxComponents = Math.Clamp(p.MaxComponents <= 0 ? 120 : p.MaxComponents, 1, 1000);
        int maxFanout = Math.Clamp(p.MaxFanout <= 0 ? 30 : p.MaxFanout, 2, 10000);
        var excluded = new HashSet<string>(InputNormalizer.OptionalList(p.ExcludeNets), StringComparer.OrdinalIgnoreCase);

        IProject project = ResolveProject(p.ProjectPath);
        IDocument flat = RequireFlattened(project, p.CompileIfNeeded);
        List<NetEntry> nets = IndexNets(flat);

        // Connectivity index: net → pins, component → (net, pin).
        var netPins = new Dictionary<NetEntry, List<(string Designator, string Pin)>>();
        var compNets = new Dictionary<string, List<(NetEntry Net, string Pin)>>(StringComparer.OrdinalIgnoreCase);
        var powerNets = new HashSet<NetEntry>();
        foreach (NetEntry e in nets)
        {
            var pins = new List<(string, string)>();
            int pc = e.PinCount;
            for (int i = 0; i < pc; i++)
            {
                INetItem? pin = Safe(() => e.Net.DM_Pins(i));
                if (pin == null) continue;
                string des = Safe(() => pin.DM_PhysicalPartDesignator()) ?? Safe(() => pin.DM_LogicalPartDesignator()) ?? string.Empty;
                string num = Safe(() => pin.DM_PinNumber()) ?? string.Empty;
                if (des.Length == 0) continue;
                pins.Add((des, num));
                if (!compNets.TryGetValue(des, out var list))
                {
                    list = new List<(NetEntry, string)>();
                    compNets[des] = list;
                }

                list.Add((e, num));
            }

            netPins[e] = pins;
            if (Safe(() => e.Net.DM_PowerObjectCount()) > 0)
            {
                powerNets.Add(e);
            }
        }

        // Component descriptors (designator → compiled component) for the components we report.
        var components = new Dictionary<string, IComponent>(StringComparer.OrdinalIgnoreCase);
        int cc = Safe(() => flat.DM_ComponentCount());
        for (int i = 0; i < cc; i++)
        {
            IComponent? c = Safe(() => flat.DM_Components(i));
            if (c == null) continue;
            string des = NullIfEmpty(Safe(() => c.DM_PhysicalDesignator())) ?? Safe(() => c.DM_LogicalDesignator()) ?? string.Empty;
            if (des.Length > 0) components.TryAdd(des, c);
            string? id = Safe(() => c.DM_UniqueId());
            if (!string.IsNullOrEmpty(id)) components.TryAdd(id, c);
        }

        var result = new TraceResult { ProjectPath = Safe(() => project.DM_ProjectFullPath()) ?? string.Empty, Start = start, Depth = depth, Notes = new List<string>() };
        var netLevel = new Dictionary<NetEntry, TraceNet>();
        var compLevel = new Dictionary<string, TraceComponent>(StringComparer.OrdinalIgnoreCase);
        var frontierNets = new List<NetEntry>();
        var frontierComps = new List<string>();

        string kind = (p.StartKind ?? string.Empty).Trim().ToLowerInvariant();
        IComponent? startComp = kind == "net" ? null : (components.TryGetValue(start, out IComponent? sc) ? sc : null);
        if (startComp != null)
        {
            result.StartKind = "component";
            string des = NullIfEmpty(Safe(() => startComp.DM_PhysicalDesignator())) ?? Safe(() => startComp.DM_LogicalDesignator()) ?? start;
            AddComponent(des, 0);
            frontierComps.Add(des);
        }
        else
        {
            if (kind == "component")
            {
                throw NotFoundComponent(start);
            }

            NetEntry startNet = ResolveNet(nets, start, null); // OBJECT_NOT_FOUND / AMBIGUOUS_OBJECT
            result.StartKind = "net";
            AddNet(startNet, 0, expand: true);
            frontierNets.Add(startNet);
        }

        // BFS: each hop = components → nets → components.
        for (int level = 1; level <= depth; level++)
        {
            if (frontierComps.Count > 0)
            {
                var nextNets = new List<NetEntry>();
                foreach (string des in frontierComps)
                {
                    if (!compNets.TryGetValue(des, out var links)) continue;
                    foreach ((NetEntry net, string _) in links)
                    {
                        if (netLevel.ContainsKey(net)) continue;
                        if (netLevel.Count >= maxNets) { result.Truncated = true; break; }
                        bool expand = ExpandAllowed(net, out string? why);
                        AddNet(net, level, expand, why);
                        if (expand) nextNets.Add(net);
                    }
                }

                frontierComps.Clear();
                frontierNets = nextNets;
            }

            if (frontierNets.Count > 0)
            {
                var nextComps = new List<string>();
                foreach (NetEntry net in frontierNets)
                {
                    foreach ((string des, string _) in netPins[net])
                    {
                        if (compLevel.ContainsKey(des)) continue;
                        if (compLevel.Count >= maxComponents) { result.Truncated = true; break; }
                        AddComponent(des, level);
                        nextComps.Add(des);
                    }
                }

                frontierNets.Clear();
                frontierComps = nextComps;
            }

            if (result.Truncated)
            {
                break;
            }
        }

        // Fill pin lists: nets show all pins when expanded; components show their pins on traced nets.
        foreach ((NetEntry net, TraceNet tn) in netLevel)
        {
            if (tn.NotExpanded == null)
            {
                tn.Pins = netPins[net].Select(x => $"{x.Designator}.{x.Pin}").OrderBy(x => x, Comparer<string>.Create(NaturalCompare)).ToList();
            }
        }

        foreach ((string des, TraceComponent tc) in compLevel)
        {
            if (!compNets.TryGetValue(des, out var links)) continue;
            tc.Pins = links.Where(l => netLevel.ContainsKey(l.Net)).Select(l => $"{l.Pin}={l.Net.Name}").Distinct().OrderBy(x => x, Comparer<string>.Create(NaturalCompare)).ToList();
        }

        result.Nets = netLevel.Values.OrderBy(x => x.Level).ThenBy(x => x.Name, Comparer<string>.Create(NaturalCompare)).ToList();
        result.Components = compLevel.Values.OrderBy(x => x.Level).ThenBy(x => x.Designator, Comparer<string>.Create(NaturalCompare)).ToList();
        if (result.Truncated)
        {
            result.Notes.Add($"Walk stopped at maxNets={maxNets} / maxComponents={maxComponents}; narrow with excludeNets or lower depth.");
        }

        if (result.Notes.Count == 0)
        {
            result.Notes = null;
        }

        return result;

        bool ExpandAllowed(NetEntry net, out string? why)
        {
            why = null;
            if (excluded.Contains(net.Name) || excluded.Contains(net.NetId)) why = "excluded";
            else if (!p.IncludePowerNets && powerNets.Contains(net)) why = "power";
            else if (netPins[net].Count > maxFanout) why = "fanout";
            return why == null;
        }

        void AddNet(NetEntry net, int level, bool expand, string? why = null)
        {
            netLevel[net] = new TraceNet { NetId = net.NetId, Name = net.Name, Level = level, PinCount = netPins[net].Count, NotExpanded = expand ? null : why };
        }

        void AddComponent(string des, int level)
        {
            components.TryGetValue(des, out IComponent? c);
            compLevel[des] = new TraceComponent
            {
                Designator = des,
                Id = c == null ? string.Empty : Safe(() => c.DM_UniqueId()) ?? string.Empty,
                Comment = c == null ? null : NullIfEmpty(Safe(() => c.DM_Comment())),
                LibraryReference = c == null ? null : NullIfEmpty(Safe(() => c.DM_LibraryReference())),
                DocumentPath = c == null ? null : NullIfEmpty(Safe(() => c.DM_OwnerDocumentFullPath())),
                Level = level,
            };
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static string ParseDetail(string? value)
    {
        try
        {
            return DetailLevel.Normalize(value);
        }
        catch (ArgumentException ex)
        {
            throw new BridgeException(BridgeErrorCodes.InvalidParams, ex.Message);
        }
    }

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

    /// <param name="full">Include low-value identity fields (source library, part type) — only for detail=full.</param>
    private static ComponentSummary ToComponentSummary(IComponent c, bool includeParameters, bool full = false)
    {
        var s = new ComponentSummary
        {
            Id = Safe(() => c.DM_UniqueId()) ?? string.Empty,
            // Physical designator is what appears on the board / BOM; fall back to logical for unannotated designs.
            Designator = NullIfEmpty(Safe(() => c.DM_PhysicalDesignator())) ?? Safe(() => c.DM_LogicalDesignator()) ?? string.Empty,
            Comment = NullIfEmpty(Safe(() => c.DM_Comment())),
            Description = NullIfEmpty(Safe(() => c.DM_Description())),
            LibraryReference = NullIfEmpty(Safe(() => c.DM_LibraryReference())),
            Footprint = NullIfEmpty(Safe(() => c.DM_Footprint())),
            DocumentPath = NullIfEmpty(Safe(() => c.DM_OwnerDocumentFullPath())),
            PinCount = Safe(() => c.DM_PinCount()),
            SubPartCount = Safe(() => c.DM_SubPartCount()),
        };

        // Logical designator only when it differs (multi-channel designs); otherwise it is noise.
        string? logical = NullIfEmpty(Safe(() => c.DM_LogicalDesignator()));
        if (logical != null && !string.Equals(logical, s.Designator, StringComparison.Ordinal))
        {
            s.LogicalDesignator = logical;
        }

        if (full)
        {
            s.SourceLibraryName = NullIfEmpty(Safe(() => c.DM_SourceLibraryName()));
            s.PartType = NullIfEmpty(Safe(() => c.DM_PartType()));
        }

        if (includeParameters)
        {
            s.Parameters = ReadParameters(c);
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
