using System.Diagnostics;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- open-object: generalization of the visible-for-video open/close tools ---
//
// PLC source objects (TIPC subtree) keep the proven file-open fast path — they have
// their own editor document tab. System-manager nodes (EtherCAT/IO, Motion/NC, TcCOM,
// Scope) share ONE editor with no per-node tab (proven live),
// so "opening" one means SELECTING it in the Solution-Explorer tree via UIAutomation;
// the shared sysman editor follows. Classification is by the caret-path root: `TIPC^…`
// is PLC (a PLC folder/root still routes here and is refused by OpenPlcObject's existing
// container guard); everything else is a sysman node.
internal sealed partial class TwinCatComSession
{
    public OpenObjectResult OpenObject(string treePath, bool bringToFront = false)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        if (IsPlcPath(treePath))
        {
            // Proven file-open path → own editor tab. Re-uses OpenPlcObject verbatim
            // (its own COM attach + container guard) and maps to the generalized shape.
            var p = OpenPlcObject(treePath, bringToFront);
            return new OpenObjectResult(treePath, OpenObjectKind.PlcObject,
                p.Opened, p.AlreadyOpen, p.ForegroundRequested, p.ForegroundConfirmed, p.Note);
        }

        // Selecting a system-manager node makes the XAE show it in its shared editor and come to the front by itself
        // (live 2026-11-05, even for a visible node that needed no expanding). So it is not touched unasked.
        if (!bringToFront && !ComBusyRetry.Run(() => Win32DialogAutomation.IsXaeForeground((IntPtr)TryGetXaeMainWindow(GetActiveTcDte())), ProbeTimeout))
            throw new InvalidOperationException(
                $"showing the system-manager node '{treePath}' makes the XAE come to the foreground, which happens only " +
                "with bringToFront=true (when a person asked where it is). Nothing was selected.");

        return SelectSysmanNode(treePath, bringToFront);
    }

    public CloseObjectResult CloseObject(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");

        if (IsPlcPath(treePath))
        {
            var c = ClosePlcObject(treePath);
            return new CloseObjectResult(treePath, OpenObjectKind.PlcObject,
                c.Closed, c.WasOpen, c.Saved, c.Note);
        }

        // Sysman nodes share the single sysman editor — there is no per-node tab to
        // close. Reported no-op, never an error.
        return new CloseObjectResult(treePath, OpenObjectKind.SysmanNode,
            Closed: false, WasOpen: false, Saved: false,
            Note: "system-manager node shares the single sysman editor — no per-node tab to close");
    }

    // PLC source objects live under the TIPC root; everything else (TIID/TINC/TIRC/…,
    // and Scope nodes that aren't in the sysman tree at all) is a shared-editor node.
    private static bool IsPlcPath(string treePath) =>
        treePath.StartsWith("TIPC", StringComparison.OrdinalIgnoreCase);

    // Resolve the localized Solution-Explorer name of EVERY segment and UIAutomation-select the
    // node by walking that path (issue #150, C-9 — a leaf name that exists twice, e.g. a channel
    // under two terminals, must select the one under the given parents). The caret path uses the
    // English alias (`TIID^Device 1 (EtherCAT)`) while the SE shows localized names (`Geräte` for
    // TIID); ITcSmTreeItem.Name of each prefix is the localized bridge. Segments that are not in
    // the sysman tree (e.g. Scope nodes) are used as typed.
    private OpenObjectResult SelectSysmanNode(string treePath, bool bringToFront) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();

        var segments = treePath.Split('^').Select(p => p.Trim()).ToList();
        object? sysMan = null;
        try { sysMan = TryGetSystemManager(dte); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* no sysman — use the caret segments as typed */ }
        var display = new List<string>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            string name = segments[i];
            if (sysMan is not null)
            {
                try
                {
                    dynamic? node = LookupTreeItemWithFallback(sysMan, string.Join("^", segments.Take(i + 1)));
                    string? localized = TryGetLocalizedName(node);
                    if (!string.IsNullOrWhiteSpace(localized)) name = localized!;
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* not in the sysman tree (e.g. Scope) */ }
            }
            display.Add(name);
        }

        IntPtr hwnd = TryGetXaeMainWindow(dte);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                "Could not obtain the XAE main-window handle needed to select the node via UIAutomation.");

        // Issue #124: like a user's double-click — select the row AND activate it (Enter). Selection alone does
        // not open the shared system-manager editor while none is open (live 2026-10-08), and a Scope view
        // (a separate measurement project, not in the sysman tree) opens only on activation.
        bool selected = SolutionExplorerAutomation.TryActivatePath(hwnd, display, bringToFront, out string note);
        if (!selected)
        {
            // Activation needs the XAE in the foreground (raised only with bringToFront); still surface the node by selecting it.
            selected = SolutionExplorerAutomation.TrySelectPath(hwnd, display, out string selectNote, mayExpand: bringToFront);
            note = selected ? $"{note}; selected only (the editor may not have opened)" : $"{note}; {selectNote}";
        }
        if (!selected)
            throw new InvalidOperationException(
                $"Could not surface system-manager node '{string.Join("^", display)}' (from '{treePath}'): {note}");

        // TryActivatePath raised the XAE when bringToFront asked for it; it is never raised otherwise.
        bool foreground = Win32DialogAutomation.IsXaeForeground(hwnd);

        return new OpenObjectResult(treePath, OpenObjectKind.SysmanNode,
            Opened: true, AlreadyOpen: false,
            ForegroundRequested: bringToFront, ForegroundConfirmed: foreground, Note: note);
    }, OpenTimeout);

    private static string? TryGetLocalizedName(dynamic? node)
    {
        if (node is null) return null;
        try { return (string?)node.Name; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Prefer the HWND of the DTE we are attached to (correct even with several XAE up);
    // fall back to the single TcXaeShell process window if the DTE doesn't expose it.
    private static IntPtr TryGetXaeMainWindow(dynamic dte)
    {
        try
        {
            long h = (long)(uint)(int)dte.MainWindow.HWnd;
            if (h != 0) return new IntPtr(h);
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        try
        {
            var procs = Process.GetProcessesByName("TcXaeShell");
            if (procs.Length == 1 && procs[0].MainWindowHandle != IntPtr.Zero)
                return procs[0].MainWindowHandle;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        return IntPtr.Zero;
    }
}
