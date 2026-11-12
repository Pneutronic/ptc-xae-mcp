using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat.Visu;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The .TcVIS format layer of the visu tools (pure, COM-free). The files under Fixtures/Visu were saved by XAE
/// 3.1.4026.22 in a copy of the neutral fixture; a page read and written back unchanged must give the same bytes.
/// </summary>
public sealed class VisuDocumentTests
{
    internal static string FixtureText(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Visu", name)));

    internal static VisuDocument Fixture(string name) => VisuDocument.Parse(FixtureText(name));

    public static TheoryData<string> AllFixtures() => new(
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Visu"), "*.TcVIS").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void UnchangedPage_SerializesToTheSameText(string file)
    {
        string text = FixtureText(file);
        VisuDocument.Parse(text).Serialize().Should().Be(text);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void XaeSavedFiles_AreWritable(string file) =>
        VisuEditor.WriteProblem(Fixture(file)).Should().BeNull();

    [Fact]
    public void Header_IsRead()
    {
        var doc = Fixture("Rectangle.TcVIS");
        doc.ObjectVersion.Should().Be("1.1.0.1");
        doc.ProductVersion.Should().BeNull("XAE 4026 does not write the product version by default");
        doc.ObjectId.Should().StartWith("{").And.EndWith("}");
        doc.InterfaceText.Should().StartWith("VAR_IN_OUT").And.EndWith("END_VAR");
    }

    [Fact]
    public void NotAVisualization_IsRefused()
    {
        var act = () => VisuDocument.Parse("<?xml version=\"1.0\"?><TcPlcObject Version=\"1.1.0.1\"><POU Name=\"MAIN\" /></TcPlcObject>");
        act.Should().Throw<VisuFormatException>().WithMessage("*<Visu>*");
        var broken = () => VisuDocument.Parse("<TcPlcObject><Visu");
        broken.Should().Throw<VisuFormatException>();
        var empty = () => VisuDocument.Parse("");
        empty.Should().Throw<VisuFormatException>();
    }

    [Fact]
    public void Interface_IsReplacedInTheBlobForm()
    {
        var doc = Fixture("Empty.TcVIS");
        doc.SetInterfaceText("VAR_IN_OUT\r\n\tnValue : INT;\r\nEND_VAR");
        var back = VisuDocument.Parse(doc.Serialize());
        back.InterfaceText.Should().Be("VAR_IN_OUT\n\tnValue : INT;\nEND_VAR");
        back.Serialize().Should().Contain("\tnValue : INT;\r\nEND_VAR\"</v>", "the file keeps its CRLF line ends");
    }

    [Fact]
    public void Interface_InTheOlderLineForm_IsReadAndWritten()
    {
        var doc = VisuDocument.Parse(OlderFormat);
        doc.InterfaceText.Should().Be("VAR_IN_OUT\n\t\nEND_VAR");
        doc.SetInterfaceText("VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
        var back = VisuDocument.Parse(doc.Serialize());
        back.InterfaceText.Should().Be("VAR_IN_OUT\n\tb : BOOL;\nEND_VAR");
        VisuEditor.WriteProblem(back).Should().BeNull();
    }

    [Fact]
    public void WriteProblem_RefusesUnverifiedVersionsAndUndeclaredTypes()
    {
        VisuEditor.WriteProblem(VisuDocument.Parse(OlderFormat)).Should().BeNull("4024 files are verified");
        VisuEditor.WriteProblem(VisuDocument.Parse(OlderFormat.Replace("3.1.4024.11", "3.1.4022.6", StringComparison.Ordinal)))
            .Should().Contain("3.1.4022.6");
        VisuEditor.WriteProblem(VisuDocument.Parse(OlderFormat.Replace("Version=\"1.1.0.1\"", "Version=\"1.2.0.0\"", StringComparison.Ordinal)))
            .Should().Contain("1.2.0.0");
        VisuEditor.WriteProblem(VisuDocument.Parse(OlderFormat.Replace("<Type n=\"TextLine\">{a5de0b0b-1cb5-4913-ac21-9d70293ec00d}</Type>", "", StringComparison.Ordinal)))
            .Should().Contain("TextLine");
    }

    [Fact]
    public void WriteProblem_RefusesDuplicateElementIds()
    {
        var doc = VisuTestElements.EmptyWith(VisuTestElements.Table, VisuTestElements.Table);
        VisuEditor.WriteProblem(doc).Should().Be("element id 7 occurs 2 times");
    }

    [Fact]
    public void EnsureTypes_AddsMissingEntriesInNameOrder()
    {
        var doc = Fixture("Empty.TcVIS");
        doc.EnsureTypes(VisuCatalog.ElementTypes);
        var names = doc.TypeList.Elements("Type").Select(t => (string)t.Attribute("n")!).ToList();
        names.Should().BeInAscendingOrder(StringComparer.Ordinal);
        names.Should().Contain(VisuCatalog.ElementTypes.Keys);
        var back = VisuDocument.Parse(doc.Serialize());
        back.UndeclaredTypes().Should().BeEmpty();
    }

    /// <summary>A visualization in the serialization TwinCAT 3.1.4024 writes (interface as text lines, a size table), written by hand.</summary>
    internal const string OlderFormat =
        "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<TcPlcObject Version=\"1.1.0.1\" ProductVersion=\"3.1.4024.11\">\r\n" +
        "  <Visu Name=\"VISU_Old\" Id=\"{3b0c2a61-6f0e-4b8e-9b1a-0d4b7f2a9c11}\">\r\n" +
        "    <XmlArchive>\r\n" +
        "      <Data>\r\n" +
        "        <o xml:space=\"preserve\" t=\"VisualObject\">\r\n" +
        "          <v n=\"UniqueIdGenerator\">\"3\"</v>\r\n" +
        "          <o n=\"VisualElemList\" t=\"VisualElemList\">\r\n" +
        "            <l n=\"VisualElementList\" t=\"VisualElemCollection\" />\r\n" +
        "          </o>\r\n" +
        "          <o n=\"TextDocument\" t=\"TextDocument\">\r\n" +
        "            <a n=\"TextLines\" cet=\"TextLine\">\r\n" +
        "              <o>\r\n" +
        "                <v n=\"Id\">2L</v>\r\n" +
        "                <n n=\"Tag\" />\r\n" +
        "                <v n=\"Text\">\"VAR_IN_OUT\"</v>\r\n" +
        "              </o>\r\n" +
        "              <o>\r\n" +
        "                <v n=\"Id\">3L</v>\r\n" +
        "                <n n=\"Tag\" />\r\n" +
        "                <v n=\"Text\">\"\t\"</v>\r\n" +
        "              </o>\r\n" +
        "              <o>\r\n" +
        "                <v n=\"Id\">1L</v>\r\n" +
        "                <n n=\"Tag\" />\r\n" +
        "                <v n=\"Text\">\"END_VAR\"</v>\r\n" +
        "              </o>\r\n" +
        "            </a>\r\n" +
        "          </o>\r\n" +
        "        </o>\r\n" +
        "      </Data>\r\n" +
        "      <TypeList>\r\n" +
        "        <Type n=\"Int64\">System.Int64</Type>\r\n" +
        "        <Type n=\"String\">System.String</Type>\r\n" +
        "        <Type n=\"TextDocument\">{f3878285-8e4f-490b-bb1b-9acbb7eb04db}</Type>\r\n" +
        "        <Type n=\"TextLine\">{a5de0b0b-1cb5-4913-ac21-9d70293ec00d}</Type>\r\n" +
        "        <Type n=\"VisualElemCollection\">{ef9d0b20-c96e-48db-b361-2ded4063150e}</Type>\r\n" +
        "        <Type n=\"VisualElemList\">{f285c9a3-7019-446b-b98c-ccec3a0af8fa}</Type>\r\n" +
        "        <Type n=\"VisualObject\">{f18bec89-9fef-401d-9953-2f11739a6808}</Type>\r\n" +
        "      </TypeList>\r\n" +
        "    </XmlArchive>\r\n" +
        "  </Visu>\r\n" +
        "</TcPlcObject>";
}

/// <summary>twincat_visu_list_element_types: the catalog as JSON.</summary>
public sealed class VisuCatalogTests
{
    [Fact]
    public void Catalog_NamesEveryTypeAndProperty()
    {
        using var doc = JsonDocument.Parse(VisuJson.Catalog());
        var types = doc.RootElement.GetProperty("types").EnumerateArray().ToList();
        types.Select(t => t.GetProperty("type").GetString()).Should().Equal(
            "Rectangle", "RoundedRectangle", "Ellipse", "Line", "Label", "TextField", "Button", "Polygon", "Frame");
        types.Where(t => !t.GetProperty("create").GetBoolean()).Select(t => t.GetProperty("type").GetString()).Should().Equal("Polygon", "Frame");
        doc.RootElement.GetProperty("properties").GetArrayLength().Should().Be(VisuCatalog.Properties.Count);
        doc.RootElement.GetProperty("notSupported").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public void EveryTypeHasGeometryTooltipAndInvisible_AndOnlyKnownProperties()
    {
        foreach (var t in VisuCatalog.Types)
        {
            t.Members.Keys.Should().Contain(["x", "y", "width", "height", "tooltip", "invisibleVariable"], t.Type);
            t.Members.Keys.Should().OnlyContain(k => VisuCatalog.Property(k) != null, t.Type);
        }
        VisuCatalog.TypeByApiName("button")!.Members["tapVariable"].Should().Be(VisuCatalog.ButtonTapVariable);
        VisuCatalog.TypeByApiName("Rectangle")!.Members["tapVariable"].Should().Be(VisuCatalog.TapVariable);
    }
}

/// <summary>Reading single elements (types, properties, colours, frames, unsupported elements).</summary>
public sealed class VisuElementReadTests
{
    private static VisuElementView Only(string fixture) =>
        VisuElements.Read(VisuDocumentTests.Fixture(fixture).Elements.Single());

    [Fact]
    public void Rectangle_WithEveryCatalogProperty()
    {
        var e = Only("Rectangle.TcVIS");
        e.Supported.Should().BeTrue();
        e.Def!.Type.Should().Be("Rectangle");
        var p = e.Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
        p["x"].Should().Be(10L);
        p["y"].Should().Be(10L);
        p["width"].Should().Be(120L);
        p["height"].Should().Be(40L);
        p["text"].Should().Be("Count %d");
        p["textVariable"].Should().Be("GVL_Fixture.nCount");
        p["tooltip"].Should().Be("rect tip");
        p["fillColor"].Should().Be("#FFFFFF00", "a list of UInt32 is read as its first value");
        p["frameColor"].Should().Be("#FF000000");
        p["toggleColorVariable"].Should().Be("GVL_Fixture.bEnable");
        p["invisibleVariable"].Should().Be("NOT GVL_Fixture.bEnable");
        p["moveXVariable"].Should().Be("GVL_Fixture.nCount");
        p["moveYVariable"].Should().Be("GVL_Fixture.nCount");
        p["rotationVariable"].Should().Be("GVL_Fixture.nCount");
        e.Identifier.Should().Be("GenElemInst_" + e.Id);
    }

    [Fact]
    public void AllTypes_AsXaeSavedThePageTheToolsWrote()
    {
        // Written with twincat_visu_apply/edit_elements, then saved by XAE 4026.22 itself (it adds its defaults,
        // replaces the text-list id placeholders and appends AnimationDuration … to each element).
        var doc = VisuDocumentTests.Fixture("AllTypes.TcVIS");
        var views = doc.Elements.Select(VisuElements.Read).ToList();
        views.Select(v => v.Def?.Type).Should().Equal(
            "Rectangle", "RoundedRectangle", "Ellipse", "Label", "TextField", "Button", "Label", "Button");
        doc.InterfaceText.Should().Be("VAR_IN_OUT\n\tnLevel : INT;\n\tbFlag : BOOL;\nEND_VAR");
        var rect = views[0].Properties.ToDictionary(p => p.Key, p => p.Value);
        rect["text"].Should().Be("Level %d");
        rect["textVariable"].Should().Be("nLevel");
        rect["moveYVariable"].Should().Be("nLevel");
        rect["fillColor"].Should().Be("#FFC0FFC0");
        views[6].Properties.Single(p => p.Key == "text").Value.Should().Be("Edited while open");
        VisuElements.Members(doc.Elements[5]).Should().ContainKey(VisuCatalog.ButtonTapVariable);
    }

    [Fact]
    public void Line_IsReadWithItsGeometry_AndItsPointsAreNotOtherMembers()
    {
        var e = Only("Line.TcVIS");
        e.Def!.Type.Should().Be("Line");
        var p = e.Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
        p["x"].Should().Be(150L);
        p["width"].Should().Be(100L);
        p["height"].Should().Be(50L);
    }

    [Fact]
    public void Button_WithTextAndTapVariable()
    {
        var e = Only("Button.TcVIS");
        e.Def!.Type.Should().Be("Button");
        var p = e.Properties.ToDictionary(kv => kv.Key, kv => kv.Value);
        p["text"].Should().Be("Press");
        p["tapVariable"].Should().Be("GVL_Fixture.bEnable");
    }

    [Theory]
    [InlineData("VisuFbElemSimple", "VISU_ST_RECTANGLE", "Rectangle")]
    [InlineData("VISUFBELEMSIMPLE", "VISU_ST_RECTANGLE", "Rectangle")]
    [InlineData("VisuFbElemSimple", "VISU_ST_ROUNDRECT", "RoundedRectangle")]
    [InlineData("VISUFBELEMSIMPLE", "VISU_ST_CIRCLE", "Ellipse")]
    [InlineData("VISUFBFRAME", null, "Frame")]
    [InlineData("VisuFbElemPolygon", null, "Polygon")]
    public void Identify_AcceptsBothTypeNameSpellings(string typeName, string? shape, string expected) =>
        VisuCatalog.Identify(typeName, shape)!.Type.Should().Be(expected);

    [Theory]
    [InlineData("VisuFbElemTable", null)]
    [InlineData("VisuFbElemSimple", "VISU_ST_STYLE")]
    [InlineData("", null)]
    public void Identify_UnknownIsNull(string typeName, string? shape) =>
        VisuCatalog.Identify(typeName, shape).Should().BeNull();

    [Fact]
    public void UnsupportedElement_IsReportedWithTypeAndId()
    {
        var e = VisuElements.Read(XElement.Parse(VisuTestElements.Table));
        e.Supported.Should().BeFalse();
        e.Id.Should().Be(7);
        e.ElementName.Should().Be("Table");
        e.TypeName.Should().Be("VisuFbElemTable");
        e.Properties.Should().BeEmpty();
        e.OtherMembers.Should().Be(2);
    }

    [Theory]
    [InlineData("<v n=\"Value\">4294901760U</v>", "#FFFF0000")]
    [InlineData("<l n=\"Value\" t=\"ArrayList\" cet=\"UInt32\"><v>4278255360</v></l>", "#FF00FF00")]
    [InlineData("<l n=\"Value\" t=\"ArrayList\" cet=\"NamedStyleColor\"><o><v n=\"Color\">-16777216</v><v n=\"CanonicalName\">\"x\"</v></o></l>", "#FF000000")]
    [InlineData("<o n=\"Value\" t=\"NamedStyleColor\"><v n=\"Color\">-1</v></o>", "#FFFFFFFF")]
    public void Colors_InEveryStoredForm(string node, string expected) =>
        VisuElements.ReadValue(VisuValueKind.Color, XElement.Parse(node)).Should().Be(expected);

    [Theory]
    [InlineData("#FF0000", 0xFFFF0000u)]
    [InlineData("#80FF0000", 0x80FF0000u)]
    [InlineData(" #00ff00 ", 0xFF00FF00u)]
    public void ParseColor_RgbAndArgb(string text, uint expected) => VisuElements.ParseColor(text).Should().Be(expected);

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("FF0000")]
    public void ParseColor_RefusesOtherText(string text) => VisuElements.ParseColor(text).Should().BeNull();

    [Fact]
    public void Frame_ReferencesAndParameters()
    {
        var e = VisuElements.Read(XElement.Parse(VisuTestElements.Frame));
        e.Def!.Type.Should().Be("Frame");
        e.Frame!.Visualizations.Should().Equal("VISU_Station");
        e.Frame.Parameters.Should().ContainSingle();
        e.Frame.Parameters[0].Name.Should().Be("fb");
        e.Frame.Parameters[0].Value.Should().Be("MAIN.fbStation1");
        e.OtherMembers.Should().Be(0, "the reference node and the parameter member belong to the frame view");
    }
}

/// <summary>Elements written by hand for the tests (neutral names, format as XAE writes it).</summary>
internal static class VisuTestElements
{
    public const string Table =
        "<o><a n=\"ConfiguredComplexInputs\" et=\"ComplexInput\" /><l n=\"Elements\" t=\"ArrayList\" /><n n=\"VisualElementDescription\" />" +
        "<o n=\"VisualElemMemberList\" t=\"VisualElemMemberList\"><l n=\"VisualElemMemberList\" t=\"VisualElemMemberCollection\" cet=\"VisualElemMember\">" +
        "<o><v n=\"Id\">1649127785L</v><v n=\"Value\">5</v></o><o><v n=\"Id\">72808681L</v><v n=\"Value\">\"GVL_Fixture.aNames\"</v></o>" +
        "</l></o><v n=\"VisualElementName\">\"Table\"</v><v n=\"VisualElementTypeName\">\"VisuFbElemTable\"</v>" +
        "<v n=\"VisualElementIsRectangle\">true</v><v n=\"VisualElementIdentifier\">\"GenElemInst_7\"</v><n n=\"VisualElementOfflinePaintCommands\" />" +
        "<n n=\"VisualElementFrameInformation\" /><d n=\"VisualElementInputActions\" t=\"Hashtable\" />" +
        "<v n=\"VisualElementIdentification\">{6d3b4c55-8a1e-4f3a-9a10-2b7c0e9d4f01}</v><v n=\"VisualElementOwningObjectGuid\">{00000000-0000-0000-0000-000000000001}</v>" +
        "<a n=\"LMGuids\" et=\"Guid\" /><d n=\"SubElements\" t=\"Hashtable\" /><v n=\"VisualElementId\">7</v><l n=\"UserManagementAccessRights\" t=\"ArrayList\" /></o>";

    public const string Frame =
        "<o><a n=\"ConfiguredComplexInputs\" et=\"ComplexInput\" /><l n=\"Elements\" t=\"ArrayList\" /><n n=\"VisualElementDescription\" />" +
        "<o n=\"VisualElemMemberList\" t=\"VisualElemMemberList\"><l n=\"VisualElemMemberList\" t=\"VisualElemMemberCollection\" cet=\"VisualElemMember\">" +
        "<o><v n=\"Id\">1649127785L</v><v n=\"Value\">200</v></o><o><v n=\"Id\">357335551L</v><v n=\"Value\">10</v></o>" +
        "<o><v n=\"Id\">2422045748L</v><v n=\"Value\">300</v></o><o><v n=\"Id\">2134141914L</v><v n=\"Value\">150</v></o>" +
        "<o><v n=\"Id\">2473092364L</v><o n=\"Value\" t=\"VisualizationNode\"><v n=\"VisuNodeReferenceGuid\">{00000000-0000-0000-0000-000000000000}</v>" +
        "<n n=\"VisuNodeReference\" /><v n=\"VisNodeRefs33\">\"VISU_Station\"</v><l n=\"TypeNodeChildren\" t=\"ArrayList\" cet=\"BasicTypeNode\">" +
        "<o><v n=\"Flags\">2L</v><v n=\"BasicTypeNodeValue\">\"MAIN.fbStation1\"</v><v n=\"BasicTypeNodeAcceptsExpression\">true</v>" +
        "<v n=\"TypeNodeName\">\"fb\"</v><v n=\"TypeNodeId\" t=\"Int16\">255</v><v n=\"TypeNodeIdLong\">1234567890L</v></o></l>" +
        "<v n=\"TypeNodeName\">\"[0]\"</v><v n=\"TypeNodeIdLong\">2473092364L</v></o></o>" +
        "<o><v n=\"Id\">1234567890L</v><v n=\"Value\">\"MAIN.fbStation1\"</v></o>" +
        "</l></o><v n=\"VisualElementName\">\"Frame\"</v><v n=\"VisualElementTypeName\">\"VisuFbFrame\"</v>" +
        "<v n=\"VisualElementIsRectangle\">true</v><v n=\"VisualElementIdentifier\">\"GenElemInst_4\"</v><n n=\"VisualElementOfflinePaintCommands\" />" +
        "<o n=\"VisualElementFrameInformation\" t=\"VisualFrameInfo\"><l n=\"ContainedGuids\" t=\"ArrayList\" /><l n=\"ContainedVisualizations\" t=\"ArrayList\" />" +
        "<a n=\"ContainedVisualizations33\" cet=\"String\"><v>VISU_Station</v></a></o><d n=\"VisualElementInputActions\" t=\"Hashtable\" />" +
        "<v n=\"VisualElementIdentification\">{1f0e2d3c-4b5a-4968-8776-a5b4c3d2e1f0}</v><v n=\"VisualElementOwningObjectGuid\">{00000000-0000-0000-0000-000000000001}</v>" +
        "<a n=\"LMGuids\" et=\"Guid\" /><d n=\"SubElements\" t=\"Hashtable\" /><v n=\"VisualElementId\">4</v><l n=\"UserManagementAccessRights\" t=\"ArrayList\" /></o>";

    /// <summary>The Empty fixture with <paramref name="elements"/> put into its element list (and the types they use declared).</summary>
    public static VisuDocument EmptyWith(params string[] elements)
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        doc.EnsureTypes(VisuCatalog.ElementTypes);
        doc.EnsureTypes(new Dictionary<string, string>
        {
            ["BasicTypeNode"] = "{f7e1e748-ea0f-4fcb-b563-94837ee17e8d}",
            ["Int16"] = "System.Int16",
            ["String"] = "System.String",
            ["VisualFrameInfo"] = "{7fd6515d-f891-4717-b53f-b14197c6706c}",
            ["VisualizationNode"] = "{f8db32ff-bdd5-49e9-9014-6d9a6dea5d8c}",
        });
        foreach (var e in elements) doc.AddElement(XElement.Parse(e));
        return VisuDocument.Parse(doc.Serialize());
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
