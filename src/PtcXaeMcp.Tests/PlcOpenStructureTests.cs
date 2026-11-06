using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_plc_move's XML work: the ProjectStructure of a PLCopen export rewritten so the import places the object (and
// a member inside it) in another folder. Shaped like a real export of TC 3.1.4026.22 (an FB with a member folder).
public class PlcOpenStructureTests
{
    private const string Head =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<project xmlns=\"http://www.plcopen.org/xml/tc6_0200\">\n" +
        "  <types>\n    <pous>\n      <pou name=\"FB_X\" pouType=\"functionBlock\">\n        <interface />\n      </pou>\n    </pous>\n  </types>\n" +
        "  <addData>\n    <data name=\"http://www.3s-software.com/plcopenxml/projectstructure\" handleUnknown=\"discard\">\n      ";

    private const string Tail = "\n    </data>\n  </addData>\n</project>";

    private const string Structure =
        "<ProjectStructure>\n" +
        "        <Object Name=\"FB_X\" ObjectId=\"04d45a1f-b0f0-4187-8258-5f6be033085c\">\n" +
        "          <Folder Name=\"Members\">\n" +
        "            <Object Name=\"Reset\" ObjectId=\"b53af92c-60bb-48c4-aaa2-8bfb7cf4bdfc\" />\n" +
        "            <Object Name=\"Enabled\" ObjectId=\"7b8e93e3-8545-4177-ac94-4092230dbcc1\" />\n" +
        "          </Folder>\n" +
        "          <Object Name=\"Run\" ObjectId=\"11111111-2222-3333-4444-555555555555\" />\n" +
        "        </Object>\n" +
        "      </ProjectStructure>";

    private static string Export(string structure = Structure) => Head + structure + Tail;

    private static System.Xml.Linq.XElement ParsedStructure(string xml)
    {
        int start = xml.IndexOf("<ProjectStructure", StringComparison.Ordinal);
        int end = xml.IndexOf("</ProjectStructure>", StringComparison.Ordinal) + "</ProjectStructure>".Length;
        return System.Xml.Linq.XElement.Parse(xml[start..end]);
    }

    // The chain of Folder/Object names from the structure down to the object named name.
    private static string Where(string xml, string name)
    {
        var o = ParsedStructure(xml).Descendants("Object").Single(e => (string?)e.Attribute("Name") == name);
        return string.Join("/", o.AncestorsAndSelf().Reverse().Skip(1).Select(e => $"{e.Name.LocalName}:{(string?)e.Attribute("Name")}"));
    }

    [Fact]
    public void Rewrite_WrapsTheObjectIntoTheFolderChain_AndLeavesTheRestByteForByte()
    {
        string xml = Export();
        var (result, error) = PlcOpenStructure.Rewrite(xml, "FB_X", ["POUs", "Target"]);

        error.Should().BeNull();
        result.Should().StartWith(Head).And.EndWith(Tail);
        Where(result!, "FB_X").Should().Be("Folder:POUs/Folder:Target/Object:FB_X");
        Where(result!, "Reset").Should().Be("Folder:POUs/Folder:Target/Object:FB_X/Folder:Members/Object:Reset");
        PlcOpenStructure.ObjectIds(result!).Ids.Should().BeEquivalentTo(PlcOpenStructure.ObjectIds(xml).Ids);
        System.Xml.Linq.XDocument.Parse(result!).Root!.Name.LocalName.Should().Be("project");
    }

    [Fact]
    public void Rewrite_NoFolders_KeepsTheObjectAtTheProjectRoot()
    {
        var (result, error) = PlcOpenStructure.Rewrite(Export(), "fb_x", []);

        error.Should().BeNull();
        Where(result!, "FB_X").Should().Be("Object:FB_X");
        Where(result!, "Enabled").Should().Be("Object:FB_X/Folder:Members/Object:Enabled");
    }

    [Fact]
    public void Rewrite_MovesAMemberIntoANewNestedFolder_AndWrapsTheObject()
    {
        var move = new PlcOpenStructure.MemberMove("Reset", ["Methods", "Internal"]);
        var (result, error) = PlcOpenStructure.Rewrite(Export(), "FB_X", ["POUs"], move);

        error.Should().BeNull();
        Where(result!, "Reset").Should().Be("Folder:POUs/Object:FB_X/Folder:Methods/Folder:Internal/Object:Reset");
        Where(result!, "Enabled").Should().Be("Folder:POUs/Object:FB_X/Folder:Members/Object:Enabled");
        Where(result!, "Run").Should().Be("Folder:POUs/Object:FB_X/Object:Run");
    }

    [Fact]
    public void Rewrite_MovesAMemberIntoAnExistingFolder_AndOutOfItToThePou()
    {
        var into = PlcOpenStructure.Rewrite(Export(), "FB_X", [], new PlcOpenStructure.MemberMove("run", ["Members"]));
        into.Error.Should().BeNull();
        Where(into.Xml!, "Run").Should().Be("Object:FB_X/Folder:Members/Object:Run");
        ParsedStructure(into.Xml!).Descendants("Folder").Should().ContainSingle("the existing folder is reused, not duplicated");

        var outOf = PlcOpenStructure.Rewrite(Export(), "FB_X", [], new PlcOpenStructure.MemberMove("Reset", []));
        outOf.Error.Should().BeNull();
        Where(outOf.Xml!, "Reset").Should().Be("Object:FB_X/Object:Reset");
        Where(outOf.Xml!, "Enabled").Should().Be("Object:FB_X/Folder:Members/Object:Enabled");
    }

    [Fact]
    public void Rewrite_IsWhitespaceTolerant_CompactAndCrLf()
    {
        string compact = "<ProjectStructure><Object Name=\"FB_X\" ObjectId=\"a\"><Folder Name=\"Members\"><Object Name=\"Reset\" ObjectId=\"b\" />" +
                         "</Folder></Object></ProjectStructure>";
        var (result, error) = PlcOpenStructure.Rewrite(Export(compact), "FB_X", ["POUs"], new PlcOpenStructure.MemberMove("Reset", ["M"]));
        error.Should().BeNull();
        Where(result!, "Reset").Should().Be("Folder:POUs/Object:FB_X/Folder:M/Object:Reset");

        string crlf = Export().Replace("\n", "\r\n", StringComparison.Ordinal);
        var (crlfResult, crlfError) = PlcOpenStructure.Rewrite(crlf, "FB_X", ["POUs", "Target"]);
        crlfError.Should().BeNull();
        crlfResult!.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n", "the file keeps its CRLF line ends");
        crlfResult.Should().StartWith(Head.Replace("\n", "\r\n", StringComparison.Ordinal));
        Where(crlfResult, "FB_X").Should().Be("Folder:POUs/Folder:Target/Object:FB_X");
    }

    [Fact]
    public void Rewrite_ReplacesFolderWrappersTheExportAlreadyHas()
    {
        string wrapped = "<ProjectStructure>\n<Folder Name=\"Old\">\n<Object Name=\"FB_X\" ObjectId=\"a\" />\n</Folder>\n</ProjectStructure>";
        var (result, error) = PlcOpenStructure.Rewrite(Export(wrapped), "FB_X", ["New"]);

        error.Should().BeNull();
        Where(result!, "FB_X").Should().Be("Folder:New/Object:FB_X");
        result.Should().NotContain("Old");
    }

    [Theory]
    [InlineData("FB_X", "Nope", "the member 'Nope' is not in the project structure")]
    [InlineData("FB_Y", null, "holds 'FB_X', not 'FB_Y'")]
    public void Rewrite_UnknownObjectOrMember_IsAnError(string objectName, string? member, string expected)
    {
        var move = member is null ? null : new PlcOpenStructure.MemberMove(member, ["M"]);
        var (result, error) = PlcOpenStructure.Rewrite(Export(), objectName, [], move);

        result.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Theory]
    [InlineData("<project />", "has no project structure")]
    [InlineData("<project><ProjectStructure /></project>", "is empty")]
    [InlineData("<project><ProjectStructure><Object Name=\"A\" /><Object Name=\"B\" /></ProjectStructure></project>", "2 top-level objects")]
    [InlineData("<project><ProjectStructure><Object Name=\"A\"></ProjectStructure></project>", "not readable XML")]
    public void Rewrite_UnusableStructure_IsAnError(string xml, string expected)
    {
        var (result, error) = PlcOpenStructure.Rewrite(xml, "A", []);

        result.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void ObjectIds_KeysAreObjectChainsWithoutFolders_AndCompareFindsChanges()
    {
        var ids = PlcOpenStructure.ObjectIds(Export()).Ids!;
        ids.Keys.Should().BeEquivalentTo("FB_X", "FB_X/Reset", "FB_X/Enabled", "FB_X/Run");
        ids["FB_X/Reset"].Should().Be("b53af92c-60bb-48c4-aaa2-8bfb7cf4bdfc");

        var moved = PlcOpenStructure.Rewrite(Export(), "FB_X", ["POUs"], new PlcOpenStructure.MemberMove("Reset", ["Methods"])).Xml!;
        PlcOpenStructure.CompareIds(ids, PlcOpenStructure.ObjectIds(moved).Ids!).Should().BeNull();

        var changed = PlcOpenStructure.ObjectIds(Export().Replace("b53af92c", "deadbeef", StringComparison.Ordinal)).Ids!;
        PlcOpenStructure.CompareIds(ids, changed).Should().Contain("'FB_X/Reset' has ObjectId deadbeef");
        PlcOpenStructure.ObjectIds("<project />").Error.Should().Contain("no project structure");
    }
}
