using System.Text;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Visu;

namespace PtcXaeMcp.Tests;

// Frames through the coordinator: the referenced visualization is found in the project tree and its interface read.
public sealed partial class VisuCoordinatorTests
{
    private const string BlockPath = Folder + "^VISU_Block";

    private void AddBlock(string interfaceText)
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        doc.SetInterfaceText(interfaceText);
        string file = Path.Combine(_dir, "VISU_Block.TcVIS");
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes(doc.Serialize()));
        _session.PlcNodeItemTypes[BlockPath] = PlcItemType.VisObj;
        _session.TreeChildren[Folder] = [new TreeChildInfo("VISU_Main", PlcItemType.VisObj), new TreeChildInfo("VISU_Block", PlcItemType.VisObj)];
        _session.TreeItemXml[BlockPath] = TreeItemXml(file);
    }

    private const string AddFrame = """
        [ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 160, "height": 60, "visualization": "VISU_Block",
            "parameters": { "fb": "MAIN.fbStepChain" } } } ]
        """;

    [Fact]
    public void Edit_AddFrame_FindsTheBlockAndWritesItsReference()
    {
        AddBlock("VAR_IN_OUT\n\tfb : FB_StepChain;\nEND_VAR");
        string result = _coordinator.Edit(Visu, "VISU", AddFrame, null, null);
        result.Should().NotStartWith("[Error]", result);
        var frame = VisuDocument.Parse(ProjectText).Elements.Select(VisuElements.Read).Single(e => e.Def?.Type == "Frame");
        frame.Frame!.Visualizations.Should().Equal("VISU_Block");
        frame.Frame.Parameters.Single().Value.Should().Be("MAIN.fbStepChain");
    }

    [Fact]
    public void Edit_AddFrame_UnknownVisualization_IsRefusedBeforeAnythingChanges()
    {
        _session.TreeChildren[Folder] = [new TreeChildInfo("VISU_Main", PlcItemType.VisObj)];
        string before = ProjectText;
        string result = _coordinator.Edit(Visu, "VISU", AddFrame, null, null);
        result.Should().StartWith("[Error]").And.Contain("no visualization 'VISU_Block'").And.Contain("Nothing was changed");
        ProjectText.Should().Be(before);
        CallIndex("DeletePlcObject").Should().Be(-1);
    }

    [Fact]
    public void Edit_AddFrame_BlockWithAnInputSection_IsRefused()
    {
        AddBlock("VAR_INPUT\n\tbOn : BOOL;\nEND_VAR\nVAR_IN_OUT\n\tfb : FB_StepChain;\nEND_VAR");
        string result = _coordinator.Edit(Visu, "VISU", AddFrame, null, null);
        result.Should().StartWith("[Error]").And.Contain("VAR_IN_OUT parameters only");
    }
}
