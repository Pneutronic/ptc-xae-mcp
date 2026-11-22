using PtcXaeMcp.WinForms.Localization;
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
    /// <param name="language">The language code in force, preselected in the language list.</param>
    public SettingsDialog(int port, bool darkTheme, bool rememberWindowPosition, bool diagnosticsEnabled,
        bool checkForUpdates = false, bool updateCheckLocked = false, McpAccessSettings? access = null,
        string language = HostLanguage.Fallback)
    {
        Text = Loc.Get("settings.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, access is null ? 610 : 1010);
        MinimumSize = new Size(600, access is null ? 530 : 870);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(layout, 0, Loc.Get("settings.theme"), _cmbTheme);
        _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTheme.Items.AddRange([Loc.Get("settings.theme.light"), Loc.Get("settings.theme.dark")]);
        _cmbTheme.SelectedIndex = darkTheme ? 1 : 0;
        _cmbTheme.Width = 160;

        _chkRemember.Text = Loc.Get("settings.remember");
        _chkRemember.Checked = rememberWindowPosition;
        _chkRemember.AutoSize = true;
        _chkRemember.MaximumSize = new Size(560, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 1);
        layout.Controls.Add(_chkRemember, 1, 1);

        _chkUpdates.Text = Loc.Get("settings.updates.check");
        _chkUpdates.Checked = checkForUpdates && !updateCheckLocked;
        _chkUpdates.Enabled = !updateCheckLocked;
        _chkUpdates.AutoSize = true;
        _chkUpdates.MaximumSize = new Size(560, 0);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 2);
        layout.Controls.Add(_chkUpdates, 1, 2);

        int row = 3;
        _cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var l in HostLanguage.Shipped)
            _cmbLanguage.Items.Add(l.Reviewed ? l.NativeName : Loc.Format("settings.language.unreviewed", l.NativeName));
        _cmbLanguage.SelectedIndex = Math.Max(0, IndexOfLanguage(language));
        _cmbLanguage.Width = 300;
        AddRow(layout, row++, Loc.Get("settings.language.label"), _cmbLanguage);
        AddNote(layout, row++, Loc.Get("settings.language.note"));
        if (Environment.GetEnvironmentVariable(HostLanguage.EnvVar) is { Length: > 0 } envLanguage
            && HostLanguage.Match(envLanguage) is not null)
            AddNote(layout, row++, Loc.Format("settings.language.envOverride", HostLanguage.EnvVar, envLanguage));

        if (access is not null)
            row = AddAccessRows(layout, row, access);

        var caption = new Label
        {
            Text = Loc.Get("settings.serverCaption"),
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
        var cancel = new Button { Text = Loc.Get("settings.button.cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = Loc.Get("settings.button.ok"), AutoSize = true, DialogResult = DialogResult.OK, Tag = "primary" };
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

    /// <summary>The code of the language chosen in the list.</summary>
    public string Language => HostLanguage.Shipped[Math.Max(0, _cmbLanguage.SelectedIndex)].Code;

    private readonly ComboBox _cmbLanguage = new();

    private static int IndexOfLanguage(string code)
    {
        for (int i = 0; i < HostLanguage.Shipped.Count; i++)
            if (HostLanguage.Shipped[i].Code == code) return i;
        return -1;
    }

    /// <summary>The tool profile chosen: <see cref="ToolProfiles.Automatic"/>, <c>all</c>, <c>core</c> or a toolset list.</summary>
    public string ToolProfile => _cmbProfile.SelectedIndex == 0 || string.IsNullOrWhiteSpace(_cmbProfile.Text)
        ? ToolProfiles.Automatic
        : _cmbProfile.Text.Trim();

    /// <summary>The window's access policy as edited: level, blocked NetIds, allowed NetIds (empty = no allow list).</summary>
    public AccessPolicy AccessPolicy => new(
        _cmbAccess.SelectedIndex switch { 0 => AccessLevel.Read, 1 => AccessLevel.Engineer, _ => AccessLevel.Runtime },
        AccessPolicy.ParseList(_txtBlocked.Text),
        string.IsNullOrWhiteSpace(_txtAllowed.Text) ? null : AccessPolicy.ParseList(_txtAllowed.Text),
        "window",
        string.IsNullOrWhiteSpace(_txtWriteAllow.Text) ? null : AccessPolicy.ParsePatterns(_txtWriteAllow.Text),
        string.IsNullOrWhiteSpace(_txtWriteDeny.Text) ? null : AccessPolicy.ParsePatterns(_txtWriteDeny.Text),
        _cmbConfirm.SelectedIndex switch { 1 => ConfirmMode.Remote, 2 => ConfirmMode.All, 3 => ConfirmMode.Off, _ => null });

    private readonly ComboBox _cmbProfile = new();
    private readonly ComboBox _cmbAccess = new();
    private readonly TextBox _txtBlocked = new();
    private readonly TextBox _txtAllowed = new();
    private readonly TextBox _txtWriteAllow = new();
    private readonly TextBox _txtWriteDeny = new();
    private readonly ComboBox _cmbConfirm = new();

    private static string AutomaticLabel() => Loc.Get("settings.profile.automatic");

    private int AddAccessRows(TableLayoutPanel layout, int row, McpAccessSettings access)
    {
        var heading = new Label
        {
            Text = Loc.Get("settings.access.heading"),
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Margin = new Padding(0, 16, 0, 6),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, row);
        layout.SetColumnSpan(heading, 2);
        row++;

        _cmbProfile.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbProfile.Items.AddRange([AutomaticLabel(), ToolProfiles.All, ToolProfiles.Core]);
        _cmbProfile.Width = 460;
        if (string.Equals(access.ToolProfile, ToolProfiles.Automatic, StringComparison.OrdinalIgnoreCase))
            _cmbProfile.SelectedIndex = 0;
        else
            _cmbProfile.Text = access.ToolProfile;
        AddRow(layout, row++, Loc.Get("settings.access.profile"), _cmbProfile);
        if (access.ToolProfileEnvironment is { } env)
            AddNote(layout, row++, Loc.Format("settings.access.profileEnv", ToolProfiles.EnvVar, env));

        _cmbAccess.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAccess.Items.AddRange([Loc.Get("settings.access.level.read"),
                                   Loc.Get("settings.access.level.engineer"),
                                   Loc.Get("settings.access.level.runtime")]);
        _cmbAccess.SelectedIndex = (int)access.Window.Level;
        _cmbAccess.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.level"), _cmbAccess);

        _txtBlocked.Text = string.Join("; ", access.Window.BlockedNetIds.Order());
        _txtBlocked.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.blocked"), _txtBlocked);
        AddNote(layout, row++, Loc.Get("settings.access.blocked.note"));

        _txtAllowed.Text = access.Window.AllowedNetIds is { } allowed ? string.Join("; ", allowed.Order()) : string.Empty;
        _txtAllowed.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.allowed"), _txtAllowed);
        AddNote(layout, row++, Loc.Get("settings.access.allowed.note"));

        _txtWriteAllow.Text = access.Window.AdsWriteAllow is { } writeAllow ? string.Join("; ", writeAllow.Order()) : string.Empty;
        _txtWriteAllow.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.writeAllow"), _txtWriteAllow);
        AddNote(layout, row++, Loc.Get("settings.access.writeAllow.note"));

        _txtWriteDeny.Text = access.Window.AdsWriteDeny is { } writeDeny ? string.Join("; ", writeDeny.Order()) : string.Empty;
        _txtWriteDeny.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.writeDeny"), _txtWriteDeny);
        AddNote(layout, row++, Loc.Get("settings.access.writeDeny.note"));

        _cmbConfirm.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbConfirm.Items.AddRange([Loc.Get("settings.confirm.default"),
                                    Loc.Get("settings.confirm.remote"),
                                    Loc.Get("settings.confirm.all"),
                                    Loc.Get("settings.confirm.off")]);
        _cmbConfirm.SelectedIndex = access.Window.Confirm switch { ConfirmMode.Remote => 1, ConfirmMode.All => 2, ConfirmMode.Off => 3, _ => 0 };
        _cmbConfirm.Width = 460;
        AddRow(layout, row++, Loc.Get("settings.access.confirm"), _cmbConfirm);
        AddNote(layout, row++, Loc.Get("settings.access.confirm.note"));

        if (access.Environment is { } envPolicy)
            AddNote(layout, row++, Loc.Format("settings.access.envPolicy", envPolicy.Summary()));
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
        grid.Columns.Add("setting", Loc.Get("settings.grid.setting"));
        grid.Columns.Add("value", Loc.Get("settings.grid.value"));
        grid.Columns.Add("howto", Loc.Get("settings.grid.howto"));
        grid.Columns[0].FillWeight = 28;
        grid.Columns[1].FillWeight = 36;
        grid.Columns[2].FillWeight = 36;
        foreach (var r in rows)
            grid.Rows.Add(r.Name, r.Value, r.HowToChange);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        return grid;
    }
}
