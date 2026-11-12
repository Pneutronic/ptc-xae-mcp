using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat.Visu;
using static PtcXaeMcp.Tests.VisuTestElements;

namespace PtcXaeMcp.Tests;

/// <summary>Frames (reference + parameters) and the tap/toggle inputs, on the document (pure).</summary>
public sealed class VisuFrameAndInputTests
{
    private static VisuDocument RoundTrip(VisuDocument doc) => VisuDocument.Parse(doc.Serialize());

    private static readonly VisuReferenceTarget Block = new("VISU_Block", [new VisuInterfaceVariable("fb", "FB_StepChain")]);

    private static readonly VisuReferenceTarget Station = new("VISU_Station",
        [new VisuInterfaceVariable("fbAxis", "FB_Axis"), new VisuInterfaceVariable("bManual", "BOOL")]);

    private static VisuReferenceTarget? Resolve(string name) => name switch
    {
        "VISU_Block" => Block,
        "VISU_Station" => Station,
        _ => null,
    };

    private static VisuDocument Ops(VisuDocument doc, string ops)
    {
        VisuEditor.ApplyOperations(doc, Json(ops), null, Resolve);
        return doc;
    }

    private const string TwoFrames = """
        { "elements": [
          { "type": "Frame", "x": 10, "y": 40, "width": 160, "height": 65, "visualization": "VISU_Block", "parameters": { "fb": "MAIN.fbStepChain" } },
          { "type": "Frame", "x": 200, "y": 40, "width": 160, "height": 65, "visualization": "VISU_Block", "parameters": { "fb": "MAIN.fbStepChain2" } } ] }
        """;

    [Theory]
    [InlineData("fb", 259060178u)]
    [InlineData("HMI", 2501871159u)]
    [InlineData("stVisu", 2537106899u)]
    [InlineData("ePMLState", 3642510270u)]
    public void ParameterId_IsTheCrcOfThePathInTheReferenceList(string name, uint expected) =>
        VisuFrames.ParameterId(name).Should().Be(expected, "ids as XAE writes them for these parameters");

    [Fact]
    public void Crc32_OfKnownMemberPaths() =>
        new[] { Crc32.Of("m_StaticPosition.iX"), Crc32.Of("m_References"), Crc32.Of("pVariable") }
            .Should().Equal(1649127785u, 363316305u, 1186196937u);

    [Fact]
    public void ApplyPage_TwoFramesOfOneBlock_EachWithItsOwnInstance()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json(TwoFrames), Resolve);
        var back = RoundTrip(doc);
        VisuEditor.WriteProblem(back).Should().BeNull();

        var frames = back.Elements.Select(VisuElements.Read).ToList();
        frames.Should().HaveCount(2).And.OnlyContain(f => f.Def!.Type == "Frame");
        frames.Select(f => f.Frame!.Visualizations.Single()).Should().Equal("VISU_Block", "VISU_Block");
        frames.Select(f => f.Frame!.Parameters.Single().Value).Should().Equal("MAIN.fbStepChain", "MAIN.fbStepChain2");
        frames[0].Frame!.Parameters.Single().MemberId.Should().Be(259060178u);
        frames.Should().OnlyContain(f => f.OtherMembers == 0, "the reference list, the first reference and the parameter belong to the frame");

        var members = VisuElements.Members(back.Elements[0]);
        members.Should().ContainKey(VisuCatalog.FrameReferences);
        members.Should().ContainKey(VisuCatalog.FrameFirstReference);
        var list = members[VisuCatalog.FrameReferences];
        list.Descendants("v").Where(v => (string?)v.Attribute("n") == "VisNodeRefs33").Select(v => v.Value).Should().Equal("\"VISU_Block\"");
        list.Descendants("v").Where(v => (string?)v.Attribute("n") == "QualifiedName").Select(v => v.Value).Should().Contain("\"FB_StepChain\"");
        back.TypeList.Elements("Type").Single(t => (string?)t.Attribute("n") == "VisualizationNode").Value
            .Should().Be("{f8db32ff-bdd5-49e9-9014-6d9a6dea5d8c}");
    }

    [Fact]
    public void Frame_UpdateParameters_ChangesMemberAndReference_AndCopyGetsItsOwn()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json(TwoFrames), Resolve);
        doc = RoundTrip(doc);
        int first = VisuEditor.ElementId(doc.Elements[0])!.Value;
        Ops(doc, $$"""
            [ { "op": "update", "id": {{first}}, "set": { "x": 20, "parameters": { "fb": "MAIN.fbOther" } } },
              { "op": "add", "element": { "copyFrom": {{first}}, "x": 400, "parameters": { "fb": "MAIN.fbThird" } } } ]
            """);
        var back = RoundTrip(doc);
        var views = back.Elements.Select(VisuElements.Read).ToList();
        views[0].Frame!.Parameters.Single().Value.Should().Be("MAIN.fbOther");
        views[2].Frame!.Parameters.Single().Value.Should().Be("MAIN.fbThird");
        views[2].Properties.Single(p => p.Key == "x").Value.Should().Be(400L);
        var reference = VisuElements.Members(back.Elements[0])[VisuCatalog.FrameFirstReference];
        reference.Descendants("v").Where(v => (string?)v.Attribute("n") == "BasicTypeNodeValue").Select(v => v.Value).Should().Equal("\"MAIN.fbOther\"");
    }

    [Fact]
    public void Frame_NewVisualization_ReplacesTheReferenceAndItsParameterMembers()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json(TwoFrames), Resolve);
        doc = RoundTrip(doc);
        int first = VisuEditor.ElementId(doc.Elements[0])!.Value;
        Ops(doc, $$"""[ { "op": "update", "id": {{first}}, "set": { "visualization": "VISU_Station", "parameters": { "fbAxis": "MAIN.fbAxis1", "bManual": "MAIN.bMan" } } } ]""");
        var back = RoundTrip(doc);
        var frame = VisuElements.Read(back.Elements[0]).Frame!;
        frame.Visualizations.Should().Equal("VISU_Station");
        frame.Parameters.Select(p => p.Name + "=" + p.Value).Should().Equal("fbAxis=MAIN.fbAxis1", "bManual=MAIN.bMan");
        VisuElements.Members(back.Elements[0]).Should().NotContainKey(259060178u, "the old parameter member goes with the old reference");
    }

    [Fact]
    public void Frame_UpdateOfTheHandWrittenFrame_KeepsItsParameterId()
    {
        // VisuTestElements.Frame stores its parameter under an id of its own (1234567890); an update writes there.
        var doc = EmptyWith(Frame);
        Ops(doc, """[ { "op": "update", "id": 4, "set": { "parameters": { "fb": "MAIN.fbStation3" }, "y": 20 } } ]""");
        var back = RoundTrip(doc);
        VisuElements.Read(back.Elements[0]).Frame!.Parameters.Single().Value.Should().Be("MAIN.fbStation3");
        VisuElements.Members(back.Elements[0]).Should().ContainKey(1234567890u).And.NotContainKey(VisuFrames.ParameterId("fb"));
    }

    [Theory]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10 } } ]""", "needs visualization")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VISU_Block" } } ]""", "needs a value for fb")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VISU_Nope", "parameters": {} } } ]""", "no visualization 'VISU_Nope'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VISU_Block", "parameters": { "fb": "a", "x": "b" } } } ]""", "has no VAR_IN_OUT 'x'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VISU_Block", "parameters": { "fb": "" } } } ]""", "non-empty expression")]
    [InlineData("""[ { "op": "add", "element": { "type": "Frame", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VisuNoMan", "parameters": {} } } ]""", "cannot show the visualization it is on")]
    [InlineData("""[ { "op": "add", "element": { "type": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10, "visualization": "VISU_Block" } } ]""", "belongs to a Frame only")]
    [InlineData("""[ { "op": "update", "id": 4, "set": { "parameters": { "nope": "x" } } } ]""", "has no parameter 'nope'")]
    [InlineData("""[ { "op": "add", "element": { "type": "Label", "x": 0, "y": 0, "width": 10, "height": 10, "tapVariable": "a" } } ]""", "Label has no property 'tapVariable'")]
    [InlineData("""[ { "op": "add", "element": { "type": "TextField", "x": 0, "y": 0, "width": 10, "height": 10, "toggleVariable": "a" } } ]""", "TextField has no property 'toggleVariable'")]
    public void InvalidFrameAndInputRequests_AreRefusedWithoutAChange(string ops, string expected)
    {
        var doc = EmptyWith(Frame);
        string before = doc.Serialize();
        var act = () => VisuEditor.ApplyOperations(doc, Json(ops), null, Resolve);
        act.Should().Throw<VisuRequestException>().Which.Message.Should().Contain(expected);
        doc.Serialize().Should().Be(before);
    }

    [Fact]
    public void Inputs_TapAndToggle_AreWrittenAsComplexInputs_AndRead()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json("""
            { "elements": [
              { "type": "Button", "x": 0, "y": 0, "width": 100, "height": 40, "text": "1 tap", "tapVariable": "GVL.aIn[1]" },
              { "type": "Rectangle", "x": 0, "y": 50, "width": 100, "height": 40, "toggleVariable": "GVL.aIn[2]", "tapVariable": "GVL.aIn[3]" } ] }
            """));
        var back = RoundTrip(doc);
        VisuEditor.WriteProblem(back).Should().BeNull();
        var button = VisuElements.Read(back.Elements[0]).Properties.ToDictionary(p => p.Key, p => p.Value);
        button["tapVariable"].Should().Be("GVL.aIn[1]");
        button.Should().NotContainKey("toggleVariable");
        var rect = VisuElements.Read(back.Elements[1]).Properties.ToDictionary(p => p.Key, p => p.Value);
        rect["toggleVariable"].Should().Be("GVL.aIn[2]");
        rect["tapVariable"].Should().Be("GVL.aIn[3]");

        var inputs = VisuDocument.Field(back.Elements[0], "ConfiguredComplexInputs")!;
        inputs.Attribute("cet")!.Value.Should().Be("ComplexInput");
        inputs.Attribute("et").Should().BeNull();
        var entry = inputs.Elements("o").Single();
        VisuDocument.Field(entry, "SignatureName")!.Value.Should().Be("\"Visu_TapInput\"");
        VisuDocument.Field(entry, "Name")!.Value.Should().Be("\"Tap\"");
        VisuElements.Members(entry)[VisuCatalog.InputVariable].Value.Should().Be("\"GVL.aIn[1]\"");
        VisuElements.Members(back.Elements[0]).Should().NotContainKey(VisuCatalog.TapInput, "inputs are no members");
    }

    [Fact]
    public void Inputs_ChangeKeepsOtherOptions_AndNullRemovesTheEntry()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json("""{ "elements": [ { "type": "Rectangle", "x": 0, "y": 0, "width": 10, "height": 10, "tapVariable": "a", "toggleVariable": "b" } ] }"""));
        doc = RoundTrip(doc);
        // An option XAE wrote (tap FALSE, member 1329445385) stays when the variable changes.
        var tap = VisuDocument.Field(doc.Elements[0], "ConfiguredComplexInputs")!.Elements("o").First();
        VisuElements.MemberList(tap)!.Add(XElement.Parse("<o><v n=\"Id\">1329445385L</v><v n=\"Value\">true</v></o>"));
        int id = VisuEditor.ElementId(doc.Elements[0])!.Value;
        Ops(doc, $$"""[ { "op": "update", "id": {{id}}, "set": { "tapVariable": "c", "toggleVariable": null } } ]""");
        var back = RoundTrip(doc);
        var inputs = VisuDocument.Field(back.Elements[0], "ConfiguredComplexInputs")!;
        inputs.Elements("o").Should().ContainSingle();
        VisuElements.Members(inputs.Elements("o").Single()).Keys.Should().BeEquivalentTo([VisuCatalog.InputVariable, 1329445385u]);
        VisuElements.Read(back.Elements[0]).Properties.Single(p => p.Key == "tapVariable").Value.Should().Be("c");

        Ops(back, $$"""[ { "op": "update", "id": {{id}}, "set": { "tapVariable": "" } } ]""");
        var none = VisuDocument.Field(RoundTrip(back).Elements[0], "ConfiguredComplexInputs")!;
        none.HasElements.Should().BeFalse();
        none.Attribute("et")!.Value.Should().Be("ComplexInput");
    }

    [Theory]
    [InlineData("VAR_IN_OUT\n\tfb : FB_StepChain;\nEND_VAR", "fb:FB_StepChain")]
    [InlineData("VAR_IN_OUT\n\ta, b : INT; // two\n\t(* block *) c : ARRAY[1..3] OF BOOL;\nEND_VAR", "a:INT|b:INT|c:ARRAY[1..3] OF BOOL")]
    [InlineData("VAR_IN_OUT fb : FB_X; END_VAR", "fb:FB_X")]
    [InlineData("VAR_IN_OUT\nEND_VAR", "")]
    [InlineData("", "")]
    public void InterfaceParser_ReadsTheInOutVariables(string text, string expected)
    {
        var (vars, problem) = VisuInterfaceParser.Parse(text);
        problem.Should().BeNull();
        string.Join("|", vars.Select(v => v.Name + ":" + v.Type)).Should().Be(expected);
    }

    [Fact]
    public void InterfaceParser_OtherSections_AreAProblem()
    {
        var (_, problem) = VisuInterfaceParser.Parse("VAR_INPUT\n\tb : BOOL;\nEND_VAR\nVAR_IN_OUT\n\tfb : FB_X;\nEND_VAR");
        problem.Should().Contain("VAR_IN_OUT parameters only");
    }
}
