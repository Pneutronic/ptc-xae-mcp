using System.Text.Json;
using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.Theme;

internal enum ThemeMode { Light, Dark }

internal sealed class ThemePalette
{
    public Color Background { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceAlt { get; init; }
    public Color Border { get; init; }
    public Color Text { get; init; }
    public Color TextMuted { get; init; }
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color AccentText { get; init; }
    public Color Ok { get; init; }
    public Color Warn { get; init; }
    public Color Error { get; init; }
    public Color Inactive { get; init; }
    public Color LogBackground { get; init; }
    public Color LogText { get; init; }
}

internal static class ThemeManager
{
    public static ThemeMode Current { get; private set; } = ThemeMode.Light;

    public static event Action? Changed;

    public static ThemePalette Palette => Current == ThemeMode.Dark ? Dark : Light;

    public static readonly ThemePalette Light = new()
    {
        Background  = Color.FromArgb(0xF6, 0xF8, 0xFA),
        Surface     = Color.White,
        SurfaceAlt  = Color.FromArgb(0xF0, 0xF2, 0xF5),
        Border      = Color.FromArgb(0xE1, 0xE4, 0xE8),
        Text        = Color.FromArgb(0x1F, 0x23, 0x28),
        TextMuted   = Color.FromArgb(0x6E, 0x76, 0x81),
        Accent      = Color.FromArgb(0x0E, 0x63, 0x9C),
        AccentHover = Color.FromArgb(0x12, 0x74, 0xB5),
        AccentText  = Color.White,
        Ok          = Color.FromArgb(0x2D, 0xA4, 0x4E),
        Warn        = Color.FromArgb(0xBF, 0x87, 0x00),
        Error       = Color.FromArgb(0xCF, 0x22, 0x2E),
        Inactive    = Color.FromArgb(0x8C, 0x95, 0x9F),
        LogBackground = Color.FromArgb(0xFA, 0xFB, 0xFC),
        LogText     = Color.FromArgb(0x1F, 0x23, 0x28),
    };

    public static readonly ThemePalette Dark = new()
    {
        Background  = Color.FromArgb(0x0D, 0x11, 0x17),
        Surface     = Color.FromArgb(0x16, 0x1B, 0x22),
        SurfaceAlt  = Color.FromArgb(0x21, 0x26, 0x2D),
        Border      = Color.FromArgb(0x30, 0x36, 0x3D),
        Text        = Color.FromArgb(0xE6, 0xED, 0xF3),
        TextMuted   = Color.FromArgb(0x8B, 0x94, 0x9E),
        Accent      = Color.FromArgb(0x29, 0x8E, 0xCC),
        AccentHover = Color.FromArgb(0x3D, 0xA3, 0xE0),
        AccentText  = Color.White,
        Ok          = Color.FromArgb(0x3F, 0xB9, 0x50),
        Warn        = Color.FromArgb(0xD2, 0x9C, 0x22),
        Error       = Color.FromArgb(0xF8, 0x51, 0x49),
        Inactive    = Color.FromArgb(0x6E, 0x76, 0x81),
        LogBackground = Color.FromArgb(0x0A, 0x0D, 0x12),
        LogText     = Color.FromArgb(0xC9, 0xD1, 0xD9),
    };

    public static void Set(ThemeMode mode)
    {
        if (Current == mode) return;
        Current = mode;
        Save();
        Changed?.Invoke();
    }

    public static void Toggle() => Set(Current == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);

    /// <summary>The settings read from / written to ui.json (theme, window position; issue #273).</summary>
    public static UiSettings Settings { get; private set; } = new();

    public static void LoadFromDisk()
    {
        Settings = UiSettings.Load(SettingsPath);
        Current = Settings.DarkTheme ? ThemeMode.Dark : ThemeMode.Light;
        // The host language is part of the same file: resolved here so every text built after this call has it.
        HostLanguage.Apply(HostLanguage.Resolve(Environment.GetEnvironmentVariable, Settings.Language,
            HostLanguage.ReadInstallerSeed(), System.Globalization.CultureInfo.CurrentUICulture.Name, out _));
    }

    /// <summary>Writes <see cref="Settings"/> (with the current theme) to ui.json; best effort.</summary>
    public static void Save()
    {
        Settings.DarkTheme = Current == ThemeMode.Dark;
        Settings.Save(SettingsPath);
    }

    internal static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "ptc-xae-mcp", "ui.json");

    public static void Apply(Control root)
    {
        var p = Palette;
        ApplyRecursive(root, p);
    }

    /// <summary>
    /// Themes a dialog from its constructor: <see cref="Apply"/> plus the title bar and the
    /// native parts (scroll bars, check boxes, spin buttons) once its handle exists.
    /// </summary>
    public static void ApplyDialog(Form dialog)
    {
        Apply(dialog);
        NativeDarkMode.ApplyWhenCreated(dialog);
    }

    private static void ApplyRecursive(Control c, ThemePalette p)
    {
        switch (c)
        {
            case Form form:
                form.BackColor = p.Background;
                form.ForeColor = p.Text;
                break;
            case TabControl tab:
                tab.BackColor = p.Background;
                tab.ForeColor = p.Text;
                StyleTabControl(tab, p);
                break;
            case TabPage page:
                page.BackColor = p.Background;
                page.ForeColor = p.Text;
                page.UseVisualStyleBackColor = false;
                break;
            case GroupBox gb:
                gb.BackColor = p.Background;
                gb.ForeColor = p.Text;
                break;
            case Button btn:
                StyleButton(btn, p);
                break;
            case RichTextBox rtb:
                rtb.BackColor = p.LogBackground;
                rtb.ForeColor = p.LogText;
                rtb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case TextBox txt:
                txt.BackColor = p.Surface;
                txt.ForeColor = p.Text;
                txt.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cmb:
                cmb.BackColor = p.Surface;
                cmb.ForeColor = p.Text;
                // A drop-down list is drawn by the (dark) visual style, see NativeDarkMode; Flat kept
                // a light arrow button in the dark theme.
                cmb.FlatStyle = cmb.DropDownStyle == ComboBoxStyle.DropDownList ? FlatStyle.Standard : FlatStyle.Flat;
                break;
            case CheckBox chk:
                chk.BackColor = p.Background;
                chk.ForeColor = p.Text;
                // Standard: the box comes from the visual style, which NativeDarkMode switches to
                // dark. Flat filled it with LightLight(BackColor) — a light box with a light check
                // mark in the dark theme.
                if (chk.Appearance == Appearance.Normal)
                    chk.FlatStyle = FlatStyle.Standard;
                break;
            case UpDownBase upDown:
                // The inner edit takes its colours from the UpDownBase and the spin buttons get the
                // native dark theme (NativeDarkMode); not recursed, so the edit gets no second border.
                upDown.BackColor = p.Surface;
                upDown.ForeColor = p.Text;
                upDown.BorderStyle = BorderStyle.FixedSingle;
                return;
            case ListBox lb:
                lb.BackColor = p.Surface;
                lb.ForeColor = p.Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case TreeView tree:
                tree.BackColor = p.Surface;
                tree.ForeColor = p.Text;
                tree.BorderStyle = BorderStyle.FixedSingle;
                break;
            case DataGridView grid:
                grid.BackgroundColor = p.Surface;
                grid.ForeColor = p.Text;
                grid.GridColor = p.Border;
                grid.BorderStyle = BorderStyle.FixedSingle;
                grid.EnableHeadersVisualStyles = false;
                grid.ColumnHeadersDefaultCellStyle.BackColor = p.SurfaceAlt;
                grid.ColumnHeadersDefaultCellStyle.ForeColor = p.Text;
                grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = p.SurfaceAlt;
                grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = p.Text;
                grid.DefaultCellStyle.BackColor = p.Surface;
                grid.DefaultCellStyle.ForeColor = p.Text;
                grid.DefaultCellStyle.SelectionBackColor = p.Accent;
                grid.DefaultCellStyle.SelectionForeColor = p.AccentText;
                break;
            case SplitContainer split:
                split.BackColor = p.Border;
                split.Panel1.BackColor = p.Background;
                split.Panel2.BackColor = p.Background;
                break;
            case Panel panel:
                if (IsInHeader(panel))
                {
                    panel.BackColor = p.Surface;
                }
                else
                {
                    panel.BackColor = p.Background;
                    panel.ForeColor = p.Text;
                }
                break;
            case Label lbl:
                // A semantic colour (Ok/Warn/Error/Inactive of either palette) is carried over to
                // the new palette instead of being reset, so a theme switch keeps status meaning.
                // Dot labels use BackColor as their visual color — don't reset it.
                lbl.BackColor = RemapSemantic(lbl.BackColor, p)
                                ?? (IsDotIndicator(lbl) ? lbl.BackColor : Color.Transparent);
                lbl.ForeColor = RemapSemantic(lbl.ForeColor, p)
                                ?? (IsStatusIndicator(lbl) ? lbl.ForeColor : p.Text);
                break;
        }

        foreach (Control child in c.Controls)
            ApplyRecursive(child, p);
    }

    /// <summary>
    /// Maps a semantic colour of either palette to the same role in <paramref name="p"/>;
    /// <c>null</c> when <paramref name="c"/> is not a semantic palette colour.
    /// </summary>
    internal static Color? RemapSemantic(Color c, ThemePalette p)
    {
        foreach (var src in (ThemePalette[])[Light, Dark])
        {
            if (c == src.Ok) return p.Ok;
            if (c == src.Warn) return p.Warn;
            if (c == src.Error) return p.Error;
            if (c == src.Inactive) return p.Inactive;
        }
        return null;
    }

    private static bool IsStatusIndicator(Label lbl) =>
        lbl.Name is "_lblXaeState" or "_lblProjectState"
                 or "_lblXaeDot" or "_lblProjectDot" or "_lblMcpDot";

    /// <summary>The header (<c>_pnlHeader</c>) and every layout panel inside it share the
    /// header's surface colour.</summary>
    private static bool IsInHeader(Control c)
    {
        for (Control? cur = c; cur is not null; cur = cur.Parent)
            if (cur.Name == "_pnlHeader") return true;
        return false;
    }

    /// <summary>
    /// Colours a drop-down menu (the header menu) from the palette. Menus are not part of the
    /// control tree <see cref="Apply"/> walks, so the owner calls this on every theme apply.
    /// </summary>
    public static void ApplyMenu(ToolStrip menu)
    {
        var p = Palette;
        menu.BackColor = p.Surface;
        menu.ForeColor = p.Text;
        // The default renderer draws hover, border and separators from the light system colours,
        // which made the selected item unreadable in the dark theme.
        menu.Renderer = new ToolStripProfessionalRenderer(new PaletteColorTable(p));
        foreach (ToolStripItem item in menu.Items)
        {
            item.BackColor = p.Surface;
            item.ForeColor = p.Text;
        }
    }

    /// <summary>Menu colours from the theme palette instead of the system colour table.</summary>
    private sealed class PaletteColorTable : ProfessionalColorTable
    {
        private readonly ThemePalette _p;

        public PaletteColorTable(ThemePalette p)
        {
            _p = p;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => _p.Surface;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => _p.Accent;
        public override Color MenuItemSelected => _p.SurfaceAlt;
        public override Color MenuItemSelectedGradientBegin => _p.SurfaceAlt;
        public override Color MenuItemSelectedGradientEnd => _p.SurfaceAlt;
        public override Color MenuItemPressedGradientBegin => _p.SurfaceAlt;
        public override Color MenuItemPressedGradientEnd => _p.SurfaceAlt;
        public override Color ImageMarginGradientBegin => _p.Surface;
        public override Color ImageMarginGradientMiddle => _p.Surface;
        public override Color ImageMarginGradientEnd => _p.Surface;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Surface;
    }

    private static bool IsDotIndicator(Label lbl) =>
        lbl.Name is "_lblXaeDot" or "_lblProjectDot" or "_lblMcpDot";

    private static readonly HashSet<TabControl> _themedTabs = [];

    private static void StyleTabControl(TabControl tab, ThemePalette p)
    {
        // ThemedTabControl paints strip, tabs and frame itself from the palette.
        if (tab is ThemedTabControl)
        {
            tab.Invalidate();
            return;
        }
        tab.DrawMode = TabDrawMode.OwnerDrawFixed;
        if (_themedTabs.Add(tab))
        {
            tab.DrawItem += (sender, e) =>
            {
                var tc = (TabControl)sender!;
                var pal = Palette;
                bool selected = e.Index == tc.SelectedIndex;
                Color bg = selected ? pal.Background : pal.SurfaceAlt;
                Color fg = selected ? pal.Text : pal.TextMuted;
                using var bgBrush = new SolidBrush(bg);
                e.Graphics.FillRectangle(bgBrush, e.Bounds);
                if (!selected)
                {
                    using var borderPen = new Pen(pal.Border);
                    e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
                string text = tc.TabPages[e.Index].Text;
                using var fgBrush = new SolidBrush(fg);
                var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                e.Graphics.DrawString(text, tc.Font, fgBrush, e.Bounds, fmt);
            };
        }
        tab.Invalidate();
    }

    private static void StyleButton(Button btn, ThemePalette p)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.UseVisualStyleBackColor = false;
        btn.FlatAppearance.BorderSize = 1;

        bool primary = btn.Tag as string == "primary";
        if (btn.Tag as string == "danger")
        {
            // Panic buttons (IBN ABORT / RELEASE-ALL) must stay red in both themes.
            btn.BackColor = p.Error;
            btn.ForeColor = Color.White;
            btn.FlatAppearance.BorderColor = p.Error;
            btn.FlatAppearance.MouseOverBackColor = p.Error;
        }
        else if (primary)
        {
            btn.BackColor = p.Accent;
            btn.ForeColor = p.AccentText;
            btn.FlatAppearance.BorderColor = p.Accent;
            btn.FlatAppearance.MouseOverBackColor = p.AccentHover;
        }
        else
        {
            btn.BackColor = p.Surface;
            btn.ForeColor = p.Text;
            btn.FlatAppearance.BorderColor = p.Border;
            btn.FlatAppearance.MouseOverBackColor = p.SurfaceAlt;
        }
    }
}
