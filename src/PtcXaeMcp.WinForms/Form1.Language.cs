using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.WinForms;

// Host language of the window: every human text the Designer assigns (or code assigns once) is set again here,
// so a language change from the Settings dialog shows at once. Nothing in this file probes the XAE: the status
// chips are re-rendered from the last known state (ChipSpec), never from a new COM walk.
public partial class Form1
{
    /// <summary>
    /// What a XAE/Project chip shows, kept as a key plus arguments (not as finished text) so the chip can be
    /// rendered again in another language. <paramref name="RawTip"/> is a tooltip that is data, not a sentence.
    /// </summary>
    private sealed record ChipSpec(string Key, object?[] Args, string? TipKey, object?[] TipArgs, string? RawTip,
        IndicatorState State)
    {
        public string ChipText => Loc.Format(Key, Args);

        public string ToolTip => TipKey is not null ? Loc.Format(TipKey, TipArgs) : RawTip ?? string.Empty;
    }

    private ChipSpec? _xaeChipSpec;
    private ChipSpec? _projectChipSpec;

    /// <summary>Handler of <see cref="HostLanguage.Changed"/>: any thread, applies on the UI thread.</summary>
    private void OnHostLanguageChanged()
    {
        if (IsDisposed) return;
        if (IsHandleCreated && InvokeRequired)
        {
            try { BeginInvoke(new Action(ApplyLanguageEverywhere)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            return;
        }
        ApplyLanguageEverywhere();
    }

    /// <summary>The views first (the Approvals badge reads their captions), then the form itself.</summary>
    private void ApplyLanguageEverywhere()
    {
        if (IsDisposed) return;
        _activityView.ApplyLanguage();
        _approvalsView.ApplyLanguage();
        _consoleView.ApplyLanguage();
        ApplyLanguage();
    }

    /// <summary>
    /// Sets every static human text of the window again in the host language: header buttons, menu items, tab
    /// pages, tooltips, window title and the status chips (from their cached state).
    /// </summary>
    private void ApplyLanguage()
    {
        if (IsDisposed) return;

        _btnStop.Text = Loc.Get("main.button.stop");
        _btnMenu.Text = Loc.Get("main.button.menu");
        _toolTip.SetToolTip(_btnStop, Loc.Get("main.stop.tooltip"));
        ApplyThemeToggleTexts();

        _mnuRefreshStatus.Text = Loc.Get("menu.refresh");
        _mnuForceCloseXae.Text = Loc.Get("menu.closexae");
        _mnuDismissSecurityDialog.Text = Loc.Get("menu.dismisssecurity");
        _mnuOpenLogFolder.Text = Loc.Get("menu.openlogfolder");
        _mnuToggleTheme.Text = Loc.Get("menu.toggletheme");
        _mnuOpenSample.Text = Loc.Get("menu.opensample");
        _mnuConnectClient.Text = Loc.Get("menu.connectclient");
        _mnuSettings.Text = Loc.Get("menu.settings");
        _mnuCheckUpdates.Text = CheckUpdatesMenuText();

        _tabActivity.Text = Loc.Get("main.tab.activity");
        _tabConsole.Text = Loc.Get("main.tab.console");
        // The Approvals caption carries the pending count and belongs to the view's attention state.
        _tabApprovals.Text = _approvalsView.Attention.TabCaption;
        _toolTip.SetToolTip(_lblApprovalsState, Loc.Get("main.approvals.chip.tooltip"));
        RenderApprovalsBadge(_approvalsView.Attention);
        _tabControl.Invalidate(); // owner-drawn tab strip (ThemeManager)

        ShowAccessLevel();
        RenderStatusChips();
    }

    /// <summary>Theme toggle caption and tooltip name the theme the click switches to.</summary>
    private void ApplyThemeToggleTexts()
    {
        bool dark = ThemeManager.Current == ThemeMode.Dark;
        _btnThemeToggle.Text = Loc.Get(dark ? "main.theme.light" : "main.theme.dark");
        _toolTip.SetToolTip(_btnThemeToggle,
            Loc.Get(dark ? "main.theme.tooltip.light" : "main.theme.tooltip.dark"));
    }

    /// <summary>The menu item names a newer release once the update check found one.</summary>
    private string CheckUpdatesMenuText() =>
        _lastUpdateCheck is { Kind: Updates.UpdateCheckKind.UpdateAvailable, Latest: { } latest }
            ? Loc.Format("menu.updateavailable", latest.ToString(3))
            : Loc.Get("menu.checkupdates");

    /// <summary>XAE, Project and MCP chip again from their last known state (no probe).</summary>
    private void RenderStatusChips()
    {
        if (_xaeChipSpec is { } xae)
            SetChip(_lblXaeState, _lblXaeDot, xae);
        else
            _lblXaeState.Text = Loc.Get("status.xae.notchecked");
        if (_projectChipSpec is { } project)
            SetChip(_lblProjectState, _lblProjectDot, project);
        else
            _lblProjectState.Text = Loc.Get("status.project.notchecked");
        RenderMcpStatus();
    }
}
