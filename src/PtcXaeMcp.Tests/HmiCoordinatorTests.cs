using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class HmiCoordinatorTests
{
    private const string PlantHmi = "PlantHmi";
    private const string SecondHmi = "SecondHmi";
    private const string LicenseDialog = "Activate Engineering Server Trial License";

    private static HmiProjectEntry Entry(string name, bool ready = true) =>
        new(name, $@"C:\Repos\{name}\{name}", ready, false);

    private static FakeTwinCatSession Session(params HmiProjectEntry[] projects) => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1, projects, null),
    };

    private static HmiProjectInfo Info(string name, bool withFiles = false, bool truncated = false) =>
        new(name, $@"C:\Repos\{name}\{name}", true, false,
            new Dictionary<string, string?> { ["Locale"] = "de", ["StartupView"] = "Desktop.view" },
            [
                new HmiServerExtension("ADS", "Initialized", true, "14.5.73.0"),
                new HmiServerExtension("TcHmiSrv", "Initialized", true, "14.5.73.0"),
                new HmiServerExtension("TcHmiEventLogger", "Invalid", false, "0.0.0.0"),
            ],
            withFiles
                ? [new HmiFileNode("Server", "Server/", 9, null), new HmiFileNode("Desktop.view", "Desktop.view", 0, null)]
                : null,
            truncated);

    // ── ListProjects ─────────────────────────────────────────────────────────

    [Fact]
    public void ListProjects_NotConnected_ReturnsErrorWithoutTouchingHmi()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var (summary, json) = new HmiCoordinator(fake).ListProjects();

        Assert.StartsWith("[Error]", summary);
        Assert.Equal("{}", json);
        Assert.DoesNotContain("ListHmiProjects()", fake.CallLog);
    }

    [Fact]
    public void ListProjects_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new HmiCoordinator(fake).ListProjects().Summary);
    }

    [Fact]
    public void ListProjects_NoHmiProject_IsAnAnswerNotAnError()
    {
        var (summary, json) = new HmiCoordinator(Session()).ListProjects();

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
        Assert.Contains("No TwinCAT HMI project", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal("Beckhoff.TcHmi.1.12", doc.RootElement.GetProperty("automationId").GetString());
    }

    [Fact]
    public void ListProjects_SeveralProjects_SummaryNamesThemAndFlagsLoading()
    {
        var (summary, json) = new HmiCoordinator(Session(Entry(PlantHmi), Entry(SecondHmi, ready: false))).ListProjects();

        Assert.StartsWith("2 HMI projects:", summary);
        Assert.Contains(PlantHmi, summary);
        Assert.Contains($"{SecondHmi} (loading)", summary);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.GetProperty("projects")[0];
        Assert.Equal(PlantHmi, first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("isReady").GetBoolean());
        Assert.StartsWith(@"C:\Repos\", first.GetProperty("projectDirectory").GetString());
    }

    [Fact]
    public void ListProjects_LicenseDialogOpen_SummaryCarriesTheHint()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiProjects = fake.HmiProjects with { BlockingDialog = LicenseDialog };

        var (summary, _) = new HmiCoordinator(fake).ListProjects();

        Assert.Contains(LicenseDialog, summary);
        Assert.Contains("CAPTCHA", summary);
    }

    [Fact]
    public void ListProjects_NotBuiltWithTcHmi_ReportsWhy()
    {
        var fake = Session();
        fake.ListHmiProjectsThrows = TwinCatNotBuilt.HmiException();

        var (summary, json) = new HmiCoordinator(fake).ListProjects();

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("TE2000", summary);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void ListProjects_AttachFailsWhileLicenseDialogOpen_ErrorCarriesTheHint()
    {
        var fake = Session();
        fake.ListHmiProjectsThrows = new InvalidOperationException("dte.GetObject(\"Beckhoff.TcHmi.1.12\") failed");
        fake.HmiBlockingDialog = LicenseDialog;

        var (summary, _) = new HmiCoordinator(fake).ListProjects();

        Assert.StartsWith("[Error]", summary);
        Assert.Contains(LicenseDialog, summary);
    }

    // ── ResolveProject ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoProjects_Error()
    {
        var (project, error) = new HmiCoordinator(Session()).ResolveProject(null);
        Assert.Null(project);
        Assert.StartsWith("[Error]", error);
        Assert.Contains(".hmiproj", error);
    }

    [Fact]
    public void Resolve_SingleProject_NameOptional()
    {
        var (project, error) = new HmiCoordinator(Session(Entry(PlantHmi))).ResolveProject(null);
        Assert.Null(error);
        Assert.Equal(PlantHmi, project!.Name);
    }

    [Fact]
    public void Resolve_SeveralProjectsWithoutName_ErrorListsNames_NoFallback()
    {
        var (project, error) = new HmiCoordinator(Session(Entry(PlantHmi), Entry(SecondHmi))).ResolveProject("  ");

        Assert.Null(project);
        Assert.StartsWith("[Error]", error);
        Assert.Contains(PlantHmi, error);
        Assert.Contains(SecondHmi, error);
    }

    [Fact]
    public void Resolve_UnknownName_ErrorListsAvailable()
    {
        var (project, error) = new HmiCoordinator(Session(Entry(PlantHmi))).ResolveProject("Nope");
        Assert.Null(project);
        Assert.Contains("'Nope' not found", error);
        Assert.Contains(PlantHmi, error);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive_AndReturnsCanonicalName()
    {
        var (project, _) = new HmiCoordinator(Session(Entry(PlantHmi), Entry(SecondHmi))).ResolveProject(" secondhmi ");
        Assert.Equal(SecondHmi, project!.Name);
    }

    [Fact]
    public void Resolve_ProjectStillLoading_Error()
    {
        var (project, error) = new HmiCoordinator(Session(Entry(PlantHmi, ready: false))).ResolveProject(PlantHmi);
        Assert.Null(project);
        Assert.Contains("still loading", error);
    }

    [Fact]
    public void Resolve_LoadingBehindLicenseDialog_ErrorNamesTheDialog()
    {
        var fake = Session(Entry(PlantHmi, ready: false));
        fake.HmiProjects = fake.HmiProjects with { BlockingDialog = LicenseDialog };

        var (_, error) = new HmiCoordinator(fake).ResolveProject(null);

        Assert.Contains("still loading", error);
        Assert.Contains(LicenseDialog, error);
    }

    // ── GetProjectInfo ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void GetProjectInfo_MaxDepthOutOfRange_RejectedBeforeAnyCall(int depth)
    {
        var fake = Session(Entry(PlantHmi));
        var (summary, _) = new HmiCoordinator(fake).GetProjectInfo(null, includeFiles: true, depth);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("maxDepth", summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void GetProjectInfo_PassesCanonicalNameAndOptions()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiProjectInfoByName[PlantHmi] = Info(PlantHmi, withFiles: true);

        new HmiCoordinator(fake).GetProjectInfo(PlantHmi.ToUpperInvariant(), includeFiles: true, maxDepth: 3);

        Assert.Equal(new[] { "ListHmiProjects()", $"GetHmiProjectInfo({PlantHmi}, files=True, depth=3)" }, fake.CallLog);
    }

    [Fact]
    public void GetProjectInfo_SummaryCountsLoadedExtensions()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiProjectInfoByName[PlantHmi] = Info(PlantHmi);

        var (summary, json) = new HmiCoordinator(fake).GetProjectInfo(null, false, 2);

        Assert.Contains("2/3 server extension(s) loaded (ADS, TcHmiSrv)", summary);
        Assert.Contains("startup view Desktop.view", summary);
        Assert.Contains("locale de", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("serverExtensions").GetArrayLength());
        Assert.Equal("de", doc.RootElement.GetProperty("config").GetProperty("Locale").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("files").ValueKind);
    }

    [Fact]
    public void GetProjectInfo_WithFiles_ReportsCountAndTruncation()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiProjectInfoByName[PlantHmi] = Info(PlantHmi, withFiles: true, truncated: true);

        var (summary, json) = new HmiCoordinator(fake).GetProjectInfo(null, true, 2);

        Assert.Contains("2 top-level item(s) (tree truncated)", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Server/", doc.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void GetProjectInfo_NothingLoadedAndLicenseDialog_SummaryCarriesTheHint()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiProjectInfoByName[PlantHmi] = Info(PlantHmi) with
        {
            ServerExtensions = [new HmiServerExtension("ADS", "Invalid", false, null)],
        };
        fake.HmiBlockingDialog = LicenseDialog;

        var (summary, _) = new HmiCoordinator(fake).GetProjectInfo(null, false, 2);

        Assert.Contains("0/1 server extension(s) loaded", summary);
        Assert.Contains(LicenseDialog, summary);
    }

    [Fact]
    public void GetProjectInfo_SessionThrows_ReturnsError()
    {
        var fake = Session(Entry(PlantHmi));
        fake.GetHmiProjectInfoThrows = new System.Runtime.InteropServices.COMException("RPC server unavailable");

        var (summary, json) = new HmiCoordinator(fake).GetProjectInfo(null, false, 2);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("RPC server unavailable", summary);
        Assert.Equal("{}", json);
    }

    // ── ListMappedSymbols ────────────────────────────────────────────────────

    private static FakeTwinCatSession WithMappedSymbols()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiMappedSymbols.AddRange(
        [
            new HmiMappedSymbol("ADS", "ADS", null, null, false, false),
            new HmiMappedSymbol("ADS.PLC1.MAIN.fbHmi.bStart", "ADS", "tchmi:general#/definitions/BOOL", "BOOL", false, false),
            new HmiMappedSymbol("ADS.PLC1.GVL_HMI.HmiOpened", "ADS", "tchmi:general#/definitions/BOOL", "BOOL", false, false),
            new HmiMappedSymbol("ADS.AdsSyncReadReq", "ADS", null, null, false, true),
            new HmiMappedSymbol("TcHmiSrv.Config", "TcHmiSrv", null, null, true, false),
        ]);
        return fake;
    }

    [Fact]
    public void ListMapped_Default_ExcludesHiddenAndSortsByName()
    {
        var (summary, json) = new HmiCoordinator(WithMappedSymbols()).ListMappedSymbols(null, null, null, false, false);

        Assert.StartsWith("4 mapped symbol(s)", summary);
        Assert.Contains("5 mapped in all", summary);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Select(e => e.GetProperty("mappedName").GetString()).ToList();
        Assert.DoesNotContain("ADS.AdsSyncReadReq", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), names);
        Assert.Equal(4, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(5, doc.RootElement.GetProperty("mappedCount").GetInt32());
        Assert.False(doc.RootElement.GetProperty("has_more").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("count", out _));
    }

    [Fact]
    public void ListMapped_LimitAndOffset_PageTheFilteredList()
    {
        var coordinator = new HmiCoordinator(WithMappedSymbols());
        var (_, fullJson) = coordinator.ListMappedSymbols(null, null, null, false, false);
        var all = JsonDocument.Parse(fullJson).RootElement.GetProperty("symbols").EnumerateArray()
            .Select(e => e.GetProperty("mappedName").GetString()).ToList();

        var (summary, json) = coordinator.ListMappedSymbols(null, null, null, false, false, limit: 2, offset: 1);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(all.Skip(1).Take(2), root.GetProperty("symbols").EnumerateArray().Select(e => e.GetProperty("mappedName").GetString()));
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Equal(1, root.GetProperty("offset").GetInt32());
        Assert.Equal(2, root.GetProperty("returned").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(3, root.GetProperty("next_offset").GetInt32());
        Assert.Contains("Showing 2–3, next_offset 3.", summary);
    }

    [Fact]
    public void ListMapped_NegativeLimit_IsRefusedBeforeAnythingIsRead()
    {
        var fake = WithMappedSymbols();
        var (summary, _) = new HmiCoordinator(fake).ListMappedSymbols(null, null, null, false, false, limit: -3);

        Assert.StartsWith("[Error] limit must be 0 or more", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ListHmiMappedSymbols", StringComparison.Ordinal));
    }

    [Fact]
    public void ListMapped_DomainAndPrefixFilter_AreCaseInsensitive()
    {
        var (summary, json) = new HmiCoordinator(WithMappedSymbols())
            .ListMappedSymbols(null, "ads", "ads.plc1.main.", false, false);

        Assert.StartsWith("1 mapped symbol(s)", summary);
        Assert.Contains("domain=ads", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ADS.PLC1.MAIN.fbHmi.bStart",
            doc.RootElement.GetProperty("symbols")[0].GetProperty("mappedName").GetString());
    }

    [Fact]
    public void ListMapped_IncludeHiddenAndRefresh_ArePassedAndApplied()
    {
        var fake = WithMappedSymbols();
        var (summary, _) = new HmiCoordinator(fake).ListMappedSymbols(null, null, null, includeHidden: true, refresh: true);

        Assert.StartsWith("5 mapped symbol(s)", summary);
        Assert.Contains($"ListHmiMappedSymbols({PlantHmi}, refresh=True)", fake.CallLog);
    }

    [Fact]
    public void ListMapped_UnresolvedProject_NeverReadsSymbols()
    {
        var fake = WithMappedSymbols();
        fake.HmiProjects = fake.HmiProjects with { Projects = [Entry(PlantHmi), Entry(SecondHmi)] };

        var (summary, _) = new HmiCoordinator(fake).ListMappedSymbols(null, null, null, false, false);

        Assert.StartsWith("[Error]", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ListHmiMappedSymbols", StringComparison.Ordinal));
    }

    // ── ReadSymbols ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadSymbols_NullOrEmpty_RejectedBeforeAnyCall()
    {
        var fake = Session(Entry(PlantHmi));
        Assert.StartsWith("[Error]", new HmiCoordinator(fake).ReadSymbols(null, null).Summary);
        Assert.StartsWith("[Error]", new HmiCoordinator(fake).ReadSymbols(null, []).Summary);
        Assert.StartsWith("[Error]", new HmiCoordinator(fake).ReadSymbols(null, ["ok", "  "]).Summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void ReadSymbols_TooMany_Rejected()
    {
        var names = Enumerable.Range(0, HmiCoordinator.MaxReadSymbols + 1).Select(i => $"S{i}").ToArray();
        var (summary, _) = new HmiCoordinator(Session(Entry(PlantHmi))).ReadSymbols(null, names);
        Assert.Contains($"At most {HmiCoordinator.MaxReadSymbols}", summary);
    }

    [Fact]
    public void ReadSymbols_TrimsAndDeduplicates_BeforeTheSessionCall()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiSymbolValues["UserSelectType"] = "0";

        new HmiCoordinator(fake).ReadSymbols(null, [" UserSelectType ", "UserSelectType"]);

        Assert.Contains($"ReadHmiSymbols({PlantHmi}, [UserSelectType])", fake.CallLog);
    }

    [Fact]
    public void ReadSymbols_OneBadName_DoesNotSinkTheBatch()
    {
        var fake = Session(Entry(PlantHmi));
        fake.HmiSymbolValues["UserSelectType"] = "0";
        fake.HmiSymbolValues["IsAuthRequired"] = "False";

        var (summary, json) = new HmiCoordinator(fake).ReadSymbols(null, ["UserSelectType", "NoSuch.Symbol", "IsAuthRequired"]);

        Assert.StartsWith("Read 2/3 symbol(s)", summary);
        Assert.Contains("NoSuch.Symbol (", summary);
        Assert.Contains("SYMBOL_NOT_MAPPED", summary);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal("NoSuch.Symbol", results[1].GetProperty("symbol").GetString());
        Assert.False(results[1].GetProperty("ok").GetBoolean());
        Assert.Equal("False", results[2].GetProperty("value").GetString());
    }

    [Fact]
    public void ReadSymbols_AdsTimeout_AddsTheUnverifiedRuntimeNote()
    {
        // The exact failure seen live when the PLC runtime was unreachable.
        var fake = Session(Entry(PlantHmi));
        fake.HmiSymbolErrors["ADS.PLC1.GVL_HMI.HmiOpened"] =
            "server request: Command Error: Code:1048597, Message: HMI_ADS_E_SYNC_TIMEOUT, " +
            "Reason: Symbol information is out of date and currently cannot be updated because the connection timed out.";

        var (summary, _) = new HmiCoordinator(fake).ReadSymbols(null, ["ADS.PLC1.GVL_HMI.HmiOpened"]);

        Assert.StartsWith("Read 0/1", summary);
        Assert.Contains("HMI_ADS_E_SYNC_TIMEOUT", summary);
        Assert.Contains("Not verified", summary);
    }

    [Fact]
    public void ReadSymbols_UnmappedName_NoAdsNote()
    {
        var (summary, _) = new HmiCoordinator(Session(Entry(PlantHmi))).ReadSymbols(null, ["NoSuch.Symbol"]);
        Assert.DoesNotContain("Not verified", summary);
    }

    // ── ControlServer ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("restart")]
    [InlineData("publish")]
    public void ControlServer_UnknownAction_RejectedBeforeAnyCall(string? action)
    {
        var fake = Session(Entry(PlantHmi));
        var (summary, _) = new HmiCoordinator(fake).ControlServer(null, action, 1000);

        Assert.StartsWith("[Error] action must be one of", summary);
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(HmiCoordinator.MaxServerTimeoutMs + 1)]
    public void ControlServer_TimeoutOutOfRange_Rejected(int timeoutMs)
    {
        var (summary, _) = new HmiCoordinator(Session(Entry(PlantHmi))).ControlServer(null, "start", timeoutMs);
        Assert.Contains("timeoutMs", summary);
    }

    [Fact]
    public void ControlServer_NormalizesAction_AndReportsLoadedCount()
    {
        var fake = Session(Entry(PlantHmi));
        var (summary, json) = new HmiCoordinator(fake).ControlServer(null, "  START ", 5000);

        Assert.Contains($"ControlHmiServer({PlantHmi}, start, 5000)", fake.CallLog);
        Assert.Equal($"HMI engineering server of '{PlantHmi}': start done — 1/1 server extension(s) loaded.", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("settled").GetBoolean());
    }

    [Fact]
    public void ControlServer_WorksWhileProjectReportsNotReady()
    {
        // After a shutdown the project may report IsReady=false; start must stay possible.
        var fake = Session(Entry(PlantHmi, ready: false));
        var (summary, _) = new HmiCoordinator(fake).ControlServer(null, "start", 1000);
        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
    }

    [Fact]
    public void ControlServer_NotSettled_IsAnError_WithDialogHint()
    {
        var fake = Session(Entry(PlantHmi));
        fake.NextHmiServerControlResult = new HmiServerControlResult("start", true, false, null,
            [new HmiServerExtension("ADS", "Invalid", false, null)]);
        fake.HmiBlockingDialog = LicenseDialog;

        var (summary, _) = new HmiCoordinator(fake).ControlServer(null, "start", 2000);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("had not settled after 2000 ms (0/1", summary);
        Assert.Contains(LicenseDialog, summary);
    }

    [Fact]
    public void ControlServer_SessionThrows_ReturnsError()
    {
        var fake = Session(Entry(PlantHmi));
        fake.ControlHmiServerThrows = new System.Runtime.InteropServices.COMException("server gone");

        Assert.StartsWith("[Error] server gone", new HmiCoordinator(fake).ControlServer(null, "shutdown", 1000).Summary);
    }

    // ── server error condensing ──────────────────────────────────────────────

    [Fact]
    public void CondenseServerError_KeepsHeadlineDropsRequestDumpAndSessionId()
    {
        // Shape of the live LastError (2026-09-25), shortened.
        const string live =
            "server request: Command Error: Code:513, Message: SYMBOL_NOT_MAPPED, Reason: Symbol not mapped\r\n" +
            "Symbol: NoSuch.Symbol\r\n\r\nComplete Command:\r\n{\r\n  \"sessionId\": \"09f43ad6…\"\r\n}";

        string condensed = HmiCoordinator.CondenseServerError(live);

        Assert.Equal("server request: Command Error: Code:513, Message: SYMBOL_NOT_MAPPED, Reason: Symbol not mapped", condensed);
        Assert.DoesNotContain("sessionId", condensed);
    }

    [Fact]
    public void CondenseServerError_CapsLength()
    {
        string condensed = HmiCoordinator.CondenseServerError(new string('x', 1000));
        Assert.Equal(HmiCoordinator.MaxServerErrorLength + 1, condensed.Length);
        Assert.EndsWith("…", condensed);
    }

    [Fact]
    public void CondenseServerError_SkipsLeadingBlankLines()
    {
        Assert.Equal("Unexpected end", HmiCoordinator.CondenseServerError("\r\n  \r\nUnexpected end\r\nmore"));
    }

    // ── license-dialog title matcher (Win32 detection, never automation) ─────

    [Theory]
    [InlineData("Activate Engineering Server Trial License", true)]
    [InlineData("Engineering Server Testlizenz aktivieren", true)]
    [InlineData("TwinCAT Engineering", false)]
    [InlineData("Engineering Server", false)]
    [InlineData("Trial License", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LicenseDialogTitleMatcher(string? title, bool expected) =>
        Assert.Equal(expected, Win32DialogAutomation.IsHmiEngineeringLicenseTitle(title));
}
