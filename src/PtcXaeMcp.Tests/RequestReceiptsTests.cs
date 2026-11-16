using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>The receipts of requests with a requestKey: the same key never runs twice.</summary>
public sealed class RequestReceiptsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "receipts_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    public RequestReceiptsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Key = "3f0c1d2e-4a5b-4c6d-8e7f-0a1b2c3d4e5f";
    private const string Remote = "10.1.1.1.1.1";

    private string FilePath => Path.Combine(_dir, ReceiptStore.FileName);

    private ReceiptStore NewStore() => new(FilePath, () => _now);

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static CallToolResult Ok(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static string[] Texts(CallToolResult r) => r.Content.OfType<TextContentBlock>().Select(c => c.Text).ToArray();

    private sealed class Body(string result = "activated")
    {
        public int Runs;
        public Func<CallToolResult>? Custom;

        public ValueTask<CallToolResult> Run()
        {
            Runs++;
            return ValueTask.FromResult(Custom?.Invoke() ?? Ok(result));
        }
    }

    private RequestReceipts Create(ReceiptStore store, Func<string?, string>? liveState = null, McpActivity? activity = null) =>
        new(store, () => Remote, liveState ?? (n => $"state of {n}: Run"), activity);

    // ---- the store ---------------------------------------------------------------------------------------------

    [Fact]
    public void Begin_AKeyIsNewOnlyOnce_AndTheReceiptSurvivesTheRestart()
    {
        var store = NewStore();
        var first = store.Begin(Key, "twincat_activate_config", "h1", Remote);
        Assert.True(first.Created);
        Assert.Equal(ReceiptOutcome.Running, first.Receipt.Outcome);

        var second = store.Begin(Key, "twincat_activate_config", "h1", Remote);
        Assert.False(second.Created);
        Assert.Equal(_now, second.Receipt.StartedUtc);

        store.Finish(Key, ReceiptOutcome.Ok, "activated");
        var done = store.Get(Key)!;
        Assert.Equal(ReceiptOutcome.Ok, done.Outcome);
        Assert.Equal("activated", done.ResultText);
        Assert.Equal(_now, done.FinishedUtc);

        var restarted = NewStore();
        var loaded = restarted.Get(Key)!;
        Assert.Equal(ReceiptOutcome.Ok, loaded.Outcome);
        Assert.Equal("activated", loaded.ResultText);
        Assert.Equal("h1", loaded.ArgumentHash);
        Assert.Equal(Remote, loaded.TargetNetId);
    }

    [Fact]
    public void ARequestThatWasRunningWhenTheHostWentAway_IsUnknownAfterTheRestart()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);

        var restarted = NewStore();

        var receipt = restarted.Get(Key)!;
        Assert.Equal(ReceiptOutcome.Unknown, receipt.Outcome);
        Assert.Null(receipt.FinishedUtc);
        Assert.Equal(ReceiptOutcome.Unknown, NewStore().Get(Key)!.Outcome);
        Assert.Single(File.ReadAllLines(FilePath));
    }

    [Fact]
    public void AReceiptIsKeptForSevenDays()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);
        store.Finish(Key, ReceiptOutcome.Ok, "x");
        store.Begin("another-key-0001", "twincat_activate_config", "h2", Remote);
        store.Finish("another-key-0001", ReceiptOutcome.Ok, "y");

        _now = _now.AddDays(6).AddHours(23);
        Assert.Equal(2, NewStore().Count);

        _now = _now.AddHours(2);
        var later = NewStore();
        Assert.Equal(0, later.Count);
        Assert.Null(later.Get(Key));
        Assert.Empty(File.ReadAllLines(FilePath).Where(l => l.Length > 0));
    }

    [Fact]
    public void ThePathAFileTakes_IsOneLinePerStateAndLastWins()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);
        store.Finish(Key, ReceiptOutcome.Error, "[Error] boom");

        var lines = File.ReadAllLines(FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"outcome\":\"running\"", lines[0]);
        Assert.Contains("\"outcome\":\"error\"", lines[1]);
        Assert.Equal(ReceiptOutcome.Error, NewStore().Get(Key)!.Outcome);
    }

    [Fact]
    public void AVoidedRequest_FreesTheKey_AlsoAfterARestart()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);
        store.Void(Key);

        Assert.Null(store.Get(Key));
        Assert.Null(NewStore().Get(Key));
        Assert.True(NewStore().Begin(Key, "twincat_activate_config", "h1", Remote).Created);
    }

    [Fact]
    public void ABrokenLine_IsSkipped_NotFatal()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);
        store.Finish(Key, ReceiptOutcome.Ok, "x");
        File.AppendAllText(FilePath, "this is not json\n{\"x\":1}\n");

        var loaded = NewStore();
        Assert.Equal(ReceiptOutcome.Ok, loaded.Get(Key)!.Outcome);
        Assert.Equal(1, loaded.Count);
    }

    [Fact]
    public void AStoreThatCannotBeWritten_DoesNotStartARequest()
    {
        string blocked = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocked, "x");
        var store = new ReceiptStore(Path.Combine(blocked, ReceiptStore.FileName), () => _now);

        var begin = store.Begin(Key, "twincat_activate_config", "h1", Remote);

        Assert.False(begin.Created);
        Assert.NotNull(begin.Failure);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ALongResult_IsCutAndSaysSo()
    {
        var store = NewStore();
        store.Begin(Key, "twincat_activate_config", "h1", Remote);
        store.Finish(Key, ReceiptOutcome.Ok, new string('x', ReceiptStore.MaxResultChars + 100));

        var text = store.Get(Key)!.ResultText!;
        Assert.True(text.Length < ReceiptStore.MaxResultChars + 100);
        Assert.Contains("was cut", text);
    }

    // ---- the filter ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutAKey_EveryCallRuns()
    {
        var sut = Create(NewStore());
        var body = new Body();

        // The filter only sees calls with a key; the key reader says there is none.
        Assert.Null(RequestReceipts.KeyOf(Args(new { dryRun = false })));
        Assert.Null(RequestReceipts.KeyOf(Args(new { requestKey = "  " })));
        Assert.Null(RequestReceipts.KeyOf(null));

        // A tool that takes no key is not remembered, key or not.
        await sut.RunAsync("twincat_get_status", Key, null, body.Run);
        await sut.RunAsync("twincat_get_status", Key, null, body.Run);
        Assert.Equal(2, body.Runs);
    }

    [Fact]
    public async Task TheSameKeyAndArguments_RunOnce_ARepeatGetsTheStoredResult()
    {
        var store = NewStore();
        var sut = Create(store);
        var body = new Body("activated 1");
        var args = Args(new { dryRun = false });

        var first = await sut.RunAsync("twincat_activate_boot_project", Key, args, body.Run);
        var again = await sut.RunAsync("twincat_activate_boot_project", Key, args, body.Run);
        var third = await sut.RunAsync("twincat_activate_boot_project", Key, args, body.Run);

        Assert.Equal(1, body.Runs);
        Assert.Equal(["activated 1"], Texts(first));
        Assert.Equal(2, Texts(again).Length);
        Assert.Equal("activated 1", Texts(again)[0]);
        Assert.StartsWith("[Receipt] Repeated request", Texts(again)[1]);
        Assert.Contains("Nothing was run again", Texts(again)[1]);
        Assert.Equal(Texts(again), Texts(third));
        Assert.NotEqual(true, again.IsError);
        Assert.Equal(ReceiptOutcome.Ok, store.Get(Key)!.Outcome);
    }

    [Fact]
    public async Task TheSameKeyWithOtherArgumentsOrAnotherTool_IsRefused()
    {
        var sut = Create(NewStore());
        var body = new Body();
        await sut.RunAsync("twincat_write_ads_variable", Key, Args(new { symbolPath = "MAIN.x", value = "1", amsNetId = Remote }), body.Run);

        var otherValue = await sut.RunAsync("twincat_write_ads_variable", Key, Args(new { symbolPath = "MAIN.x", value = "2", amsNetId = Remote }), body.Run);
        var otherTool = await sut.RunAsync("twincat_plc_start", Key, Args(new { amsNetId = Remote }), body.Run);
        var otherTarget = await sut.RunAsync("twincat_write_ads_variable", Key, Args(new { symbolPath = "MAIN.x", value = "1", amsNetId = "10.2.2.2.1.1" }), body.Run);

        Assert.Equal(1, body.Runs);
        foreach (var refused in new[] { otherValue, otherTool, otherTarget })
        {
            Assert.True(refused.IsError);
            Assert.StartsWith("[Error] requestKey '" + Key + "' belongs to another request", Texts(refused)[0]);
            Assert.Contains("Nothing was done", Texts(refused)[0]);
        }
    }

    [Fact]
    public async Task ArgumentOrderNullsAndTheReservedNames_DoNotMakeADifferentRequest()
    {
        var sut = Create(NewStore());
        var body = new Body();

        await sut.RunAsync("twincat_write_ads_variable", Key, Args(new { symbolPath = "MAIN.x", value = "1", amsNetId = Remote }), body.Run);
        await sut.RunAsync("twincat_write_ads_variable", Key,
            Args(new { value = "1", adsPort = (int?)null, amsNetId = Remote, symbolPath = "MAIN.x", requestKey = Key, confirmationToken = "t" }), body.Run);

        Assert.Equal(1, body.Runs);
    }

    [Fact]
    public async Task ARunningRequest_IsReportedAsRunning_AndNotStartedAgain()
    {
        var store = NewStore();
        var sut = Create(store);
        var gate = new TaskCompletionSource<CallToolResult>();
        int runs = 0;
        var slow = () => { runs++; return new ValueTask<CallToolResult>(gate.Task); };
        var args = Args(new { dryRun = false });

        var first = sut.RunAsync("twincat_activate_boot_project", Key, args, slow).AsTask();
        _now = _now.AddSeconds(5);
        var second = await sut.RunAsync("twincat_activate_boot_project", Key, args, slow);

        Assert.Equal(1, runs);
        Assert.StartsWith("[Receipt] running: twincat_activate_boot_project", Texts(second)[0]);
        Assert.Contains("twincat_receipt_get", Texts(second)[0]);
        Assert.Equal(ReceiptOutcome.Running, store.Get(Key)!.Outcome);

        gate.SetResult(Ok("done"));
        Assert.Equal(["done"], Texts(await first));
        Assert.Equal(ReceiptOutcome.Ok, store.Get(Key)!.Outcome);
    }

    [Fact]
    public async Task AnUnknownOutcome_IsNeverRetried_AndShowsTheLiveState()
    {
        var first = NewStore();
        first.Begin(Key, "twincat_restart_runtime", HumanConfirmation.ArgumentHash(null), Remote); // the host went away
        var sut = Create(NewStore(), liveState: n => $"{n}: TwinCAT system Run, PLC Stop");
        var body = new Body();

        var result = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        Assert.Equal(0, body.Runs);
        Assert.NotEqual(true, result.IsError);
        string text = Texts(result).Single();
        Assert.StartsWith("[Warn] outcome_unknown: twincat_restart_runtime", text);
        Assert.Contains("10.1.1.1.1.1: TwinCAT system Run, PLC Stop", text);
        Assert.Contains("NEW requestKey", text);
    }

    [Fact]
    public async Task ACancelledCall_BecomesUnknown_AndRethrows()
    {
        var store = NewStore();
        var sut = Create(store);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await sut.RunAsync("twincat_restart_runtime", Key, null, () => throw new OperationCanceledException()));

        Assert.Equal(ReceiptOutcome.Unknown, store.Get(Key)!.Outcome);
        var body = new Body();
        var again = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);
        Assert.Equal(0, body.Runs);
        Assert.StartsWith("[Warn] outcome_unknown", Texts(again)[0]);
    }

    [Fact]
    public async Task AFailedCall_IsRecordedAsError_AndRepeatedAsTheSameError()
    {
        var store = NewStore();
        var sut = Create(store);
        var body = new Body { Custom = () => Ok("[Error] The runtime did not reach Run.") };

        var first = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);
        var again = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        Assert.Equal(1, body.Runs);
        Assert.Equal(ReceiptOutcome.Error, store.Get(Key)!.Outcome);
        Assert.Equal("[Error] The runtime did not reach Run.", Texts(again)[0]);
        Assert.Equal(first.Content.Count + 1, again.Content.Count);
    }

    [Theory]
    [InlineData("[Confirm] twincat_x needs a human's release. Nothing was done yet.")]
    [InlineData("[Error] Access policy 'read' blocks twincat_x (needs 'runtime').")]
    [InlineData("[Error] An operation is already in progress.")]
    [InlineData("[Error] Declined by the human. Nothing was done.")]
    [InlineData("[Error] Something refused. Nothing was done.")]
    public async Task ARefusalThatDidNothing_FreesTheKey(string refusal)
    {
        var store = NewStore();
        var sut = Create(store);
        var body = new Body { Custom = () => Ok(refusal) };

        await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        Assert.Null(store.Get(Key));
        body.Custom = () => Ok("restarted");
        var retry = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);
        Assert.Equal(2, body.Runs);
        Assert.Equal(["restarted"], Texts(retry));
    }

    [Theory]
    [InlineData("[Blocked] twincat_x waits for an XAE dialog.")]
    [InlineData("[Error] Cancelled: call cancelled")]
    public async Task ACallHeldOrCancelledMidFlight_IsUnknown(string text)
    {
        var store = NewStore();
        var sut = Create(store);

        await sut.RunAsync("twincat_restart_runtime", Key, null, new Body { Custom = () => Ok(text) }.Run);

        Assert.Equal(ReceiptOutcome.Unknown, store.Get(Key)!.Outcome);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has space 123")]
    [InlineData("bad/chars/in/key")]
    [InlineData("")]
    public async Task AMalformedKey_IsRefused_NothingRuns(string key)
    {
        var body = new Body();
        var result = await Create(NewStore()).RunAsync("twincat_restart_runtime", key, null, body.Run);

        Assert.True(result.IsError);
        Assert.StartsWith("[Error] requestKey must be 8 to 128 characters", Texts(result)[0]);
        Assert.Equal(0, body.Runs);
    }

    [Fact]
    public async Task AStoreThatCannotBeWritten_RefusesTheKeyedCall()
    {
        string blocked = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocked, "x");
        var sut = Create(new ReceiptStore(Path.Combine(blocked, ReceiptStore.FileName), () => _now));
        var body = new Body();

        var result = await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        Assert.True(result.IsError);
        Assert.Contains("was not started", Texts(result)[0]);
        Assert.Equal(0, body.Runs);
    }

    [Fact]
    public async Task EveryReplayIsNotedInTheActivityLog()
    {
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir));
        var sut = Create(NewStore(), activity: activity);
        var body = new Body();
        await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        await sut.RunAsync("twincat_restart_runtime", Key, null, body.Run);

        var entry = Assert.Single(activity.Log.Snapshot());
        Assert.Equal("twincat_restart_runtime (receipt)", entry.ToolName);
        Assert.Contains("Repeated request", entry.Response);
    }

    // ---- the catalog and the schema --------------------------------------------------------------------------------------

    [Fact]
    public void TheToolsThatTakeAKey_AreEveryAskedToolAndTheRestore()
    {
        Assert.All(ToolCatalog.Tools.Values.Where(t => t.Confirm != ConfirmClass.Never), t => Assert.True(t.Receipts, t.Name));
        foreach (var name in new[] { "twincat_activate_config", "twincat_restart_runtime", "twincat_activate_boot_project",
                     "twincat_persistent_snapshot_restore", "twincat_write_ads_variable" })
            Assert.True(ToolCatalog.Tools[name].Receipts, name);
        Assert.False(ToolCatalog.Tools["twincat_read_ads_variable"].Receipts);
        Assert.False(ToolCatalog.Tools["twincat_stop"].Receipts);
        Assert.False(ToolCatalog.Tools["twincat_plc_write"].Receipts);
    }

    [Fact]
    public void TheReceiptTool_IsReadOnlyAndAlwaysVisible()
    {
        var tool = ToolCatalog.Tools["twincat_receipt_get"];
        Assert.Equal(Toolset.Meta, tool.Toolset);
        Assert.Equal(AccessLevel.Read, tool.Level);
        Assert.True(tool.ReadOnly);
        Assert.False(tool.Receipts);
    }

    [Fact]
    public void TheKeyArgument_IsAddedToTheToolsThatTakeOne()
    {
        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "dryRun": { "type": "boolean" } } }""").RootElement;

        var restore = ReservedArguments.WithReserved("twincat_persistent_snapshot_restore", schema)!.Value;
        var props = restore.GetProperty("properties");
        Assert.True(props.TryGetProperty("requestKey", out _));
        Assert.False(props.TryGetProperty("confirmationToken", out _), "the restore has its own confirmation");

        var write = ReservedArguments.WithReserved("twincat_write_ads_variable", schema)!.Value.GetProperty("properties");
        Assert.True(write.TryGetProperty("requestKey", out _));
        Assert.True(write.TryGetProperty("confirmationToken", out _));

        Assert.Null(ReservedArguments.WithReserved("twincat_get_status", schema));
        Assert.Empty(UnknownArguments.Find(restore, ["requestKey", "dryRun"]));
    }

    [Fact]
    public void ThePathPolicy_KeepsTheReceiptsAwayFromFileTools()
    {
        Assert.False(PtcXaeMcp.WinForms.Security.PathPolicy.IsAllowed(ReceiptStore.DefaultPath, null, out var reason));
        Assert.Contains("request receipts", reason);
    }
}
