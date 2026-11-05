using System.Runtime.InteropServices;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Win32 dialog watcher for the TwinCAT activation flow, plus the Win32-only licence helpers.
/// Runs on a background thread (no COM access) and answers the modal dialogs XAE raises during
/// ActivateConfiguration()/StartRestartTwinCAT():
///   1. "Neustart TwinCAT System im Run Modus" — OK (autoConfirmRestart)
///   2. trial-licence prompt — "Ja" (autoGenerateLicenses) or "Nein" (otherwise, so it cannot hang)
///   3. "Sicherheitscode eingeben" — code typed when readable; when it is not (TC 4026.22 draws it),
///      one screenshot and the dialog is CANCELLED so activation never hangs (issue #150, C-6)
///   4. the "Applikation … geändert" login modal (autoConfirmLogin)
///
/// Issue #150: every window is taken from <see cref="XaeDialogs.Snapshot"/> restricted to an
/// <see cref="XaeWindowScope"/> — the attached TcXaeShell process or windows owned by its DTE main
/// window, never the host process (C-1…C-4). Buttons are pressed with BM_CLICK on the located
/// control; WM_COMMAND is only the fallback when no such control exists, and no decision is ever
/// taken on a message return value (C-7). Outcomes are verified from window state.
/// </summary>
internal sealed class Win32DialogAutomation
{
    // ---- screen capture (PrintWindow first, CopyFromScreen fallback) ----------

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    private const int SW_RESTORE = 9;
    private const int IDNO = 7;
    // PrintWindow renders the window's content directly from the window (works
    // even when the window is occluded/in the background) — unlike CopyFromScreen,
    // which BitBlts the visible desktop and captures whatever is on top.
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ---- Config & state -------------------------------------------------

    internal sealed record WatcherConfig(
        bool AutoConfirmRestart,
        bool AutoGenerateLicenses,
        // When true, also confirm the "Applikation … seit dem letzten Download
        // geändert" login modal that re-activation raises (issue #136).
        bool AutoConfirmLogin = false,
        LoginMode LoginMode = LoginMode.OnlineChange,
        bool UpdateBootProject = true,
        // Issue #150 C-17: online_change never silently becomes a full download.
        bool AllowDownloadFallback = false,
        // The attached DTE main window — scopes every window match to that XAE.
        IntPtr XaeMain = default);

    private readonly WatcherConfig _cfg;
    private readonly XaeWindowScope _scope;
    private readonly HashSet<IntPtr> _captured = new();
    private volatile bool _stop;
    private Thread? _thread;

    // Records which radio the app-changed login modal was confirmed with, so the
    // activation flow can report it. "none" until the modal is handled.
    internal string LoginModalHandled { get; private set; } = "none";

    /// <summary>
    /// Set when a licence dialog was declined or cancelled so activation could not hang — the
    /// activation result must then report failure with this text, never success.
    /// </summary>
    internal string? LicenseProblem { get; private set; }

    /// <summary>Human-readable log of every dialog this watcher answered (for the result/trace).</summary>
    internal System.Collections.Concurrent.ConcurrentQueue<string> Actions { get; } = new();

    internal Win32DialogAutomation(WatcherConfig cfg)
    {
        _cfg = cfg;
        _scope = XaeWindowScope.For(cfg.XaeMain);
    }

    internal void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Win32DialogWatcher" };
        _thread.Start();
    }

    internal void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void Loop()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(290);  // #136: must outlive a LATE-appearing modal on a slow/degraded XAE (the blocking DTE/COM call doesn't return until it's dismissed). The watcher is Stop()ed the instant the operation returns, so in the normal fast path this cap is never reached.
        while (!_stop && DateTime.UtcNow < deadline)
        {
            try { Tick(XaeDialogs.Snapshot(_scope)); }
            catch (Exception) { /* never crash the watcher */ }
            Thread.Sleep(150);
        }
    }

    // One pass over one snapshot: answer at most the dialogs this configuration asks for.
    private void Tick(List<XaeDialog> windows)
    {
        if (windows.Count == 0) return;

        if (_cfg.AutoConfirmRestart)
            foreach (var d in windows.Where(IsRestartDialog))
                Press(d, XaeDialogs.PlanClick(d, XaeDialogs.IDOK, "OK", "Ja", "Yes"), "restart confirmation");

        foreach (var d in windows.Where(IsTrialLicensePrompt))
        {
            if (_cfg.AutoGenerateLicenses)
                Press(d, XaeDialogs.PlanClick(d, XaeDialogs.IDYES, "Ja", "Yes"), "trial-licence prompt (Ja)");
            else
            {
                // Unanswered, this prompt blocks ActivateConfiguration() for good.
                Press(d, XaeDialogs.PlanClick(d, IDNO, "Nein", "No"), "trial-licence prompt declined");
                LicenseProblem ??= "XAE asked to generate trial licences; declined because autoGenerateLicenses=false. " +
                                   "Generate them with twincat_open_license_manager_and_request_trial → " +
                                   "twincat_generate_trial_licenses → twincat_submit_security_code, then activate again.";
            }
        }

        if (_cfg.AutoConfirmLogin)
            foreach (var d in windows.Where(IsReplaceRunningAppModal))
            {
                if (!_captured.Add(d.Hwnd)) continue;
                string handled = HandleReplaceRunningAppModal(d, _cfg.LoginMode, _cfg.AllowDownloadFallback, _cfg.UpdateBootProject);
                LoginModalHandled = handled;
                Actions.Enqueue($"replace-running-app modal: {handled}");
            }

        if (_cfg.AutoConfirmLogin)
            foreach (var d in windows.Where(IsAppChangedLoginModal))
            {
                string handled = HandleAppChangedLoginModal(d, _cfg.LoginMode, _cfg.UpdateBootProject, _cfg.AllowDownloadFallback);
                LoginModalHandled = handled;
                Actions.Enqueue($"login modal: {handled}");
            }

        if (_cfg.AutoGenerateLicenses)
        {
            foreach (var d in windows.Where(IsSecurityCodeDialog))
            {
                var r = AnswerSecurityCodeDialog(d, code: null, _captured, cancelIfUnreadable: true);
                Actions.Enqueue($"security-code dialog: {r.Note}");
                if (!r.Submitted)
                    LicenseProblem ??= $"{r.Note}. Generate the trial licence with twincat_open_license_manager_and_request_trial → " +
                                       "twincat_generate_trial_licenses (returns the code as an image) → twincat_submit_security_code, then activate again.";
            }
            foreach (var d in windows.Where(IsLicenseCreatedDialog))
                Press(d, XaeDialogs.PlanClick(d, XaeDialogs.IDOK, "OK"), "licence-created popup");
        }
    }

    private void Press(XaeDialog d, ClickPlan plan, string what)
    {
        XaeDialogs.Execute(d, plan);
        Actions.Enqueue($"{what}: {plan.Description}");
    }

    /// <summary>
    /// Issue #230: after a failed activation the XAE can still raise its licence prompt and then
    /// "Neustart TwinCAT System im Run Modus" — seconds after the COM call threw and the watcher stopped.
    /// Nobody answered them, every later call was blocked, and confirming the restart by hand stopped the
    /// local TcSysSrv. After a failure these are therefore always declined, never confirmed.
    /// </summary>
    internal static (ClickPlan Plan, string What)? AfterFailureAnswer(XaeDialog d)
    {
        if (IsTargetFatalErrorBox(d)) return null;
        if (IsRestartDialog(d))
            return (XaeDialogs.PlanClick(d, XaeDialogs.IDCANCEL, "Abbrechen", "Cancel"), "restart prompt after the failure (cancelled)");
        if (IsSecurityCodeDialog(d))
            return (XaeDialogs.PlanClick(d, XaeDialogs.IDCANCEL, "Abbrechen", "Cancel"), "security-code dialog after the failure (cancelled)");
        if (IsTrialLicensePrompt(d))
            return (XaeDialogs.PlanClick(d, IDNO, "Nein", "No"), "trial-licence prompt after the failure (declined)");
        return null;
    }

    /// <summary>
    /// Polls this XAE's windows after a failed activation and declines the late dialogs
    /// (<see cref="AfterFailureAnswer"/>). Ends after <paramref name="quiet"/> without such a dialog,
    /// at the latest after <paramref name="max"/>. Returns what it pressed.
    /// </summary>
    internal static List<string> SweepAfterFailure(XaeWindowScope scope, TimeSpan quiet, TimeSpan max)
    {
        var actions = new List<string>();
        var started = DateTime.UtcNow;
        var lastSeen = started;
        while (DateTime.UtcNow - started < max && DateTime.UtcNow - lastSeen < quiet)
        {
            try
            {
                foreach (var d in XaeDialogs.Snapshot(scope))
                {
                    if (AfterFailureAnswer(d) is not { } answer) continue;
                    XaeDialogs.Execute(d, answer.Plan);
                    actions.Add($"{answer.What}: {answer.Plan.Description}");
                    lastSeen = DateTime.UtcNow;
                }
            }
            catch (Exception) { /* never let the sweep hide the original failure */ }
            Thread.Sleep(150);
        }
        return actions;
    }

    /// <summary>
    /// True for a dialog this watcher answers itself with its configuration. The activation wait
    /// (issue #195) does not count such a dialog as the outcome of the activation.
    /// </summary>
    internal bool Answers(XaeDialog d) => Answers(_cfg, d);

    /// <summary>Pure form of <see cref="Answers(XaeDialog)"/>; mirrors what <see cref="Tick"/> presses.</summary>
    internal static bool Answers(WatcherConfig cfg, XaeDialog d) =>
        (cfg.AutoConfirmRestart && IsRestartDialog(d))
        || IsTrialLicensePrompt(d)
        || (cfg.AutoConfirmLogin && (IsAppChangedLoginModal(d) || IsReplaceRunningAppModal(d)))
        || (cfg.AutoGenerateLicenses && (IsSecurityCodeDialog(d) || IsLicenseCreatedDialog(d)));

    // ---- Pure matchers (unit-tested with synthetic window lists) ------------

    // Issue #195: a box in which the target reports a fatal error is never one of the dialogs this
    // watcher answers, even when its text happens to mention a restart or a licence. Pressing its OK
    // as a "restart confirmation" would hide the failure; the activation wait reports it instead.
    internal static bool IsRestartDialog(XaeDialog d) =>
        d.IsMessageBox && !IsTargetFatalErrorBox(d) && d.Texts.Any(t =>
            Contains(t, "Run Modus") || Contains(t, "Run Mode") ||
            Contains(t, "Neustart") || Contains(t, "restart"));

    internal static bool IsSecurityCodeDialog(XaeDialog d) =>
        Contains(d.Title, "Sicherheitscode") || Contains(d.Title, "Security Code");

    internal static bool IsTrialLicensePrompt(XaeDialog d) =>
        !IsSecurityCodeDialog(d) && !IsLicenseCreatedDialog(d) && !IsTargetFatalErrorBox(d) &&
        d.Controls.Any(c => c.IsButton) &&
        (IsTrialLicenseText(d.Title) || d.Controls.Where(c => !c.IsButton).Any(c => IsTrialLicenseText(c.Text)));

    internal static bool IsLicenseCreatedDialog(XaeDialog d) =>
        !IsSecurityCodeDialog(d) && !IsTargetFatalErrorBox(d) &&
        (IsLicenseCreatedText(d.Title) || d.Texts.Any(IsLicenseCreatedText));

    /// <summary>
    /// Issue #195: the box the XAE raises when the target fails after an activation, e.g.
    /// "Zielsystem meldet fatalen Fehler" (German XAE) or "Target system reports fatal error" (English).
    /// Matches the title or any control text. Umlaut-free fragments of XAE's own wording.
    /// </summary>
    internal static bool IsTargetFatalErrorBox(XaeDialog d) =>
        IsTargetFatalErrorText(d.Title) || d.Controls.Any(c => IsTargetFatalErrorText(c.Text));

    internal static bool IsTargetFatalErrorText(string text) =>
        Contains(text, "fatalen Fehler") || Contains(text, "Zielsystem meldet") ||
        Contains(text, "fatal error") || Contains(text, "target system reports") || Contains(text, "target reports");

    internal static bool IsAppChangedLoginModal(XaeDialog d) => IsAppChangedLoginModal(d.Texts);

    internal static bool IsLoginDialog(XaeDialog d) => d.IsMessageBox && IsLoginDialog(d.Texts);

    internal static bool IsFirstDownloadConfirm(XaeDialog d) => d.IsMessageBox && d.Texts.Any(IsFirstDownloadConfirmText);

    internal static bool IsOverwriteRunningConfirm(XaeDialog d) => d.IsMessageBox && d.Texts.Any(IsOverwriteRunningConfirmText);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="text"/> mentions a trial-license prompt.
    /// Matches both German ("Testlizenz") and English ("trial license", "Generate trial").
    /// </summary>
    internal static bool IsTrialLicenseText(string text) =>
        text.Contains("trial license",  StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Testlizenz",     StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Generate trial", StringComparison.OrdinalIgnoreCase);

    // Returns true when text matches the "Activate 7-day trial license" button
    // label in German ("Aktiviere 7 Tage Testlizenz") or English variants.
    internal static bool IsTrialLicenseButtonText(string text) =>
        text.Contains("7 Tage Testlizenz",  StringComparison.OrdinalIgnoreCase) ||
        text.Contains("7 Days Trial",        StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Trial License",       StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Testlizenz aktivier", StringComparison.OrdinalIgnoreCase);

    // Captions that are 5 alphanumerics but never a security code (C-18).
    private static readonly HashSet<string> NotACode = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hilfe", "Close", "Enter", "Abort", "Apply", "Reset", "Start", "Retry", "Weiter", "Code:", "Codes",
    };

    /// <summary>
    /// Extracts a 5-character alphanumeric security code from a flat list of
    /// child window texts.  Accepts mixed case (TC3 4026.22 shows codes like
    /// "bW5Rm").  Returns null if no match is found.
    /// </summary>
    internal static string? ExtractSecurityCode(IEnumerable<string> childTexts)
    {
        foreach (string raw in childTexts)
        {
            string t = raw.Trim();
            if (t.Length == 5 && t.All(char.IsAsciiLetterOrDigit) && !NotACode.Contains(t))
                return t;
        }
        return null;
    }

    /// <summary>
    /// C-18: the code comes only from a label/display control — never from a button caption, the
    /// dialog title or the input field we type into.
    /// </summary>
    internal static string? ExtractSecurityCode(XaeDialog d)
    {
        var input = SecurityCodeInput(d);
        return ExtractSecurityCode(d.Controls
            .Where(c => !c.IsButton && c.Hwnd != input?.Hwnd && !string.Equals(c.Text.Trim(), d.Title.Trim(), StringComparison.Ordinal))
            .Select(c => c.Text));
    }

    // The security-code dialog has two Edit controls:
    //   [0] pre-filled with the 5-char code to read (display / read-only)
    //   [1] the actual input field the user (or we) must type into
    // Returns the second Edit (input field), or the first if only one exists.
    internal static XaeControl? SecurityCodeInput(XaeDialog d)
    {
        var edits = d.Controls.Where(c => c.IsEdit).ToList();
        return edits.Count >= 2 ? edits[1] : edits.FirstOrDefault();
    }

    /// <summary>
    /// Returns true when <paramref name="text"/> looks like the post-activation
    /// "license file created" success popup that follows the security-code
    /// dialog. Matches the German wording ("Lizenzdatei erzeugt"), the English
    /// equivalent, and any literal that references a .tclrs file.
    /// </summary>
    internal static bool IsLicenseCreatedText(string text) =>
        text.Contains("Lizenzdatei",   StringComparison.OrdinalIgnoreCase) ||
        text.Contains("license file",  StringComparison.OrdinalIgnoreCase) ||
        text.Contains(".tclrs",        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the first child text containing a .tclrs path, stripping the
    /// surrounding quotes XAE wraps the path in. Returns null when no path
    /// text is present.
    /// </summary>
    internal static string? ExtractLicenseFilePath(IEnumerable<string> childTexts)
    {
        foreach (string raw in childTexts)
        {
            string t = raw.Trim();
            if (t.Contains(".tclrs", StringComparison.OrdinalIgnoreCase))
                return t.Trim('\'', '"', ' ');
        }
        return null;
    }

    // ---- Security-code dialog ------------------------------------------------

    internal sealed record SecurityCodeAnswer(bool Submitted, string? ScreenshotPath, string Note);

    /// <summary>
    /// Answers one security-code dialog: types <paramref name="code"/> (or the code read from the
    /// dialog) and presses OK, verified by the dialog closing. When no code is available the dialog
    /// is captured ONCE per HWND (C-13) and — with <paramref name="cancelIfUnreadable"/> — cancelled,
    /// so a blocking ActivateConfiguration() returns instead of hanging (C-6).
    /// </summary>
    private static SecurityCodeAnswer AnswerSecurityCodeDialog(
        XaeDialog d, string? code, ISet<IntPtr> captured, bool cancelIfUnreadable)
    {
        code ??= ExtractSecurityCode(d);
        var input = SecurityCodeInput(d);
        if (code is not null && input is not null)
        {
            // Set instantly via WM_SETTEXT (correct Unicode marshaling, no
            // artificial delay) — this is the only production path.
            XaeDialogs.SetText(input.Hwnd, code);
            XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDOK, "OK"));
            bool accepted = XaeDialogs.WaitClosed(d.Hwnd, TimeSpan.FromSeconds(3));
            return new SecurityCodeAnswer(accepted, null, accepted
                ? $"code '{code}' entered and OK pressed"
                : $"code '{code}' entered but the dialog did not close on OK");
        }

        string? shot = captured.Add(d.Hwnd) ? TryCaptureWindowAsPng(d.Hwnd) : null;
        string why = input is null ? "no input field found" : "the code is not machine-readable (drawn, not a control text)";
        if (!cancelIfUnreadable)
            return new SecurityCodeAnswer(false, shot, $"security-code dialog open but {why}");

        XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDCANCEL, "Abbrechen", "Cancel"));
        bool closed = XaeDialogs.WaitClosed(d.Hwnd, TimeSpan.FromSeconds(2));
        if (!closed)
        {
            XaeDialogs.Close(d.Hwnd);
            closed = XaeDialogs.WaitClosed(d.Hwnd, TimeSpan.FromSeconds(2));
        }
        string outcome = closed ? "security-code dialog cancelled" : "security-code dialog could NOT be cancelled — close it in the XAE";
        return new SecurityCodeAnswer(false, shot,
            $"{outcome} — {why}" + (shot is null ? "" : $"; screenshot: {shot}"));
    }

    // Interactive dismiss used by the WinForms GUI button and the MCP
    // twincat_dismiss_security_dialog tool. Polls up to <paramref name="timeoutMs"/>
    // milliseconds so the caller does not lose a race against the dialog
    // appearing slightly after the click. Returns:
    //   - Dismissed=true                          — code typed and OK clicked (dialog closed)
    //   - Dismissed=false, ScreenshotPath != null — dialog found but code
    //                                                 unreadable; image saved ONCE
    //   - Dismissed=false, ScreenshotPath == null — dialog never appeared
    internal static (bool Dismissed, string? ScreenshotPath, string? CreatedFilePath)
        DismissSecurityCodeDialogWithWait(int timeoutMs = 3000)
    {
        var scope = XaeWindowScope.For(IntPtr.Zero);
        var captured = new HashSet<IntPtr>();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        string? screenshot = null;
        do
        {
            var dlg = XaeDialogs.Find(scope, IsSecurityCodeDialog).FirstOrDefault();
            if (dlg is not null)
            {
                var r = AnswerSecurityCodeDialog(dlg, code: null, captured, cancelIfUnreadable: false);
                if (r.Submitted) return (true, null, WaitForLicenseCreatedAndDismiss(scope));
                screenshot ??= r.ScreenshotPath;
                if (screenshot is not null) break; // unreadable — polling on only re-reads the same dialog
            }
            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);
        return (false, screenshot, null);
    }

    // Standalone license generation: click "Ja" on the visible trial-license
    // dialog, then wait for the security-code dialog and snapshot it.
    // Returns (Message, ScreenshotPng) — the PNG is always captured so the
    // MCP client can read the code from the embedded image. The code is
    // never auto-typed here; call TrySubmitSecurityCode after reading the code.
    internal static (string Message, byte[]? ScreenshotPng) TryGenerateTrialLicenses()
    {
        var scope = XaeWindowScope.For(IntPtr.Zero);
        if (scope.XaePids.Count == 0)
            return ("[Error] No running TwinCAT XAE (TcXaeShell) found — nothing to answer.", null);

        // Step 1 — find the trial-license prompt (XAE windows only) and press "Ja".
        var prompt = XaeDialogs.Find(scope, IsTrialLicensePrompt).FirstOrDefault();
        string step1;
        if (prompt is not null)
        {
            var plan = XaeDialogs.PlanClick(prompt, XaeDialogs.IDYES, "Ja", "Yes");
            XaeDialogs.Execute(prompt, plan);
            if (!XaeDialogs.WaitClosed(prompt.Hwnd, TimeSpan.FromSeconds(3)))
                return ($"[Error] Trial-license prompt '{prompt.Title}' did not close after {plan.Description} — answer it in the XAE.", null);
            step1 = $"Trial-license prompt answered ({plan.Description}). ";
        }
        else if (XaeDialogs.Find(scope, IsSecurityCodeDialog).Count == 0)
        {
            return ("No trial-license dialog found on screen. " +
                    "Call twincat_open_license_manager_and_request_trial first, " +
                    "or ensure the 'Some required runtime licenses missing' dialog is visible.", null);
        }
        else step1 = "";

        // Step 2 — wait for "Sicherheitscode eingeben" and snapshot it.
        // Never auto-type; the MCP client reads the embedded image and calls
        // twincat_submit_security_code with the code.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var sec = XaeDialogs.Find(scope, IsSecurityCodeDialog).FirstOrDefault();
            if (sec is null) { Thread.Sleep(200); continue; }

            byte[]? png = TryCaptureWindowAsPngBytes(sec.Hwnd);
            return png is not null
                ? (step1 + "Security code dialog is open. Read the 5-character code from the image and call twincat_submit_security_code(code).", png)
                : (step1 + "Security code dialog is open but screenshot failed. Read the code in the XAE and call twincat_submit_security_code(code).", null);
        }

        return ($"[Error] {step1}The security-code dialog did not appear within 10 seconds.", null);
    }

    // Tries to click the "Aktiviere 7 Tage Testlizenz" / "7 Days Trial License" button
    // embedded in the TwinCAT XAE License Manager document tab. Searches the child
    // windows of the XAE's own windows only (Win32 Button controls — WPF buttons
    // without HWNDs will not be found by this method). Returns true when clicked.
    internal static bool TryClickTrialLicenseButton()
    {
        var scope = XaeWindowScope.For(IntPtr.Zero);
        foreach (var w in XaeDialogs.Snapshot(scope, includeMainWindow: true))
        {
            var btn = w.Controls.FirstOrDefault(c => c.IsButton && IsTrialLicenseButtonText(c.Caption));
            if (btn is null) continue;
            XaeDialogs.Click(btn.Hwnd);
            return true;
        }
        return false;
    }

    // Enter an explicit code string into the open security-code dialog and
    // then auto-confirm the follow-up "Lizenzdatei erzeugt" success popup.
    // Returns (true, createdFilePath?) only when the security dialog was found, the
    // code submitted and the dialog CLOSED on OK. The file path is populated when the
    // success popup appeared within the wait window and exposed a .tclrs path.
    internal static (bool Dismissed, string? CreatedFilePath, string Note) TrySubmitSecurityCode(string code)
    {
        var scope = XaeWindowScope.For(IntPtr.Zero);
        var dlg = XaeDialogs.Find(scope, IsSecurityCodeDialog).FirstOrDefault();
        if (dlg is null) return (false, null, "No 'Sicherheitscode eingeben' dialog found — nothing submitted.");

        var r = AnswerSecurityCodeDialog(dlg, code, new HashSet<IntPtr>(), cancelIfUnreadable: false);
        if (!r.Submitted) return (false, null, r.Note);
        // REQUIRED settle delay — do NOT remove. After the security code is
        // submitted, XAE tears down the input dialog and only then swaps in the
        // "Lizenzdatei erzeugt" popup. Without this pause the poll below races
        // the swap and can miss the popup entirely.
        Thread.Sleep(1000);
        return (true, WaitForLicenseCreatedAndDismiss(scope), r.Note);
    }

    // Polls the XAE's windows up to <paramref name="timeoutMs"/> ms for a
    // "Lizenzdatei erzeugt" success popup, extracts the .tclrs path, presses OK
    // (BM_CLICK on the button; WM_COMMAND IDOK only without one) and returns the
    // path (or null when no such dialog appeared).
    internal static string? WaitForLicenseCreatedAndDismiss(XaeWindowScope scope, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        do
        {
            var dlg = XaeDialogs.Find(scope, IsLicenseCreatedDialog).FirstOrDefault();
            if (dlg is not null)
            {
                XaeDialogs.Execute(dlg, XaeDialogs.PlanClick(dlg, XaeDialogs.IDOK, "OK"));
                return ExtractLicenseFilePath(dlg.Texts);
            }
            if (timeoutMs == 0) break;
            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);
        return null;
    }

    // Detection-only (no dismissal): is one of the known XAE activation modal
    // dialogs currently open? Used by the recovery path to prove an
    // unreachable-DTE shell is alive-but-blocked rather than a zombie, so it
    // must NOT be force-closed (issue #57 Q1). XAE windows only (issue #150).
    internal static bool TryFindKnownModalDialog(out string? title)
    {
        // A target fatal-error box (issue #195) proves a live-but-blocked shell just as well.
        var d = XaeDialogs.Find(XaeWindowScope.For(IntPtr.Zero), w =>
            IsSecurityCodeDialog(w) || IsTrialLicensePrompt(w) || IsRestartDialog(w) || IsTargetFatalErrorBox(w)).FirstOrDefault();
        title = d is null ? null : string.IsNullOrWhiteSpace(d.Title) ? "(TwinCAT modal dialog)" : d.Title;
        return d is not null;
    }

    /// <summary>
    /// The XAE window a running or rejected COM call waits for: a message box of a TcXaeShell or
    /// a window of any class owned by a disabled XAE window (issue #149 — the orphaned Go-To
    /// dialog is a WindowsForms window, not a #32770). "title" or "title: text | text".
    /// DETECTION ONLY — answering it is the engineer's decision.
    /// </summary>
    internal static string? TryFindXaeMessageBox() => XaeModalProbe.FindBlockingDialog();

    // ---- screenshots ------------------------------------------------------------

    // Renders a window to a Bitmap. Prefers PrintWindow (captures the window's
    // own content even when occluded / in the background); falls back to
    // CopyFromScreen if PrintWindow fails or yields a blank frame. Caller owns
    // the returned Bitmap. Returns null on failure.
    private static System.Drawing.Bitmap? CaptureWindowBitmap(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return null;
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        var bmp = new System.Drawing.Bitmap(w, h);
        try
        {
            bool ok = false;
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            // PrintWindow can succeed yet leave the bitmap all-black for some
            // composited controls — fall back to a screen BitBlt in that case.
            if (!ok || IsBitmapBlank(bmp))
            {
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
            }
            return bmp;
        }
        catch (Exception) { bmp.Dispose(); return null; }
    }

    // Cheap blank-frame check: samples a few pixels; true if all are pure black.
    private static bool IsBitmapBlank(System.Drawing.Bitmap bmp)
    {
        try
        {
            int[] xs = { bmp.Width / 4, bmp.Width / 2, (bmp.Width * 3) / 4 };
            int[] ys = { bmp.Height / 4, bmp.Height / 2, (bmp.Height * 3) / 4 };
            foreach (int x in xs)
                foreach (int y in ys)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R != 0 || p.G != 0 || p.B != 0) return false;
                }
            return true;
        }
        catch (Exception) { return false; }
    }

    // Captures a window as a PNG byte array (in-memory). Returns null on failure.
    internal static byte[]? TryCaptureWindowAsPngBytes(IntPtr hwnd)
    {
        try
        {
            using var bmp = CaptureWindowBitmap(hwnd);
            if (bmp is null) return null;
            using var ms = new System.IO.MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch (Exception) { return null; }
    }

    // Captures a window as a PNG file in %TEMP% and returns the path, or null on failure.
    internal static string? TryCaptureWindowAsPng(IntPtr hwnd)
    {
        try
        {
            using var bmp = CaptureWindowBitmap(hwnd);
            if (bmp is null) return null;

            string path = Path.Combine(
                Path.GetTempPath(),
                $"tc_security_code_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception) { return null; }
    }

    // ---- foreground: only on request ---------------------------------------
    //
    // The MCP does not bring the XAE to the foreground on its own: it jumped in front of the engineer's work on every
    // open, go-to and system-node selection (decision 2026-11-05). Only a tool called with bringToFront=true — a
    // person asked where something is — raises it (BringXaeToFront). Keystroke fallbacks that need the XAE in front
    // (Enter to open a system node, Ctrl+G) otherwise run only when it already is, and say so when it is not.

    /// <summary>
    /// Raises the XAE main window <paramref name="xaeMain"/> (restores it when minimized). Only for a call with
    /// bringToFront=true. Returns true only when that window IS the foreground window afterwards; Windows may
    /// refuse the foreground change — then false, never an exception.
    /// </summary>
    internal static bool BringXaeToFront(IntPtr xaeMain)
    {
        try
        {
            IntPtr h = xaeMain != IntPtr.Zero ? xaeMain : XaeWindowScope.For(IntPtr.Zero).XaeMain;
            if (h == IntPtr.Zero) return false;
            if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
            _ = SetForegroundWindow(h);
            return IsForeground(h);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// True when the XAE main window <paramref name="xaeMain"/> — the attached DTE's window, not "some
    /// TcXaeShell" (C-23) — is the OS foreground window right now. Without a handle the single running TcXaeShell
    /// is used. Never changes the foreground.
    /// </summary>
    internal static bool IsXaeForeground(IntPtr xaeMain)
    {
        try
        {
            IntPtr h = xaeMain != IntPtr.Zero ? xaeMain : XaeWindowScope.For(IntPtr.Zero).XaeMain;
            return IsForeground(h);
        }
        catch (Exception) { return false; }
    }

    /// <summary>True when <paramref name="hwnd"/> is the OS foreground window right now.</summary>
    internal static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    // ---- hmi-server: the TE2000 windows that block the HMI engineering server ----
    // DETECTION ONLY. The "Activate Engineering Server Trial License" window carries a CAPTCHA;
    // that is the engineer's job and this code never types into it. The HMI tools use the title to
    // turn an opaque server failure into "confirm the license dialog in the XAE".
    //
    // Scope matters (#150, C-1…C-4): only windows of the attached TcXaeShell are looked at. The
    // first version of this walked EVERY visible top-level window, which would also match this
    // host's own GUI — its log shows XAE-like texts.

    internal static bool IsHmiEngineeringLicenseTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && Contains(title, "Engineering Server")
        && (Contains(title, "License") || Contains(title, "Lizenz"));

    internal static string? TryFindHmiEngineeringLicenseDialog() =>
        TryFindXaeWindowTitle(IsHmiEngineeringLicenseTitle);

    /// <summary>Title of the first in-scope XAE window whose title matches, or null. Uses the
    /// COM-free scope (every running TcXaeShell, never this process) because the callers are
    /// session methods that have no DTE handle at hand.</summary>
    private static string? TryFindXaeWindowTitle(Func<string?, bool> titleMatches)
    {
        XaeWindowScope scope = XaeWindowScope.For(IntPtr.Zero);
        if (scope.IsEmpty) return null;
        foreach (XaeDialog d in XaeDialogs.Find(scope, w => titleMatches(w.Title)))
            return d.Title;
        return null;
    }

    // hmi-views: opening an older HMI project raises a #32770 message box
    // "TwinCAT HMI: Project Migration — This project was last opened with version
    // 1.12.754.4. Do you want to migrate it to … 14.17.22.1?" (seen live 2026-09-25).
    // It blocks the load until answered; declining leaves the project unloaded.
    // DETECTION ONLY — migrating is a project decision for the engineer. Same scope as the
    // license dialog (#150): only windows of a running TcXaeShell, never this host.
    internal static bool IsHmiMigrationText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Contains(text, "TwinCAT HMI") && Contains(text, "Migration");

    internal static string? TryFindHmiMigrationDialog()
    {
        XaeWindowScope scope = XaeWindowScope.For(IntPtr.Zero);
        if (scope.IsEmpty) return null;
        foreach (XaeDialog d in XaeDialogs.Find(scope, w => w.IsMessageBox && w.Texts.Any(IsHmiMigrationText)))
        {
            string text = d.Texts.First(IsHmiMigrationText);
            return string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
        }
        return null;
    }

    /// <summary>
    /// Returns true when the text list looks like the 3-way PLC login dialog
    /// ("Login with Online Change / Download / without changes").
    /// Matches German and English dialog wording.
    /// </summary>
    internal static bool IsLoginDialog(IEnumerable<string> childTexts) =>
        childTexts.Any(t =>
            t.Contains("Online-Change",      StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Online Change",      StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Einloggen mit",      StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Login with",         StringComparison.OrdinalIgnoreCase) ||
            t.Contains("ohne Änderung",      StringComparison.OrdinalIgnoreCase) ||
            t.Contains("without changes",    StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the button caption(s) to click for a given <paramref name="mode"/>
    /// on the 3-way login dialog. Returns candidates for German (primary) and
    /// English (fallback) — empirically verify captions on the live German XAE
    /// before hardcoding; the current set covers the most likely wording.
    /// </summary>
    internal static string[] LoginDialogButtonLabels(LoginMode mode) => mode switch
    {
        LoginMode.OnlineChange       => ["Mit Online-Change einloggen", "Login with Online Change", "Online Change"],
        LoginMode.Download           => ["Einloggen mit Download",      "Login with download",      "Download"],
        LoginMode.LoginWithoutChange => ["Einloggen ohne Änderung",     "Login without changes",    "Without changes"],
        _ => []
    };

    // ---- app-changed login modal (issue #136) -------------------------------
    //
    // Re-activating a project whose PLC code changed since the last download
    // raises a modal: "TwinCAT PLC Control — Die Applikation wurde seit dem
    // letzten Download geändert. Was möchten Sie tun?" with three RADIO buttons
    // (Mit Online-Change einloggen / Mit Download einloggen / Ohne Änderung
    // einloggen), a "Bootapplikation aktualisieren" checkbox, and an OK button.
    // autoConfirmRestart does NOT dismiss this one — without handling it the
    // changed code never reaches the runtime and activate_config falsely
    // reports success.

    /// <summary>
    /// Returns true when the child texts look like the "application changed since
    /// last download" login modal. Matches German (primary) and English wording.
    /// </summary>
    internal static bool IsAppChangedLoginModal(IEnumerable<string> childTexts) =>
        childTexts.Any(t =>
            (t.Contains("seit dem letzten Download", StringComparison.OrdinalIgnoreCase) &&
             t.Contains("geändert",                  StringComparison.OrdinalIgnoreCase)) ||
            (t.Contains("changed since",             StringComparison.OrdinalIgnoreCase) &&
             t.Contains("last download",             StringComparison.OrdinalIgnoreCase)) ||
             t.Contains("Was möchten Sie tun",       StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Distinctive substrings of the radio caption to select for a given mode on
    /// the app-changed login modal. Matched case-insensitively via Contains (so
    /// accelerator '&amp;' prefixes and caption tweaks do not break selection).
    /// </summary>
    internal static string[] AppChangedRadioNeedles(LoginMode mode) => mode switch
    {
        LoginMode.OnlineChange       => ["Online-Change", "Online Change"],
        LoginMode.Download           => ["Download"],
        LoginMode.LoginWithoutChange => ["ohne Änderung", "without change"],
        _ => []
    };

    // Substrings of the "update boot project" checkbox caption.
    private static readonly string[] BootProjectCheckboxNeedles =
        ["Bootapplikation", "Bootprojekt", "boot project", "boot application"];

    /// <summary>
    /// Maps a login mode to the wire string reported in ActivateResult.LoginModalHandled.
    /// </summary>
    internal static string LoginModeWire(LoginMode mode) => mode switch
    {
        LoginMode.Download           => "download",
        LoginMode.LoginWithoutChange => "login_without_change",
        _                            => "online_change",
    };

    /// <summary>Wire value reported when online change was not possible and no fallback was allowed.</summary>
    internal const string RefusedOnlineChange = "refused: online change not possible (allowDownloadFallback=false) — modal cancelled";

    /// <summary>
    /// C-17: which login mode to confirm on the app-changed modal. Online change only falls back to
    /// a full download (which stops a running PLC) when the caller explicitly allowed it; otherwise
    /// null = refuse (the modal is cancelled and the caller reports it).
    /// </summary>
    internal static LoginMode? DecideLoginMode(LoginMode requested, bool onlineChangeAvailable, bool allowDownloadFallback)
    {
        if (requested != LoginMode.OnlineChange || onlineChangeAvailable) return requested;
        return allowDownloadFallback ? LoginMode.Download : null;
    }

    /// <summary>
    /// Handles the app-changed login modal: selects the radio for the decided mode (see
    /// <see cref="DecideLoginMode"/>), optionally ticks "Bootapplikation aktualisieren" and presses
    /// OK — or presses Cancel when the decision is a refusal. Returns the wire string.
    /// </summary>
    private static string HandleAppChangedLoginModal(XaeDialog d, LoginMode mode, bool updateBootProject, bool allowDownloadFallback)
    {
        var oc = XaeDialogs.FindButtonContaining(d, AppChangedRadioNeedles(LoginMode.OnlineChange));
        LoginMode? effective = DecideLoginMode(mode, oc is not null && oc.Enabled, allowDownloadFallback);
        if (effective is null)
        {
            XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDCANCEL, "Abbrechen", "Cancel"));
            if (!XaeDialogs.WaitClosed(d.Hwnd, TimeSpan.FromSeconds(2))) XaeDialogs.Close(d.Hwnd);
            return RefusedOnlineChange;
        }

        var radio = XaeDialogs.FindButtonContaining(d, AppChangedRadioNeedles(effective.Value));
        if (radio is null)
        {
            // Captions differ from expectation — never guess: cancel, report, and keep XAE usable.
            XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDCANCEL, "Abbrechen", "Cancel"));
            return $"cancelled: no '{LoginModeWire(effective.Value)}' option found on the modal";
        }

        // Synchronous click so the radio's state (and any checkbox it
        // enables) is settled before we read/tick the checkbox and click OK.
        XaeDialogs.Click(radio.Hwnd);
        if (effective == LoginMode.Download && updateBootProject)
        {
            var chk = XaeDialogs.FindButtonContaining(d, BootProjectCheckboxNeedles);
            if (chk is not null && !XaeDialogs.IsChecked(chk.Hwnd)) XaeDialogs.Click(chk.Hwnd);
        }

        XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDOK, "OK"));
        return LoginModeWire(effective.Value);
    }

    /// <summary>
    /// Returns true when the text matches the first-download confirmation dialog
    /// ("No application exists on the device — create and download?").
    /// </summary>
    internal static bool IsFirstDownloadConfirmText(string text) =>
        text.Contains("Applikation existiert nicht",  StringComparison.OrdinalIgnoreCase) ||
        text.Contains("application does not exist",   StringComparison.OrdinalIgnoreCase) ||
        text.Contains("keine Applikation",            StringComparison.OrdinalIgnoreCase) ||
        text.Contains("erstellen und laden",          StringComparison.OrdinalIgnoreCase) ||
        text.Contains("create and download",          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Issue #204: login against a runtime whose running application has no matching compile information
    /// ("Eine Applikation 'Port_851' läuft gerade auf der SPS. Da es keine passende Übersetzungsinformation gibt,
    /// muss diese vorhandene Applikation ersetzt werden …"). A WinForms "TwinCAT PLC Control" window with
    /// Ja/Nein, a "Bootapplikation aktualisieren" checkbox and "Details...", so it is no #32770 message box.
    /// Answering it "Ja" is a full download that replaces the running application.
    /// </summary>
    internal static bool IsReplaceRunningAppText(string text) =>
        (text.Contains("läuft gerade", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("ersetzt werden", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("Übersetzungsinformation", StringComparison.OrdinalIgnoreCase))) ||
        (text.Contains("currently running", StringComparison.OrdinalIgnoreCase) &&
         (text.Contains("must be replaced", StringComparison.OrdinalIgnoreCase) ||
          text.Contains("compile information", StringComparison.OrdinalIgnoreCase)));

    internal static bool IsReplaceRunningAppModal(XaeDialog d) =>
        !IsTargetFatalErrorBox(d) && d.Controls.Any(c => c.IsButton) &&
        (IsReplaceRunningAppText(d.Title) || d.Texts.Any(IsReplaceRunningAppText));

    /// <summary>Wire value when the replace-running-application box was answered "Nein".</summary>
    internal const string RefusedReplaceRunningApp =
        "refused: the running application has no matching compile information and must be replaced by a full download " +
        "(mode is not download and allowDownloadFallback=false) — answered Nein";

    /// <summary>
    /// Issue #204: "Ja" only when the login really is a download (<see cref="MayConfirmDownload"/>); the
    /// "Bootapplikation aktualisieren" checkbox is set to <paramref name="updateBootProject"/> first.
    /// Otherwise "Nein". Returns the wire string.
    /// </summary>
    internal static string HandleReplaceRunningAppModal(XaeDialog d, LoginMode mode, bool allowDownloadFallback, bool updateBootProject)
    {
        if (!MayConfirmDownload(mode, allowDownloadFallback))
        {
            XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, IDNO, "Nein", "No"));
            return RefusedReplaceRunningApp;
        }
        var chk = XaeDialogs.FindButtonContaining(d, BootProjectCheckboxNeedles);
        if (chk is not null && XaeDialogs.IsChecked(chk.Hwnd) != updateBootProject) XaeDialogs.Click(chk.Hwnd);
        XaeDialogs.Execute(d, XaeDialogs.PlanClick(d, XaeDialogs.IDYES, "Ja", "Yes"));
        return $"download: running application replaced (Ja; update boot application: {(updateBootProject ? "yes" : "no")})";
    }

    /// <summary>
    /// Returns true when the text matches the "overwrite running program?" confirmation.
    /// </summary>
    internal static bool IsOverwriteRunningConfirmText(string text) =>
        text.Contains("laufende Applikation",         StringComparison.OrdinalIgnoreCase) ||
        text.Contains("running application",          StringComparison.OrdinalIgnoreCase) ||
        text.Contains("überschreiben",                StringComparison.OrdinalIgnoreCase) ||
        text.Contains("overwrite",                    StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// C-3: a download confirmation ("create and download?" / "overwrite running application?") is
    /// only answered "Ja" when the login really is a download — requested, or reached through an
    /// explicitly allowed fallback. Every other mode answers "Nein" so a monitoring-only login can
    /// never overwrite a running PLC.
    /// </summary>
    internal static bool MayConfirmDownload(LoginMode requested, bool allowDownloadFallback) =>
        requested == LoginMode.Download || (requested == LoginMode.OnlineChange && allowDownloadFallback);

    // Background login dialog watcher — analogous to the activate-time watcher.
    // Runs on a non-COM background thread; monitors the XAE's windows for:
    //   1. The app-changed modal  → radio for the decided mode (C-17)
    //   2. The 3-way login dialog → clicks the button for the chosen mode
    //   3. First-download confirm / overwrite-running query → Ja only for a download (C-3), else Nein

    internal sealed class LoginDialogWatcher
    {
        private readonly LoginMode _mode;
        private readonly bool _allowDownloadFallback;
        private readonly bool _updateBootProject;
        private readonly XaeWindowScope _scope;
        private readonly HashSet<IntPtr> _answered = new();
        private volatile bool _stop;
        private Thread? _thread;

        internal string DialogHandled { get; private set; } = "none";

        internal LoginDialogWatcher(LoginMode mode, bool allowDownloadFallback = false, IntPtr xaeMain = default,
            bool updateBootProject = true)
        {
            _mode = mode;
            _allowDownloadFallback = allowDownloadFallback;
            _updateBootProject = updateBootProject;
            _scope = XaeWindowScope.For(xaeMain);
        }

        internal void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "LoginDialogWatcher" };
            _thread.Start();
        }

        internal void Stop()
        {
            _stop = true;
            _thread?.Join(TimeSpan.FromSeconds(2));
        }

        private void Loop()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(290);  // #136: must outlive a LATE-appearing modal on a slow/degraded XAE (the blocking DTE/COM call doesn't return until it's dismissed). The watcher is Stop()ed the instant the operation returns, so in the normal fast path this cap is never reached.
            while (!_stop && DateTime.UtcNow < deadline)
            {
                try { Tick(XaeDialogs.Snapshot(_scope)); }
                catch (Exception) { /* never crash the watcher */ }
                Thread.Sleep(150);
            }
        }

        private void Tick(List<XaeDialog> windows)
        {
            foreach (var d in windows)
            {
                // The "Applikation … seit dem letzten Download geändert" modal
                // (WinForms "TwinCAT PLC Control") is what TC raises on login
                // when the project differs from the running app — issue #136.
                // Without handling it, the Login DTE command blocks forever.
                if (IsAppChangedLoginModal(d))
                {
                    DialogHandled = $"app-changed-modal ({HandleAppChangedLoginModal(d, _mode, updateBootProject: true, _allowDownloadFallback)})";
                    continue;
                }

                if (IsLoginDialog(d))
                {
                    var plan = XaeDialogs.PlanClick(d, 0, LoginDialogButtonLabels(_mode));
                    if (plan.UsesButton)
                    {
                        XaeDialogs.Click(plan.Button);
                        DialogHandled = $"login-dialog ({_mode})";
                    }
                    else if (_answered.Add(d.Hwnd))
                    {
                        // Captions differ from expectation — snapshot ONCE for diagnosis.
                        _ = TryCaptureWindowAsPng(d.Hwnd);
                        DialogHandled = $"login-dialog ({_mode}): no matching button — left open";
                    }
                    continue;
                }

                if (IsReplaceRunningAppModal(d))
                {
                    if (_answered.Add(d.Hwnd))
                        DialogHandled = $"replace-running-app modal ({HandleReplaceRunningAppModal(d, _mode, _allowDownloadFallback, _updateBootProject)})";
                    continue;
                }

                if (IsFirstDownloadConfirm(d) || IsOverwriteRunningConfirm(d))
                {
                    bool yes = MayConfirmDownload(_mode, _allowDownloadFallback);
                    var plan = yes
                        ? XaeDialogs.PlanClick(d, XaeDialogs.IDYES, "Ja", "Yes")
                        : XaeDialogs.PlanClick(d, IDNO, "Nein", "No");
                    XaeDialogs.Execute(d, plan);
                    string kind = IsFirstDownloadConfirm(d) ? "first-download confirmation" : "overwrite confirmation";
                    DialogHandled = yes ? $"{kind} (Ja)" : $"{kind} declined — mode {LoginModeWire(_mode)} never downloads";
                }
            }
        }
    }
}
