using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_plc_search, twincat_plc_replace_text and the revision guard of twincat_plc_edit_entries on the coordinator:
// which elements are searched, how hits are reported and paged, and that every refusal of a replacement happens
// BEFORE the logout.
public class PlcCodeSearchCoordinatorTests
{
    private const string Root = "TIPC^Plc^Plc Project";
    private const string Fb = Root + "^POUs^FB_Motor";
    private const string Run = Fb + "^Run";
    private const string Speed = Fb + "^Speed";
    private const string SpeedGet = Speed + "^Get";
    private const string Main = Root + "^POUs^MAIN";
    private const string Sfc = Root + "^POUs^PRG_Seq";
    private const string Gvl = Root + "^GVLs^GVL_Main";
    private const string Dut = Root + "^DUTs^ST_Point";
    private const string Folder = Root + "^POUs";
    private const string TextList = Root + "^TL_Texts";

    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        void Text(string path, int itemType, string decl, string impl = "", string lang = "")
        {
            fake.PlcNodeItemTypes[path] = itemType;
            fake.PouBodiesByPath[path] = new PouBody(decl, impl, lang);
            fake.GvlDeclarationsByPath[path] = decl;
        }

        fake.Pous.Add(new PouEntry(Folder, "POUs", PlcKind.Folder, null, PlcItemType.Folder));
        fake.Pous.Add(new PouEntry(Fb, "FB_Motor", PlcKind.Pou, "ST", PlcItemType.PouFunctionBlock));
        fake.Pous.Add(new PouEntry(Main, "MAIN", PlcKind.Pou, "ST", PlcItemType.PouProgram));
        fake.Pous.Add(new PouEntry(Sfc, "PRG_Seq", PlcKind.Pou, "SFC", PlcItemType.PouProgram));
        fake.Pous.Add(new PouEntry(Gvl, "GVL_Main", PlcKind.Gvl, null, PlcItemType.Gvl));
        fake.Pous.Add(new PouEntry(Dut, "ST_Point", PlcKind.Dut, null, PlcItemType.DutStruct));
        fake.Pous.Add(new PouEntry(TextList, "TL_Texts", PlcKind.TextList, null, PlcItemType.TextList));

        Text(Fb, PlcItemType.PouFunctionBlock, "FUNCTION_BLOCK FB_Motor\nVAR\n    nSpeed : INT;\nEND_VAR\n", "nSpeed := GVL_Main.nSetpoint;", "ST");
        Text(Run, PlcItemType.Method, "METHOD Run : BOOL\n", "Run := nSpeed > 0;", "ST");
        Text(Speed, PlcItemType.Property, "PROPERTY Speed : INT\n");
        Text(SpeedGet, PlcItemType.PropertyGet, "", "Speed := nSpeed;", "ST");
        Text(Main, PlcItemType.PouProgram, "PROGRAM MAIN\nVAR\n    fbMotor : FB_Motor;\nEND_VAR\n", "fbMotor();\nGVL_Main.nSetpoint := 5;", "ST");
        Text(Sfc, PlcItemType.PouProgram, "PROGRAM PRG_Seq\nVAR\n    nSetpoint : INT;\nEND_VAR\n", "<SFC><step name=\"nSetpoint\"/></SFC>", "SFC");
        Text(Gvl, PlcItemType.Gvl, "VAR_GLOBAL\n    nSetpoint : INT := 10;\nEND_VAR\n");
        Text(Dut, PlcItemType.DutStruct, "TYPE ST_Point :\nSTRUCT\n    X : INT;\nEND_STRUCT\nEND_TYPE\n");

        fake.MembersByPou[Fb] = [new(Run, "Run", PlcMemberKind.Method, PlcItemType.Method),
                                 new(Speed, "Speed", PlcMemberKind.Property, PlcItemType.Property),
                                 new(SpeedGet, "Get", PlcMemberKind.Get, PlcItemType.PropertyGet)];
        fake.PlcNodeItemTypes[Folder] = PlcItemType.Folder;
        fake.MembersByPou[Main] = [];
        fake.MembersByPou[Sfc] = [];
        return fake;
    }

    private static PlcObjectCoordinator Coord(FakeTwinCatSession fake) => new(fake);

    private static void AssertNothingHappened(FakeTwinCatSession fake)
    {
        fake.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
        fake.CallLog.Should().NotContain(c => c.StartsWith("WritePou", StringComparison.Ordinal));
    }

    // ---- search ----------------------------------------------------------------

    [Fact]
    public void Search_FindsEveryUseAcrossElementsMembersAndSections_InTreeOrder()
    {
        var r = Coord(OpenFake()).Search("nSetpoint");

        r.Matches.Select(m => (m.Path, m.Section, m.Line)).Should().Equal(
            (Fb, "implementation", 1),
            (Main, "implementation", 2),
            (Sfc, "declaration", 3),
            (Gvl, "declaration", 2));
        r.Summary.Should().Be("Found 4 matches in 4 elements (8 elements searched, 1 skipped). Showing 1–4.");
        r.HasMore.Should().BeFalse();
        r.Page!.Total.Should().Be(4);
    }

    [Fact]
    public void Search_ReportsTheRevisionOfTheElementAndAnExcerpt()
    {
        var fake = OpenFake();
        var m = Coord(fake).Search("GVL_Main.nSetpoint := 5").Matches.Single();

        m.Path.Should().Be(Main);
        m.Kind.Should().Be(PlcKind.Pou);
        m.Subtype.Should().Be("PROGRAM");
        m.Column.Should().Be(1);
        m.Revision.Should().Be(PlcRevision.Of(fake.PouBodiesByPath[Main].Declaration, fake.PouBodiesByPath[Main].Implementation));
        m.Excerpt.Should().Equal("  L1: fbMotor();", "> L2: GVL_Main.nSetpoint := 5;");
    }

    [Fact]
    public void Search_DoesNotSearchAGraphicalImplementation_AndSaysSo()
    {
        var r = Coord(OpenFake()).Search("step name");
        r.Matches.Should().BeEmpty();
        r.Skipped.Should().ContainSingle(s => s.Path == Sfc && s.Reason == "implementation in SFC (XML) not searched");
        r.Summary.Should().Be("No match for 'step name' in 8 elements searched (1 skipped).");
    }

    [Fact]
    public void Search_ScopeKindAndPathPrefix_Narrow()
    {
        var c = Coord(OpenFake());
        c.Search("nSetpoint", scope: "declaration").Matches.Select(m => m.Path).Should().Equal(Sfc, Gvl);
        c.Search("nSetpoint", kind: "GVL").Matches.Select(m => m.Path).Should().Equal(Gvl);
        c.Search("nSpeed", pathPrefix: Fb).Matches.Select(m => m.Path).Should().Equal(Fb, Fb, Run, SpeedGet);
        c.Search("nSpeed", pathPrefix: Run).Matches.Select(m => m.Path).Should().Equal(Run);
        c.Search("nSpeed", kind: "MEMBER").Matches.Select(m => m.Path).Should().Equal(Run, SpeedGet);
    }

    [Fact]
    public void Search_PathPrefix_DoesNotListTheMembersOfPousOutsideIt()
    {
        var fake = OpenFake();
        Coord(fake).Search("x", pathPrefix: Root + "^GVLs");
        fake.CallLog.Should().NotContain(c => c.StartsWith("ListPouMembers", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_Pages()
    {
        var c = Coord(OpenFake());
        var page = c.Search("nSetpoint", limit: 3);
        page.Matches.Should().HaveCount(3);
        page.HasMore.Should().BeTrue();
        page.NextOffset.Should().Be(3);
        page.Summary.Should().Be("Found 4 matches in 4 elements (8 elements searched, 1 skipped). Showing 1–3.");
        page.Page!.Footer().Should().Be("Page: offset 0, returned 3 of 4; has_more true, next_offset 3");

        var rest = c.Search("nSetpoint", limit: 3, offset: 3);
        rest.Matches.Select(m => m.Path).Should().Equal(Gvl);
        rest.HasMore.Should().BeFalse();
        rest.Page!.Footer().Should().Be("Page: offset 3, returned 1 of 4; has_more false");
    }

    [Fact]
    public void Search_LimitZeroOrAboveTheMaximum_GivesTheMaximum()
    {
        var c = Coord(OpenFake());
        c.Search("nSetpoint", limit: 0).Matches.Should().HaveCount(4);
        c.Search("nSetpoint", limit: 50_000).Matches.Should().HaveCount(4);
        PlcObjectCoordinator.SearchPage.Resolve(50_000).Should().Be(1000);
        c.Search("nothing-like-this").Page!.Footer().Should().Be("Page: offset 0, returned 0 of 0; has_more false");
    }

    [Fact]
    public void Search_RefusesBadParameters()
    {
        var c = Coord(OpenFake());
        c.Search(" ").Summary.Should().Be("[Error] pattern is required.");
        c.Search("x", scope: "body").Summary.Should().Be("[Error] Unknown scope 'body'. Expected declaration, implementation or both.");
        c.Search("x", kind: "FOLDER").Summary.Should().Be("[Error] kind FOLDER has no text to search. Expected POU, INTERFACE, DUT, GVL or MEMBER.");
        c.Search("x", limit: -1).Summary.Should().StartWith("[Error] limit must be 0 or more (got -1)");
        c.Search("x", offset: -1).Summary.Should().Be("[Error] offset must be 0 or more (got -1). Nothing was read.");
        c.Search("x", contextLines: 11).Summary.Should().Be("[Error] contextLines must be between 0 and 10.");
        c.Search("(", regex: true).Summary.Should().StartWith("[Error] pattern is not a valid regular expression:");
    }

    [Fact]
    public void Search_AnUnreadableElement_IsSkippedWithTheReason()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath.Remove(Main);
        var r = Coord(fake).Search("nSetpoint");
        r.Matches.Select(m => m.Path).Should().NotContain(Main);
        r.Skipped.Should().ContainSingle(s => s.Path == Main && s.Reason.Contains("not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_UnlistableMembers_SkipThatPouOnly()
    {
        var fake = OpenFake();
        fake.MembersByPou.Remove(Fb);
        var r = Coord(fake).Search("nSetpoint");
        r.Matches.Select(m => m.Path).Should().Equal(Fb, Main, Sfc, Gvl);
        r.Skipped.Should().Contain(s => s.Path == Fb && s.Reason.StartsWith("members unreadable:", StringComparison.Ordinal));
    }

    [Fact]
    public void Search_ABlockedXae_FailsTheWholeSearch()
    {
        var fake = OpenFake();
        fake.ReadGvlThrows = new XaeBlockedException("XAE is blocked by dialog 'Hinweis'.", "Hinweis", null);
        Coord(fake).Search("nSetpoint").Summary.Should().StartWith("[Error] XAE is blocked by dialog");
    }

    [Fact]
    public void Search_WithoutAnOpenSolution_IsRefused()
    {
        var fake = OpenFake();
        fake.SolutionOpen = false;
        Coord(fake).Search("x").Summary.Should().StartWith("[Error]");
    }

    // ---- replace_text ----------------------------------------------------------

    [Fact]
    public void Replace_WritesOnlyTheSection_AndReturnsTheChangedLinesAndTheNewRevision()
    {
        var fake = OpenFake();
        string rev = Coord(fake).Read(Main, "POU").Content!.Revision;

        string result = Coord(fake).ReplaceText(Main, "POU", "implementation", "nSetpoint := 5", "nSetpoint := 7", expectedRevision: rev);

        fake.PouBodiesByPath[Main].Implementation.Should().Be("fbMotor();\nGVL_Main.nSetpoint := 7;");
        fake.CallLog.Should().Contain($"WritePouBody({Main}, decl=<null>, impl=<set>)");
        string newRev = PlcRevision.Of(fake.PouBodiesByPath[Main].Declaration, fake.PouBodiesByPath[Main].Implementation);
        result.Should().Be(
            $"Replaced text in the implementation of POU '{Main}', line 2 (verified by read-back). Revision: {newRev}\n" +
            "  L1: fbMotor();\n" +
            "> L2: GVL_Main.nSetpoint := 7;");
    }

    [Fact]
    public void Replace_InAMember_UsesTheMemberWritePath()
    {
        var fake = OpenFake();
        Coord(fake).ReplaceText(Run, "MEMBER", "implementation", "> 0", ">= 1").Should().StartWith("Replaced text");
        fake.PouBodiesByPath[Run].Implementation.Should().Be("Run := nSpeed >= 1;");
        fake.CallLog.Should().Contain($"WritePouMember({Run}, decl=<null>, impl=<set>)");
    }

    [Fact]
    public void Replace_InADeclaration_OfAGvl()
    {
        var fake = OpenFake();
        Coord(fake).ReplaceText(Gvl, "GVL", "declaration", ":= 10", ":= 20").Should().StartWith($"Replaced text in the declaration of GVL '{Gvl}'");
        fake.PouBodiesByPath[Gvl].Declaration.Should().Contain("nSetpoint : INT := 20;");
    }

    [Fact]
    public void Replace_Refusals_HappenBeforeTheLogout()
    {
        var fake = OpenFake();
        var c = Coord(fake);

        c.ReplaceText(Main, "POU", "implementation", "nothing like this", "x")
            .Should().Be($"[Error] oldText does not occur in the text (in the implementation of '{Main}'). Nothing was changed.");
        c.ReplaceText(Main, "POU", "implementation", ";", "x")
            .Should().Be($"[Error] oldText occurs 2 times (lines 1, 2) — include surrounding text to make it unique, or pass occurrence (1–2) " +
                         $"(in the implementation of '{Main}'). Nothing was changed.");
        c.ReplaceText(Main, "POU", "implementation", "fbMotor", "x", expectedRevision: "0000000000000000")
            .Should().StartWith($"[Error] '{Main}' changed since it was read (expected revision 0000000000000000, now ");
        c.ReplaceText(Main, "GVL", "implementation", "fbMotor", "x")
            .Should().StartWith("[Error]").And.Contain("not a GVL");
        c.ReplaceText(Gvl, "GVL", "implementation", "x", "y")
            .Should().StartWith($"[Error] '{Gvl}' is").And.Contain("has no implementation section");
        const string reset = Fb + "^Reset";
        fake.PlcNodeItemTypes[reset] = PlcItemType.Action;
        fake.PouBodiesByPath[reset] = new PouBody("", "nSpeed := 0;", "ST");
        c.ReplaceText(reset, "MEMBER", "declaration", "x", "y")
            .Should().StartWith($"[Error] '{reset}' is").And.Contain("has no declaration section");
        c.ReplaceText(Sfc, "POU", "implementation", "step", "x")
            .Should().StartWith($"[Error] '{Sfc}' is implemented in SFC").And.Contain("twincat_plc_write");
        c.ReplaceText(Main, "POU", "body", "x", "y")
            .Should().Be("[Error] section must be declaration or implementation.");
        c.ReplaceText(Main, "POU", "implementation", "fbMotor", null)
            .Should().Be("[Error] newText is required (an empty string deletes oldText).");
        c.ReplaceText(Folder, "FOLDER", "declaration", "x", "y")
            .Should().StartWith($"[Error] '{Folder}' is a FOLDER");

        AssertNothingHappened(fake);
    }

    [Fact]
    public void Replace_AMemberHeaderIntoAPouDeclaration_IsRefused()
    {
        var fake = OpenFake();
        Coord(fake).ReplaceText(Fb, "POU", "declaration", "END_VAR\n", "END_VAR\nMETHOD Stop : BOOL\n")
            .Should().StartWith("[Error]").And.Contain("METHOD");
        AssertNothingHappened(fake);
    }

    // ---- edit_entries revision -------------------------------------------------

    [Fact]
    public void EditEntries_WithAStaleRevision_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).EditEntries(Gvl, "GVL", "add", "bNew", type: "BOOL", expectedRevision: "0000000000000000")
            .Should().StartWith($"[Error] '{Gvl}' changed since it was read (expected revision 0000000000000000, now ");
        AssertNothingHappened(fake);
    }

    [Fact]
    public void EditEntries_WithTheCurrentRevision_Writes_AndReportsTheNewRevision()
    {
        var fake = OpenFake();
        string rev = Coord(fake).Read(Gvl, "GVL").Content!.Revision;
        string result = Coord(fake).EditEntries(Gvl, "GVL", "add", "bNew", type: "BOOL", expectedRevision: rev);
        fake.PouBodiesByPath[Gvl].Declaration.Should().Contain("bNew : BOOL;");
        fake.GvlDeclarationsByPath[Gvl].Should().Contain("bNew : BOOL;");
        result.Should().EndWith($"Revision: {PlcRevision.Of(fake.GvlDeclarationsByPath[Gvl], "")}");
    }
}
