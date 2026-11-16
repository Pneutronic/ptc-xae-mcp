using System.Globalization;
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
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Code", HeaderText = "Request", FillWeight = 10 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Time", HeaderText = "Time", FillWeight = 9 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tool", HeaderText = "Tool", FillWeight = 22 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Target", HeaderText = "Target", FillWeight = 16 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Arguments", HeaderText = "Arguments", FillWeight = 31 });
        _gridConfirmations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", FillWeight = 12 });
        _gridConfirmations.SelectionChanged += (_, _) => UpdateConfirmationButtons();

        _btnConfirmRelease = new Button { Name = "_btnConfirmRelease", Text = "Release", AutoSize = true, MinimumSize = new Size(90, 29), Tag = "primary", Enabled = false };
        _btnConfirmDeny = new Button { Name = "_btnConfirmDeny", Text = "Deny", AutoSize = true, MinimumSize = new Size(90, 29), Enabled = false };
        _lblConfirmHint = new Label
        {
            Name = "_lblConfirmHint",
            AutoSize = true,
            ForeColor = ThemeManager.Palette.Inactive,
            Text = "An agent whose client cannot ask you itself is waiting here. Check the tool, the target and the arguments, then " +
                   "press Release; the agent repeats its call with the token it was given and the action runs once. Nothing runs until then.",
            Margin = new Padding(8, 6, 0, 0),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([_btnConfirmRelease, _btnConfirmDeny, _lblConfirmHint]);

        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        inner.Controls.Add(_gridConfirmations, 0, 0);
        inner.Controls.Add(buttons, 0, 1);

        var group = new GroupBox { Name = "_grpConfirmations", Text = "Confirmation requests", Dock = DockStyle.Fill, Padding = new Padding(8) };
        group.Controls.Add(inner);
        return group;
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
        return row.Tag is string code && row.Cells["Status"].Value as string == ConfirmationStateText(ConfirmationState.Pending) ? code : null;
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
        ShowLastAction(done ? $"Request {code} {what}." : $"Request {code} could not be {what} (it expired or was answered already).", isError: !done);
        AppendLog(done ? $"Confirmation request {code} {what} by the operator." : $"Confirmation request {code}: answer {what} had no effect.");
        RefreshConfirmations();
        RefreshIbnState();
    }

    internal static string ConfirmationStateText(ConfirmationState state) => state switch
    {
        ConfirmationState.Pending => "Pending",
        ConfirmationState.Released => "Released",
        ConfirmationState.Denied => "Denied",
        ConfirmationState.Used => "Used",
        _ => "Expired",
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
        null => "unknown",
        AccessPolicy.LocalKeyword => "local",
        var id => id,
    };
}
