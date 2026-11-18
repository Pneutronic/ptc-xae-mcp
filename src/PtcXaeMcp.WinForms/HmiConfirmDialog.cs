using System.Drawing;
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

        Text = "TwinCAT HMI — confirm change";
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
            Text = request.WritesToPlc
                ? "⚠ Writes through the HMI server into the running PLC."
                : "Changes the HMI project's server configuration (no PLC access).",
            Location = new Point(16, 224),
            AutoSize = true,
            ForeColor = request.WritesToPlc ? Theme.ThemeManager.Palette.Error : Theme.ThemeManager.Palette.Text,
            Font = request.WritesToPlc ? new Font(Font, FontStyle.Bold) : Font,
        };
        _lblCountdown = new Label { Location = new Point(16, 252), AutoSize = true };
        var btnConfirm = new Button
        {
            Text = "Execute",
            DialogResult = DialogResult.OK,
            Location = new Point(420, 284),
            Size = new Size(98, 30),
        };
        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(526, 284),
            Size = new Size(98, 30),
        };
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

    private void Tick()
    {
        TimeSpan left = _deadlineUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _timer.Stop();
            DialogResult = DialogResult.Abort; // closes the modal dialog as "expired"
            return;
        }
        _lblCountdown.Text = $"Nothing is written without confirmation — expires in {Math.Ceiling(left.TotalSeconds):0} s.";
    }
}

/// <summary>Popup text, kept out of the Form so it is unit-testable.</summary>
internal static class HmiConfirmText
{
    internal static string Headline(HmiWriteRequest request) => request.Kind switch
    {
        HmiWriteKind.MapSymbol => "MCP wants to map a symbol into the HMI",
        HmiWriteKind.UnmapSymbol => "MCP wants to remove a symbol mapping",
        HmiWriteKind.AddItem => "MCP wants to create a file in the HMI project",
        HmiWriteKind.WriteFile => "MCP wants to change a file in the HMI project",
        HmiWriteKind.ImportFile => "MCP wants to import a file into the HMI project",
        HmiWriteKind.ApplyBundle => "MCP wants to write a bundle of files into the HMI project",
        HmiWriteKind.DeleteItem => "MCP wants to DELETE from the HMI project (also from disk)",
        HmiWriteKind.AttachTmcFile => "MCP wants to attach a TMC file to an ADS runtime (symbols without a PLC)",
        HmiWriteKind.DetachTmcFile => "MCP wants to remove the TMC file from an ADS runtime (back to the ADS upload)",
        HmiWriteKind.EnableRuntimeTmc =>
            "MCP wants the HMI server to serve this ADS runtime's PLC symbols from a TMC file (no PLC connection)",
        HmiWriteKind.DisableRuntimeTmc =>
            "MCP wants this ADS runtime to upload its symbols from the PLC again (TMC file off)",
        HmiWriteKind.AddInternalSymbol => "MCP wants to add a client internal symbol (%i%) to the HMI project",
        HmiWriteKind.RemoveInternalSymbol => "MCP wants to remove a client internal symbol (%i%) from the HMI project",
        HmiWriteKind.MapOfflineSymbol => "MCP wants to create a server symbol in the HMI server (TcHmiSrv domain)",
        HmiWriteKind.CreateProjectDatatype =>
            "MCP wants to add project datatypes (from a PLC type of a TMC file) to Properties/tchmi.project.Schema.json",
        HmiWriteKind.SwitchMappingToPlc =>
            "MCP wants to switch HMI mappings from a TMC file to a real PLC (same mapped names, domain ADS)",
        _ => "MCP wants to write a value through the HMI server",
    };

    internal static string Details(HmiWriteRequest request) => string.Join(Environment.NewLine,
        $"HMI project : {request.HmiProject}",
        $"Directory   : {request.ProjectDirectory}",
        $"{Subject(request.Kind),-12}: {request.Symbol}",
        $"Domain      : {request.Domain ?? "-"}",
        $"Current     : {request.CurrentValue ?? "(unknown)"}",
        $"New         : {request.NewValue ?? "-"}");

    /// <summary>What <see cref="HmiWriteRequest.Symbol"/> carries for this kind. "Symbol"
    /// pads to exactly the historic column, so existing popups read unchanged.</summary>
    private static string Subject(HmiWriteKind kind) => kind switch
    {
        HmiWriteKind.AttachTmcFile or HmiWriteKind.DetachTmcFile
            or HmiWriteKind.EnableRuntimeTmc or HmiWriteKind.DisableRuntimeTmc => "ADS runtime",
        HmiWriteKind.AddInternalSymbol or HmiWriteKind.RemoveInternalSymbol => "%i% symbol",
        HmiWriteKind.CreateProjectDatatype => "Datatype",
        HmiWriteKind.SwitchMappingToPlc => "Mappings",
        _ => "Symbol",
    };
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
