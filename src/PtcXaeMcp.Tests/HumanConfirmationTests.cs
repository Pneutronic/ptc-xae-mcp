using System.Text.Json;
using ModelContextProtocol.Protocol;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>The human confirmation before an action with effect on a target system: rules, token path, in-band question.</summary>
public sealed class HumanConfirmationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "confirm_" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    public HumanConfirmationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Remote = "10.1.1.1.1.1";
    private const string Local = "127.0.0.1.1.1";

    private static AccessPolicy Policy(ConfirmMode? mode, AccessLevel level = AccessLevel.Runtime) =>
        new(level, AccessPolicy.ParseList(null), null, "test", Confirm: mode);

    private sealed class FakeAsker(ConfirmAnswer answer, bool canAsk = true, TimeSpan? hang = null) : IConfirmAsker
    {
        public bool CanAsk => canAsk;
        public List<string> Questions { get; } = [];

        public async Task<ConfirmAnswer> AskAsync(string message, CancellationToken cancellationToken)
        {
            Questions.Add(message);
            if (hang is { } wait) await Task.Delay(wait, cancellationToken);
            return answer;
        }
    }

    private (HumanConfirmation Sut, ConfirmationRegistry Registry) Create(ConfirmMode? mode, string? projectTarget = Remote,
        AccessLevel level = AccessLevel.Runtime, TimeSpan? askTimeout = null)
    {
        var registry = new ConfirmationRegistry(() => _now);
        var sut = new HumanConfirmation(() => Policy(mode, level), () => projectTarget, () => [], registry, null, () => [Local])
        {
            AskTimeout = askTimeout ?? TimeSpan.FromSeconds(5),
        };
        return (sut, registry);
    }

    private static Dictionary<string, JsonElement> Args(object value) =>
        JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static string TextOf(CallToolResult result) => ((TextContentBlock)Assert.Single(result.Content)).Text;

    // ---- the rule ---------------------------------------------------------------------------------------------

    [Theory]
    // Always-class: every target under all, other systems under remote, never under off.
    [InlineData("twincat_activate_config", "Off", false, false)]
    [InlineData("twincat_activate_config", "Off", true, false)]
    [InlineData("twincat_activate_config", "Remote", false, true)]
    [InlineData("twincat_activate_config", "Remote", true, false)]
    [InlineData("twincat_activate_config", "All", false, true)]
    [InlineData("twincat_activate_config", "All", true, true)]
    // Runtime-class: only other systems.
    [InlineData("twincat_write_ads_variable", "Remote", false, true)]
    [InlineData("twincat_write_ads_variable", "Remote", true, false)]
    [InlineData("twincat_write_ads_variable", "All", true, false)]
    [InlineData("twincat_plc_start", "Remote", false, true)]
    [InlineData("twincat_login", "Remote", false, true)]
    // Never-class: neither reads nor project changes nor what a window popup confirms already.
    [InlineData("twincat_read_ads_variable", "All", false, false)]
    [InlineData("twincat_plc_write", "All", false, false)]
    [InlineData("twincat_persistent_snapshot_restore", "All", false, false)]
    [InlineData("twincat_hmi_write_symbol", "All", false, false)]
    // Emergency stops are never asked.
    [InlineData("twincat_stop", "All", false, false)]
    [InlineData("twincat_ibn_abort", "All", false, false)]
    public void Rule_FollowsClassModeAndTarget(string tool, string mode, bool isLocal, bool expected) =>
        Assert.Equal(expected, ConfirmRules.Needs(ToolCatalog.Tools[tool], Enum.Parse<ConfirmMode>(mode), isLocal, new { dryRun = false }));

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", false)]
    [InlineData("false", true)]
    [InlineData("FALSE", true)]
    public void Rule_ADryRunIsNeverAsked(string? dryRun, bool expected)
    {
        object args = dryRun is null ? new { } : new Dictionary<string, object?> { ["dryRun"] = dryRun };
        Assert.Equal(expected, ConfirmRules.Needs(ToolCatalog.Tools["twincat_activate_boot_project"], ConfirmMode.All, false, args));
    }

    [Theory]
    [InlineData("login_without_change", false)]
    [InlineData("download", true)]
    [InlineData("online_change", true)]
    public void Rule_AMonitoringLoginIsNotAnAction(string mode, bool expected) =>
        Assert.Equal(expected, ConfirmRules.Needs(ToolCatalog.Tools["twincat_login"], ConfirmMode.Remote, false, new { mode }));

    [Fact]
    public void EveryRuntimeActionThatIsNotAsked_IsNamedWithItsReason()
    {
        // A new runtime action must choose: be asked, or be listed here with the reason it need not be.
        string[] notAsked =
        [
            "twincat_persistent_snapshot_restore", // a human answers the host popup before anything is written
            "twincat_hmi_write_symbol", "twincat_hmi_server_control", // HMI writes: popup; server control: this machine only
            "twincat_hmi_publish", // a human answers the host popup (target, profile, what runs there) before the publish
            "twincat_umr_start", "twincat_umr_stop", // this machine's User Mode Runtime only
            "twincat_ibn_propose", "twincat_persistent_snapshot_test_confirm", // act on a target an earlier, checked call chose
            "twincat_confirm_event_logger", // acknowledges a log entry
            "twincat_set_target", // changes the project's target only; activating it is asked
            "twincat_generate_trial_licenses", "twincat_open_license_manager_and_request_trial", "twincat_submit_security_code", // license dialogs: a human types the code
        ];
        var undecided = ToolCatalog.Tools.Values
            .Where(t => t.Level == AccessLevel.Runtime && !t.AlwaysAllowed && t.Confirm == ConfirmClass.Never)
            .Select(t => t.Name).Except(notAsked).Order().ToList();
        Assert.True(undecided.Count == 0, "Runtime actions without a Confirm class (ToolCatalog): " + string.Join(", ", undecided));
        Assert.All(notAsked.Where(n => ToolCatalog.Tools.ContainsKey(n)), n => Assert.Equal(ConfirmClass.Never, ToolCatalog.Tools[n].Confirm));
    }

    [Fact]
    public void TheClasses_MatchTheDesign()
    {
        foreach (var name in new[] { "twincat_activate_config", "twincat_restart_runtime", "twincat_set_system_mode", "twincat_plc_stop",
                     "twincat_activate_boot_project", "twincat_umr_set_mode", "twincat_ibn_arm" })
            Assert.Equal(ConfirmClass.Always, ToolCatalog.Tools[name].Confirm);
        foreach (var name in new[] { "twincat_write_ads_variable", "twincat_login", "twincat_plc_start", "twincat_add_route" })
            Assert.Equal(ConfirmClass.Runtime, ToolCatalog.Tools[name].Confirm);
        Assert.All(ToolCatalog.Tools.Values.Where(t => t.ReadOnly || t.AlwaysAllowed), t => Assert.Equal(ConfirmClass.Never, t.Confirm));
        Assert.All(ToolCatalog.Tools.Values.Where(t => t.Level == AccessLevel.Engineer), t => Assert.Equal(ConfirmClass.Never, t.Confirm));
    }

    // ---- the policy key ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("off", "Off")]
    [InlineData("OFF", "Off")]
    [InlineData(" remote ", "Remote")]
    [InlineData("all", "All")]
    [InlineData("never", "All")]
    [InlineData("", "All")]
    public void Confirm_AnUnreadableValueIsTheStrictestMode(string text, string expected) =>
        Assert.Equal(Enum.Parse<ConfirmMode>(expected), AccessPolicy.ParseConfirm(text));

    [Fact]
    public void Confirm_DefaultsToRemote_AndNoSettingIsNoPolicy()
    {
        Assert.Equal(ConfirmMode.Remote, AccessPolicy.Unrestricted.EffectiveConfirm);
        Assert.Null(AccessPolicy.FromEnvironment(_ => null));
        Assert.Equal(ConfirmMode.All, AccessPolicy.FromEnvironment(n => n == AccessPolicy.ConfirmEnvVar ? "all" : null)!.Confirm);
        Assert.Equal(AccessLevel.Runtime, AccessPolicy.FromEnvironment(n => n == AccessPolicy.ConfirmEnvVar ? "off" : null)!.Level);
    }

    [Fact]
    public void Confirm_TheStricterOfTwoSourcesWins()
    {
        Assert.Equal(ConfirmMode.All, AccessPolicy.Merge(Policy(ConfirmMode.Off), Policy(ConfirmMode.All)).Confirm);
        Assert.Equal(ConfirmMode.Remote, AccessPolicy.Merge(Policy(ConfirmMode.Remote), Policy(ConfirmMode.Off)).Confirm);
        Assert.Equal(ConfirmMode.Off, AccessPolicy.Merge(Policy(null), Policy(ConfirmMode.Off)).Confirm);
        Assert.Null(AccessPolicy.Merge(Policy(null), Policy(null)).Confirm);
    }

    [Fact]
    public void Confirm_IsKeptInTheFile_AndABrokenValueIsAll()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        Policy(ConfirmMode.All).Save(path);
        Assert.Contains("\"confirm\": \"all\"", File.ReadAllText(path));
        Assert.Equal(ConfirmMode.All, AccessPolicy.Load(path)!.Confirm);

        Policy(null).Save(path);
        Assert.DoesNotContain("confirm", File.ReadAllText(path));
        Assert.Null(AccessPolicy.Load(path)!.Confirm);

        Assert.Equal(ConfirmMode.All, AccessPolicy.Parse("""{ "confirm": 3 }""").Confirm);
        Assert.Equal(ConfirmMode.All, AccessPolicy.Parse("""{ "confirm": "sometimes" }""").Confirm);
        Assert.Equal(ConfirmMode.Off, AccessPolicy.Parse("""{ "confirm": "off" }""").Confirm);
    }

    [Fact]
    public void Summary_NamesTheMode() =>
        Assert.Contains("human confirmation: remote", AccessPolicy.Unrestricted.Summary());

    [Fact]
    public void StatusLine_SaysHowTheClientIsAsked()
    {
        var capable = new ClientCapabilities { Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() } };
        Assert.Contains("is asked in its own window", HumanConfirmation.StatusLine(AccessPolicy.Unrestricted, capable));
        Assert.Contains("does not support MCP elicitation", HumanConfirmation.StatusLine(AccessPolicy.Unrestricted, new ClientCapabilities()));
        Assert.Contains("does not support MCP elicitation", HumanConfirmation.StatusLine(AccessPolicy.Unrestricted, null));
        Assert.StartsWith("Human confirmation: off", HumanConfirmation.StatusLine(Policy(ConfirmMode.Off), capable));
        Assert.Contains("human confirmation: all", Policy(ConfirmMode.All).Summary());
    }

    [Fact]
    public void Elicitation_AnOldClientWithAnEmptyCapabilityCanBeAsked_AUrlOnlyClientCannot()
    {
        Assert.False(HumanConfirmation.ServerAsker.CanElicit(null));
        Assert.False(HumanConfirmation.ServerAsker.CanElicit(new ClientCapabilities()));
        Assert.True(HumanConfirmation.ServerAsker.CanElicit(new ClientCapabilities { Elicitation = new ElicitationCapability() }));
        Assert.True(HumanConfirmation.ServerAsker.CanElicit(
            new ClientCapabilities { Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() } }));
        Assert.False(HumanConfirmation.ServerAsker.CanElicit(
            new ClientCapabilities { Elicitation = new ElicitationCapability { Url = new UrlElicitationCapability() } }));
    }

    // ---- the in-band question ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Off_NeverAsks()
    {
        var (sut, _) = Create(ConfirmMode.Off);
        var asker = new FakeAsker(ConfirmAnswer.No);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));
        Assert.Empty(asker.Questions);
    }

    [Fact]
    public async Task ALocalTarget_IsNotAsked_UnderRemote()
    {
        var (sut, _) = Create(ConfirmMode.Remote, projectTarget: Local);
        var asker = new FakeAsker(ConfirmAnswer.No);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));
        Assert.Null(await sut.CheckAsync("twincat_write_ads_variable", Args(new { symbolPath = "MAIN.x", value = "1", amsNetId = "local" }), asker, default));
        Assert.Empty(asker.Questions);
    }

    [Fact]
    public async Task ALocalTarget_IsAsked_UnderAll_ForTheLifecycleClass()
    {
        var (sut, _) = Create(ConfirmMode.All, projectTarget: Local);
        var asker = new FakeAsker(ConfirmAnswer.Yes);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));
        Assert.Single(asker.Questions);

        Assert.Null(await sut.CheckAsync("twincat_write_ads_variable", Args(new { symbolPath = "MAIN.x", value = "1" }), asker, default));
        Assert.Single(asker.Questions);
    }

    [Fact]
    public async Task ARemoteTarget_IsAsked_AndAYesRunsTheTool()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));

        string question = Assert.Single(asker.Questions);
        Assert.Contains("twincat_activate_config", question);
        Assert.Contains(Remote, question);
        Assert.Contains("another system", question);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task TheQuestion_NamesTheEffectiveArguments_AndHidesSecrets()
    {
        var (sut, _) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes);

        await sut.CheckAsync("twincat_write_ads_variable",
            Args(new { symbolPath = "MAIN.bStart", value = "true", amsNetId = Remote, adsPort = 851, password = "hunter2", confirmationToken = "abc" }),
            asker, default);

        string question = Assert.Single(asker.Questions);
        Assert.Contains("symbolPath=MAIN.bStart", question);
        Assert.Contains("value=true", question);
        Assert.Contains("adsPort=851", question);
        Assert.Contains("password=***", question);
        Assert.DoesNotContain("hunter2", question);
        Assert.DoesNotContain("abc", question);
    }

    [Fact]
    public async Task ADecline_RefusesTheCall_NothingRuns()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);

        var result = await sut.CheckAsync("twincat_activate_config", Args(new { }), new FakeAsker(ConfirmAnswer.No), default);

        Assert.NotNull(result);
        Assert.True(result!.IsError);
        Assert.Equal("[Error] Declined by the human. Nothing was done.", TextOf(result));
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task AnUnansweredQuestion_FallsBackToTheTokenPath_AndNeverHangs()
    {
        var (sut, registry) = Create(ConfirmMode.Remote, askTimeout: TimeSpan.FromMilliseconds(100));
        var asker = new FakeAsker(ConfirmAnswer.Yes, hang: TimeSpan.FromSeconds(30));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.StartsWith("[Confirm] twincat_activate_config", TextOf(result!));
        Assert.Contains("did not answer", TextOf(result!));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public async Task ACallerThatCancels_StaysCancelled()
    {
        var (sut, _) = Create(ConfirmMode.Remote);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.CheckAsync("twincat_activate_config", Args(new { }), new FakeAsker(ConfirmAnswer.Yes, hang: TimeSpan.FromSeconds(30)), cts.Token));
    }

    [Fact]
    public async Task AToolThatChangesNothing_IsNeverAsked()
    {
        var (sut, _) = Create(ConfirmMode.All);
        var asker = new FakeAsker(ConfirmAnswer.No);

        Assert.Null(await sut.CheckAsync("twincat_get_status", null, asker, default));
        Assert.Null(await sut.CheckAsync("twincat_plc_write", Args(new { }), asker, default));
        Assert.Null(await sut.CheckAsync("twincat_stop", null, asker, default));
        Assert.Null(await sut.CheckAsync("not_a_tool", null, asker, default));
        Assert.Null(await sut.CheckAsync("twincat_activate_boot_project", Args(new { dryRun = true }), asker, default));
        Assert.Empty(asker.Questions);
    }

    [Fact]
    public async Task AnUnknownTarget_IsAsked()
    {
        var (sut, _) = Create(ConfirmMode.Remote, projectTarget: null);
        var asker = new FakeAsker(ConfirmAnswer.Yes);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));
        Assert.Contains("an unknown target system", Assert.Single(asker.Questions));
    }

    [Fact]
    public async Task ACallThePolicyRefusesAnyway_IsNotAsked()
    {
        var registry = new ConfirmationRegistry(() => _now);
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir))
        {
            AccessGate = (_, _) => new AccessDecision("[Error] Access policy blocks it.", null),
        };
        var sut = new HumanConfirmation(() => Policy(ConfirmMode.Remote), () => Remote, () => [], registry, activity, () => [Local]);
        var asker = new FakeAsker(ConfirmAnswer.Yes);

        Assert.Null(await sut.CheckAsync("twincat_activate_config", Args(new { }), asker, default));
        Assert.Empty(asker.Questions);
        Assert.Empty(registry.Snapshot());
    }

    [Fact]
    public async Task EveryQuestionAndItsAnswer_GoIntoTheActivityLog()
    {
        var registry = new ConfirmationRegistry(() => _now);
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir));
        var sut = new HumanConfirmation(() => Policy(ConfirmMode.Remote), () => Remote, () => [], registry, activity, () => [Local]);

        await sut.CheckAsync("twincat_activate_config", Args(new { }), new FakeAsker(ConfirmAnswer.Yes), default);
        await sut.CheckAsync("twincat_activate_config", Args(new { }), new FakeAsker(ConfirmAnswer.No), default);
        await sut.CheckAsync("twincat_activate_config", Args(new { }), new FakeAsker(ConfirmAnswer.Yes, canAsk: false), default);

        var entries = activity.Log.Snapshot();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("twincat_activate_config (confirmation)", e.ToolName));
        Assert.Equal([true, false, false], entries.Select(e => e.Success));
        Assert.Contains("confirmed by the human", entries[0].Response);
        Assert.Contains("Declined", entries[1].Response);
        Assert.Contains("[Confirm]", entries[2].Response);
    }

    // ---- the token path ------------------------------------------------------------------------------------------------

    private static string TokenOf(CallToolResult result)
    {
        var match = System.Text.RegularExpressions.Regex.Match(TextOf(result), "confirmationToken=\"([0-9a-f]{32})\"");
        Assert.True(match.Success, TextOf(result));
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task AClientWithoutElicitation_GetsAToken_ReleasedOnlyInTheWindow()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes, canAsk: false);

        var first = await sut.CheckAsync("twincat_restart_runtime", null, asker, default);

        Assert.False(first!.IsError);
        Assert.StartsWith("[Confirm] twincat_restart_runtime on " + Remote, TextOf(first));
        Assert.Contains("Nothing was done yet", TextOf(first));
        Assert.Empty(asker.Questions);
        var entry = Assert.Single(registry.Snapshot());
        Assert.Equal(ConfirmationState.Pending, entry.State);
        string token = TokenOf(first);
        Assert.Equal(token, entry.Token);
        Assert.Contains(entry.Code, TextOf(first));
        Assert.Equal(1, registry.PendingCount);

        // The agent repeats the call with the token before a human released it.
        var early = await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = token }), asker, default);
        Assert.Contains("is not released yet", TextOf(early!));
        Assert.Contains(entry.Code, TextOf(early!));

        Assert.True(registry.Release(entry.Code));

        Assert.Null(await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = token }), asker, default));
        Assert.Equal(ConfirmationState.Used, Assert.Single(registry.Snapshot()).State);

        // One time: the same token does not work twice.
        var again = await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = token }), asker, default);
        Assert.StartsWith("[Confirm]", TextOf(again!));
        Assert.Contains("already used", TextOf(again!));
        Assert.NotEqual(token, TokenOf(again!));
    }

    [Fact]
    public async Task AToken_IsBoundToTheArgumentsAndTheTarget()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes, canAsk: false);
        var first = await sut.CheckAsync("twincat_write_ads_variable",
            Args(new { symbolPath = "MAIN.bStart", value = "true", amsNetId = Remote }), asker, default);
        string token = TokenOf(first!);
        registry.Release(registry.Snapshot()[0].Code);

        // another value
        var other = await sut.CheckAsync("twincat_write_ads_variable",
            Args(new { symbolPath = "MAIN.bStart", value = "false", amsNetId = Remote, confirmationToken = token }), asker, default);
        Assert.Contains("different tool, arguments or target", TextOf(other!));
        // another target
        var elsewhere = await sut.CheckAsync("twincat_write_ads_variable",
            Args(new { symbolPath = "MAIN.bStart", value = "true", amsNetId = "10.2.2.2.1.1", confirmationToken = token }), asker, default);
        Assert.Contains("different tool, arguments or target", TextOf(elsewhere!));
        // another tool
        var tool = await sut.CheckAsync("twincat_plc_start", Args(new { amsNetId = Remote, confirmationToken = token }), asker, default);
        Assert.Contains("different tool, arguments or target", TextOf(tool!));

        // the order of the arguments and an explicit null do not matter
        Assert.Null(await sut.CheckAsync("twincat_write_ads_variable",
            Args(new { value = "true", adsPort = (int?)null, symbolPath = "MAIN.bStart", amsNetId = Remote, confirmationToken = token }), asker, default));
    }

    [Fact]
    public async Task AToken_ExpiresAfterItsLifetime()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes, canAsk: false);
        var first = await sut.CheckAsync("twincat_restart_runtime", null, asker, default);
        string token = TokenOf(first!);
        registry.Release(registry.Snapshot()[0].Code);

        _now = _now.AddSeconds(ConfirmationRegistry.TtlSeconds + 1);

        var late = await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = token }), asker, default);
        Assert.Contains("expired", TextOf(late!));
        Assert.Equal(ConfirmationState.Expired, registry.Snapshot()[0].State);
    }

    [Fact]
    public async Task ADeniedRequest_NeverWorks()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes, canAsk: false);
        string token = TokenOf((await sut.CheckAsync("twincat_restart_runtime", null, asker, default))!);
        Assert.True(registry.Deny(registry.Snapshot()[0].Code));
        Assert.False(registry.Release(registry.Snapshot()[0].Code), "a denied request cannot be released afterwards");

        var result = await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = token }), asker, default);

        Assert.True(result!.IsError);
        Assert.Contains("Declined by the human in the window", TextOf(result));
    }

    [Fact]
    public async Task AnUnknownToken_OpensANewRequest()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var result = await sut.CheckAsync("twincat_restart_runtime", Args(new { confirmationToken = "nope" }),
            new FakeAsker(ConfirmAnswer.Yes, canAsk: false), default);

        Assert.Contains("is not known", TextOf(result!));
        Assert.StartsWith("[Confirm]", TextOf(result!));
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public async Task TheSameCallAskingAgain_ReusesItsRequest()
    {
        var (sut, registry) = Create(ConfirmMode.Remote);
        var asker = new FakeAsker(ConfirmAnswer.Yes, canAsk: false);

        string a = TokenOf((await sut.CheckAsync("twincat_restart_runtime", null, asker, default))!);
        string b = TokenOf((await sut.CheckAsync("twincat_restart_runtime", null, asker, default))!);

        Assert.Equal(a, b);
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void TheRegistry_KeepsOnlyTheMostRecentRequests()
    {
        var registry = new ConfirmationRegistry(() => _now);
        for (int i = 0; i < 130; i++)
            registry.Issue("twincat_restart_runtime", "hash" + i, Remote, "");
        Assert.Equal(100, registry.Snapshot().Count);
    }

    [Fact]
    public void TheRegistry_AnnouncesEveryChange()
    {
        var registry = new ConfirmationRegistry(() => _now);
        int changes = 0;
        registry.Changed += () => changes++;

        var entry = registry.Issue("twincat_restart_runtime", "h", Remote, "");
        registry.Release(entry.Code);
        registry.Redeem(entry.Token, "twincat_restart_runtime", "h", Remote);

        Assert.Equal(3, changes);
    }

    [Fact]
    public void TheRegistry_ACodeThatIsNotThereReleasesNothing()
    {
        var registry = new ConfirmationRegistry(() => _now);
        Assert.False(registry.Release("deadbeef"));
        Assert.False(registry.Deny("deadbeef"));
    }

    // ---- the arguments ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ArgumentHash_IgnoresOrderNullsAndReservedNames_ButSeesEveryValue()
    {
        string a = HumanConfirmation.ArgumentHash(Args(new { x = 1, y = new { b = 2, a = 1 }, z = "t" }));
        string b = HumanConfirmation.ArgumentHash(Args(new { z = "t", y = new { a = 1, b = 2 }, x = 1, n = (string?)null, confirmationToken = "q", requestKey = "k" }));
        Assert.Equal(a, b);
        Assert.NotEqual(a, HumanConfirmation.ArgumentHash(Args(new { x = 2, y = new { b = 2, a = 1 }, z = "t" })));
        Assert.NotEqual(a, HumanConfirmation.ArgumentHash(Args(new { x = 1, y = new { b = 2, a = 1 }, z = "T" })));
        Assert.Equal(HumanConfirmation.ArgumentHash(null), HumanConfirmation.ArgumentHash(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void Summarise_IsShort_AndNamesNoSecret()
    {
        Assert.Equal("", HumanConfirmation.Summarise(null));
        string line = HumanConfirmation.Summarise(Args(new { value = new string('x', 500), userName = "u", password = "p", token = "t" }));
        Assert.Contains("password=***", line);
        Assert.Contains("token=***", line);
        Assert.Contains("userName=u", line);
        Assert.True(line.Length <= 410);
    }

    // ---- the schema ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheTokenArgument_IsAddedToTheToolsThatCanAsk_AndNoOther()
    {
        var schema = JsonDocument.Parse("""{ "type": "object", "properties": { "mode": { "type": "string" } } }""").RootElement;

        var withToken = ReservedArguments.WithReserved("twincat_activate_config", schema);
        Assert.NotNull(withToken);
        Assert.Equal("string", withToken!.Value.GetProperty("properties").GetProperty("confirmationToken").GetProperty("type").GetString());
        Assert.True(withToken.Value.GetProperty("properties").TryGetProperty("mode", out _));

        Assert.Null(ReservedArguments.WithReserved("twincat_get_status", schema));
        Assert.Null(ReservedArguments.WithReserved("twincat_plc_write", schema));
        Assert.Null(ReservedArguments.WithReserved("twincat_activate_config", withToken.Value));
    }

    [Fact]
    public void TheTokenArgument_IsNotAnUnknownArgument()
    {
        var schema = JsonDocument.Parse("""{ "type": "object", "properties": {} }""").RootElement;
        var withToken = ReservedArguments.WithReserved("twincat_login", schema)!.Value;

        Assert.Empty(UnknownArguments.Find(withToken, ["confirmationToken", "mode"]).Where(a => a == "confirmationToken"));
        Assert.Contains("confirmationToken", UnknownArguments.Find(schema, ["confirmationToken"]));
    }

    [Fact]
    public void TheGuiIsTheOnlyCallerOfRelease()
    {
        string root = FindSource();
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("Confirmations.Release(", StringComparison.Ordinal)
                        || File.ReadAllText(f).Contains("Confirmations.Deny(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)).Distinct().ToList();

        Assert.Equal(["ApprovalsView.Confirmations.cs"], callers);
    }

    private static string FindSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/PtcXaeMcp.WinForms not found from " + AppContext.BaseDirectory);
    }
}
