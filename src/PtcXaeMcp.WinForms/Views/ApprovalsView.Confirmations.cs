using System.Globalization;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.WinForms.Views;

// Human confirmation, token path: an MCP client that cannot be asked in-band (no elicitation) is refused with a token
// and waits for a human to press Release here. The Release and Deny buttons are the ONLY callers of
// ConfirmationRegistry.Release / Deny (no MCP binding; pinned by a source test).
internal sealed partial class ApprovalsView
{
    private DataGridView _gridConfirmations = null!;
    private Button _btnConfirmRelease = null!;
    private Button _btnConfirmDeny = null!;
    private Label _lblConfirmHint = null!;
    private GroupBox _grpConfirmations = null!;
    private Action? _onConfirmationsChanged;
    private string _confirmationSignature = string.Empty;

    private GroupBox BuildConfirmationGroup()
    {
        _gridConfirmations = new DataGridView
        {
            Name = "_gridConfirmations",
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
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = Loc.Get("approvals.column.request"), FillWeight = 10 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = Loc.Get("approvals.column.time"), FillWeight = 9 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tool", HeaderText = Loc.Get("approvals.column.tool"), FillWeight = 22 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = Loc.Get("approvals.column.target"), FillWeight = 16 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Arguments", HeaderText = Loc.Get("approvals.column.arguments"), FillWeight = 31 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = Loc.Get("approvals.column.status"), FillWeight = 12 });
        _gridConfirmations.SelectionChanged += (_, _) => UpdateConfirmationButtons();

        _btnConfirmRelease = new Button { Name = "_btnConfirmRelease", Text = Loc.Get("approvals.button.release"), AutoSize = true, MinimumSize = new Size(90, 29), Tag = "primary", Enabled = false };
        _btnConfirmDeny = new Button { Name = "_btnConfirmDeny", Text = Loc.Get("approvals.button.deny"), AutoSize = true, MinimumSize = new Size(90, 29), Enabled = false };
        _lblConfirmHint = new Label
        {
            Name = "_lblConfirmHint",
            AutoSize = true,
            ForeColor = ThemeManager.Palette.Inactive,
            Text = Loc.Get("approvals.confirm.hint"),
            Margin = new Padding(0, 4, 0, 0),
        };
        // Buttons in one row that never wraps, the hint in its own row bounded to the group width: a wrapping hint inside
        // the button row was measured on one line and then took the height the grid needs (grid shrank to its header).
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };
        buttons.Controls.AddRange([_btnConfirmRelease, _btnConfirmDeny]);

        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.Controls.Add(_gridConfirmations, 0, 0);
        inner.Controls.Add(buttons, 0, 1);
        inner.Controls.Add(_lblConfirmHint, 0, 2);
        inner.Resize += (_, _) => _lblConfirmHint.MaximumSize = new Size(Math.Max(200, inner.ClientSize.Width - 8), 0);

        var group = _grpConfirmations = new GroupBox { Name = "_grpConfirmations", Text = Loc.Get("approvals.group.confirmations"), Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(inner);
        return group;
    }

    /// <summary>Sets the static texts of the confirmation group again and renders its rows again from the registry.</summary>
    private void ApplyConfirmationLanguage()
    {
        _grpConfirmations.Text = Loc.Get("approvals.group.confirmations");
        _btnConfirmRelease.Text = Loc.Get("approvals.button.release");
        _btnConfirmDeny.Text = Loc.Get("approvals.button.deny");
        _lblConfirmHint.Text = Loc.Get("approvals.confirm.hint");
        _gridConfirmations.Columns["Code"]!.HeaderText = Loc.Get("approvals.column.request");
        _gridConfirmations.Columns["Time"]!.HeaderText = Loc.Get("approvals.column.time");
        _gridConfirmations.Columns["Tool"]!.HeaderText = Loc.Get("approvals.column.tool");
        _gridConfirmations.Columns["Target"]!.HeaderText = Loc.Get("approvals.column.target");
        _gridConfirmations.Columns["Arguments"]!.HeaderText = Loc.Get("approvals.column.arguments");
        _gridConfirmations.Columns["Status"]!.HeaderText = Loc.Get("approvals.column.status");
        // State and target words are part of the rows: force the rebuild that RefreshConfirmations otherwise skips.
        _confirmationSignature = string.Empty;
        RefreshConfirmations();
    }

    private void AttachConfirmations(ServerHost host)
    {
        _onConfirmationsChanged = () => _post(() => { RefreshConfirmations(); RefreshIbnState(); });
        host.Confirmations.Changed += _onConfirmationsChanged;
        _btnConfirmRelease.Click += (_, _) => AnswerSelectedConfirmation(release: true);
        _btnConfirmDeny.Click += (_, _) => AnswerSelectedConfirmation(release: false);
        RefreshConfirmations();
    }

    private void DetachConfirmations(ServerHost host)
    {
        if (_onConfirmationsChanged is not null) host.Confirmations.Changed -= _onConfirmationsChanged;
    }

    /// <summary>Requests that wait for a human (the badge counts them).</summary>
    private int PendingConfirmationCount() => _host?.Confirmations.PendingCount ?? 0;

    private string? SelectedPendingCode()
    {
        if (_gridConfirmations.SelectedRows.Count != 1) return null;
        var row = _gridConfirmations.SelectedRows[0];
        // The state is read from the cell's tag, not from its (translated) text.
        return row.Tag is string code && row.Cells["Status"].Tag is ConfirmationState.Pending ? code : null;
    }

    private void UpdateConfirmationButtons()
    {
        bool pending = SelectedPendingCode() is not null;
        _btnConfirmRelease.Enabled = pending;
        _btnConfirmDeny.Enabled = pending;
    }

    private void AnswerSelectedConfirmation(bool release)
    {
        if (_host is null || SelectedPendingCode() is not { } code) return;
        bool done = release ? _host.Confirmations.Release(code) : _host.Confirmations.Deny(code);
        string what = release ? "released" : "denied";
        ShowLastAction(release
            ? (done ? Loc.Format("approvals.confirm.released", code) : Loc.Format("approvals.confirm.release_failed", code))
            : (done ? Loc.Format("approvals.confirm.denied", code) : Loc.Format("approvals.confirm.deny_failed", code)), isError: !done);
        AppendLog(done ? $"Confirmation request {code} {what} by the operator." : $"Confirmation request {code}: answer {what} had no effect.");
        RefreshConfirmations();
        RefreshIbnState();
    }

    internal static string ConfirmationStateText(ConfirmationState state) => state switch
    {
        ConfirmationState.Pending => Loc.Get("approvals.state.pending"),
        ConfirmationState.Released => Loc.Get("approvals.state.released"),
        ConfirmationState.Denied => Loc.Get("approvals.state.denied"),
        ConfirmationState.Used => Loc.Get("approvals.state.used"),
        _ => Loc.Get("approvals.state.expired"),
    };

    private void RefreshConfirmations()
    {
        if (IsDisposed || _host is null) return;
        var entries = _host.Confirmations.Snapshot();
        // The grid is rebuilt only when something it shows changed, so the selection survives the countdown timer.
        string signature = string.Join('|', entries.Select(e => e.Code + e.State));
        if (signature == _confirmationSignature) return;
        _confirmationSignature = signature;

        string? selected = _gridConfirmations.SelectedRows.Count == 1 ? _gridConfirmations.SelectedRows[0].Tag as string : null;
        _gridConfirmations.Rows.Clear();
        foreach (var e in entries.OrderByDescending(x => x.CreatedUtc))
        {
            int index = _gridConfirmations.Rows.Add(
                e.Code,
                e.CreatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                e.Tool,
                ConfirmationTarget(e),
                string.IsNullOrEmpty(e.Summary) ? "-" : e.Summary,
                ConfirmationStateText(e.State));
            var row = _gridConfirmations.Rows[index];
            row.Tag = e.Code;
            row.Cells["Status"].Tag = e.State;
            row.Cells["Status"].Style.ForeColor = e.State switch
            {
                ConfirmationState.Pending => ThemeManager.Palette.Warn,
                ConfirmationState.Released or ConfirmationState.Used => ThemeManager.Palette.Ok,
                _ => ThemeManager.Palette.Error,
            };
            row.Selected = e.Code == selected;
        }
        UpdateConfirmationButtons();
    }

    private static string ConfirmationTarget(ConfirmationEntry e) => e.NetId switch
    {
        null => Loc.Get("approvals.target.unknown"),
        AccessPolicy.LocalKeyword => Loc.Get("approvals.target.local"),
        var id => id,
    };
}
