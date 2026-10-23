using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// open_project refuses a solution whose .tsproj references an independent project file (.xti) that does not
/// exist — the XAE would load a broken configuration, and saving it damages the .tsproj.
/// </summary>
public sealed class FileReferencePreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptc-xti-" + Guid.NewGuid().ToString("N"));
    private readonly string _sln;
    private readonly string _proj;

    public FileReferencePreflightTests()
    {
        _proj = Path.Combine(_root, "Proj");
        Directory.CreateDirectory(_proj);
        _sln = Path.Combine(_root, "Proj.sln");
        File.WriteAllText(_sln, "Project(\"{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}\") = \"Proj\", \"Proj\\Proj.tsproj\", \"{X}\"\r\nEndProject\r\n");

        // The layout TwinCAT writes: tsproj → _Config\IO\Device; Device → <dir>\<stem>\Box; Box → nested Box;
        // tsproj → _Config\NC\NC.xti → _Config\NC\Axes\Axis.
        File.WriteAllText(Path.Combine(_proj, "Proj.tsproj"),
            "<TcSmProject><Project><Io><Device File=\"Device 1 (EtherCAT).xti\" Id=\"1\"/></Io>" +
            "<NC File=\"NC.xti\"/><AdditionalTmcFile Name=\"Extra\" File=\"Extra.tmc\"/></Project></TcSmProject>");
        Write(@"_Config\IO\Device 1 (EtherCAT).xti", "<TcSmItem><Device><Box File=\"Term 1 (EK1100).xti\" Id=\"1\"/></Device></TcSmItem>");
        Write(@"_Config\IO\Device 1 (EtherCAT)\Term 1 (EK1100).xti", "<TcSmItem><Box><Box File=\"Term 2 (EL1008).xti\" Id=\"2\"/></Box></TcSmItem>");
        Write(@"_Config\IO\Device 1 (EtherCAT)\Term 1 (EK1100)\Term 2 (EL1008).xti", "<TcSmItem><Box/></TcSmItem>");
        Write(@"_Config\NC\NC.xti", "<TcSmItem><NC><Axis File=\"Axis 1.xti\" Id=\"1\"/></NC></TcSmItem>");
        Write(@"_Config\NC\Axes\Axis 1.xti", "<TcSmItem><Axis/></TcSmItem>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_proj, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Delete(string relative) => File.Delete(Path.Combine(_proj, relative));

    [Fact]
    public void CompleteProject_HasNoFindings_AndIgnoresNonXtiFiles()
    {
        var report = FileReferencePreflight.Check(_sln);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void RenamedBox_IsReportedWithItsReferencingFile()
    {
        File.Move(Path.Combine(_proj, @"_Config\IO\Device 1 (EtherCAT)\Term 1 (EK1100)\Term 2 (EL1008).xti"),
                  Path.Combine(_proj, @"_Config\IO\Device 1 (EtherCAT)\Term 1 (EK1100)\DI1 (EL1008).xti"));

        var m = Assert.Single(FileReferencePreflight.Check(_sln).Missing);

        Assert.Equal(@"_Config\IO\Device 1 (EtherCAT)\Term 1 (EK1100).xti", m.ReferencingFile);
        Assert.Equal("Term 2 (EL1008).xti", m.Reference);
        Assert.Equal(@"_Config\IO\Device 1 (EtherCAT)", m.SearchedUnder);
    }

    [Fact]
    public void MissingDevice_DoesNotDescendFurther()
    {
        Delete(@"_Config\IO\Device 1 (EtherCAT).xti");

        var m = Assert.Single(FileReferencePreflight.Check(_sln).Missing);

        Assert.Equal("Proj.tsproj", m.ReferencingFile);
        Assert.Equal("_Config", m.SearchedUnder);
    }

    [Fact]
    public void MissingAxis_IsFoundBelowTheNcFolder()
    {
        Delete(@"_Config\NC\Axes\Axis 1.xti");

        var m = Assert.Single(FileReferencePreflight.Check(_sln).Missing);

        Assert.Equal("Axis 1.xti", m.Reference);
    }

    [Fact]
    public void UnparseableXti_OnlyWarns()
    {
        Write(@"_Config\NC\NC.xti", "<TcSmItem><NC>");

        var report = FileReferencePreflight.Check(_sln);

        Assert.Empty(report.Missing);
        Assert.Contains(report.Warnings, w => w.Contains("NC.xti", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_NamesEveryReference()
    {
        string text = FileReferencePreflight.Describe(
            [new(@"a.xti", "b.xti", "_Config"), new(@"c.xti", "d.xti", "_Config")]);

        Assert.StartsWith("2 referenced project files (.xti) missing:", text);
        Assert.Contains("a.xti → File=\"b.xti\"", text);
        Assert.Contains("c.xti → File=\"d.xti\"", text);
    }

    // ── open_project / save / close ────────────────────────────────────────────

    private static FakeTwinCatSession Session() => new() { IsConnected = true, SolutionOpen = false };

    [Fact]
    public async Task Open_MissingReference_RefusesBeforeTheXaeIsTouched()
    {
        Delete(@"_Config\NC\Axes\Axis 1.xti");
        var fake = Session();

        string result = await new LifecycleCoordinator(fake).OpenProjectAsync(_sln, false, CancellationToken.None);

        Assert.StartsWith("[Error] 1 referenced project file (.xti) missing", result);
        Assert.Contains("allowMissingReferences=true", result);
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public async Task Open_MissingReferenceAllowed_OpensWithWarn_AndSaveRepeatsIt()
    {
        Delete(@"_Config\NC\Axes\Axis 1.xti");
        var fake = Session();
        var coordinator = new LifecycleCoordinator(fake) { StaleGuard = new StaleBufferGuard() };

        string opened = await coordinator.OpenProjectAsync(_sln, false, CancellationToken.None, allowMissingReferences: true);
        string saved = coordinator.SaveProject();
        string closed = coordinator.CloseProject(saveFirst: true);
        string reopened = await coordinator.OpenProjectAsync(_sln, false, CancellationToken.None, allowMissingReferences: true);
        Write(@"_Config\NC\Axes\Axis 1.xti", "<TcSmItem><Axis/></TcSmItem>");
        coordinator.CloseProject(saveFirst: false);
        await coordinator.OpenProjectAsync(_sln, false, CancellationToken.None);

        Assert.StartsWith("[Warn] Opened with missing references: 1 referenced project file", opened);
        Assert.Contains($"Opened {_sln}", opened);
        Assert.Contains($"OpenSolution({_sln})", fake.CallLog);
        Assert.StartsWith("[Warn] The project was opened with missing file references", saved);
        Assert.StartsWith("[Warn] The project was opened with missing file references", closed);
        Assert.StartsWith("[Warn]", reopened);
        Assert.Null(LifecycleCoordinator.OpenedWithMissingReferences);
        Assert.DoesNotContain("[Warn]", coordinator.SaveProject());
    }

    [Fact]
    public async Task Open_CompleteProject_HasNoWarn()
    {
        var fake = Session();

        string result = await new LifecycleCoordinator(fake) { StaleGuard = new StaleBufferGuard() }
            .OpenProjectAsync(_sln, false, CancellationToken.None);

        Assert.StartsWith("Opened ", result);
    }
}
