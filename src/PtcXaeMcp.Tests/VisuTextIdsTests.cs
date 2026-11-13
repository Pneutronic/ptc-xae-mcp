using System.Text;
using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat.Visu;
using static PtcXaeMcp.Tests.VisuTestElements;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Text-list ids of static texts. At runtime an element shows the global text list's text under its id; XAE keeps the
/// ids of a taken-over file when the project is saved. With "0" on every element, every element showed the text of
/// id 0 after the project was opened again. These tests resolve each element's text the way the reopened project does.
/// </summary>
public sealed class VisuTextIdsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "visu-textids-" + Guid.NewGuid().ToString("N"));

    public VisuTextIdsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Page = """
        { "elements": [
            { "type": "Label",     "x": 0, "y": 0,  "width": 200, "height": 20, "text": "Title" },
            { "type": "Button",    "x": 0, "y": 30, "width": 80,  "height": 20, "text": "Start" },
            { "type": "Button",    "x": 0, "y": 60, "width": 80,  "height": 20, "text": "Stop" },
            { "type": "Rectangle", "x": 0, "y": 90, "width": 80,  "height": 20, "text": "%d", "textVariable": "GVL.n" },
            { "type": "Label",     "x": 90, "y": 0, "width": 80,  "height": 20, "text": "Start" },
            { "type": "Ellipse",   "x": 0, "y": 120, "width": 20, "height": 20 }
        ] }
        """;

    private static VisuDocument BuiltPage()
    {
        var doc = VisuDocumentTests.Fixture("Empty.TcVIS");
        VisuEditor.ApplyPage(doc, Json(Page));
        return doc;
    }

    private static string TextList(params (string Id, string Text)[] rows)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><TcPlcObject Version=\"1.1.0.1\"><GlobalTextList Name=\"GlobalTextList\" Id=\"{00000000-0000-0000-0000-000000000001}\">");
        sb.Append("<XmlArchive><Data><o xml:space=\"preserve\" t=\"GlobalTextListObject\"><l n=\"TextList\" t=\"ArrayList\" cet=\"TextListRow\">");
        foreach (var (id, text) in rows)
            sb.Append("<o><v n=\"TextID\">\"").Append(id).Append("\"</v><v n=\"TextDefault\">\"").Append(text)
              .Append("\"</v><l n=\"LanguageTexts\" t=\"ArrayList\" /></o>");
        sb.Append("</l></o></Data></XmlArchive></GlobalTextList></TcPlcObject>");
        return sb.ToString();
    }

    /// <summary>
    /// The texts the elements show after the project is opened again: the text list's text under the element's id;
    /// an id the list does not have is filed with the text of the first element that carries it.
    /// </summary>
    private static List<string> ShownAfterReopen(VisuDocument doc, VisuTextIds.Known known)
    {
        var list = new Dictionary<string, string>(known.TextById);
        var page = VisuTextIds.Texts(VisuDocument.Parse(doc.Serialize())).ToList();
        foreach (var (text, id) in page) list.TryAdd(id!, text);
        return page.Select(t => list[t.Id!]).ToList();
    }

    private static List<string> OwnTexts(VisuDocument doc) => VisuTextIds.Texts(doc).Select(t => t.Text).ToList();

    [Fact]
    public void WithoutAssign_EveryTextHasIdZero_AndAllShowTheFirstText()
    {
        // The defect as it was: the placeholder "0" on every text.
        var doc = BuiltPage();
        VisuTextIds.Texts(doc).Select(t => t.Id).Should().OnlyContain(id => id == "0");
        ShownAfterReopen(doc, VisuTextIds.Known.Empty).Should().OnlyContain(t => t == "Title");
    }

    [Fact]
    public void Assign_GivesEveryDistinctTextItsOwnId_AndTheSameTextSharesOne()
    {
        var doc = BuiltPage();
        var lines = VisuTextIds.Assign(doc, VisuTextIds.Known.Empty);

        var texts = VisuTextIds.Texts(VisuDocument.Parse(doc.Serialize())).ToList();
        texts.Should().HaveCount(5, "the ellipse has no text");
        texts.Select(t => t.Id).Should().OnlyContain(id => id != null);
        texts.GroupBy(t => t.Text).Should().OnlyContain(g => g.Select(t => t.Id).Distinct().Count() == 1);
        texts.Select(t => t.Id).Distinct().Should().HaveCount(4, "four distinct texts");
        ShownAfterReopen(doc, VisuTextIds.Known.Empty).Should().Equal(OwnTexts(doc));
        lines.Should().HaveCount(4, "the first text keeps the free id 0");
    }

    [Fact]
    public void Assign_UsesTheTextListIdOfAKnownText_AndNewIdsAboveEveryUsedId()
    {
        var known = VisuTextIds.Known.Empty;
        known.TextById["0"] = "Something else";
        known.TextById["17"] = "Stop";
        known.OtherIds.Add("40");
        var doc = BuiltPage();
        VisuTextIds.Assign(doc, known);

        var ids = VisuTextIds.Texts(doc).DistinctBy(t => t.Text).ToDictionary(t => t.Text, t => t.Id);
        ids["Stop"].Should().Be("17");
        ids["Title"].Should().NotBe("0", "id 0 is another text in the text list");
        new[] { ids["Title"], ids["Start"], ids["%d"] }.Select(long.Parse).Should().OnlyContain(n => n > 40);
        ShownAfterReopen(doc, known).Should().Equal(OwnTexts(doc));
    }

    [Fact]
    public void Assign_RepairsAPageWrittenWithIdZeroEverywhere_AndKeepsCorrectIds()
    {
        // A page as the defect left it: XAE saved the text list with its own ids, the page kept "0" everywhere.
        var known = VisuTextIds.Known.Empty;
        known.TextById["0"] = "Title";
        known.TextById["429"] = "Start";
        known.TextById["430"] = "Stop";
        var doc = BuiltPage();
        ShownAfterReopen(doc, known).Should().OnlyContain(t => t == "Title");

        VisuTextIds.Assign(doc, known);
        var ids = VisuTextIds.Texts(doc).Select(t => t.Id).ToList();
        ids.Should().Equal("0", "429", "430", "431", "429");
        ShownAfterReopen(doc, known).Should().Equal(OwnTexts(doc));

        VisuTextIds.Assign(doc, known).Should().BeEmpty("a second run changes nothing");
    }

    [Fact]
    public void Load_ReadsTheTextListAndOtherVisualizationsOfTheProject_NotTheFileItself()
    {
        string plc = Path.Combine(_dir, "Plc");
        Directory.CreateDirectory(Path.Combine(plc, "VISUs"));
        Directory.CreateDirectory(Path.Combine(plc, "_Libraries"));
        File.WriteAllText(Path.Combine(plc, "Plc.plcproj"), "<Project />");
        File.WriteAllText(Path.Combine(plc, "GlobalTextList.TcGTLO"), TextList(("0", "Title"), ("12", "Start")));
        var other = BuiltPage();
        VisuTextIds.Assign(other, VisuTextIds.Known.Empty);
        foreach (var e in other.Elements)
            VisuElements.SetMemberValue(other, e, VisuCatalog.TextId, new XElement("v", new XAttribute("n", "Value"), "\"55\""));
        File.WriteAllText(Path.Combine(plc, "VISUs", "VISU_Other.TcVIS"), other.Serialize());
        File.WriteAllText(Path.Combine(plc, "_Libraries", "VISU_Lib.TcVIS"), "not read");
        string self = Path.Combine(plc, "VISUs", "VISU_Main.TcVIS");
        File.WriteAllText(self, "not read either");

        var known = VisuTextIds.Load(self);
        known.TextById.Should().BeEquivalentTo(new Dictionary<string, string> { ["0"] = "Title", ["12"] = "Start" });
        known.OtherIds.Should().BeEquivalentTo(["55"]);
    }

    [Fact]
    public void Load_WithoutAPlcProject_KnowsNothing()
    {
        string file = Path.Combine(_dir, "VISU_Loose.TcVIS");
        File.WriteAllText(file, "x");
        VisuTextIds.Load(file).TextById.Should().BeEmpty();
    }
}
