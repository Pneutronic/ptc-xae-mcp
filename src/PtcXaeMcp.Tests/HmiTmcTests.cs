using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// hmi-ads-tmc. Three invariants every test guards:
/// nothing reaches AttachHmiTmcFile/DetachHmiTmcFile unless the engineer confirmed AND the
/// .tmc on disk was identified as a PLC module description; the duplicates TE2000 answers
/// with a modal box are refused before the call; and success is reported only after the
/// project's HmiTmcFiles property reads the new state back — never on the Automation
/// Interface's own return value, which is true on failure and false on success.
/// </summary>
[Collection(PathPolicyEnvironment.Name)]
public class HmiTmcTests : IDisposable
{
    private const string Project = "HMI_Fixture";

    private readonly string _root;
    private readonly string _solutionDir;
    private readonly string _projectDir;

    public HmiTmcTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ptc-tmc-{Guid.NewGuid():N}");
        _solutionDir = Path.Combine(_root, "sln");
        _projectDir = Path.Combine(_solutionDir, Project);
        Directory.CreateDirectory(_projectDir);
        File.WriteAllText(Path.Combine(_projectDir, Project + ".hmiproj"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <ProjectGuid>{11111111-2222-3333-4444-555555555555}</ProjectGuid>
              </PropertyGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private FakeTwinCatSession Session(string? runtimes = null)
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ActiveSlnPath = Path.Combine(_solutionDir, "Fixture.sln"),
            HmiProjects = new HmiProjectList("Beckhoff.TcHmi.1.12", 1,
                [new HmiProjectEntry(Project, _projectDir, true, false)], null),
        };
        fake.SetAdsRuntimes(runtimes ?? FakeTwinCatSession.AdsRuntimeJson());
        return fake;
    }

    private static bool Wrote(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("AttachHmiTmcFile", StringComparison.Ordinal)
                           || c.StartsWith("DetachHmiTmcFile", StringComparison.Ordinal));

    /// <summary>A file with the real shape of a PLC project's .tmc, inside the solution tree.</summary>
    private string TmcOnDisk(string name = "PLC", int symbols = 2, string generatedBy = "TwinCAT XAE Plc",
        string root = TmcFile.RootElement, string extension = TmcFile.Extension, string? subFolder = "PlcProj")
    {
        string dir = subFolder is null ? _root : Path.Combine(_solutionDir, subFolder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + extension);
        string body = string.Concat(Enumerable.Repeat(
            "<Symbol><Name>MAIN.bTest</Name><BaseType>BOOL</BaseType></Symbol>", symbols));
        File.WriteAllText(path,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{root} GeneratedBy=\"{generatedBy}\">{body}</TcModuleClass>");
        return path;
    }

    private static (string Summary, string Json) Attach(FakeTwinCatSession fake, IHmiWriteConfirmer? confirmer,
        string? tmcFile, string? virtualPlcName = null, int timeout = 120) =>
        new HmiCoordinator(fake, confirmer).AttachTmcFile(null, tmcFile, virtualPlcName, timeout);

    private static (string Summary, string Json) Detach(FakeTwinCatSession fake, IHmiWriteConfirmer? confirmer,
        string? virtualPlcName, int timeout = 120) =>
        new HmiCoordinator(fake, confirmer).DetachTmcFile(null, virtualPlcName, timeout);

    // ── TmcFile.Inspect: the file is identified, not just named ──────────────

    [Fact]
    public void Inspect_RealShape_ReportsSymbolCountAndGenerator()
    {
        var (facts, error) = TmcFile.Inspect(TmcOnDisk(symbols: 7));

        Assert.Null(error);
        Assert.NotNull(facts);
        Assert.Equal(7, facts.SymbolCount);
        Assert.Equal("TwinCAT XAE Plc", facts.GeneratedBy);
        Assert.True(facts.SizeBytes > 0);
    }

    [Fact]
    public void Inspect_CountsSymbolsAcrossChunkBoundaries()
    {
        // 20k symbols push the stream well past the 64 KB read buffer, so a tag split
        // across two chunks would be miscounted.
        var (facts, error) = TmcFile.Inspect(TmcOnDisk(symbols: 20_000));

        Assert.Null(error);
        Assert.Equal(20_000, facts!.SymbolCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Inspect_NoPath_IsRefused(string? path)
    {
        var (facts, error) = TmcFile.Inspect(path);
        Assert.Null(facts);
        Assert.Contains("tmcFile is required", error);
    }

    [Fact]
    public void Inspect_RelativePath_IsRefused()
    {
        var (facts, error) = TmcFile.Inspect(@"PLC\PLC.tmc");
        Assert.Null(facts);
        Assert.Contains("must be an absolute path", error);
    }

    [Fact]
    public void Inspect_MissingFile_IsRefused()
    {
        var (facts, error) = TmcFile.Inspect(Path.Combine(_root, "nope.tmc"));
        Assert.Null(facts);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void Inspect_WrongExtension_IsRefused()
    {
        var (facts, error) = TmcFile.Inspect(TmcOnDisk(extension: ".xml"));
        Assert.Null(facts);
        Assert.Contains("does not end in .tmc", error);
    }

    [Fact]
    public void Inspect_NotAModuleDescription_IsRefused()
    {
        var (facts, error) = TmcFile.Inspect(TmcOnDisk(root: "<SomethingElse"));
        Assert.Null(facts);
        Assert.Contains("not a TwinCAT module description", error);
    }

    [Fact]
    public void Inspect_EmptyFile_IsRefused()
    {
        string path = Path.Combine(_root, "empty.tmc");
        File.WriteAllText(path, "");

        var (facts, error) = TmcFile.Inspect(path);
        Assert.Null(facts);
        Assert.Contains("is empty", error);
    }

    // ── HmiTmcPaths: the same strings TE2000 writes ──────────────────────────

    [Fact]
    public void StoragePath_BelowSolution_IsStoredRelative() =>
        Assert.Equal(@"PlcProj\PLC.tmc",
            HmiTmcPaths.StoragePath(@"C:\sln\PlcProj\PLC.tmc", @"C:\sln"));

    [Fact]
    public void StoragePath_OutsideSolution_StaysAbsolute() =>
        Assert.Equal(@"D:\elsewhere\PLC.tmc",
            HmiTmcPaths.StoragePath(@"D:\elsewhere\PLC.tmc", @"C:\sln"));

    [Fact]
    public void StoragePath_AboveSolution_StaysAbsolute() =>
        Assert.Equal(@"C:\PLC.tmc", HmiTmcPaths.StoragePath(@"C:\PLC.tmc", @"C:\sln\deeper"));

    [Fact]
    public void StoragePath_WithoutSolution_StaysAbsolute() =>
        Assert.Equal(@"C:\sln\PLC.tmc", HmiTmcPaths.StoragePath(@"C:\sln\PLC.tmc", null));

    [Fact]
    public void Resolve_IsTheInverseOfStoragePath()
    {
        string stored = HmiTmcPaths.StoragePath(@"C:\sln\PlcProj\PLC.tmc", @"C:\sln");
        Assert.Equal(@"C:\sln\PlcProj\PLC.tmc", HmiTmcPaths.Resolve(stored, @"C:\sln"));
    }

    [Theory]
    [InlineData("PLC", true)]
    [InlineData("12345_PlantPlc", true)]              // a project name may start with a digit
    [InlineData("PLC-1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("PLC 1", false)]
    [InlineData("PLC.tmc", false)]
    [InlineData(@"a\b", false)]
    [InlineData("PLC1::MAIN", false)]
    public void IsValidVirtualPlcName_RejectsWhatCannotBeASymbolRoot(string name, bool valid) =>
        Assert.Equal(valid, HmiTmcPaths.IsValidVirtualPlcName(name));

    [Fact]
    public void ParseAttachments_Garbage_IsNullNotEmpty()
    {
        Assert.Null(HmiTmcPaths.ParseAttachments("not json", null));
        Assert.Null(HmiTmcPaths.ParseAttachments("{}", null));
        Assert.Null(HmiTmcPaths.ParseAttachments("[1,2]", null));
    }

    [Fact]
    public void ParseAttachments_RealShape_IsRead()
    {
        var list = HmiTmcPaths.ParseAttachments(
            """[{"FileName":"PlcProj\\PLC.tmc","RootNamespace":"PLC","PlcName":"PLC1"}]""", @"C:\sln");

        HmiTmcAttachment a = Assert.Single(list!);
        Assert.Equal("PLC", a.RootNamespace);
        Assert.Equal(@"PlcProj\PLC.tmc", a.StoredFileName);
        Assert.Equal("PLC1", a.PlcName);
        Assert.Equal(@"C:\sln\PlcProj\PLC.tmc", a.ResolvedPath);
        Assert.False(a.FileExists);
    }

    [Fact]
    public void ReadAttachments_ProjectWithoutTheProperty_IsEmptyNotNull()
    {
        var list = HmiTmcPaths.ReadAttachments(Path.Combine(_projectDir, Project + ".hmiproj"), _solutionDir);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public void ReadAttachments_MissingProjectFile_IsNull() =>
        Assert.Null(HmiTmcPaths.ReadAttachments(Path.Combine(_projectDir, "gone.hmiproj"), _solutionDir));

    // ── ADS.Config::RUNTIMES parsing ────────────────────────────────────────

    [Fact]
    public void ParseRuntimes_WithoutTmcKey_IsNotUsingTmc()
    {
        var runtimes = HmiCoordinator.TryParseAdsRuntimes(FakeTwinCatSession.AdsRuntimeJson());

        HmiAdsRuntime r = Assert.Single(runtimes!);
        Assert.Equal("PLC1", r.Name);
        Assert.Equal("127.0.0.1.1.1", r.NetId);
        Assert.Equal(851, r.Port);
        Assert.True(r.Enabled);
        Assert.False(r.UseTmc);
        Assert.Null(r.TmcFile);
    }

    [Fact]
    public void ParseRuntimes_ReadsTheRealSchemaKeys()
    {
        // Keys and nesting exactly as TcHmiAds.Schema.json:150-241 defines them.
        var runtimes = HmiCoordinator.TryParseAdsRuntimes("""
            {
              "PLC2": { "ENABLED": false, "NETID": "5.6.7.8.1.1", "PORT": 852, "READ_ONLY": true,
                        "USE_WHITELISTING": true, "SYMBOLS": { "a": {}, "b": {} },
                        "TMC": { "USE_TMC": true, "DATA_SYMBOLUPANDDOWNLOAD": "up.bin" } },
              "PLC1": { "ENABLED": true, "NETID": "1.2.3.4.1.1", "PORT": 851, "READ_ONLY": false,
                        "USE_WHITELISTING": false, "SYMBOLS": {} }
            }
            """);

        Assert.NotNull(runtimes);
        Assert.Equal(["PLC1", "PLC2"], runtimes.Select(r => r.Name));   // sorted
        HmiAdsRuntime plc2 = runtimes.Single(r => r.Name == "PLC2");
        Assert.False(plc2.Enabled);
        Assert.True(plc2.ReadOnly);
        Assert.True(plc2.UseWhitelisting);
        Assert.Equal(2, plc2.SymbolCount);
        Assert.True(plc2.UseTmc);
        Assert.Equal("up.bin", plc2.TmcFile);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"PLC1\": 5 }")]
    public void ParseRuntimes_Garbage_IsNullNotEmpty(string json) =>
        Assert.Null(HmiCoordinator.TryParseAdsRuntimes(json));

    // ── list_ads_runtimes ───────────────────────────────────────────────────

    [Fact]
    public void ListRuntimes_NotConnected_ReturnsErrorWithoutTouchingHmi()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var (summary, json) = new HmiCoordinator(fake).ListAdsRuntimes(null);

        Assert.StartsWith("[Error]", summary);
        Assert.Equal("{}", json);
        Assert.DoesNotContain("ListHmiProjects()", fake.CallLog);
    }

    [Fact]
    public void ListRuntimes_ReportsTmcAndAdsSources()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson(useTmc: true, tmcFile: @"ADS\up.bin"));

        var (summary, json) = new HmiCoordinator(fake).ListAdsRuntimes(null);

        Assert.Contains("1 ADS runtime(s)", summary);
        Assert.Contains("PLC1 (TMC)", summary);
        Assert.Contains("1 of them use a TMC file", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("usingTmc").GetInt32());
        Assert.True(doc.RootElement.GetProperty("runtimes")[0].GetProperty("useTmc").GetBoolean());
    }

    [Fact]
    public void ListRuntimes_UnreadableSymbol_IsAnErrorNotAnEmptyList()
    {
        var fake = Session();
        fake.HmiSymbolValues.Remove(HmiCoordinator.AdsRuntimesSymbol);
        fake.HmiSymbolErrors[HmiCoordinator.AdsRuntimesSymbol] = "server request: SYMBOL_NOT_MAPPED";

        var (summary, json) = new HmiCoordinator(fake).ListAdsRuntimes(null);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("ADS.Config::RUNTIMES", summary);
        Assert.Equal("{}", json);
    }

    // ── list_tmc_files ──────────────────────────────────────────────────────

    [Fact]
    public void ListTmcFiles_NothingAttached_SaysSo()
    {
        var (summary, json) = new HmiCoordinator(Session()).ListTmcFiles(null);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("no TMC file attached", summary);
        Assert.Contains("No ADS runtime uses a TMC file server-side", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ListTmcFiles_AfterAttach_ReportsRelativePathAndExistence()
    {
        var fake = Session();
        string tmc = TmcOnDisk();
        Attach(fake, new FakeHmiConfirmer(), tmc);

        var (summary, json) = new HmiCoordinator(fake).ListTmcFiles(null);

        Assert.Contains("1 TMC file(s) attached", summary);
        Assert.Contains(@"PLC → PlcProj\PLC.tmc", summary);
        using var doc = JsonDocument.Parse(json);
        JsonElement a = doc.RootElement.GetProperty("attachments")[0];
        Assert.Equal("PLC", a.GetProperty("rootNamespace").GetString());
        Assert.Equal(@"PlcProj\PLC.tmc", a.GetProperty("storedFileName").GetString());
        Assert.Equal(tmc, a.GetProperty("resolvedPath").GetString());
        Assert.True(a.GetProperty("fileExists").GetBoolean());
    }

    [Fact]
    public void ListTmcFiles_AttachedFileDeleted_IsWarned()
    {
        var fake = Session();
        string tmc = TmcOnDisk();
        Attach(fake, new FakeHmiConfirmer(), tmc);
        File.Delete(tmc);

        var (summary, _) = new HmiCoordinator(fake).ListTmcFiles(null);

        Assert.Contains("no longer exist on disk", summary);
    }

    [Fact]
    public void ListTmcFiles_UnreadableRuntimeTable_SaysUnknownNotNone()
    {
        // Review finding E-5: "could not read" must never be rendered as a fact.
        var fake = Session();
        fake.HmiSymbolValues.Remove(HmiCoordinator.AdsRuntimesSymbol);
        fake.HmiSymbolErrors[HmiCoordinator.AdsRuntimesSymbol] = "server request: SYMBOL_NOT_MAPPED";

        var (summary, json) = new HmiCoordinator(fake).ListTmcFiles(null);

        Assert.DoesNotContain("[Error]", summary);                    // secondary info, not fatal
        Assert.Contains("UNKNOWN", summary);
        Assert.DoesNotContain("No ADS runtime uses a TMC file", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("adsRuntimesReadable").GetBoolean());
    }

    [Fact]
    public void ListTmcFiles_UnreadableProject_IsAnError()
    {
        var fake = Session();
        File.Delete(Path.Combine(_projectDir, Project + ".hmiproj"));

        var (summary, json) = new HmiCoordinator(fake).ListTmcFiles(null);

        Assert.StartsWith("[Error]", summary);
        Assert.Equal("{}", json);
    }

    // ── attach: nothing without a confirmed engineer ─────────────────────────

    [Fact]
    public void Attach_Rejected_WritesNothing()
    {
        var fake = Session();
        var (summary, _) = Attach(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected), TmcOnDisk());

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("rejected", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_TimedOut_WritesNothing()
    {
        var fake = Session();
        var (summary, _) = Attach(fake, new FakeHmiConfirmer(HmiConfirmOutcome.TimedOut), TmcOnDisk());

        Assert.StartsWith("[Error]", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_NoConfirmer_WritesNothing()
    {
        var fake = Session();
        var (summary, _) = Attach(fake, null, TmcOnDisk());

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("No confirmation channel", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_BadFile_NeverReachesTheConfirmer()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Attach(fake, confirmer, Path.Combine(_root, "exist.tmc"));

        Assert.StartsWith("[Error]", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_TmcWithoutSymbols_NamesTheEventClassTrap()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Attach(fake, confirmer, TmcOnDisk(symbols: 0));

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("HMI_Error.tmc", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
    }

    [Theory]
    [InlineData("PLC 1")]
    [InlineData("PLC1::MAIN")]
    public void Attach_InvalidVirtualPlcName_IsRefusedBeforeAnything(string name)
    {
        var fake = Session();
        var (summary, _) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk(), name);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("cannot be a symbol root", summary);
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public void Attach_TimeoutOutOfRange_IsRefused(int seconds)
    {
        var fake = Session();
        var (summary, _) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk(), null, seconds);

        Assert.StartsWith("[Error] confirmTimeoutSeconds", summary);
        Assert.Empty(fake.CallLog);
    }

    // ── attach: the duplicates TE2000 would answer with a modal box ──────────

    [Fact]
    public void Attach_SameFileTwice_IsRefusedBeforeTheCall()
    {
        var fake = Session();
        string tmc = TmcOnDisk();
        Attach(fake, new FakeHmiConfirmer(), tmc);
        fake.CallLog.Clear();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Attach(fake, confirmer, tmc, "OtherName");

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("already attached", summary);
        Assert.Contains("virtual PLC name 'PLC'", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_NameAlreadyUsed_IsRefusedBeforeTheCall()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));
        fake.CallLog.Clear();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Attach(fake, confirmer, TmcOnDisk("Other"), "PLC");

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("already used", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
    }

    // ── attach: the happy path, and what the engineer sees ──────────────────

    [Fact]
    public void Attach_Confirmed_AttachesThenReadsBack()
    {
        var fake = Session();
        string tmc = TmcOnDisk(symbols: 3);

        var (summary, json) = Attach(fake, new FakeHmiConfirmer(), tmc);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("attached to 'HMI_Fixture' as virtual PLC 'PLC'", summary);
        Assert.Contains("3 symbols", summary);
        Assert.Contains("verified in the project's HmiTmcFiles", summary);
        Assert.Contains("without a PLC connection", summary);
        Assert.Contains($"AttachHmiTmcFile({Project}, PLC, {tmc})", fake.CallLog);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("PLC", doc.RootElement.GetProperty("virtualPlcName").GetString());
        Assert.Equal(tmc, doc.RootElement.GetProperty("tmcFile").GetString());
        Assert.Equal(@"PlcProj\PLC.tmc", doc.RootElement.GetProperty("storedAs").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("symbolCount").GetInt32());
        Assert.Equal("TMC", doc.RootElement.GetProperty("domain").GetString());
    }

    [Fact]
    public void Attach_DefaultName_IsTheFileNameLikeTheTe2000Dialog()
    {
        var fake = Session();
        var (summary, json) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("12345_PlantPlc"));

        Assert.DoesNotContain("[Error]", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("12345_PlantPlc", doc.RootElement.GetProperty("virtualPlcName").GetString());
    }

    [Fact]
    public void Attach_PopupShowsNameFileAndThatNoPlcIsTouched()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();
        string tmc = TmcOnDisk(symbols: 5);

        Attach(fake, confirmer, tmc);

        HmiWriteRequest request = Assert.Single(confirmer.Requests).Request;
        Assert.Equal(HmiWriteKind.AttachTmcFile, request.Kind);
        Assert.Equal("PLC", request.Symbol);
        Assert.Equal("TMC", request.Domain);
        Assert.False(request.WritesToPlc);
        Assert.Contains(tmc, request.NewValue);
        Assert.Contains("5 symbols", request.NewValue);
        Assert.Contains("GeneratedBy=TwinCAT XAE Plc", request.NewValue);
        Assert.Contains("no TMC file attached", request.CurrentValue);
        Assert.Contains("ADS runtime : PLC", HmiConfirmText.Details(request));
        Assert.Contains("TMC file", HmiConfirmText.Headline(request));
    }

    [Fact]
    public void Attach_TwoDifferentFiles_BothLand()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));
        var (summary, json) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("Safety"));

        Assert.DoesNotContain("[Error]", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("attachedTotal").GetInt32());
    }

    // ── attach: the Automation Interface's return value is not evidence ──────

    [Fact]
    public void Attach_ApiSaysTrueButNothingStored_IsAnError()
    {
        var fake = Session();
        fake.HmiTmcAttachDoesNothing = true;   // exactly what TE2000 does on a failure
        fake.HmiTmcLastError = "some server reason";

        var (summary, json) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk());

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("not in the project's HmiTmcFiles on read-back", summary);
        Assert.Contains("some server reason", summary);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Attach_ApiThrows_IsAnError()
    {
        var fake = Session();
        fake.AttachHmiTmcFileThrows = new InvalidOperationException("COM is unhappy");

        var (summary, _) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk());

        Assert.StartsWith("[Error] AttachTmcFile failed", summary);
        Assert.Contains("COM is unhappy", summary);
    }

    // ── detach ──────────────────────────────────────────────────────────────

    [Fact]
    public void Detach_NothingAttached_IsNothingToDoWithoutPopup()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, json) = Detach(fake, confirmer, "PLC");

        Assert.StartsWith("Nothing to do", summary);
        Assert.Contains("none at all", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("detached").GetBoolean());
    }

    [Fact]
    public void Detach_OtherNameAttached_ListsWhatIsThere()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));

        var (summary, _) = Detach(fake, new FakeHmiConfirmer(), "Safety");

        Assert.StartsWith("Nothing to do", summary);
        Assert.Contains("attached: PLC", summary);
    }

    [Fact]
    public void Detach_Confirmed_DetachesThenReadsBack()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));

        var (summary, json) = Detach(fake, new FakeHmiConfirmer(), "PLC");

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("detached from 'HMI_Fixture'", summary);
        Assert.Contains("verified in the project's HmiTmcFiles", summary);
        Assert.Contains($"DetachHmiTmcFile({Project}, PLC)", fake.CallLog);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("detached").GetBoolean());
        Assert.Equal(@"PlcProj\PLC.tmc", doc.RootElement.GetProperty("previousFile").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("attachedTotal").GetInt32());
    }

    [Fact]
    public void Detach_IsCaseInsensitiveOnTheName()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));

        var (summary, _) = Detach(fake, new FakeHmiConfirmer(), "plc");

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("virtual PLC 'PLC'", summary);
    }

    [Fact]
    public void Detach_Rejected_WritesNothing()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));
        fake.CallLog.Clear();

        var (summary, _) = Detach(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected), "PLC");

        Assert.StartsWith("[Error]", summary);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Detach_ApiSaysNothingButEntryRemains_IsAnError()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));
        fake.HmiTmcDetachDoesNothing = true;

        var (summary, _) = Detach(fake, new FakeHmiConfirmer(), "PLC");

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("still in the project's HmiTmcFiles", summary);
    }

    [Fact]
    public void Detach_MissingName_IsRefused()
    {
        var fake = Session();
        var (summary, _) = Detach(fake, new FakeHmiConfirmer(), null);

        Assert.StartsWith("[Error] virtualPlcName is required", summary);
        Assert.Empty(fake.CallLog);
    }

    // ── the hint that closes hmi-server's B10 ───────────────────────────────

    [Fact]
    public void MapAgainstUnreachablePlc_PointsAtTheTmcRoute()
    {
        var fake = Session();
        fake.HmiMapResult = new HmiOperationResult(false, "server request");

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer())
            .MapSymbol(null, "MAIN.bStart", null, null, null, null, null, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("twincat_hmi_attach_tmc_file", summary);
        Assert.Contains("twincat_hmi_list_ads_runtimes", summary);
    }

    [Theory]
    [InlineData("TMC")]
    [InlineData("tmc")]
    public void MapIntoTheTmcDomain_IsRefusedBeforeAnythingIsWritten(string domain)
    {
        var fake = Session();

        var (summary, _) = new HmiCoordinator(fake, new FakeHmiConfirmer())
            .MapSymbol(null, null, "Probe_nCounter", "FixturePlc.MAIN.nCounter", domain, null, null, 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("INVALID_DOMAIN", summary, StringComparison.Ordinal);
        Assert.Contains("twincat_hmi_enable_runtime_tmc", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("MapHmiSymbol", StringComparison.Ordinal));
    }

    // ── tmcFile against the PathPolicy (#156 item 7) ────────────────────────

    /// <summary>A path under a fresh directory at the system drive root: not the solution
    /// directory, not %TEMP%, not %LOCALAPPDATA%\ptc-xae-mcp, not a TwinCAT install dir.</summary>
    private static string OutsideEveryRoot(out string root)
    {
        root = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "ptc-xae-mcp-tmc-outside-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(root, "PlcProj", "PLC.tmc");
    }

    /// <summary>Runs <paramref name="body"/> with PTC_XAE_MCP_ALLOWED_PATHS set to
    /// <paramref name="value"/> (null = unset) and restores it. The class shares the
    /// PathPolicy environment collection, so no other test changes the variable meanwhile.</summary>
    private static void WithAllowedPaths(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, previous);
        }
    }

    [Fact]
    public void Attach_TmcFileOutsideAllowedRoots_IsRefusedAndNeverInspected()
    {
        string outside = OutsideEveryRoot(out _);
        WithAllowedPaths(null, () =>
        {
            var fake = Session();
            var confirmer = new FakeHmiConfirmer();
            int inspected = 0;
            var coordinator = new HmiCoordinator(fake, confirmer)
            {
                TmcInspector = p => { inspected++; return TmcFile.Inspect(p); },
            };

            var (summary, json) = coordinator.AttachTmcFile(null, outside, null, 120);

            Assert.StartsWith("[Error] tmcFile: ", summary);
            Assert.Contains("outside every allowed directory", summary);
            Assert.Contains(PathPolicy.AllowedPathsEnvVar, summary);
            Assert.Equal("{}", json);
            Assert.Equal(0, inspected);
            Assert.Empty(confirmer.Requests);
            Assert.False(Wrote(fake));
        });
    }

    [Fact]
    public void Attach_RelativeTmcFile_GetsTheAbsolutePathMessage_NotAHostDirectoryVerdict()
    {
        // A relative path would be resolved against this host's working directory by the
        // PathPolicy; Inspect refuses it without touching the disk and says what is needed.
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Attach(fake, confirmer, @"PlcProj\PLC.tmc");

        Assert.StartsWith("[Error] tmcFile 'PlcProj", summary);
        Assert.Contains("must be an absolute path", summary);
        Assert.DoesNotContain("allowed directory", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(Wrote(fake));
    }

    [Fact]
    public void Attach_TmcFileUnderRootFromAllowedPathsEnvVar_IsAllowed()
    {
        string tmc = OutsideEveryRoot(out string root);
        WithAllowedPaths(root, () =>
        {
            var fake = Session();
            var confirmer = new FakeHmiConfirmer();
            var inspected = new List<string?>();
            // The root is not created on disk; the stub stands in for a real PLC .tmc there.
            var coordinator = new HmiCoordinator(fake, confirmer)
            {
                TmcInspector = p =>
                {
                    inspected.Add(p);
                    return (new TmcFileFacts(p!, 4096, 3, "TwinCAT XAE Plc"), null);
                },
            };

            var (summary, _) = coordinator.AttachTmcFile(null, tmc, null, 120);

            Assert.Equal([tmc], inspected);
            Assert.DoesNotContain("[Error]", summary);
            Assert.Contains("TMC file attached to 'HMI_Fixture' as virtual PLC 'PLC'", summary);
            Assert.Single(confirmer.Requests);
            Assert.Contains($"AttachHmiTmcFile({Project}, PLC, {tmc})", fake.CallLog);
        });
    }

    [Fact]
    public void Detach_NeverReadsTheTmcFile_OnlyTheHmiTmcFilesEntries()
    {
        // Attached under a root that is then taken away again and whose file never existed:
        // detach matches the virtual PLC name against HmiTmcFiles and must not care.
        string tmc = OutsideEveryRoot(out string root);
        var fake = Session();
        WithAllowedPaths(root, () =>
        {
            var attach = new HmiCoordinator(fake, new FakeHmiConfirmer())
            {
                TmcInspector = p => (new TmcFileFacts(p!, 4096, 3, null), null),
            };
            Assert.DoesNotContain("[Error]", attach.AttachTmcFile(null, tmc, null, 120).Summary);
        });
        Assert.False(File.Exists(tmc));

        WithAllowedPaths(null, () =>
        {
            int inspected = 0;
            var detach = new HmiCoordinator(fake, new FakeHmiConfirmer())
            {
                TmcInspector = p => { inspected++; return TmcFile.Inspect(p); },
            };

            var (summary, json) = detach.DetachTmcFile(null, "PLC", 120);

            Assert.DoesNotContain("[Error]", summary);
            Assert.Equal(0, inspected);
            Assert.Contains($"DetachHmiTmcFile({Project}, PLC)", fake.CallLog);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("detached").GetBoolean());
            Assert.Equal(tmc, doc.RootElement.GetProperty("previousFile").GetString());
        });
    }

    // ── #152 E-5: an unreadable read-back is an error, never a success ──────

    [Fact]
    public void Attach_ReadBackUnreadable_IsAnError()
    {
        var fake = Session();
        fake.HmiTmcCorruptsProjectFile = true;

        var (summary, json) = Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));

        Assert.StartsWith("[Error] AttachTmcFile was issued, but the read-back failed", summary);
        Assert.Contains("could not be read as a project file", summary);
        Assert.Contains("whether it took effect is unknown", summary);
        Assert.Equal("{}", json);
        Assert.True(Wrote(fake));
    }

    [Fact]
    public void Detach_ReadBackUnreadable_IsAnError()
    {
        var fake = Session();
        Attach(fake, new FakeHmiConfirmer(), TmcOnDisk("PLC"));
        fake.HmiTmcCorruptsProjectFile = true;

        var (summary, json) = Detach(fake, new FakeHmiConfirmer(), "PLC");

        Assert.StartsWith("[Error] DetachTmcFile was issued, but the read-back failed", summary);
        Assert.Contains("could not be read as a project file", summary);
        Assert.Equal("{}", json);
        Assert.Contains($"DetachHmiTmcFile({Project}, PLC)", fake.CallLog);
    }
}
