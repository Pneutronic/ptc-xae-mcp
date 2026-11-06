using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.Updates;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>What the Settings dialog shows of the MCP access: the window's tool profile and policy, and what the environment sets.</summary>
internal sealed record McpAccessSettings(string ToolProfile, string? ToolProfileEnvironment, AccessPolicy Window, AccessPolicy? Environment);

/// <summary>
/// "Settings…" (issue #273). Editable: theme, "remember window position" and the update-check
/// opt-in (saved in ui.json by the caller on OK). Read-only: the environment-driven server settings with the variable that
/// sets each one (<see cref="SettingsInfo"/>).
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly ComboBox _cmbTheme = new();
    private readonly CheckBox _chkRemember = new();
    private readonly CheckBox _chkUpdates = new();

    /// <param name="diagnosticsEnabled">The running server's state (McpBridge.DiagnosticsEnabled).</param>
    /// <param name="checkForUpdates">The ui.json opt-in for the update check at start.</param>
    /// <param name="updateCheckLocked">True when the machine policy or <see cref="UpdateCheck.EnvVar"/>
    /// forbids the check: the box is shown unchecked and disabled, so nobody can opt in here.</param>
    /// <param name="access">The tool profile and access policy to edit; null leaves the section out.</param>
    public SettingsDialog(int port, bool darkTheme, bool rememberWindowPosition, bool diagnosticsEnabled,
        bool checkForUpdates = false, bool updateCheckLocked = false, McpAccessSettings? access = null)
    {
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, access is null ? 520 : 760);
        MinimumSize = new Size(600, access is null ? 440 : 640);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, "Theme", _cmbTheme);
        _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTheme.Items.AddRange(["Light", "Dark"]);
        _cmbTheme.SelectedIndex = darkTheme ? 1 : 0;
        _cmbTheme.Width = 160;

        _chkRemember.Text = "Remember window position and size";
        _chkRemember.Checked = rememberWindowPosition;
        _chkRemember.AutoSize = true;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 1);
        layout.Controls.Add(_chkRemember, 1, 1);

        _chkUpdates.Text = "Check for a new release at start (one request to github.com, nothing is downloaded)";
        _chkUpdates.Checked = checkForUpdates && !updateCheckLocked;
        _chkUpdates.Enabled = !updateCheckLocked;
        _chkUpdates.AutoSize = true;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
        layout.Controls.Add(_chkUpdates, 1, 2);

        int row = 3;
        if (access is not null)
            row = AddAccessRows(layout, row, access);

        var caption = new Label
        {
            Text = "Server settings (read-only). They are read once at start: set the environment variable, then restart the app.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 16, 0, 6),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(caption, 0, row);
        layout.SetColumnSpan(caption, 2);

        var grid = BuildGrid(SettingsInfo.Rows(port, diagnosticsEnabled, Environment.GetEnvironmentVariable,
            UpdateCheck.DescribeState(checkForUpdates, UpdateCheck.IsDisabledByPolicy(), Environment.GetEnvironmentVariable)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(grid, 0, row + 1);
        layout.SetColumnSpan(grid, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK, Tag = "primary" };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(buttons, 0, row + 2);
        layout.SetColumnSpan(buttons, 2);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(layout);
        ThemeManager.ApplyDialog(this);
    }

    public bool DarkTheme => _cmbTheme.SelectedIndex == 1;

    public bool RememberWindowPosition => _chkRemember.Checked;

    public bool CheckForUpdates => _chkUpdates.Checked;

    /// <summary>The tool profile chosen: <see cref="ToolProfiles.Automatic"/>, <c>all</c>, <c>core</c> or a toolset list.</summary>
    public string ToolProfile => _cmbProfile.SelectedIndex == 0 || string.IsNullOrWhiteSpace(_cmbProfile.Text)
        ? ToolProfiles.Automatic
        : _cmbProfile.Text.Trim();

    /// <summary>The window's access policy as edited: level, blocked NetIds, allowed NetIds (empty = no allow list).</summary>
    public AccessPolicy AccessPolicy => new(
        _cmbAccess.SelectedIndex switch { 0 => AccessLevel.Read, 1 => AccessLevel.Engineer, _ => AccessLevel.Runtime },
        AccessPolicy.ParseList(_txtBlocked.Text),
        string.IsNullOrWhiteSpace(_txtAllowed.Text) ? null : AccessPolicy.ParseList(_txtAllowed.Text),
        "window");

    private readonly ComboBox _cmbProfile = new();
    private readonly ComboBox _cmbAccess = new();
    private readonly TextBox _txtBlocked = new();
    private readonly TextBox _txtAllowed = new();

    private const string AutomaticLabel = "Automatic (by client: Claude all, Cursor/VS Code/Windsurf core)";

    private int AddAccessRows(TableLayoutPanel layout, int row, McpAccessSettings access)
    {
        var heading = new Label
        {
            Text = "MCP access. Saved when you press OK and applied at once; connected clients are told to reload their tool list.",
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 16, 0, 6),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, row);
        layout.SetColumnSpan(heading, 2);
        row++;

        _cmbProfile.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbProfile.Items.AddRange([AutomaticLabel, ToolProfiles.All, ToolProfiles.Core]);
        _cmbProfile.Width = 460;
        if (string.Equals(access.ToolProfile, ToolProfiles.Automatic, StringComparison.OrdinalIgnoreCase))
            _cmbProfile.SelectedIndex = 0;
        else
            _cmbProfile.Text = access.ToolProfile;
        AddRow(layout, row++, "Tool profile", _cmbProfile);
        if (access.ToolProfileEnvironment is { } env)
            AddNote(layout, row++, $"{ToolProfiles.EnvVar}={env} is set and overrides this choice.");

        _cmbAccess.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAccess.Items.AddRange(["read — only tools that change nothing",
                                   "engineer — also change the project (no activate, login, ADS write)",
                                   "runtime — also change target systems"]);
        _cmbAccess.SelectedIndex = (int)access.Window.Level;
        _cmbAccess.Width = 460;
        AddRow(layout, row++, "Access level", _cmbAccess);

        _txtBlocked.Text = string.Join("; ", access.Window.BlockedNetIds.Order());
        _txtBlocked.Width = 460;
        AddRow(layout, row++, "Blocked NetIds", _txtBlocked);
        AddNote(layout, row++, "No runtime action on these systems; reading stays allowed. Separate with ';'. 'local' = this PC and its User Mode Runtimes.");

        _txtAllowed.Text = access.Window.AllowedNetIds is { } allowed ? string.Join("; ", allowed.Order()) : string.Empty;
        _txtAllowed.Width = 460;
        AddRow(layout, row++, "Allowed NetIds", _txtAllowed);
        AddNote(layout, row++, "When set, runtime actions only on these systems. Empty = no allow list.");

        if (access.Environment is { } envPolicy)
            AddNote(layout, row++, $"The environment sets {envPolicy.Summary()}; the stricter of both applies.");
        return row;
    }

    private static void AddNote(TableLayoutPanel layout, int row, string text)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, row);
        layout.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 0, 0, 6) }, 1, row);
    }

    private static void AddRow(TableLayoutPanel layout, int row, string label, Control editor)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 16, 6),
        }, 0, row);
        layout.Controls.Add(editor, 1, row);
    }

    private static DataGridView BuildGrid(IReadOnlyList<SettingsRow> rows)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        };
        grid.Columns.Add("setting", "Setting");
        grid.Columns.Add("value", "Value");
        grid.Columns.Add("howto", "Set by");
        grid.Columns[0].FillWeight = 28;
        grid.Columns[1].FillWeight = 36;
        grid.Columns[2].FillWeight = 36;
        foreach (var r in rows)
            grid.Rows.Add(r.Name, r.Value, r.HowToChange);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        return grid;
    }
}
