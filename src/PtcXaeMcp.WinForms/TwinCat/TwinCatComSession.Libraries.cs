using PtcXaeMcp.WinForms.TwinCat.HostSelection;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // Fallback ItemType for the References child node (varies by XAE version).
    private const int ItemType_References = 617;

    public IReadOnlyList<LibraryReference> ListLibraryReferences() =>
        ComBusyRetry.Run<IReadOnlyList<LibraryReference>>(() =>
        {
            string? solutionPath = ActiveSolutionPath();
            string? plcproj = solutionPath is null
                ? null
                : LibraryParsing.FindPlcprojNear(solutionPath);

            var xaeRefs = TryListReferencesFromXae();
            if (xaeRefs.Count > 0)
            {
                if (plcproj is not null)
                {
                    var fileRefs = LibraryParsing.ListReferencesFromPlcproj(plcproj);
                    return LibraryParsing.MergeWithFile(xaeRefs, fileRefs);
                }
                return xaeRefs;
            }

            if (plcproj is not null)
                return LibraryParsing.ListReferencesFromPlcproj(plcproj);

            return Array.Empty<LibraryReference>();
        }, ProbeTimeout);

    public IReadOnlyList<InstalledLibrary> ListInstalledLibraries() =>
        ComBusyRetry.Run<IReadOnlyList<InstalledLibrary>>(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic? plcProj = FindPlcSourceProjectNode(dte)
                ?? throw new InvalidOperationException(
                    "No PLC source-project node found under TIPC — open a solution first.");

            // ItemType-600 source-project node implements ITcPlcLibraryManager.
            dynamic libMgr = plcProj!;
            object scanResult;
            try { scanResult = libMgr.ScanLibraries(); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"ITcPlcLibraryManager.ScanLibraries failed: {ex.Message}", ex);
            }

            return MaterialiseInstalled(scanResult);
        }, OpenTimeout);

    private static List<LibraryReference> TryListReferencesFromXae()
    {
        var results = new List<LibraryReference>();
        try
        {
            dynamic? dte = GetActiveTcDteOrNull();
            if (dte is null) return results;

            dynamic? plcProj = FindPlcSourceProjectNode(dte);
            if (plcProj is null) return results;

            dynamic? refsNode = FindReferencesChild(plcProj);
            if (refsNode is null) return results;

            int refCount = 0;
            try { refCount = (int)refsNode.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            for (int i = 1; i <= refCount; i++)
            {
                try
                {
                    dynamic refItem = refsNode.Child(i);
                    string xml = string.Empty;
                    try { xml = (string)refItem.ProduceXml(false); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (string.IsNullOrWhiteSpace(xml)) continue;

                    var entry = LibraryParsing.ParseSingleReferenceXml(xml);
                    if (entry is not null) results.Add(entry);
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* XAE not ready / library manager not available */ }
        return results;
    }

    public IReadOnlyList<LibraryReference> AddLibraryReference(
        string name, string version, string? company, bool asPlaceholder)
    {
        // Mutate inside ComBusyRetry, then re-read outside to avoid nesting two retry blocks.
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            // ITcPlcLibraryManager lives on the ItemType-600 source-project node (same as ScanLibraries).
            dynamic libMgr = GetLibraryManager(dte);

            // AddLibrary creates a LibraryReference (direct ref); AddPlaceholder with "*" fails
            // ("TcLibVersion invalid" or "not found" depending on overload). AddLibrary accepts
            // "*" as version (= "newest") and the read path handles LibraryReference via plcproj.
            if (asPlaceholder)
            {
                string addRes = BuildResolutionString(name, version, company);
                libMgr.AddPlaceholder(addRes);
            }
            else if (!string.IsNullOrEmpty(company))
                libMgr.AddLibrary(name, version, company);
            else
                libMgr.AddLibrary(name, version, string.Empty);

            SaveAll();
        }, OpenTimeout);
        return ListLibraryReferences();
    }

    public IReadOnlyList<LibraryReference> RemoveLibraryReference(string name)
    {
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic libMgr = GetLibraryManager(dte);
            libMgr.RemoveReference(name);
            SaveAll();
        }, OpenTimeout);
        return ListLibraryReferences();
    }

    public IReadOnlyList<LibraryReference> SetLibraryVersion(
        string name, string version, string? company)
    {
        try
        {
            SetLibraryVersionViaCom(name, version, company);
        }
        // A referenced PLC is refused as it is, not sent into the close/edit/reopen fallback below.
        catch (InvalidOperationException comEx) when (ComCall.NotBusy(comEx) && comEx is not ReferencedPlcProjectException) {
            // Placeholder pin via COM failed — safe file-based path: save-close the
            // solution, upsert the <PlaceholderResolution> in the .plcproj, reopen.
            var (sln, proj) = ComBusyRetry.Run<(string, string)>(() =>
            {
                string s = ActiveSolutionPath()
                    ?? throw new InvalidOperationException("No active solution path resolved.");
                string p = LibraryParsing.FindPlcprojNear(s)
                    ?? throw new InvalidOperationException("No .plcproj found near the solution.");
                return (s, p);
            }, ProbeTimeout);
            // Issue #148 (B-2): validate the edit BEFORE closing, and reopen in a finally so a
            // failed file edit never leaves the user without their solution.
            string resolution = BuildResolutionString(name, version, company);
            LibraryParsing.ValidatePlaceholderResolutionUpsert(proj, name, resolution);
            CloseSolution(saveFirst: true);
            try
            {
                LibraryParsing.UpsertPlaceholderResolutionInPlcproj(proj, name, resolution);
            }
            finally
            {
                OpenSolution(sln);
            }
        }
        return ListLibraryReferences();
    }

    private void SetLibraryVersionViaCom(string name, string version, string? company)
    {
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic libMgr = GetLibraryManager(dte);

            // Determine whether the existing reference is a placeholder.
            dynamic? plcProj = FindPlcSourceProjectNode(dte);
            dynamic? refsNode = plcProj is null ? null : FindReferencesChild(plcProj);
            bool isPlaceholder = false;
            if (refsNode is not null)
            {
                int count = 0;
                try { count = (int)refsNode.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                for (int i = 1; i <= count; i++)
                {
                    try
                    {
                        dynamic child = refsNode.Child(i);
                        string xml = string.Empty;
                        try { xml = (string)child.ProduceXml(false); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                        // TreeItem XML of a placeholder: <PlcLibPlaceholder>/<PlaceholderReference>
                        // (ItemSubTypeName TREEITEMTYPE_PLCLIBPLACEHOLDER) — NOT "<PlaceholderResolution",
                        // which never occurs here and previously misrouted placeholders into the
                        // destructive Remove+AddLibrary branch (live-verified on 100000).
                        if ((xml.Contains("<PlcLibPlaceholder", StringComparison.OrdinalIgnoreCase) ||
                             xml.Contains("PLCLIBPLACEHOLDER", StringComparison.OrdinalIgnoreCase)) &&
                            xml.Contains(name, StringComparison.OrdinalIgnoreCase))
                        {
                            isPlaceholder = true;
                            break;
                        }
                    }
                    catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                }
            }

            string setRes = BuildResolutionString(name, version, company);
            if (isPlaceholder)
            {
                try
                {
                    // SetEffectiveResolution takes the full resolution string.
                    libMgr.SetEffectiveResolution(name, setRes);
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
                    // NEVER Remove+AddPlaceholder here: that drops the placeholder's
                    // <Parameters> overrides and its file pin (live-verified on 100000).
                    // Signal the caller instead; the safe path is the file-based
                    // <PlaceholderResolution> edit below (solution closed).
                    throw new InvalidOperationException(
                        $"SetEffectiveResolution failed for placeholder '{name}' — falling back to the .plcproj pin edit.");
                }
            }
            else
            {
                libMgr.RemoveReference(name);
                // #171 audit (issue #152): the reference is gone now. A busy AddLibrary must be
                // retried HERE — escaping to the outer retry would replay RemoveReference, which then
                // fails ("not found") and sent the caller into the file-based fallback.
                object mgr = libMgr;
                AfterMutation<object?>(() =>
                {
                    ((dynamic)mgr).AddLibrary(name, version, string.IsNullOrEmpty(company) ? string.Empty : company);
                    return null;
                });
            }

            SaveAll();
        }, OpenTimeout);
    }

    public IReadOnlyList<InstalledLibrary> InstallLibrary(string libraryPath, bool overwrite)
    {
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic? plcProj = FindPlcSourceProjectNode(dte)
                ?? throw new InvalidOperationException(
                    "No PLC source-project node found under TIPC — open a solution first.");
            // Same ITcPlcLibraryManager surface as ScanLibraries (ItemType-600 node).
            dynamic libMgr = plcProj!;
            libMgr.InstallLibrary("System", libraryPath, overwrite);
        }, OpenTimeout);
        return ListInstalledLibraries();
    }

    // ITcPlcIECProject.SaveAsLibrary(bstrPath, bInstall) — same ItemType-600
    // source-project node as ITcPlcLibraryManager (InstallLibrary/ScanLibraries
    // above); TwinCAT's automation model implements both interfaces on it.
    // UNVERIFIED against a live XAE at the time of writing — the exact
    // parameter order/behaviour (e.g. whether bInstall also registers a
    // placeholder reference in the *current* project) needs a live smoke test
    // before this is trusted for anything beyond an ad-hoc export.
    public string SaveAsLibrary(string outputPath, bool install)
    {
        return ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            dynamic? plcProj = FindPlcSourceProjectNode(dte)
                ?? throw new InvalidOperationException(
                    "No PLC source-project node found under TIPC — open a solution first.");
            dynamic iecProj = plcProj!;
            try { iecProj.SaveAsLibrary(outputPath, install); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"ITcPlcIECProject.SaveAsLibrary('{outputPath}', install={install}) failed: {ex.Message}", ex);
            }
            return install
                ? $"Saved and installed library: {outputPath}"
                : $"Saved library: {outputPath}";
        }, OpenTimeout);
    }

    public IReadOnlyList<LibraryParameter> ListLibraryParameters(string placeholder) =>
        ComBusyRetry.Run<IReadOnlyList<LibraryParameter>>(() =>
        {
            // Parameters are persisted in the .plcproj — read from disk (deterministic,
            // works even while XAE is busy). SaveAll() after mutations keeps this current.
            string? solutionPath = ActiveSolutionPath();
            string? plcproj = solutionPath is null
                ? null
                : LibraryParsing.FindPlcprojNear(solutionPath);
            return plcproj is null
                ? Array.Empty<LibraryParameter>()
                : LibraryParsing.ListParametersFromPlcproj(plcproj, placeholder);
        }, ProbeTimeout);

    public IReadOnlyList<LibraryParameter> SetLibraryParameter(
        string placeholder, string listName, string key, string value)
    {
        // ConsumeXml is deliberately NOT used here: the placeholder <ParameterList> is
        // read-only via the Automation Interface (child full XML, child fragment and parent
        // References node were all ignored), and a ConsumeXml attempt on the parent node
        // corrupted the in-memory reference state, which a later save then persisted with
        // wiped parameters (verified live on 100000, recovered via git). Deterministic and
        // non-destructive instead: verify the reference exists, save-close the solution,
        // upsert the override in the .plcproj on disk, reopen.
        var (slnPathUsed, plcprojUsed) = ComBusyRetry.Run<(string Sln, string Proj)>(() =>
        {
            string sln = ActiveSolutionPath()
                ?? throw new InvalidOperationException("No active solution path resolved.");
            string proj = LibraryParsing.FindPlcprojNear(sln)
                ?? throw new InvalidOperationException("No .plcproj found near the solution.");

            dynamic dte = GetActiveTcDte();
            // Same TIPC References node as AddLibrary/RemoveReference (see GetLibraryManager).
            dynamic refsNode = GetLibraryManager(dte);
            _ = FindReferenceChildByPlaceholder(refsNode, placeholder)
                ?? throw new InvalidOperationException(
                    $"Placeholder reference '{placeholder}' not found under the References node.");
            return (sln, proj);
        }, OpenTimeout);

        // Issue #148 (B-2): validate the edit BEFORE closing, reopen in a finally.
        LibraryParsing.ValidateParameterUpsert(plcprojUsed, placeholder, listName, key, value);
        CloseSolution(saveFirst: true);
        try
        {
            LibraryParsing.UpsertParameterInPlcproj(plcprojUsed, placeholder, listName, key, value);
        }
        finally
        {
            OpenSolution(slnPathUsed);
        }

        // Verify against the .plcproj — a silently failed write must not masquerade as
        // success (safety-relevant constants like NUMBER_OF_ITEMS ride on this).
        // Placeholder-agnostic scan: the XAE placeholder may not textually match the Include.
        var after = ListLibraryParameters(placeholder);
        if (after.Count == 0)
        {
            string? solutionPath = ActiveSolutionPath();
            string? plcproj = solutionPath is null ? null : LibraryParsing.FindPlcprojNear(solutionPath);
            if (plcproj is not null)
                after = LibraryParsing.ListAllParametersFromPlcproj(plcproj);
        }
        bool persisted = after.Any(p =>
            string.Equals(p.ListName, listName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Value, value, StringComparison.Ordinal));
        if (!persisted)
            throw new InvalidOperationException(
                $"Parameter {listName}/{key}={value} did not persist to the .plcproj — " +
                "set it manually in the XAE Library Manager and report this as a bug.");
        return after;
    }

    private static dynamic? FindReferenceChildByPlaceholder(dynamic refsNode, string placeholder)
    {
        int count = 0;
        try { count = (int)refsNode.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        for (int i = 1; i <= count; i++)
        {
            dynamic? child;
            try { child = refsNode.Child(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (child is null) continue;

            string name = string.Empty;
            try { name = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (string.Equals(name, placeholder, StringComparison.OrdinalIgnoreCase))
                return child;

            // Fallback: match on the PlaceholderName inside the produced XML (node names
            // occasionally carry the resolved library name instead of the placeholder).
            try
            {
                string xml = (string)child.ProduceXml(false);
                var entry = LibraryParsing.ParseSingleReferenceXml(xml);
                if (entry is not null &&
                    (string.Equals(entry.Placeholder, placeholder, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(entry.Name, placeholder, StringComparison.OrdinalIgnoreCase)))
                    return child;
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string BuildResolutionString(string name, string version, string? company) =>
        string.IsNullOrEmpty(company)
            ? $"{name}, {version}"
            : $"{name}, {version} ({company})";

    private static dynamic GetLibraryManager(dynamic dte)
    {
        // Per Infosys 242733963, ITcPlcLibraryManager (AddLibrary/RemoveReference) must be
        // obtained via sysMan.LookupTreeItem("TIPC^<plcName>^<projName>^References").
        // Navigating via plcNode.NestedProject.Child() yields a different COM pointer that
        // exposes ScanLibraries (read) but does NOT dispatch AddLibrary/RemoveReference.
        dynamic? node = FindLibraryManagerNode((object)dte, out string? referencedPlc);
        if (node is not null) return node;
        if (referencedPlc is not null) throw new ReferencedPlcProjectException(referencedPlc);
        throw new InvalidOperationException(
            "References node not found via TIPC path — open a solution with a PLC project first.");
    }

    private static dynamic? FindLibraryManagerNode(dynamic dte) => FindLibraryManagerNode((object)dte, out string? _);

    /// <summary>
    /// The PLC instances whose References node may be looked up directly: they have a NestedProject
    /// and their name is not also listed without one. A referenced stand-alone PLC project (.tspproj)
    /// shows up under the main System Manager WITHOUT a NestedProject and under its own System
    /// Manager with one; LookupTreeItem on a path below such a PLC crashes the XAE (#9, #14), so it
    /// is never looked up, whichever System Manager lists it. Pure; unit-tested.
    /// </summary>
    internal static IReadOnlyList<int> LibraryManagerLookupCandidates(
        IReadOnlyList<(string PlcName, bool HasNestedProject)> instances, out string? referencedPlc)
    {
        var referenced = new HashSet<string>(
            instances.Where(i => !i.HasNestedProject).Select(i => i.PlcName), StringComparer.OrdinalIgnoreCase);
        var allowed = new List<int>();
        referencedPlc = null;
        for (int i = 0; i < instances.Count; i++)
        {
            if (!instances[i].HasNestedProject) continue;
            if (referenced.Contains(instances[i].PlcName)) { referencedPlc ??= instances[i].PlcName; continue; }
            allowed.Add(i);
        }
        if (allowed.Count == 0) referencedPlc ??= referenced.FirstOrDefault();
        return allowed;
    }

    private static dynamic? FindLibraryManagerNode(dynamic dte, out string? referencedPlc)
    {
        referencedPlc = null;
        // Every PLC instance under TIPC of every solution project first, the lookups after:
        // whether a PLC is referenced is only known once all System Managers were seen.
        var instances = new List<(string PlcName, bool HasNestedProject)>();
        var lookups = new List<(object SysMan, string PlcName, string ProjName)?>();
        try
        {
            dynamic projects = dte.Solution.Projects;
            int count = (int)projects.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic? project;
                try { project = projects.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (project is null) continue;

                object? sysObj;
                try { sysObj = project.Object as object; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (sysObj is null) continue;

                dynamic sysMan = sysObj;
                dynamic? tipc;
                try { tipc = sysMan.LookupTreeItem("TIPC"); }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (tipc is null) continue;

                int tipcChildren = 0;
                try { tipcChildren = (int)tipc.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }

                for (int p = 1; p <= tipcChildren; p++)
                {
                    dynamic? plcNode;
                    try { plcNode = tipc.Child(p); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                    if (plcNode is null) continue;

                    string plcName = string.Empty;
                    try { plcName = (string)plcNode.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                    if (string.IsNullOrEmpty(plcName)) continue;

                    // Verify it's a PLC project and get the source-project name. An unreadable
                    // NestedProject counts as absent, like HasNestedProject.
                    dynamic? nested;
                    try { nested = plcNode.NestedProject; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { nested = null; }
                    instances.Add((plcName, nested is not null));
                    lookups.Add(null);
                    if (nested is null) continue;
                    if (!LooksLikePlcSourceProject(nested)) continue;

                    string projName = string.Empty;
                    try { projName = (string)nested.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                    if (string.IsNullOrEmpty(projName)) continue;
                    lookups[^1] = ((object)sysMan, plcName, projName);
                }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        foreach (int c in LibraryManagerLookupCandidates(instances, out referencedPlc))
        {
            if (lookups[c] is not { } lookup) continue;
            dynamic sysMan = lookup.SysMan;
            // Try "References" then "Verweise" (German XAE).
            foreach (var suffix in new[] { "References", "Verweise" })
            {
                dynamic? refsNode;
                try { refsNode = sysMan.LookupTreeItem($"TIPC^{lookup.PlcName}^{lookup.ProjName}^{suffix}"); }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (refsNode is not null) return refsNode;
            }
        }
        return null;
    }

    private static dynamic? FindReferencesChild(dynamic parent)
    {
        int count = 0;
        try { count = (int)parent.ChildCount; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
        for (int i = 1; i <= count; i++)
        {
            dynamic? child;
            try { child = parent.Child(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (child is null) continue;

            int itemType = 0;
            try { itemType = (int)child.ItemType; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (itemType == ItemType_References) return child;

            string name = string.Empty;
            try { name = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (string.Equals(name, "References", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Verweise", StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private static List<InstalledLibrary> MaterialiseInstalled(object scanResult)
    {
        var results = new List<InstalledLibrary>();
        if (scanResult is null) return results;

        if (scanResult is Array arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var item = arr.GetValue(i);
                if (item is null) continue;
                var lib = MaterialiseLibrary(item);
                if (lib is not null) results.Add(lib);
            }
        }
        else if (scanResult is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                if (item is null) continue;
                var lib = MaterialiseLibrary(item);
                if (lib is not null) results.Add(lib);
            }
        }
        return results;
    }

    private static InstalledLibrary? MaterialiseLibrary(object item)
    {
        try
        {
            dynamic d = item;
            string name = string.Empty;
            string version = string.Empty;
            string publisher = string.Empty;
            string title = string.Empty;
            try { name = (string)d.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            try { version = (string)d.Version ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            try { publisher = (string)d.Distributor ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            try { title = (string)d.Title ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            if (string.IsNullOrEmpty(name)) return null;
            return new InstalledLibrary(name, version, publisher, title);
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }
}

/// <summary>
/// The library tools that need the References node's ITcPlcLibraryManager refuse a PLC that is a
/// referenced stand-alone project (.tspproj): the only pointer known to dispatch AddLibrary /
/// RemoveReference comes from LookupTreeItem, which crashes the XAE on such a PLC (#14).
/// </summary>
internal sealed class ReferencedPlcProjectException(string plcName) : InvalidOperationException(
    $"PLC '{plcName}' is a referenced stand-alone PLC project (.tspproj). Its library references cannot be changed " +
    "through this solution: the References node of such a PLC can only be reached with a tree lookup that crashes " +
    "the XAE. Open the .tspproj on its own with twincat_open_project and repeat the call there, or change the " +
    "reference in the Library Manager of the XAE. Reading the references (twincat_list_library_references) works.")
{
    public string PlcName { get; } = plcName;
}
