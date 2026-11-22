using PtcXaeMcp.WinForms.Dialogs;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.Updates;

namespace PtcXaeMcp.WinForms;

// Header menu additions of issue #273: Open sample project, Connect a client…, Settings… and the
// window position that Settings can remember. Built in code (not in the Designer) so the menu
// order lives in one place: WireMenu rebuilds the item list the Designer created.
public partial class Form1
{
    private readonly ToolStripMenuItem _mnuOpenSample = new() { Name = "_mnuOpenSample", Text = "" };
    private readonly ToolStripMenuItem _mnuConnectClient = new() { Name = "_mnuConnectClient", Text = "" };
    private readonly ToolStripMenuItem _mnuSettings = new() { Name = "_mnuSettings", Text = "" };
    private readonly ToolStripMenuItem _mnuWebsite = new() { Name = "_mnuWebsite", Text = "pneutronic.ch" };
    private readonly ToolStripMenuItem _mnuCheckUpdates = new() { Name = "_mnuCheckUpdates", Text = "" };

    /// <summary>Read once at start: HKLM, so only an administrator changes it (then restart the app).</summary>
    private readonly bool _updateCheckDisabledByPolicy = UpdateCheck.IsDisabledByPolicy();

    /// <summary>The result of the last check, so a found release stays one click away in the menu.</summary>
    private UpdateCheckOutcome? _lastUpdateCheck;

    /// <summary>The one GUI-side runner on the shared dispatcher lock (also handed to ApprovalsView).</summary>
    private GuiComDispatchRunner _guiDispatch = null!;

    /// <summary>The server's real diagnostics state, set by AttachMcp (shown in Settings).</summary>
    private bool _diagnosticsEnabled;

    /// <summary>Last non-minimized state, so "maximized, then minimized, then closed" restores maximized.</summary>
    private FormWindowState _lastShownState = FormWindowState.Normal;

    /// <summary>Called from the constructor after <see cref="WireHeader"/>, before the theme is applied.</summary>
    private void WireMenu()
    {
        _menuHeader.Items.Clear();
        _menuHeader.Items.AddRange(new ToolStripItem[]
        {
            _mnuRefreshStatus,
            _mnuOpenSample,
            _mnuConnectClient,
            _mnuSettings,
            new ToolStripSeparator(),
            _mnuForceCloseXae,
            _mnuDismissSecurityDialog,
            new ToolStripSeparator(),
            _mnuOpenLogFolder,
            _mnuToggleTheme,
            new ToolStripSeparator(),
            _mnuCheckUpdates,
            _mnuWebsite,
        });

        _mnuOpenSample.Click += async (_, _) => await OpenSampleProjectFromGuiAsync();
        _mnuConnectClient.Click += (_, _) =>
        {
            using var dialog = new ConnectClientDialog(McpBridge.Port);
            dialog.ShowDialog(this);
        };
        _mnuSettings.Click += (_, _) => ShowSettingsDialog();
        _mnuWebsite.Click += (_, _) => OpenWebsite();
        _mnuCheckUpdates.Click += async (_, _) => await CheckForUpdatesFromGuiAsync(interactive: true);
        // A machine policy or the environment variable forbids the check for every path, the menu too.
        _mnuCheckUpdates.Enabled = !UpdateCheckLocked;
        // The opt-in check at start: after Load (the server is up by then), off the UI thread,
        // and never a dialog: the result goes to the run log, the run-state note and the menu text.
        Load += async (_, _) =>
        {
            if (UpdateCheck.IsEnabled(ThemeManager.Settings.CheckForUpdates, _updateCheckDisabledByPolicy,
                    Environment.GetEnvironmentVariable))
                await CheckForUpdatesFromGuiAsync(interactive: false);
        };

        // After WinForms' auto-scaling (which runs on load): bounds set in the constructor would
        // be scaled again, and the designer size shrank to ~750 px at 96 DPI.
        Load += (_, _) => RestoreWindowPlacement();
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized) _lastShownState = WindowState; };
        FormClosing += (_, _) => SaveWindowPlacement();
    }

    private void ShowSettingsDialog()
    {
        var settings = ThemeManager.Settings;
        var access = _bridge is { } b
            ? new McpAccessSettings(b.Surface.WindowProfile, b.Surface.EnvironmentProfile, b.Policy.Window, b.Policy.Environment)
            : null;
        using var dialog = new SettingsDialog(McpBridge.Port, ThemeManager.Current == ThemeMode.Dark,
            settings.RememberWindowPosition, _diagnosticsEnabled, settings.CheckForUpdates, UpdateCheckLocked, access,
            HostLanguage.Current);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (_bridge is { } bridge)
        {
            settings.ToolProfile = dialog.ToolProfile;
            if (!string.Equals(dialog.ToolProfile, bridge.Surface.WindowProfile, StringComparison.Ordinal))
                bridge.Surface.SetWindowProfile(dialog.ToolProfile);
            var policy = dialog.AccessPolicy;
            if (policy.ToJson() != bridge.Policy.Window.ToJson())
            {
                try { bridge.Policy.SetWindowPolicy(policy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, Loc.Format("main.policy.savefailed", bridge.Policy.FilePath, ex.Message),
                        Loc.Get("main.policy.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        settings.RememberWindowPosition = dialog.RememberWindowPosition;
        // Only a changed choice is saved, so an unchanged list never pins the language the Windows setting gave.
        if (dialog.Language != HostLanguage.Current)
        {
            settings.Language = dialog.Language;
            HostLanguage.Apply(dialog.Language);
        }
        if (!UpdateCheckLocked)
            settings.CheckForUpdates = dialog.CheckForUpdates;
        var mode = dialog.DarkTheme ? ThemeMode.Dark : ThemeMode.Light;
        if (mode != ThemeManager.Current)
            ThemeManager.Set(mode); // saves ui.json, including RememberWindowPosition
        else
            ThemeManager.Save();
    }

    private bool UpdateCheckLocked =>
        _updateCheckDisabledByPolicy || UpdateCheck.IsDisabledByEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Runs the update check (see <see cref="UpdateCheck"/>): one HEAD request to github.com, no
    /// download. The outcome is logged and shown in the run-state note; the menu item then names
    /// the newer release and opens its page. Interactive (menu) also shows a message box and
    /// offers to open the release page. Never throws, never blocks the UI thread.
    /// </summary>
    private async Task CheckForUpdatesFromGuiAsync(bool interactive)
    {
        if (UpdateCheckLocked) return;
        if (_lastUpdateCheck is { Kind: UpdateCheckKind.UpdateAvailable } found && interactive)
        {
            // Already known: the menu item is the link to the release page.
            OpenUrl(found.Url);
            return;
        }

        _mnuCheckUpdates.Enabled = false;
        if (interactive) ShowTransientRunState(Loc.Get("main.runstate.checkingupdates"));
        UpdateCheckOutcome outcome;
        try
        {
            outcome = await Task.Run(() => UpdateCheck.RunAsync(McpBridge.ThisAssemblyVersion()));
        }
        finally
        {
            _mnuCheckUpdates.Enabled = true;
        }
        if (IsDisposed) return;

        _lastUpdateCheck = outcome;
        AppendLog(outcome.LogText);
        if (outcome.Kind == UpdateCheckKind.UpdateAvailable && outcome.Latest is { } latest)
        {
            _mnuCheckUpdates.Text = Loc.Format("menu.updateavailable", latest.ToString(3));
            ShowTransientRunState(Loc.Format("main.runstate.updateavailable", latest.ToString(3)));
        }
        else if (interactive)
        {
            ShowTransientRunState(Loc.Get(outcome.Kind == UpdateCheckKind.UpToDate ? "main.runstate.uptodate" : "main.runstate.updatecheckfailed"));
        }

        if (!interactive) return;
        if (outcome.Kind == UpdateCheckKind.UpdateAvailable)
        {
            if (MessageBox.Show(this, outcome.Message + "\n\n" + Loc.Get("main.updates.openpage"),
                    Loc.Get("main.updates.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                OpenUrl(outcome.Url);
        }
        else
        {
            MessageBox.Show(this, outcome.Message, Loc.Get("main.updates.title"), MessageBoxButtons.OK,
                outcome.Kind == UpdateCheckKind.UpToDate ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    /// <summary>Opens an https URL in the default browser; a failure is a message, never an exception.</summary>
    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, Loc.Format("main.error.openurl", url, ex.Message), Loc.Get("main.openlink.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Menu > Open sample project: copies the shipped neutral fixture to a fresh folder under
    /// Documents and opens that copy (never the shipped one) through the same lifecycle path as
    /// <c>twincat_open_project</c>, under the dispatcher lock. An XAE is launched if none runs.
    /// </summary>
    private async Task OpenSampleProjectFromGuiAsync()
    {
        string source = SampleProject.ShippedDirectory(AppContext.BaseDirectory);
        if (!Directory.Exists(source))
        {
            MessageBox.Show(this, Loc.Format("main.sample.missing", source),
                Loc.Get("main.sample.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _mnuOpenSample.Enabled = false;
        try
        {
            // 1. An open solution would make the open fail: ask first, before anything is copied.
            var openCheck = await _guiDispatch.RunAsync(() =>
            {
                try { return _session.HasOpenSolution() ? "open" : "none"; }
                catch (Exception) { return "none"; } // no XAE yet: the open below launches one
            });
            if (!openCheck.Ok)
            {
                MessageBox.Show(this, openCheck.ErrorMessage, Loc.Get("main.sample.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool closeCurrent = openCheck.Value == "open";
            if (closeCurrent && MessageBox.Show(this,
                    Loc.Get("main.sample.closeconfirm"),
                    Loc.Get("main.sample.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            // 2. Copy outside the dispatcher lock (no COM involved); a failed copy leaves nothing behind.
            ShowTransientRunState(Loc.Get("main.runstate.copyingsample"));
            string target = SampleProject.UniqueTargetDirectory(documents, DateTime.Now);
            string sln;
            try
            {
                sln = await Task.Run(() => SampleProject.CopyTo(source, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (Directory.Exists(target)) Directory.Delete(target, recursive: true); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                MessageBox.Show(this, Loc.Format("main.sample.copyfailed", target, ex.Message),
                    Loc.Get("main.sample.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AppendLog($"Sample project copied to {target}");

            // 3. Open the copy under the lock, same path as twincat_open_project.
            ShowTransientRunState(Loc.Get("main.runstate.openingsample"));
            var outcome = await _guiDispatch.RunAsync(() =>
                new LifecycleCoordinator(_session)
                    .OpenProjectAsync(sln, closeCurrentFirst: closeCurrent, CancellationToken.None)
                    .GetAwaiter().GetResult());

            if (!outcome.Ok)
            {
                ShowTransientRunState(Loc.Get("main.runstate.samplenotopened"));
                MessageBox.Show(this, outcome.ErrorMessage, Loc.Get("main.sample.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string result = outcome.Value ?? string.Empty;
            AppendLog(result);
            bool ok = McpErrors.IsOk(result);
            _host.RequestEnvironmentRefresh("After sample project");
            ShowTransientRunState(Loc.Get(ok ? "main.runstate.sampleopened" : "main.runstate.samplenotopened"));
            if (!ok)
                MessageBox.Show(this, result, Loc.Get("main.sample.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _mnuOpenSample.Enabled = true;
        }
    }

    /// <summary>Applies the position saved in ui.json, clamped to the screens connected now.</summary>
    private void RestoreWindowPlacement()
    {
        var settings = ThemeManager.Settings;
        if (!settings.RememberWindowPosition || settings.Window is not { } w)
        {
            ApplyDefaultWindowSize();
            return;
        }

        var areas = Screen.AllScreens
            .OrderByDescending(s => s.Primary)
            .Select(s => s.WorkingArea)
            .ToList();
        if (UiSettings.ClampToScreens(new Rectangle(w.X, w.Y, w.Width, w.Height), areas, MinimumSize) is not { } bounds)
            return;

        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        if (w.Maximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>First start (or nothing remembered): a comfortable size, centred on the primary screen.</summary>
    private void ApplyDefaultWindowSize()
    {
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        var size = new Size(Math.Min(1200, area.Width * 9 / 10), Math.Min(860, area.Height * 9 / 10));
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2,
            size.Width, size.Height);
    }

    private void SaveWindowPlacement()
    {
        var settings = ThemeManager.Settings;
        if (!settings.RememberWindowPosition) return;

        // The restore bounds are the normal bounds while minimized or maximized.
        Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        bool maximized = WindowState == FormWindowState.Maximized
            || (WindowState == FormWindowState.Minimized && _lastShownState == FormWindowState.Maximized);
        settings.Window = new WindowPlacement(b.X, b.Y, b.Width, b.Height, maximized);
        ThemeManager.Save();
    }
}
