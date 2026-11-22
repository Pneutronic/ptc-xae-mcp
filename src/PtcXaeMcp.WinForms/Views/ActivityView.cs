using System.Diagnostics;
using System.Globalization;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.WinForms.Views;

/// <summary>
/// The "Activity" view (issue #270, moved unchanged from the former MCP Activity tab,
/// <c>Form1.McpActivity.cs</c>): live trace of every twincat_* tool call, the HTML report
/// link and — only with <c>--diagnostics</c> (#269) — the step/pause gate of the live suite.
/// <para>
/// live-test-narration: the view also shows which integration test is running RIGHT NOW.
/// The suite fires a ptc_test_marker before/after each test (see LiveTestTrace); that drives
/// <see cref="McpActivity.CurrentTest"/>, rendered as a status banner
/// "▶ Running (i/N): test — description" plus a per-test separator row in the grid, under
/// which that test's XAE operations appear.
/// </para>
/// </summary>
internal sealed class ActivityView : UserControl
{
    private McpActivity? _mcpActivity;
    private Action<Action> _post = action => action();
    private bool _suppressStepToggle;

    // Count of unified timeline items (separator + XAE-operation rows) already
    // rendered into the grid. The timeline is prefix-stable, so we only ever
    // append the new tail — existing rows are never touched.
    private int _renderedItemCount;

    private readonly DataGridView _gridActivity;
    private readonly TextBox _txtActivityDetail;
    private readonly Button _btnActivityClear;
    private readonly CheckBox _chkActivityAutoScroll;
    private readonly CheckBox _chkActivitySingleStep;
    private readonly Button _btnActivityContinue;
    private readonly Button _btnActivityStop;
    private readonly LinkLabel _lblActivityLogPath;
    private readonly Label _lblActivityPending;
    private readonly Label _lblActivityNarration;
    private readonly Button _btnRunAgain;

    /// <summary>
    /// Raised by "Run again in Console" for a selected tool-call row: tool name and the recorded
    /// arguments text (may be truncated at <see cref="McpActivityEntry.MaxFieldChars"/>).
    /// </summary>
    public event Action<string, string>? RunAgainRequested;

    public ActivityView()
    {
        Name = "_activityView";

        // ── Toolbar ───────────────────────────────────────────────────────
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 6),
        };

        _btnActivityClear = new Button { Name = "_btnActivityClear", Text = Loc.Get("activity.clear"), AutoSize = true };

        _chkActivityAutoScroll = new CheckBox
        {
            Name = "_chkActivityAutoScroll",
            Text = Loc.Get("activity.autoscroll"),
            Checked = true,
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 0),
        };

        _chkActivitySingleStep = new CheckBox
        {
            Name = "_chkActivitySingleStep",
            Visible = false, // diagnostics only, see Attach
            Text = Loc.Get("activity.singlestep"),
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 0),
        };
        _chkActivitySingleStep.CheckedChanged += (_, _) =>
        {
            if (_mcpActivity is null || _suppressStepToggle) return;
            _mcpActivity.Gate.Mode = _chkActivitySingleStep.Checked
                ? McpTestGateMode.SingleStep
                : McpTestGateMode.Run;
        };

        _btnActivityContinue = new Button { Name = "_btnActivityContinue", Text = Loc.Get("activity.next"), AutoSize = true, Visible = false };
        _btnActivityContinue.Click += (_, _) => _mcpActivity?.Gate.Continue();

        _btnActivityStop = new Button { Name = "_btnActivityStop", Text = Loc.Get("activity.stop"), AutoSize = true, Visible = false };
        _btnActivityStop.Click += (_, _) => _mcpActivity?.Gate.Stop();

        toolbar.Controls.Add(_btnActivityClear);
        toolbar.Controls.Add(_chkActivityAutoScroll);
        toolbar.Controls.Add(_chkActivitySingleStep);
        toolbar.Controls.Add(_btnActivityContinue);
        toolbar.Controls.Add(_btnActivityStop);

        // ── Live narration banner: which test is running right now ────────
        _lblActivityNarration = new Label
        {
            Name = "_lblActivityNarration",
            Visible = false, // diagnostics only
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            ForeColor = ThemeManager.Palette.Accent,
            BackColor = ThemeManager.Palette.SurfaceAlt,
            Text = "",
        };

        // ── Status line: pending-at-gate + active JSON log path ───────────
        _lblActivityPending = new Label
        {
            Name = "_lblActivityPending",
            Visible = false, // diagnostics only
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            Text = "",
        };
        _lblActivityLogPath = new LinkLabel
        {
            Name = "_lblActivityLogPath",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 22,
            Text = Loc.Get("activity.jsonlog.none"),
            LinkColor = ThemeManager.Palette.Accent,
            ActiveLinkColor = ThemeManager.Palette.AccentHover,
            VisitedLinkColor = ThemeManager.Palette.Accent,
        };
        // Generates and opens a human-readable HTML report of the CURRENT run.
        _lblActivityLogPath.LinkClicked += (_, _) => OpenCurrentHtmlReport();

        // ── Grid + detail split ───────────────────────────────────────────
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 360,
        };

        _gridActivity = new DataGridView
        {
            Name = "_gridActivity",
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
        _gridActivity.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Nr", HeaderText = "#", FillWeight = 8 });
        _gridActivity.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Time", HeaderText = Loc.Get("activity.column.time"), FillWeight = 18 });
        _gridActivity.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Tool", HeaderText = Loc.Get("activity.column.tool"), FillWeight = 42 });
        _gridActivity.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Ms", HeaderText = Loc.Get("activity.column.duration"), FillWeight = 16 });
        _gridActivity.Columns.Add(new DataGridViewTextBoxColumn
        { Name = "Status", HeaderText = Loc.Get("activity.column.status"), FillWeight = 16 });
        _gridActivity.SelectionChanged += (_, _) => ShowActivityDetail();
        _btnActivityClear.Click += (_, _) =>
        {
            _gridActivity.Rows.Clear();
            _renderedItemCount = 0;
            _mcpActivity?.StartNewRun();
        };

        _txtActivityDetail = new TextBox
        {
            Name = "_txtActivityDetail",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        _btnRunAgain = new Button
        {
            Name = "_btnRunAgain",
            Text = Loc.Get("activity.runagain"),
            AutoSize = true,
            Enabled = false,
        };
        _btnRunAgain.Click += (_, _) =>
        {
            if (_gridActivity.SelectedRows.Count > 0 && _gridActivity.SelectedRows[0].Tag is McpActivityEntry sel)
                RunAgainRequested?.Invoke(sel.ToolName, sel.Arguments);
        };
        var detailBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 4),
        };
        detailBar.Controls.Add(_btnRunAgain);

        split.Panel1.Controls.Add(_gridActivity);
        split.Panel2.Controls.Add(_txtActivityDetail);
        split.Panel2.Controls.Add(detailBar);

        // Docked controls are added last-to-first so Fill ends up innermost.
        // Top-to-bottom result: toolbar, narration banner, log path, pending,
        // then the grid/detail split.
        Controls.Add(split);
        Controls.Add(_lblActivityPending);
        Controls.Add(_lblActivityLogPath);
        Controls.Add(_lblActivityNarration);
        Controls.Add(toolbar);
    }

    /// <summary>
    /// Sets every static text again after a language change: buttons, check boxes, column headers, and the
    /// report link, gate and narration lines. Rows already in the grid keep their text (tool names, test
    /// names and the detail text of the selected row are not re-rendered).
    /// </summary>
    public void ApplyLanguage()
    {
        _btnActivityClear.Text = Loc.Get("activity.clear");
        _chkActivityAutoScroll.Text = Loc.Get("activity.autoscroll");
        _chkActivitySingleStep.Text = Loc.Get("activity.singlestep");
        _btnActivityContinue.Text = Loc.Get("activity.next");
        _btnActivityStop.Text = Loc.Get("activity.stop");
        _btnRunAgain.Text = Loc.Get("activity.runagain");
        _gridActivity.Columns["Time"]!.HeaderText = Loc.Get("activity.column.time");
        _gridActivity.Columns["Tool"]!.HeaderText = Loc.Get("activity.column.tool");
        _gridActivity.Columns["Ms"]!.HeaderText = Loc.Get("activity.column.duration");
        _gridActivity.Columns["Status"]!.HeaderText = Loc.Get("activity.column.status");
        if (_mcpActivity is null) _lblActivityLogPath.Text = Loc.Get("activity.jsonlog.none");
        else RenderMcpActivity();
    }

    /// <summary>
    /// Binds the view to the live <see cref="McpActivity"/> owned by the bridge (created after
    /// the form) and subscribes to its change notifications.
    /// </summary>
    /// <param name="post">Marshals an action onto the UI thread. The form's poster, not this
    /// control's: a view on a tab that was never shown has no window handle yet.</param>
    public void Attach(McpActivity activity, bool diagnosticsEnabled, Action<Action> post)
    {
        if (_mcpActivity is not null)
            throw new InvalidOperationException("ActivityView is already attached.");
        _mcpActivity = activity;
        _post = post;
        // The step/pause gate and the narration banner are test-harness controls: only
        // shown when the server runs with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1 (#269).
        _chkActivitySingleStep.Visible = diagnosticsEnabled;
        _btnActivityContinue.Visible = diagnosticsEnabled;
        _btnActivityStop.Visible = diagnosticsEnabled;
        _lblActivityNarration.Visible = diagnosticsEnabled;
        _lblActivityPending.Visible = diagnosticsEnabled;

        activity.Log.Changed += OnMcpActivityChanged;
        activity.Gate.Changed += OnMcpActivityChanged;
        activity.CurrentTest.Changed += OnMcpActivityChanged;
        RenderMcpActivity();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _mcpActivity is { } activity)
        {
            activity.Log.Changed -= OnMcpActivityChanged;
            activity.Gate.Changed -= OnMcpActivityChanged;
            activity.CurrentTest.Changed -= OnMcpActivityChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Updates the report line, making the path of the current run's HTML report
    /// a clickable link (or plain text before the first run). The report is the
    /// human-readable view; the raw per-run JSON-lines log remains on disk beside
    /// it as the machine record.
    /// </summary>
    private void SetReportLink(string? jsonPath)
    {
        string prefix = Loc.Get("activity.report.prefix");
        _lblActivityLogPath.Links.Clear();
        if (string.IsNullOrEmpty(jsonPath))
        {
            _lblActivityLogPath.Text = prefix + Loc.Get("activity.report.none");
            return;
        }
        var htmlPath = Path.ChangeExtension(jsonPath, ".html");
        _lblActivityLogPath.Text = prefix + htmlPath;
        _lblActivityLogPath.Links.Add(prefix.Length, htmlPath.Length);
    }

    /// <summary>
    /// Renders the current run as an HTML report (the same test-grouped timeline
    /// the grid shows), writes it next to the per-run JSON log, and opens it in
    /// the default browser. Generated on demand so there is no per-call overhead.
    /// </summary>
    private void OpenCurrentHtmlReport()
    {
        if (_mcpActivity is null) return;

        var jsonPath = _mcpActivity.JsonLog.CurrentPath;
        var entries = _mcpActivity.Log.Snapshot();
        var boundaries = _mcpActivity.CurrentTest.Boundaries();
        if (jsonPath is null && entries.Count == 0)
        {
            MessageBox.Show(Loc.Get("activity.report.norun"),
                Loc.Get("activity.report.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            var html = McpActivityHtmlReport.Render(
                entries, boundaries, 0, DateTimeOffset.Now, jsonPath);

            var htmlPath = jsonPath is not null
                ? Path.ChangeExtension(jsonPath, ".html")
                : Path.Combine(Path.GetTempPath(),
                    $"ptc-xae-mcp-activity-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            File.WriteAllText(htmlPath, html, System.Text.Encoding.UTF8);

            Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
            _lblActivityLogPath.LinkVisited = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.Format("activity.report.failed", ex.Message),
                Loc.Get("activity.report.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Server-thread change notification → marshal to the UI thread.</summary>
    private void OnMcpActivityChanged() => _post(RenderMcpActivity);

    private void RenderMcpActivity()
    {
        if (IsDisposed || _mcpActivity is null) return;

        RenderActivityRows();
        RenderNarrationBanner();

        SetReportLink(_mcpActivity.JsonLog.CurrentPath);

        string? pending = _mcpActivity.Gate.PendingToolName;
        _lblActivityPending.Text = pending is null
            ? (_mcpActivity.Gate.Mode == McpTestGateMode.SingleStep
                ? Loc.Get("activity.pending.singlestep")
                : "")
            : Loc.Format("activity.pending.gate", pending);

        bool stepping = _mcpActivity.Gate.Mode == McpTestGateMode.SingleStep;
        _btnActivityContinue.Enabled = stepping;
        // Keep the checkbox in sync if the mode was changed elsewhere
        // (e.g. ResetForNewRun) without re-entering the toggle handler.
        if (_chkActivitySingleStep.Checked != stepping)
        {
            _suppressStepToggle = true;
            _chkActivitySingleStep.Checked = stepping;
            _suppressStepToggle = false;
        }
    }

    /// <summary>
    /// Appends the new tail of the unified activity timeline. The timeline
    /// interleaves XAE-operation rows with one separator row per test, placed
    /// just before the operation whose index matches the test's captured
    /// <see cref="TestBoundary.AfterLogCount"/>. The prefix is stable as the log
    /// and boundaries only grow, so appending the tail never disturbs prior rows.
    /// </summary>
    private void RenderActivityRows()
    {
        var entries = _mcpActivity!.Log.Snapshot();
        var boundaries = _mcpActivity.CurrentTest.Boundaries();

        var items = new List<ActivityRow>(entries.Count + boundaries.Count);
        int bi = 0;
        for (int k = 0; k <= entries.Count; k++)
        {
            while (bi < boundaries.Count && boundaries[bi].AfterLogCount <= k)
            {
                items.Add(ActivityRow.ForSeparator(boundaries[bi]));
                bi++;
            }
            if (k < entries.Count)
                items.Add(ActivityRow.ForEntry(entries[k], k + 1));
        }

        if (_renderedItemCount >= items.Count) return;

        _gridActivity.SuspendLayout();
        for (int i = _renderedItemCount; i < items.Count; i++)
            AppendActivityRow(items[i]);
        _renderedItemCount = items.Count;
        _gridActivity.ResumeLayout();

        if (_chkActivityAutoScroll.Checked && _gridActivity.Rows.Count > 0)
        {
            _gridActivity.FirstDisplayedScrollingRowIndex = _gridActivity.Rows.Count - 1;
            _gridActivity.ClearSelection();
            _gridActivity.Rows[^1].Selected = true;
        }
    }

    private void AppendActivityRow(ActivityRow row)
    {
        if (row.Boundary is { } b)
        {
            string desc = string.IsNullOrWhiteSpace(b.Description) ? "" : $"  —  {b.Description}";
            int sidx = _gridActivity.Rows.Add("", "", $"▶ {b.Index}.  {b.Name}{desc}", "", "");
            var srow = _gridActivity.Rows[sidx];
            srow.Tag = b;
            srow.DefaultCellStyle.BackColor = ThemeManager.Palette.SurfaceAlt;
            srow.DefaultCellStyle.ForeColor = ThemeManager.Palette.Accent;
            srow.DefaultCellStyle.Font = new Font(_gridActivity.Font, FontStyle.Bold);
            return;
        }

        var e = row.Entry!;
        int idx = _gridActivity.Rows.Add(
            row.Number.ToString(CultureInfo.InvariantCulture),
            e.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            e.ToolName,
            ((int)e.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            e.Success ? "✓" : "✗");
        _gridActivity.Rows[idx].Tag = e;
        if (!e.Success)
            _gridActivity.Rows[idx].DefaultCellStyle.ForeColor = ThemeManager.Palette.Error;
    }

    private void RenderNarrationBanner()
    {
        var cur = _mcpActivity?.CurrentTest.Current;
        if (cur is null)
        {
            _lblActivityNarration.Text = "";
            return;
        }

        string ofN = cur.Index.ToString(CultureInfo.InvariantCulture);
        string desc = string.IsNullOrWhiteSpace(cur.Description) ? "" : $" — {cur.Description}";
        bool running = _mcpActivity!.CurrentTest.Running;
        _lblActivityNarration.Text = running
            ? Loc.Format("activity.narration.running", ofN, cur.Name, desc)
            : Loc.Format("activity.narration.done", ofN, cur.Name, desc);
    }

    /// <summary>A single grid row in the unified timeline: either a test
    /// separator (<see cref="Boundary"/> set) or an XAE-operation
    /// (<see cref="Entry"/> set with its 1-based <see cref="Number"/>).</summary>
    private readonly struct ActivityRow
    {
        public McpActivityEntry? Entry { get; private init; }
        public int Number { get; private init; }
        public TestBoundary? Boundary { get; private init; }

        public static ActivityRow ForEntry(McpActivityEntry entry, int number)
            => new() { Entry = entry, Number = number };

        public static ActivityRow ForSeparator(TestBoundary boundary)
            => new() { Boundary = boundary };
    }

    private void ShowActivityDetail()
    {
        _btnRunAgain.Enabled = _gridActivity.SelectedRows.Count > 0 &&
                               _gridActivity.SelectedRows[0].Tag is McpActivityEntry;
        if (_gridActivity.SelectedRows.Count == 0)
        {
            _txtActivityDetail.Text = "";
            return;
        }

        var row = _gridActivity.SelectedRows[0];

        // Separator row → show the test's narration on its own.
        if (row.Tag is TestBoundary tb)
        {
            var sep = new List<string>
            {
                Loc.Format("activity.detail.test.name", tb.Name),
                Loc.Format("activity.detail.test.index", tb.Index),
            };
            if (!string.IsNullOrEmpty(tb.Description))
            {
                sep.Add("");
                sep.Add(tb.Description);
            }
            _txtActivityDetail.Text = string.Join(Environment.NewLine, sep);
            return;
        }

        if (row.Tag is not McpActivityEntry e)
            return;

        // Attribute the operation to the nearest separator row above it.
        TestBoundary? owner = null;
        for (int i = row.Index - 1; i >= 0; i--)
            if (_gridActivity.Rows[i].Tag is TestBoundary owned) { owner = owned; break; }

        var lines = new List<string>
        {
            Loc.Format("activity.detail.tool", e.ToolName),
            Loc.Format("activity.detail.time", $"{e.TimestampUtc:o}"),
            Loc.Format("activity.detail.duration", $"{e.Duration.TotalMilliseconds:0.###}"),
            Loc.Format("activity.detail.status", Loc.Get(e.Success ? "activity.detail.status.ok" : "activity.detail.status.error")),
        };
        if (owner is not null)
        {
            lines.Add(Loc.Format("activity.detail.owner.test", owner.Name));
            if (!string.IsNullOrEmpty(owner.Description))
                lines.Add(Loc.Format("activity.detail.owner.description", owner.Description));
        }
        lines.Add("");
        lines.Add(Loc.Get("activity.detail.arguments"));
        lines.Add(e.Arguments);
        lines.Add("");
        lines.Add(Loc.Get("activity.detail.response"));
        lines.Add(e.Response);
        _txtActivityDetail.Text = string.Join(Environment.NewLine, lines);
    }
}
