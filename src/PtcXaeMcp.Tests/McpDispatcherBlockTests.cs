using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A call held by an XAE dialog answers [Blocked] after the grace time instead of hanging; the lock stays held
/// until the body returns, later calls are refused with the dialog named, and the late result is reported.
/// Every call runs on a pool thread: the dispatcher keeps a call inline on a thread with a SynchronizationContext.
/// </summary>
public sealed class McpDispatcherBlockTests : IDisposable
{
    private static readonly BlockingXaeDialog Reload =
        new("0x1A2B", "Microsoft Visual Studio", XaeDialogClass.KnownSafe, ["Ja", "Nein"]);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dispatch_" + Guid.NewGuid().ToString("N"));
    private readonly ManualResetEventSlim _release = new(false);

    public McpDispatcherBlockTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _release.Set(); // lets every held body return; not disposed, a body may still be leaving Wait
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Late
    {
        public readonly ManualResetEventSlim Arrived = new(false);
        public string? Tool, Result;
        public void Take(string tool, string result, TimeSpan _) { Tool = tool; Result = result; Arrived.Set(); }
    }

    /// <summary>A dispatcher whose watch sees <paramref name="dialog"/> (null = none) with a 100 ms grace time.</summary>
    private static McpDispatcher New(BlockingXaeDialog? dialog, Late? late = null, Func<BlockingXaeDialog?>? now = null)
    {
        var d = new McpDispatcher(new FakeTwinCatSession(),
            () => new XaeCallBlockWatch(() => dialog?.Title, () => dialog, TimeSpan.FromMilliseconds(100)),
            TimeSpan.FromMilliseconds(20), now ?? (() => dialog));
        if (late is not null) d.LateResult = late.Take;
        return d;
    }

    /// <summary>True once the held call has released the lock (the late result is reported just before).</summary>
    private static bool Freed(McpDispatcher d)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            if (d.TryAcquire())
            {
                d.Release();
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    private string Hang(string result = "late ok")
    {
        _release.Wait(Wait);
        return result;
    }

    [Fact]
    public async Task AHeldCall_AnswersBlocked_NamingTheDialogAndItsButtons()
    {
        var d = New(Reload);
        var ex = await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.Run(() => Hang())))
            .WaitAsync(Wait);
        Assert.StartsWith("[Blocked] XAE dialog 'Microsoft Visual Studio' (id 0x1A2B, known-safe; buttons: 'Ja', 'Nein')", ex.Message);
        Assert.Contains("answer it with twincat_xae_dialog_answer or in the XAE", ex.Message);
        Assert.Same(Reload, ex.BlockingDialog);
        Assert.True(ComCall.IsBusy(ex) && ComCall.IsFinal(ex), "busy-aware catches must let it through");
    }

    [Fact]
    public async Task WhileHeld_ALaterCallIsRefused_WithTheDialogAndTheNextStep_AndNeverRuns()
    {
        var d = New(Reload);
        await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.Run(() => Hang()))).WaitAsync(Wait);
        Assert.NotNull(d.Detached);

        bool ran = false;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => d.Run(() => { ran = true; return "x"; })));
        Assert.False(ran);
        Assert.StartsWith("An operation is already in progress: The running call waits for the XAE dialog 'Microsoft Visual Studio' (id 0x1A2B", refused.Message);
        Assert.Contains("twincat_xae_dialog_answer", refused.Message);
        Assert.Contains("\nNext: twincat_xae_dialogs", refused.Message);
        Assert.False(d.TryAcquire(), "the lock stays held while the call runs");
    }

    [Fact]
    public async Task WhenTheCallReturns_ItsLateResultIsReported_AndTheLockIsFree()
    {
        var late = new Late();
        int afterSuccess = 0;
        var d = New(Reload, late);
        await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.Run(() => Hang("written"), () => afterSuccess++)))
            .WaitAsync(Wait);
        Assert.False(late.Arrived.IsSet);

        _release.Set();
        Assert.True(late.Arrived.Wait(Wait), "no late result");
        Assert.Equal("written", late.Result);
        Assert.Equal("The running call", late.Tool);
        Assert.Equal(1, afterSuccess);
        Assert.True(Freed(d), "the lock is free again");
        Assert.Null(d.Detached);
    }

    [Fact]
    public async Task ALateException_IsReportedAsError()
    {
        var late = new Late();
        var d = New(Reload, late);
        await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.Run(() =>
        {
            _release.Wait(Wait);
            throw new InvalidOperationException("reload failed");
        }))).WaitAsync(Wait);

        _release.Set();
        Assert.True(late.Arrived.Wait(Wait));
        Assert.Equal("[Error] reload failed", late.Result);
        Assert.True(Freed(d));
    }

    [Fact]
    public async Task AfterTheDialogClosed_TheRefusalSaysTheCallStillRuns()
    {
        var d = New(Reload, now: () => null);
        await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.Run(() => Hang()))).WaitAsync(Wait);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => d.Run(() => "x")));
        Assert.Contains("still runs in the XAE", refused.Message);
        Assert.Contains("that dialog is closed", refused.Message);
    }

    [Fact]
    public async Task AFastCall_IsNeverBlocked_EvenWithADialogOpen()
    {
        var d = New(Reload);
        Assert.Equal("ok", await Task.Run(() => d.Run(() => "ok")));
        Assert.Equal("ok", await Task.Run(() => d.Run(() => { Thread.Sleep(60); return "ok"; })));  // within the grace time
        Assert.Null(d.Detached);
    }

    [Fact]
    public async Task ASlowCallWithoutDialog_RunsToTheEnd()
    {
        var d = New(null);
        Assert.Equal("done", await Task.Run(() => d.Run(() => { Thread.Sleep(400); return "done"; })));
    }

    [Fact]
    public async Task AnException_ReachesTheCallerUnchanged()
    {
        var d = New(null);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => d.Run(() => throw new ArgumentException("bad"))));
        Assert.Equal("bad", ex.Message);
        Assert.True(d.TryAcquire());
        d.Release();
    }

    [Fact]
    public async Task OnAThreadWithASynchronizationContext_TheBodyRunsInline()
    {
        var d = New(Reload);
        int caller = 0, bodyThread = -1;
        await Task.Factory.StartNew(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            caller = Environment.CurrentManagedThreadId;
            d.Run(() => { bodyThread = Environment.CurrentManagedThreadId; return "ok"; });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.Equal(caller, bodyThread);
    }

    [Fact]
    public async Task RunAsync_AHeldCall_AnswersBlocked_AndReportsTheLateResult()
    {
        var late = new Late();
        var d = New(Reload, late);
        await Assert.ThrowsAsync<XaeCallDetachedException>(() => Task.Run(() => d.RunAsync(async () =>
        {
            await Task.Yield();
            return Hang("opened");
        }))).WaitAsync(Wait);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => d.RunAsync(() => Task.FromResult("x"))));

        _release.Set();
        Assert.True(late.Arrived.Wait(Wait));
        Assert.Equal("opened", late.Result);
        Assert.True(Freed(d));
        Assert.Equal("next", await Task.Run(() => d.RunAsync(() => Task.FromResult("next"))));
    }

    [Fact]
    public async Task ThroughTrace_TheAnswerIsBlockedUnchanged_AndTheLateResultIsLogged()
    {
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir));
        var d = New(Reload);
        d.LateResult = activity.RecordLateResult;

        string answer = await Task.Run(() => activity.Trace("twincat_plc_read", new { path = "MAIN" }, () => d.Run(() => Hang("MAIN body"))))
            .WaitAsync(Wait);
        Assert.StartsWith("[Blocked] XAE dialog 'Microsoft Visual Studio'", answer);
        Assert.Contains("twincat_plc_read keeps running in the XAE", answer);
        var blocked = Assert.Single(activity.Log.Snapshot());
        Assert.False(blocked.Success);

        string refused = await Task.Run(() => activity.Trace("twincat_plc_list", null, () => d.Run(() => "x")));
        Assert.StartsWith("[Error] An operation is already in progress: twincat_plc_read waits for the XAE dialog", refused);

        _release.Set();
        var until = DateTime.UtcNow + Wait;
        while (!activity.Log.Snapshot().Any(e => e.ToolName == "twincat_plc_read (late result)") && DateTime.UtcNow < until)
            await Task.Delay(20);
        var lateEntry = Assert.Single(activity.Log.Snapshot(), e => e.ToolName == "twincat_plc_read (late result)");
        Assert.Equal("MAIN body", lateEntry.Response);
        Assert.True(lateEntry.Success);
    }

    [Fact]
    public void HeldText_And_WaitingCall_NameToolDialogAndAge()
    {
        var call = new DetachedCall("twincat_plc_read", Reload, new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc));
        var now = call.SinceUtc.AddSeconds(12);
        Assert.Contains("twincat_plc_read waits for the XAE dialog 'Microsoft Visual Studio' (id 0x1A2B", McpDispatcher.HeldText(call, Reload, now));
        Assert.Contains("(12 s since its [Blocked] answer)", McpDispatcher.HeldText(call, Reload, now));
        Assert.Equal("", TwinCatMcpTools.WaitingCall(null, now));
        Assert.Contains("Waiting for a dialog: twincat_plc_read (answered [Blocked] 12 s ago for 'Microsoft Visual Studio', id 0x1A2B)",
            TwinCatMcpTools.WaitingCall(call, now));
    }
}
