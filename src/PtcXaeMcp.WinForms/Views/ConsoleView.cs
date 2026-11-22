using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Theme;
using PtcXaeMcp.WinForms.ToolConsole;

namespace PtcXaeMcp.WinForms.Views;

/// <summary>
/// The "Console" view (issue #271): a generic tool console. The tool list and each tool's form
/// come from the server's own <c>tools/list</c> (via an in-process MCP client), so a manual call
/// takes the same path as one from an AI client. Offers "Run again" from the Activity view,
/// favourites with arguments, and the raw request/response for issue reports.
/// The schema, argument, grouping and preset logic lives in <c>ToolConsole/</c> (WinForms-free).
/// </summary>
internal sealed class ConsoleView : UserControl
{
    private sealed record FieldEditor(ToolField Field, Control Control, Func<string> Get, Action<string> Set);

    private readonly TextBox _txtSearch;
    private readonly TreeView _tree;
    private readonly Button _btnReload;
    private readonly Label _lblToolName;
    private readonly TextBox _txtDescription;
    private readonly Panel _formHost;
    private readonly Label _lblBanner;
    private readonly List<Font> _formFonts = [];
    private bool _disposed;
    private readonly Button _btnRun;
    private readonly Button _btnSaveFavourite;
    private readonly Button _btnRemoveFavourite;
    private readonly CheckBox _chkRaw;
    private readonly Button _btnCopy;
    private readonly Label _lblStatus;
    private readonly TextBox _txtOutput;
    private readonly ToolTip _tips = new();

    private ToolConsoleClient? _client;
    private PresetStore _presets = new(PresetStore.DefaultPath());
    private IReadOnlyList<ToolPreset> _presetList = [];
    private IReadOnlyList<ToolSummary> _tools = [];
    private ToolSummary? _selected;
    private IReadOnlyList<ToolField> _fields = [];
    private readonly List<FieldEditor> _editors = [];
    private ToolPreset? _selectedPreset;
    private string _resultText = string.Empty;
    private string _rawText = string.Empty;
    private bool _toolsLoaded;
    private bool _loading;
    private bool _running;
    private bool _suppressTreeEvent;
    private bool _toolNameIsPlaceholder = true;
    private (string Tool, string Arguments)? _pendingLoad;
    private CancellationTokenSource? _runCts;

    public ConsoleView()
    {
        Name = "_consoleView";

        var split = new SplitContainer
        {
            Name = "_splitConsole",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
        };
        // SplitterDistance set here is clamped while the container still has no width (the
        // tree ended up ~115 px wide). Set it once the view has its real size.
        bool splitterPlaced = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitterPlaced || split.Width < 600) return;
            splitterPlaced = true;
            split.Panel1MinSize = 200;
            split.SplitterDistance = Math.Clamp(split.Width / 3, 260, 380);
        };

        // ── Left: search + tool tree ─────────────────────────────────────
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
        };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _txtSearch = new TextBox
        {
            Name = "_txtConsoleSearch",
            Dock = DockStyle.Fill,
            PlaceholderText = Loc.Get("console.search.placeholder"),
        };
        _txtSearch.TextChanged += (_, _) => RebuildTree();
        _btnReload = new Button { Name = "_btnConsoleReload", Text = Loc.Get("console.reload"), AutoSize = true };
        _btnReload.Click += (_, _) => FireAndForget(async () =>
        {
            await LoadToolsAsync(force: true);
            if (!_disposed && !IsDisposed) ReloadPresets(); // after the tools: keeps a read warning visible
        });

        _tree = new TreeView
        {
            Name = "_treeConsoleTools",
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowLines = false,
        };
        _tree.AfterSelect += OnTreeSelect;

        left.Controls.Add(_txtSearch, 0, 0);
        left.Controls.Add(_btnReload, 1, 0);
        left.Controls.Add(_tree, 0, 1);
        left.SetColumnSpan(_tree, 2);
        split.Panel1.Controls.Add(left);

        // ── Right: header, description, form, buttons, result ────────────
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(8, 0, 0, 0),
        };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // banner
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

        _lblToolName = new Label
        {
            Name = "_lblConsoleToolName",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            Text = Loc.Get("console.tool.placeholder"),
        };
        _txtDescription = new TextBox
        {
            Name = "_txtConsoleDescription",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 64,
            BorderStyle = BorderStyle.None,
        };
        _formHost = new Panel { Name = "_pnlConsoleForm", Dock = DockStyle.Fill, AutoScroll = true };
        _lblBanner = new Label
        {
            Name = "_lblConsoleBanner",
            Dock = DockStyle.Fill,
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Visible = false,
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = ThemeManager.Palette.Warn,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 4),
        };
        _btnRun = new Button { Name = "_btnConsoleRun", Text = Loc.Get("console.run"), AutoSize = true, Enabled = false };
        _btnRun.Click += (_, _) => FireAndForget(RunAsync);
        _btnSaveFavourite = new Button
        {
            Name = "_btnConsoleSaveFavourite",
            Text = Loc.Get("console.favourite.save"),
            AutoSize = true,
            Enabled = false,
        };
        _btnSaveFavourite.Click += (_, _) => SaveFavourite();
        _btnRemoveFavourite = new Button
        {
            Name = "_btnConsoleRemoveFavourite",
            Text = Loc.Get("console.favourite.remove"),
            AutoSize = true,
            Visible = false,
        };
        _btnRemoveFavourite.Click += (_, _) => RemoveFavourite();
        _chkRaw = new CheckBox
        {
            Name = "_chkConsoleRaw",
            Text = Loc.Get("console.raw"),
            AutoSize = true,
            Padding = new Padding(8, 4, 8, 0),
        };
        _chkRaw.CheckedChanged += (_, _) => ShowOutput();
        _btnCopy = new Button { Name = "_btnConsoleCopy", Text = Loc.Get("console.copy"), AutoSize = true };
        _btnCopy.Click += (_, _) => CopyOutput();
        buttons.Controls.AddRange([_btnRun, _btnSaveFavourite, _btnRemoveFavourite, _chkRaw, _btnCopy]);

        _lblStatus = new Label
        {
            Name = "_lblConsoleStatus",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = string.Empty,
        };
        _txtOutput = new TextBox
        {
            Name = "_txtConsoleOutput",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };

        right.Controls.Add(_lblToolName, 0, 0);
        right.Controls.Add(_txtDescription, 0, 1);
        right.Controls.Add(_lblBanner, 0, 2);
        right.Controls.Add(_formHost, 0, 3);
        right.Controls.Add(buttons, 0, 4);
        right.Controls.Add(_lblStatus, 0, 5);
        right.Controls.Add(_txtOutput, 0, 6);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);

        ReloadPresets();
        // Lazy: connect when the tab is first shown, not while the form is still starting up.
        VisibleChanged += (_, _) => FireAndForget(async () =>
        {
            if (Visible && !_toolsLoaded && !_loading) await LoadToolsAsync(force: false);
        });
    }

    /// <summary>
    /// Runs an async body from an event handler: nothing may escape an async void (it would
    /// crash the message loop), and a disposed view is not written to.
    /// </summary>
    private async void FireAndForget(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (ObjectDisposedException) when (_disposed || IsDisposed)
        {
            // The view was closed while the call was in flight.
        }
        catch (Exception ex)
        {
            if (!_disposed && !IsDisposed) SetStatus(Loc.Format("console.status.unexpected", ex.Message), StatusKind.Error);
        }
    }

    /// <summary>
    /// Sets every static text again after a language change. The form generated for the selected tool and the
    /// last status line keep their language until the next selection or action (rebuilding the form would drop
    /// what the user typed); the tool tree (group titles, Favourites) is rebuilt.
    /// </summary>
    public void ApplyLanguage()
    {
        _txtSearch.PlaceholderText = Loc.Get("console.search.placeholder");
        _btnReload.Text = Loc.Get("console.reload");
        _btnRun.Text = Loc.Get("console.run");
        _btnSaveFavourite.Text = Loc.Get("console.favourite.save");
        _btnRemoveFavourite.Text = Loc.Get("console.favourite.remove");
        _chkRaw.Text = Loc.Get("console.raw");
        _btnCopy.Text = Loc.Get("console.copy");
        if (_toolNameIsPlaceholder) _lblToolName.Text = Loc.Get("console.tool.placeholder");
        if (_toolsLoaded) RebuildTree();
    }

    /// <summary>Points the view at the in-process server. Does not connect yet.</summary>
    public void Attach(int port, PresetStore? presets = null)
    {
        _client = new ToolConsoleClient(port);
        if (presets is not null)
        {
            _presets = presets;
            ReloadPresets();
        }
    }

    /// <summary>
    /// Loads a tool and arguments into the form ("Run again"). Waits for the tool list if it is
    /// not loaded yet. The call is not started: the user reviews the arguments and presses Run.
    /// </summary>
    public async Task LoadCallAsync(string tool, string argumentsJson)
    {
        _pendingLoad = (tool, argumentsJson);
        if (!_toolsLoaded) await LoadToolsAsync(force: false);
        ApplyPendingLoad();
    }

    // ── Tool list ────────────────────────────────────────────────────────

    private async Task LoadToolsAsync(bool force)
    {
        if (_client is null || _loading) return;
        if (_toolsLoaded && !force) return;
        _loading = true;
        _btnReload.Enabled = false;
        SetStatus(Loc.Get("console.status.loading"), StatusKind.Info);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _tools = await _client.ListToolsAsync(cts.Token);
            if (_disposed || IsDisposed) return;
            _toolsLoaded = true;
            SetStatus(Loc.Format("console.status.loaded", _tools.Count), StatusKind.Info);
            RebuildTree();
        }
        catch (Exception ex) when (!_disposed && !IsDisposed)
        {
            _toolsLoaded = false;
            SetStatus(_client.NotReachableMessage(ex), StatusKind.Error);
        }
        finally
        {
            _loading = false;
            if (!_disposed && !IsDisposed) _btnReload.Enabled = true;
        }
        if (!_disposed && !IsDisposed) ApplyPendingLoad();
    }

    private void RebuildTree()
    {
        string? filter = _txtSearch.Text;
        _suppressTreeEvent = true;
        _tree.BeginUpdate();
        try
        {
            string? keepTool = _selectedPreset is null ? _selected?.Name : null;
            _tree.Nodes.Clear();

            var favs = _presetList
                .Where(p => string.IsNullOrWhiteSpace(filter) ||
                            p.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            p.Tool.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (favs.Count > 0)
            {
                var favNode = new TreeNode(Loc.Format("console.tree.favourites", favs.Count));
                foreach (var p in favs)
                    favNode.Nodes.Add(new TreeNode(p.Name) { Tag = p, ToolTipText = p.Tool });
                _tree.Nodes.Add(favNode);
                favNode.Expand();
            }

            foreach (var g in ToolGrouping.Build(_tools, filter))
            {
                var node = new TreeNode($"{ToolGrouping.DisplayTitle(g.Title)} ({g.Tools.Count})");
                foreach (var name in g.Tools)
                    node.Nodes.Add(new TreeNode(name) { Tag = name });
                _tree.Nodes.Add(node);
                if (!string.IsNullOrWhiteSpace(filter)) node.Expand();
            }

            if (keepTool is not null) SelectToolNode(keepTool);
        }
        finally
        {
            _tree.EndUpdate();
            _suppressTreeEvent = false;
        }
    }

    private void SelectToolNode(string tool)
    {
        foreach (TreeNode group in _tree.Nodes)
            foreach (TreeNode n in group.Nodes)
                if (n.Tag is string s && s == tool)
                {
                    group.Expand();
                    _tree.SelectedNode = n;
                    n.EnsureVisible();
                    return;
                }
    }

    private void OnTreeSelect(object? sender, TreeViewEventArgs e)
    {
        if (_suppressTreeEvent) return;
        switch (e.Node?.Tag)
        {
            case string tool:
                _selectedPreset = null;
                SelectTool(tool, null);
                break;
            case ToolPreset preset:
                _selectedPreset = preset;
                SelectTool(preset.Tool, preset.Arguments);
                break;
        }
        _btnRemoveFavourite.Visible = _selectedPreset is not null;
    }

    private void ApplyPendingLoad()
    {
        if (_pendingLoad is not { } pending || !_toolsLoaded) return;
        _pendingLoad = null;
        _selectedPreset = null;
        _btnRemoveFavourite.Visible = false;
        _suppressTreeEvent = true;
        try
        {
            if (_txtSearch.Text.Length > 0) _txtSearch.Text = string.Empty; // the tool must be in the tree
            SelectToolNode(pending.Tool);
        }
        finally
        {
            _suppressTreeEvent = false;
        }
        SelectTool(pending.Tool, pending.Arguments, recorded: true);
    }

    // ── Form ─────────────────────────────────────────────────────────────

    private void SelectTool(string toolName, string? argumentsJson, bool recorded = false)
    {
        _selected = _tools.FirstOrDefault(t => t.Name == toolName);
        _editors.Clear();
        _formHost.SuspendLayout();
        ClearForm();
        ShowBanner(null);

        if (_selected is null)
        {
            _toolNameIsPlaceholder = false;
            _lblToolName.Text = toolName;
            _txtDescription.Text = _toolsLoaded
                ? Loc.Get("console.tool.notoffered")
                : Loc.Get("console.tool.notloaded");
            _btnRun.Enabled = false;
            _btnSaveFavourite.Enabled = false;
            _formHost.ResumeLayout();
            return;
        }

        _toolNameIsPlaceholder = false;
        _lblToolName.Text = _selected.Name;
        _txtDescription.Text = _selected.Description;
        _fields = ToolFormModel.FromSchema(_selected.InputSchema);
        BuildForm();

        var initial = ToolFormModel.InitialValues(_fields).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (argumentsJson is not null)
        {
            var parsed = ToolArgumentBuilder.TryParseArguments(argumentsJson);
            if (parsed is null)
            {
                ShowBanner(Loc.Get("console.banner.notjson"));
            }
            else
            {
                var replay = ToolArgumentBuilder.Replay(_fields, parsed);
                foreach (var kv in replay.Values) initial[kv.Key] = kv.Value;
                // An Activity row records the tool's trace summary, which need not be its real
                // arguments, so any gap warns; a favourite was saved from this form, so only a mismatch does.
                if (recorded ? replay.Incomplete : replay.HasMismatch)
                {
                    ShowBanner(Loc.Format("console.banner.incomplete", string.Join("; ", replay.Warnings)));
                }
            }
        }
        foreach (var ed in _editors)
            if (initial.TryGetValue(ed.Field.Name, out var v)) ed.Set(v);

        _btnRun.Enabled = !_running;
        _btnSaveFavourite.Enabled = true;
        _formHost.ResumeLayout();
        ThemeManager.Apply(_formHost);
    }

    private void BuildForm()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 4, 0, 4),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (_fields.Count == 0)
        {
            table.Controls.Add(new Label { Text = Loc.Get("console.form.noarguments"), AutoSize = true }, 0, 0);
            table.SetColumnSpan(table.GetControlFromPosition(0, 0)!, 2);
        }

        int row = 0;
        foreach (var f in _fields)
        {
            var label = new Label
            {
                Text = f.Required ? f.Name + " *" : f.Name,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(0, 6, 12, 2),
            };
            if (f.Required) label.Font = TrackFont(new Font(Font, FontStyle.Bold));
            var editor = CreateEditor(f);
            editor.Control.Margin = new Padding(0, 2, 0, 2);
            string tip = f.Description ?? string.Empty;
            if (f.Kind == ToolFieldKind.Text)
                tip = (tip.Length > 0 ? tip + "\r\n" : "") + Loc.Get("console.form.emptytip");
            if (tip.Length > 0)
            {
                _tips.SetToolTip(label, tip);
                _tips.SetToolTip(editor.Control, tip);
            }
            table.Controls.Add(label, 0, row);
            table.Controls.Add(editor.Control, 1, row);
            _editors.Add(editor);
            row++;
        }
        if (_fields.Any(f => f.Kind == ToolFieldKind.Text))
        {
            var hint = new Label
            {
                Text = Loc.Get("console.form.emptyhint"),
                AutoSize = true,
                MaximumSize = new Size(900, 0),
                Margin = new Padding(0, 6, 0, 2),
            };
            table.Controls.Add(hint, 0, row);
            table.SetColumnSpan(hint, 2);
        }
        _formHost.Controls.Add(table);
    }

    private Font TrackFont(Font font)
    {
        _formFonts.Add(font);
        return font;
    }

    /// <summary>Removes the previous tool's form and releases its controls, fonts and tooltips.</summary>
    private void ClearForm()
    {
        _tips.RemoveAll();
        var old = _formHost.Controls.Cast<Control>().ToList();
        _formHost.Controls.Clear();
        foreach (var c in old) c.Dispose(); // disposes the children too
        foreach (var f in _formFonts) f.Dispose();
        _formFonts.Clear();
    }

    private void ShowBanner(string? text)
    {
        _lblBanner.Text = text ?? string.Empty;
        _lblBanner.Visible = !string.IsNullOrEmpty(text);
        _lblBanner.ForeColor = ThemeManager.Palette.Warn;
    }

    private sealed record ChoiceItem(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    private FieldEditor CreateEditor(ToolField f)
    {
        switch (f.Kind)
        {
            case ToolFieldKind.Boolean:
            {
                var chk = new CheckBox { Name = "_fld_" + f.Name, AutoSize = true };
                return new FieldEditor(f, chk, () => chk.Checked ? "true" : "false",
                    v => chk.Checked = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
            }
            case ToolFieldKind.Choice:
            {
                var cmb = new ComboBox
                {
                    Name = "_fld_" + f.Name,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Dock = DockStyle.Fill,
                };
                // Always an empty first entry, so a required enum without a default starts unselected
                // (the required check then fires) and an optional one can stay unset.
                cmb.Items.Add(new ChoiceItem(string.Empty, Loc.Get("console.form.choose")));
                foreach (var c in f.Choices) cmb.Items.Add(new ChoiceItem(c, c));
                return new FieldEditor(f, cmb, () => (cmb.SelectedItem as ChoiceItem)?.Value ?? string.Empty, v =>
                {
                    int i = cmb.Items.Cast<ChoiceItem>().ToList().FindIndex(
                        x => string.Equals(x.Value, v, StringComparison.OrdinalIgnoreCase));
                    cmb.SelectedIndex = i >= 0 ? i : 0;
                });
            }
            case ToolFieldKind.Json:
            {
                var txt = new TextBox
                {
                    Name = "_fld_" + f.Name,
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    Height = 90,
                    Dock = DockStyle.Fill,
                    Font = TrackFont(new Font(FontFamily.GenericMonospace, 9f)),
                    PlaceholderText = "JSON",
                };
                return new FieldEditor(f, txt, () => txt.Text, v => txt.Text = v.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            }
            default:
            {
                var txt = new TextBox
                {
                    Name = "_fld_" + f.Name,
                    Dock = DockStyle.Fill,
                    PlaceholderText = f.Kind switch
                    {
                        ToolFieldKind.Integer => Loc.Get("console.form.integer"),
                        ToolFieldKind.Number => Loc.Get("console.form.number"),
                        _ => string.Empty,
                    },
                };
                return new FieldEditor(f, txt, () => txt.Text, v => txt.Text = v);
            }
        }
    }

    // ── Run ──────────────────────────────────────────────────────────────

    private (JsonObject? Args, bool Ok) CollectArguments()
    {
        var values = _editors.ToDictionary(e => e.Field.Name, e => (string?)e.Get(), StringComparer.Ordinal);
        var result = ToolArgumentBuilder.Build(_fields, values);
        if (!result.IsValid)
        {
            SetStatus(Loc.Format("console.status.cannotrun", string.Join("; ", result.Errors)), StatusKind.Error);
            return (null, false);
        }
        return (result.Arguments, true);
    }

    private async Task RunAsync()
    {
        if (_client is null || _selected is null || _running) return;
        var (args, ok) = CollectArguments();
        if (!ok || args is null) return;

        var tool = _selected;
        // Anything not explicitly read-only needs a confirmation (default button = Cancel).
        if (tool.ReadOnly != true && !ConfirmWrite(tool)) return;

        string request = new JsonObject
        {
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = args.DeepClone() },
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        _running = true;
        _btnRun.Enabled = false;
        SetStatus(Loc.Format("console.status.running", tool.Name), StatusKind.Info);
        var cts = _runCts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        try
        {
            var outcome = await _client.CallAsync(tool.Name, args, cts.Token);
            if (_disposed || IsDisposed) return;
            _resultText = outcome.Text;
            _rawText = "// request\r\n" + request + "\r\n\r\n// response\r\n" +
                       (outcome.RawResponseJson.Length > 0 ? PrettyJson(outcome.RawResponseJson) : outcome.Text);
            ShowOutput();
            string duration = outcome.Duration.TotalSeconds >= 1
                ? $"{outcome.Duration.TotalSeconds:0.0} s"
                : $"{outcome.Duration.TotalMilliseconds:0} ms";
            if (outcome.NotReachable)
                SetStatus($"{outcome.Text} ({duration})", StatusKind.Error);
            else if (outcome.OutcomeUnknown)
                SetStatus(Loc.Format("console.status.outcomeunknown", duration), StatusKind.Warn);
            else
                SetStatus(Loc.Format(outcome.IsError ? "console.status.toolerror" : "console.status.ok", duration),
                    outcome.IsError ? StatusKind.Error : StatusKind.Ok);
        }
        finally
        {
            _running = false;
            cts.Dispose();
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            if (!_disposed && !IsDisposed) _btnRun.Enabled = _selected is not null;
        }
    }

    private bool ConfirmWrite(ToolSummary tool)
    {
        string kind = tool.Destructive == true
            ? Loc.Get("console.confirm.destructive")
            : Loc.Get("console.confirm.notreadonly");
        var answer = MessageBox.Show(
            FindForm(),
            Loc.Format("console.confirm.body", tool.Name, kind),
            Loc.Get("console.confirm.title"),
            MessageBoxButtons.OKCancel,
            tool.Destructive == true ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.OK;
    }

    private static string PrettyJson(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private void ShowOutput() =>
        _txtOutput.Text = (_chkRaw.Checked ? _rawText : _resultText).Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void CopyOutput()
    {
        if (_txtOutput.TextLength == 0) return;
        try
        {
            Clipboard.SetText(_txtOutput.Text);
            SetStatus(Loc.Get("console.status.copied"), StatusKind.Info);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            SetStatus(Loc.Get("console.status.clipboardbusy"), StatusKind.Warn);
        }
    }

    // ── Favourites ───────────────────────────────────────────────────────

    private void ReloadPresets()
    {
        var (list, warning) = _presets.LoadWithWarning();
        _presetList = list;
        if (_toolsLoaded) RebuildTree();
        if (warning is not null) SetStatus(warning, StatusKind.Warn);
    }

    private void SaveFavourite()
    {
        if (_selected is null) return;
        var (args, ok) = CollectArguments();
        if (!ok || args is null) return;

        string? name = PromptName(_selectedPreset?.Name ?? _selected.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        var preset = new ToolPreset(name.Trim(), _selected.Name, args.ToJsonString());
        _presetList = _presets.Upsert(_presetList, preset, out bool saved);
        _selectedPreset = preset;
        RebuildTree();
        if (saved) SetStatus(Loc.Format("console.status.favsaved", preset.Name), StatusKind.Info);
        else SetStatus(Loc.Format("console.status.favsavefailed", preset.Name, _presets.Path), StatusKind.Error);
    }

    private void RemoveFavourite()
    {
        if (_selectedPreset is null) return;
        string name = _selectedPreset.Name;
        _presetList = _presets.Remove(_presetList, name, out bool saved);
        _selectedPreset = null;
        _btnRemoveFavourite.Visible = false;
        RebuildTree();
        if (saved) SetStatus(Loc.Format("console.status.favremoved", name), StatusKind.Info);
        else SetStatus(Loc.Format("console.status.favremovefailed", name, _presets.Path), StatusKind.Error);
    }

    private string? PromptName(string suggestion)
    {
        using var dlg = new Form
        {
            Text = Loc.Get("console.favourite.save"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 120),
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var box = new TextBox { Dock = DockStyle.Top, Text = suggestion };
        var ok = new Button { Text = Loc.Get("console.dialog.save"), DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = Loc.Get("console.dialog.cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        row.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(new Label { Text = Loc.Get("console.dialog.name"), AutoSize = true, MaximumSize = new Size(360, 0) }, 0, 0);
        layout.Controls.Add(box, 0, 1);
        layout.Controls.Add(row, 0, 2);
        dlg.Controls.Add(layout);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        ThemeManager.ApplyDialog(dlg);
        box.SelectAll();
        return dlg.ShowDialog(FindForm()) == DialogResult.OK ? box.Text : null;
    }

    // ── Status ───────────────────────────────────────────────────────────

    private enum StatusKind { Info, Ok, Warn, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        var p = ThemeManager.Palette;
        _lblStatus.Text = text;
        _lblStatus.ForeColor = kind switch
        {
            StatusKind.Ok => p.Ok,
            StatusKind.Warn => p.Warn,
            StatusKind.Error => p.Error,
            _ => p.Text,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            try { _runCts?.Cancel(); }
            catch (ObjectDisposedException) { /* the call just finished */ }
            _tips.Dispose();
            foreach (var f in _formFonts) f.Dispose();
            _formFonts.Clear();
            // A pending call sees _disposed and ObjectDisposedException and ends quietly; the
            // client tolerates being disposed while a call is in flight. Faults are observed here.
            if (_client is { } c)
                _ = c.DisposeAsync().AsTask().ContinueWith(
                    t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        base.Dispose(disposing);
    }
}
