using System.Drawing;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms;

/// <summary>
/// hmi-server: the modal popup behind EVERY HMI write — the engineer's rule "ask
/// every time; it should be possible". Same idea as the IBN
/// confirm popup: the AI asks, only a human executes.
/// Deliberately: no AcceptButton (Enter never confirms), focus on "Cancel", Esc
/// rejects, and the dialog closes itself as "expired" at the caller's deadline.
/// </summary>
internal sealed class HmiConfirmDialog : Form
{
    private readonly DateTime _deadlineUtc;
    private readonly Label _lblCountdown;
    private readonly System.Windows.Forms.Timer _timer;

    public HmiConfirmDialog(HmiWriteRequest request, DateTime deadlineUtc)
    {
        _deadlineUtc = deadlineUtc;

        Text = Loc.Get("hmiconfirm.title");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(640, 330);

        var lblHeadline = new Label
        {
            Text = HmiConfirmText.Headline(request),
            Location = new Point(16, 14),
            AutoSize = true,
            MaximumSize = new Size(608, 0),
            Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
        };
        var txtDetails = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            TabStop = false,
            ScrollBars = ScrollBars.Vertical,
            Location = new Point(16, 46),
            Size = new Size(608, 168),
            Font = new Font("Consolas", 9.5f),
            Text = HmiConfirmText.Details(request),
        };
        var lblEffect = new Label
        {
            Text = request.WritesToPlc ? Loc.Get("hmiconfirm.effect.plc") : Loc.Get("hmiconfirm.effect.server"),
            Location = new Point(16, 224),
            AutoSize = true,
            MaximumSize = new Size(608, 0),
            ForeColor = request.WritesToPlc ? Theme.ThemeManager.Palette.Error : Theme.ThemeManager.Palette.Text,
            Font = request.WritesToPlc ? new Font(Font, FontStyle.Bold) : Font,
        };
        _lblCountdown = new Label { Location = new Point(16, 252), AutoSize = true };
        var btnConfirm = new Button
        {
            Text = Loc.Get("hmiconfirm.button.execute"),
            DialogResult = DialogResult.OK,
            Location = new Point(420, 284),
            Size = new Size(98, 30),
        };
        var btnCancel = new Button
        {
            Text = Loc.Get("confirm.button.cancel"),
            DialogResult = DialogResult.Cancel,
            Location = new Point(526, 284),
            Size = new Size(98, 30),
        };

        // German/French/Italian headlines and warnings run longer: stack the parts by their measured height
        // (one line of English gives the historic positions).
        txtDetails.Top = Math.Max(46, lblHeadline.Top + WrappedHeight(lblHeadline) + 10);
        lblEffect.Top = txtDetails.Bottom + 10;
        _lblCountdown.Top = lblEffect.Top + WrappedHeight(lblEffect) + 8;
        int buttonsTop = Math.Max(284, _lblCountdown.Top + 19 + 13);
        btnConfirm.Top = buttonsTop;
        btnCancel.Top = buttonsTop;
        ClientSize = new Size(640, buttonsTop + 30 + 16);

        Controls.AddRange([lblHeadline, txtDetails, lblEffect, _lblCountdown, btnConfirm, btnCancel]);
        CancelButton = btnCancel;
        ActiveControl = btnCancel;

        Theme.ThemeManager.ApplyDialog(this);
        _lblCountdown.ForeColor = Theme.ThemeManager.Palette.TextMuted; // Apply resets plain labels to Text

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

    /// <summary>The height a wrapping label needs for its text (<c>MaximumSize</c> gives the width).</summary>
    private static int WrappedHeight(Label label)
    {
        int width = label.MaximumSize.Width > 0 ? label.MaximumSize.Width : 608;
        int measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        return Math.Max(label.Height, measured);
    }

    private void Tick()
    {
        TimeSpan left = _deadlineUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();
            DialogResult = DialogResult.Abort; // closes the modal dialog as "expired"
            return;
        }
        _lblCountdown.Text = Loc.Format("hmiconfirm.countdown", Math.Ceiling(left.TotalSeconds).ToString("0", System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>Popup text, kept out of the Form so it is unit-testable.</summary>
internal static class HmiConfirmText
{
    internal static string Headline(HmiWriteRequest request) => Loc.Get(request.Kind switch
    {
        HmiWriteKind.MapSymbol => "hmiconfirm.headline.mapsymbol",
        HmiWriteKind.UnmapSymbol => "hmiconfirm.headline.unmapsymbol",
        HmiWriteKind.AddItem => "hmiconfirm.headline.additem",
        HmiWriteKind.WriteFile => "hmiconfirm.headline.writefile",
        HmiWriteKind.ImportFile => "hmiconfirm.headline.importfile",
        HmiWriteKind.ApplyBundle => "hmiconfirm.headline.applybundle",
        HmiWriteKind.DeleteItem => "hmiconfirm.headline.deleteitem",
        HmiWriteKind.AttachTmcFile => "hmiconfirm.headline.attachtmcfile",
        HmiWriteKind.DetachTmcFile => "hmiconfirm.headline.detachtmcfile",
        HmiWriteKind.EnableRuntimeTmc => "hmiconfirm.headline.enableruntimetmc",
        HmiWriteKind.DisableRuntimeTmc => "hmiconfirm.headline.disableruntimetmc",
        HmiWriteKind.AddInternalSymbol => "hmiconfirm.headline.addinternalsymbol",
        HmiWriteKind.RemoveInternalSymbol => "hmiconfirm.headline.removeinternalsymbol",
        HmiWriteKind.MapOfflineSymbol => "hmiconfirm.headline.mapofflinesymbol",
        HmiWriteKind.CreateProjectDatatype => "hmiconfirm.headline.createprojectdatatype",
        HmiWriteKind.SwitchMappingToPlc => "hmiconfirm.headline.switchmappingtoplc",
        HmiWriteKind.Publish => "hmiconfirm.headline.publish",
        _ => "hmiconfirm.headline.default",
    });

    /// <summary>The label column is padded to 12 characters (the historic English layout), wider when a language needs it.</summary>
    internal static string Details(HmiWriteRequest request)
    {
        string[] labels =
        [
            Loc.Get("hmiconfirm.details.project"),
            Loc.Get("hmiconfirm.details.directory"),
            Subject(request.Kind),
            Loc.Get("hmiconfirm.details.domain"),
            Loc.Get("hmiconfirm.details.current"),
            Loc.Get("hmiconfirm.details.new"),
        ];
        int width = Math.Max(12, labels.Max(l => l.Length));
        string Col(int i) => labels[i].PadRight(width);
        return string.Join(Environment.NewLine,
            $"{Col(0)}: {request.HmiProject}",
            $"{Col(1)}: {request.ProjectDirectory}",
            $"{Col(2)}: {request.Symbol}",
            $"{Col(3)}: {request.Domain ?? "-"}",
            $"{Col(4)}: {request.CurrentValue ?? Loc.Get("hmiconfirm.value.unknown")}",
            $"{Col(5)}: {request.NewValue ?? "-"}");
    }

    /// <summary>What <see cref="HmiWriteRequest.Symbol"/> carries for this kind. "Symbol"
    /// pads to exactly the historic column, so existing popups read unchanged.</summary>
    private static string Subject(HmiWriteKind kind) => Loc.Get(kind switch
    {
        HmiWriteKind.AttachTmcFile or HmiWriteKind.DetachTmcFile
            or HmiWriteKind.EnableRuntimeTmc or HmiWriteKind.DisableRuntimeTmc => "hmiconfirm.subject.runtime",
        HmiWriteKind.AddInternalSymbol or HmiWriteKind.RemoveInternalSymbol => "hmiconfirm.subject.internal",
        HmiWriteKind.CreateProjectDatatype => "hmiconfirm.subject.datatype",
        HmiWriteKind.SwitchMappingToPlc => "hmiconfirm.subject.mappings",
        HmiWriteKind.Publish => "hmiconfirm.subject.target",
        _ => "hmiconfirm.subject.symbol",
    });
}

/// <summary>
/// Shows <see cref="HmiConfirmDialog"/> on the host's UI thread and blocks the calling
/// (MCP) thread until the engineer answers or the deadline passes. Fail-closed: only
/// the confirm button yields <see cref="HmiConfirmOutcome.Confirmed"/>. A click after
/// the caller gave up changes nothing — the write only ever happens in the caller.
/// </summary>
internal sealed class FormHmiWriteConfirmer(Form owner) : IHmiWriteConfirmer
{
    private const int Pending = 0, Showing = 1, Abandoned = 2;

    public HmiConfirmOutcome Confirm(HmiWriteRequest request, TimeSpan timeout)
    {
        if (owner.IsDisposed || !owner.IsHandleCreated) return HmiConfirmOutcome.Unavailable;
        DateTime deadline = DateTime.UtcNow + timeout;

        // On the UI thread already (a GUI caller) — show inline; waiting here would deadlock.
        if (!owner.InvokeRequired) return Show(request, deadline);

        var done = new TaskCompletionSource<HmiConfirmOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = Pending;
        try
        {
            owner.BeginInvoke(() =>
            {
                if (Interlocked.CompareExchange(ref state, Showing, Pending) != Pending) return;
                done.TrySetResult(Show(request, deadline));
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return HmiConfirmOutcome.Unavailable;
        }

        // The dialog closes itself at the deadline; the grace covers the UI round trip.
        if (done.Task.Wait(timeout + TimeSpan.FromSeconds(5))) return done.Task.Result;
        // The UI thread never got to it: make sure it will not pop up later.
        if (Interlocked.CompareExchange(ref state, Abandoned, Pending) == Pending) return HmiConfirmOutcome.TimedOut;
        // On screen, but the UI thread was late with its countdown — give it a moment.
        return done.Task.Wait(TimeSpan.FromSeconds(10)) ? done.Task.Result : HmiConfirmOutcome.TimedOut;
    }

    private HmiConfirmOutcome Show(HmiWriteRequest request, DateTime deadline)
    {
        if (DateTime.UtcNow >= deadline) return HmiConfirmOutcome.TimedOut;
        // Owned windows are hidden with a minimized owner — bring the host up so the
        // engineer actually sees the request.
        if (owner.WindowState == FormWindowState.Minimized) owner.WindowState = FormWindowState.Normal;
        using var dialog = new HmiConfirmDialog(request, deadline);
        return dialog.ShowDialog(owner) switch
        {
            DialogResult.OK => HmiConfirmOutcome.Confirmed,
            DialogResult.Abort => HmiConfirmOutcome.TimedOut,
            _ => HmiConfirmOutcome.Rejected,
        };
    }
}
