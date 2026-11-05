namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- visible-for-video --------------------------------------------------
    //
    // Opt-in "show me this object" action. Every other PLC-code tool runs HEADLESS
    // (it resolves objects via the Automation tree and never opens an editor). This
    // is the one deliberate exception: open a source object's editor document in the
    // RUNNING, VISIBLE XAE, make it the active tab, and best-effort raise XAE to the
    // OS foreground — so during an MCP-driven live demo it is always visible WHICH
    // object is being worked on. The read tools (read_pou / read_gvl / read_pou_member)
    // auto-open. The write tools remain headless (no auto-open).
    //
    // The Beckhoff Automation Interface (ITcSmTreeItem) is the project MODEL and has
    // no "open editor" method, so the open/focus is pure VS-shell EnvDTE + Win32:
    //   1. Resolve the tree item with the shared LookupTreeItemWithFallback.
    //   2. Refuse non-openable container nodes (folder / PLC root / References).
    //   3. Open — PRIMARY: resolve the object's on-disk source file (.TcPOU/.TcGVL/
    //      .TcDUT/.TcIO) and dte.ItemOperations.OpenFile(path).Activate(). TwinCAT's
    //      ProduceXml metadata does NOT carry a disk path on TC3 4026.x (its
    //      <PathName> is the caret tree path, not a file), so the path is
    //      RECONSTRUCTED from the tree path: PLC-project folder + the tree folders
    //      below the IEC-project node (they mirror disk 1:1) + the leaf's .Tc*
    //      extension. A <FullPath> read is still attempted first for the TC versions
    //      that expose it. See TryGetNodeDiskPath.
    //   4. Open — FALLBACK: navigate dte.ToolWindows.SolutionExplorer by the
    //      treePath segments, select the node, and File.OpenSelectedItems. Only
    //      reachable on a full VS shell — TcXaeShell's limited DTE has no
    //      ToolWindows, so this is a no-op there and the reconstruction above is the
    //      real path. Kept for full-VS hosts.
    //   5. Focus: only with bringToFront (a person asked where it is):
    //      dte.MainWindow.Activate() and a best-effort Win32 SetForegroundWindow on the
    //      XAE process window. Windows may refuse — reported as ForegroundConfirmed=false,
    //      never an error. Without it the editor opens behind whatever the user works in.

    // Container item types that have no editor document to open.
    private static readonly int[] NonOpenableItemTypes =
    {
        PlcItemType.PlcApp,   // 600 — PLC source-project root
        PlcItemType.Folder,   // 601 — organisational folder
        ItemType_References,  // 617 — library-manager / References node
    };

    public OpenPlcObjectResult OpenPlcObject(string treePath, bool bringToFront = false) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);

        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {treePath}");

        int itemType = -1;
        try { itemType = (int)node.ItemType; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        if (Array.IndexOf(NonOpenableItemTypes, itemType) >= 0)
            throw new InvalidOperationException(
                $"'{treePath}' (ItemType {itemType}) is a container, not an openable source object. " +
                "Pass a POU, GVL, DUT, INTERFACE, or member path — not a folder, the PLC project root, " +
                "or the References node.");

        string? diskPath = TryGetNodeDiskPath(dte, node, treePath);

        // Was the document already open before we touched it? Best-effort; only
        // knowable when we have a disk path (IsFileOpen keys on the file name).
        bool alreadyOpen = false;
        if (diskPath is not null)
        {
            try { alreadyOpen = (bool)dte.ItemOperations.IsFileOpen(diskPath); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }

        // ── Open — primary: ItemOperations.OpenFile(diskPath) ─────────────────
        bool opened = false;
        string strategy = "";
        if (diskPath is not null)
        {
            try
            {
                dynamic? window = dte.ItemOperations.OpenFile(diskPath);
                // Window.Activate can pull the whole XAE in front: only on request.
                if (bringToFront) { try { window?.Activate(); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { } }
                opened = true;
                strategy = "OpenFile(reconstructed disk path)";
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* fall through to the Solution Explorer fallback */ }
        }

        // ── Open — fallback: Solution Explorer select + File.OpenSelectedItems ─
        if (!opened)
        {
            opened = TryOpenViaSolutionExplorer(dte, treePath);
            if (opened) strategy = "Solution Explorer fallback";
        }

        if (!opened)
            throw new InvalidOperationException(
                $"Resolved '{treePath}' but could not open its editor document. " +
                (diskPath is null
                    ? "Could not resolve the on-disk source file from the tree path (and the Solution Explorer fallback is unavailable on this shell)."
                    : $"OpenFile('{diskPath}') and the Solution Explorer fallback both failed."));

        // ── Focus: only with bringToFront (a person asked where it is) ──────────
        // The attached DTE's own main window — not "some TcXaeShell" (issue #150, C-23).
        IntPtr xaeMain = TryGetXaeMainWindow(dte);
        if (bringToFront)
        {
            try { dte.MainWindow.Activate(); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            Win32DialogAutomation.BringXaeToFront(xaeMain);
        }
        bool foregroundConfirmed = Win32DialogAutomation.IsXaeForeground(xaeMain);

        return new OpenPlcObjectResult(
            TreePath: treePath,
            Opened: opened,
            AlreadyOpen: alreadyOpen,
            ForegroundRequested: bringToFront,
            ForegroundConfirmed: foregroundConfirmed,
            Note: $"open via {strategy}");
    }, OpenTimeout);

    // Counterpart to OpenPlcObject: close the object's editor tab so a demo can tidy
    // up. Resolves the same on-disk path, finds the matching open Document, saves it
    // (the file is already the source of truth, so this only flushes any view edits
    // and guarantees no "save changes?" prompt blocks the COM call), then closes it.
    public ClosePlcObjectResult ClosePlcObject(string treePath) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        dynamic dte = GetActiveTcDte();
        object? sysMan = RequireSystemManager(dte);

        dynamic? node = LookupTreeItemWithFallback(sysMan, treePath)
            ?? throw new InvalidOperationException($"PLC tree path not found: {treePath}");

        string? diskPath = TryGetNodeDiskPath(dte, node, treePath);
        if (diskPath is null)
            throw new InvalidOperationException(
                $"Resolved '{treePath}' but could not map it to an on-disk source file to close.");

        // Find the open document by full path. EnvDTE.Documents keys on FullName.
        dynamic? doc = null;
        try
        {
            dynamic docs = dte.Documents;
            int count = (int)docs.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic? d = null; try { d = docs.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (d is null) continue;
                string full = ""; try { full = (string)d.FullName; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                if (string.Equals(full, diskPath, StringComparison.OrdinalIgnoreCase)) { doc = d; break; }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        if (doc is null)
            return new ClosePlcObjectResult(treePath, Closed: false, WasOpen: false, Saved: false,
                Note: "no open editor tab for this object");

        bool saved = false;
        try { doc.Save(); saved = true; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* read-only view or nothing to save */ }

        // Close the tab. Save() above means the default (prompt) variant won't prompt;
        // the explicit no-save argument is a belt-and-braces fallback.
        bool closed = false;
        try { doc.Close(); closed = true; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            try { doc.Close(EnvDteSaveChangesNo); closed = true; } catch (Exception comEx2) when (ComCall.NotBusy(comEx2)) { }
        }

        return new ClosePlcObjectResult(treePath, Closed: closed, WasOpen: true, Saved: saved,
            Note: closed ? "closed editor tab" : "found tab but Close failed");
    }, OpenTimeout);

    // EnvDTE.vsSaveChanges.vsSaveChangesNo — close discarding view changes (we already
    // saved, so this is the belt-and-braces fallback path only).
    private const int EnvDteSaveChangesNo = 1;

    // Resolve the object's on-disk source file (.TcPOU/.TcGVL/.TcDUT/.TcIO).
    //
    // TwinCAT's ProduceXml metadata does NOT expose a disk path on TC3 4026.x — its
    // <PathName> is the caret tree path, not a file — so for those hosts the path is
    // RECONSTRUCTED from the tree path. A <FullPath> read is attempted first for the
    // TC versions that do expose it.
    //
    // Reconstruction: the PLC project's source files live under "<solutionDir>\<PLC
    // project folder>\…". The tree path is "<root>^<PLC project>^<IEC project>^
    // <folder…>^<leaf>"; the folders below the IEC-project node mirror the on-disk
    // folders 1:1 and the leaf gets a .Tc* extension by kind. For a member path
    // (method/property/action inside an FB) the leaf has no file of its own, so the
    // resolver walks up to the nearest ancestor segment that maps to a file. A
    // recursive scan under the PLC folder tolerates tree/disk folder-name drift and
    // unknown extensions. Returns null when nothing on disk matches.
    private static string? TryGetNodeDiskPath(dynamic dte, dynamic node, string treePath)
    {
        // 1) Some TC versions expose <FullPath> directly.
        foreach (bool recursive in new[] { true, false })
        {
            string xml;
            try { xml = (string)node.ProduceXml(recursive); }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (string.IsNullOrEmpty(xml)) continue;

            string? p = ExtractFirstElementText(xml, "FullPath");
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return p;
        }

        // 2) Reconstruct from the tree path.
        string[] seg = treePath.Split('^');
        if (seg.Length < 3) return null; // need at least root^plcProject^iecProject^…

        string solutionDir = TryGetSolutionDir(dte);
        if (string.IsNullOrEmpty(solutionDir)) return null;

        string plcRoot = Path.Combine(solutionDir, seg[1]); // seg[1] = PLC project folder

        // Exact relative path first: seg[3..^1] are on-disk folders, last = file stem.
        string subTypeName = ""; try { subTypeName = (string)node.ItemSubTypeName ?? ""; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        string ext = subTypeName.Contains("GVL") ? ".TcGVL"
                   : subTypeName.Contains("DUT") ? ".TcDUT"
                   : subTypeName.Contains("ITF") ? ".TcIO"
                   : ".TcPOU";
        var parts = new List<string> { plcRoot };
        for (int i = 3; i < seg.Length - 1; i++) parts.Add(seg[i]);
        string candidate = Path.Combine(Path.Combine(parts.ToArray()), seg[^1] + ext);
        if (File.Exists(candidate)) return candidate;

        // Walk up (member path → containing object), scanning for the stem under the
        // PLC folder with any .Tc* extension.
        if (Directory.Exists(plcRoot))
        {
            for (int last = seg.Length - 1; last >= 3; last--)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(plcRoot, seg[last] + ".Tc*", SearchOption.AllDirectories))
                        return f;
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        return null;
    }

    // Directory holding the open solution/tsproj — the root under which the PLC
    // project folder (and its source files) live.
    private static string TryGetSolutionDir(dynamic dte)
    {
        try
        {
            dynamic projects = dte.Solution.Projects;
            int pc = (int)projects.Count;
            for (int i = 1; i <= pc; i++)
            {
                dynamic? p = null; try { p = projects.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                string f = ""; if (p is not null) { try { f = (string)p.FileName; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { } }
                if (!string.IsNullOrWhiteSpace(f)) return Path.GetDirectoryName(f) ?? "";
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return "";
    }

    // Navigate the Solution Explorer UIHierarchy to the node addressed by the
    // caret-separated treePath, select it, and issue File.OpenSelectedItems. The
    // first treePath segment is a system-manager root alias (TIPC/TIID) that does
    // not appear as a Solution Explorer node, so matching is done leaf-first: find
    // a UIHierarchyItem whose Name equals the last path segment, anywhere in the
    // tree. Heavily guarded — any COM hiccup returns false so the caller can report
    // a clean failure rather than throw. Returns true only when the open command
    // was issued against a found node.
    private static bool TryOpenViaSolutionExplorer(dynamic dte, string treePath)
    {
        try
        {
            int caret = treePath.LastIndexOf('^');
            string leaf = caret >= 0 ? treePath.Substring(caret + 1) : treePath;
            if (string.IsNullOrWhiteSpace(leaf)) return false;

            dynamic solutionExplorer = dte.ToolWindows.SolutionExplorer;
            dynamic roots = solutionExplorer.UIHierarchyItems;

            dynamic? match = FindUIHierarchyItemByName(roots, leaf, depth: 0, maxDepth: 24);
            if (match is null) return false;

            try { match.Select(1 /* vsUISelectionTypeSelect */); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return false; }
            dte.ExecuteCommand("File.OpenSelectedItems");
            return true;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return false; }
    }

    // TwinCAT appends a language qualifier to POU nodes in the Solution Explorer
    // (e.g. "MAIN [ST]", "FB_Machine [LD]") but the automation-tree treePath uses
    // the bare name ("MAIN"). Strip the qualifier before comparing.
    private static bool SolutionExplorerNameMatches(string itemName, string searchName)
    {
        if (string.Equals(itemName, searchName, StringComparison.OrdinalIgnoreCase))
            return true;
        int bracket = itemName.IndexOf(" [", StringComparison.Ordinal);
        if (bracket >= 0)
        {
            string stripped = itemName.Substring(0, bracket);
            if (string.Equals(stripped, searchName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Depth-bounded search of a UIHierarchyItems collection for an item whose Name
    // matches (case-insensitive). Expands collapsed nodes lazily — UIHierarchyItem
    // children are not populated until the node is expanded.
    private static dynamic? FindUIHierarchyItemByName(dynamic items, string name, int depth, int maxDepth)
    {
        int count = 0;
        try { count = (int)items.Count; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }

        for (int i = 1; i <= count; i++)
        {
            dynamic? item;
            try { item = items.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (item is null) continue;

            string itemName = string.Empty;
            try { itemName = (string)item.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (SolutionExplorerNameMatches(itemName, name))
                return item;

            if (depth + 1 >= maxDepth) continue;

            dynamic? children;
            try
            {
                // Expand so child items materialise, then recurse.
                try { item.UIHierarchyItems.Expanded = true; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                children = item.UIHierarchyItems;
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (children is null) continue;

            dynamic? found = FindUIHierarchyItemByName(children, name, depth + 1, maxDepth);
            if (found is not null) return found;
        }
        return null;
    }
}
