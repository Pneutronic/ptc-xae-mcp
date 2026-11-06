using System.Runtime.InteropServices;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>The Win32 operations the Go-To driver needs — faked in unit tests.</summary>
internal interface IGoToDialogWin32
{
    GoToDialogAutomation.DialogHandles? FindDialog(IntPtr xaeMain);
    bool IsOpen(IntPtr hwnd);
    bool IsReady(GoToDialogAutomation.DialogHandles dialog);
    void SetText(IntPtr edit, string text);
    string ReadText(IntPtr edit);
    /// <summary>BM_CLICK on the located OK button.</summary>
    void Click(IntPtr button);
    /// <summary>WM_COMMAND IDOK to the dialog — fallback only.</summary>
    void CommandOk(IntPtr dialog, IntPtr okButton);
    /// <summary>WM_CLOSE — cancels the dialog.</summary>
    void Close(IntPtr dialog);
    /// <summary>An XAE error/assertion message box that may be dismissed, or null.</summary>
    GoToDialogAutomation.ErrorBox? FindErrorBox(IntPtr xaeMain);
    /// <summary>
    /// Cancels one other dialog that blocks the XAE (e.g. "Variable deklarieren" opened by keys that reached the
    /// editor) through its Cancel button — never a destructive dialog. Returns its title, or null when there is none.
    /// </summary>
    string? CancelStrayDialog(IntPtr xaeMain);
}

/// <summary>
/// Win32 driver for the TwinCAT Go-To dialog ("Gehe zu Zeile" / "Go To Line") that the
/// VS <c>Edit.GoTo</c> command opens on a POU document.
/// Runs on the calling (non-COM) thread while the command
/// itself blocks a worker thread until the modal closes.
///
/// <para><b>Issue #150 (LIVE-5), proven live on TcXaeShell 4026.22:</b> one
/// <c>DTE.ExecuteCommand("Edit.GoTo")</c> shows the dialog TWICE — the first one closes on OK
/// (or Cancel, or WM_CLOSE) and ~40 ms later a second instance opens, pre-filled with the line
/// the first one moved the cursor to; <c>ExecuteCommand</c> returns only after the second one
/// closes. Reproduced from a separate process with nothing but ROT + ExecuteCommand, so it is
/// XAE's command routing, not this driver. The Ctrl+G hotkey opens it once. The old driver
/// stopped after the first close and reported success while the second dialog kept XAE modal.
/// This driver therefore keeps answering every Go-To dialog that appears while the command
/// is still running, and succeeds only when the command has returned AND no Go-To dialog is
/// open; otherwise it cancels whatever is still open and reports failure.</para>
///
/// Discovery is structural (<see cref="GoToDialogMatcher"/>) — never by the localized title —
/// and limited to windows owned by the XAE main window in the XAE process, never the host.
/// </summary>
internal static class GoToDialogAutomation
{
    internal sealed record DialogHandles(IntPtr Hwnd, string Title, IntPtr Edit, IntPtr Ok);

    internal sealed record DriveOutcome(bool DialogSeen, bool Closed, string Note, int DialogsAnswered = 0);

    /// <summary>An XAE message box raised by the Go-To command, and the button that safely dismisses it.</summary>
    internal sealed record ErrorBox(IntPtr Hwnd, string Text, IntPtr DismissButton);

    /// <summary>
    /// Which button of an XAE message box raised during the Go-To drive may be pressed: the only
    /// button of a pure notification ("OK"), or "Ignorieren"/"Ignore" of a .NET assertion box
    /// ("Abbrechen=Beenden" there would TERMINATE the XAE). Anything else — a real question — is
    /// never answered (zero). Seen live (4026.22) for a line beyond the pane: "Fehler beim
    /// Ausführen des Batch-Befehls 'text gotoline'", then an assertion, then "synEdControl" NULL.
    /// </summary>
    internal static IntPtr PickErrorBoxButton(XaeDialog box)
    {
        if (!box.IsMessageBox) return IntPtr.Zero;
        var buttons = box.Controls.Where(c => c.IsButton).ToList();
        if (buttons.Count == 1 && GoToDialogMatcher.IsOkText(buttons[0].Text)) return buttons[0].Hwnd;
        var ignore = buttons.FirstOrDefault(b =>
            b.Caption.Equals("Ignorieren", StringComparison.OrdinalIgnoreCase) ||
            b.Caption.Equals("Ignore", StringComparison.OrdinalIgnoreCase));
        return ignore?.Hwnd ?? IntPtr.Zero;
    }

    /// <param name="Appear">How long to wait for the first dialog.</param>
    /// <param name="Close">How long one OK press may take to close a dialog.</param>
    /// <param name="Reopen">After a close: how long to watch for the next dialog. With a command
    /// this is an upper bound — the wait ends as soon as the command returns.</param>
    /// <param name="CommandReturn">After the last dialog: how long the command may take to return.</param>
    internal sealed record Timings(TimeSpan Appear, TimeSpan Close, TimeSpan Reopen, TimeSpan CommandReturn);

    /// <summary>Upper bound of dialogs answered for one trigger (observed: 2 for the command, 1 for Ctrl+G).</summary>
    internal const int MaxDialogs = 4;

    /// <summary>
    /// Drives the Go-To dialog(s) owned by <paramref name="xaeMain"/> to <paramref name="line"/>.
    /// <paramref name="commandDone"/> reports whether the triggering <c>Edit.GoTo</c> command has
    /// returned; null for the hotkey trigger (no command to wait for). Never throws.
    /// <c>Closed</c> is true only when every dialog closed on OK, no Go-To dialog is open anymore
    /// and — with a command — the command has returned. On any other outcome the dialog still open
    /// is cancelled (WM_CLOSE) so XAE stays responsive, and <c>Closed</c> is false.
    /// </summary>
    public static DriveOutcome Drive(IGoToDialogWin32 w, IntPtr xaeMain, int line, Timings t, Func<bool>? commandDone)
    {
        string lineText = line.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dlg = WaitForDialog(w, xaeMain, t.Appear, commandDone);
        if (dlg is null) return new DriveOutcome(false, false, "Go-To dialog did not appear");

        string title = dlg.Title;
        var notes = new List<string>();
        var xaeErrors = new List<string>();
        int answered = 0;
        string? stuck = null;

        // XAE error boxes raised by the command: dismiss the safe ones, remember their text.
        bool ErrorsSeen()
        {
            for (int guard = 0; guard < 5; guard++)
            {
                var box = w.FindErrorBox(xaeMain);
                if (box is null) break;
                xaeErrors.Add(Flatten(box.Text));
                if (box.DismissButton == IntPtr.Zero) break;   // a real question — never answered
                w.Click(box.DismissButton);
                WaitUntil(() => !w.IsOpen(box.Hwnd), t.Close);
                Thread.Sleep(300); // the next box of a chain opens right after
            }
            return xaeErrors.Count > 0;
        }

        while (dlg is not null)
        {
            if (answered >= MaxDialogs) { stuck = $"the Go-To dialog kept re-opening ({answered} answered)"; break; }
            if (answered > 0)
            {
                string pre = w.ReadText(dlg.Edit).Trim();
                notes.Add(pre == lineText
                    ? $"re-opened by XAE pre-filled with line {pre} (cursor is there) — answered again"
                    : $"re-opened by XAE pre-filled with '{pre}' — answered again");
            }

            string? how = Answer(w, xaeMain, dlg, lineText, t.Close);
            if (ErrorsSeen()) { stuck = "XAE reported an error"; break; }
            if (how is null) { stuck = $"dialog {answered + 1} did not close on OK"; break; }
            if (answered == 0) notes.Insert(0, $"line {line} entered, closed on {how}");
            answered++;

            dlg = WaitForNext(w, xaeMain, t.Reopen, commandDone);
            if (ErrorsSeen()) { stuck = "XAE reported an error"; break; }
        }

        // Verdict — never report success while XAE is still modal.
        if (stuck is null && commandDone is not null &&
            !WaitUntil(() => commandDone() || w.FindErrorBox(xaeMain) is not null, t.CommandReturn))
            stuck = "the Edit.GoTo command did not return — XAE may still be busy";
        if (ErrorsSeen()) stuck ??= "XAE reported an error";

        var open = w.FindDialog(xaeMain);
        if (open is null && stuck is null)
            return new DriveOutcome(true, true, $"Go-To dialog '{title}': {string.Join("; ", notes)}", answered);

        string why = stuck ?? "a Go-To dialog is still open";
        if (xaeErrors.Count > 0)
            why += $": '{string.Join("' / '", xaeErrors.Distinct())}' (dismissed where safe)";
        if (open is not null)
        {
            w.Close(open.Hwnd);
            bool gone = WaitUntil(() => !w.IsOpen(open.Hwnd), t.Close);
            ErrorsSeen();
            why += gone ? " — Go-To dialog cancelled (WM_CLOSE) to keep XAE responsive"
                        : " — WM_CLOSE sent but the Go-To dialog is STILL open; close it in the XAE";
        }
        var swept = SweepAfterFailure(w, xaeMain, t, commandDone);
        if (swept.Count > 0)
            why += $" — afterwards cancelled to leave the XAE free: {string.Join(", ", swept.Select(s => $"'{s}'"))}";
        string done = notes.Count == 0 ? "" : $"{string.Join("; ", notes)}; ";
        return new DriveOutcome(true, false, $"Go-To dialog '{title}': {done}{why}", answered);
    }

    /// <summary>How long a failed drive keeps clearing dialogs it left behind.</summary>
    internal static readonly TimeSpan SweepWindow = TimeSpan.FromSeconds(4);

    /// <summary>
    /// A failed drive must not leave the XAE modal: the command may still re-open the Go-To dialog after the verdict,
    /// and keys that reached the editor can open another dialog ("Variable deklarieren") the command then waits for.
    /// For <see cref="SweepWindow"/>, or until the command has returned and nothing is open, every Go-To dialog is
    /// cancelled (WM_CLOSE) and every other non-destructive blocking dialog is cancelled through its Cancel button.
    /// Seen in a live run: both were left open and blocked every later call. Returns what was cancelled.
    /// </summary>
    internal static List<string> SweepAfterFailure(IGoToDialogWin32 w, IntPtr xaeMain, Timings t, Func<bool>? commandDone)
    {
        var swept = new List<string>();
        var deadline = DateTime.UtcNow + SweepWindow;
        while (DateTime.UtcNow < deadline && swept.Count < 8)
        {
            if (w.CancelStrayDialog(xaeMain) is { } stray)
            {
                swept.Add(stray);
                continue;
            }
            if (w.FindDialog(xaeMain) is { } goTo)
            {
                w.Close(goTo.Hwnd);
                if (WaitUntil(() => !w.IsOpen(goTo.Hwnd), t.Close)) swept.Add(goTo.Title);
                continue;
            }
            if (commandDone is null || commandDone()) break;
            Thread.Sleep(100);
        }
        return swept;
    }

    private static string Flatten(string text) =>
        string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();

    // Enter the line and press OK (BM_CLICK, re-clicked once — the first click right after
    // creation was observed to be swallowed), WM_COMMAND IDOK only as the last resort.
    // Returns how it closed, or null when it did not (or an XAE error box took over).
    private static string? Answer(IGoToDialogWin32 w, IntPtr xaeMain, DialogHandles dlg, string lineText, TimeSpan close)
    {
        WaitUntil(() => w.IsReady(dlg), TimeSpan.FromSeconds(1));
        Thread.Sleep(150);
        w.SetText(dlg.Edit, lineText);
        string echoed = w.ReadText(dlg.Edit).Trim();
        string suffix = echoed == lineText ? "" : $" (edit read back '{echoed}')";

        bool ClosedOrError() => !w.IsOpen(dlg.Hwnd) || w.FindErrorBox(xaeMain) is not null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            w.Click(dlg.Ok);
            if (WaitUntil(ClosedOrError, close)) return w.IsOpen(dlg.Hwnd) ? null : $"OK (click {attempt}){suffix}";
        }
        w.CommandOk(dlg.Hwnd, dlg.Ok);
        return WaitUntil(ClosedOrError, close) && !w.IsOpen(dlg.Hwnd) ? $"WM_COMMAND IDOK{suffix}" : null;
    }

    internal static DialogHandles? WaitForDialog(IGoToDialogWin32 w, IntPtr xaeMain, TimeSpan timeout, Func<bool>? commandDone)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var dlg = w.FindDialog(xaeMain);
            if (dlg is not null) return dlg;
            if (DateTime.UtcNow >= deadline) return null;
            // Give a fast-failing command a beat to surface its dialog before giving up.
            if (commandDone?.Invoke() == true) { Thread.Sleep(300); return w.FindDialog(xaeMain); }
            Thread.Sleep(50);
        }
    }

    // After a dialog closed: the next one (XAE re-opens it while the command runs), or null once
    // the command returned / the watch window elapsed with no dialog.
    private static DialogHandles? WaitForNext(IGoToDialogWin32 w, IntPtr xaeMain, TimeSpan window, Func<bool>? commandDone)
    {
        var deadline = DateTime.UtcNow + window;
        while (true)
        {
            var dlg = w.FindDialog(xaeMain);
            if (dlg is not null) return dlg;
            if (commandDone?.Invoke() == true) { Thread.Sleep(50); return w.FindDialog(xaeMain); }
            if (DateTime.UtcNow >= deadline || w.FindErrorBox(xaeMain) is not null) return null;
            Thread.Sleep(20);
        }
    }

    /// <summary>
    /// Ctrl+G via SendInput — only when <paramref name="xaeMain"/> is the foreground window
    /// immediately before sending (C-8); keystrokes into any other window are never sent.
    /// Returns false (nothing sent) otherwise.
    /// </summary>
    public static bool SendCtrlGToXae(IntPtr xaeMain)
    {
        if (!Win32DialogAutomation.IsForeground(xaeMain)) return false;
        var inputs = new[]
        {
            Key(VK_CONTROL, 0), Key(VK_G, 0), Key(VK_G, KEYEVENTF_KEYUP), Key(VK_CONTROL, KEYEVENTF_KEYUP),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>
    /// Enter via SendInput — same foreground rule as <see cref="SendCtrlGToXae"/>: sent only when
    /// <paramref name="xaeMain"/> is the foreground window immediately before sending. Opens the
    /// focused Solution-Explorer row's editor (twincat_open_license_manager_and_request_trial).
    /// </summary>
    public static bool SendEnterToXae(IntPtr xaeMain)
    {
        if (!Win32DialogAutomation.IsForeground(xaeMain)) return false;
        var inputs = new[] { Key(VK_RETURN, 0), Key(VK_RETURN, KEYEVENTF_KEYUP) };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>Cancel an orphaned Go-To dialog if one exists (called before every goto). Returns true when one was found.</summary>
    public static bool TryCancelDialog(IntPtr xaeMain)
    {
        var dlg = Win32.FindDialog(xaeMain);
        if (dlg is null) return false;
        Win32.Close(dlg.Hwnd);
        WaitUntil(() => !Win32.IsOpen(dlg.Hwnd), TimeSpan.FromSeconds(2));
        return true;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try { if (condition()) return true; } catch (Exception) { /* window vanished mid-probe — re-check */ }
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(20);
        }
    }

    // ---- real Win32 ----------------------------------------------------------

    /// <summary>The production <see cref="IGoToDialogWin32"/>.</summary>
    internal static readonly IGoToDialogWin32 Win32 = new RealWin32();

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const int IDOK = 1;
    private const uint GW_OWNER = 4;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_G = 0x47;
    private const ushort VK_RETURN = 0x0D;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc fn, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc fn, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutInt(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }

    private static INPUT Key(ushort vk, uint flags) =>
        new() { Type = INPUT_KEYBOARD, U = new InputUnion { Ki = new KEYBDINPUT { Vk = vk, Flags = flags } } };

    private sealed class RealWin32 : IGoToDialogWin32
    {
        // Top-level windows owned by the XAE main window, in the XAE's process (never the host),
        // matched by structure.
        public DialogHandles? FindDialog(IntPtr xaeMain)
        {
            if (xaeMain == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(xaeMain, out uint xaePid);
            uint self = (uint)Environment.ProcessId;
            DialogHandles? found = null;
            try
            {
                EnumWindows((hwnd, lp) =>
                {
                    if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != xaeMain) return true;
                    _ = GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == self || pid != xaePid) return true;

                    var children = new List<(string, string)>();
                    IntPtr edit = IntPtr.Zero, ok = IntPtr.Zero;
                    EnumChildWindows(hwnd, (child, __) =>
                    {
                        string cls = ClassOf(child), text = TextOf(child);
                        children.Add((cls, text));
                        if (edit == IntPtr.Zero && cls.Contains("EDIT", StringComparison.OrdinalIgnoreCase)) edit = child;
                        if (ok == IntPtr.Zero && cls.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) && GoToDialogMatcher.IsOkText(text)) ok = child;
                        return true;
                    }, IntPtr.Zero);

                    if (GoToDialogMatcher.IsGoToDialog(new WindowShape(true, ClassOf(hwnd), children)))
                    {
                        found = new DialogHandles(hwnd, TextOf(hwnd), edit, ok);
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception) { /* enumeration hiccup — treat as "no dialog" */ }
            return found;
        }

        public bool IsOpen(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

        public bool IsReady(DialogHandles d) => IsWindowVisible(d.Hwnd) && IsWindowEnabled(d.Edit);

        public void SetText(IntPtr edit, string text) => XaeDialogs.SetText(edit, text);

        public string ReadText(IntPtr edit) => XaeDialogs.ReadText(edit);

        public void Click(IntPtr button) => XaeDialogs.Click(button);

        public void CommandOk(IntPtr dialog, IntPtr okButton) =>
            _ = SendMessageTimeoutInt(dialog, WM_COMMAND, (IntPtr)IDOK, okButton, SMTO_ABORTIFHUNG, 1000, out _);

        public void Close(IntPtr dialog) => _ = PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        public string? CancelStrayDialog(IntPtr xaeMain)
        {
            if (xaeMain == IntPtr.Zero) return null;
            try
            {
                var windows = PromptWindowReader.Snapshot(XaeWindowScope.For(xaeMain));
                IntPtr goTo = FindDialog(xaeMain)?.Hwnd ?? IntPtr.Zero; // the Go-To dialog itself is closed by WM_CLOSE
                foreach (var view in XaeDialogInspector.View(windows, XaeDialogs.IsEnabled))
                {
                    if (!view.Blocking || view.Class == XaeDialogClass.KnownDestructive) continue;
                    var window = windows.First(p => XaeDialogInspector.IdOf(p.Hwnd) == view.Id);
                    if (window.Hwnd == goTo) continue;
                    var cancel = window.Buttons.FirstOrDefault(b =>
                        b.Caption.Equals("Abbrechen", StringComparison.OrdinalIgnoreCase) ||
                        b.Caption.Equals("Cancel", StringComparison.OrdinalIgnoreCase));
                    if (cancel is null || !XaeDialogs.IsEnabled(window.Hwnd)) continue;
                    PromptWindowReader.Press(window, cancel);
                    return XaeDialogs.WaitClosed(window.Hwnd, TimeSpan.FromSeconds(2)) ? view.Title : null;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException) { }
            return null;
        }

        // Message boxes of the XAE process (owned by its main window, or ownerless like the .NET
        // assertion box) — never the host's.
        public ErrorBox? FindErrorBox(IntPtr xaeMain)
        {
            if (xaeMain == IntPtr.Zero) return null;
            var box = XaeDialogs.Find(XaeWindowScope.For(xaeMain), d => d.IsMessageBox).FirstOrDefault();
            return box is null ? null
                : new ErrorBox(box.Hwnd,
                    string.Join(" | ", box.Controls.Where(c => !c.IsButton).Select(c => c.Text).Where(t => t.Trim().Length > 0)),
                    PickErrorBoxButton(box));
        }
    }

    // Character counts ignored (CA1806): the StringBuilder is already sized for the
    // expected max text and read back via ToString(), so the count adds nothing here.
    private static string TextOf(IntPtr hwnd) { var sb = new StringBuilder(256); _ = GetWindowText(hwnd, sb, 256); return sb.ToString(); }
    private static string ClassOf(IntPtr hwnd) { var sb = new StringBuilder(256); _ = GetClassName(hwnd, sb, 256); return sb.ToString(); }
}
