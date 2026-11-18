using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// #161 — server-side TMC. Invariants every
/// test guards: nothing is converted for a refused runtime or file; nothing is written (neither the
/// .bin nor the server config) unless the engineer confirmed; the two config writes go through one
/// raw session call; and success is reported only when ADS.Config::RUNTIMES reads the new state back.
/// The fake writes the two leaves into the same runtime JSON the coordinator reads, so the read-back
/// runs through the real parser.
/// </summary>
[Collection(PathPolicyEnvironment.Name)]
public class HmiRuntimeTmcTests : IDisposable
{
    private const string Project = "HMI_Fixture";
    private static readonly byte[] Converted = [3, 0, 64, 0, 1, 2, 3, 4];

    private readonly string _root;
    private readonly string _solutionDir;
    private readonly string _projectDir;
    private readonly string _serverFolder;

    public HmiRuntimeTmcTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ptc-rttmc-{Guid.NewGuid():N}");
        _solutionDir = Path.Combine(_root, "sln");
        _projectDir = Path.Combine(_solutionDir, Project);
        _serverFolder = Path.Combine(_solutionDir, ".engineering_servers", Project);
        Directory.CreateDirectory(_projectDir);
        Directory.CreateDirectory(_serverFolder);
        WriteProjectFile(workingDirectory: Project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The .hmiproj with the Server folder metadata TE2000 writes on the first server start.</summary>
    private void WriteProjectFile(string? workingDirectory) =>
        File.WriteAllText(Path.Combine(_projectDir, Project + ".hmiproj"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Folder Include="Server">{(workingDirectory is null ? "" : $"<WorkingDirectory>{workingDirectory}</WorkingDirectory>")}</Folder>
                <Folder Include="Properties" />
              </ItemGroup>
            </Project>
            """);

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
        // The shape a started server reports: the TMC object with its schema defaults.
        fake.SetAdsRuntimes(runtimes ?? FakeTwinCatSession.AdsRuntimeJson(tmcFile: ""));
        return fake;
    }

    private string TmcOnDisk(string name = "PLC", int symbols = 2)
    {
        string dir = Path.Combine(_solutionDir, "PlcProj");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + TmcFile.Extension);
        string body = string.Concat(Enumerable.Repeat(
            "<Symbol><Name>MAIN.bTest</Name><BaseType>BOOL</BaseType></Symbol>", symbols));
        File.WriteAllText(path,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{TmcFile.RootElement} GeneratedBy=\"TwinCAT XAE Plc\">{body}</TcModuleClass>");
        return path;
    }

    private string BinPath(string runtime = "PLC1", string tmc = "PLC") =>
        Path.Combine(_serverFolder, "Tmc", $"{runtime}_{tmc}.DATA_UPLOADINFO2.bin");

    private static string Relative(string runtime = "PLC1", string tmc = "PLC") =>
        $@"Tmc\{runtime}_{tmc}.DATA_UPLOADINFO2.bin";

    /// <summary>A coordinator whose conversion is a stub that records its calls.</summary>
    private static HmiCoordinator Coordinator(FakeTwinCatSession fake, IHmiWriteConfirmer? confirmer,
        List<string>? converted = null, string? convertError = null) =>
        new(fake, confirmer)
        {
            UploadInfoConverter = (path, name) =>
            {
                converted?.Add($"{path}|{name}");
                return convertError is null
                    ? (new TmcUploadInfo(Converted, name, @"C:\TwinCAT\TcTypeSystem.dll", "2.8.116.0", 8), null)
                    : (null, convertError);
            },
        };

    private static bool WroteConfig(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("SetHmiRuntimeTmc", StringComparison.Ordinal));

    private static HmiAdsRuntime Runtime(FakeTwinCatSession fake, string name = "PLC1") =>
        HmiCoordinator.TryParseAdsRuntimes(fake.HmiSymbolValues[HmiCoordinator.AdsRuntimesSymbol])!
            .Single(r => r.Name == name);

    // ── pure rules ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("USE_TMC", "ADS.Config::RUNTIMES::PLC1::TMC::USE_TMC")]
    [InlineData("DATA_SYMBOLUPANDDOWNLOAD", "ADS.Config::RUNTIMES::PLC1::TMC::DATA_SYMBOLUPANDDOWNLOAD")]
    public void ConfigSymbol_IsExactlyWhatTe2000Writes(string leaf, string expected) =>
        Assert.Equal(expected, HmiRuntimeTmc.ConfigSymbol("PLC1", leaf));

    [Fact]
    public void UploadInfoFileName_IsRuntimeAndTmcName() =>
        Assert.Equal("PLC1_FixturePlc.DATA_UPLOADINFO2.bin",
            HmiRuntimeTmc.UploadInfoFileName("PLC1", @"C:\x\FixturePlc\FixturePlc.tmc"));

    [Theory]
    [InlineData("a<b>c", "a_b_c")]
    [InlineData("  padded  ", "padded")]
    [InlineData("", "")]
    public void MakeFileSystemSafe_MirrorsTe2000(string input, string expected) =>
        Assert.Equal(expected, HmiRuntimeTmc.MakeFileSystemSafe(input));

    [Fact]
    public void MakeFileSystemSafe_CapsAt80Characters() =>
        Assert.Equal(80, HmiRuntimeTmc.MakeFileSystemSafe(new string('x', 200)).Length);

    [Fact]
    public void RelativeToServerFolder_BelowIsBackslashRelative() =>
        Assert.Equal(@"Tmc\PLC1_PLC.DATA_UPLOADINFO2.bin",
            HmiRuntimeTmc.RelativeToServerFolder(@"C:\s\.engineering_servers\Hmi",
                @"C:\s\.engineering_servers\Hmi\Tmc\PLC1_PLC.DATA_UPLOADINFO2.bin"));

    [Theory]
    [InlineData(@"C:\s\.engineering_servers\Other\x.bin")]
    [InlineData(@"D:\x.bin")]
    [InlineData(@"C:\s\.engineering_servers\Hmi")]
    public void RelativeToServerFolder_OutsideIsNull(string file) =>
        Assert.Null(HmiRuntimeTmc.RelativeToServerFolder(@"C:\s\.engineering_servers\Hmi", file));

    [Theory]
    [InlineData(@"Tmc\a.bin", "Tmc/a.bin", true)]
    [InlineData(@"TMC\A.BIN", @"Tmc\a.bin", true)]
    [InlineData(null, "", true)]
    [InlineData(@"Tmc\a.bin", @"Tmc\b.bin", false)]
    [InlineData(null, @"Tmc\a.bin", false)]
    public void SamePath_IgnoresSeparatorsCaseAndEmpty(string? left, string? right, bool same) =>
        Assert.Equal(same, HmiRuntimeTmc.SamePath(left, right));

    [Fact]
    public void ServerFolderCandidates_FollowTe2000sOrder()
    {
        var candidates = HmiRuntimeTmc.ServerFolderCandidates(
            @"C:\repo\Hmi\HMI_Fixture", "HMI_Fixture", "Custom", @"C:\repo");

        Assert.Equal(
        [
            @"C:\repo\Hmi\.engineering_servers\Custom",
            @"C:\repo\Hmi\.engineering_servers\HMI_Fixture",
            @"C:\repo\.engineering_servers\Custom",
            @"C:\repo\.engineering_servers\HMI_Fixture",
        ], candidates);
    }

    [Fact]
    public void ServerFolderCandidates_AreDistinct_WhenTheProjectSitsInTheSolutionDirectory()
    {
        var candidates = HmiRuntimeTmc.ServerFolderCandidates(
            @"C:\repo\HMI_Fixture\", "HMI_Fixture", "HMI_Fixture", @"C:\repo");

        Assert.Equal([@"C:\repo\.engineering_servers\HMI_Fixture"], candidates);
    }

    [Theory]
    [InlineData(@"..\escape")]
    [InlineData("..")]
    [InlineData(@"a\b")]
    public void ServerFolderCandidates_IgnoreAWorkingDirectoryThatIsNotAPlainName(string workingDirectory)
    {
        var candidates = HmiRuntimeTmc.ServerFolderCandidates(
            @"C:\repo\HMI_Fixture", "HMI_Fixture", workingDirectory, null);

        Assert.Equal([@"C:\repo\.engineering_servers\HMI_Fixture"], candidates);
    }

    [Fact]
    public void ReadServerWorkingDirectory_ReadsTheRealShape()
    {
        WriteProjectFile("Custom");
        Assert.Equal("Custom",
            HmiRuntimeTmc.ReadServerWorkingDirectory(Path.Combine(_projectDir, Project + ".hmiproj")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"..\evil")]
    public void ReadServerWorkingDirectory_MissingOrUnsafe_IsNull(string? workingDirectory)
    {
        WriteProjectFile(workingDirectory);
        Assert.Null(HmiRuntimeTmc.ReadServerWorkingDirectory(Path.Combine(_projectDir, Project + ".hmiproj")));
    }

    [Fact]
    public void ReadServerWorkingDirectory_Garbage_IsNull()
    {
        string file = Path.Combine(_root, "broken.hmiproj");
        File.WriteAllText(file, "<Project><Folder");
        Assert.Null(HmiRuntimeTmc.ReadServerWorkingDirectory(file));
        Assert.Null(HmiRuntimeTmc.ReadServerWorkingDirectory(Path.Combine(_root, "missing.hmiproj")));
    }

    [Fact]
    public void BaseTypeFiles_NewestBuildWithASystemManager_TcBaseTypesFirst()
    {
        string components = Path.Combine(_root, "Components", "Base");
        // Newest by version, but without a system manager library — TmcParser skips it.
        Directory.CreateDirectory(Path.Combine(components, "Build_4030.1", "Config", "Io"));
        string older = Path.Combine(components, "Build_4024.62");
        string newer = Path.Combine(components, "Build_4026.22");
        foreach (string build in new[] { older, newer })
        {
            Directory.CreateDirectory(Path.Combine(build, "Config", "Io"));
            Directory.CreateDirectory(Path.Combine(build, "Config", "Modules"));
            File.WriteAllText(Path.Combine(build, "TcSysManCmd.x64.dll"), "");
            File.WriteAllText(Path.Combine(build, "Config", "Io", "ATypes.tmc"), "");
            File.WriteAllText(Path.Combine(build, "Config", "Io", "TcBaseTypes.tmc"), "");
            File.WriteAllText(Path.Combine(build, "Config", "Modules", "TcIotBase.tmc"), "");
            File.WriteAllText(Path.Combine(build, "Config", "Io", "readme.txt"), "");
        }

        var (files, error) = HmiRuntimeTmc.BaseTypeFiles(components, twinCat3Dir: null);

        Assert.Null(error);
        Assert.Equal(
        [
            Path.Combine(newer, "Config", "Io", "TcBaseTypes.tmc"),
            Path.Combine(newer, "Config", "Io", "ATypes.tmc"),
            Path.Combine(newer, "Config", "Modules", "TcIotBase.tmc"),
        ], files);
    }

    [Fact]
    public void BaseTypeFiles_WithoutABuild_FallsBackToTwinCat3Dir()
    {
        string twinCat3Dir = Path.Combine(_root, "TwinCAT", "3.1");
        string baseTypes = Path.Combine(twinCat3Dir, "Config", "Io", "TcBaseTypes.tmc");
        Directory.CreateDirectory(Path.GetDirectoryName(baseTypes)!);
        File.WriteAllText(baseTypes, "");

        var (files, error) = HmiRuntimeTmc.BaseTypeFiles(Path.Combine(_root, "no-components"), twinCat3Dir);

        Assert.Null(error);
        Assert.Equal([baseTypes], files);
    }

    [Fact]
    public void BaseTypeFiles_NothingThere_IsAnErrorNotAnEmptyList()
    {
        var (files, error) = HmiRuntimeTmc.BaseTypeFiles(Path.Combine(_root, "no-components"), Path.Combine(_root, "nope"));

        Assert.Null(files);
        Assert.Contains("TcBaseTypes.tmc", error);
    }

    [Fact]
    public void NativeLibraryCandidates_PreferTe2000sOwnCopy()
    {
        var candidates = HmiRuntimeTmc.NativeLibraryCandidates(@"C:\Program Files (x86)");

        Assert.Equal(
        [
            @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\bin\TmcParser\win-x64\native\TcTypeSystem.dll",
            @"C:\Program Files (x86)\Beckhoff\TwinCAT\CommonComServer\TcTypeSystem\2.8\x64\TcTypeSystem.dll",
        ], candidates);
    }

    [Fact]
    public void WriteAtomically_ReplacesAndLeavesNoTempFile()
    {
        string file = Path.Combine(_root, "deep", "Tmc", "x.bin");
        HmiRuntimeTmc.WriteAtomically(file, [1, 2, 3]);
        HmiRuntimeTmc.WriteAtomically(file, [9]);

        Assert.Equal([9], File.ReadAllBytes(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    // ── enable: refused before anything is converted or written ─────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PLC 1")]
    [InlineData("PLC1::TMC")]
    [InlineData("ADS.PLC1")]
    public void Enable_InvalidRuntime_IsRefusedBeforeAnything(string? runtime)
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();
        var converted = new List<string>();

        var (summary, json) = Coordinator(fake, confirmer, converted).EnableRuntimeTmc(null, runtime, TmcOnDisk(), 120);

        Assert.StartsWith("[Error] runtime is required", summary);
        Assert.Equal("{}", json);
        Assert.Empty(converted);
        Assert.Empty(confirmer.Requests);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Enable_TmcWithoutSymbols_NamesTheEventClassTrap()
    {
        var fake = Session();
        var converted = new List<string>();

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer(), converted)
            .EnableRuntimeTmc(null, "PLC1", TmcOnDisk("HMI_Error", symbols: 0), 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("HMI_Error.tmc", summary);
        Assert.Empty(converted);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_RelativeTmcFile_IsRefused()
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", @"PlcProj\PLC.tmc", 120);

        Assert.StartsWith("[Error] tmcFile 'PlcProj", summary);
        Assert.Contains("must be an absolute path", summary);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_TmcOutsideAllowedRoots_IsNeverInspectedNorConverted()
    {
        string outsideRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "ptc-xae-mcp-rttmc-outside-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(outsideRoot, "PlcProj", "PLC.tmc");
        string? previous = Environment.GetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, null);
            var fake = Session();
            var converted = new List<string>();
            int inspected = 0;
            var coordinator = new HmiCoordinator(fake, new FakeHmiConfirmer())
            {
                TmcInspector = p => { inspected++; return TmcFile.Inspect(p); },
                UploadInfoConverter = (p, n) => { converted.Add(p); return (null, "unreachable"); },
            };

            var (summary, _) = coordinator.EnableRuntimeTmc(null, "PLC1", outside, 120);

            Assert.StartsWith("[Error] tmcFile: ", summary);
            Assert.Contains(PathPolicy.AllowedPathsEnvVar, summary);
            Assert.Equal(0, inspected);
            Assert.Empty(converted);
            Assert.False(WroteConfig(fake));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, previous);
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    public void Enable_TimeoutOutOfRange_IsRefused(int seconds)
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), seconds);

        Assert.StartsWith("[Error] confirmTimeoutSeconds", summary);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_UnknownRuntime_ListsTheConfiguredOnes_AndNeverCreatesOne()
    {
        var fake = Session();
        var converted = new List<string>();

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer(), converted).EnableRuntimeTmc(null, "PLC2", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] The ADS extension of 'HMI_Fixture' has no runtime 'PLC2'", summary);
        Assert.Contains("configured: PLC1", summary);
        Assert.Contains("does not create one", summary);
        Assert.Empty(converted);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_RuntimeNameInOtherCase_IsRefusedWithAHint()
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "plc1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("did you mean 'PLC1'", summary);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_UnreadableRuntimeTable_IsAnError()
    {
        var fake = Session();
        fake.HmiSymbolValues.Remove(HmiCoordinator.AdsRuntimesSymbol);
        fake.HmiSymbolErrors[HmiCoordinator.AdsRuntimesSymbol] = "server request: SYMBOL_NOT_MAPPED";

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] Cannot read 'ADS.Config::RUNTIMES'", summary);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_NoEngineeringServerFolder_IsRefused_AndSaysHowToGetOne()
    {
        Directory.Delete(_serverFolder);
        var fake = Session();
        var converted = new List<string>();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Coordinator(fake, confirmer, converted).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] The engineering server folder of 'HMI_Fixture' was not found", summary);
        Assert.Contains(_serverFolder, summary);
        Assert.Contains("twincat_hmi_server_control", summary);
        Assert.Empty(converted);
        Assert.Empty(confirmer.Requests);
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_ConversionFails_IsRefusedBeforeThePopup()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, _) = Coordinator(fake, confirmer, convertError: "Module info collection is empty.")
            .EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("could not be converted", summary);
        Assert.Contains("Module info collection is empty.", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(File.Exists(BinPath()));
        Assert.False(WroteConfig(fake));
    }

    // ── enable: nothing without a confirmed engineer ─────────────────────────

    [Theory]
    [InlineData(nameof(HmiConfirmOutcome.Rejected), "rejected")]
    [InlineData(nameof(HmiConfirmOutcome.TimedOut), "Not confirmed within")]
    public void Enable_NotConfirmed_WritesNeitherTheFileNorTheConfig(string outcome, string expected)
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer(Enum.Parse<HmiConfirmOutcome>(outcome)))
            .EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains(expected, summary);
        Assert.False(File.Exists(BinPath()));
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_NoConfirmer_WritesNothing()
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, confirmer: null).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.Contains("No confirmation channel", summary);
        Assert.False(File.Exists(BinPath()));
        Assert.False(WroteConfig(fake));
    }

    [Fact]
    public void Enable_PopupShowsRuntimeFileSizeAndThatNoPlcIsTouched()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer(HmiConfirmOutcome.Rejected);
        string tmc = TmcOnDisk(symbols: 3);

        Coordinator(fake, confirmer).EnableRuntimeTmc(null, "PLC1", tmc, 45);

        var (request, timeout) = Assert.Single(confirmer.Requests);
        Assert.Equal(TimeSpan.FromSeconds(45), timeout);
        Assert.Equal(HmiWriteKind.EnableRuntimeTmc, request.Kind);
        Assert.Equal("PLC1", request.Symbol);
        Assert.Equal("ADS", request.Domain);
        Assert.False(request.WritesToPlc);
        Assert.Contains(Relative(), request.NewValue);
        Assert.Contains(tmc, request.NewValue);
        Assert.Contains("3 symbols", request.NewValue);
        Assert.Contains($"{Converted.Length} bytes", request.NewValue);
        Assert.Contains("USE_TMC=false — symbols from the PLC over ADS 127.0.0.1.1.1:851", request.CurrentValue);
        Assert.Contains("ADS runtime : PLC1", HmiConfirmText.Details(request));
        Assert.Contains("TMC file", HmiConfirmText.Headline(request));
    }

    // ── enable: write, then decide by the read-back ─────────────────────────

    [Fact]
    public void Enable_Confirmed_WritesTheFileThenTheConfigThenReadsBack()
    {
        var fake = Session();
        var converted = new List<string>();
        string tmc = TmcOnDisk();

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer(), converted).EnableRuntimeTmc(null, "PLC1", tmc, 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("ADS runtime 'PLC1' of 'HMI_Fixture' now takes its PLC symbols from", summary);
        Assert.Contains("verified in ADS.Config::RUNTIMES", summary);
        Assert.Contains("twincat_hmi_disable_runtime_tmc", summary);
        Assert.Equal([$"{tmc}|PLC"], converted);
        Assert.Equal(Converted, File.ReadAllBytes(BinPath()));
        Assert.Contains($@"SetHmiRuntimeTmc({Project}, PLC1, {Relative()}, useTmc=True)", fake.CallLog);
        HmiAdsRuntime after = Runtime(fake);
        Assert.True(after.UseTmc);
        Assert.Equal(Relative(), after.TmcFile);

        using var doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        Assert.Equal("PLC1", r.GetProperty("runtime").GetString());
        Assert.True(r.GetProperty("useTmc").GetBoolean());
        Assert.Equal(Relative(), r.GetProperty("dataSymbolUpAndDownload").GetString());
        Assert.Equal(BinPath(), r.GetProperty("binFile").GetString());
        Assert.Equal(Converted.Length, r.GetProperty("binSizeBytes").GetInt32());
        Assert.Equal(_serverFolder, r.GetProperty("engineeringServerFolder").GetString());
        Assert.Equal(2, r.GetProperty("tmcSymbolCount").GetInt32());
        Assert.False(r.GetProperty("previous").GetProperty("useTmc").GetBoolean());
        Assert.Equal("confirmedByEngineer", r.GetProperty("confirmation").GetString());
    }

    [Fact]
    public void Enable_RuntimeWithoutATmcObject_WritesTheObjectWhole()
    {
        // Live 2026-11-18: a runtime that never had a TMC carries no TMC key, and the server refuses a
        // write to a leaf below it (INVALID_SUBSYMBOL) — the tool must write the object as a whole.
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson());
        Assert.False(Runtime(fake).HasTmcConfig);

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains($@"SetHmiRuntimeTmc({Project}, PLC1, {Relative()}, useTmc=True, createTmcObject)", fake.CallLog);
        HmiAdsRuntime after = Runtime(fake);
        Assert.True(after.HasTmcConfig);
        Assert.True(after.UseTmc);
        Assert.Equal(Relative(), after.TmcFile);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("createdTmcObject").GetBoolean());
    }

    [Fact]
    public void Enable_RuntimeWithATmcObject_WritesTheLeaves()
    {
        var fake = Session();
        Assert.True(Runtime(fake).HasTmcConfig);

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.DoesNotContain(fake.CallLog, c => c.Contains("createTmcObject", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("createdTmcObject").GetBoolean());
    }

    [Fact]
    public void FakeServer_RefusesALeafWriteWithoutTmcObject_LikeTheLiveServer()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson());

        HmiOperationResult result = fake.SetHmiRuntimeTmc(Project, "PLC1", Relative(), useTmc: true);

        Assert.False(result.Ok);
        Assert.Contains("INVALID_SUBSYMBOL", result.LastError);
        Assert.False(Runtime(fake).HasTmcConfig);
    }

    [Fact]
    public void TmcObjectJson_HoldsBothLeaves()
    {
        Assert.Equal("ADS.Config::RUNTIMES::PLC1::TMC", HmiRuntimeTmc.TmcObjectSymbol("PLC1"));
        using var doc = JsonDocument.Parse(HmiRuntimeTmc.TmcObjectJson(@"Tmc\PLC1_PLC.DATA_UPLOADINFO2.bin", true));
        Assert.Equal(@"Tmc\PLC1_PLC.DATA_UPLOADINFO2.bin", doc.RootElement.GetProperty("DATA_SYMBOLUPANDDOWNLOAD").GetString());
        Assert.True(doc.RootElement.GetProperty("USE_TMC").GetBoolean());
    }

    [Fact]
    public void Enable_WorkingDirectoryMetadata_DecidesTheFolder()
    {
        WriteProjectFile("Custom");
        string custom = Path.Combine(_solutionDir, ".engineering_servers", "Custom");
        Directory.CreateDirectory(custom);
        var fake = Session();

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.True(File.Exists(Path.Combine(custom, "Tmc", "PLC1_PLC.DATA_UPLOADINFO2.bin")));
        Assert.False(File.Exists(BinPath()));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(custom, doc.RootElement.GetProperty("engineeringServerFolder").GetString());
    }

    [Fact]
    public void Enable_AgainOnTheSameRuntime_RegeneratesTheFile()
    {
        var fake = Session();
        string tmc = TmcOnDisk();
        Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", tmc, 120);
        File.WriteAllBytes(BinPath(), [0]);

        var confirmer = new FakeHmiConfirmer();
        var (summary, json) = Coordinator(fake, confirmer).EnableRuntimeTmc(null, "PLC1", tmc, 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Equal(Converted, File.ReadAllBytes(BinPath()));
        Assert.Contains($"USE_TMC=true ({Relative()})", Assert.Single(confirmer.Requests).Request.CurrentValue);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("previous").GetProperty("useTmc").GetBoolean());
    }

    [Fact]
    public void Enable_ServerSaysOkButNothingChanged_IsAnError()
    {
        var fake = Session();
        fake.HmiRuntimeTmcDoesNothing = true;

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] The server accepted the writes, but ADS.Config::RUNTIMES reads back USE_TMC=false", summary);
        Assert.Contains("Not treated as success", summary);
        Assert.Equal("{}", json);
        Assert.True(WroteConfig(fake));
    }

    [Fact]
    public void Enable_FlagRefusedAfterThePath_SaysWhatTheServerHoldsNow()
    {
        var fake = Session();
        fake.HmiRuntimeTmcRefusesFlag = true;

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] The HMI server refused the TMC setting of runtime 'PLC1'", summary);
        Assert.Contains("USE_TMC", summary);
        Assert.Contains($"The server now holds: USE_TMC=false", summary);
        Assert.Contains($"a file is still configured: {Relative()}", summary);
    }

    [Fact]
    public void Enable_SessionThrows_IsAnError()
    {
        var fake = Session();
        fake.SetHmiRuntimeTmcThrows = new InvalidOperationException("HMI project 'HMI_Fixture' returned no server interface.");

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] Writing ADS.Config::RUNTIMES::PLC1::TMC failed", summary);
        Assert.Contains("no server interface", summary);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Enable_ReadBackUnreadable_IsAnErrorNotASuccess()
    {
        var fake = Session();
        fake.HmiRuntimeTmcBreaksReadBack = true;

        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer()).EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.StartsWith("[Error] The TMC setting of runtime 'PLC1' was written, but the read-back failed", summary);
        Assert.Contains("whether it took effect is unknown", summary);
        Assert.DoesNotContain("([Error]", summary);
        Assert.Equal("{}", json);
    }

    [Fact]
    public void Enable_TempFixtureAutoConfirm_IsReportedAsSuch()
    {
        var fake = Session();
        var (summary, json) = Coordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.AutoConfirmed))
            .EnableRuntimeTmc(null, "PLC1", TmcOnDisk(), 120);

        Assert.Contains("auto-confirmed", summary);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("autoConfirmedTempFixture", doc.RootElement.GetProperty("confirmation").GetString());
    }

    // ── disable ─────────────────────────────────────────────────────────────

    [Fact]
    public void Disable_RuntimeWithoutTmc_IsNothingToDoWithoutPopup()
    {
        var fake = Session();
        var confirmer = new FakeHmiConfirmer();

        var (summary, json) = Coordinator(fake, confirmer).DisableRuntimeTmc(null, "PLC1", 120);

        Assert.StartsWith("Nothing to do", summary);
        Assert.Empty(confirmer.Requests);
        Assert.False(WroteConfig(fake));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public void Disable_Confirmed_ClearsBothLeavesThenReadsBack()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson(useTmc: true, tmcFile: Relative()));
        var confirmer = new FakeHmiConfirmer();

        var (summary, json) = Coordinator(fake, confirmer).DisableRuntimeTmc(null, "PLC1", 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Contains("no longer uses a TMC file", summary);
        Assert.Contains("ADS 127.0.0.1.1.1:851", summary);
        Assert.Contains("left on disk", summary);
        Assert.Contains($"SetHmiRuntimeTmc({Project}, PLC1, , useTmc=False)", fake.CallLog);
        HmiAdsRuntime after = Runtime(fake);
        Assert.False(after.UseTmc);
        Assert.Null(after.TmcFile);

        var (request, _) = Assert.Single(confirmer.Requests);
        Assert.Equal(HmiWriteKind.DisableRuntimeTmc, request.Kind);
        Assert.Contains($"USE_TMC=true ({Relative()})", request.CurrentValue);
        Assert.Contains("ADS runtime : PLC1", HmiConfirmText.Details(request));

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal(Relative(), doc.RootElement.GetProperty("previousFile").GetString());
    }

    [Fact]
    public void Disable_FlagOffButPathLeft_ClearsThePathToo()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson(useTmc: false, tmcFile: Relative()));

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).DisableRuntimeTmc(null, "PLC1", 120);

        Assert.DoesNotContain("[Error]", summary);
        Assert.Null(Runtime(fake).TmcFile);
    }

    [Fact]
    public void Disable_Rejected_WritesNothing()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson(useTmc: true, tmcFile: Relative()));

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer(HmiConfirmOutcome.Rejected)).DisableRuntimeTmc(null, "PLC1", 120);

        Assert.StartsWith("[Error]", summary);
        Assert.False(WroteConfig(fake));
        Assert.True(Runtime(fake).UseTmc);
    }

    [Fact]
    public void Disable_ServerSaysOkButStillOn_IsAnError()
    {
        var fake = Session(FakeTwinCatSession.AdsRuntimeJson(useTmc: true, tmcFile: Relative()));
        fake.HmiRuntimeTmcDoesNothing = true;

        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).DisableRuntimeTmc(null, "PLC1", 120);

        Assert.StartsWith("[Error] The server accepted the writes, but ADS.Config::RUNTIMES reads back USE_TMC=true", summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PLC 1")]
    public void Disable_InvalidRuntime_IsRefused(string? runtime)
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).DisableRuntimeTmc(null, runtime, 120);

        Assert.StartsWith("[Error] runtime is required", summary);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Disable_UnknownRuntime_IsRefused()
    {
        var fake = Session();
        var (summary, _) = Coordinator(fake, new FakeHmiConfirmer()).DisableRuntimeTmc(null, "PLC9", 120);

        Assert.StartsWith("[Error] The ADS extension of 'HMI_Fixture' has no runtime 'PLC9'", summary);
        Assert.False(WroteConfig(fake));
    }

    // ── the hint that closes hmi-server's B10 now names the server-side route ─

    [Fact]
    public void TmcHint_PointsAtTheServerSideRoute() =>
        Assert.Contains("twincat_hmi_enable_runtime_tmc", HmiCoordinator.TmcHint);

    // ── the real conversion (only where TwinCAT's type system is installed) ─

    [TcTypeSystemFact]
    public void Convert_NeutralFixtureTmc_YieldsOneModuleWithThePlcSymbols()
    {
        string tmc = FixtureTmc();

        var (info, error) = TmcUploadInfoExporter.Convert(tmc, "FixturePlc");

        Assert.Null(error);
        Assert.NotNull(info);
        Assert.Equal("FixturePlc", info.ModuleName);
        Assert.True(info.BaseTypeFileCount > 0);
        Assert.Contains(info.NativeLibrary, HmiRuntimeTmc.NativeLibraryCandidates(ProgramFilesX86()));
        // Only names are asserted — the segment's layout is TwinCAT's, never interpreted here.
        string text = Encoding.ASCII.GetString(info.Payload);
        Assert.Contains("GVL_Fixture.bEnable", text, StringComparison.Ordinal);
        Assert.Contains("MAIN.bInput", text, StringComparison.Ordinal);

        var (again, _) = TmcUploadInfoExporter.Convert(tmc, "FixturePlc");
        Assert.Equal(info.Payload, again!.Payload);
    }

    [TcTypeSystemFact]
    public void Convert_NotATmc_IsAnErrorNotACrash()
    {
        string file = Path.Combine(_root, "garbage.tmc");
        File.WriteAllText(file, "this is not xml");

        var (info, error) = TmcUploadInfoExporter.Convert(file, "garbage");

        Assert.Null(info);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    private static string ProgramFilesX86() =>
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string FixtureTmc([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "tests", "Fixtures",
            "PtcXaeMcpFixture", "PtcXaeMcpFixture", "FixturePlc", "FixturePlc.tmc"));
}

/// <summary>Runs only where TwinCAT's native type system is installed (the dev VM); reported as
/// skipped on a machine without it (hosted CI), instead of passing vacuously.</summary>
public sealed class TcTypeSystemFactAttribute : FactAttribute
{
    public TcTypeSystemFactAttribute()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!HmiRuntimeTmc.NativeLibraryCandidates(programFilesX86).Any(File.Exists))
            Skip = "TwinCAT's native type system (TcTypeSystem.dll) is not installed.";
    }
}
