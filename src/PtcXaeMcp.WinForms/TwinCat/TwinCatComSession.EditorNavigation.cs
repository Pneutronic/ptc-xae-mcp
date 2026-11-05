namespace PtcXaeMcp.WinForms.TwinCat;

// ---- editor-navigation: cursor to a pane line in the visible XAE -------------
//
// The TwinCAT ST editor is NOT an EnvDTE text editor (Document.Selection and
// Object("TextDocument") are null — proven live),
// so the cursor is moved the way a user does it: focus the pane, run the VS
// `Edit.GoTo` command, and answer its modal "Gehe zu Zeile" dialog. The command
// blocks its caller until the modal closes and makes every other DTE call return
// RPC_E_SERVERCALL_RETRYLATER meanwhile, so it runs on a dedicated STA worker with
// its own ROT-resolved DTE proxy while THIS thread drives the dialog via Win32 only.
// Verified Offline (PTC_Template) and Online on a real machine (100000, FB_Spindle):
// no logout is needed, and the implementation pane keeps its monitoring values.
internal sealed partial class TwinCatComSession
{
    // Command trigger: the command re-opens the dialog once (issue #150), so after each close the
    // driver watches until the command returns (Reopen is only the upper bound).
    private static readonly GoToDialogAutomation.Timings CommandTimings = new(
        Appear: TimeSpan.FromSeconds(8), Close: TimeSpan.FromSeconds(3),
        Reopen: TimeSpan.FromSeconds(5), CommandReturn: TimeSpan.FromSeconds(5));
    // Hotkey trigger: no command to wait for — a short watch for a re-opened dialog.
    private static readonly GoToDialogAutomation.Timings HotkeyTimings = new(
        Appear: TimeSpan.FromSeconds(5), Close: TimeSpan.FromSeconds(3),
        Reopen: TimeSpan.FromMilliseconds(800), CommandReturn: TimeSpan.Zero);

    public GotoLineResult GotoLine(string treePath, int line, EditorSection section, bool bringToFront = false)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            throw new InvalidOperationException("treePath is required.");
        if (line < 1)
            throw new InvalidOperationException($"line must be >= 1, got {line}.");

        // Placing the cursor needs keyboard focus in the editor pane (UIAutomation SetFocus), which activates the
        // XAE window. The XAE is brought to the foreground only on request (a person asked where it is).
        if (!bringToFront && !ComBusyRetry.Run(() => Win32DialogAutomation.IsXaeForeground((IntPtr)TryGetXaeMainWindow(GetActiveTcDte())), ProbeTimeout))
            throw new InvalidOperationException(
                "placing the cursor needs the XAE in the foreground, and it is not brought there unasked. Call again with " +
                "bringToFront=true when a person asked where it is; twincat_locate_step finds a step without touching the XAE.");

        // 1. Tab activation (reliable); the XAE is raised only with bringToFront.
        var open = OpenPlcObject(treePath, bringToFront);

        // 2. The pane's cleartext lets us pick the right SynEdControl by content — and bounds the
        //    line: the XAE crashes on a line beyond the pane (issue #150).
        string? expected = TryReadPaneText(treePath, section);
        if (expected is not null && line > EditorPaneAutomation.LineCount(expected))
            throw new InvalidOperationException(
                $"line {line} is beyond the {section.ToString().ToLowerInvariant()} pane of '{treePath}' " +
                $"({EditorPaneAutomation.LineCount(expected)} lines) — the XAE Go-To command fails on such a line, " +
                "so it was not sent.");

        // 3. Handles: the active document window (hosts the panes) and the XAE main
        //    window (owner of the modal dialog). Both are DTE reads → busy-retry.
        IntPtr docHwnd = ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            return new IntPtr((long)(uint)(int)dte.ActiveWindow.HWnd);
        }, ProbeTimeout);
        IntPtr mainHwnd = ComBusyRetry.Run(() => TryGetXaeMainWindow(GetActiveTcDte()), ProbeTimeout);
        if (docHwnd == IntPtr.Zero || mainHwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                "Could not obtain the XAE document/main window handles needed to drive the Go-To dialog.");

        // 4. Pane focus — precondition for Edit.GoTo being available.
        var pane = EditorPaneAutomation.TryFocusPane(docHwnd, section, expected);
        if (!pane.PaneFound)
            throw new InvalidOperationException(
                $"{section.ToString().ToLowerInvariant()} pane not found for '{treePath}': {pane.Note}. " +
                "While the XAE is Online the declaration pane is replaced by the monitoring grid — use section=implementation.");

        // 5. An orphaned Go-To dialog from an earlier call would swallow this one: cancel it first.
        string pre = GoToDialogAutomation.TryCancelDialog(mainHwnd) ? "orphaned Go-To dialog from an earlier call cancelled; " : "";

        // 6. Trigger the command and drive its dialog(s).
        var (dialogSeen, closed, trigger, note) = TriggerAndDriveGoToDialog(mainHwnd, line, bringToFront);

        return new GotoLineResult(
            TreePath: treePath,
            Section: section,
            Line: line,
            Opened: open.Opened,
            AlreadyOpen: open.AlreadyOpen,
            PaneFocused: pane.Focused,
            DialogSeen: dialogSeen,
            CursorConfirmed: dialogSeen && closed,
            ForegroundRequested: open.ForegroundRequested,
            ForegroundConfirmed: open.ForegroundConfirmed,
            Trigger: dialogSeen ? trigger : null,
            Note: $"{pre}{pane.Note}; {note}");
    }

    // Best-effort: a POU exposes both texts; a GVL/DUT only the declaration; anything
    // unreadable just falls back to pane selection by order.
    private string? TryReadPaneText(string treePath, EditorSection section)
    {
        try
        {
            var body = ReadPou(treePath);
            return section == EditorSection.Implementation ? body.Implementation : body.Declaration;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            if (section != EditorSection.Declaration) return null;
            try { return ReadGvl(treePath); } catch (Exception comEx2) when (ComCall.NotBusy(comEx2)) { return null; }
        }
    }

    // Trigger A: the Edit.GoTo command on an STA worker (blocks until its modal(s) close;
    // takes NO argument — passing one throws "akzeptiert keine Argumente oder Schalter").
    // Trigger B: the Ctrl+G hotkey — the fallback when the command reports "nicht verfügbar"
    // (typically: the pane never got keyboard focus). Sent only when the attached XAE main
    // window is the OS foreground window immediately before sending (issue #150, C-8).
    private static (bool DialogSeen, bool Closed, string Trigger, string Note) TriggerAndDriveGoToDialog(
        IntPtr mainHwnd, int line, bool bringToFront)
    {
        using var commandDone = new ManualResetEventSlim(false);
        Exception? commandError = null;
        var worker = new Thread(() =>
        {
            try
            {
                dynamic dte = GetActiveTcDte();
                dte.ExecuteCommand("Edit.GoTo");
            }
            catch (Exception ex) { commandError = ex; } // com-allow: thread entry — captured, reported by the caller
            finally { commandDone.Set(); }
        })
        { IsBackground = true, Name = "EditGoTo" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        string trigger = "Edit.GoTo command";
        var outcome = GoToDialogAutomation.Drive(GoToDialogAutomation.Win32, mainHwnd, line, CommandTimings, () => commandDone.IsSet);
        string note = outcome.Note;

        if (!outcome.DialogSeen)
        {
            if (!commandDone.Wait(TimeSpan.FromSeconds(2)))
            {
                // The command neither showed the dialog nor returned: something else holds XAE.
                string? box = XaeModalProbe.FindBlockingDialog();
                return (false, false, trigger,
                    $"{note}; Edit.GoTo command still running" + (box is null ? "" : $" — XAE shows '{box}'"));
            }
            if (commandError is not null) note += $"; Edit.GoTo command failed: {FirstLine(commandError.Message)}";

            if (bringToFront) Win32DialogAutomation.BringXaeToFront(mainHwnd);
            if (!GoToDialogAutomation.SendCtrlGToXae(mainHwnd))
                return (false, false, trigger, $"{note}; Ctrl+G fallback not sent: the XAE window is not the foreground window");

            trigger = "Ctrl+G hotkey";
            outcome = GoToDialogAutomation.Drive(GoToDialogAutomation.Win32, mainHwnd, line, HotkeyTimings, commandDone: null);
            note = $"{note}; {outcome.Note}";
        }

        return (outcome.DialogSeen, outcome.Closed, trigger, note);
    }

    private static string FirstLine(string s)
    {
        int i = s.IndexOfAny(new[] { '\r', '\n' });
        return i < 0 ? s : s[..i];
    }
}
