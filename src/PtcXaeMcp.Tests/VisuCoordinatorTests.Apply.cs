using System.Text;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Visu;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_visu_apply and the write path (logout, close, delete, import, read back, recovery, creation, manager).</summary>
public sealed partial class VisuCoordinatorTests
{
    [Fact]
    public void Apply_ReplacesThroughDeleteAndImport_AfterTheLogout_AndReadsBack()
    {
        string result = _coordinator.Apply(Visu, "VISU", OneLabel, expectedRevision: null);

        result.Should().StartWith("Wrote visualization").And.Contain("exactly the content written").And.Contain("Revision: ");
        int logout = CallIndex("LogoutPlc"), close = CallIndex("ClosePlcObject"), delete = CallIndex("DeletePlcObject"), import = CallIndex("ImportPlcObjectFile");
        logout.Should().BeGreaterThanOrEqualTo(0);
        close.Should().BeGreaterThan(logout);
        delete.Should().BeGreaterThan(close);
        import.Should().BeGreaterThan(delete);
        _session.CallLog[import].Should().Contain($"parent={Folder}, name=VISU_Main, type={PlcItemType.VisObj}");

        var page = VisuDocument.Parse(ProjectText);
        VisuElements.Read(page.Elements.Single()).Def!.Type.Should().Be("Label");
        Directory.GetFiles(Path.Combine(_dir, "work"), "VISU_Main.TcVIS", SearchOption.AllDirectories).Should().HaveCount(2, "the new file and the previous one are kept");
    }

    [Fact]
    public void StaleRevision_IsRefusedBeforeTheLogout()
    {
        _coordinator.Apply(Visu, "VISU", OneLabel, expectedRevision: "0123456789abcdef")
            .Should().StartWith("[Error]").And.Contain("changed since it was read");
        _session.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
    }

    [Fact]
    public void MatchingRevision_IsAccepted()
    {
        string revision = VisuJson.Revision(ProjectText);
        _coordinator.Apply(Visu, "VISU", OneLabel, expectedRevision: revision).Should().StartWith("Wrote visualization");
    }

    [Fact]
    public void UnverifiedFileFormat_IsRefusedBeforeTheLogout()
    {
        File.WriteAllText(_projectFile, VisuDocumentTests.OlderFormat.Replace("3.1.4024.11", "3.1.4020.0", StringComparison.Ordinal));
        _coordinator.Apply(Visu, "VISU", OneLabel, null).Should().StartWith("[Error]").And.Contain("3.1.4020.0").And.Contain("Nothing was changed");
        _session.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingManager_IsRefusedBeforeTheLogout_AndNamesTheCall()
    {
        _session.TreeChildren[Root] = [new TreeChildInfo("VISUs", PlcItemType.Folder)];
        string result = _coordinator.Apply(Visu, "VISU", OneLabel, null);
        result.Should().StartWith("[Error]").And.Contain("no visualization manager")
            .And.Contain("twincat_plc_create(kind='VISU', subtype='MANAGER'");
        _session.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlineXae_ThatCannotLogOut_IsRefused()
    {
        _session.FakeLogoutPlcResult = new LogoutPlcResult(false, true, "still online");
        _coordinator.Apply(Visu, "VISU", OneLabel, null).Should().StartWith("[Error] XAE is Online");
        _session.CallLog.Should().NotContain(c => c.StartsWith("DeletePlcObject", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusedImport_ImportsThePreviousFileAgain()
    {
        string before = ProjectText;
        _session.ImportPlcObjectFileThrows.Enqueue(new InvalidOperationException("XAE did not import it"));
        _session.ImportPlcObjectFileThrows.Enqueue(null);

        string result = _coordinator.Apply(Visu, "VISU", OneLabel, null);

        result.Should().StartWith("[Error] XAE did not take the new content").And.Contain("was imported again");
        _session.CallLog.Count(c => c.StartsWith("ImportPlcObjectFile", StringComparison.Ordinal)).Should().Be(2);
        ProjectText.Should().Be(before);
    }

    [Fact]
    public void RefusedImportAndRefusedRestore_SayWhereThePreviousFileIs()
    {
        _session.ImportPlcObjectFileThrows.Enqueue(new InvalidOperationException("no"));
        _session.ImportPlcObjectFileThrows.Enqueue(new InvalidOperationException("no again"));
        _coordinator.Apply(Visu, "VISU", OneLabel, null)
            .Should().StartWith("[Error]").And.Contain("is missing from the project").And.Contain(Path.Combine(_dir, "work"));
    }


    [Fact]
    public void Apply_CreatesAMissingVisualization()
    {
        const string newVisu = Folder + "^VISU_New";
        string newFile = Path.Combine(_dir, "VISU_New.TcVIS");
        _session.MissingPlcPaths.Add(newVisu);
        _session.TreeItemXml[newVisu] = TreeItemXml(newFile);
        _session.CreatePlcChildHook = path =>
        {
            _session.MissingPlcPaths.Remove(path);
            File.WriteAllBytes(newFile, Encoding.UTF8.GetBytes(VisuDocumentTests.FixtureText("Empty.TcVIS")));
        };
        _session.ImportPlcObjectFileHook = (_, _, file) => File.Copy(file, newFile, overwrite: true);

        string result = _coordinator.Apply(newVisu, "VISU", OneLabel, null);

        result.Should().StartWith("Created visualization");
        CallIndex("CreatePlcChild").Should().BeGreaterThan(CallIndex("LogoutPlc"));
        _session.CallLog.Should().Contain(c => c.StartsWith($"CreatePlcChild(parent={Folder}, name=VISU_New, subType={PlcItemType.VisObj}", StringComparison.Ordinal));
        VisuElements.Read(VisuDocument.Parse(Encoding.UTF8.GetString(File.ReadAllBytes(newFile))).Elements.Single()).Def!.Type.Should().Be("Label");
    }

    [Fact]
    public void Apply_Create_ChecksPageParentAndManagerFirst()
    {
        const string newVisu = Folder + "^VISU_New";
        _session.MissingPlcPaths.Add(newVisu);
        _coordinator.Apply(newVisu, "VISU", """{ "elements": [ { "type": "Polygon", "x": 0, "y": 0, "width": 1, "height": 1 } ] }""", null)
            .Should().Contain("Polygon cannot be created");
        _session.MissingPlcPaths.Add(Root + "^MAIN^VISU_New");
        _session.PlcNodeItemTypes[Root + "^MAIN"] = PlcItemType.PouProgram;
        _coordinator.Apply(Root + "^MAIN^VISU_New", "VISU", OneLabel, null).Should().Contain("must be a folder or the PLC project node");
        _coordinator.Apply(Folder + "^1bad name", "VISU", OneLabel, null).Should().StartWith("[Error]");
        _session.TreeChildren[Root] = [];
        _coordinator.Apply(newVisu, "VISU", OneLabel, null).Should().Contain("no visualization manager");
        _session.CallLog.Should().NotContain(c => c.StartsWith("CreatePlcChild", StringComparison.Ordinal));
        _session.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
    }
}
