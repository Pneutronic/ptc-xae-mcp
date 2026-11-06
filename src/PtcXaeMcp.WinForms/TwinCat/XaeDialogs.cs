using System.Runtime.InteropServices;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One child control of a top-level window (class, text read via WM_GETTEXT, enabled).</summary>
internal sealed record XaeControl(IntPtr Hwnd, string ClassName, string Text, bool Enabled = true)
{
    public bool IsButton => ClassName.Contains("button", StringComparison.OrdinalIgnoreCase);
    public bool IsEdit => ClassName.Contains("edit", StringComparison.OrdinalIgnoreCase);

    /// <summary>Caption without the accelerator '&amp;' and surrounding blanks.</summary>
    public string Caption => Text.Replace("&", "", StringComparison.Ordinal).Trim();
}

/// <summary>Snapshot of one visible top-level window and its child controls.</summary>
internal sealed record XaeDialog(
    IntPtr Hwnd,
    uint Pid,
    IntPtr Owner,
    string ClassName,
    string Title,
    IReadOnlyList<XaeControl> Controls)
{
    public bool IsMessageBox => string.Equals(ClassName, "#32770", StringComparison.Ordinal);

    /// <summary>Non-empty child texts, in enumeration order.</summary>
    public IEnumerable<string> Texts => Controls.Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t));
}

/// <summary>
/// Which top-level windows dialog automation may look at (issue #150, C-1…C-4): only windows of
/// the attached TcXaeShell process, or windows owned by its DTE main window — and never a window
/// of the current (host) process, whose own GUI shows XAE-like texts ("Testlizenzen generieren",
/// ".tclrs" paths in its log, "Neustart bestätigen").
/// </summary>
internal sealed record XaeWindowScope(IReadOnlySet<uint> XaePids, IntPtr XaeMain, uint SelfPid)
{
    public bool Includes(uint pid, IntPtr owner) =>
        XaeModalProbe.InScope(pid, owner, XaePids, XaeMain, SelfPid);

    public bool Includes(XaeDialog d) => Includes(d.Pid, d.Owner);

    /// <summary>
    /// Scope of the XAE whose main window is <paramref name="xaeMain"/> (the attached DTE). Without a
    /// handle (pure-Win32 tools that must not touch COM) every running TcXaeShell counts — the host
    /// process never does.
    /// </summary>
    public static XaeWindowScope For(IntPtr xaeMain)
    {
        var pids = new HashSet<uint>();
        uint mainPid = xaeMain == IntPtr.Zero ? 0 : XaeModalProbe.PidOf(xaeMain);
        if (mainPid != 0) pids.Add(mainPid);
        else
        {
            pids.UnionWith(XaeModalProbe.XaePids());
            xaeMain = pids.Count == 1 ? MainWindowOf(pids.First()) : IntPtr.Zero;
        }
        return new XaeWindowScope(pids, xaeMain, (uint)Environment.ProcessId);
    }

    private static IntPtr MainWindowOf(uint pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.MainWindowHandle;
        }
        catch (Exception) { return IntPtr.Zero; } // process gone meanwhile
    }

    public bool IsEmpty => XaePids.Count == 0 && XaeMain == IntPtr.Zero;
}

/// <summary>How to press a button of a dialog: BM_CLICK on the located control, or — only when no
/// such control exists — WM_COMMAND with the standard dialog id.</summary>
internal sealed record ClickPlan(IntPtr Button, int FallbackCommandId, string Description)
{
    public bool UsesButton => Button != IntPtr.Zero;
}

/// <summary>
/// Win32 side of dialog automation: enumerate the in-scope windows, read their controls, press
/// buttons. All cross-process messages go through <c>SendMessageTimeout(SMTO_ABORTIFHUNG)</c> so a
/// hung XAE cannot hang the caller (C-22); outcomes are verified by the caller from window state,
/// never from a message return value (C-7).
/// </summary>
internal static class XaeDialogs
{
    private const uint WM_SETTEXT = 0x000C;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint BM_CLICK = 0x00F5;
    private const uint BM_GETCHECK = 0x00F0;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint GW_OWNER = 4;

    public const int IDOK = 1;
    public const int IDCANCEL = 2;
    public const int IDYES = 6;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc fn, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc fn, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hwnd, uint msg, IntPtr wp, StringBuilder lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutString(IntPtr hwnd, uint msg, IntPtr wp, string lp, uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeoutInt(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeoutMs, out IntPtr result);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    // ---- pure helpers (unit-tested) ------------------------------------------

    /// <summary>The in-scope windows among <paramref name="windows"/> that satisfy <paramref name="match"/>.</summary>
    public static IEnumerable<XaeDialog> InScope(IEnumerable<XaeDialog> windows, XaeWindowScope scope, Func<XaeDialog, bool> match) =>
        windows.Where(w => scope.Includes(w) && match(w));

    /// <summary>
    /// Button to press: the first button control whose caption equals one of <paramref name="labels"/>
    /// (case-insensitive, accelerator ignored) → BM_CLICK on it; otherwise WM_COMMAND
    /// <paramref name="fallbackCommandId"/> to the dialog (plain message boxes route that id to their
    /// button; WinForms windows ignore it, which the caller detects because the window stays open).
    /// </summary>
    public static ClickPlan PlanClick(XaeDialog d, int fallbackCommandId, params string[] labels)
    {
        var btn = d.Controls.FirstOrDefault(c => c.IsButton &&
            labels.Any(l => string.Equals(c.Caption, l.Replace("&", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)));
        return btn is not null
            ? new ClickPlan(btn.Hwnd, fallbackCommandId, $"BM_CLICK '{btn.Caption}'")
            : new ClickPlan(IntPtr.Zero, fallbackCommandId, $"WM_COMMAND {fallbackCommandId} (no '{string.Join("'/'", labels)}' button found)");
    }

    /// <summary>First button whose caption CONTAINS one of the needles (radio/checkbox captions vary).</summary>
    public static XaeControl? FindButtonContaining(XaeDialog d, params string[] needles) =>
        d.Controls.FirstOrDefault(c => c.IsButton && needles.Any(n => c.Caption.Contains(n, StringComparison.OrdinalIgnoreCase)));

    // ---- Win32 ----------------------------------------------------------------

    /// <summary>
    /// Visible top-level windows in <paramref name="scope"/> with their controls. The XAE main window
    /// itself is skipped unless <paramref name="includeMainWindow"/> (its descendant walk is large and
    /// it is never a dialog). <paramref name="keep"/> (hwnd, owner, class name) filters windows before
    /// their controls are read, so a caller interested in a few windows pays for no cross-process text
    /// reads of the others. Never throws.
    /// </summary>
    public static List<XaeDialog> Snapshot(XaeWindowScope scope, bool includeMainWindow = false,
        Func<IntPtr, IntPtr, string, bool>? keep = null)
    {
        var list = new List<XaeDialog>();
        if (scope.IsEmpty) return list;
        try
        {
            EnumWindows((hwnd, lp) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                _ = GetWindowThreadProcessId(hwnd, out uint pid);
                IntPtr owner = GetWindow(hwnd, GW_OWNER);
                if (!scope.Includes(pid, owner)) return true;
                // The ownerless XAE main window is huge and never a dialog — skip its child walk.
                if (hwnd == scope.XaeMain && !includeMainWindow) return true;
                string className = ClassOf(hwnd);
                if (keep is not null && !keep(hwnd, owner, className)) return true;
                list.Add(new XaeDialog(hwnd, pid, owner, className, TitleOf(hwnd), Controls(hwnd)));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception) { /* enumeration hiccup — treat as "nothing found" */ }
        return list;
    }

    /// <summary>The in-scope windows satisfying <paramref name="match"/>, freshly enumerated.</summary>
    public static List<XaeDialog> Find(XaeWindowScope scope, Func<XaeDialog, bool> match) =>
        InScope(Snapshot(scope), scope, match).ToList();

    // Bound per window: the License Manager tab lives in the main window's (large) descendant tree.
    private const int MaxControls = 2000;

    private static List<XaeControl> Controls(IntPtr hwnd)
    {
        var list = new List<XaeControl>();
        EnumChildWindows(hwnd, (child, lp) =>
        {
            list.Add(new XaeControl(child, ClassOf(child), ReadText(child), IsWindowEnabled(child)));
            return list.Count < MaxControls;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Executes <paramref name="plan"/>: synchronous BM_CLICK (bounded) or WM_COMMAND to the dialog.</summary>
    public static void Execute(XaeDialog d, ClickPlan plan)
    {
        if (plan.UsesButton)
            Click(plan.Button);
        else
            _ = SendMessageTimeoutInt(d.Hwnd, WM_COMMAND, (IntPtr)plan.FallbackCommandId, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>BM_CLICK, bounded: a click that opens a nested modal returns after the timeout.</summary>
    public static void Click(IntPtr button) =>
        _ = SendMessageTimeoutInt(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);

    public static bool IsChecked(IntPtr button) =>
        SendMessageTimeoutInt(button, BM_GETCHECK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 500, out IntPtr r) != IntPtr.Zero
        && (long)r == 1;

    public static void SetText(IntPtr control, string text) =>
        _ = SendMessageTimeoutString(control, WM_SETTEXT, IntPtr.Zero, text, SMTO_ABORTIFHUNG, 1000, out _);

    /// <summary>WM_CLOSE (posted: the dialog's own close path runs asynchronously).</summary>
    public static void Close(IntPtr hwnd) => _ = PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static bool IsOpen(IntPtr hwnd) => IsWindow(hwnd) && IsWindowVisible(hwnd);

    /// <summary>True when <paramref name="hwnd"/> accepts input; a modal dialog disables its owner.</summary>
    public static bool IsEnabled(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowEnabled(hwnd);

    /// <summary>Polls until <paramref name="hwnd"/> is gone or hidden.</summary>
    public static bool WaitClosed(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsOpen(hwnd))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    /// <summary>WM_GETTEXT, bounded — reads control text across processes (GetWindowText does not for edits).</summary>
    public static string ReadText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        _ = SendMessageTimeoutText(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, 200, out _);
        return sb.ToString();
    }

    private static string TitleOf(IntPtr hwnd) { var sb = new StringBuilder(256); _ = GetWindowText(hwnd, sb, 256); return sb.ToString(); }
    private static string ClassOf(IntPtr hwnd) { var sb = new StringBuilder(256); _ = GetClassName(hwnd, sb, 256); return sb.ToString(); }
}
