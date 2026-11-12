using System.Text;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Visu;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The visu tools against the fake session: kind gate and checks before the logout, the replace path
/// (close, delete, import, read back), recovery when XAE refuses the file, creation, the manager rule.
/// </summary>
public sealed partial class VisuCoordinatorTests : IDisposable
{
    private const string Root = "TIPC^P^P Project";
    private const string Folder = Root + "^VISUs";
    private const string Visu = Folder + "^VISU_Main";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "visu-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectFile;
    private readonly FakeTwinCatSession _session = new() { IsConnected = true, SolutionOpen = true };
    private readonly VisuCoordinator _coordinator;

    public VisuCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _projectFile = Path.Combine(_dir, "VISU_Main.TcVIS");
        File.WriteAllBytes(_projectFile, Encoding.UTF8.GetBytes(VisuDocumentTests.FixtureText("Rectangle.TcVIS")));
        _session.PlcNodeItemTypes[Root] = PlcItemType.PlcApp;
        _session.PlcNodeItemTypes[Folder] = PlcItemType.Folder;
        _session.PlcNodeItemTypes[Visu] = PlcItemType.VisObj;
        _session.TreeChildren[Root] = [new TreeChildInfo("VISUs", PlcItemType.Folder), new TreeChildInfo("Visualization Manager", PlcItemType.VisMan)];
        _session.TreeItemXml[Visu] = TreeItemXml(_projectFile);
        _coordinator = new VisuCoordinator(_session) { WorkRoot = Path.Combine(_dir, "work") };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string TreeItemXml(string file) =>
        $"<TreeItem><ItemName>x</ItemName><ItemType>619</ItemType><VSProperties><VSProperty category=\"Misc\" readonly=\"true\"><Name>FullPath</Name><Value>{file}</Value></VSProperty></VSProperties></TreeItem>";

    private string ProjectText => Encoding.UTF8.GetString(File.ReadAllBytes(_projectFile));

    private int CallIndex(string prefix) => _session.CallLog.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));

    private const string OneLabel = """{ "elements": [ { "type": "Label", "x": 1, "y": 2, "width": 30, "height": 10, "text": "Hi" } ] }""";

    [Fact]
    public void Read_ReturnsTheStructuredPage()
    {
        string json = _coordinator.Read(Visu, "VISU");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("path").GetString().Should().Be(Visu);
        root.GetProperty("file").GetString().Should().Be(_projectFile);
        root.GetProperty("writable").GetBoolean().Should().BeTrue();
        root.GetProperty("revision").GetString().Should().HaveLength(16);
        root.GetProperty("elementCount").GetInt32().Should().Be(1);
        var e = root.GetProperty("elements")[0];
        e.GetProperty("type").GetString().Should().Be("Rectangle");
        e.GetProperty("textVariable").GetString().Should().Be("GVL_Fixture.nCount");
        root.GetProperty("extent").GetProperty("width").GetInt64().Should().Be(130);
    }

    [Fact]
    public void Read_WrongNodeKind_IsRefused()
    {
        _session.PlcNodeItemTypes[Visu] = PlcItemType.PouProgram;
        _coordinator.Read(Visu, "VISU").Should().Be($"[Error] '{Visu}' is a POU, not a VISU.");
    }

    [Theory]
    [InlineData("", "kind is required")]
    [InlineData("POU", "work on kind VISU")]
    [InlineData("SCREEN", "Unknown kind 'SCREEN'")]
    public void Read_KindArgument_IsChecked(string kind, string expected) =>
        _coordinator.Read(Visu, kind).Should().StartWith("[Error]").And.Contain(expected);

    [Fact]
    public void Read_TheManagerIsNoVisualization()
    {
        _session.PlcNodeItemTypes[Visu] = PlcItemType.VisMan;
        _coordinator.Read(Visu, "VISU").Should().Contain("is a VISU (MANAGER), not a VISU (VISUALIZATION)");
    }

    [Fact]
    public void Read_PathNotFound_And_NoSolution()
    {
        _session.MissingPlcPaths.Add(Visu);
        _coordinator.Read(Visu, "VISU").Should().StartWith("[Error] PLC tree path not found");
        _session.SolutionOpen = false;
        _coordinator.Read(Visu, "VISU").Should().Contain("No solution is currently open");
    }


    [Fact]
    public void FullPath_IsReadFromTheTreeItemProperties()
    {
        VisuFiles.FullPathFromTreeItemXml(TreeItemXml(@"C:\x\VISUs\V.TcVIS")).Should().Be(@"C:\x\VISUs\V.TcVIS");
        VisuFiles.FullPathFromTreeItemXml("<TreeItem />").Should().BeNull();
        VisuFiles.FullPathFromTreeItemXml("not xml").Should().BeNull();
    }
}
