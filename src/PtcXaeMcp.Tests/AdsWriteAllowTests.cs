using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>The symbol lists of the access policy: which PLC variables a runtime write may reach.</summary>
public sealed class AdsWriteAllowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "adswrite_" + Guid.NewGuid().ToString("N"));

    public AdsWriteAllowTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlyCollection<string> Locals = ["127.0.0.1.1.1"];

    private static AccessPolicy Policy(string? allow = null, string? deny = null, AccessLevel level = AccessLevel.Runtime) =>
        new(level, AccessPolicy.ParseList(null), null, "test",
            allow is null ? null : AccessPolicy.ParsePatterns(allow),
            deny is null ? null : AccessPolicy.ParsePatterns(deny));

    private static string? Write(AccessPolicy policy, string? symbol, string tool = "twincat_write_ads_variable") =>
        policy.Deny(ToolCatalog.Tools[tool], () => "local", () => Locals, out _, () => symbol);

    // ---- the pattern language -----------------------------------------------------------------------------

    [Theory]
    [InlineData("MAIN.stTest.*", "MAIN.stTest.bFlag", true)]
    [InlineData("MAIN.stTest.*", "main.sttest.nValue", true)]
    [InlineData("MAIN.stTest.*", "MAIN.stTest", false)]
    [InlineData("MAIN.stTest.*", "MAIN.stTestOther.x", false)]
    [InlineData("GVL_Param.r*", "GVL_Param.rSpeed", true)]
    [InlineData("GVL_Param.r*", "GVL_Param.nCount", false)]
    [InlineData("MAIN.bTest?", "MAIN.bTestA", true)]
    [InlineData("MAIN.bTest?", "MAIN.bTestAB", false)]
    [InlineData("MAIN.aTest[1]", "MAIN.aTest[1]", true)]
    [InlineData("MAIN.aTest[1]", "MAIN.aTest[10]", false)]
    [InlineData("MAIN.aTest*", "MAIN.aTest[10].bX", true)]
    [InlineData("*.bEmergency*", "GVL.fbSafety.bEmergencyStop", true)]
    [InlineData("MAIN.x+y", "MAIN.xxy", false)]
    public void Glob_MatchesTheFullPathCaseInsensitive(string pattern, string symbol, bool expected) =>
        Assert.Equal(expected, SymbolGlob.IsMatch(pattern, symbol));

    // ---- the decision --------------------------------------------------------------------------------------

    [Fact]
    public void WithoutLists_EveryWriteIsAsBefore()
    {
        Assert.Null(Write(AccessPolicy.Unrestricted, "MAIN.nStep"));
        Assert.Null(Write(AccessPolicy.Unrestricted, null));
    }

    [Fact]
    public void AnAllowList_PermitsOnlyMatchingSymbols()
    {
        var p = Policy(allow: "MAIN.bTest*; GVL_Param.r*");
        Assert.Null(Write(p, "MAIN.bTestA"));
        Assert.Null(Write(p, "GVL_Param.rSpeed"));
        var refused = Write(p, "MAIN.nStep");
        Assert.StartsWith("[Error] Access policy: twincat_write_ads_variable may write only", refused);
        Assert.Contains("'MAIN.nStep'", refused);
        Assert.Contains("Nothing was done", refused);
    }

    [Fact]
    public void ADenyPattern_BeatsAnAllowPattern()
    {
        var p = Policy(allow: "MAIN.*", deny: "MAIN.stSafety.*; *.bEmergency*");
        Assert.Null(Write(p, "MAIN.nSpeed"));
        Assert.Contains("deny pattern 'MAIN.stSafety.*'", Write(p, "MAIN.stSafety.bOk"));
        Assert.Contains("deny pattern '*.bEmergency*'", Write(p, "MAIN.fb.bEmergencyStop"));
    }

    [Fact]
    public void ADenyListAlone_RefusesOnlyItsSymbols()
    {
        var p = Policy(deny: "GVL_Cmd.*");
        Assert.Null(Write(p, "MAIN.nStep"));
        Assert.NotNull(Write(p, "GVL_Cmd.bValve"));
    }

    [Fact]
    public void AnEmptyAllowList_AllowsNoSymbol()
    {
        var p = Policy(allow: "");
        Assert.True(p.HasSymbolRules);
        Assert.NotNull(Write(p, "MAIN.bTestA"));
    }

    [Fact]
    public void AnAllowList_RefusesACallWhoseSymbolCannotBeRead_ADenyListAloneDoesNot()
    {
        Assert.Contains("could not be read", Write(Policy(allow: "MAIN.*"), null));
        Assert.Contains("could not be read", Write(Policy(allow: "MAIN.*"), "  "));
        Assert.Null(Write(Policy(deny: "MAIN.*"), null));
    }

    [Fact]
    public void ALeadingDot_IsNoWayAround()
    {
        var p = Policy(allow: "MAIN.*");
        Assert.NotNull(Write(p, ".GVL.nX"));
        Assert.Null(Write(p, ".MAIN.nX"));
    }

    [Fact]
    public void Reading_IsNeverRestrictedByTheLists() =>
        Assert.Null(Write(Policy(allow: ""), "MAIN.nStep", "twincat_read_ads_variable"));

    [Fact]
    public void TheListsApplyBelowTheRuntimeLevelToo_AnInitSymbolIsAConfigurationWrite()
    {
        var p = Policy(allow: "GVL_Param.*", level: AccessLevel.Engineer);
        Assert.Null(Write(p, "GVL_Param.nSpeed", "twincat_write_init_symbol"));
        Assert.NotNull(Write(p, "GVL_Cmd.nSpeed", "twincat_write_init_symbol"));
    }

    [Theory]
    [InlineData("ADS.PLC1.MAIN.bTestA", true)]
    [InlineData("ads.plc2.MAIN.bTestA", true)]
    [InlineData("ADS.PLC1.MAIN.nStep", false)]
    [InlineData("MAIN.bTestA", true)]
    [InlineData("Internal.bTestA", false)]
    public void AnHmiSymbolOfTheAdsDomain_IsComparedByItsPlcPath(string hmiSymbol, bool allowed)
    {
        var refused = Write(Policy(allow: "MAIN.bTest*"), hmiSymbol, "twincat_hmi_write_symbol");
        Assert.Equal(allowed, refused is null);
    }

    [Fact]
    public void EmergencyStops_AreNeverTouched() =>
        Assert.Null(Policy(allow: "").Deny(ToolCatalog.Tools["twincat_stop"], () => "local", () => Locals, out _, () => null));

    [Fact]
    public void TheLevelIsCheckedFirst()
    {
        var p = Policy(allow: "MAIN.*", level: AccessLevel.Engineer);
        Assert.Contains("needs 'runtime'", Write(p, "GVL.x"));
    }

    // ---- several symbols at once (snapshot restore) ----------------------------------------------------------

    [Fact]
    public void AMultiSymbolWrite_IsRefusedAsAWhole_AndNamesTheRefusedSymbols()
    {
        var tool = ToolCatalog.Tools["twincat_persistent_snapshot_restore"];
        var p = Policy(allow: "GVL_Recipe.*");

        Assert.Null(p.DenySymbols(tool, ["GVL_Recipe.nA", "GVL_Recipe.nB"]));

        var refused = p.DenySymbols(tool, ["GVL_Recipe.nA", "MAIN.nStep", "GVL_Cmd.bValve", "main.nstep"])!;
        Assert.StartsWith("[Error] Access policy: twincat_persistent_snapshot_restore would write 2 symbol(s)", refused);
        Assert.Contains("MAIN.nStep", refused);
        Assert.Contains("GVL_Cmd.bValve", refused);
        Assert.DoesNotContain("GVL_Recipe.nA,", refused);
        Assert.Contains("not even the allowed symbols", refused);
    }

    [Fact]
    public void AMultiSymbolWrite_ShortensALongList()
    {
        var tool = ToolCatalog.Tools["twincat_persistent_snapshot_restore"];
        string refused = Policy(allow: "GVL.*").DenySymbols(tool, Enumerable.Range(0, 50).Select(i => $"MAIN.n{i}"))!;
        Assert.Contains("50 symbol(s)", refused);
        Assert.Contains("(+30 more)", refused);
    }

    [Fact]
    public void AMultiSymbolWrite_WithoutListsIsNeverChecked() =>
        Assert.Null(AccessPolicy.Unrestricted.DenySymbols(ToolCatalog.Tools["twincat_persistent_snapshot_restore"], ["MAIN.x"]));

    // ---- merging, environment, file ---------------------------------------------------------------------------

    [Fact]
    public void Merge_AllowListsIntersect_DenyListsUnite()
    {
        var merged = AccessPolicy.Merge(
            Policy(allow: "MAIN.a*; MAIN.b*", deny: "MAIN.x*"),
            Policy(allow: "MAIN.b*; MAIN.c*", deny: "MAIN.y*"));

        Assert.Equal(["MAIN.b*"], merged.AdsWriteAllow!.Order());
        Assert.Equal(["MAIN.x*", "MAIN.y*"], merged.AdsWriteDeny!.Order());
    }

    [Fact]
    public void Merge_OneListIsKept_NoneStaysNone_AnEmptyListWins()
    {
        Assert.Null(AccessPolicy.Merge(Policy(), Policy()).AdsWriteAllow);
        Assert.Equal(["MAIN.*"], AccessPolicy.Merge(Policy(allow: "MAIN.*"), Policy()).AdsWriteAllow!);
        Assert.Empty(AccessPolicy.Merge(Policy(allow: "MAIN.*"), Policy(allow: "")).AdsWriteAllow!);
        Assert.Equal(["A.*"], AccessPolicy.Merge(Policy(), Policy(deny: "A.*")).AdsWriteDeny!);
    }

    [Fact]
    public void FromEnvironment_ReadsBothVariables_AndAloneTheyAreAPolicy()
    {
        var env = new Dictionary<string, string>
        {
            [AccessPolicy.AdsWriteAllowEnvVar] = "MAIN.stTest.*; GVL_Param.r*",
            [AccessPolicy.AdsWriteDenyEnvVar] = "*.bEmergency*",
        };
        var p = AccessPolicy.FromEnvironment(n => env.GetValueOrDefault(n))!;
        Assert.Equal(AccessLevel.Runtime, p.Level);
        Assert.Equal(["GVL_Param.r*", "MAIN.stTest.*"], p.AdsWriteAllow!.Order());
        Assert.Equal(["*.bEmergency*"], p.AdsWriteDeny!);

        Assert.Null(AccessPolicy.FromEnvironment(_ => null));
        Assert.Null(AccessPolicy.FromEnvironment(n => n == AccessPolicy.AdsWriteAllowEnvVar ? "  " : null));
    }

    [Fact]
    public void TheFile_RoundTripsTheLists()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        Policy(allow: "MAIN.bTest*", deny: "MAIN.nStep").Save(path);
        string json = File.ReadAllText(path);
        Assert.Contains("adsWriteAllow", json);
        Assert.Contains("adsWriteDeny", json);

        var loaded = AccessPolicy.Load(path)!;
        Assert.Equal(["MAIN.bTest*"], loaded.AdsWriteAllow!);
        Assert.Equal(["MAIN.nStep"], loaded.AdsWriteDeny!);
    }

    [Fact]
    public void TheFile_LeavesOutAnUnsetList()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        Policy().Save(path);
        string json = File.ReadAllText(path);
        Assert.DoesNotContain("adsWrite", json);
        Assert.Null(AccessPolicy.Load(path)!.AdsWriteAllow);
    }

    [Theory]
    [InlineData("""{ "adsWriteAllow": "MAIN.*" }""")]
    [InlineData("""{ "adsWriteAllow": 5 }""")]
    [InlineData("""{ "adsWriteAllow": [] }""")]
    public void ABrokenOrEmptyAllowList_AllowsNothing(string content)
    {
        var p = AccessPolicy.Parse(content);
        Assert.NotNull(p.AdsWriteAllow);
        Assert.Empty(p.AdsWriteAllow!);
        Assert.NotNull(Write(p, "MAIN.nStep"));
    }

    [Fact]
    public void State_MergesTheListsOfWindowAndEnvironment()
    {
        string path = Path.Combine(_dir, AccessPolicy.FileName);
        var state = new AccessPolicyState(path, n => n == AccessPolicy.AdsWriteDenyEnvVar ? "MAIN.nStep" : null);
        state.SetWindowPolicy(Policy(allow: "MAIN.*", deny: "MAIN.bSafe"));

        Assert.Equal(["MAIN.*"], state.Current.AdsWriteAllow!);
        Assert.Equal(["MAIN.bSafe", "MAIN.nStep"], state.Current.AdsWriteDeny!.Order());
    }

    [Fact]
    public void Summary_NamesTheLists()
    {
        string text = Policy(allow: "MAIN.bTest*", deny: "MAIN.nStep").Summary();
        Assert.Contains("variable writes only to: MAIN.bTest*", text);
        Assert.Contains("variable writes never to: MAIN.nStep", text);
        Assert.DoesNotContain("variable writes", AccessPolicy.Unrestricted.Summary());
    }

    // ---- the gate -----------------------------------------------------------------------------------------------

    [Fact]
    public void Gate_ReadsTheSymbolFromTheTracedArguments()
    {
        var policy = Policy(allow: "MAIN.bTest*");
        var gate = AccessGate.Create(() => policy, () => null, () => []);

        Assert.Null(gate("twincat_write_ads_variable", new { symbolPath = "MAIN.bTestA", value = "true", amsNetId = (string?)null, adsPort = (int?)null }).Denied);
        Assert.Contains("'MAIN.nStep'",
            gate("twincat_write_ads_variable", new { symbolPath = "MAIN.nStep", value = "1", amsNetId = (string?)null, adsPort = (int?)null }).Denied);
        Assert.Null(gate("twincat_hmi_write_symbol", new { hmiProject = (string?)null, symbol = "ADS.PLC1.MAIN.bTestB", valueJson = "true", confirmTimeoutSeconds = 30 }).Denied);
        Assert.NotNull(gate("twincat_write_init_symbol", new { symbol = "GVL.nX", value = "1", plcProjectName = (string?)null }).Denied);
    }

    [Fact]
    public async Task Activity_ARefusedWrite_NeverRunsTheBody()
    {
        var policy = Policy(allow: "MAIN.bTest*");
        var activity = new McpActivity(new McpActivityLog(capacity: 10), new McpActivityJsonLog(_dir))
        {
            AccessGate = AccessGate.Create(() => policy, () => null, () => []),
        };
        bool ran = false;

        string result = await activity.TraceAsync("twincat_write_ads_variable",
            new { symbolPath = "MAIN.nStep", value = "1" }, () => { ran = true; return Task.FromResult("done"); });

        Assert.False(ran);
        Assert.StartsWith("[Error] Access policy: twincat_write_ads_variable may write only", result);
    }

    [Fact]
    public void TheCatalog_NamesTheSymbolArgumentOfEveryVariableWriter()
    {
        Assert.Equal("symbolPath", ToolCatalog.Tools["twincat_write_ads_variable"].SymbolArgument);
        Assert.Equal("symbol", ToolCatalog.Tools["twincat_write_init_symbol"].SymbolArgument);
        Assert.Equal("symbol", ToolCatalog.Tools["twincat_hmi_write_symbol"].SymbolArgument);
        Assert.Null(ToolCatalog.Tools["twincat_read_ads_variable"].SymbolArgument);
    }
}
