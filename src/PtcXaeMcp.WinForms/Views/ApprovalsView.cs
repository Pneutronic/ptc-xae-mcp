using System.Globalization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Views;

/// <summary>
/// The "Approvals" view (issue #272): one place for every human approval.
/// <list type="bullet">
/// <item><b>IBN Live</b> — ABORT / RELEASE-ALL and the state line (armed, remaining time,
/// pending proposals) in the top row, the arm row (GVL, TTL, allowlist, Arm / Disarm) and the
/// audit log. Arming happens ONLY here (decision 2026-10-19); the MCP client can only propose.</item>
/// <item><b>HMI write approvals</b> — every answer of the HMI confirm popup, from
/// <see cref="ServerHost.HmiApprovals"/>.</item>
/// </list>
/// <para>
/// The view is the ONLY execution path of an IBN step: every proposal raised by
/// <see cref="IbnLiveCoordinator.ProposalRaised"/> opens <see cref="IbnConfirmDialog"/> (modal,
/// TopMost) through the main window, even while another view is active. OK →
/// <see cref="IbnLiveCoordinator.ConfirmProposal"/> (host-only, writes the single command bit);
/// anything else → <see cref="IbnLiveCoordinator.RejectProposal"/> or nothing. Every IBN action
/// runs under the shared dispatcher lock (<see cref="GuiComDispatchRunner"/>, issue #154).
/// </para>
/// </summary>
internal sealed partial class ApprovalsView : UserControl
{
    private static readonly char[] AllowlistSeparators = [',', ';', '\r', '\n'];

    private ServerHost? _host;
    private GuiComDispatchRunner? _guiDispatch;
    private IWin32Window? _owner;
    private Action<Action> _post = action => action();

    private readonly Button _btnIbnAbort;
    private readonly Label _lblIbnArmStatus;
    private readonly Label _lblIbnLastAction;
    private readonly TextBox _txtIbnGvl;
    private readonly NumericUpDown _numIbnTtl;
    private readonly TextBox _txtIbnAllowlist;
    private readonly TextBox _txtIbnNetId;
    private readonly NumericUpDown _numIbnPort;
    private readonly Button _btnIbnArm;
    private readonly Button _btnIbnDisarm;
    private readonly TextBox _txtIbnAudit;
    private readonly DataGridView _gridHmiApprovals;
    private readonly System.Windows.Forms.Timer _ibnCountdownTimer;

    // Audit entries replayed from the snapshot in Attach (UI thread only), see _onAudited.
    private HashSet<IbnAuditEntry>? _replayedAudit;

    // Proposals whose popup is open right now (UI thread only).
    private readonly HashSet<string> _openPopups = new(StringComparer.Ordinal);
    private ApprovalsAttention _lastAttention = ApprovalsAttention.None;

    // Stored so Dispose can unsubscribe from the host-lifetime coordinator and history.
    private readonly Action _onStateChanged;
    private readonly Action<IbnAuditEntry> _onAudited;
    private readonly Action<HmiApprovalRecord> _onHmiApprovalChanged;

    /// <summary>Raised on the UI thread when the badge state changes (see <see cref="Attention"/>).</summary>
    public event Action<ApprovalsAttention>? AttentionChanged;

    /// <summary>What the header badge shows: pending approvals and the arm state.</summary>
    public ApprovalsAttention Attention => _lastAttention;

    private ServerHost Host => _host ?? throw new InvalidOperationException("ApprovalsView is not attached.");

    /// <summary>ibn-live: the host's commissioning coordinator, shared with the MCP tools
    /// (one proposal registry / heartbeat).</summary>
    private IbnLiveCoordinator _liveCoordinator => Host.LiveCoordinator;

    public ApprovalsView()
    {
        Name = "_approvalsView";

        // ── Top row: ABORT (always visible while this view is shown) + state ─────
        _btnIbnAbort = new Button
        {
            Name = "_btnIbnAbort",
            Text = "■ ABORT / RELEASE-ALL",
            AutoSize = true,
            MinimumSize = new Size(260, 44),
            BackColor = ThemeManager.Palette.Error,
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            Tag = "danger", // ThemeManager.StyleButton keeps it red on every theme apply
            Margin = new Padding(0, 0, 12, 0),
            Anchor = AnchorStyles.Left,
        };
        _lblIbnArmStatus = new Label
        {
            Name = "_lblIbnArmStatus",
            AutoSize = true,
            Text = "Disarmed.",
            ForeColor = ThemeManager.Palette.Inactive,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 2),
        };
        _lblIbnLastAction = new Label
        {
            Name = "_lblIbnLastAction",
            AutoSize = true,
            Text = string.Empty,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        var stateText = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        stateText.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        stateText.Controls.Add(_lblIbnArmStatus, 0, 0);
        stateText.Controls.Add(_lblIbnLastAction, 0, 1);

        var topRow = new TableLayoutPanel
        {
            Name = "_pnlIbnTop",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        topRow.Controls.Add(_btnIbnAbort, 0, 0);
        topRow.Controls.Add(stateText, 1, 0);

        // ── Arm row (wraps instead of clipping at narrow widths) ─────────────
        _txtIbnGvl = new TextBox { Name = "_txtIbnGvl", Width = 160, Text = IbnLiveCoordinator.DefaultGvlPath };
        _numIbnTtl = new NumericUpDown
        {
            Name = "_numIbnTtl",
            Width = 70,
            Minimum = 1,
            Maximum = IbnLiveCoordinator.MaxArmTtlSeconds,
            Value = IbnLiveCoordinator.DefaultArmTtlSeconds,
        };
        _txtIbnAllowlist = new TextBox { Name = "_txtIbnAllowlist", Width = 220, PlaceholderText = "Cyl1, Cyl2 (comma-separated)" };
        // Arming is GUI-only (#272), so the GUI must reach every target the MCP tool could:
        // empty NetId = the project's target, port 851 = the first PLC runtime.
        _txtIbnNetId = new TextBox { Name = "_txtIbnNetId", Width = 130, PlaceholderText = "project target" };
        _numIbnPort = new NumericUpDown { Name = "_numIbnPort", Width = 70, Minimum = 1, Maximum = 65535, Value = 851 };
        _btnIbnArm = new Button { Name = "_btnIbnArm", Text = "Arm", AutoSize = true, MinimumSize = new Size(80, 29) };
        _btnIbnDisarm = new Button { Name = "_btnIbnDisarm", Text = "Disarm", AutoSize = true, MinimumSize = new Size(80, 29) };

        var armRow = new FlowLayoutPanel
        {
            Name = "_pnlIbnArm",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 4),
        };
        armRow.Controls.AddRange(new Control[]
        {
            RowLabel("GVL:"), _txtIbnGvl,
            RowLabel("TTL (s):"), _numIbnTtl,
            RowLabel("Allowlist:"), _txtIbnAllowlist,
            RowLabel("Target NetId:"), _txtIbnNetId,
            RowLabel("Port:"), _numIbnPort,
            _btnIbnArm, _btnIbnDisarm,
        });

        var lblArmHint = new Label
        {
            Name = "_lblIbnArmHint",
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = ThemeManager.Palette.Inactive,
            Text = "Arming happens only here. The MCP client can propose steps; each one opens a confirm " +
                   "popup, and nothing moves without your click. Abort, disarm and release-all are always allowed.",
            Margin = new Padding(0, 0, 0, 8),
        };

        // ── IBN audit log ────────────────────────────────────────────────────
        _txtIbnAudit = new TextBox
        {
            Name = "_txtIbnAudit",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5F),
        };
        var grpAudit = new GroupBox { Name = "_grpIbnAudit", Text = "IBN Live audit log", Dock = DockStyle.Fill, Padding = new Padding(8) };
        grpAudit.Controls.Add(_txtIbnAudit);

        // ── HMI write approvals ──────────────────────────────────────────────
        _gridHmiApprovals = new DataGridView
        {
            Name = "_gridHmiApprovals",
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time", FillWeight = 11 });
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Project", HeaderText = "Project", FillWeight = 16 });
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Kind", HeaderText = "Kind", FillWeight = 14 });
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Symbol / item", FillWeight = 29 });
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value", FillWeight = 16 });
        _gridHmiApprovals.Columns.Add(new DataGridViewTextBoxColumn { Name = "Outcome", HeaderText = "Outcome", FillWeight = 14 });
        var grpHmi = new GroupBox { Name = "_grpHmiApprovals", Text = "HMI write approvals", Dock = DockStyle.Fill, Padding = new Padding(8) };
        grpHmi.Controls.Add(_gridHmiApprovals);

        // ── Root layout: fixed rows on top, the two logs share the rest ──────
        var root = new TableLayoutPanel
        {
            Name = "_pnlApprovals",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
        root.Controls.Add(topRow, 0, 0);
        root.Controls.Add(armRow, 0, 1);
        root.Controls.Add(lblArmHint, 0, 2);
        root.Controls.Add(grpAudit, 0, 3);
        root.Controls.Add(grpHmi, 0, 4);
        Controls.Add(root);

        // The hint wraps to the view width instead of widening the window.
        Resize += (_, _) => lblArmHint.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 8), 0);

        _ibnCountdownTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _onStateChanged = () => _post(RefreshIbnState);
        _onAudited = e => _post(() =>
        {
            // Raised between subscribing and the snapshot replay in Attach: already shown.
            if (_replayedAudit?.Remove(e) == true) return;
            AppendIbnAudit(e);
        });
        _onHmiApprovalChanged = r => _post(() => ShowHmiApproval(r));
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 4, 0),
    };

    /// <summary>
    /// Wires the view to the host's IBN Live coordinator and HMI approval history and starts the
    /// countdown.
    /// </summary>
    /// <param name="host">Owns the coordinator, the HMI approval history and the run log.</param>
    /// <param name="guiDispatch">The shared dispatcher-lock seam (issue #154).</param>
    /// <param name="owner">Owner of the confirm popup — the main window, which has a handle
    /// even while this tab was never shown.</param>
    /// <param name="post">Marshals onto the UI thread through the main window, for the same
    /// reason: a proposal raised before the tab was opened must still pop up at once.</param>
    public void Attach(ServerHost host, GuiComDispatchRunner guiDispatch, IWin32Window owner, Action<Action> post)
    {
        if (_host is not null)
            throw new InvalidOperationException("ApprovalsView is already attached.");
        _host = host;
        _guiDispatch = guiDispatch;
        _owner = owner;
        _post = post;

        // Every handler that triggers ADS work runs it on a background thread under the
        // dispatcher lock (RunGuiComActionAsync), so the WinForms UI thread keeps pumping:
        // ADS calls load the symbol table synchronously with a 10 s connect timeout.
        _btnIbnArm.Click += async (_, _) => await ArmIbnFromGuiAsync();
        _btnIbnDisarm.Click += async (_, _) => await RunIbnAsync(() => _liveCoordinator.Disarm(), "Disarm");
        _btnIbnAbort.Click += async (_, _) => await RunIbnAsync(() => _liveCoordinator.ReleaseAll(), "ABORT / RELEASE-ALL");

        // The coordinator and the history raise these on MCP/timer threads; marshal to the UI.
        _liveCoordinator.ProposalRaised += OnIbnProposalRaised;
        _liveCoordinator.StateChanged += _onStateChanged;
        _liveCoordinator.Audited += _onAudited;
        host.HmiApprovals.Changed += _onHmiApprovalChanged;
        AttachRestore(host);

        var auditSnapshot = _liveCoordinator.AuditSnapshot();
        _replayedAudit = new HashSet<IbnAuditEntry>(auditSnapshot);
        foreach (var entry in auditSnapshot)
            AppendIbnAudit(entry);
        foreach (var record in host.HmiApprovals.Snapshot())
            ShowHmiApproval(record);

        _ibnCountdownTimer.Tick += (_, _) => RefreshIbnState();
        _ibnCountdownTimer.Start();
        RefreshIbnState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Stop the countdown timer; the coordinator it reads is torn down by
            // ServerHost.Dispose (IBN abort, UMR, session, dispatcher) after Application.Run.
            _ibnCountdownTimer.Stop();
            _ibnCountdownTimer.Dispose();
            if (_host is { } host)
            {
                host.LiveCoordinator.ProposalRaised -= OnIbnProposalRaised;
                host.LiveCoordinator.StateChanged -= _onStateChanged;
                host.LiveCoordinator.Audited -= _onAudited;
                host.HmiApprovals.Changed -= _onHmiApprovalChanged;
                DetachRestore(host);
            }
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Runs a synchronous COM/ADS-touching action off the UI thread under the shared
    /// <see cref="McpDispatcher"/> lock (issue #154) — same seam the main window used.
    /// <paramref name="onResult"/> runs once on success; <paramref name="onError"/> once for a
    /// busy dispatcher, an <see cref="XaeBlockedException"/> or any other exception.
    /// </summary>
    private async Task RunGuiComActionAsync<T>(Func<T> action, Action<T> onResult, Action<string> onError)
    {
        var dispatch = _guiDispatch ?? throw new InvalidOperationException("ApprovalsView is not attached.");
        var outcome = await dispatch.RunAsync(action);
        if (outcome.Ok) onResult(outcome.Value!);
        else onError(outcome.ErrorMessage!);
    }

    /// <summary>Writes to the host run log (<c>twincat_get_log</c>), as the IBN tab did.</summary>
    private void AppendLog(string line) => Host.RunLog.Append(line);

    private async Task ArmIbnFromGuiAsync()
    {
        // Snapshot UI control values on the UI thread before going off-thread.
        var allow = _txtIbnAllowlist.Text
            .Split(AllowlistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int ttl = (int)_numIbnTtl.Value;
        string? gvl = string.IsNullOrWhiteSpace(_txtIbnGvl.Text) ? null : _txtIbnGvl.Text.Trim();
        string? netId = string.IsNullOrWhiteSpace(_txtIbnNetId.Text) ? null : _txtIbnNetId.Text.Trim();
        int port = (int)_numIbnPort.Value;

        _btnIbnArm.Enabled = false;
        try
        {
            await RunGuiComActionAsync(
                () => _liveCoordinator.Arm(ttl, allow, gvl, netId, port),
                text =>
                {
                    ShowLastAction(McpErrors.IsOk(text) ? "Arm ok." : "Arm refused: " + text, !McpErrors.IsOk(text));
                    AppendLog(text);
                },
                text =>
                {
                    ShowLastAction("Arm failed: " + text, isError: true);
                    AppendLog(text);
                });
        }
        finally
        {
            _btnIbnArm.Enabled = true;
            RefreshIbnState();
        }
    }

    private async Task RunIbnAsync(Func<string> action, string label)
    {
        // Disarm / ABORT-RELEASE-ALL write the stop bits over ADS — never on the UI thread,
        // and deliberately NOT under the dispatcher lock (issue #1): while a build or an
        // activation holds it, ABORT could only answer "busy". The coordinator's own I/O
        // gate serialises the stop against the other IBN writes, exactly as for the matching
        // MCP tools (twincat_ibn_disarm/_abort/_release_all). A failure (ADS error) is shown
        // in red next to the button so the operator knows to retry or use the E-stop.
        string text;
        try
        {
            text = await Task.Run(action).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            text = $"[Error] {ex.Message}";
        }
        bool ok = McpErrors.IsOk(text);
        ShowLastAction(ok ? $"{label} ok." : $"{label} FAILED: {text}", !ok);
        AppendLog(text);
        RefreshIbnState();
    }

    private void ShowLastAction(string text, bool isError)
    {
        if (IsDisposed) return;
        _lblIbnLastAction.Text = $"{DateTime.Now:HH:mm:ss}  {text}";
        _lblIbnLastAction.ForeColor = isError ? ThemeManager.Palette.Error : ThemeManager.Palette.Text;
    }

    // Modal confirm popup — the ONLY execution path. Posted through the main window so it
    // appears at once, whichever view is active; non-blocking for the MCP thread (Propose
    // returned 'pending' already).
    private void OnIbnProposalRaised(IbnProposal p)
    {
        _post(async () =>
        {
            if (IsDisposed) return;
            _openPopups.Add(p.ProposalId);
            DialogResult answer;
            try
            {
                RefreshIbnState();
                // Owned windows are hidden with a minimized owner — bring the host up first.
                if (_owner is Form { WindowState: FormWindowState.Minimized } form)
                    form.WindowState = FormWindowState.Normal;
                using var dialog = new IbnConfirmDialog(p, () => IsPending(p.ProposalId));
                answer = dialog.ShowDialog(_owner);
            }
            finally
            {
                _openPopups.Remove(p.ProposalId);
            }

            // ConfirmProposal writes the single command bit over ADS — off the UI thread,
            // under the same dispatcher lock every other ADS-touching IBN action uses. Reject
            // touches no ADS but goes through the same seam for registry consistency. A popup
            // that closed because the proposal was no longer pending needs no answer.
            if (answer == DialogResult.OK)
            {
                await RunGuiComActionAsync(
                    () => _liveCoordinator.ConfirmProposal(p.ProposalId),
                    text =>
                    {
                        ShowLastAction(McpErrors.IsOk(text) ? $"Step {p.ProposalId} executed." : $"Step {p.ProposalId} not executed: {text}", !McpErrors.IsOk(text));
                        AppendLog(text);
                    },
                    text =>
                    {
                        // Busy lock (or another failure before the write): the proposal is still
                        // pending, so ask again rather than leave it without a popup.
                        ShowLastAction($"Step {p.ProposalId} not executed: {text}", isError: true);
                        AppendLog(text);
                        AskAgainIfStillPending(p);
                    });
            }
            else if (IsPending(p.ProposalId))
            {
                bool expired = answer == DialogResult.Abort;
                string reason = expired ? "confirm popup expired" : "operator cancelled";
                await RunGuiComActionAsync(
                    () => _liveCoordinator.RejectProposal(p.ProposalId, reason),
                    text =>
                    {
                        bool ok = McpErrors.IsOk(text);
                        ShowLastAction(ok ? $"Step {p.ProposalId} rejected ({reason})."
                                          : $"Step {p.ProposalId} NOT rejected: {text}", isError: !ok);
                        AppendLog(text);
                    },
                    text =>
                    {
                        ShowLastAction($"Step {p.ProposalId} NOT rejected (still pending): {text}", isError: true);
                        AppendLog(text);
                        // An expired popup is not re-shown (it would close at once); the coordinator
                        // refuses a confirm once the arm window has closed.
                        if (!expired) AskAgainIfStillPending(p);
                    });
            }
            RefreshIbnState();
        });
    }

    /// <summary>Re-shows the confirm popup for a proposal whose answer could not be applied.</summary>
    private void AskAgainIfStillPending(IbnProposal p)
    {
        if (IsPending(p.ProposalId) && !_openPopups.Contains(p.ProposalId) && DateTime.UtcNow < p.ExpiresUtc)
            OnIbnProposalRaised(p);
    }

    private bool IsPending(string proposalId) =>
        _host is not null &&
        _liveCoordinator.ProposalsSnapshot().Any(x => x.ProposalId == proposalId && x.State == IbnProposalState.Pending);

    private void RefreshIbnState()
    {
        if (IsDisposed || _host is null) return;
        int pending = PendingProposalCount();
        string pendingText = pending == 0 ? string.Empty
            : pending == 1 ? " · 1 proposal awaiting your confirmation" : $" · {pending} proposals awaiting your confirmation";
        bool armed = _liveCoordinator.IsArmed;
        int remaining = armed ? _liveCoordinator.RemainingSeconds : 0;
        if (armed)
        {
            _lblIbnArmStatus.Text = $"ARMED — {remaining} s remaining · GVL {_liveCoordinator.GvlPath}{pendingText}";
            _lblIbnArmStatus.ForeColor = ThemeManager.Palette.Warn;
        }
        else
        {
            _lblIbnArmStatus.Text = "Disarmed." + pendingText;
            _lblIbnArmStatus.ForeColor = ThemeManager.Palette.Inactive;
        }
        UpdateAttention(new ApprovalsAttention(pending, Host.HmiApprovals.PendingCount, armed, remaining));
    }

    /// <summary>Pending, unexpired proposals plus popups still on screen.</summary>
    private int PendingProposalCount()
    {
        DateTime now = DateTime.UtcNow;
        var pendingIds = _liveCoordinator.ProposalsSnapshot()
            .Where(x => x.State == IbnProposalState.Pending && now < x.ExpiresUtc)
            .Select(x => x.ProposalId)
            .ToHashSet(StringComparer.Ordinal);
        pendingIds.UnionWith(_openPopups);
        return pendingIds.Count + PendingRestoreCount();
    }

    private void UpdateAttention(ApprovalsAttention attention)
    {
        if (attention == _lastAttention) return;
        _lastAttention = attention;
        AttentionChanged?.Invoke(attention);
    }

    private void AppendIbnAudit(IbnAuditEntry e)
    {
        if (IsDisposed) return;
        _txtIbnAudit.AppendText(
            $"{e.TimestampUtc.ToLocalTime():HH:mm:ss} {e.Kind}: {e.Detail}{Environment.NewLine}");
    }

    private void ShowHmiApproval(HmiApprovalRecord r)
    {
        if (IsDisposed) return;
        string id = r.Id.ToString(CultureInfo.InvariantCulture);
        DataGridViewRow? row = _gridHmiApprovals.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(x => x.Tag as string == id);
        object[] cells =
        [
            r.RequestedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            r.HmiProject,
            r.Kind.ToString(),
            r.Symbol,
            r.NewValue ?? "-",
            OutcomeText(r),
        ];
        if (row is null)
        {
            int index = _gridHmiApprovals.Rows.Add(cells);
            row = _gridHmiApprovals.Rows[index];
            row.Tag = id;
            // Same bound as the history: drop the oldest rows.
            while (_gridHmiApprovals.Rows.Count > Host.HmiApprovals.Capacity)
                _gridHmiApprovals.Rows.RemoveAt(0);
            try { _gridHmiApprovals.FirstDisplayedScrollingRowIndex = _gridHmiApprovals.Rows.Count - 1; }
            catch (InvalidOperationException) { /* grid not laid out yet (tab never shown) */ }
        }
        else
        {
            row.SetValues(cells);
        }
        row.Cells["Outcome"].Style.ForeColor = OutcomeColor(r);
        RefreshIbnState();
    }

    internal static string OutcomeText(HmiApprovalRecord r) => r.Outcome switch
    {
        null => "Pending",
        HmiConfirmOutcome.Confirmed => "Confirmed",
        HmiConfirmOutcome.AutoConfirmed => "AutoConfirmed",
        HmiConfirmOutcome.Rejected => "Rejected",
        HmiConfirmOutcome.TimedOut => "TimedOut",
        HmiConfirmOutcome.Unavailable => "Unavailable",
        _ => r.Outcome.Value.ToString(),
    };

    private static Color OutcomeColor(HmiApprovalRecord r)
    {
        var p = ThemeManager.Palette;
        return r.Outcome switch
        {
            null => p.Warn,
            HmiConfirmOutcome.Confirmed or HmiConfirmOutcome.AutoConfirmed => p.Ok,
            _ => p.Error,
        };
    }
}

/// <summary>What needs the operator's attention in the Approvals view — drives the header badge.</summary>
/// <param name="PendingProposals">IBN proposals awaiting a human click.</param>
/// <param name="PendingHmi">HMI write popups currently open.</param>
/// <param name="Armed">IBN Live control window open.</param>
/// <param name="RemainingSeconds">Seconds left in the arm window (0 when disarmed).</param>
internal sealed record ApprovalsAttention(int PendingProposals, int PendingHmi, bool Armed, int RemainingSeconds)
{
    public static readonly ApprovalsAttention None = new(0, 0, false, 0);

    public int Pending => PendingProposals + PendingHmi;

    public bool NeedsAttention => Pending > 0 || Armed;

    /// <summary>Tab caption, e.g. "Approvals (1)".</summary>
    public string TabCaption => Pending > 0 ? $"Approvals ({Pending})" : "Approvals";

    /// <summary>Header chip text; empty when nothing needs attention.</summary>
    public string ChipText => (Pending, Armed) switch
    {
        ( > 0, true) => $"{PendingText} · IBN armed {RemainingSeconds} s",
        ( > 0, false) => PendingText,
        (_, true) => $"IBN armed — {RemainingSeconds} s",
        _ => string.Empty,
    };

    private string PendingText => Pending == 1 ? "1 approval pending" : $"{Pending} approvals pending";
}
