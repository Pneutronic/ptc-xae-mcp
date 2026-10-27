using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #268: the MCP tools depend on <see cref="ServerHost"/> instead of <c>Form1</c>.
/// These cover the parts that used to read GUI controls or live inside the form.
/// </summary>
public sealed class ServerHostTests : IDisposable
{
    private readonly ServerHost _host = new(new FakeTwinCatSession(), enableTimers: false);

    public void Dispose() => _host.Dispose();

    private static HmiWriteRequest Request() =>
        new(HmiWriteKind.MapSymbol, "P", @"C:\Projects\Hmi", "S", "ADS", "x", null, false);

    [Fact]
    public void HmiConfirmer_FailsClosed_UntilAUiIsAttached()
    {
        Assert.Equal(HmiConfirmOutcome.Unavailable, _host.HmiConfirmer.Confirm(Request(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void HmiConfirmer_ForwardsToTheAttachedUi()
    {
        var ui = new RecordingConfirmer(HmiConfirmOutcome.Confirmed);
        _host.AttachHmiConfirmUi(ui);

        Assert.Equal(HmiConfirmOutcome.Confirmed, _host.HmiConfirmer.Confirm(Request(), TimeSpan.FromSeconds(1)));
        Assert.Equal(1, ui.Calls);
    }

    [Fact]
    public void RequestStop_CancelsTheTrackedApplyRun_AndLogsIt()
    {
        using var cts = new CancellationTokenSource();
        using (_host.RunCancellation.Track(cts))
        {
            Assert.True(_host.RequestStop());
        }

        Assert.True(cts.IsCancellationRequested);
        Assert.Contains("=== Cancellation requested ===", _host.RunLog.Text);
    }

    [Fact]
    public void RequestStop_WithNothingRunning_ReturnsFalse_AndLogsNothing()
    {
        Assert.False(_host.RequestStop());
        Assert.Equal(string.Empty, _host.RunLog.Text);
    }

    [Fact]
    public async Task RunApplySchemaAsync_RefusesWhileTheLockIsHeld_LikeDuringAGuiApply()
    {
        Assert.True(_host.Dispatcher.TryAcquire()); // what any GUI or MCP TwinCAT operation holds
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _host.RunApplySchemaAsync(@"C:\nowhere\schema.json", null, null, CancellationToken.None));
        }
        finally
        {
            _host.Dispatcher.Release();
        }

        Assert.True(_host.Dispatcher.TryAcquire(), "the refusal must not leave the lock taken");
        _host.Dispatcher.Release();
    }

    [Fact]
    public void GetStatus_WaitsForARequestedRefresh_SoOpenProjectThenStatusShowsTheNewProject()
    {
        _host.XaeProcessCount = () => 1;
        _host.EnvironmentRefreshRequested += _ => Task.Run(async () =>
        {
            await Task.Delay(200);
            _host.ReportEnvironment(new HostEnvironmentSnapshot("Active", "Opened"));
        });

        _host.RequestEnvironmentRefresh("After OpenProject");

        Assert.Contains("Project: Opened", _host.GetStatus());
    }

    [Fact]
    public void GetStatus_ReportsTheLastEnvironmentSnapshot()
    {
        _host.XaeProcessCount = () => 1;
        Assert.StartsWith("XAE: Not checked yet | Project: Not checked yet | ", _host.GetStatus());

        _host.ReportEnvironment(new HostEnvironmentSnapshot("Active", "Demo | PLC: MainPlc"));

        Assert.StartsWith("XAE: Active | Project: Demo | PLC: MainPlc | ", _host.GetStatus());
    }

    [Fact]
    public void GetStatus_AfterTheShellExited_ReportsNotActive_NotTheStaleSnapshot()
    {
        _host.ReportEnvironment(new HostEnvironmentSnapshot("Active", "Demo  ·  Host: TcXaeShell.DTE.15.0 (32-bit)"));
        _host.XaeProcessCount = () => 0;

        Assert.StartsWith("XAE: Not active | Project: Not available | ", _host.GetStatus());
    }

    [Fact]
    public void GetStatus_WithAnUnknownProcessCount_KeepsTheSnapshot()
    {
        _host.ReportEnvironment(new HostEnvironmentSnapshot("Active", "Demo"));
        _host.XaeProcessCount = () => null;

        Assert.StartsWith("XAE: Active | Project: Demo | ", _host.GetStatus());
    }

    [Fact]
    public void RequestEnvironmentRefresh_RaisesTheEventWithTheReason()
    {
        string? reason = null;
        _host.EnvironmentRefreshRequested += r => reason = r;

        _host.RequestEnvironmentRefresh("After OpenProject");

        Assert.Equal("After OpenProject", reason);
    }

    [Fact]
    public void RunLog_AppendsClearsAndNotifies()
    {
        var log = new HostRunLog();
        var lines = new List<string>();
        bool cleared = false;
        log.LineAppended += lines.Add;
        log.Cleared += () => cleared = true;

        log.Append("one");
        log.Append("two");
        Assert.Equal($"one{Environment.NewLine}two{Environment.NewLine}", log.Text);
        Assert.Equal(["one", "two"], lines);

        log.Clear();
        Assert.True(cleared);
        Assert.Equal(string.Empty, log.Text);
    }

    [Fact]
    public void RunLog_Tail_MatchesLogTailOnTheFullText()
    {
        var log = new HostRunLog();
        for (int i = 0; i < 50; i++) log.Append($"line {i}");

        Assert.Equal(LogTail.Tail(log.Text, 30), log.Tail(30));
        Assert.Equal(log.Text, log.Tail(0));
    }

    [Fact]
    public void RunLog_DropsTheOldestPart_PastTheLimit()
    {
        var log = new HostRunLog();
        string chunk = new('x', 100_000);
        for (int i = 0; i < 25; i++) log.Append(chunk);
        log.Append("newest");

        Assert.True(log.Text.Length <= HostRunLog.MaxChars);
        Assert.EndsWith("newest" + Environment.NewLine, log.Text);
    }

    private sealed class RecordingConfirmer(HmiConfirmOutcome outcome) : IHmiWriteConfirmer
    {
        public int Calls { get; private set; }

        public HmiConfirmOutcome Confirm(HmiWriteRequest request, TimeSpan timeout)
        {
            Calls++;
            return outcome;
        }
    }
}
