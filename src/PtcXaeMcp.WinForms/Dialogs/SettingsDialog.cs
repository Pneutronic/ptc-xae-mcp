using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.Updates;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>What the Settings dialog shows of the MCP access: the window's tool profile and policy, and what the environment sets.</summary>
internal sealed record McpAccessSettings(string ToolProfile, string? ToolProfileEnvironment, AccessPolicy Window, AccessPolicy? Environment);

/// <summary>
/// "Settings…" (issue #273). Editable: theme, language, "remember window position", the update-check
/// opt-in and the MCP access (saved by the caller on OK). Read-only: the environment-driven server settings with the
/// variable that sets each one (<see cref="SettingsInfo"/>).
/// The content sits on three tabs (General, MCP access, Server) and the OK/Cancel bar is docked at the bottom, so
/// the buttons stay visible on a small screen or at a high display scale; a tab scrolls when its content does not fit.
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly ComboBox _cmbTheme = new();
    private readonly CheckBox _chkRemember = new();
    private readonly CheckBox _chkUpdates = new();

    private readonly ComboBox _cmbLanguage = new();
    private readonly ComboBox _cmbProfile = new();
    private readonly ComboBox _cmbAccess = new();
    private readonly TextBox _txtBlocked = new();
    private readonly TextBox _txtAllowed = new();
    private readonly TextBox _txtWriteAllow = new();
    private readonly TextBox _txtWriteDeny = new();
    private readonly ComboBox _cmbConfirm = new();

    // Texts that wrap at the width of their page, and the notes that are drawn in the muted colour.
    private readonly List<(Control Text, TableLayoutPanel Host)> _wrapped = [];
    private readonly List<Label> _notes = [];

    /// <param name="diagnosticsEnabled">The running server's state (McpBridge.DiagnosticsEnabled).</param>
    /// <param name="checkForUpdates">The ui.json opt-in for the update check at start.</param>
    /// <param name="updateCheckLocked">True when the machine policy or <see cref="UpdateCheck.EnvVar"/>
    /// forbids the check: the box is shown unchecked and disabled, so nobody can opt in here.</param>
    /// <param name="access">The tool profile and access policy to edit; null leaves the tab out.</param>
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

        // Never taller or wider than the screen the dialog opens on: the buttons must stay reachable.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(860, area.Width - 80), Math.Min(access is null ? 520 : 680, area.Height - 140));
        MinimumSize = new Size(Math.Min(640, Width), Math.Min(460, Height));

        var tabs = new ThemedTabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralPage(darkTheme, rememberWindowPosition, checkForUpdates, updateCheckLocked, language));
        if (access is not null)
            tabs.TabPages.Add(BuildAccessPage(access));
        tabs.TabPages.Add(BuildServerPage(port, diagnosticsEnabled, checkForUpdates));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        var cancel = new Button { Text = Loc.Get("settings.button.cancel"), AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = Loc.Get("settings.button.ok"), AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.OK, Tag = "primary" };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(buttons, 0, 1);
        Controls.Add(root);

        ThemeManager.ApplyDialog(this);
        foreach (var note in _notes)
            note.ForeColor = ThemeManager.Palette.TextMuted;
    }

    public bool DarkTheme => _cmbTheme.SelectedIndex == 1;

    public bool RememberWindowPosition => _chkRemember.Checked;

    public bool CheckForUpdates => _chkUpdates.Checked;

    /// <summary>The code of the language chosen in the list.</summary>
    public string Language => HostLanguage.Shipped[Math.Max(0, _cmbLanguage.SelectedIndex)].Code;

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

    private static string AutomaticLabel() => Loc.Get("settings.profile.automatic");

    // ---- pages ---------------------------------------------------------------------------------------------------

    private TabPage BuildGeneralPage(bool darkTheme, bool rememberWindowPosition, bool checkForUpdates,
        bool updateCheckLocked, string language)
    {
        var page = NewScrollingPage(Loc.Get("settings.tab.general"), out var layout);

        _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbTheme.Items.AddRange([Loc.Get("settings.theme.light"), Loc.Get("settings.theme.dark")]);
        _cmbTheme.SelectedIndex = darkTheme ? 1 : 0;
        _cmbTheme.Width = 220;
        AddField(layout, Loc.Get("settings.theme"), _cmbTheme, null, fill: false);

        _cmbLanguage.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var l in HostLanguage.Shipped)
            _cmbLanguage.Items.Add(l.Reviewed ? l.NativeName : Loc.Format("settings.language.unreviewed", l.NativeName));
        _cmbLanguage.SelectedIndex = Math.Max(0, IndexOfLanguage(language));
        _cmbLanguage.Width = 360;
        FitDropDown(_cmbLanguage);
        AddField(layout, Loc.Get("settings.language.label"), _cmbLanguage, Loc.Get("settings.language.note"), fill: false);
        if (Environment.GetEnvironmentVariable(HostLanguage.EnvVar) is { Length: > 0 } envLanguage
            && HostLanguage.Match(envLanguage) is not null)
            AddNote(layout, Loc.Format("settings.language.envOverride", HostLanguage.EnvVar, envLanguage));

        _chkRemember.Text = Loc.Get("settings.remember");
        _chkRemember.Checked = rememberWindowPosition;
        AddCheckBox(layout, _chkRemember, 18);

        _chkUpdates.Text = Loc.Get("settings.updates.check");
        _chkUpdates.Checked = checkForUpdates && !updateCheckLocked;
        _chkUpdates.Enabled = !updateCheckLocked;
        AddCheckBox(layout, _chkUpdates, 6);
        return page;
    }

    private TabPage BuildAccessPage(McpAccessSettings access)
    {
        var page = NewScrollingPage(Loc.Get("settings.tab.access"), out var layout);
        AddNote(layout, Loc.Get("settings.access.heading"));

        _cmbProfile.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbProfile.Items.AddRange([AutomaticLabel(), ToolProfiles.All, ToolProfiles.Core]);
        if (string.Equals(access.ToolProfile, ToolProfiles.Automatic, StringComparison.OrdinalIgnoreCase))
            _cmbProfile.SelectedIndex = 0;
        else
            _cmbProfile.Text = access.ToolProfile;
        FitDropDown(_cmbProfile);
        AddField(layout, Loc.Get("settings.access.profile"), _cmbProfile, null);
        if (access.ToolProfileEnvironment is { } env)
            AddNote(layout, Loc.Format("settings.access.profileEnv", ToolProfiles.EnvVar, env));

        _cmbAccess.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbAccess.Items.AddRange([Loc.Get("settings.access.level.read"),
                                   Loc.Get("settings.access.level.engineer"),
                                   Loc.Get("settings.access.level.runtime")]);
        _cmbAccess.SelectedIndex = (int)access.Window.Level;
        FitDropDown(_cmbAccess);
        AddField(layout, Loc.Get("settings.access.level"), _cmbAccess, null);

        // The human confirmation is the setting changed most often: right under the level, above the four lists that
        // are rarely filled in, so it is visible without scrolling.
        _cmbConfirm.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbConfirm.Items.AddRange([Loc.Get("settings.confirm.default"),
                                    Loc.Get("settings.confirm.remote"),
                                    Loc.Get("settings.confirm.all"),
                                    Loc.Get("settings.confirm.off")]);
        _cmbConfirm.SelectedIndex = access.Window.Confirm switch { ConfirmMode.Remote => 1, ConfirmMode.All => 2, ConfirmMode.Off => 3, _ => 0 };
        FitDropDown(_cmbConfirm);
        AddField(layout, Loc.Get("settings.access.confirm"), _cmbConfirm, Loc.Get("settings.access.confirm.note"));

        _txtBlocked.Text = string.Join("; ", access.Window.BlockedNetIds.Order());
        AddField(layout, Loc.Get("settings.access.blocked"), _txtBlocked, Loc.Get("settings.access.blocked.note"));

        _txtAllowed.Text = access.Window.AllowedNetIds is { } allowed ? string.Join("; ", allowed.Order()) : string.Empty;
        AddField(layout, Loc.Get("settings.access.allowed"), _txtAllowed, Loc.Get("settings.access.allowed.note"));

        _txtWriteAllow.Text = access.Window.AdsWriteAllow is { } writeAllow ? string.Join("; ", writeAllow.Order()) : string.Empty;
        AddField(layout, Loc.Get("settings.access.writeAllow"), _txtWriteAllow, Loc.Get("settings.access.writeAllow.note"));

        _txtWriteDeny.Text = access.Window.AdsWriteDeny is { } writeDeny ? string.Join("; ", writeDeny.Order()) : string.Empty;
        AddField(layout, Loc.Get("settings.access.writeDeny"), _txtWriteDeny, Loc.Get("settings.access.writeDeny.note"));

        if (access.Environment is { } envPolicy)
            AddNote(layout, Loc.Format("settings.access.envPolicy", envPolicy.Summary()));
        return page;
    }

    private TabPage BuildServerPage(int port, bool diagnosticsEnabled, bool checkForUpdates)
    {
        var page = new TabPage(Loc.Get("settings.tab.server"));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 12, 16, 12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.SizeChanged += (_, _) => Rewrap(layout);
        page.Controls.Add(layout);

        var caption = new Label { Text = Loc.Get("settings.serverCaption"), AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        _wrapped.Add((caption, layout));
        layout.Controls.Add(caption, 0, 0);

        var grid = BuildGrid(SettingsInfo.Rows(port, diagnosticsEnabled, Environment.GetEnvironmentVariable,
            UpdateCheck.DescribeState(checkForUpdates, UpdateCheck.IsDisabledByPolicy(), Environment.GetEnvironmentVariable)));
        layout.Controls.Add(grid, 0, 1);
        return page;
    }

    // ---- building blocks -----------------------------------------------------------------------------------------

    /// <summary>A tab page that scrolls when its content is taller than the window; the content is a single column.</summary>
    private TabPage NewScrollingPage(string title, out TableLayoutPanel layout)
    {
        var page = new TabPage(title) { AutoScroll = true };
        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(16, 8, 16, 16),
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.SizeChanged += (_, _) => Rewrap(host);
        page.Controls.Add(host);
        layout = host;
        return page;
    }

    /// <summary>Lets every wrapping text of <paramref name="host"/> fill the width of the page.</summary>
    private void Rewrap(TableLayoutPanel host)
    {
        foreach (var (text, owner) in _wrapped)
        {
            if (owner != host) continue;
            int width = host.ClientSize.Width - host.Padding.Horizontal - text.Margin.Horizontal;
            if (width < 160) continue;
            text.MaximumSize = new Size(width, 0);
        }
    }

    private static int NextRow(TableLayoutPanel layout)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return layout.RowStyles.Count - 1;
    }

    /// <summary>A label above its editor and, when given, a muted note below. The editor fills the page unless <paramref name="fill"/> is false.</summary>
    private void AddField(TableLayoutPanel layout, string label, Control editor, string? note, bool fill = true)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 14, 0, 4) }, 0, NextRow(layout));
        editor.Margin = new Padding(0);
        editor.Anchor = fill ? AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Left;
        layout.Controls.Add(editor, 0, NextRow(layout));
        if (note is not null)
            AddNote(layout, note);
    }

    private void AddNote(TableLayoutPanel layout, string text)
    {
        var note = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        _wrapped.Add((note, layout));
        _notes.Add(note);
        layout.Controls.Add(note, 0, NextRow(layout));
    }

    private void AddCheckBox(TableLayoutPanel layout, CheckBox box, int topMargin)
    {
        box.AutoSize = true;
        box.Margin = new Padding(0, topMargin, 0, 0);
        _wrapped.Add((box, layout));
        layout.Controls.Add(box, 0, NextRow(layout));
    }

    /// <summary>Widens the drop-down list so the longest entry shows in full even when the closed box is narrower.</summary>
    private static void FitDropDown(ComboBox combo)
    {
        int widest = combo.Items.Cast<object>()
            .Select(i => TextRenderer.MeasureText(i.ToString() ?? string.Empty, combo.Font).Width)
            .DefaultIfEmpty(0).Max();
        combo.DropDownWidth = Math.Max(combo.Width, widest + SystemInformation.VerticalScrollBarWidth + 8);
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
