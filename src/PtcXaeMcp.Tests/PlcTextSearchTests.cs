using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// The text work behind twincat_plc_search and twincat_plc_replace_text: matching, line/column counting over LF and
// CRLF, excerpts, and the refusals of a replacement that would not hit exactly one place.
public class PlcTextSearchTests
{
    private const string Fb = "IF bEnable THEN\r\n    nCount := nCount + 1;\r\nEND_IF\r\nfbTimer(IN := bEnable);\r\n";

    private static System.Text.RegularExpressions.Regex Compile(string pattern, bool regex = false, bool caseSensitive = false, bool wholeWord = false)
    {
        var (matcher, error) = PlcTextSearch.Compile(pattern, regex, caseSensitive, wholeWord);
        error.Should().BeNull();
        return matcher!;
    }

    // ---- search ----------------------------------------------------------------

    [Fact]
    public void Find_Literal_IsCaseInsensitiveByDefault_AndCountsLinesAndColumnsOverCrlf()
    {
        var hits = PlcTextSearch.Find(Fb, Compile("BENABLE"));
        hits.Select(h => (h.Line, h.Column)).Should().Equal((1, 4), (4, 15));
    }

    [Fact]
    public void Find_CaseSensitive_MissesOtherCasing()
    {
        PlcTextSearch.Find(Fb, Compile("BENABLE", caseSensitive: true)).Should().BeEmpty();
        PlcTextSearch.Find(Fb, Compile("bEnable", caseSensitive: true)).Should().HaveCount(2);
    }

    [Fact]
    public void Find_WholeWord_SkipsIdentifiersThatOnlyContainThePattern()
    {
        const string text = "nCount := nCountMax;\nnCount_2 := 0;\nnCount := 1;";
        PlcTextSearch.Find(text, Compile("nCount", wholeWord: true)).Select(h => h.Line).Should().Equal(1, 3);
        PlcTextSearch.Find(text, Compile("nCount")).Should().HaveCount(4);
    }

    [Fact]
    public void Find_Literal_EscapesRegexCharacters()
    {
        PlcTextSearch.Find(Fb, Compile("fbTimer(IN")).Select(h => h.Line).Should().Equal(4);
    }

    [Fact]
    public void Find_Regex_AnchorsWorkPerLine()
    {
        PlcTextSearch.Find(Fb, Compile(@"^END_IF$", regex: true)).Select(h => h.Line).Should().Equal(3);
    }

    [Fact]
    public void Find_ZeroLengthMatches_AreIgnored()
    {
        PlcTextSearch.Find("abc", Compile("x*", regex: true)).Should().BeEmpty();
    }

    [Fact]
    public void Find_AMatchOverSeveralLines_ReportsItsLastLine()
    {
        var hit = PlcTextSearch.Find(Fb, Compile(@"THEN\s+nCount", regex: true)).Single();
        (hit.Line, hit.EndLine).Should().Be((1, 2));
    }

    [Fact]
    public void Compile_RejectsAnEmptyPatternAndAnInvalidRegex()
    {
        PlcTextSearch.Compile("", false, false, false).Error.Should().Be("pattern is required.");
        PlcTextSearch.Compile("(", true, false, false).Error.Should().StartWith("pattern is not a valid regular expression:");
    }

    [Fact]
    public void Excerpt_MarksTheHitLines_StripsCr_AndClampsToTheText()
    {
        PlcTextSearch.Excerpt(Fb, 1, 1, context: 1).Should().Equal(
            "> L1: IF bEnable THEN",
            "  L2:     nCount := nCount + 1;");
        PlcTextSearch.Excerpt(Fb, 4, 4, context: 0).Should().Equal("> L4: fbTimer(IN := bEnable);");
    }

    [Fact]
    public void Excerpt_ShortensVeryLongLines()
    {
        string line = new('x', 500);
        PlcTextSearch.Excerpt(line, 1, 1, 0).Single().Should().HaveLength("> L1: ".Length + PlcTextSearch.MaxExcerptLineLength + 1).And.EndWith("…");
    }

    // ---- replace ---------------------------------------------------------------

    [Fact]
    public void Replace_OneOccurrence_ChangesOnlyThatText()
    {
        var r = PlcTextReplace.Apply(Fb, "nCount := nCount + 1;", "nCount := nCount + 2;");
        r.Error.Should().BeNull();
        r.Text.Should().Be(Fb.Replace("+ 1;", "+ 2;"));
        (r.StartLine, r.OldEndLine, r.NewEndLine).Should().Be((2, 2, 2));
    }

    [Fact]
    public void Replace_MultiLineOldTextWithLf_MatchesACrlfText_AndKeepsCrlf()
    {
        var r = PlcTextReplace.Apply(Fb, "END_IF\nfbTimer(IN := bEnable);", "END_IF\nfbTimer(IN := bEnable, PT := T#1S);\nbDone := fbTimer.Q;");
        r.Error.Should().BeNull();
        r.Text.Should().Be("IF bEnable THEN\r\n    nCount := nCount + 1;\r\nEND_IF\r\nfbTimer(IN := bEnable, PT := T#1S);\r\nbDone := fbTimer.Q;\r\n");
        (r.StartLine, r.OldEndLine, r.NewEndLine).Should().Be((3, 4, 5));
    }

    [Fact]
    public void Replace_EmptyNewText_DeletesTheOldText()
    {
        var r = PlcTextReplace.Apply("a;\nb;\nc;", "b;\n", "");
        r.Text.Should().Be("a;\nc;");
    }

    [Fact]
    public void Replace_IsCaseSensitive()
    {
        var r = PlcTextReplace.Apply(Fb, "if benable then", "IF x THEN");
        r.Error.Should().StartWith("oldText does not occur in the text");
    }

    [Fact]
    public void Replace_NotFound_ButFoundIgnoringWhitespace_SaysWhere()
    {
        var r = PlcTextReplace.Apply(Fb, "nCount:=nCount + 1;", "x");
        r.Text.Should().BeNull();
        r.Error.Should().Be("oldText does not occur in the text; it matches when whitespace is ignored, at line 2 — " +
                            "copy it exactly as twincat_plc_read returns it (spaces, tabs, line breaks).");
    }

    [Fact]
    public void Replace_NotFound_ButItsFirstLineIs_SaysWhereItDiverges()
    {
        var r = PlcTextReplace.Apply(Fb, "IF bEnable THEN\n    nCount := 0;", "x");
        r.Error.Should().Be("oldText does not occur in the text; its first line is at line 1, a later line differs.");
    }

    [Fact]
    public void Replace_NotFound_Plain()
    {
        PlcTextReplace.Apply(Fb, "nothing like this", "x").Error.Should().Be("oldText does not occur in the text.");
    }

    [Fact]
    public void Replace_Ambiguous_ListsTheLines_UnlessAnOccurrenceIsGiven()
    {
        const string text = "x := 1;\ny := 2;\nx := 1;\n";
        PlcTextReplace.Apply(text, "x := 1;", "x := 3;").Error.Should().Be(
            "oldText occurs 2 times (lines 1, 3) — include surrounding text to make it unique, or pass occurrence (1–2).");

        var second = PlcTextReplace.Apply(text, "x := 1;", "x := 3;", occurrence: 2);
        second.Text.Should().Be("x := 1;\ny := 2;\nx := 3;\n");
        second.StartLine.Should().Be(3);

        PlcTextReplace.Apply(text, "x := 1;", "x := 3;", occurrence: 3).Error.Should().Be(
            "occurrence 3 is out of range: oldText occurs 2 times.");
    }

    [Fact]
    public void Replace_RejectsAnEmptyOldText()
    {
        PlcTextReplace.Apply(Fb, "", "x").Error.Should().Be("oldText is required.");
    }
}
