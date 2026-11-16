using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_release_check: one light per item, read-only, and "unknown" instead of green for what a read cannot show.</summary>
public sealed class ReleaseCheckCoordinatorTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private const string Sln = @"C:\work\Plant\Plant.sln";

    private const string CleanSarif = """{ "version": "2.1.0", "runs": [ { "results": [] } ] }""";

    private const string SarifWithError = """
        { "version": "2.1.0", "runs": [ { "results": [
          { "ruleId": "SA0035", "level": "error", "message": { "text": "Unused output" },
            "locations": [{ "physicalLocation": { "artifactLocation": { "uri": "MAIN.TcPOU" }, "region": { "startLine": 3 } } }] } ] } ] }
        """;

    private static SolutionFileFacts Fresh() => new()
    {
        NewestSource = "MAIN.TcPOU", NewestSourceUtc = T0,
        NewestCompileInfoUtc = T0.AddMinutes(5),
        NewestBootApp = "Port_851.app", NewestBootAppUtc = T0.AddMinutes(6),
        SarifPath = "x.sarif.json", SarifText = CleanSarif, SarifWriteUtc = T0.AddMinutes(7),
    };

    private static ReleaseCheckCoordinator.ComFacts Com(Action<ReleaseCheckCoordinator.ComFacts>? arrange = null)
    {
        var com = new ReleaseCheckCoordinator.ComFacts
        {
            SolutionPath = Sln,
            ErrorList = [],
            Libraries = [],
            RequiredLicenses = new RequiredLicensesResult([], "<x/>"),
            LicenseInfo = new LicenseInfoResult([], "<x/>"),
            PlcState = new AdsRunStateResult(true, "Run", "5.1.2.3.1.1", 851, "OK"),
        };
        arrange?.Invoke(com);
        return com;
    }

    private static LibraryReference Ref(string name, string version) =>
        new(name, version, "Beckhoff", name, $"{name}, {version}", LibrarySource.Xae, null, null);

    private static InstalledLibrary Installed(string name, string version) => new(name, version, "Beckhoff", name);

    // ---- build ----

    [Fact]
    public void Build_NoErrorsAndNewerThanTheSources_IsGreen_WarningsCounted()
    {
        var item = ReleaseCheckCoordinator.EvaluateBuild(
            Com(c => c.ErrorList = [new BuildError("warning", "w1", null, null), new BuildError("warning", "w2", null, null)]), Fresh());

        Assert.Equal(Light.Green, item.Light);
        Assert.Contains("0 errors, 2 warnings", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AnError_IsRed_WithItsLocation()
    {
        var item = ReleaseCheckCoordinator.EvaluateBuild(
            Com(c => c.ErrorList = [new BuildError("error", "C0077: Unknown type", @"POUs\MAIN.TcPOU", 12)]), Fresh());

        Assert.Equal(Light.Red, item.Light);
        Assert.Contains(@"POUs\MAIN.TcPOU:12", Assert.Single(item.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_SourcesNewerThanTheLastBuild_IsYellow()
    {
        var files = Fresh() with { NewestSourceUtc = T0.AddHours(1) };

        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.EvaluateBuild(Com(), files).Light);
    }

    [Fact]
    public void Build_AnEmptyErrorListWithoutCompileInformation_IsUnknown_NotGreen()
    {
        var files = Fresh() with { NewestCompileInfoUtc = null };

        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateBuild(Com(), files).Light);
    }

    [Fact]
    public void Build_AnErrorListThatCannotBeRead_IsUnknown()
    {
        var com = Com(c => { c.ErrorList = null; c.Errors["errors"] = "the XAE does not answer"; });

        var item = ReleaseCheckCoordinator.EvaluateBuild(com, Fresh());

        Assert.Equal(Light.Unknown, item.Light);
        Assert.Contains("does not answer", item.Summary, StringComparison.Ordinal);
    }

    // ---- check all objects / static analysis ----

    [Fact]
    public void CheckAllObjects_IsUnknown_AndNamesTheTool()
    {
        var item = ReleaseCheckCoordinator.EvaluateCheckAllObjects();

        Assert.Equal(Light.Unknown, item.Light);
        Assert.Contains("twincat_plc_check_all_objects", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAnalysis_NoReport_IsUnknown()
    {
        var item = ReleaseCheckCoordinator.EvaluateStaticAnalysis(Fresh() with { SarifText = null, SarifWriteUtc = null }, @"C:\work\Plant", T0);

        Assert.Equal(Light.Unknown, item.Light);
        Assert.Contains("twincat_plc_static_analysis", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAnalysis_ACleanReportNewerThanTheSources_IsGreen()
    {
        Assert.Equal(Light.Green, ReleaseCheckCoordinator.EvaluateStaticAnalysis(Fresh(), @"C:\work\Plant", T0.AddHours(1)).Light);
    }

    [Fact]
    public void StaticAnalysis_AnErrorFinding_IsRed()
    {
        var item = ReleaseCheckCoordinator.EvaluateStaticAnalysis(Fresh() with { SarifText = SarifWithError }, @"C:\work\Plant", T0);

        Assert.Equal(Light.Red, item.Light);
        Assert.Contains("SA0035", Assert.Single(item.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void StaticAnalysis_AReportOlderThanTheSources_IsYellow()
    {
        var files = Fresh() with { NewestSourceUtc = T0.AddHours(2) };

        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.EvaluateStaticAnalysis(files, @"C:\work\Plant", T0).Light);
    }

    [Fact]
    public void StaticAnalysis_ABrokenReport_IsUnknown()
    {
        var item = ReleaseCheckCoordinator.EvaluateStaticAnalysis(Fresh() with { SarifText = "{ not json" }, null, T0);

        Assert.Equal(Light.Unknown, item.Light);
    }

    // ---- libraries ----

    [Fact]
    public void Libraries_AllInstalledAndPinned_IsGreen()
    {
        var com = Com(c => c.Libraries = [new PlcLibraryContext("PLC", [Ref("Tc2_Standard", "3.4.1.0")], [], [Installed("Tc2_Standard", "3.4.1.0")])]);

        Assert.Equal(Light.Green, ReleaseCheckCoordinator.EvaluateLibraries(com).Light);
    }

    [Fact]
    public void Libraries_AStarVersion_IsYellow_NotGreen()
    {
        var com = Com(c => c.Libraries = [new PlcLibraryContext("PLC", [Ref("Tc2_Standard", "*")], [], [Installed("Tc2_Standard", "3.4.1.0")])]);

        var item = ReleaseCheckCoordinator.EvaluateLibraries(com);

        Assert.Equal(Light.Yellow, item.Light);
        Assert.Contains("not pinned", Assert.Single(item.Details), StringComparison.Ordinal);
    }

    [Fact]
    public void Libraries_AMissingOrWrongVersion_IsRed()
    {
        var com = Com(c => c.Libraries = [new PlcLibraryContext("PLC",
            [Ref("Tc2_Standard", "3.4.1.0"), Ref("Tc3_Gone", "1.0.0.0"), Ref("Tc2_MC2", "3.3.0.0")], [],
            [Installed("Tc2_Standard", "3.4.1.0"), Installed("Tc2_MC2", "3.2.0.0")])]);

        var item = ReleaseCheckCoordinator.EvaluateLibraries(com);

        Assert.Equal(Light.Red, item.Light);
        Assert.Equal(2, item.Details.Count);
    }

    [Fact]
    public void Libraries_NoProjectsOrNoRead_IsUnknown()
    {
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateLibraries(Com()).Light);
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateLibraries(Com(c => c.Libraries = null)).Light);
    }

    // ---- licenses ----

    private static RequiredLicense Required(string id) => new(id, "TC1200", "TC3 PLC");

    private static LicenseEntry License(string id, LicenseStatus status) => new(id, "TC3 PLC", status, null);

    [Theory]
    [InlineData("Present", "Green")]
    [InlineData("Trial", "Yellow")]
    [InlineData("Expired", "Red")]
    [InlineData("Missing", "Red")]
    [InlineData("Unknown", "Unknown")]
    public void Licenses_FollowTheStatusOfTheTarget(string status, string expected)
    {
        var com = Com(c =>
        {
            c.RequiredLicenses = new RequiredLicensesResult([Required("{ABC-1}")], "<x/>");
            c.LicenseInfo = new LicenseInfoResult([License("abc-1", Enum.Parse<LicenseStatus>(status))], "<x/>"); // braces and case do not matter
        });

        Assert.Equal(Enum.Parse<Light>(expected), ReleaseCheckCoordinator.EvaluateLicenses(com).Light);
    }

    [Fact]
    public void Licenses_ARequiredOneThatTheTargetDoesNotList_IsUnknown_AndTheWorstLightWins()
    {
        var com = Com(c =>
        {
            c.RequiredLicenses = new RequiredLicensesResult([Required("{A}"), Required("{B}")], "<x/>");
            c.LicenseInfo = new LicenseInfoResult([License("{A}", LicenseStatus.Present)], "<x/>");
        });

        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateLicenses(com).Light);
    }

    [Fact]
    public void Licenses_NothingMarkedRequired_IsUnknown_NotGreen()
    {
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateLicenses(Com()).Light);
    }

    [Fact]
    public void Licenses_ATargetThatDoesNotAnswer_IsUnknown()
    {
        var com = Com(c =>
        {
            c.RequiredLicenses = new RequiredLicensesResult([Required("{A}")], "<x/>");
            c.LicenseInfo = null;
            c.Errors["licenses"] = "ADS timeout";
        });

        var item = ReleaseCheckCoordinator.EvaluateLicenses(com);

        Assert.Equal(Light.Unknown, item.Light);
        Assert.Contains("ADS timeout", item.Summary, StringComparison.Ordinal);
    }

    // ---- boot project, runtime, dialogs ----

    [Fact]
    public void BootProject_NotOlderThanTheLastBuild_IsGreen_ButSaysWhatItCannotShow()
    {
        var item = ReleaseCheckCoordinator.EvaluateBootProject(Fresh());

        Assert.Equal(Light.Green, item.Light);
        Assert.Contains("cannot be read here", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BootProject_OlderThanTheLastBuild_IsYellow()
    {
        var files = Fresh() with { NewestBootAppUtc = T0 };

        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.EvaluateBootProject(files).Light);
    }

    [Fact]
    public void BootProject_NoneOrUnreadable_IsNotGreen()
    {
        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.EvaluateBootProject(Fresh() with { NewestBootAppUtc = null }).Light);
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateBootProject(new SolutionFileFacts { Problem = "access denied" }).Light);
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateBootProject(Fresh() with { NewestCompileInfoUtc = null }).Light);
    }

    [Theory]
    [InlineData("Run", true, "Green")]
    [InlineData("Stop", true, "Yellow")]
    [InlineData("Run", false, "Unknown")]
    public void Runtime_FollowsTheState(string state, bool answered, string expected)
    {
        var com = Com(c => c.PlcState = new AdsRunStateResult(answered, state, "5.1.2.3.1.1", 851, answered ? "OK" : "[Error] timeout"));

        Assert.Equal(Enum.Parse<Light>(expected), ReleaseCheckCoordinator.EvaluateRuntime(com).Light);
    }

    private static XaeDialogView Dialog(bool blocking) =>
        new("0x1A", "Save changes?", "#32770", blocking, XaeDialogClass.Unknown, ["text"], [("OK", true)]);

    [Fact]
    public void Dialogs_FollowWhetherTheyBlock()
    {
        Assert.Equal(Light.Green, ReleaseCheckCoordinator.EvaluateDialogs([], null).Light);
        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.EvaluateDialogs([Dialog(false)], null).Light);
        Assert.Equal(Light.Red, ReleaseCheckCoordinator.EvaluateDialogs([Dialog(true)], null).Light);
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.EvaluateDialogs(null, "no window list").Light);
    }

    // ---- the whole answer ----

    [Fact]
    public void Overall_IsTheWorst_RedThenUnknownThenYellow_AndUnknownIsNeverGreen()
    {
        ReleaseItem I(Light l) => new("x", "x", l, "x", []);

        Assert.Equal(Light.Green, ReleaseCheckCoordinator.Overall([I(Light.Green)]));
        Assert.Equal(Light.Yellow, ReleaseCheckCoordinator.Overall([I(Light.Green), I(Light.Yellow)]));
        Assert.Equal(Light.Unknown, ReleaseCheckCoordinator.Overall([I(Light.Green), I(Light.Yellow), I(Light.Unknown)]));
        Assert.Equal(Light.Red, ReleaseCheckCoordinator.Overall([I(Light.Unknown), I(Light.Red), I(Light.Yellow)]));
    }

    private static ReleaseCheckCoordinator Coordinator(FakeTwinCatSession session, ReleaseCheckSources? sources = null) =>
        new(session, runLocked: null, sources ?? new ReleaseCheckSources
        {
            Dialogs = () => [],
            ScanFiles = _ => Fresh(),
            UtcNow = () => T0.AddHours(1),
        });

    private static FakeTwinCatSession OpenSession() => new() { SolutionOpen = true, IsConnected = true, ActiveSlnPath = Sln, PlcRunState = "Run" };

    [Fact]
    public void TheCheck_AnswersEveryItem_AndNothingIsGreenThatItCannotKnow()
    {
        var session = OpenSession();
        session.FakePlcLibraryContexts.Add(new PlcLibraryContext("PLC", [Ref("Tc2_Standard", "3.4.1.0")], [], [Installed("Tc2_Standard", "3.4.1.0")]));

        var json = JsonDocument.Parse(Coordinator(session).Check()).RootElement;

        Assert.True(json.GetProperty("success").GetBoolean());
        var items = json.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("status").GetString()!);
        Assert.Equal(
            ["build", "check_all_objects", "static_analysis", "libraries", "licenses", "boot_project", "target_runtime", "xae_dialogs"],
            items.Keys.ToArray());
        Assert.Equal("green", items["build"]);
        Assert.Equal("green", items["libraries"]);
        Assert.Equal("unknown", items["check_all_objects"]);
        Assert.Equal("unknown", items["licenses"]); // nothing marked as required
        Assert.Equal("unknown", json.GetProperty("overall").GetString());
        Assert.Equal(2, json.GetProperty("unknown").GetInt32()); // check_all_objects and licenses
        Assert.Equal(6, json.GetProperty("green").GetInt32());
    }

    [Fact]
    public void TheCheck_ChangesNothing_ItReadsOnly()
    {
        var session = OpenSession();

        Coordinator(session).Check();

        // Reads of the session only: no build, no check, no analysis, no save, no activation.
        Assert.All(session.CallLog, call =>
        {
            Assert.DoesNotContain("Build", call, StringComparison.Ordinal);
            Assert.DoesNotContain("CheckAll", call, StringComparison.Ordinal);
            Assert.DoesNotContain("RunStaticAnalysis", call, StringComparison.Ordinal);
            Assert.DoesNotContain("Save", call, StringComparison.Ordinal);
            Assert.DoesNotContain("Activate", call, StringComparison.Ordinal);
            Assert.DoesNotContain("Clear", call, StringComparison.Ordinal);
        });
        Assert.Contains("GetErrorList()", session.CallLog);
    }

    [Fact]
    public void TheCheck_AReadThatFails_IsOneUnknownItem_NotAFailedCheck()
    {
        var session = OpenSession();
        session.GetLicenseInfoThrows = new InvalidOperationException("ADS timeout");
        session.FakeRequiredLicensesResult = new RequiredLicensesResult([Required("{A}")], "<x/>");

        var json = JsonDocument.Parse(Coordinator(session).Check()).RootElement;

        var licenses = json.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "licenses");
        Assert.Equal("unknown", licenses.GetProperty("status").GetString());
        Assert.Contains("ADS timeout", licenses.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(json.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void TheCheck_NoOpenSolution_IsAnError()
    {
        var session = new FakeTwinCatSession { SolutionOpen = false, IsConnected = true };

        string text = Coordinator(session).Check();

        Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCheck_ABlockingDialog_IsRed_AndTheXaeIsNotAskedAnything()
    {
        var session = OpenSession();
        var sources = new ReleaseCheckSources { Dialogs = () => [Dialog(blocking: true)], ScanFiles = _ => Fresh(), UtcNow = () => T0 };

        var json = JsonDocument.Parse(Coordinator(session, sources).Check()).RootElement;

        var byId = json.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("status").GetString()!);
        Assert.Equal("red", byId["xae_dialogs"]);
        Assert.Equal("unknown", byId["build"]);
        Assert.Equal("unknown", byId["libraries"]);
        Assert.Equal("unknown", byId["target_runtime"]);
        Assert.Equal("red", json.GetProperty("overall").GetString());
        Assert.Empty(session.CallLog); // no COM call was made while a dialog holds the XAE
    }

    [Fact]
    public void TheCheck_RunsTheXaeReadsInTheLock_AndTheDialogListAndFileScanOutsideIt()
    {
        var session = OpenSession();
        bool inLock = false;
        var outside = new List<string>();
        var sources = new ReleaseCheckSources
        {
            Dialogs = () => { if (inLock) outside.Add("dialogs in lock"); return []; },
            ScanFiles = _ => { if (inLock) outside.Add("scan in lock"); return Fresh(); },
            UtcNow = () => T0,
        };
        var coordinator = new ReleaseCheckCoordinator(session, body =>
        {
            inLock = true;
            try { return body(); }
            finally { inLock = false; }
        }, sources);

        coordinator.Check();

        Assert.Empty(outside);
        Assert.Contains("GetErrorList()", session.CallLog);
    }

    // ---- the file scan ----

    [Fact]
    public void TheFileScan_FindsTheNewestSource_TheCompileInfoAndTheBootApplication()
    {
        string dir = Path.Combine(Path.GetTempPath(), "release-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Plc", "POUs"));
            Directory.CreateDirectory(Path.Combine(dir, "Plc", "_CompileInfo"));
            Directory.CreateDirectory(Path.Combine(dir, "Plc", "_Boot", "TwinCAT RT (x64)"));
            Directory.CreateDirectory(Path.Combine(dir, "Plc", "_Libraries", "Beckhoff"));
            string source = Path.Combine(dir, "Plc", "POUs", "MAIN.TcPOU");
            File.WriteAllText(source, "x");
            File.SetLastWriteTimeUtc(source, T0);
            // A source inside a library cache is not the project's own.
            string cached = Path.Combine(dir, "Plc", "_Libraries", "Beckhoff", "Lib.TcPOU");
            File.WriteAllText(cached, "x");
            File.SetLastWriteTimeUtc(cached, T0.AddDays(30));
            string info = Path.Combine(dir, "Plc", "_CompileInfo", "Plc.compileinfo");
            File.WriteAllText(info, "x");
            File.SetLastWriteTimeUtc(info, T0.AddMinutes(5));
            string app = Path.Combine(dir, "Plc", "_Boot", "TwinCAT RT (x64)", "Port_851.app");
            File.WriteAllText(app, "x");
            File.SetLastWriteTimeUtc(app, T0.AddMinutes(6));

            var facts = SolutionFileScan.Scan(Path.Combine(dir, "Plant.sln"));

            Assert.Equal(T0, facts.NewestSourceUtc);
            Assert.Equal(source, facts.NewestSource);
            Assert.Equal(T0.AddMinutes(5), facts.NewestCompileInfoUtc);
            Assert.Equal(T0.AddMinutes(6), facts.NewestBootAppUtc);
            Assert.Null(facts.Problem);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheFileScan_ADirectoryThatIsNotThere_IsAProblem_NotACrash()
    {
        var facts = SolutionFileScan.Scan(@"Z:\does\not\exist\Plant.sln");

        Assert.NotNull(facts.Problem);
        Assert.Null(facts.NewestCompileInfoUtc);
    }

    // ---- the tool in the catalog ----

    [Fact]
    public void BothWorkflowTools_AreBuildToolsetMembers_AndInTheDocumentedProfileSets()
    {
        Assert.Equal(PtcXaeMcp.WinForms.Mcp.Toolset.Build, PtcXaeMcp.WinForms.Mcp.ToolCatalog.Find("twincat_deploy")!.Toolset);
        Assert.Equal(PtcXaeMcp.WinForms.Mcp.Toolset.Build, PtcXaeMcp.WinForms.Mcp.ToolCatalog.Find("twincat_release_check")!.Toolset);
        Assert.Contains("twincat_deploy", TwinCatSystemService.AdsTools);
    }
}
