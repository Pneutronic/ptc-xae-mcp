using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// plc-project-lifecycle: argument checks run before the XAE is called (a failing CreateChild opens a
/// modal XAE dialog, live 2026-10-07), the mode → subType mapping, and the read-back of every mutation.
/// </summary>
public sealed class PlcProjectLifecycleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ptc-plc-lifecycle-").FullName;
    private readonly string _templates;

    public PlcProjectLifecycleTests()
    {
        // Both Beckhoff layouts: 1.0.x flat, 1.1.x one folder per template.
        _templates = Path.Combine(_dir, "PlcTemplates");
        Directory.CreateDirectory(Path.Combine(_templates, "1.0.0.0", "Plc Templates"));
        File.WriteAllText(Path.Combine(_templates, "1.0.0.0", "Plc Templates", "Empty PLC Template.plcproj"), "<Project/>");
        Directory.CreateDirectory(Path.Combine(_templates, "1.1.0.1", "Standard PLC Template"));
        File.WriteAllText(Path.Combine(_templates, "1.1.0.1", "Standard PLC Template", "Standard PLC Template.plcproj"), "<Project/>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    private PlcProjectLifecycleCoordinator Coordinator(FakeTwinCatSession fake) => new(fake) { TemplateRoots = [_templates] };

    private static bool CalledXae(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("CreatePlcProjectChild", StringComparison.Ordinal) || c == "LogoutPlc()");

    // ---- args ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Plc2", true)]
    [InlineData("My_Plc 2", true)]
    [InlineData("", false)]
    [InlineData(" Plc", false)]
    [InlineData("a^b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a.b", false)]
    public void ValidateProjectName(string name, bool ok) =>
        (PlcProjectLifecycleArgs.ValidateProjectName(name) is null).Should().Be(ok);

    [Theory]
    [InlineData("COPY", 0)]
    [InlineData("move", 1)]
    [InlineData("In_Place", 2)]
    public void ParseMode_MapsToSubType(string mode, int subType)
    {
        PlcProjectLifecycleArgs.ParseMode(mode, out var parsed).Should().BeNull();
        ((int)parsed).Should().Be(subType);
    }

    [Theory]
    [InlineData("", "mode is required")]
    [InlineData("LINK", "Unknown mode 'LINK'")]
    public void ParseMode_Rejects(string mode, string message) =>
        PlcProjectLifecycleArgs.ParseMode(mode, out _).Should().Contain(message);

    [Fact]
    public void ResolveTemplate_DefaultIsStandard()
    {
        PlcProjectLifecycleArgs.ResolveTemplate(null, [_templates], out string source).Should().BeNull();
        source.Should().Be("Standard PLC Template");
    }

    [Fact]
    public void ResolveTemplate_NameIsCaseInsensitive_AndCanonicalised()
    {
        PlcProjectLifecycleArgs.ResolveTemplate("empty plc template", [_templates], out string source).Should().BeNull();
        source.Should().Be("Empty PLC Template");
    }

    [Fact]
    public void ResolveTemplate_UnknownName_ListsAvailable()
    {
        string? error = PlcProjectLifecycleArgs.ResolveTemplate("No Such Template", [_templates], out _);
        error.Should().Contain("not found").And.Contain("Empty PLC Template").And.Contain("Standard PLC Template");
    }

    [Fact]
    public void ResolveTemplate_CustomPath_MustExistAndBePlcproj()
    {
        string custom = Path.Combine(_dir, "Mine.plcproj");
        PlcProjectLifecycleArgs.ResolveTemplate(custom, [_templates], out _).Should().Contain("file not found");
        File.WriteAllText(custom, "<Project/>");
        PlcProjectLifecycleArgs.ResolveTemplate(custom, [_templates], out string source).Should().BeNull();
        source.Should().Be(custom);
        PlcProjectLifecycleArgs.ResolveTemplate(Path.Combine(_dir, "Mine.txt"), [_templates], out _).Should().Contain(".plcproj");
    }

    [Fact]
    public void EffectivePlcprojName_ReadsNameProperty_ElseFileName()
    {
        string withName = Path.Combine(_dir, "FileName.plcproj");
        File.WriteAllText(withName,
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><Name>Inner</Name></PropertyGroup></Project>");
        PlcProjectLifecycleArgs.EffectivePlcprojName(withName).Should().Be("Inner");

        string without = Path.Combine(_dir, "Other.plcproj");
        File.WriteAllText(without, "<Project><PropertyGroup/></Project>");
        PlcProjectLifecycleArgs.EffectivePlcprojName(without).Should().Be("Other");

        string broken = Path.Combine(_dir, "Broken.plcproj");
        File.WriteAllText(broken, "not xml");
        PlcProjectLifecycleArgs.EffectivePlcprojName(broken).Should().Be("Broken");
    }

    [Fact]
    public void TmcModuleName_ReadsFirstModule()
    {
        string tmc = Path.Combine(_dir, "X.tmc");
        File.WriteAllText(tmc, "<TcModuleClass><DataTypes><DataType><Name>ST_X</Name></DataType></DataTypes>" +
                               "<Modules><Module GUID=\"{1}\"><Name>MyPlc</Name></Module></Modules></TcModuleClass>");
        PlcProjectLifecycleArgs.TmcModuleName(tmc).Should().Be("MyPlc");

        File.WriteAllText(tmc, "<TcModuleClass/>");
        PlcProjectLifecycleArgs.TmcModuleName(tmc).Should().BeNull();
    }

    // ---- create ----------------------------------------------------------------------------

    [Fact]
    public void Create_Standard_CallsCreateChildWithTemplateName_AndReadsBack()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).CreateProject("Plc2", null);

        result.Should().StartWith("Created PLC project 'Plc2' from template 'Standard PLC Template'")
            .And.Contain("read-back: TIPC lists FakePlc, Plc2");
        fake.CallLog.Should().Contain("CreatePlcProjectChild(Plc2,0,Standard PLC Template,vInfoNull=False)");
        fake.CallLog.Should().Contain("LogoutPlc()").And.Contain("SaveAll()");
        fake.CallLog.IndexOf("LogoutPlc()").Should().BeLessThan(fake.CallLog.FindIndex(c => c.StartsWith("CreatePlcProjectChild", StringComparison.Ordinal)));
    }

    [Fact]
    public void Create_UnknownTemplate_IsRefusedBeforeTheXae()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).CreateProject("Plc2", "No Such Template");

        result.Should().StartWith("[Error] Template 'No Such Template' not found");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_ExistingName_IsRefusedBeforeTheXae()
    {
        var fake = OpenFake();

        string result = Coordinator(fake).CreateProject("fakeplc", null);

        result.Should().StartWith("[Error] A PLC project named 'fakeplc' already exists");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_BadName_IsRefused()
    {
        var fake = OpenFake();
        Coordinator(fake).CreateProject("a^b", null).Should().StartWith("[Error] name 'a^b'");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void Create_NoSolution_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true };
        Coordinator(fake).CreateProject("Plc2", null).Should().StartWith("[Error] No solution is currently open.\nNext: ");
    }

    [Fact]
    public void Create_StillOnline_IsRefused()
    {
        var fake = OpenFake();
        fake.FakeLogoutPlcResult = new LogoutPlcResult(true, true, "still online (fake)");

        Coordinator(fake).CreateProject("Plc2", null).Should().StartWith("[Error]").And.Contain("Online");
        fake.CallLog.Should().NotContain(c => c.StartsWith("CreatePlcProjectChild", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_NotInTipcAfterwards_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.CreatePlcProjectChild));

        Coordinator(fake).CreateProject("Plc2", null).Should().StartWith("[Error] CreateChild returned 'Plc2', but TIPC does not list a new 'Plc2'");
    }

    [Fact]
    public void Create_ComFailure_IsError()
    {
        var fake = OpenFake();
        fake.CreatePlcProjectChildThrows = new InvalidOperationException("Cannot create new PLC project 'Plc2'!");

        Coordinator(fake).CreateProject("Plc2", null).Should().StartWith("[Error]").And.Contain("Cannot create new PLC project");
    }

    // ---- add_existing ----------------------------------------------------------------------

    private string Plcproj(string folder, string fileName, string? innerName)
    {
        string dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName + ".plcproj");
        File.WriteAllText(path, innerName is null
            ? "<Project><PropertyGroup/></Project>"
            : $"<Project><PropertyGroup><Name>{innerName}</Name></PropertyGroup></Project>");
        return path;
    }

    [Theory]
    [InlineData("COPY", 0, "Ext")]
    [InlineData("MOVE", 1, "Ext")]
    [InlineData("IN_PLACE", 2, "")]
    public void AddExisting_Plcproj_PassesSubTypeAndName(string mode, int subType, string passedName)
    {
        var fake = OpenFake();
        fake.CreatedChildName = "Ext";
        string path = Plcproj("src", "Ext", "Ext");

        string result = Coordinator(fake).AddExisting(path, mode, null);

        result.Should().StartWith("Added PLC project 'Ext'").And.Contain("read-back: TIPC lists FakePlc, Ext");
        fake.CallLog.Should().Contain($"CreatePlcProjectChild({passedName},{subType},{path},vInfoNull=False)");
    }

    [Fact]
    public void AddExisting_NameDiffersFromTheProjectsOwnName_IsRefused()
    {
        var fake = OpenFake();
        string path = Plcproj("src", "File", "Inner");

        string result = Coordinator(fake).AddExisting(path, "COPY", "Other");

        result.Should().StartWith("[Error] The XAE names a project taken in from a .plcproj after the project itself ('Inner')");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void AddExisting_InnerNameCollides_IsRefusedBeforeTheXae()
    {
        // Live 2026-10-07: a .plcproj whose <Name> is taken opens "Loading project failed … already exists" as a modal dialog.
        var fake = OpenFake();
        string path = Plcproj("src", "Unique", "FakePlc");

        Coordinator(fake).AddExisting(path, "IN_PLACE", null).Should().StartWith("[Error] A PLC project named 'FakePlc' already exists");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void AddExisting_CopyTargetFolderExists_IsRefused()
    {
        var fake = OpenFake();
        fake.ActiveSlnPath = Path.Combine(_dir, "sln", "S.sln");
        Directory.CreateDirectory(Path.Combine(_dir, "sln", "Ext"));
        File.WriteAllText(Path.Combine(_dir, "sln", "Ext", "x.txt"), "x");
        string path = Plcproj("src", "Ext", "Ext");

        Coordinator(fake).AddExisting(path, "COPY", null).Should().StartWith("[Error] mode COPY would copy the project to");
        Coordinator(fake).AddExisting(path, "IN_PLACE", null).Should().StartWith("Added");
    }

    [Theory]
    [InlineData("", "mode is required")]
    [InlineData("LINK", "Unknown mode")]
    public void AddExisting_BadMode_IsRefused(string mode, string message)
    {
        var fake = OpenFake();
        Coordinator(fake).AddExisting(Plcproj("src", "Ext", "Ext"), mode, null).Should().StartWith("[Error]").And.Contain(message);
        CalledXae(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData("Ext.txt", "must end with .plcproj or .tpzip")]
    [InlineData("Missing.plcproj", "file not found")]
    public void AddExisting_BadPath_IsRefused(string file, string message)
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, file);
        if (file.EndsWith(".txt", StringComparison.Ordinal)) File.WriteAllText(path, "x");

        Coordinator(fake).AddExisting(path, "COPY", null).Should().StartWith("[Error] path").And.Contain(message);
    }

    [Fact]
    public void AddExisting_Tpzip_NeedsCopyAndName_ThenImports()
    {
        var fake = OpenFake();
        string tpzip = Path.Combine(_dir, "P.tpzip");
        File.WriteAllText(tpzip, "zip");

        Coordinator(fake).AddExisting(tpzip, "MOVE", "P2").Should().StartWith("[Error] mode MOVE does not apply to a .tpzip");
        Coordinator(fake).AddExisting(tpzip, "COPY", null).Should().StartWith("[Error] name is required");
        Coordinator(fake).AddExisting(tpzip, "COPY", "P2").Should().StartWith("Imported");
        fake.CallLog.Should().Contain($"CreatePlcProjectChild(P2,0,{tpzip},vInfoNull=True)");
    }

    // ---- export ----------------------------------------------------------------------------

    [Fact]
    public void Export_WritesFile_AndReportsReadBack()
    {
        var fake = OpenFake();
        string tpzip = Path.Combine(_dir, "FakePlc.tpzip");

        string result = Coordinator(fake).Export("FakePlc", tpzip);

        result.Should().StartWith($"Exported PLC project 'FakePlc' to {tpzip}").And.Contain("bytes written");
        fake.CallLog.Should().Contain($"ExportPlcProject(FakePlc,{tpzip})").And.NotContain("LogoutPlc()");
    }

    [Fact]
    public void Export_UnknownProject_IsRefused()
    {
        var fake = OpenFake();
        Coordinator(fake).Export("Nope", Path.Combine(_dir, "x.tpzip")).Should().StartWith("[Error] No PLC project 'Nope' under TIPC. Present: FakePlc");
    }

    [Fact]
    public void Export_StaleFile_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.ExportPlcProject));
        string tpzip = Path.Combine(_dir, "old.tpzip");
        File.WriteAllText(tpzip, "old");
        File.SetLastWriteTimeUtc(tpzip, DateTime.UtcNow.AddHours(-1));

        Coordinator(fake).Export("FakePlc", tpzip).Should().StartWith("[Error] Export did not land").And.Contain("old file");
    }

    [Theory]
    [InlineData("x.zip", "must end with .tpzip")]
    [InlineData("missingdir/x.tpzip", "target directory does not exist")]
    public void Export_BadTarget_IsRefused(string file, string message)
    {
        var fake = OpenFake();
        Coordinator(fake).Export("FakePlc", Path.Combine(_dir, file)).Should().StartWith("[Error] tpzipPath").And.Contain(message);
    }

    // ---- import ----------------------------------------------------------------------------

    [Fact]
    public void Import_PassesNullVInfo_AndReadsBack()
    {
        var fake = OpenFake();
        string tpzip = Path.Combine(_dir, "P.tpzip");
        File.WriteAllText(tpzip, "zip");

        string result = Coordinator(fake).Import(tpzip, "Copy1");

        result.Should().StartWith($"Imported '{tpzip}' as PLC project 'Copy1'").And.Contain("read-back: TIPC lists FakePlc, Copy1");
        fake.CallLog.Should().Contain($"CreatePlcProjectChild(Copy1,0,{tpzip},vInfoNull=True)");
    }

    [Fact]
    public void Import_ExistingName_OrMissingFile_IsRefused()
    {
        var fake = OpenFake();
        string tpzip = Path.Combine(_dir, "P.tpzip");

        Coordinator(fake).Import(tpzip, "Copy1").Should().StartWith("[Error] tpzipPath: file not found");
        File.WriteAllText(tpzip, "zip");
        Coordinator(fake).Import(tpzip, "FakePlc").Should().StartWith("[Error] A PLC project named 'FakePlc' already exists");
        CalledXae(fake).Should().BeFalse();
    }

    // ---- save archive ------------------------------------------------------------------------

    [Fact]
    public void SaveArchive_SavesFirst_ThenWrites()
    {
        var fake = OpenFake();
        string tszip = Path.Combine(_dir, "S.tszip");

        string result = Coordinator(fake).SaveArchive(tszip);

        result.Should().StartWith($"Saved the solution as archive {tszip}").And.Contain("bytes written");
        fake.CallLog.IndexOf("SaveAll()").Should().BeLessThan(fake.CallLog.IndexOf($"SaveSolutionArchive({tszip})"));
    }

    [Fact]
    public void SaveArchive_NotWritten_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.SaveSolutionArchive));

        Coordinator(fake).SaveArchive(Path.Combine(_dir, "S.tszip")).Should().StartWith("[Error] Save as archive did not land").And.Contain("does not exist");
    }

    [Fact]
    public void SaveArchive_WrongExtension_IsRefused()
    {
        var fake = OpenFake();
        Coordinator(fake).SaveArchive(Path.Combine(_dir, "S.tnzip")).Should().StartWith("[Error] tszipPath must end with .tszip");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    // ---- open archive ------------------------------------------------------------------------

    private string Tszip()
    {
        string tszip = Path.Combine(_dir, "S.tszip");
        File.WriteAllText(tszip, "zip");
        return tszip;
    }

    [Fact]
    public void OpenArchive_NoSolutionOpen_UnpacksAndProvesTheActiveSolution()
    {
        var fake = new FakeTwinCatSession { IsConnected = true };
        string dest = Path.Combine(_dir, "opened");

        string result = Coordinator(fake).OpenArchive(Tszip(), dest, "Opened", closeCurrentFirst: false);

        result.Should().StartWith("Opened archive").And.Contain($"read-back: the open solution is {Path.Combine(dest, "Opened.sln")}");
        fake.CallLog.Should().NotContain(c => c.StartsWith("CloseSolution", StringComparison.Ordinal));
    }

    [Fact]
    public void OpenArchive_SolutionOpen_NeedsCloseCurrentFirst()
    {
        var fake = OpenFake();
        string dest = Path.Combine(_dir, "opened");

        Coordinator(fake).OpenArchive(Tszip(), dest, "Opened", closeCurrentFirst: false).Should().StartWith("[Error] A solution is already open");
        fake.CallLog.Should().NotContain(c => c.StartsWith("OpenSolutionArchive", StringComparison.Ordinal));

        Coordinator(fake).OpenArchive(Tszip(), dest, "Opened", closeCurrentFirst: true).Should().StartWith("Opened archive");
        fake.CallLog.Should().Contain("CloseSolution(saveFirst=False)");
    }

    [Fact]
    public void OpenArchive_NonEmptyDestDir_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true };
        string dest = Path.Combine(_dir, "busy");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "x.txt"), "x");

        Coordinator(fake).OpenArchive(Tszip(), dest, "Opened", false).Should().StartWith("[Error] destDir").And.Contain("not empty");
    }

    [Fact]
    public void OpenArchive_NoXae_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        Coordinator(fake).OpenArchive(Tszip(), Path.Combine(_dir, "o"), "Opened", false).Should().StartWith("[Error] No TwinCAT XAE instance is running.");
    }

    [Fact]
    public void OpenArchive_NothingUnpacked_IsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true };
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.OpenSolutionArchive));

        Coordinator(fake).OpenArchive(Tszip(), Path.Combine(_dir, "o"), "Opened", false).Should().StartWith("[Error] Open archive did not land");
    }

    // ---- instance from tmc ---------------------------------------------------------------------

    private string Tmc(string module)
    {
        string tmc = Path.Combine(_dir, "M.tmc");
        File.WriteAllText(tmc, $"<TcModuleClass><Modules><Module><Name>{module}</Name></Module></Modules></TcModuleClass>");
        return tmc;
    }

    [Fact]
    public void InstanceFromTmc_UsesTheModuleName()
    {
        var fake = OpenFake();
        string tmc = Tmc("Inst1");

        string result = Coordinator(fake).InstanceFromTmc(null, tmc);

        result.Should().StartWith("Created PLC instance 'Inst1'").And.Contain("TIPC lists FakePlc, Inst1")
            .And.Contain("no PLC source to build").And.NotContain("twincat_build_plc");
        fake.CallLog.Should().Contain($"CreatePlcProjectChild(Inst1,0,{tmc},vInfoNull=False)");
    }

    [Fact]
    public void InstanceFromTmc_ModuleNameTaken_IsRefused()
    {
        // Live 2026-10-07: the XAE happily created a SECOND 'FixturePlc' child from the fixture's own .tmc.
        var fake = OpenFake();
        Coordinator(fake).InstanceFromTmc(null, Tmc("FakePlc")).Should().StartWith("[Error] A PLC project named 'FakePlc' already exists");
        CalledXae(fake).Should().BeFalse();
    }

    [Fact]
    public void InstanceFromTmc_OtherName_IsRefused()
    {
        var fake = OpenFake();
        Coordinator(fake).InstanceFromTmc("Other", Tmc("Inst1")).Should().StartWith("[Error] The XAE names a PLC instance created from a .tmc after its module ('Inst1')");
    }

    [Fact]
    public void InstanceFromTmc_NoModule_IsRefused()
    {
        var fake = OpenFake();
        string tmc = Path.Combine(_dir, "Empty.tmc");
        File.WriteAllText(tmc, "<TcModuleClass/>");
        Coordinator(fake).InstanceFromTmc(null, tmc).Should().StartWith("[Error]").And.Contain("contains no <Module>");
    }
}
