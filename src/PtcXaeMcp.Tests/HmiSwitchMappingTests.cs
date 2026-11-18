using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// "Switch Mapping to PLC". Invariants: only TMC-domain mappings are touched; mapped names never change; every
/// entry is checked before the popup and nothing is written without confirmation; a mapping whose symbol the
/// runtime does not know gets its old entry back and is reported as not switched.
/// </summary>
public class HmiSwitchMappingTests
{
    private const string Project = "HMI_Fixture";
    private const string Name = "PLC_TMC.GVL_HmiTest.stSingle";
    private const string Config = "TcHmiSrv.Config::SYMBOLS::PLC_TMC.GVL_HmiTest.stSingle";

    private static string TmcEntry(string mapping = "PLC_TMC::GVL_HmiTest::stSingle") => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["ACCESS"] = 3,
        ["DOMAIN"] = "TMC",
        ["MAPPING"] = mapping,
        ["USEMAPPING"] = true,
        ["SCHEMA"] = new Dictionary<string, string> { ["$ref"] = "tchmi:server#/definitions/x" },
    });

    private static FakeTwinCatSession Session(string? entry = null)
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = @"C:\x\Fixture.sln",
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, [new HmiProjectEntry(Project, @"C:\x\HMI_Fixture", true, false)], null),
        };
        fake.SetAdsRuntimes(FakeTwinCatSession.AdsRuntimeJson());
        fake.HmiMappedSymbols.Add(new HmiMappedSymbol(Name, "TMC", null, "ST_FixtureRecord", false, false));
        fake.HmiMappedSymbols.Add(new HmiMappedSymbol("ADS.PLC1.MAIN.bStart", "ADS", null, "BOOL", false, false));
        fake.HmiSymbolValues[Config] = entry ?? TmcEntry();
        fake.HmiSymbolErrors[Name] = "INVALID_DOMAIN, Domain TMC does not exist or is not initialized";
        return fake;
    }

    /// <summary>After the entry is rewritten, the mapped name answers like the PLC would.</summary>
    private static void PlcAnswers(FakeTwinCatSession fake, string? value, string? error) =>
        fake.AfterHmiWrite = (symbol, json) =>
        {
            if (symbol != Config || !json.Contains("\"ADS\"", StringComparison.Ordinal)) return;
            fake.HmiSymbolErrors.Remove(Name);
            if (value is not null) fake.HmiSymbolValues[Name] = value;
            else fake.HmiSymbolErrors[Name] = error!;
        };

    // ── pure rules ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("PLC_TMC::MAIN::x", "PLC1", "PLC1::MAIN::x")]
    [InlineData("PLC_TMC::GVL::aData[0]::nId", "PLC2", "PLC2::GVL::aData[0]::nId")]
    [InlineData("PLC_TMC", "PLC1", null)]
    [InlineData("", "PLC1", null)]
    [InlineData(null, "PLC1", null)]
    public void Reroot_ReplacesOnlyTheRoot(string? mapping, string runtime, string? expected) =>
        Assert.Equal(expected, HmiMappingSwitch.Reroot(mapping, runtime));

    [Fact]
    public void Switched_SetsAdsKeepsTheRestDropsTheTmcSchema()
    {
        JsonNode e = JsonNode.Parse(HmiMappingSwitch.Switched(TmcEntry(), "PLC1")!)!;

        Assert.Equal("ADS", (string?)e["DOMAIN"]);
        Assert.Equal("PLC1::GVL_HmiTest::stSingle", (string?)e["MAPPING"]);
        Assert.True((bool?)e["AUTOMAP"]);
        Assert.True((bool?)e["USEMAPPING"]);
        Assert.Equal(3, (int?)e["ACCESS"]);
        Assert.Null(e["SCHEMA"]);
    }

    // ── the tool ────────────────────────────────────────────────────────────

    [Fact]
    public void Switch_PlcKnowsTheSymbol_SwitchedUnderTheSameName()
    {
        var fake = Session();
        PlcAnswers(fake, "{\"nId\": 99.0}", null);
        var popup = new FakeHmiConfirmer();

        var (summary, json) = new HmiCoordinator(fake, popup).SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("1/1 switched", summary);
        Assert.Contains("Mapped names are unchanged", summary);
        JsonNode entry = JsonNode.Parse(fake.HmiSymbolValues[Config])!;
        Assert.Equal("ADS", (string?)entry["DOMAIN"]);
        Assert.Equal("PLC1::GVL_HmiTest::stSingle", (string?)entry["MAPPING"]);
        Assert.DoesNotContain(fake.CallLog, c => c.Contains("ADS.PLC1.MAIN.bStart", StringComparison.Ordinal)
                                                 && c.StartsWith("WriteHmiSymbol", StringComparison.Ordinal));
        var (request, _) = Assert.Single(popup.Requests);
        Assert.Equal(HmiWriteKind.SwitchMappingToPlc, request.Kind);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("switched", doc.RootElement.GetProperty("results")[0].GetProperty("state").GetString());
        Assert.Equal(Name, doc.RootElement.GetProperty("results")[0].GetProperty("mappedName").GetString());
    }

    [Fact]
    public void Switch_RuntimeInTmcMode_CountsAsSwitchedButSaysNoValue()
    {
        var fake = Session();
        PlcAnswers(fake, null, "Message: HMI_ADS_E_USING_TMC");

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.Contains("1/1 switched", summary);
        Assert.Contains("USE_TMC", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("switchedTmcMode", doc.RootElement.GetProperty("results")[0].GetProperty("state").GetString());
    }

    [Fact]
    public void Switch_PlcDoesNotKnowTheSymbol_RestoresTheOldEntry()
    {
        var fake = Session();
        string before = fake.HmiSymbolValues[Config];
        PlcAnswers(fake, null, "Message: HMI_ADS_E_INVALID_SYMBOL");

        var (summary, json) = new HmiCoordinator(fake, new FakeHmiConfirmer()).SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.Contains("0/1 switched", summary);
        Assert.Contains("does not exist in runtime 'PLC1'", summary);
        Assert.Equal(before, fake.HmiSymbolValues[Config]);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("refused", doc.RootElement.GetProperty("results")[0].GetProperty("state").GetString());
    }

    [Fact]
    public void Switch_NoTmcMapping_IsNothingToDoAndPointsAtDisable()
    {
        var fake = Session();
        fake.HmiMappedSymbols.RemoveAll(s => s.Domain == "TMC");
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.StartsWith("Nothing to do", summary);
        Assert.Contains("twincat_hmi_disable_runtime_tmc", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void Switch_NamedAdsMapping_IsRefusedBeforeThePopup()
    {
        var fake = Session();
        fake.HmiSymbolValues["TcHmiSrv.Config::SYMBOLS::ADS.PLC1.MAIN.bStart"] = """{"DOMAIN":"ADS","MAPPING":"PLC1::MAIN::bStart"}""";
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).SwitchMappingToPlc(null, "PLC1", ["ADS.PLC1.MAIN.bStart"], 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("not TMC", summary);
        Assert.Empty(popup.Requests);
    }

    [Fact]
    public void Switch_UnknownRuntime_IsRefused()
    {
        var (summary, _) = new HmiCoordinator(Session(), new FakeHmiConfirmer()).SwitchMappingToPlc(null, "PLC7", null, 120);
        Assert.Contains("has no runtime 'PLC7'", summary);
    }

    [Fact]
    public void Switch_Rejected_WritesNothing()
    {
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected))
            .SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.StartsWith("[Error] Not confirmed", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("WriteHmiSymbol", StringComparison.Ordinal));
    }

    [Fact]
    public void Switch_UnreadableEntry_WritesNothing()
    {
        var fake = Session();
        fake.HmiSymbolValues.Remove(Config);
        fake.HmiSymbolErrors[Config] = "INVALID_SUBSYMBOL";
        var popup = new FakeHmiConfirmer();

        var (summary, _) = new HmiCoordinator(fake, popup).SwitchMappingToPlc(null, "PLC1", null, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("not readable", summary);
        Assert.Empty(popup.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PLC 1")]
    [InlineData("PLC1::TMC")]
    public void Switch_InvalidRuntime_IsRefused(string? runtime) =>
        Assert.StartsWith("[Error] runtime is required",
            new HmiCoordinator(Session(), new FakeHmiConfirmer()).SwitchMappingToPlc(null, runtime, null, 120).Summary);
}
