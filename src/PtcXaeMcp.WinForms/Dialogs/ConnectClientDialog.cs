using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>
/// "Connect a client…" (issue #273): shows the <c>.mcp.json</c> snippet and the
/// <c>claude mcp add</c> command with Copy buttons, and tests the connection with
/// <c>GET /health</c>. The token value is never shown (see <see cref="ClientConnectInfo"/>).
/// </summary>
internal sealed class ConnectClientDialog : Form
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly int _port;
    private readonly Label _lblResult = new();
    private readonly Button _btnTest = new();

    public ConnectClientDialog(int port)
    {
        _port = port;
        bool tokenSet = ClientConnectInfo.TokenIsSet();

        Text = Loc.Get("connect.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 470);
        MinimumSize = new Size(520, 420);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Caption(
            Loc.Format("connect.intro", ClientConnectInfo.Url(port))), 0, 0);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Caption(Loc.Get("connect.claudeCode")), 0, 1);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        layout.Controls.Add(CodeBox(ClientConnectInfo.ClaudeMcpAddCommand(port, tokenSet), out var cmdBox), 0, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CopyButton(Loc.Get("connect.copyCommand"), cmdBox), 0, 3);

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Caption(Loc.Get("connect.json")), 0, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(CodeBox(ClientConnectInfo.McpJsonSnippet(port, tokenSet), out var jsonBox), 0, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(CopyButton(Loc.Get("connect.copySnippet"), jsonBox), 0, 6);

        if (tokenSet)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(Caption(
                Loc.Format("connect.tokenNote", ClientConnectInfo.TokenPlaceholder)), 0, 7);
        }

        var testRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _btnTest.Text = Loc.Get("connect.test");
        _btnTest.AutoSize = true;
        _btnTest.Tag = "primary";
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _lblResult.AutoSize = true;
        _lblResult.Margin = new Padding(12, 8, 0, 0);
        _lblResult.MaximumSize = new Size(480, 0);
        testRow.Controls.Add(_btnTest);
        testRow.Controls.Add(_lblResult);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(testRow, 0, 8);

        var close = new Button { Text = Loc.Get("connect.close"), AutoSize = true, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(close, 0, 9);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(layout);
        ThemeManager.ApplyDialog(this);
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(600, 0),
        Margin = new Padding(0, 8, 0, 4),
    };

    private static Control CodeBox(string text, out TextBox box)
    {
        box = new TextBox
        {
            Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
        };
        return box;
    }

    private static Button CopyButton(string text, TextBox source)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        button.Click += (_, _) =>
        {
            try { Clipboard.SetText(source.Text); }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard held by another process */ }
        };
        return button;
    }

    private async Task TestConnectionAsync()
    {
        _btnTest.Enabled = false;
        _lblResult.ForeColor = ThemeManager.Palette.TextMuted;
        _lblResult.Text = Loc.Get("connect.testing");
        try
        {
            // Off the UI thread: Task.Run keeps a slow answer from blocking the dialog.
            string body = await Task.Run(async () =>
            {
                using var response = await Http.GetAsync($"http://localhost:{_port}/health");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            });
            if (IsDisposed) return; // dialog closed while the request was running
            _lblResult.ForeColor = ThemeManager.Palette.Ok;
            _lblResult.Text = ClientConnectInfo.DescribeHealth(body);
        }
        // Async-void click handler: nothing may escape to the UI thread.
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _lblResult.ForeColor = ThemeManager.Palette.Error;
            _lblResult.Text = Loc.Format("connect.notReachable",
                ex is TaskCanceledException ? Loc.Get("connect.timeout") : ex.Message);
        }
        finally
        {
            if (!IsDisposed) _btnTest.Enabled = true;
        }
    }
}
