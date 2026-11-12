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
              "frameColor": "#000000", "fillColor": "#C0FFC0", "alarmFrameColor": "#FF0000", "alarmFillColor": "#FFC0C0",
              "toggleColorVariable": "GVL_Fixture.bEnable", "invisibleVariable": "NOT GVL_Fixture.bEnable",
              "fillColorVariable": "SEL(GVL_Fixture.bEnable, 16#FFC0C0C0, 16#FF00C000)", "frameColorVariable": "16#FF0000FF",
              "textColorVariable": "16#FFFF0000",
              "moveXVariable": "GVL_Fixture.nCount", "moveYVariable": "GVL_Fixture.nCount", "rotationVariable": "GVL_Fixture.nCount" },
            { "type": "RoundedRectangle", "x": 150, "y": 10, "width": 100, "height": 40, "text": "Ready" },
            { "type": "Ellipse", "x": 270, "y": 10, "width": 40, "height": 40, "fillColor": "#00FF00" },
            { "type": "Line", "x": 10, "y": 60, "width": 300, "height": 0, "lineColor": "#FF0000", "moveYVariable": "GVL_Fixture.nCount" },
            { "type": "Label", "x": 10, "y": 70, "width": 200, "height": 20, "text": "Station" },
            { "type": "TextField", "x": 10, "y": 100, "width": 120, "height": 24, "text": "%.2f", "textVariable": "GVL_Fixture.fValue" },
            { "type": "Button", "x": 150, "y": 100, "width": 100, "height": 30, "text": "Start", "textColorVariable": "16#FF0000FF" }
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
        rect["fillColorVariable"].Should().Be("SEL(GVL_Fixture.bEnable, 16#FFC0C0C0, 16#FF00C000)");
        rect["rotationVariable"].Should().Be("GVL_Fixture.nCount");
        rect.Should().HaveCount(18);

        var rectMembers = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[0])!);
        VisuDocument.NumberValue(rectMembers[VisuCatalog.CenterX]).Should().Be(70);
        VisuDocument.NumberValue(rectMembers[VisuCatalog.CenterY]).Should().Be(30);
        VisuDocument.StringValue(rectMembers[VisuCatalog.ShapeMember]).Should().Be("VISU_ST_RECTANGLE");
        rectMembers.Should().ContainKey(VisuCatalog.TextId, "a text gets a text-list id placeholder XAE replaces");
        rectMembers.Should().ContainKey(VisuCatalog.InvisibleVariable);
        rectMembers.Should().ContainKey(VisuCatalog.ToggleColorVariable);

        var line = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[3])!);
        VisuDocument.NumberValue(line[VisuCatalog.LineStartX]).Should().Be(10);
        VisuDocument.NumberValue(line[VisuCatalog.LineStartY]).Should().Be(60);
        VisuDocument.NumberValue(line[VisuCatalog.LineEndX]).Should().Be(310);
        VisuDocument.NumberValue(line[VisuCatalog.LineEndY]).Should().Be(60);
        line.Should().ContainKey(VisuCatalog.LinePoints, "a line without its point list is not drawn");
        line[VisuCatalog.LinePoints].Attribute("t")!.Value.Should().Be("StructuredTypeNode");
        VisuDocument.NumberValue(line[VisuCatalog.LineAngle]).Should().Be(0);
        Props(back, outcome.AddedIds[3])["lineColor"].Should().Be("#FFFF0000");
        VisuDocument.Field(VisuJson.ElementById(back, outcome.AddedIds[3])!, "VisualElementIsRectangle")!.Value.Should().Be("false");

        var button = VisuElements.Members(VisuJson.ElementById(back, outcome.AddedIds[6])!);
        button.Should().ContainKey(VisuCatalog.TextColorVariable);
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
        var doc = VisuDocumentTests.Fixture("Rectangle.TcVIS");
        int src = VisuEditor.ElementId(doc.Elements[0])!.Value;
        VisuEditor.ApplyPage(doc, Json($$"""
            { "elements": [
              { "copyFrom": {{src}} },
              { "copyFrom": {{src}}, "x": 320, "text": "Copy %d" } ] }
            """));
        var back = RoundTrip(doc);
        var copies = back.Elements.Select(VisuElements.Read).ToList();
        copies.Should().HaveCount(2).And.OnlyContain(c => c.Def!.Type == "Rectangle");
        copies[0].Properties.Single(p => p.Key == "text").Value.Should().Be("Count %d");
        copies[1].Properties.Single(p => p.Key == "text").Value.Should().Be("Copy %d");
        copies[1].Properties.Single(p => p.Key == "x").Value.Should().Be(320L);
        copies.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        back.Elements.Select(e => VisuDocument.Field(e, "VisualElementIdentification")!.Value).Should().OnlyHaveUniqueItems();
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
        ApplyOps(doc, $$"""[ { "op": "update", "id": {{id}}, "set": { "text": null, "invisibleVariable": "", "fillColor": null } } ]""");
        var back = RoundTrip(doc);
        var members = VisuElements.Members(back.Elements[0]);
        members.Should().NotContainKey(VisuCatalog.TextMember);
        members.Should().NotContainKey(VisuCatalog.TextId);
        members.Should().NotContainKey(VisuCatalog.InvisibleVariable);
        members.Should().NotContainKey(VisuCatalog.FillColor);
        members.Should().ContainKey(571893170u, "a member the catalog does not know (here the tooltip) stays");
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
    public void Line_GetsThePointListInTheTypeNamesOfTheFile()
    {
        // This archive names the hashtable type of the point list "CaseInsensitiveHashtable_1" and gives the usual name
        // to another type id; the inserted point list must use the file's names.
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        doc.EnsureTypes(new Dictionary<string, string>
        {
            ["CaseInsensitiveHashtable"] = "{7df88604-7ac5-4e36-91c4-55e4fdad3e68}",
            ["CaseInsensitiveHashtable_1"] = "{02a85e84-ef2d-46fc-93f2-acb0bbff3eda}",
        });
        ApplyOps(doc, """[ { "op": "add", "element": { "type": "Line", "x": 0, "y": 0, "width": 10, "height": 10 } } ]""");
        var back = RoundTrip(doc);
        VisuEditor.WriteProblem(back).Should().BeNull();
        var points = VisuElements.Members(back.Elements[0])[VisuCatalog.LinePoints];
        points.Descendants("d").Select(d => (string?)d.Attribute("t")).Distinct().Should().Equal("CaseInsensitiveHashtable_1");
        back.TypeList.Elements("Type").Single(x => (string?)x.Attribute("n") == "StructuredTypeNode").Value
            .Should().Be("{503c5b2e-e80e-4ee7-ae00-c5b93a62b1aa}");
    }

    [Fact]
    public void Line_TypeNameTakenByAnotherType_IsAddedUnderANumberedName()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        doc.EnsureTypes(new Dictionary<string, string> { ["CaseInsensitiveHashtable"] = "{7df88604-7ac5-4e36-91c4-55e4fdad3e68}" });
        ApplyOps(doc, """[ { "op": "add", "element": { "type": "Line", "x": 0, "y": 0, "width": 10, "height": 10 } } ]""");
        var back = RoundTrip(doc);
        VisuEditor.WriteProblem(back).Should().BeNull();
        var types = back.TypeList.Elements("Type").ToDictionary(x => (string)x.Attribute("n")!, x => x.Value);
        types["CaseInsensitiveHashtable_1"].Should().Be("{02a85e84-ef2d-46fc-93f2-acb0bbff3eda}");
        types["CaseInsensitiveHashtable"].Should().Be("{7df88604-7ac5-4e36-91c4-55e4fdad3e68}");
    }

    [Fact]
    public void Line_UpdateOfAnXaeLine_KeepsItsPointListAndMovesItsPoints()
    {
        var doc = VisuDocumentTests.Fixture("Line.TcVIS");
        int id = VisuEditor.ElementId(doc.Elements[0])!.Value;
        string pointsBefore = VisuElements.Members(doc.Elements[0])[VisuCatalog.LinePoints].ToString(SaveOptions.DisableFormatting);
        ApplyOps(doc, $$"""[ { "op": "update", "id": {{id}}, "set": { "y": 20, "height": -10, "lineColor": "#00FF00" } } ]""");
        var back = RoundTrip(doc);
        var members = VisuElements.Members(back.Elements[0]);
        members[VisuCatalog.LinePoints].ToString(SaveOptions.DisableFormatting).Should().Be(pointsBefore);
        back.Serialize().Split("<v n=\"Id\">1051212449L</v>").Should().HaveCount(2, "the line keeps exactly one point list");
        VisuDocument.NumberValue(members[VisuCatalog.LineStartY]).Should().Be(20);
        VisuDocument.NumberValue(members[VisuCatalog.LineEndY]).Should().Be(10);
        Props(back, id)["lineColor"].Should().Be("#FF00FF00");
    }

    [Fact]
    public void Edit_InterfaceOnly()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyOperations(doc, Json("[]"), "VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
        RoundTrip(doc).InterfaceText.Should().Be("VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
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
    [InlineData("""[ { "op": "update", "id": 8, "set": { "x": null } } ]""", "cannot be null")]
    [InlineData("""[ { "op": "update", "id": 8, "set": { "type": "Ellipse" } } ]""", "cannot be changed")]
    [InlineData("""[ { "op": "update", "id": 8, "set": {} } ]""", "names no property")]
    [InlineData("""[ { "op": "update", "id": 8, "set": { "tooltip": "x" } } ]""", "Rectangle has no property 'tooltip'")]
    [InlineData("""[ { "op": "update", "id": 8, "set": { "tapVariable": "x" } } ]""", "Rectangle has no property 'tapVariable'")]
    [InlineData("""[ { "op": "update", "id": 8, "set": { "frameParameters": { "fb": "x" } } } ]""", "Rectangle has no property 'frameParameters'")]
    [InlineData("""[ { "op": "update", "id": 4, "set": { "y": 20 } } ]""", "read but do not change")]
    [InlineData("""[ { "op": "add", "element": { "type": "Label", "x": 0, "y": 0, "width": 1, "height": 1, "fillColor": "#FFFFFF" } } ]""", "Label has no property 'fillColor'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Label", "x": 0, "y": 0, "width": 1, "height": 1, "textVariable": "a" } } ]""", "Label has no property 'textVariable'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Button", "x": 0, "y": 0, "width": 1, "height": 1, "fillColor": "#FFFFFF" } } ]""", "Button has no property 'fillColor'")]
    [InlineData("""[ { "op": "add", "element": { "type": "TextField", "x": 0, "y": 0, "width": 1, "height": 1, "moveXVariable": "a" } } ]""", "TextField has no property 'moveXVariable'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Line", "x": 0, "y": 0, "width": 1, "height": 1, "frameColor": "#FFFFFF" } } ]""", "Line has no property 'frameColor'")]
    [InlineData("""[ { "op": "remove", "id": 4 }, { "op": "remove", "id": 4 } ]""", "named twice")]
    [InlineData("""[ { "op": "move", "id": 4 } ]""", "unknown op 'move'")]
    [InlineData("""[ { "op": "remove", "id": 4, "set": {} } ]""", "'set' does not belong")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 7 } } ]""", "copy only the types they create")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 4 } } ]""", "copy only the types they create")]
    [InlineData("""[ { "op": "add", "element": { "copyFrom": 8, "type": "Label" } } ]""", "does not match")]
    [InlineData("[]", "Nothing to do")]
    [InlineData("{}", "must be a JSON array")]
    public void Edit_InvalidRequests_AreRefusedWithoutAChange(string ops, string expected)
    {
        // Frame 4 (read only), Table 7 (unsupported), Rectangle 8.
        var doc = RoundTrip(ApplyOps(EmptyWith(Table, Frame), """[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10 } } ]"""));
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
