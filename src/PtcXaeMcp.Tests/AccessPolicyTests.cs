using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.Theme;

namespace PtcXaeMcp.Tests;

/// <summary>The access policy: levels, NetId rules, merging, the file, the gate in front of every tool.</summary>
public sealed class AccessPolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "policy_" + Guid.NewGuid().ToString("N"));

    public AccessPolicyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlyCollection<string> Locals = ["127.0.0.1.1.1", "5.1.2.3.1.1", "192.168.250.20.1.1"];

    private static AccessPolicy Policy(AccessLevel level, string? blocked = null, string? allowed = null) =>
        new(level, AccessPolicy.ParseList(blocked), allowed is null ? null : AccessPolicy.ParseList(allowed), "test");

    private static string? Deny(AccessPolicy policy, string tool, string? target, out string? warning) =>
        policy.Deny(ToolCatalog.Tools[tool], () => target, () => Locals, out warning);

    private static string? Deny(AccessPolicy policy, string tool, string? target = null) => Deny(policy, tool, target, out _);

    // ---- levels -------------------------------------------------------------------------------------------

    [Fact]
    public void Read_AllowsReading_BlocksWriting()
    {
        var p = Policy(AccessLevel.Read);
        Assert.Null(Deny(p, "twincat_plc_read"));
        var denied = Deny(p, "twincat_plc_write");
        Assert.NotNull(denied);
        Assert.StartsWith("[Error] Access policy 'read' blocks twincat_plc_write (needs 'engineer')", denied);
    }

    [Fact]
    public void Engineer_AllowsProjectChanges_BlocksRuntimeActions()
    {
        var p = Policy(AccessLevel.Engineer);
        Assert.Null(Deny(p, "twincat_plc_write"));
        Assert.Null(Deny(p, "twincat_build_plc"));
        Assert.Contains("needs 'runtime'", Deny(p, "twincat_activate_config"));
        Assert.Contains("needs 'runtime'", Deny(p, "twincat_login"));
        Assert.Contains("needs 'runtime'", Deny(p, "twincat_write_ads_variable", "local"));
    }

    [Theory]
    [InlineData("twincat_stop")]
    [InlineData("twincat_ibn_abort")]
    [InlineData("twincat_force_close_xae")]
    public void EmergencyStops_PassEvenInRead(string tool) =>
        Assert.Null(Deny(Policy(AccessLevel.Read, blocked: "local"), tool, "local"));

    [Fact]
    public void Unrestricted_AllowsEverything() =>
        Assert.All(ToolCatalog.Tools.Values, t => Assert.Null(AccessPolicy.Unrestricted.Deny(t, () => null, () => Locals, out _)));

    // ---- NetId rules --------------------------------------------------------------------------------------

    [Fact]
    public void ABlockedNetId_RefusesRuntimeActions_ButNotReads()
    {
        var p = Policy(AccessLevel.Runtime, blocked: "10.1.1.1.1.1");
        Assert.Contains("blocks runtime actions on 10.1.1.1.1.1", Deny(p, "twincat_write_ads_variable", "10.1.1.1.1.1"));
        Assert.Null(Deny(p, "twincat_read_ads_variable", "10.1.1.1.1.1"));
        Assert.Null(Deny(p, "twincat_write_ads_variable", "10.2.2.2.1.1"));
    }

    [Fact]
    public void BlockedLocal_CoversTheRouterAndTheUmrInstances()
    {
        var p = Policy(AccessLevel.Runtime, blocked: "local");
        Assert.NotNull(Deny(p, "twincat_plc_stop", AccessPolicy.LocalKeyword));
        Assert.NotNull(Deny(p, "twincat_plc_stop", "192.168.250.20.1.1"));
        Assert.NotNull(Deny(p, "twincat_umr_start"));
        Assert.Null(Deny(p, "twincat_plc_stop", "10.9.9.9.1.1"));
    }

    [Fact]
    public void AnAllowList_PermitsOnlyItsSystems()
    {
        var p = Policy(AccessLevel.Runtime, allowed: "local; 10.1.1.1.1.1");
        Assert.Null(Deny(p, "twincat_activate_config", "10.1.1.1.1.1"));
        Assert.Null(Deny(p, "twincat_activate_config", "5.1.2.3.1.1"));
        Assert.Contains("only on 10.1.1.1.1.1, local", Deny(p, "twincat_activate_config", "10.2.2.2.1.1"));
    }

    [Fact]
    public void AnUnknownTarget_IsRefusedUnderAnAllowList_AndWarnedOtherwise()
    {
        Assert.Contains("cannot be determined", Deny(Policy(AccessLevel.Runtime, allowed: "local"), "twincat_hmi_write_symbol", null));

        Assert.Null(Deny(Policy(AccessLevel.Runtime, blocked: "10.1.1.1.1.1"), "twincat_hmi_write_symbol", null, out var warning));
        Assert.StartsWith("[Warn] Access policy", warning);
    }

    [Fact]
    public void ActionsOnAnEarlierCheckedTarget_AreNotCheckedAgain() =>
        Assert.Null(Deny(Policy(AccessLevel.Runtime, allowed: "10.1.1.1.1.1"), "twincat_ibn_propose", null));

    [Fact]
    public void WithoutNetIdRules_TheTargetIsNeverResolved()
    {
        bool asked = false;
        Policy(AccessLevel.Runtime).Deny(ToolCatalog.Tools["twincat_activate_config"], () => { asked = true; return null; }, () => Locals, out _);
        Assert.False(asked);
    }

    // ---- merging and sources ------------------------------------------------------------------------------

    [Fact]
    public void Merge_TheStricterWins()
    {
        var merged = AccessPolicy.Merge(
            Policy(AccessLevel.Engineer, blocked: "1.1.1.1.1.1", allowed: "local; 2.2.2.2.1.1"),
            Policy(AccessLevel.Runtime, blocked: "3.3.3.3.1.1", allowed: "2.2.2.2.1.1; 4.4.4.4.1.1"));

        Assert.Equal(AccessLevel.Engineer, merged.Level);
        Assert.Equal(["1.1.1.1.1.1", "3.3.3.3.1.1"], merged.BlockedNetIds.Order());
        Assert.Equal(["2.2.2.2.1.1"], merged.AllowedNetIds!.Order());
    }

    [Fact]
    public void Merge_OneAllowListIsKept_NoneStaysNone()
    {
        Assert.Null(AccessPolicy.Merge(Policy(AccessLevel.Runtime), Policy(AccessLevel.Read)).AllowedNetIds);
        Assert.Equal(["local"], AccessPolicy.Merge(Policy(AccessLevel.Runtime, allowed: "LOCAL"), Policy(AccessLevel.Runtime)).AllowedNetIds!);
        Assert.Same(AccessPolicy.Unrestricted, AccessPolicy.Merge(null, null));
    }

    [Fact]
    public void FromEnvironment_ReadsTheThreeVariables_AnInvalidLevelIsRead()
    {
        var env = new Dictionary<string, string>
        {
            [AccessPolicy.LevelEnvVar] = "Engineer",
            [AccessPolicy.BlockedEnvVar] = "1.1.1.1.1.1;2.2.2.2.1.1",
        };
        var p = AccessPolicy.FromEnvironment(n => env.GetValueOrDefault(n))!;
        Assert.Equal(AccessLevel.Engineer, p.Level);
        Assert.Equal(2, p.BlockedNetIds.Count);
        Assert.Null(p.AllowedNetIds);

        Assert.Equal(AccessLevel.Read, AccessPolicy.FromEnvironment(n => n == AccessPolicy.LevelEnvVar ? "admin" : null)!.Level);
        Assert.Equal(AccessLevel.Read, AccessPolicy.FromEnvironment(n => n == AccessPolicy.LevelEnvVar ? "0" : null)!.Level);
        Assert.Null(AccessPolicy.FromEnvironment(_ => null));
    }

    [Fact]
    public void TheFile_RoundTrips()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        Policy(AccessLevel.Engineer, blocked: "1.1.1.1.1.1", allowed: "local").Save(path);

        var loaded = AccessPolicy.Load(path)!;
        Assert.Equal(AccessLevel.Engineer, loaded.Level);
        Assert.Equal(["1.1.1.1.1.1"], loaded.BlockedNetIds);
        Assert.Equal(["local"], loaded.AllowedNetIds!);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "access": "superuser" }""")]
    public void ABrokenFile_RestrictsToRead(string content) =>
        Assert.Equal(AccessLevel.Read, AccessPolicy.Parse(content).Level);

    [Fact]
    public void NoFile_IsNoPolicy() => Assert.Null(AccessPolicy.Load(Path.Combine(_dir, "missing.json")));

    [Fact]
    public void State_MergesWindowAndEnvironment_AndAnnouncesAChange()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        var state = new AccessPolicyState(path, n => n == AccessPolicy.LevelEnvVar ? "engineer" : null);
        Assert.Equal(AccessLevel.Engineer, state.Current.Level);

        int changes = 0;
        state.Changed += () => changes++;
        state.SetWindowPolicy(Policy(AccessLevel.Read));

        Assert.Equal(AccessLevel.Read, state.Current.Level);
        Assert.Equal(1, changes);
        Assert.Equal(AccessLevel.Read, AccessPolicy.Load(path)!.Level);
    }

    [Fact]
    public void ThePolicyFile_IsNoPlaceForFileTools()
    {
        Assert.False(PathPolicy.IsAllowed(AccessPolicy.DefaultPath, null, out var reason));
        Assert.Contains("access policy", reason);
        string neighbour = Path.Combine(Path.GetDirectoryName(AccessPolicy.DefaultPath)!, "export.xml");
        Assert.True(PathPolicy.IsAllowed(neighbour, null, out _));
    }

    // ---- the gate -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData(" 10.1.1.1.1.1 ", "10.1.1.1.1.1")]
    [InlineData("project", "7.7.7.7.1.1")]
    [InlineData("PROJECT", "7.7.7.7.1.1")]
    public void Target_FromTheAmsNetIdArgument(string? amsNetId, string expected) =>
        Assert.Equal(expected, AccessGate.Target(ToolCatalog.Tools["twincat_plc_start"], new { amsNetId, adsPort = 851 }, () => "7.7.7.7.1.1"));

    [Theory]
    [InlineData("8.8.8.8.1.1", "8.8.8.8.1.1")]
    [InlineData("", "local")]
    [InlineData(null, null)]
    public void Target_OfAProjectAction_IsTheProjectTarget(string? projectTarget, string? expected) =>
        Assert.Equal(expected, AccessGate.Target(ToolCatalog.Tools["twincat_activate_config"], null, () => projectTarget));

    [Fact]
    public void ArgumentText_ReadsAnonymousObjectsDictionariesAndJson()
    {
        Assert.Equal("x", AccessGate.ArgumentText(new { AmsNetId = "x" }, "amsNetId"));
        Assert.Equal("y", AccessGate.ArgumentText(new Dictionary<string, object?> { ["amsNetId"] = "y" }, "amsNetId"));
        Assert.Equal("z", AccessGate.ArgumentText(System.Text.Json.JsonDocument.Parse("""{"amsNetId":"z"}""").RootElement, "amsNetId"));
        Assert.Null(AccessGate.ArgumentText(new { other = 1 }, "amsNetId"));
        Assert.Null(AccessGate.ArgumentText(null, "amsNetId"));
    }

    [Fact]
    public void Gate_AnUnknownToolName_IsTreatedAsAChange()
    {
        var gate = AccessGate.Create(() => Policy(AccessLevel.Read), () => null, () => []);
        Assert.NotNull(gate("not_a_tool", null).Denied);
        Assert.Null(gate("twincat_plc_read", null).Denied);
    }

    [Fact]
    public async Task Activity_ARefusedCall_NeverRunsTheBody_AndIsLogged()
    {
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir))
        {
            AccessGate = AccessGate.Create(() => Policy(AccessLevel.Engineer), () => null, () => []),
        };
        bool ran = false;

        string result = await activity.TraceAsync("twincat_activate_config", null, () => { ran = true; return Task.FromResult("done"); });

        Assert.False(ran);
        Assert.StartsWith("[Error] Access policy 'engineer' blocks twincat_activate_config", result);
        Assert.False(Assert.Single(activity.Log.Snapshot()).Success);
    }

    [Fact]
    public async Task Activity_AWarningIsAppendedToTheResult()
    {
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir))
        {
            AccessGate = (_, _) => new AccessDecision(null, "[Warn] note"),
        };
        Assert.Equal("done\n[Warn] note", await activity.TraceAsync("twincat_plc_read", null, () => Task.FromResult("done")));
    }

    [Fact]
    public async Task Activity_AGateThatThrows_RefusesTheCall()
    {
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir))
        {
            AccessGate = (_, _) => throw new InvalidOperationException("broken"),
        };
        string result = await activity.TraceAsync("twincat_plc_read", null, () => Task.FromResult("done"));
        Assert.StartsWith("[Error] The access policy could not be checked", result);
    }

    // ---- what a session sees ------------------------------------------------------------------------------

    [Fact]
    public void Surface_HidesWhatTheProfileOrThePolicyForbids()
    {
        var surface = new ToolSurface(_ => null, () => Policy(AccessLevel.Read), windowProfile: null);
        var all = surface.ProfileFor("claude-code");
        Assert.True(surface.Visible("twincat_plc_read", all));
        Assert.False(surface.Visible("twincat_plc_write", all));
        Assert.True(surface.Visible("twincat_stop", all));

        var core = surface.ProfileFor("cursor");
        Assert.False(surface.Visible("twincat_hmi_list_projects", core));
        Assert.True(surface.Visible("twincat_plc_search", core));
    }

    [Fact]
    public void Surface_TheWindowProfileAppliesToEveryClient()
    {
        var surface = new ToolSurface(_ => null, () => AccessPolicy.Unrestricted, windowProfile: "core");
        Assert.Equal(ProfileSource.Gui, surface.ProfileFor("claude-code").Source);
        surface.SetWindowProfile("auto");
        Assert.Equal(ProfileSource.ClientRule, surface.ProfileFor("claude-code").Source);
    }

    [Fact]
    public void HiddenMessage_NamesTheToolsetAndTheWayOut()
    {
        var core = ToolProfiles.Resolve(null, null, "cursor");
        string text = ToolSurface.HiddenMessage(ToolCatalog.Tools["twincat_hmi_list_projects"], core);
        Assert.StartsWith("[Error] twincat_hmi_list_projects belongs to the toolset 'hmi'", text);
        Assert.Contains(ToolProfiles.EnvVar, text);
    }

    [Fact]
    public void UiSettings_KeepTheToolProfile()
    {
        Assert.Equal(ToolProfiles.Automatic, UiSettings.Parse("{}").ToolProfile);
        var s = UiSettings.Parse("""{ "toolProfile": "core" }""");
        Assert.Equal("core", s.ToolProfile);
        Assert.Equal("core", UiSettings.Parse(s.ToJson()).ToolProfile);
    }
}
