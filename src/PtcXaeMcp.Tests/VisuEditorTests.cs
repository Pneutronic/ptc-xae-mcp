using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat.Visu;
using static PtcXaeMcp.Tests.VisuTestElements;

namespace PtcXaeMcp.Tests;

/// <summary>twincat_visu_apply / twincat_visu_edit_elements on the document (pure): building, changing, validation.</summary>
public sealed class VisuEditorTests
{
    private static VisuDocument RoundTrip(VisuDocument doc) => VisuDocument.Parse(doc.Serialize());

    private static Dictionary<string, object> Props(VisuDocument doc, int id) =>
        VisuElements.Read(VisuJson.ElementById(doc, id)!).Properties.ToDictionary(p => p.Key, p => p.Value);

    private const string FullPage = """
        {
          "interface": "VAR_IN_OUT\n\tfbStation : FB_StepChain;\nEND_VAR",
          "elements": [
            { "type": "Rectangle", "x": 10, "y": 10, "width": 120, "height": 40, "text": "Step %d", "textVariable": "MAIN.fbStepChain.Step",
              "tooltip": "the step", "frameColor": "#000000", "fillColor": "#C0FFC0", "alarmFrameColor": "#FF0000", "alarmFillColor": "#FFC0C0",
              "toggleColorVariable": "GVL_Fixture.bEnable", "invisibleVariable": "NOT GVL_Fixture.bEnable",
              "moveXVariable": "GVL_Fixture.nCount", "moveYVariable": "GVL_Fixture.nCount", "rotationVariable": "GVL_Fixture.nCount",
              "tapVariable": "GVL_Fixture.bEnable" },
            { "type": "RoundedRectangle", "x": 150, "y": 10, "width": 100, "height": 40, "text": "Ready" },
            { "type": "Ellipse", "x": 270, "y": 10, "width": 40, "height": 40, "fillColor": "#00FF00" },
            { "type": "Line", "x": 10, "y": 60, "width": 300, "height": 0 },
            { "type": "Label", "x": 10, "y": 70, "width": 200, "height": 20, "text": "Station" },
            { "type": "TextField", "x": 10, "y": 100, "width": 120, "height": 24, "text": "%.2f", "textVariable": "GVL_Fixture.fValue" },
            { "type": "Button", "x": 150, "y": 100, "width": 100, "height": 30, "text": "Start", "tapVariable": "GVL_Fixture.bEnable" }
          ]
        }
        """;

    [Fact]
    public void ApplyPage_BuildsEveryCreatableTypeWithItsProperties()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        var outcome = VisuEditor.ApplyPage(doc, Json(FullPage));
        outcome.AddedIds.Should().HaveCount(7).And.OnlyHaveUniqueItems();

        var back = RoundTrip(doc);
        VisuEditor.WriteProblem(back).Should().BeNull();
        back.InterfaceText.Should().Be("VAR_IN_OUT\n\tfbStation : FB_StepChain;\nEND_VAR");
        var views = back.Elements.Select(VisuElements.Read).ToList();
        views.Select(v => v.Def!.Type).Should().Equal("Rectangle", "RoundedRectangle", "Ellipse", "Line", "Label", "TextField", "Button");
        views.Should().OnlyContain(v => v.Identifier == "GenElemInst_" + v.Id);
        back.UniqueIdGenerator.Should().BeGreaterThan(outcome.AddedIds.Max());

        var rect = Props(back, outcome.AddedIds[0]);
        rect["text"].Should().Be("Step %d");
        rect["textVariable"].Should().Be("MAIN.fbStepChain.Step");
        rect["fillColor"].Should().Be("#FFC0FFC0");
        rect["alarmFillColor"].Should().Be("#FFFFC0C0");
        rect["tapVariable"].Should().Be("GVL_Fixture.bEnable");
        rect["rotationVariable"].Should().Be("GVL_Fixture.nCount");
        rect.Should().HaveCount(17);

        var rectMembers = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[0])!);
        VisuDocument.NumberValue(rectMembers[VisuCatalog.CenterX]).Should().Be(70);
        VisuDocument.NumberValue(rectMembers[VisuCatalog.CenterY]).Should().Be(30);
        VisuDocument.StringValue(rectMembers[VisuCatalog.ShapeMember]).Should().Be("VISU_ST_RECTANGLE");
        rectMembers.Should().ContainKey(VisuCatalog.TextId, "a text gets a text-list id placeholder XAE replaces");
        rectMembers.Should().ContainKey(VisuCatalog.TooltipId);

        var line = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[3])!);
        VisuDocument.NumberValue(line[VisuCatalog.LineEndX]).Should().Be(310);
        VisuDocument.NumberValue(line[VisuCatalog.LineEndY]).Should().Be(60);
        VisuDocument.Field(VisuJson.ElementById(back, outcome.AddedIds[3])!, "VisualElementIsRectangle")!.Value.Should().Be("false");

        var button = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[6])!);
        button.Should().ContainKey(VisuCatalog.ButtonTapVariable);
        button.Should().ContainKey(VisuCatalog.TextId, "XAE refuses a Button text without its text-list id");
    }

    [Fact]
    public void ApplyPage_ReplacesEveryElement_AndKeepsTheInterfaceWhenOmitted()
    {
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        string iface = doc.InterfaceText!;
        VisuEditor.ApplyPage(doc, Json("""{ "elements": [ { "type": "Label", "x": 1, "y": 2, "width": 3, "height": 4 } ] }"""));
        var back = RoundTrip(doc);
        back.Elements.Should().ContainSingle();
        VisuElements.Read(back.Elements[0]).Def!.Type.Should().Be("Label");
        back.InterfaceText.Should().Be(iface);
    }

    [Fact]
    public void ApplyPage_EmptyElements_LeavesAnEmptyList()
    {
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        VisuEditor.ApplyPage(doc, Json("""{ "elements": [] }"""));
        var back = RoundTrip(doc);
        back.Elements.Should().BeEmpty();
        back.ElementList.Attribute("cet").Should().BeNull();
    }

    [Fact]
    public void ApplyPage_CopyFromRefersToThePageAsItWas()
    {
        var doc = EmptyWith(Frame);
        VisuEditor.ApplyPage(doc, Json("""
            { "elements": [
              { "copyFrom": 4, "x": 0 },
              { "copyFrom": 4, "x": 320, "frameParameters": { "fb": "MAIN.fbStation2" } } ] }
            """));
        var back = RoundTrip(doc);
        var frames = back.Elements.Select(VisuElements.Read).ToList();
        frames.Should().HaveCount(2).And.OnlyContain(f => f.Def!.Type == "Frame");
        frames[0].Frame!.Parameters[0].Value.Should().Be("MAIN.fbStation1");
        frames[1].Frame!.Parameters[0].Value.Should().Be("MAIN.fbStation2");
        frames[1].Properties.Single(p => p.Key == "x").Value.Should().Be(320L);
        frames.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        frames.Select(f => VisuDocument.Field(back.Elements[frames.IndexOf(f)], "VisualElementIdentification")!.Value).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Edit_UpdateRemoveAdd_LeaveOtherElementsByteForByte()
    {
        var doc = EmptyWith(Table, Frame);
        var withRect = RoundTrip(ApplyOps(doc, """[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10 } } ]"""));
        int rectId = withRect.Elements.Select(VisuElements.Read).Single(e => e.Def?.Type == "Rectangle").Id!.Value;
        string tableBefore = VisuJson.ElementById(withRect, 7)!.ToString(SaveOptions.DisableFormatting);

        ApplyOps(withRect, $$"""
            [ { "op": "update", "id": {{rectId}}, "set": { "x": 40, "fillColor": "#FF0000", "text": "Moved" } },
              { "op": "remove", "id": 4 },
              { "op": "add", "element": { "type": "Label", "x": 5, "y": 5, "width": 50, "height": 20, "text": "New" } } ]
            """);
        var back = RoundTrip(withRect);
        VisuJson.ElementById(back, 7)!.ToString(SaveOptions.DisableFormatting).Should().Be(tableBefore, "an unsupported element is never touched");
        VisuJson.ElementById(back, 4).Should().BeNull();
        var rect = Props(back, rectId);
        rect["x"].Should().Be(40L);
        rect["fillColor"].Should().Be("#FFFF0000");
        rect["text"].Should().Be("Moved");
        VisuDocument.NumberValue(VisuElements.Members(VisuJson.ElementById(back, rectId)!)[VisuCatalog.CenterX]).Should().Be(45);
        back.Elements.Select(VisuElements.Read).Should().Contain(e => e.Def != null && e.Def.Type == "Label");
    }

    [Fact]
    public void Edit_NullResetsAProperty_AndRemovesItsTextId()
    {
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        int id = VisuEditor.ElementId(doc.Elements[0])!.Value;
        ApplyOps(doc, $$"""[ { "op": "update", "id": {{id}}, "set": { "text": null, "toggleColorVariable": "", "fillColor": null } } ]""");
        var back = RoundTrip(doc);
        var members = VisuElements.Members(back.Elements[0]);
        members.Should().NotContainKey(VisuCatalog.TextMember);
        members.Should().NotContainKey(VisuCatalog.TextId);
        members.Should().NotContainKey(VisuCatalog.ToggleColorVariable);
        members.Should().NotContainKey(VisuCatalog.FillColor);
        members.Should().ContainKey(VisuCatalog.Tooltip);
    }

    [Fact]
    public void Edit_ChangedText_ReplacesTheOldTextIdWithAPlaceholder()
    {
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        int id = VisuEditor.ElementId(doc.Elements[0])!.Value;
        VisuDocument.StringValue(VisuElements.Members(doc.Elements[0])[VisuCatalog.TextId]).Should().NotBe("0");
        ApplyOps(doc, $$"""[ { "op": "update", "id": {{id}}, "set": { "text": "Other %d" } } ]""");
        VisuDocument.StringValue(VisuElements.Members(RoundTrip(doc).Elements[0])[VisuCatalog.TextId]).Should().Be("0");
    }

    [Fact]
    public void Edit_FrameParameter_IsWrittenToNodeAndMember()
    {
        var doc = EmptyWith(Frame);
        ApplyOps(doc, """[ { "op": "update", "id": 4, "set": { "frameParameters": { "fb": "MAIN.fbStation3" }, "y": 20 } } ]""");
        var back = RoundTrip(doc);
        var frame = VisuElements.Read(back.Elements[0]).Frame!;
        frame.Parameters.Single().Value.Should().Be("MAIN.fbStation3");
        back.Serialize().Should().Contain("<v n=\"BasicTypeNodeValue\">\"MAIN.fbStation3\"</v>");
    }

    [Fact]
    public void Edit_InterfaceOnly()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyOperations(doc, Json("[]"), "VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
        RoundTrip(doc).InterfaceText.Should().Be("VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
    }

    [Fact]
    public void Edit_CopyOfAnUnsupportedElement_IsAnExactCopyWithNewIds()
    {
        var doc = EmptyWith(Table);
        ApplyOps(doc, """[ { "op": "add", "element": { "copyFrom": 7 } } ]""");
        var back = RoundTrip(doc);
        back.Elements.Should().HaveCount(2);
        var copy = VisuElements.Read(back.Elements[1]);
        copy.Supported.Should().BeFalse();
        copy.Id.Should().Be(8);
        copy.OtherMembers.Should().Be(2);
    }

    [Theory]
    [InlineData("""[ { "op": "add", "element": { "type": "Gauge", "x": 0, "y": 0, "width": 1, "height": 1 } } ]""", "unknown element type 'Gauge'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Polygon", "x": 0, "y": 0, "width": 1, "height": 1 } } ]""", "Polygon cannot be created")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 1, "height": 1 } } ]""", "Frame cannot be created")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0 } } ]""", "needs width, height")]
    [InlineData("""[ { "op": "add", "element": { "type": "Line", "x": 0, "y": 0, "width": 1, "height": 1, "text": "a" } } ]""", "Line has no property 'text'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 1, "height": 1, "colour": "#FFFFFF" } } ]""", "no property 'colour'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 1, "height": 1, "text": "say \"hi\"" } } ]""", "double quote")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 1, "height": 1, "fillColor": "green" } } ]""", "'#RRGGBB'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 40000, "height": 1 } } ]""", "between")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0.5, "y": 0, "width": 1, "height": 1 } } ]""", "whole number")]
    [InlineData("""[ { "op": "update", "id": 99, "set": { "x": 1 } } ]""", "no element 99")]
    [InlineData("""[ { "op": "update", "id": 7, "set": { "x": 1 } } ]""", "do not support")]
    [InlineData("""[ { "op": "update", "id": 4, "set": { "x": null } } ]""", "cannot be null")]
    [InlineData("""[ { "op": "update", "id": 4, "set": { "type": "Rectangle" } } ]""", "cannot be changed")]
    [InlineData("""[ { "op": "update", "id": 4, "set": {} } ]""", "names no property")]
    [InlineData("""[ { "op": "update", "id": 4, "set": { "frameParameters": { "nope": "x" } } } ]""", "no parameter 'nope'")]
    [InlineData("""[ { "op": "remove", "id": 4 }, { "op": "remove", "id": 4 } ]""", "named twice")]
    [InlineData("""[ { "op": "move", "id": 4 } ]""", "unknown op 'move'")]
    [InlineData("""[ { "op": "remove", "id": 4, "set": {} } ]""", "'set' does not belong")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 7, "x": 3 } } ]""", "copied only as it is")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 4, "frameParameters": { "nope": "x" } } } ]""", "no parameter 'nope'")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 4, "type": "Label" } } ]""", "does not match")]
    [InlineData("""[ { "op": "add", "element": { "type": "Label", "x": 0, "y": 0, "width": 1, "height": 1, "frameParameters": { "fb": "x" } } } ]""", "Frame only")]
    [InlineData("[]", "Nothing to do")]
    [InlineData("{}", "must be a JSON array")]
    public void Edit_InvalidRequests_AreRefusedWithoutAChange(string ops, string expected)
    {
        var doc = EmptyWith(Table, Frame);
        string before = doc.Serialize();
        var act = () => VisuEditor.ApplyOperations(doc, Json(ops), null);
        act.Should().Throw<VisuRequestException>().Which.Message.Should().Contain(expected);
        doc.Serialize().Should().Be(before);
    }

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{ "elements": [], "size": 3 }""", "Unknown page field 'size'")]
    [InlineData("""{ "interface": 3, "elements": [] }""", "'interface' must be a string")]
    [InlineData("""{ "interface": "VAR_IN_OUT \"x\" END_VAR", "elements": [] }""", "double quote")]
    [InlineData("""{ }""", "'elements' array")]
    [InlineData("""{ "elements": [ { "x": 1 } ] }""", "'type' is required")]
    public void ApplyPage_InvalidPages_AreRefusedWithoutAChange(string page, string expected)
    {
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        string before = doc.Serialize();
        var act = () => VisuEditor.ApplyPage(doc, Json(page));
        act.Should().Throw<VisuRequestException>().Which.Message.Should().Contain(expected);
        doc.Serialize().Should().Be(before);
    }

    [Fact]
    public void NewIds_ContinueAfterTheHighestIdAndTheGenerator()
    {
        var doc = EmptyWith(Table); // id 7
        doc.UniqueIdGenerator = 3;
        var outcome = VisuEditor.ApplyOperations(doc, Json("""[ { "op": "add", "element": { "type": "Label", "x": 0, "y": 0, "width": 1, "height": 1 } } ]"""), null);
        outcome.AddedIds.Should().Equal(8);
        doc.UniqueIdGenerator.Should().Be(9);
    }

    private static VisuDocument ApplyOps(VisuDocument doc, string ops)
    {
        VisuEditor.ApplyOperations(doc, Json(ops), null);
        return doc;
    }
}
