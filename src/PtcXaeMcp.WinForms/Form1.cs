using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.Views;

namespace PtcXaeMcp.WinForms;

// Form1 MUST stay the first type in this file: the SDK names Form1.resx's manifest resource
// after the first type declared in Form1.cs (DependentUpon convention). FieldbusNodeTag used to
// sit above it; SDK 10.0.401 counts records, so the resource became
// "FieldbusNodeTag.resources" and the host crashed at start (MissingManifestResourceException).

/// <summary>
/// The host window (issue #270): a header with status chips, Stop and Menu, and three views —
/// Activity (MCP tool calls), Approvals (IBN Live) and Console (tool console, #271). TwinCAT
/// itself is operated through the MCP client; the window shows what the server and the XAE do.
/// </summary>
public partial class Form1 : Form
{
    /// <summary>
    /// Owns the session, the dispatcher lock and everything else the GUI shares with
    /// the MCP tools (issue #268). The form only reads from it and subscribes to it.
    /// </summary>
    private readonly ServerHost _host;

    private ITwinCatSession _session => _host.Session;

    // Built in CreateViews, after ThemeManager.LoadFromDisk: the views read palette colours
    // in their constructors, and a field initializer would always see the light palette.
    private ActivityView _activityView = null!;
    private ApprovalsView _approvalsView = null!;
    private ConsoleView _consoleView = null!;

    internal Form1(ServerHost host)
    {
        _host = host;
        ThemeManager.LoadFromDisk();
        InitializeComponent();
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        // The installed version must be visible at a glance (window title + header).
        string version = McpBridge.DisplayVersion(McpBridge.ThisAssemblyVersion());
        Text = $"{ProductInfo.DisplayName} {version}";
        _lblProductName.Text = $"{ProductInfo.DisplayName} {version}";
        // The logo itself is set per theme in ApplyTheme (light grey lettering on dark).

        CreateViews();
        // hmi-server: this window asks the engineer (modal popup) before EVERY HMI write.
        _host.AttachHmiConfirmUi(new FormHmiWriteConfirmer(this));

        WireHeader();
        WireMenu();
        ApplyTheme();
        ThemeManager.Changed += ApplyTheme;
        // issue #154, A-6/A-15: the initial check must not run in the constructor — strictly
        // BEFORE Program.cs constructs and starts McpBridge. A slow COM tree walk (see
        // TwinCatEnvironmentProbe) would keep the MCP HTTP port closed for that long, so a
        // client racing the host's startup got ECONNREFUSED. Load only fires once
        // Application.Run's message loop starts, i.e. after the bridge was started.
        Load += (_, _) => UpdateEnvironmentStatus("Initial check");
        _host.EnvironmentRefreshRequested += RefreshEnvironmentStatus;
        _host.ApplyRunStateChanged += OnHostApplyRunStateChanged;
        FormClosed += (_, _) =>
        {
            ThemeManager.Changed -= ApplyTheme;
            _host.EnvironmentRefreshRequested -= RefreshEnvironmentStatus;
            _host.ApplyRunStateChanged -= OnHostApplyRunStateChanged;
            StopActiveProcess();
            // The views stop their own timers and unsubscribe when the form disposes them;
            // the coordinator they read is torn down by ServerHost.Dispose after Application.Run.
            Image? logo = _picLogo.Image;
            _picLogo.Image = null;
            logo?.Dispose();
        };
    }

    /// <summary>
    /// Hosts the three views on their tabs. Built after InitializeComponent (like the former
    /// runtime-built tabs), so the moved IBN Live layout is not re-scaled by the form's
    /// AutoScale pass and looks as it did on its old tab.
    /// </summary>
    private void CreateViews()
    {
        _activityView = new ActivityView { Dock = DockStyle.Fill };
        _approvalsView = new ApprovalsView { Dock = DockStyle.Fill };
        _consoleView = new ConsoleView { Dock = DockStyle.Fill };
        WireConsole();
        _tabActivity.Controls.Add(_activityView);
        _tabApprovals.Controls.Add(_approvalsView);
        _tabConsole.Controls.Add(_consoleView);
        // One shared runner on the dispatcher lock (also used by the menu actions, #273).
        _guiDispatch = new Mcp.GuiComDispatchRunner(_host.Dispatcher);
        AttachApprovals(); // Form1.Approvals.cs (#272)
    }

    /// <summary>
    /// Wires the MCP server's events to the header, the activity view and the
    /// shutdown/crash handling (issue #268: the bridge no longer knows the form).
    /// </summary>
    internal void AttachMcp(McpBridge bridge)
    {
        _diagnosticsEnabled = bridge.DiagnosticsEnabled;
        _activityView.Attach(bridge.Activity, bridge.DiagnosticsEnabled, action => BeginInvokeSafe(action));
        bridge.StatusChanged += SetMcpStatus;
        bridge.Sessions.Changed += () => SetMcpClients(bridge.Sessions.Snapshot());
        bridge.Faulted += message => BeginInvokeSafe(() =>
            MessageBox.Show(message, "MCP Error", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        bridge.ShutdownRequested += () => BeginInvokeSafe(Close);
    }

    private void OnHostApplyRunStateChanged(bool running, string status) =>
        BeginInvokeSafe(() => _lblRunState.Text = status);

    private void ApplyTheme()
    {
        ThemeManager.Apply(this);
        ThemeManager.ApplyMenu(_menuHeader);
        // Names the action (the theme it switches to); the ☼/☾ glyphs were clipped (#274).
        bool dark = ThemeManager.Current == ThemeMode.Dark;
        Image? oldLogo = _picLogo.Image;
        _picLogo.Image = ProductInfo.LoadLogo(forDarkBackground: dark);
        _picLogo.Visible = _picLogo.Image is not null;
        oldLogo?.Dispose();
        _btnThemeToggle.Text = dark ? "Light" : "Dark";
        _toolTip.SetToolTip(_btnThemeToggle, dark ? "Switch to the light theme" : "Switch to the dark theme");
        // Re-apply status dot colors after theme switch. Apply() has already remapped the state
        // labels' semantic ForeColor to the new palette, so the comparison below matches.
        ApplyStatusDot(_lblXaeDot, _lblXaeState.ForeColor);
        ApplyStatusDot(_lblProjectDot, _lblProjectState.ForeColor);
        RenderMcpStatus();
        // Apply OS-level dark scrollbars and title bar (requires a real window handle).
        if (IsHandleCreated)
            NativeDarkMode.ApplyToForm(this, ThemeManager.Current == ThemeMode.Dark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeDarkMode.ApplyToForm(this, ThemeManager.Current == ThemeMode.Dark);
    }

    private static void ApplyStatusDot(Label dot, Color stateColor)
    {
        // Map foreground "state" color back to palette dot color.
        var p = ThemeManager.Palette;
        if (stateColor == p.Ok)
            dot.BackColor = p.Ok;
        else if (stateColor == p.Warn)
            dot.BackColor = p.Warn;
        else if (stateColor == p.Error)
            dot.BackColor = p.Error;
        else
            dot.BackColor = p.Inactive;
    }

    // MCP status chip state. client-detection extends the one-line readout (a single-line
    // summary is proportionate for self-reported, diagnostics-only client identity).
    private bool _mcpActive;
    private int _mcpPort;
    private IReadOnlyList<Mcp.McpClientSession> _mcpClients = [];

    public void SetMcpStatus(bool active, int port = 0)
    {
        if (IsDisposed) return;
        _mcpActive = active;
        _mcpPort = port;
        if (!active) _mcpClients = [];
        BeginInvokeSafe(RenderMcpStatus);
    }

    /// <summary>
    /// Connected-client readout for the MCP status chip. Called by
    /// <see cref="McpSessionRegistry.Changed"/> on a server thread.
    /// </summary>
    public void SetMcpClients(IReadOnlyList<Mcp.McpClientSession> clients)
    {
        if (IsDisposed) return;
        _mcpClients = clients ?? [];
        BeginInvokeSafe(RenderMcpStatus);
    }

    /// <summary>
    /// Cross-thread UI update helper (issue #154, A-12/F-30). Plain
    /// <see cref="Control.BeginInvoke(Delegate)"/> throws
    /// <see cref="InvalidOperationException"/> when the window handle does
    /// not exist yet — which is exactly the state <see cref="SetMcpStatus"/>
    /// can be called in: McpBridge's RunServerAsync calls it right after
    /// <c>await app.StartAsync(ct)</c>, which can complete before
    /// Application.Run(form) has created the form's handle. This helper defers the
    /// action once (via <see cref="Control.HandleCreated"/>) instead of throwing
    /// when the handle isn't there yet, and swallows the disposed/invalid
    /// races BeginInvoke can otherwise still hit during shutdown.
    /// </summary>
    /// <param name="alwaysQueue">Post even when already on the UI thread, so the action
    /// keeps its place behind earlier posts.</param>
    internal void BeginInvokeSafe(Action action, bool alwaysQueue = false)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated)
        {
            void OnHandleCreated(object? s, EventArgs e)
            {
                HandleCreated -= OnHandleCreated;
                BeginInvokeSafe(action, alwaysQueue);
            }
            HandleCreated += OnHandleCreated;
            return;
        }
        try
        {
            // Already on the UI thread (e.g. a direct call from a button
            // handler) — run inline instead of round-tripping through the
            // message queue.
            if (!alwaysQueue && !InvokeRequired) { action(); return; }
            BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void RenderMcpStatus()
    {
        if (IsDisposed) return;
        var p = ThemeManager.Palette;
        if (_mcpActive)
        {
            _lblMcpCaption.Text = $"MCP :{_mcpPort}{FormatClientSuffix(_mcpClients)}";
            _lblMcpDot.BackColor = p.Ok;
        }
        else
        {
            _lblMcpCaption.Text = "MCP: inactive";
            _lblMcpDot.BackColor = p.Inactive;
        }
    }

    private static string FormatClientSuffix(IReadOnlyList<Mcp.McpClientSession> clients)
    {
        if (clients.Count == 0)
            return " — 0 clients";
        // Newest first (registry Snapshot order); name the primary one.
        var primary = clients[0];
        var label = $"{primary.ClientName} {primary.ClientVersion}";
        return clients.Count == 1
            ? $" — 1 client ({label})"
            : $" — {clients.Count} clients ({label} +{clients.Count - 1})";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopActiveProcess();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Probes the XAE, updates the XAE/Project chips and reports the result to
    /// <see cref="ServerHost.ReportEnvironment"/> — in every path, also when the probe or a
    /// UI update throws: <c>twincat_get_status</c> waits for that report after a refresh request.
    /// </summary>
    private void UpdateEnvironmentStatus(string reason)
    {
        string xaeStatusText = "Unknown";
        string projectStatusText = "Not available";
        List<string> plcs = [];
        List<string> masters = [];
        try
        {
            TwinCatEnvironmentStatus env = _session.Probe();
            string projectDisplay = string.IsNullOrWhiteSpace(env.PlcProjectName)
                ? env.ProjectName
                : $"{env.ProjectName} | PLC: {env.PlcProjectName}";
            string chipText = projectDisplay;
            string? hostLine = TryDescribeResolvedHost();
            if (hostLine is not null)
                projectDisplay += $"  ·  {hostLine}";
            xaeStatusText = "Active";
            projectStatusText = projectDisplay;
            SetChip(_lblXaeState, _lblXaeDot, "XAE: Active", "TwinCAT XAE instance: active", IndicatorState.Ok);
            SetChip(_lblProjectState, _lblProjectDot, $"Project: {chipText}", projectDisplay, IndicatorState.Ok);
            plcs.AddRange(env.PlcProjectNames);
            masters.AddRange(env.MasterNames);
        }
        catch (Exception ex)
        {
            // XaeReachability.Describe: "No TwinCAT XAE instance is running." (older builds threw the
            // German text). The process count is the robust signal if the text changes again.
            if (TwinCat.XaeProcessProbe.Count() == 0 ||
                ex.Message.Contains("No TwinCAT XAE instance is running", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Keine laufende TwinCAT XAE Instanz", StringComparison.OrdinalIgnoreCase))
            {
                xaeStatusText = "Not active";
                projectStatusText = "Not available";
                SetChip(_lblXaeState, _lblXaeDot, "XAE: Not active", "No running TwinCAT XAE instance.", IndicatorState.Error);
                SetChip(_lblProjectState, _lblProjectDot, "Project: Not available", "No TwinCAT XAE instance, so no project.", IndicatorState.Error);
                plcs.Add("<not available (XAE inactive)>");
                masters.Add("<not available (XAE inactive)>");
            }
            else
            {
                xaeStatusText = "Active";
                projectStatusText = "Not available";
                SetChip(_lblXaeState, _lblXaeDot, "XAE: Active", "TwinCAT XAE instance: active", IndicatorState.Ok);
                SetChip(_lblProjectState, _lblProjectDot, "Project: Not available", "TwinCAT project probe: " + ex.Message, IndicatorState.Warn);
                AppendLog("[warn] TwinCAT project probe: " + ex.Message);
                plcs.Add("<not available (no TwinCAT project)>");
                masters.Add("<not available (no TwinCAT project)>");
            }
        }
        finally
        {
            // twincat_get_status reads this snapshot instead of the labels (issue #268).
            _host.ReportEnvironment(new HostEnvironmentSnapshot(xaeStatusText, projectStatusText));
        }
        WriteEnvironmentLog(reason, xaeStatusText, projectStatusText, plcs, masters);
    }

    /// <summary>
    /// Read-only host readout for the project status, e.g. "Host: TcXaeShell.DTE.17.0 (64-bit, TcVersion 3.1.4026.22)".
    /// The host is the XAE that is attached (Running Object Table), not the one the TcVersion would select — a
    /// project opened on purpose in the other bitness must not be reported as the TcVersion's host. The TcVersion
    /// is added when the .tsproj is readable. Null when no XAE is registered (the status then shows only the project).
    /// </summary>
    private string? TryDescribeResolvedHost()
    {
        try
        {
            var running = TwinCat.HostSelection.XaeHostReport.RunningHosts();
            if (running.Count == 0)
                return null;

            TwinCat.HostSelection.TcVersion? tcVersion = null;
            var tsproj = TwinCat.HostSelection.TsprojLocator.FindForSolution(_session.ActiveSolutionPath());
            if (tsproj is not null &&
                new TwinCat.HostSelection.XaeHostResolver().TryResolveFromTsprojFile(tsproj, out var selection, out _))
                tcVersion = selection!.TcVersion;

            return TwinCat.HostSelection.XaeHostReport.StatusLine(running, tcVersion);
        }
        catch
        {
            return null;
        }
    }

    private void SetChip(Label text, Label dot, string chipText, string toolTip, IndicatorState state)
    {
        var p = ThemeManager.Palette;
        Color colour = state switch
        {
            IndicatorState.Ok => p.Ok,
            IndicatorState.Warn => p.Warn,
            IndicatorState.Error => p.Error,
            _ => p.Inactive
        };
        text.Text = chipText;
        text.ForeColor = colour;
        dot.BackColor = colour;
        _toolTip.SetToolTip(text, toolTip);
        _toolTip.SetToolTip(dot, toolTip);
    }

    /// <summary>Queue a UI refresh on the WinForms thread. Safe to call from any thread
    /// (ServerHost.EnvironmentRefreshRequested is raised on MCP threads).</summary>
    private void RefreshEnvironmentStatus(string reason) =>
        BeginInvokeSafe(() => UpdateEnvironmentStatus(reason));

    /// <summary>Header Stop button and window close: cancels the running apply (twincat_stop).</summary>
    private bool StopActiveProcess() => _host.RequestStop();

    /// <summary>Writes to the host run log, which <c>twincat_get_log</c> reads.</summary>
    private void AppendLog(string line) => _host.RunLog.Append(line);

    private void WriteEnvironmentLog(string reason, string xaeState, string projectState, IReadOnlyList<string> plcs, IReadOnlyList<string> masters)
    {
        AppendLog($"=== Check: {reason} ===");
        AppendLog($"XAE    : {xaeState}");
        AppendLog($"Project: {projectState}");
        AppendLog($"PLCs   : {string.Join(", ", plcs)}");
        AppendLog($"Master : {string.Join(", ", masters)}");
        AppendLog(string.Empty);
    }
}
