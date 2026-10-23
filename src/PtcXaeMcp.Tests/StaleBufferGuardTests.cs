using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A PLC file changed on disk outside the XAE: the XAE would build its old in-memory state and the build's
/// File.SaveAll could write that state over the change, so the build is refused before it saves.
/// </summary>
public sealed class StaleBufferGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptc-stale-" + Guid.NewGuid().ToString("N"));
    private readonly string _sln;
    private readonly string _pou;
    private DateTime _clock = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, DateTime> _mtimes = new(StringComparer.OrdinalIgnoreCase);

    private const string PouXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcPlcObject Version="1.1.0.1">
          <POU Name="MAIN" Id="{1}" SpecialFunc="None">
            <Declaration><![CDATA[PROGRAM MAIN
        VAR
        END_VAR]]></Declaration>
            <Implementation>
              <ST><![CDATA[x := 1;]]></ST>
            </Implementation>
            <Folder Name="Members" Id="{2}" />
            <Method Name="Reset" Id="{3}" FolderPath="Members\">
              <Declaration><![CDATA[METHOD Reset : BOOL]]></Declaration>
              <Implementation>
                <ST><![CDATA[Reset := TRUE;]]></ST>
              </Implementation>
            </Method>
            <Property Name="Enabled" Id="{4}">
              <Declaration><![CDATA[PROPERTY Enabled : BOOL]]></Declaration>
              <Get Name="Get" Id="{5}">
                <Declaration><![CDATA[VAR
        END_VAR]]></Declaration>
                <Implementation>
                  <ST><![CDATA[Enabled := TRUE;]]></ST>
                </Implementation>
              </Get>
            </Property>
          </POU>
        </TcPlcObject>
        """;

    private static List<PlcTextSection> XaeSections(string mainBody = "x := 1;") =>
    [
        new("decl", "PROGRAM MAIN\r\nVAR\r\nEND_VAR"),
        new("impl", mainBody),
        new("Reset/decl", "METHOD Reset : BOOL"),
        new("Reset/impl", "Reset := TRUE;"),
        new("Enabled/decl", "PROPERTY Enabled : BOOL"),
        new("Enabled/Get/decl", "VAR\r\nEND_VAR"),
        new("Enabled/Get/impl", "Enabled := TRUE;  "),
    ];

    public StaleBufferGuardTests()
    {
        string tsprojDir = Path.Combine(_root, "Proj");
        string plcDir = Path.Combine(tsprojDir, "MyPlc");
        Directory.CreateDirectory(Path.Combine(plcDir, "POUs"));
        _sln = Path.Combine(_root, "Proj.sln");
        File.WriteAllText(_sln, "Project(\"{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}\") = \"Proj\", \"Proj\\Proj.tsproj\", \"{X}\"\r\nEndProject\r\n");
        File.WriteAllText(Path.Combine(tsprojDir, "Proj.tsproj"),
            "<TcSmProject><Project><Plc><Project Name=\"MyPlc\" PrjFilePath=\"MyPlc\\MyPlc.plcproj\" /></Plc></Project></TcSmProject>");
        File.WriteAllText(Path.Combine(plcDir, "MyPlc.plcproj"),
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><ItemGroup>" +
            "<Compile Include=\"POUs\\MAIN.TcPOU\"><SubType>Code</SubType></Compile>" +
            "<Compile Include=\"PlcTask.TcTTO\" /></ItemGroup></Project>");
        _pou = Path.Combine(plcDir, "POUs", "MAIN.TcPOU");
        File.WriteAllText(_pou, PouXml);
        File.WriteAllText(Path.Combine(plcDir, "PlcTask.TcTTO"), "<TcPlcObject />");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private StaleBufferGuard Guard() => new() { LastWriteUtc = f => _mtimes.TryGetValue(f, out var t) ? t : _clock };

    private void Touch(string file, string? content = null)
    {
        if (content is not null) File.WriteAllText(file, content);
        _clock = _clock.AddMinutes(1);
        _mtimes[file] = _clock;
    }

    private FakeTwinCatSession Session(List<PlcTextSection>? xae = null)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ActiveSlnPath = _sln };
        fake.PlcObjectSections[@"MyPlc|POUs\MAIN.TcPOU"] = xae ?? XaeSections();
        return fake;
    }

    // ── file side ──────────────────────────────────────────────────────────────

    [Fact]
    public void FindPlcProjects_ReadsPrjFilePathFromTheTsproj()
    {
        var projects = PlcSourceFiles.FindPlcProjects(_sln);

        var p = Assert.Single(projects);
        Assert.Equal("MyPlc", p.PlcName);
        Assert.EndsWith(@"Proj\MyPlc\MyPlc.plcproj", p.PlcprojPath);
    }

    [Fact]
    public void FindPlcProjects_FollowsAnIndependentProjectFile()
    {
        string tsprojDir = Path.Combine(_root, "Proj");
        File.WriteAllText(Path.Combine(tsprojDir, "Proj.tsproj"),
            "<TcSmProject><Project><Plc><Project File=\"MyPlc.xti\" /></Plc></Project></TcSmProject>");
        Directory.CreateDirectory(Path.Combine(tsprojDir, "_Config", "PLC"));
        File.WriteAllText(Path.Combine(tsprojDir, "_Config", "PLC", "MyPlc.xti"),
            "<TcSmItem><Project Name=\"MyPlc\" PrjFilePath=\"..\\..\\MyPlc\\MyPlc.plcproj\" /></TcSmItem>");

        var p = Assert.Single(PlcSourceFiles.FindPlcProjects(_sln));

        Assert.Equal("MyPlc", p.PlcName);
    }

    [Fact]
    public void SourceFiles_OnlyComparableKinds()
    {
        var files = PlcSourceFiles.SourceFiles(PlcSourceFiles.FindPlcProjects(_sln)[0].PlcprojPath);

        Assert.Equal([_pou], files);
    }

    [Fact]
    public void ParseSections_KeysMembersByNameChain_IgnoringFolders()
    {
        var sections = PlcSourceFiles.ParseSections(PouXml);

        Assert.Equal(
            ["decl", "impl", "Reset/decl", "Reset/impl", "Enabled/decl", "Enabled/Get/decl", "Enabled/Get/impl"],
            sections.Select(s => s.Key));
    }

    [Fact]
    public void ParseSections_GraphicalBody_IsNotCompared()
    {
        var sections = PlcSourceFiles.ParseSections(
            "<TcPlcObject><POU Name=\"P\"><Declaration><![CDATA[PROGRAM P]]></Declaration>" +
            "<Implementation><FBD><network /></FBD></Implementation></POU></TcPlcObject>");

        Assert.Null(sections.Single(s => s.Key == "impl").Text);
        Assert.Empty(PlcSourceFiles.Differences(sections, [new("decl", "PROGRAM P"), new("impl", "<xml/>")]));
        Assert.Empty(PlcSourceFiles.Differences(sections, [new("decl", "PROGRAM P")]));
    }

    [Fact]
    public void Differences_IgnoresLineEndingsAndTrailingWhitespace()
    {
        Assert.Empty(PlcSourceFiles.Differences(PlcSourceFiles.ParseSections(PouXml), XaeSections()));
    }

    [Fact]
    public void Differences_NamesChangedAndOneSidedSections()
    {
        var file = PlcSourceFiles.ParseSections(PouXml);
        var xae = XaeSections("x := 2;").Where(s => s.Key != "Reset/impl").ToList();

        Assert.Equal(["impl", "Reset/impl"], PlcSourceFiles.Differences(file, xae).Order());
    }

    // ── baseline + check ───────────────────────────────────────────────────────

    [Fact]
    public void Check_FirstSight_OnlyRecordsTheBaseline()
    {
        var fake = Session(XaeSections("x := 99;"));

        var result = Guard().Check(fake, null, allProjects: true);

        Assert.Empty(result.Stale);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcObjectSections", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_UnchangedMtime_ReadsNothingFromTheXae()
    {
        var guard = Guard();
        var fake = Session(XaeSections("x := 99;"));
        guard.Establish(fake);

        var result = guard.Check(fake, null, allProjects: true);

        Assert.Empty(result.Stale);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcObjectSections", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_NewerMtimeSameContent_IsNotStale_AndMovesTheBaseline()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        Touch(_pou);

        Assert.Empty(guard.Check(fake, null, allProjects: true).Stale);
        fake.CallLog.Clear();
        Assert.Empty(guard.Check(fake, null, allProjects: true).Stale);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcObjectSections", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ChangedOnDisk_IsStale_AndStaysStale()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        Touch(_pou, PouXml.Replace("x := 1;", "x := 2;", StringComparison.Ordinal));

        Assert.Equal([_pou], guard.Check(fake, null, allProjects: true).Stale);
        Assert.Equal([_pou], guard.Check(fake, null, allProjects: true).Stale);
    }

    [Fact]
    public void Check_XaeObjectNotReadable_IsInconclusive()
    {
        var guard = Guard();
        var fake = Session();
        fake.PlcObjectSections.Clear();
        guard.Establish(fake);
        Touch(_pou);

        var result = guard.Check(fake, null, allProjects: true);

        Assert.Empty(result.Stale);
        Assert.Contains(result.Inconclusive, i => i.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_NamedProjectThatIsNotThere_ChecksNothing()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        Touch(_pou, PouXml.Replace("x := 1;", "x := 2;", StringComparison.Ordinal));

        Assert.Empty(guard.Check(fake, "OtherPlc", allProjects: false).Stale);
        Assert.Single(guard.Check(fake, "MyPlc", allProjects: false).Stale);
    }

    // ── build integration ──────────────────────────────────────────────────────

    [Fact]
    public void Build_StaleFile_IsRefusedBeforeTheBuildSaves()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        Touch(_pou, PouXml.Replace("x := 1;", "x := 2;", StringComparison.Ordinal));

        string result = new BuildCoordinator(fake) { StaleGuard = guard }.BuildPlc(null);

        Assert.StartsWith("[Error] 1 PLC file changed on disk outside the XAE", result);
        Assert.Contains(_pou, result);
        Assert.Contains("twincat_open_project(closeCurrentFirst=true)", result);
        Assert.Contains("ignoreStaleFiles=true", result);
        Assert.DoesNotContain("BuildPlc(<null>)", fake.CallLog);
    }

    [Fact]
    public void Build_StaleFileIgnored_BuildsWithWarn_AndKeepsReporting()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        Touch(_pou, PouXml.Replace("x := 1;", "x := 2;", StringComparison.Ordinal));
        var coordinator = new BuildCoordinator(fake) { StaleGuard = guard };

        string result = coordinator.RebuildSolution(ignoreStaleFiles: true);

        Assert.StartsWith("[Warn] 1 PLC file changed on disk outside the XAE", result);
        Assert.Contains("Build succeeded", result);
        Assert.Contains("RebuildSolution()", fake.CallLog);
        Assert.StartsWith("[Error]", coordinator.RebuildSolution());
    }

    [Fact]
    public void Build_Inconclusive_WarnsAndBuilds()
    {
        var guard = Guard();
        var fake = Session();
        fake.PlcObjectSections.Clear();
        guard.Establish(fake);
        Touch(_pou);

        string result = new BuildCoordinator(fake) { StaleGuard = guard }.BuildSolution();

        Assert.StartsWith("[Warn] Stale-file check inconclusive for 1 file", result);
        Assert.Contains("BuildSolution()", fake.CallLog);
    }

    [Fact]
    public void Build_AfterABuild_TheSavedFilesAreTheNewBaseline()
    {
        var guard = Guard();
        var fake = Session();
        guard.Establish(fake);
        fake.BuildSolutionHook = () => Touch(_pou); // File.SaveAll rewrote the file
        var coordinator = new BuildCoordinator(fake) { StaleGuard = guard };

        coordinator.BuildSolution();
        fake.CallLog.Clear();
        coordinator.BuildSolution();

        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcObjectSections", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenProject_EstablishesTheBaseline()
    {
        var guard = Guard();
        var fake = Session(XaeSections("x := 99;"));
        fake.SolutionOpen = false;
        fake.ActiveSlnPath = null;

        await new LifecycleCoordinator(fake) { StaleGuard = guard }.OpenProjectAsync(_sln, false, CancellationToken.None);
        Touch(_pou);

        Assert.Single(guard.Check(fake, null, allProjects: true).Stale);
    }
}
