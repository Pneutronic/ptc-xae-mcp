using System.Windows.Automation;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Result of closing the solution.</summary>
/// <param name="SavePrompt">What happened to the XAE's "save changes?" prompt during the close (see
/// <see cref="SaveChangesDialog"/>); null when the XAE raised none.</param>
/// <param name="SavePromptFailed">True when the prompt appeared but could not be answered as asked
/// (it was cancelled or is still open).</param>
/// <param name="ShellSaveFallback">True when the explicit save left something dirty (or its state was
/// unreadable) and the close therefore ran with the shell's own save option.</param>
public sealed record CloseSolutionResult(string? SavePrompt = null, bool SavePromptFailed = false, bool ShellSaveFallback = false);

/// <summary>One button of a <see cref="PromptWindow"/>: its caption, and its HWND for a Win32 button
/// (<see cref="IntPtr.Zero"/> for a WPF button, which has no window of its own and is pressed through
/// UI Automation by its name).</summary>
internal sealed record PromptButton(string Name, IntPtr Hwnd = default)
{
    /// <summary>Caption without accelerator markers ('&amp;' Win32/WinForms, '_' WPF) and surrounding blanks.</summary>
    public string Caption => Name.Replace("&", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Trim();
}

/// <summary>
/// A top-level XAE window as the save-prompt matcher needs it — read either from Win32 child windows
/// (#32770 / WinForms) or, for a WPF window (<c>HwndWrapper[…]</c>, no child HWNDs), through UI Automation.
/// </summary>
internal sealed record PromptWindow(
    IntPtr Hwnd,
    uint Pid,
    IntPtr Owner,
    string ClassName,
    string Title,
    IReadOnlyList<string> Texts,
    IReadOnlyList<PromptButton> Buttons)
{
    public bool IsWpf => ClassName.StartsWith("HwndWrapper", StringComparison.Ordinal);
}

/// <summary>
/// The shell prompt the XAE raises when a solution with unsaved changes is closed through
/// <c>dte.Solution.Close(true)</c> — reported live (TC3 XAE, German UI; a WPF window, the call blocked
/// ~196 s until it was clicked by hand):
/// <code>
/// TcXaeShell / Microsoft Visual Studio                           (WPF: HwndWrapper[…], no Win32 children)
///   Sollen die Änderungen an den folgenden Elementen gespeichert werden?
///   [Speichern] [Nicht speichern] [Abbrechen]
/// </code>
/// Verified live (2026-10-08, TC3 4026, German XAE, Close(true) forced on a dirty .tsproj): a visible
/// <c>HwndWrapper[DefaultDomain;;…]</c> window of the TcXaeShell process, owned by the XAE main window,
/// title "TcXaeShell"; UI Automation exposes the question as Text, the file list as List
/// "Zu speichernde Dateien" (items "….sln", "….tsproj*") and the buttons by name "Speichern",
/// "Nicht speichern", "Abbrechen" (access keys only in the inner Text "_Speichern"); InvokePattern on a
/// button closes the window and lets <c>Close(true)</c> return.
/// Answered deterministically: "Speichern" when the caller asked to save first, "Nicht speichern" otherwise.
/// <para>
/// Matching (umlaut-free fragments, case-insensitive): a text contains "nderungen" + "gespeichert"
/// (German) or "save changes" (English), AND the window has a save button and a don't-save button. The
/// English wording and the older "Ja/Nein" captions are not observed live — they are the cheap counterparts;
/// an unmatched variant is reported by the dispatcher's blocking-dialog message instead.
/// </para>
/// </summary>
internal static class SaveChangesDialog
{
    private static readonly string[] SaveCaptions = ["Speichern", "Save", "Ja", "Yes"];
    private static readonly string[] DontSaveCaptions = ["Nicht speichern", "Don't Save", "Don’t Save", "Do not save", "Nein", "No"];
    private static readonly string[] CancelCaptions = ["Abbrechen", "Cancel"];

    /// <summary>True when the title or a text is the save prompt's question.</summary>
    internal static bool IsPromptText(string? title, IEnumerable<string> texts) =>
        texts.Prepend(title ?? string.Empty).Any(x =>
            (Has(x, "nderungen") && Has(x, "gespeichert")) ||
            Has(x, "save changes"));

    /// <summary>The prompt itself: its question plus both a save and a don't-save button.</summary>
    internal static bool Matches(PromptWindow w) =>
        Find(w, SaveCaptions) is not null &&
        Find(w, DontSaveCaptions) is not null &&
        IsPromptText(w.Title, w.Texts);

    /// <summary>The button that answers the prompt for <paramref name="save"/>, or null when it has none.</summary>
    internal static PromptButton? PlanAnswer(PromptWindow w, bool save) => Find(w, save ? SaveCaptions : DontSaveCaptions);

    /// <summary>The cancel button, or null.</summary>
    internal static PromptButton? PlanCancel(PromptWindow w) => Find(w, CancelCaptions);

    internal static string AnswerName(bool save) => save ? "Speichern" : "Nicht speichern";

    // Exact caption equality: "Nicht speichern" contains "speichern", so a substring test would pick the
    // wrong button.
    private static PromptButton? Find(PromptWindow w, string[] captions) =>
        w.Buttons.FirstOrDefault(b => captions.Any(c => string.Equals(b.Caption, c, StringComparison.OrdinalIgnoreCase)));

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads in-scope XAE windows as <see cref="PromptWindow"/>s and presses their buttons: Win32 controls via
/// <see cref="XaeDialogs"/> (BM_CLICK through SendMessageTimeout), WPF windows via UI Automation
/// (<see cref="InvokePattern"/>) because a WPF window has no child HWNDs whose text WM_GETTEXT could read.
/// Never PostMessage for a decision; the outcome is always verified from window state by the caller.
/// </summary>
internal static class PromptWindowReader
{
    /// <summary>
    /// Candidate windows only: windows owned by another window (how a modal dialog of the main window looks),
    /// message boxes, and WPF windows (the XAE main window itself is skipped by
    /// <see cref="XaeDialogs.Snapshot"/>). Never throws.
    /// </summary>
    internal static List<PromptWindow> Snapshot(XaeWindowScope scope)
    {
        var result = new List<PromptWindow>();
        foreach (XaeDialog d in XaeDialogs.Snapshot(scope, keep: (_, owner, cls) =>
                     owner != IntPtr.Zero ||
                     string.Equals(cls, "#32770", StringComparison.Ordinal) ||
                     cls.StartsWith("HwndWrapper", StringComparison.Ordinal)))
        {
            try { result.Add(Read(d)); }
            catch (Exception) { /* window vanished or UIA hiccup — skip it this pass */ }
        }
        return result;
    }

    private static PromptWindow Read(XaeDialog d)
    {
        var w32Buttons = d.Controls.Where(c => c.IsButton).Select(c => new PromptButton(c.Text, c.Hwnd)).ToList();
        if (w32Buttons.Count > 0 || !d.ClassName.StartsWith("HwndWrapper", StringComparison.Ordinal))
            return new PromptWindow(d.Hwnd, d.Pid, d.Owner, d.ClassName, d.Title,
                d.Controls.Where(c => !c.IsButton).Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                w32Buttons);

        // WPF: texts and buttons live only in the UI Automation tree. The title bar (window title, system menu,
        // its Close button) is the frame, not the dialog, and is left out.
        var root = AutomationElement.FromHandle(d.Hwnd);
        var texts = new List<string>();
        var buttons = new List<PromptButton>();
        var walker = TreeWalker.ControlViewWalker;
        // Depth-first in document order: children are pushed last-first so the first one is visited first.
        var pending = new Stack<AutomationElement>();
        PushChildren(root);
        var ordered = new List<AutomationElement>();
        while (pending.Count > 0 && ordered.Count <= 200) // bounded: a prompt has a handful of elements
        {
            var e = pending.Pop();
            ControlType? type;
            try { type = e.Current.ControlType; }
            catch (ElementNotAvailableException) { continue; }
            if (type == ControlType.TitleBar || type == ControlType.MenuBar) continue;
            ordered.Add(e);
            PushChildren(e);
        }
        foreach (var e in ordered)
        {
            string name;
            ControlType? type;
            try { name = e.Current.Name ?? string.Empty; type = e.Current.ControlType; }
            catch (ElementNotAvailableException) { continue; }
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (type == ControlType.Button) buttons.Add(new PromptButton(name));
            else texts.Add(name);
        }
        return new PromptWindow(d.Hwnd, d.Pid, d.Owner, d.ClassName, d.Title, texts, buttons);

        void PushChildren(AutomationElement parent)
        {
            var children = new List<AutomationElement>();
            try
            {
                for (var child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
                    children.Add(child);
            }
            catch (ElementNotAvailableException) { return; }
            for (int i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
        }
    }

    /// <summary>
    /// Presses <paramref name="button"/>: BM_CLICK (synchronous, bounded) for a Win32 button, UIA Invoke for a
    /// WPF one (WPF's button peer queues the click on the dispatcher, so this returns while the dialog closes).
    /// Throws when a WPF button cannot be found or invoked — the caller then sees the window still open.
    /// </summary>
    internal static void Press(PromptWindow w, PromptButton button)
    {
        if (button.Hwnd != IntPtr.Zero)
        {
            XaeDialogs.Click(button.Hwnd);
            return;
        }
        var root = AutomationElement.FromHandle(w.Hwnd);
        var match = root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, button.Name)))
            ?? throw new InvalidOperationException($"UIA button '{button.Caption}' not found.");
        if (!match.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern))
            throw new InvalidOperationException($"UIA button '{button.Caption}' does not support Invoke.");
        ((InvokePattern)pattern).Invoke();
    }
}

/// <summary>
/// Background watcher (Win32 + UI Automation, no COM) that answers <see cref="SaveChangesDialog"/> while
/// <c>Solution.Close</c> blocks, scoped to the attached XAE — the save-prompt counterpart of
/// <see cref="RenameRefactoringWatcher"/>. Each prompt window is answered once and then verified gone by
/// window state; a prompt that stays open is cancelled (and closed) so the call cannot hang, and that is
/// reported.
/// </summary>
internal sealed class SaveChangesWatcher : IDisposable
{
    internal static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    private readonly XaeWindowScope _scope;
    private readonly bool _save;
    private readonly Func<XaeWindowScope, List<PromptWindow>> _snapshot;
    private readonly Action<PromptWindow, PromptButton> _press;
    private readonly Func<IntPtr, TimeSpan, bool> _waitClosed;
    private readonly Action<IntPtr> _close;
    private readonly HashSet<IntPtr> _handled = [];
    private readonly object _gate = new();
    private readonly List<string> _outcomes = [];
    private volatile bool _stop;
    private Thread? _thread;

    internal SaveChangesWatcher(XaeWindowScope scope, bool save,
        Func<XaeWindowScope, List<PromptWindow>>? snapshot = null,
        Action<PromptWindow, PromptButton>? press = null,
        Func<IntPtr, TimeSpan, bool>? waitClosed = null,
        Action<IntPtr>? close = null)
    {
        _scope = scope;
        _save = save;
        _snapshot = snapshot ?? PromptWindowReader.Snapshot;
        _press = press ?? PromptWindowReader.Press;
        _waitClosed = waitClosed ?? XaeDialogs.WaitClosed;
        _close = close ?? XaeDialogs.Close;
    }

    /// <summary>What was done with the prompt(s), "; "-joined; null while none appeared.</summary>
    internal string? Outcome
    {
        get { lock (_gate) return _outcomes.Count == 0 ? null : string.Join("; ", _outcomes); }
    }

    /// <summary>True when a prompt appeared but could not be answered as asked.</summary>
    internal bool Failed { get; private set; }

    internal void Start()
    {
        if (_scope.IsEmpty || _thread is not null) return;
        // MTA (the default for a new thread) is what UI Automation clients want.
        _thread = new Thread(Loop) { IsBackground = true, Name = "SaveChangesWatcher" };
        _thread.Start();
    }

    /// <summary>Stops polling, after one last pass for a prompt raised as the call returned.</summary>
    internal void Stop()
    {
        if (_thread is null) return;
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(15));
        _thread = null;
        try { Tick(_snapshot(_scope)); } catch (Exception) { /* never throw from Stop */ }
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (!_stop)
        {
            try { Tick(_snapshot(_scope)); }
            catch (Exception) { /* never crash the watcher */ }
            Thread.Sleep(200);
        }
    }

    /// <summary>One pass over one snapshot (unit-tested with synthetic windows).</summary>
    internal void Tick(IEnumerable<PromptWindow> windows)
    {
        lock (_gate)
        {
            foreach (PromptWindow w in windows)
            {
                if (!_scope.Includes(w.Pid, w.Owner)) continue;
                if (SaveChangesDialog.Matches(w) && _handled.Add(w.Hwnd))
                    _outcomes.Add(Answer(w));
            }
        }
    }

    private string Answer(PromptWindow w)
    {
        string title = string.IsNullOrWhiteSpace(w.Title) ? "save-changes prompt" : w.Title.Trim();
        string want = SaveChangesDialog.AnswerName(_save);
        string how = w.IsWpf ? "UIA Invoke" : "BM_CLICK";
        PromptButton? button = SaveChangesDialog.PlanAnswer(w, _save);
        if (button is not null)
        {
            TryPress(w, button);
            if (_waitClosed(w.Hwnd, CloseWait))
                return _save
                    ? $"'{title}' answered '{button.Caption}' ({how}) — changes saved by TwinCAT"
                    : $"'{title}' answered '{button.Caption}' ({how}) — changes discarded";
        }
        return CancelAfterFailedAnswer(w, title, want, how);
    }

    private string CancelAfterFailedAnswer(PromptWindow w, string title, string want, string how)
    {
        // The answer did not close it: never leave the close call hanging — cancel (the solution then
        // stays open, which the coordinator's read-back reports), then WM_CLOSE as a last resort.
        Failed = true;
        bool cancelled = false;
        if (SaveChangesDialog.PlanCancel(w) is { } cancel)
        {
            TryPress(w, cancel);
            cancelled = _waitClosed(w.Hwnd, CloseWait);
        }
        if (!cancelled)
        {
            _close(w.Hwnd);
            cancelled = _waitClosed(w.Hwnd, CloseWait);
        }
        return cancelled
            ? $"'{title}' could NOT be answered '{want}' ({how}, still open) — cancelled instead; the solution may still be open"
            : $"'{title}' could NOT be answered '{want}' nor cancelled — close it in the XAE";
    }

    private void TryPress(PromptWindow w, PromptButton b)
    {
        try { _press(w, b); }
        catch (Exception) { /* the window-state check that follows reports the miss */ }
    }
}
