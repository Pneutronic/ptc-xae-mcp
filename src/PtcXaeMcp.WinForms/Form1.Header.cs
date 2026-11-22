using System.Diagnostics;
using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms;

// Header actions (issue #270): Stop, the Menu (status refresh, the two recovery actions that
// survived the old Build tab, log folder, theme) and the theme toggle.
public partial class Form1
{
    private void WireHeader()
    {
        _btnThemeToggle.Click += (_, _) => ThemeManager.Toggle();
        _btnStop.Click += (_, _) => StopFromGui();
        // The logo links to the company website.
        _picLogo.Cursor = Cursors.Hand;
        _picLogo.Click += (_, _) => OpenWebsite();
        _toolTip.SetToolTip(_picLogo, ProductInfo.WebsiteUrl);
        _btnMenu.Click += (_, _) => _menuHeader.Show(_btnMenu, new Point(0, _btnMenu.Height));
        _mnuRefreshStatus.Click += (_, _) => RefreshStatusFromGui();
        // Deliberately NOT routed through the dispatcher (matches the MCP-side
        // twincat_dismiss_security_dialog / twincat_force_close_xae exemptions,
        // issue #154): both are recovery actions that must still work while the
        // dispatcher lock is held by whatever they are meant to unblock
        // (ActivateConfig waiting on the security dialog; a hung XAE holding
        // the lock forever).
        _mnuDismissSecurityDialog.Click += (_, _) => DismissSecurityDialogFromGui();
        _mnuForceCloseXae.Click += (_, _) => ForceCloseXaeFromGui();
        _mnuOpenLogFolder.Click += (_, _) => OpenLogFolder();
        _mnuToggleTheme.Click += (_, _) => ThemeManager.Toggle();
    }

    /// <summary>
    /// Menu > Refresh status. The probe is lock-free (#154), so it must not run while a
    /// TwinCAT operation holds the dispatcher: the XAE answers busy and the UI thread would
    /// sit in the COM busy-retry for up to 30 s. Holding the lock for the probe avoids both.
    /// </summary>
    private void RefreshStatusFromGui()
    {
        if (!_host.Dispatcher.TryAcquire())
        {
            ShowTransientRunState(Loc.Get("main.runstate.busy"));
            return;
        }
        try { UpdateEnvironmentStatus("Manual check"); }
        finally { _host.Dispatcher.Release(); }
    }

    /// <summary>Stop = <c>twincat_stop</c>: cancels the running apply, if any.</summary>
    private void StopFromGui()
    {
        bool cancelled = StopActiveProcess();
        ShowTransientRunState(Loc.Get(cancelled ? "main.runstate.cancelrequested" : "main.runstate.nothingtostop"));
    }

    private System.Windows.Forms.Timer? _runStateRestore;
    private string _runStateRestoreText = string.Empty;

    /// <summary>Shows a short note in the run-state label for 4 s, then restores the run state
    /// that was there (unless an apply changed it meanwhile).</summary>
    private void ShowTransientRunState(string note)
    {
        if (_runStateRestore is null)
            _runStateRestoreText = _lblRunState.Text;
        _runStateRestore?.Dispose();
        _lblRunState.Text = note;
        var timer = new System.Windows.Forms.Timer { Interval = 4000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (ReferenceEquals(_runStateRestore, timer)) _runStateRestore = null;
            if (_lblRunState.Text == note) _lblRunState.Text = _runStateRestoreText;
        };
        _runStateRestore = timer;
        timer.Start();
    }

    private void ForceCloseXaeFromGui()
    {
        if (MessageBox.Show(this,
                Loc.Get("main.closexae.confirm"),
                Loc.Get("main.closexae.title"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var result = new RecoveryCoordinator(_session).ForceCloseXae();
        bool ok = McpErrors.IsOk(result);
        AppendLog(result);
        MessageBox.Show(this, result, Loc.Get(ok ? "main.closexae.done" : "main.closexae.failed"),
            MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        UpdateEnvironmentStatus("After XAE recovery");
    }

    private void DismissSecurityDialogFromGui()
    {
        var result = new BuildCoordinator(_session).DismissSecurityDialog();
        bool dismissed = result.StartsWith("Security dialog dismissed", StringComparison.Ordinal);
        AppendLog(result);
        MessageBox.Show(this, result, Loc.Get(dismissed ? "main.security.resolved" : "main.security.notresolved"),
            MessageBoxButtons.OK, dismissed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>Opens <c>%LOCALAPPDATA%\ptc-xae-mcp</c> (host log, activity logs) in Explorer.</summary>
    /// <summary>Opens the company website in the default browser.</summary>
    private void OpenWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProductInfo.WebsiteUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, Loc.Format("main.error.openurl", ProductInfo.WebsiteUrl, ex.Message),
                ProductInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogFolder()
    {
        string dir = HostLog.DefaultDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Format("main.error.openlogfolder", dir, ex.Message),
                Loc.Get("main.openlogfolder.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
