using System.Text;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Visu;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_visu_edit_elements and the request checks before the XAE is asked.</summary>
public sealed partial class VisuCoordinatorTests
{
    [Fact]
    public void Edit_ChangesOnlyTheNamedElement()
    {
        int id = VisuCoordinator.ElementIds(ProjectText).Single();
        string result = _coordinator.Edit(Visu, "VISU", $$"""[ { "op": "update", "id": {{id}}, "set": { "y": 50 } } ]""", null, null);
        result.Should().StartWith("Wrote visualization").And.Contain($"Updated element {id}: y.");
        var e = VisuElements.Read(VisuDocument.Parse(ProjectText).Elements.Single());
        e.Properties.Single(p => p.Key == "y").Value.Should().Be(50L);
        e.Properties.Single(p => p.Key == "text").Value.Should().Be("Count %d");
    }

    [Fact]
    public void InvalidRequest_IsRefusedBeforeTheLogout()
    {
        _coordinator.Edit(Visu, "VISU", """[ { "op": "update", "id": 999, "set": { "y": 50 } } ]""", null, null)
            .Should().StartWith("[Error]").And.Contain("no element 999").And.EndWith("Nothing was changed.");
        _coordinator.Apply(Visu, "VISU", "{ not json", null).Should().StartWith("[Error] page is not valid JSON");
        _session.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
        _session.CallLog.Should().NotContain(c => c.StartsWith("DeletePlcObject", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestErrorsThatNeedNoPage_AreRefusedBeforeTheXaeIsAsked()
    {
        _coordinator.Apply(Visu, "VISU", """{ "elements": [ { "type": "Gauge", "x": 0, "y": 0, "width": 1, "height": 1 } ] }""", null)
            .Should().StartWith("[Error]").And.Contain("unknown element type 'Gauge'");
        _coordinator.Edit(Visu, "VISU", """[ { "op": "rotate", "id": 1 } ]""", null, null)
            .Should().StartWith("[Error]").And.Contain("unknown op 'rotate'");
        _coordinator.Edit(Visu, "VISU", """[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 1, "height": 1, "fillColor": "blue" } } ]""", null, null)
            .Should().Contain("'#RRGGBB'");
        _session.CallLog.Should().BeEmpty();
    }
}
