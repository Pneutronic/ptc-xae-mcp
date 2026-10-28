using System.Diagnostics;
using System.Runtime.InteropServices;
using PtcXaeMcp.WinForms.TwinCat.HostSelection;
using EnvDTE80;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession : ITwinCatSession
{
    // Resolved once via TwinCatPaths (issue #157 item 9: registry → %TwinCAT3Dir%
    // → default, single source of truth) instead of a hardcoded path.
    private static readonly string XaeExePath = TwinCatPaths.GetXaeShellExePath();

    private static readonly XaeHostResolver HostResolver = new();

    // 5s was too short for the state checks LifecycleCoordinator.OpenProjectAsync
    // runs right after a fresh TcXaeShell launch: the DTE is already reachable
    // (registered in the ROT) but property gets like .Solution can keep
    // rejecting with RPC_E_SERVERCALL_RETRYLATER for well over 5s while the
    // shell finishes settling — longer than OpenTimeout's retry budget ever
    // gets a chance to help, because HasOpenSolution()/ActiveSolutionPath()
    // give up first and rethrow. ComBusyRetry only spends this budget when a
    // call is genuinely busy, so raising it doesn't slow the healthy/fast path.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SaveCloseTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(120);

    private static dynamic GetActiveTcDte() =>
        GetActiveTcDteOrNull()
        ?? throw new InvalidOperationException(DescribeUnreachableXae());

    /// <summary>
    /// THE attach path (issue #149 A-11): the session, <see cref="TwinCatEnvironmentProbe"/> and
    /// <see cref="NativeTwinCatRunner"/> all get their DTE here. Tries each ProgID in
    /// <paramref name="probeOrder"/> (default: the unbiased legacy order,
    /// <see cref="XaeHostResolver.LegacyAttachProbeOrder"/>); callers with a project context pass
    /// the resolver's biased order. Returns null only when no candidate is registered in the ROT;
    /// a busy/blocked XAE is <b>rethrown</b> (it used to be swallowed into "no XAE running",
    /// LIVE-7 / A-3) so the caller's <see cref="ComCall.Run{T}"/> can retry it or name the dialog.
    /// </summary>
    internal static dynamic? GetActiveTcDteOrNull(IEnumerable<string>? probeOrder = null)
    {
        // Q2 (issue #57): when more than one TcXaeShell is running, the ProgID
        // probe below would attach to a nondeterministic instance. If the ROT
        // contains exactly one DTE with the expected solution open, bind to
        // *that* one — deterministic even with another XAE up. 0/>1/no-expected
        // falls through to the legacy probe (read-only callers keep today's
        // best-effort behaviour; mutating callers are stopped first by
        // MultiXaeBlockReason / RequireOpenSolution).
        if (XaeProcessProbe.MultipleRunning())
        {
            var res = ResolveExpectedDte();
            if (res.Outcome == RotDteOutcome.Unique && res.Dte is not null)
                return res.Dte;
        }

        Exception? busy = null;
        foreach (string progId in probeOrder ?? XaeHostResolver.LegacyAttachProbeOrder)
        {
            try { return ComInterop.GetActiveObject(progId); }
            catch (Exception ex) when (ComCall.IsBusy(ex)) { busy ??= ex; }
            // Not registered / not in the ROT (MK_E_UNAVAILABLE, REGDB_E_CLASSNOTREG, …): next ProgID.
            catch (Exception ex) when (ComCall.NotBusy(ex)) { }
        }
        if (busy is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(busy).Throw();
        return null;
    }

    /// <summary>
    /// Why no DTE could be attached — "no process" vs "process but DTE unreachable" vs
    /// "blocked by a modal" (issue #149, scope 4). Win32 + process probes only, no COM.
    /// </summary>
    public string DescribeUnreachable() => DescribeUnreachableXae();

    internal static string DescribeUnreachableXae() =>
        XaeReachability.Describe(XaeProcessProbe.Count(), XaeModalProbe.FindBlockingDialog());

    private static object? TryGetSystemManager(dynamic dte)
    {
        // No solution / projects collection not ready: a genuine "not yet" (RequireSystemManager polls).
        dynamic? projects = ComCall.ReadProp<object?>(() => dte.Solution.Projects, null);
        if (projects is null) return null;
        int count = ComCall.ReadProp(() => (int)projects.Count, 0);
        for (int i = 1; i <= count; i++)
        {
            dynamic? project = ComCall.ReadProp<object?>(() => projects.Item(i), null);
            object? found = TryFindSysManInProject(project);
            if (found is not null) return found;
        }
        return null;
    }

    // Right after twincat_open_project (or any structural change) the DTE's
    // Solution.Projects collection can be momentarily empty while XAE finishes
    // loading, so TryGetSystemManager transiently returns null. That is a
    // settle lag, not a real "no system manager" — poll briefly before giving
    // up so the very next tool call doesn't spuriously fail (same family of
    // fix as HasOpenSolution's busy-retry). A busy XAE during this settle is
    // part of the same lag, so it is retried HERE (ComCall.Run: a blocking
    // dialog still ends it early by name) — never handed to the caller's
    // retry scope, which may be a mutating call that must not be replayed.
    // Exhaustion is a final XaeBlockedException.
    private static readonly TimeSpan SystemManagerSettle = TimeSpan.FromSeconds(20);

    private static object RequireSystemManager(dynamic dte)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            TimeSpan left = SystemManagerSettle - sw.Elapsed;
            object? sm = ComCall.Run<object?>(() => TryGetSystemManager(dte),
                left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1),
                operation: "the system manager lookup");
            if (sm is not null) return sm;
            if (sw.Elapsed >= SystemManagerSettle)
                throw new InvalidOperationException("No TwinCAT system manager available.");
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }
    }

    private static object? TryFindSysManInProject(dynamic? project)
    {
        if (project is null) return null;
        object? obj = ComCall.ReadProp<object?>(() => project.Object as object, null);
        if (obj is not null && CanLookupTreeItem(obj)) return obj;

        dynamic? items = ComCall.ReadProp<object?>(() => project.ProjectItems, null);
        if (items is null) return null;
        int count = ComCall.ReadProp(() => (int)items.Count, 0);
        for (int i = 1; i <= count; i++)
        {
            dynamic? sub = ComCall.ReadProp<object?>(() => items.Item(i).SubProject, null);
            object? found = TryFindSysManInProject(sub);
            if (found is not null) return found;
        }
        return null;
    }

    // Duck-typing probe: only an ITcSysManager answers LookupTreeItem. A busy XAE is
    // not "not a system manager" — it propagates.
    private static bool CanLookupTreeItem(object obj) =>
        ComCall.Try(() => _ = ((dynamic)obj).LookupTreeItem("TIID"));

    // LookupTreeItem throws (E_FAIL / "not found") for an unknown path — that is the one
    // genuine null. Busy used to be swallowed here too, turning POUs created seconds
    // earlier into "PLC tree path not found" (LIVE-3); it now propagates.
    // A path below a referenced stand-alone PLC project is walked instead (MustWalkPlcPath, #14):
    // every caller that hands a user path to this method gets the same protection. The walk ignores
    // a segment that has a single candidate; these callers had an exact lookup, so the node it finds
    // must carry the requested path (like ComSysManTree.Lookup), or a typo would land on another node.
    private static dynamic? LookupTreeItemOrNull(object sysMan, string path) =>
        MustWalkPlcPath(sysMan, path) ? WithExactPath(ResolveByNodeType(sysMan, path), path) : LookupTreeItemDirect(sysMan, path);

    // The node when its PathName is the requested path (or unreadable), else null.
    private static object? WithExactPath(object? node, string path)
    {
        if (node is null) return null;
        string actual = ComCall.ReadProp(() => (string?)((dynamic)node).PathName, null) ?? string.Empty;
        return actual.Length == 0 || string.Equals(actual, path, StringComparison.OrdinalIgnoreCase) ? node : null;
    }

    // The unguarded LookupTreeItem. Only for paths already checked with MustWalkPlcPath, or for
    // "TIPC" / "TIPC^<plc>", which are safe to look up even for a referenced PLC.
    private static dynamic? LookupTreeItemDirect(object sysMan, string path) =>
        ComCall.ReadProp<object?>(() => ((dynamic)sysMan).LookupTreeItem(path), null);

    private static int ChildCountOf(dynamic node) => ComCall.ReadProp(() => (int)node.ChildCount, 0);

    private static dynamic? ChildAt(dynamic node, int index) =>
        ComCall.ReadProp<object?>(() => node.Child(index), null);

    private static string NameOf(dynamic node) =>
        ComCall.ReadProp(() => (string?)node.Name ?? string.Empty, string.Empty);

    // Direct LookupTreeItem first; on null, split at the last '^' separator and walk
    // the parent's Child(i) collection looking for a case-insensitive Name match.
    // As a final fallback, resolves TIPC/TIID paths by node type so paths remain
    // valid regardless of PLC project name or EtherCAT master name.
    //
    // One exception (PR #9): a path below a PLC instance whose PLC is a REFERENCED stand-alone
    // project (.tspproj) is never looked up directly. There LookupTreeItem("TIPC^<plc>^<project>^...")
    // hangs for tens of seconds, answers "not found" and then crashes the XAE (0xc0000005 in
    // TwinCAT System Manager.x64.dll; live on TC 3.1.4026.27). Such a PLC is recognised by its
    // instance node having no NestedProject (the project tree lives in the stand-alone project's
    // own System Manager); the Child(i) walk from TIPC then resolves the node. An inline PLC
    // (NestedProject present: every fixture and most projects) keeps the single direct lookup,
    // so the per-level enumeration is not paid where it is not needed. So does a path below the
    // instance's own child ("<plc> Instance"^PlcTask Inputs^...): that node does live in this
    // System Manager, the lookup there is safe, and its process-image folders are reachable by
    // path only, not as Child(i) — the walk would not find them (#14, live on TC 3.1.4026.22).
    private static dynamic? LookupTreeItemWithFallback(object sysMan, string path)
    {
        if (MustWalkPlcPath(sysMan, path))
            return ResolveByNodeType(sysMan, path);

        // Checked once above; the parent below belongs to the same PLC instance.
        var direct = LookupTreeItemDirect(sysMan, path);
        if (direct is not null) return direct;

        int caret = path.LastIndexOf('^');
        if (caret > 0)
        {
            string parentPath = path.Substring(0, caret);
            string leaf = path.Substring(caret + 1);
            if (!string.IsNullOrEmpty(leaf))
            {
                dynamic? parent = LookupTreeItemDirect(sysMan, parentPath);
                if (parent is not null)
                {
                    int count = ChildCountOf(parent);
                    for (int i = 1; i <= count; i++)
                    {
                        dynamic? child = ChildAt(parent, i);
                        if (child is null) continue;
                        if (string.Equals(NameOf(child), leaf, StringComparison.OrdinalIgnoreCase))
                            return child;
                    }
                }
            }
        }

        return ResolveByNodeType(sysMan, path);
    }

    /// <summary>
    /// True for a path with three or more segments under TIPC ("TIPC^&lt;plc&gt;^&lt;project&gt;…").
    /// "TIPC" and "TIPC^&lt;plc&gt;" are safe to look up directly; everything below may live in a
    /// referenced stand-alone PLC project, which a direct lookup crashes. Pure; unit-tested.
    /// </summary>
    internal static bool IsBelowPlcInstance(string path)
    {
        string[] segs = path.Split('^');
        return segs.Length >= 3 && string.Equals(segs[0], "TIPC", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="path"/> must be walked instead of looked up: it lies below a PLC
    /// instance (<see cref="IsBelowPlcInstance"/>), that instance is a referenced stand-alone
    /// project, AND segment 3 is not one of the instance node's own children. <paramref name="hasNestedProject"/>
    /// answers for the instance name (segment 2): <c>true</c> = inline PLC (direct lookup is safe and
    /// cheapest), <c>false</c> = referenced, <c>null</c> = no such instance under TIPC (nothing to
    /// protect; the normal lookup and its fallbacks decide). <paramref name="isOwnChild"/> answers
    /// (instance, segment 3) for a referenced PLC: its own "&lt;plc&gt; Instance" child is safe to look
    /// up below, the project root (which only the stand-alone System Manager holds) is not.
    /// Pure apart from the two callbacks; unit-tested.
    /// </summary>
    internal static bool ShouldWalkPlcPath(
        string path, Func<string, bool?> hasNestedProject, Func<string, string, bool> isOwnChild)
    {
        if (!IsBelowPlcInstance(path)) return false;
        string[] segs = path.Split('^');
        string plcInstance = segs[1];
        if (plcInstance.Length == 0) return false;
        if (hasNestedProject(plcInstance) != false) return false;
        return !isOwnChild(plcInstance, segs[2]);
    }

    /// <summary>
    /// <see cref="ShouldWalkPlcPath"/> against the live tree of <paramref name="sysMan"/>. Also used by
    /// <see cref="Tree.ComSysManTree"/> (schema apply, GVL writer, linker), which looks paths up itself.
    /// </summary>
    internal static bool MustWalkPlcPath(object sysMan, string path) =>
        ShouldWalkPlcPath(path,
            plcInstance => HasNestedProject(sysMan, plcInstance),
            (plcInstance, segment) => IsOwnChildOfPlcInstance(sysMan, plcInstance, segment));

    // Whether TIPC^<plcInstance> itself lists a child named <segment> (one or two Child(i) reads).
    private static bool IsOwnChildOfPlcInstance(object sysMan, string plcInstance, string segment)
    {
        object? instance = LookupTreeItemDirect(sysMan, "TIPC^" + plcInstance);
        if (instance is null) return false;
        int count = ChildCountOf(instance);
        for (int i = 1; i <= count; i++)
        {
            object? child = ChildAt(instance, i);
            if (child is not null && string.Equals(NameOf(child), segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>The Child(i) walk from TIPC for a path that <see cref="MustWalkPlcPath"/> refuses to look up.</summary>
    internal static object? WalkPlcPath(object sysMan, string path) => ResolveByNodeType(sysMan, path);

    /// <summary>
    /// For a referenced stand-alone PLC project: reloads the TMC into its instance (the ItemType-57 child of
    /// TIPC^&lt;plcInstance&gt; in the main System Manager), so a GVL the build just compiled into the stand-alone
    /// project reaches the process image there. Without it the image keeps the TMC it was last loaded from and the
    /// linker finds no variable of the new GVL (#14, live). ConsumeXml with &lt;ReloadTmc&gt; on the TcCOM object is
    /// Beckhoff's documented reload (Infosys "Reloading TMC files"). Returns the reloaded instance paths; empty for an
    /// inline PLC, whose build updates its image itself.
    /// </summary>
    internal static IReadOnlyList<string> ReloadReferencedPlcTmc(object sysMan, string plcInstance)
    {
        if (HasNestedProject(sysMan, plcInstance) != false) return [];
        object? plc = LookupTreeItemDirect(sysMan, "TIPC^" + plcInstance);
        if (plc is null) return [];

        var reloaded = new List<string>();
        int count = ChildCountOf(plc);
        for (int i = 1; i <= count; i++)
        {
            object? child = ChildAt(plc, i);
            if (child is null) continue;
            dynamic instance = child;
            if (ComCall.ReadProp(() => (int)instance.ItemType, -1) != PlcInstanceSettingsXml.PlcInstanceItemType) continue;
            instance.ConsumeXml("<TreeItem><ReloadTmc>true</ReloadTmc></TreeItem>");
            reloaded.Add(ComCall.ReadProp(() => (string?)instance.PathName, null) ?? NameOf(child));
        }
        return reloaded;
    }

    /// <summary>
    /// <c>TIPC^&lt;plcInstance&gt;</c> (two segments: safe to look up even for a referenced PLC)
    /// and its <c>NestedProject</c>: <c>true</c>/<c>false</c> for present/absent, <c>null</c> when
    /// the instance is not found. Two COM reads, no enumeration.
    /// </summary>
    private static bool? HasNestedProject(object sysMan, string plcInstance)
    {
        dynamic? instance = LookupTreeItemDirect(sysMan, "TIPC^" + plcInstance);
        if (instance is null) return null;
        return ComCall.ReadProp<object?>(() => instance.NestedProject, null) is not null;
    }

    // Type-based resolution for TIPC^... and TIID^... paths.
    // Enumerates children by position/subtype rather than name so paths are
    // valid across differently-named PLC projects and EtherCAT masters.
    private static dynamic? ResolveByNodeType(object sysMan, string path)
    {
        string[] segs = path.Split('^');
        if (segs.Length < 2) return null;

        if (string.Equals(segs[0], "TIPC", StringComparison.OrdinalIgnoreCase))
            return ResolveTipcByType(sysMan, segs);

        if (string.Equals(segs[0], "TIID", StringComparison.OrdinalIgnoreCase))
            return ResolveTiidByType(sysMan, segs);

        return null;
    }

    /// <summary>
    /// Which candidates a type-based fallback may use for one path segment (issue #149 B-5):
    /// ignoring the segment is only safe when there is exactly one candidate. With several
    /// (multi-PLC solution, several EtherCAT masters) the segment must match a candidate's name —
    /// otherwise a read <b>or write</b> would silently land on another PLC. Pure; unit-tested.
    /// </summary>
    internal static IReadOnlyList<int> FallbackCandidates(IReadOnlyList<string> names, string segment)
    {
        if (names.Count == 1) return [0];
        var matches = new List<int>();
        for (int i = 0; i < names.Count; i++)
            if (string.Equals(names[i], segment, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        return matches;
    }

    private static List<object> Children(dynamic node)
    {
        var list = new List<object>();
        int count = ChildCountOf(node);
        for (int i = 1; i <= count; i++)
        {
            object? child = ChildAt(node, i);
            if (child is not null) list.Add(child);
        }
        return list;
    }

    private static List<string> NamesOf(List<object> nodes)
    {
        var names = new List<string>(nodes.Count);
        foreach (object n in nodes) names.Add(NameOf(n));
        return names;
    }

    // TIPC^{PlcInstance}^{PlcRoot}^segs[3..]
    // segs[1] (PLC instance) and segs[2] (project root) are ignored only when they are the single
    // candidate at their level; otherwise they must match by name (FallbackCandidates).
    private static dynamic? ResolveTipcByType(object sysMan, string[] segs)
    {
        dynamic? tipc = LookupTreeItemOrNull(sysMan, "TIPC");
        if (tipc is null) return null;

        List<object> instances = Children(tipc);
        foreach (int p in FallbackCandidates(NamesOf(instances), segs[1]))
        {
            dynamic plcInstance = instances[p];
            if (segs.Length == 2) return plcInstance;

            List<object> roots = Children(plcInstance);
            AddNestedProject(plcInstance, roots);
            foreach (int c in FallbackCandidates(NamesOf(roots), segs[2]))
            {
                dynamic projRoot = roots[c];
                if (segs.Length == 3) return projRoot;

                dynamic? result = NavigateSegmentsByName(projRoot, segs, startIndex: 3);
                if (result is not null) return result;
            }
        }
        return null;
    }

    // The PLC project root is the instance's NestedProject (the node twincat_plc_list walks), not
    // always a Child(i) of the instance. With a referenced stand-alone PLC project (.tspproj) the
    // instance under the main System Manager has only the "<plc> Instance" child and no NestedProject;
    // the project tree hangs under the stand-alone project's own System Manager, which
    // FindPlcSourceProjectNode finds by scanning every solution project (live, TC 3.1.4026.27).
    private static void AddNestedProject(dynamic plcInstance, List<object> roots)
    {
        object? nested = ComCall.ReadProp<object?>(() => plcInstance.NestedProject, null)
            ?? FindPlcSourceProjectNode(GetActiveTcDte(), NameOf(plcInstance));
        if (nested is null) return;
        if (!NamesOf(roots).Contains(NameOf(nested), StringComparer.OrdinalIgnoreCase)) roots.Add(nested);
    }

    // TIID^{MasterName}^segs[2..]
    // segs[1] (master name) is ignored only when exactly one EtherCAT master (subtype 111)
    // exists; with several it must match by name.
    private static dynamic? ResolveTiidByType(object sysMan, string[] segs)
    {
        dynamic? tiid = LookupTreeItemOrNull(sysMan, "TIID");
        if (tiid is null) return null;

        var masters = new List<object>();
        foreach (object child in Children(tiid))
            if (ComCall.ReadProp(() => (int)((dynamic)child).ItemSubType, -1) == 111)
                masters.Add(child);

        foreach (int m in FallbackCandidates(NamesOf(masters), segs[1]))
        {
            dynamic master = masters[m];
            if (segs.Length == 2) return master;

            dynamic? result = NavigateSegmentsByName(master, segs, startIndex: 2);
            if (result is not null) return result;
        }
        return null;
    }

    // Navigate a path from root, starting at segs[startIndex], by child name at each level.
    private static dynamic? NavigateSegmentsByName(dynamic root, string[] segs, int startIndex)
    {
        dynamic current = root;
        for (int s = startIndex; s < segs.Length; s++)
        {
            if (string.IsNullOrEmpty(segs[s])) continue;
            dynamic? found = FindChildByName(current, segs[s]);
            if (found is null) return null;
            current = found;
        }
        return current;
    }

    private static dynamic? FindPlcSourceProjectNode(dynamic dte, string? plcInstanceName = null)
    {
        // On TC3 4026.22 the PLC source-project node (ItemType 600, implements
        // ITcPlcLibraryManager) is NOT reachable through dte.Solution.Projects →
        // ProjectItems → .SubProject (that subtree is empty for the TwinCAT solution
        // project). It IS reachable via the system manager's TIPC^<PlcName> node,
        // whose .NestedProject property returns the source-project tree item.
        //
        // Strategy:
        //   1. Walk dte.Solution.Projects, treating each project.Object as a sysman.
        //   2. LookupTreeItem("TIPC") and enumerate its children (one per PLC project
        //      — itemType 56, named after the PLC instance, e.g. "PLC" or "SPS").
        //   3. Each such child exposes .NestedProject → the source-project node.
        //   4. Return the first match. Multi-PLC projects use the first PLC; this
        //      matches existing single-PLC behaviour in the rest of the codebase.
        dynamic? projects = ComCall.ReadProp<object?>(() => dte.Solution.Projects, null);
        if (projects is null) return null;
        int count = ComCall.ReadProp(() => (int)projects.Count, 0);
        for (int i = 1; i <= count; i++)
        {
            object? project = ComCall.ReadProp<object?>(() => projects.Item(i), null);
            if (project is null) continue;

            object? sysObj = ComCall.ReadProp<object?>(() => ((dynamic)project).Object as object, null);
            if (sysObj is null) continue;

            dynamic? tipc = LookupTreeItemOrNull(sysObj, "TIPC");
            if (tipc is null) continue;

            foreach (object plcNode in Children(tipc))
            {
                if (plcInstanceName is not null
                    && !string.Equals(NameOf(plcNode), plcInstanceName, StringComparison.OrdinalIgnoreCase)) continue;
                object? nested = ComCall.ReadProp<object?>(() => ((dynamic)plcNode).NestedProject, null);
                if (nested is null) continue;

                // Sanity-check: source-project node should accept ScanLibraries.
                // If it doesn't, the binding is something unexpected — skip it
                // rather than returning a node that will fail downstream.
                if (LooksLikePlcSourceProject(nested))
                    return nested;
            }
        }
        return null;
    }

    private static bool LooksLikePlcSourceProject(object node)
    {
        // Duck-type: the source-project node (ItemType 600) implements
        // ITcPlcLibraryManager, so ScanLibraries should at least bind without
        // throwing. We don't materialise the result here. Busy propagates.
        return ComCall.Try(() => _ = ((dynamic)node).ScanLibraries());
    }

    private static readonly TimeSpan PostMutationSettle = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the read-only tail of a mutating call (after CreateChild / rename …) in its own busy
    /// scope. A busy XAE right after a structural change is the normal settle lag; it must be
    /// retried HERE — letting it escape to the method's outer <see cref="ComBusyRetry.Run{T}"/>
    /// would replay the non-idempotent CreateChild ("already exists"). Exhaustion is a final
    /// <see cref="XaeBlockedException"/>, which the outer scope never replays.
    /// </summary>
    private static T AfterMutation<T>(Func<T> tail) =>
        ComCall.Run(tail, PostMutationSettle, operation: "the read-back after the change");

    private static string ReadDeclarationText(dynamic node, string treePath)
    {
        try { return (string)node.DeclarationText ?? string.Empty; }
        // LIVE-2: a busy XAE is not "this node has no declaration" — let the retry scope see it.
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"ITcPlcDeclaration.DeclarationText failed on '{treePath}': {ex.Message}. " +
                "The node may not expose this interface (e.g. a folder), or the XAE editor is not fully loaded.", ex);
        }
    }

    private static string ReadImplementationText(dynamic node, string treePath)
    {
        try { return (string)node.ImplementationText ?? string.Empty; }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"ITcPlcImplementation.ImplementationText failed on '{treePath}': {ex.Message}. " +
                "The node may not expose this interface (e.g. a GVL or DUT), or the XAE editor is not fully loaded.", ex);
        }
    }

    private static string? ReadImplementationLanguage(dynamic node)
    {
        // ITcPlcImplementation.Language returns the IEC language. On TC3 4026.22 it
        // comes back as the NUMERIC code (1=ST, 2=IL, 3=SFC, 4=FBD, 5=CFC, 6=LD);
        // other versions may return the string. Be tolerant of both AND of any
        // integral COM type (short/int/long/uint via IConvertible) — IDispatch
        // can deliver Int16 / Int32 depending on the marshaller path.
        try
        {
            object? raw = node.Language;
            if (raw is null) return null;
            if (raw is IConvertible conv)
            {
                try
                {
                    int code = conv.ToInt32(System.Globalization.CultureInfo.InvariantCulture);
                    // Heuristic: language codes are 1..6; anything else is probably
                    // already a parseable name like "ST" that converted to garbage.
                    if (code >= 1 && code <= 6) return PlcImplLanguage.FromCode(code);
                }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { /* fall through to string handling */ }
            }
            string s = raw.ToString() ?? string.Empty;
            if (int.TryParse(s, out var parsed)) return PlcImplLanguage.FromCode(parsed);
            return s; // already a language name like "ST"
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return null; }
    }

    private static bool BodiesMatch(string expected, string actual)
    {
        static string Norm(string s) => s.Replace("\r\n", "\n").TrimEnd();
        return Norm(expected) == Norm(actual);
    }

    private static string Preview(string s)
    {
        if (string.IsNullOrEmpty(s)) return "<empty>";
        var t = s.Replace("\r\n", "\\n").Replace("\n", "\\n");
        return t.Length <= 200 ? t : t.Substring(0, 200) + "…";
    }

    // No COM instance fields are stored between calls — GetActiveTcDteOrNull()
    // returns a fresh transient attachment on every invocation, so there are
    // no RCWs to release here. Dispose() exists solely to satisfy ITwinCatSession
    // : IDisposable and to give callers a clean ownership boundary.
    public void Dispose() { }
}
