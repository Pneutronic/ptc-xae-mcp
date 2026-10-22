using System.Globalization;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Views;

/// <summary>
/// The modal confirm popup behind every persistent-snapshot restore — the ONLY execution path:
/// <see cref="DialogResult.OK"/> lets the Approvals view call
/// <see cref="SnapshotRestoreCoordinator.ConfirmProposal"/>, anything else writes nothing.
/// Same rules as <see cref="IbnConfirmDialog"/>: TopMost, no AcceptButton (Enter never confirms),
/// focus on Cancel, Esc rejects, closes itself with <see cref="DialogResult.Abort"/> when the
/// proposal expires or stops being pending.
/// </summary>
internal sealed class RestoreConfirmDialog : Form
{
    private readonly DateTime _deadlineUtc;
    private readonly Func<bool> _stillPending;
    private readonly Label _lblCountdown;
    private readonly System.Windows.Forms.Timer _timer;

    public RestoreConfirmDialog(RestoreProposal proposal, Func<bool> stillPending)
    {
        _deadlineUtc = proposal.ExpiresUtc;
        _stillPending = stillPending;

        Name = "RestoreConfirmDialog";
        Text = RestoreConfirmText.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
        };

        var lblHeadline = new Label
        {
            Text = RestoreConfirmText.Headline,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        bool running = RestoreConfirmText.IsRunning(proposal.PlcState);
        var lblState = new Label
        {
            Name = "_lblRestorePlcState",
            Text = RestoreConfirmText.StateLine(proposal.PlcState),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        var txtDetails = new TextBox
        {
            Name = "_txtRestoreDetails",
            Multiline = true,
            ReadOnly = true,
            TabStop = false,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9.5f),
            Text = RestoreConfirmText.Details(proposal),
            Size = new Size(640, 260),
            Margin = new Padding(0, 0, 0, 8),
        };
        var lblEffect = new Label
        {
            Text = RestoreConfirmText.Effect,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        _lblCountdown = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
        };
        var btnCancel = new Button
        {
            Name = "_btnRestoreCancel",
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(98, 30),
        };
        var btnRestore = new Button
        {
            Name = "_btnRestoreConfirm",
            Text = "Restore values",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(120, 30),
        };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnRestore);

        layout.Controls.Add(lblHeadline);
        layout.Controls.Add(lblState);
        layout.Controls.Add(txtDetails);
        layout.Controls.Add(lblEffect);
        layout.Controls.Add(_lblCountdown);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        CancelButton = btnCancel;   // Esc rejects
        ActiveControl = btnCancel;  // no AcceptButton: Enter never confirms

        Theme.ThemeManager.ApplyDialog(this);
        _lblCountdown.ForeColor = Theme.ThemeManager.Palette.TextMuted;
        lblEffect.ForeColor = Theme.ThemeManager.Palette.Error;
        lblState.ForeColor = running ? Theme.ThemeManager.Palette.Error : Theme.ThemeManager.Palette.Warn;

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => Tick();
        Shown += (_, _) =>
        {
            Activate();
            System.Media.SystemSounds.Exclamation.Play();
            Tick();
            _timer.Start();
        };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private void Tick()
    {
        TimeSpan left = _deadlineUtc - DateTime.UtcNow;
        bool pending;
        try { pending = _stillPending(); }
        catch { pending = false; } // cannot tell — close rather than offer a confirm

        if (left <= TimeSpan.Zero || !pending)
        {
            _timer.Stop();
            DialogResult = DialogResult.Abort; // closes the modal dialog; the caller writes nothing
            return;
        }
        _lblCountdown.Text = RestoreConfirmText.Countdown(left);
    }
}

/// <summary>Popup text, kept out of the Form so it is unit-testable.</summary>
internal static class RestoreConfirmText
{
    public const string Title = "Persistent snapshot — confirm restore";

    public const string Headline = "The MCP client proposes to restore a snapshot into the PLC";

    public const string Effect =
        "⚠ Restore overwrites live PLC values. Cancel, Esc or closing this window writes nothing. " +
        "After you confirm, the PLC is checked once more; if it changed since the review, nothing is written.";

    public const int MaxDifferenceLines = 15;

    public static bool IsRunning(string plcState) =>
        string.Equals(plcState, "Run", StringComparison.OrdinalIgnoreCase);

    public static string StateLine(string plcState) => IsRunning(plcState)
        ? "PLC state: RUN — values are written while the program runs and may be overwritten by it"
        : $"PLC state: {plcState.ToUpperInvariant()}";

    public static string Details(RestoreProposal p)
    {
        var lines = new List<string>
        {
            $"Target    : {p.AmsNetId}:{p.AdsPort}",
            $"Snapshot  : {p.SnapshotPath}",
            $"Taken     : {p.SnapshotUtc} (UTC)",
            $"To write  : {p.Plan.Writes.Count} leaf/leaves ({p.Plan.Unchanged} already equal, {p.Plan.Skips.Count} skipped)",
            $"Proposal  : {p.RestoreId}",
            $"Requested : {p.CreatedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            string.Empty,
            "Differences (PLC now -> snapshot):",
        };
        foreach (var w in p.Plan.Writes.Take(MaxDifferenceLines))
            lines.Add($"  {w.Path}: {SnapshotValues.Show(w.Old.Value)} -> {SnapshotValues.Show(w.New.Value)}");
        if (p.Plan.Writes.Count > MaxDifferenceLines)
            lines.Add($"  ... and {p.Plan.Writes.Count - MaxDifferenceLines} more");
        if (p.Plan.Skips.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Skipped ({p.Plan.Skips.Count}):");
            foreach (var s in p.Plan.Skips.Take(MaxDifferenceLines))
                lines.Add($"  {s.Path}: {s.Reason}");
            if (p.Plan.Skips.Count > MaxDifferenceLines)
                lines.Add($"  ... and {p.Plan.Skips.Count - MaxDifferenceLines} more");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public static string Countdown(TimeSpan left) =>
        $"Nothing is written without your confirmation — this proposal expires in {Math.Ceiling(left.TotalSeconds):0} s.";
}
