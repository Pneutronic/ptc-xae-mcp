using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// feature/plc-objects: the generic twincat_plc_* verbs. The point of every test here is the order of events —
// the kind check, the per-kind parameter check and the "element has no such section" check must all fail
// BEFORE the logout, and a failure must leave the fake's PLC tree untouched.
public class PlcObjectCoordinatorTests
{
    private const string Dut = "^DUTs^ST_Point";
    private const string Gvl = "^GVLs^GVL_Main";
    private const string Fb = "^POUs^FB_Motor";
    private const string Itf = "^POUs^I_Motor";

    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.PlcNodeItemTypes[Dut] = PlcItemType.DutStruct;
        fake.PlcNodeItemTypes[Gvl] = PlcItemType.Gvl;
        fake.PlcNodeItemTypes[Fb] = PlcItemType.PouFunctionBlock;
        fake.PlcNodeItemTypes[Itf] = PlcItemType.Interface;
        fake.PlcNodeItemTypes["^POUs"] = PlcItemType.Folder;
        fake.PlcNodeItemTypes["^POUs^FB_Motor^Run"] = PlcItemType.Method;
        fake.PlcNodeItemTypes["^POUs^FB_Motor^Reset"] = PlcItemType.Action;
        fake.PlcNodeItemTypes["^POUs^FB_Motor^Speed"] = PlcItemType.Property;
        fake.PlcNodeItemTypes["^POUs^FB_Motor^Speed^Get"] = PlcItemType.PropertyGet;
        fake.PlcNodeItemTypes["^TIPC^Plc^Plc Project"] = PlcItemType.PlcApp;
        fake.PouBodiesByPath[Dut] = new PouBody("TYPE ST_Point :\nSTRUCT\n  X : INT;\nEND_STRUCT\nEND_TYPE", "", "");
        fake.GvlDeclarationsByPath[Dut] = "TYPE ST_Point :\nSTRUCT\n  X : INT;\nEND_STRUCT\nEND_TYPE";
        fake.GvlDeclarationsByPath[Gvl] = "VAR_GLOBAL\nEND_VAR";
        fake.PouBodiesByPath[Gvl] = new PouBody("VAR_GLOBAL\nEND_VAR", "", "");
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR", "x := 1;", "ST");
        fake.PouBodiesByPath[Itf] = new PouBody("INTERFACE I_Motor", "", "ST");
        fake.PouBodiesByPath["^POUs^FB_Motor^Run"] = new PouBody("METHOD Run : BOOL", "Run := TRUE;", "ST");
        fake.PouBodiesByPath["^POUs^FB_Motor^Reset"] = new PouBody("", "x := 0;", "ST");
        return fake;
    }

    private static PlcObjectCoordinator Coord(FakeTwinCatSession fake) => new(fake);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rename_PassesUpdateReferencesThrough(bool updateReferences)
    {
        var fake = OpenFake();
        Coord(fake).Rename("^POUs^FB_Motor^Run", "MEMBER", "Start", updateReferences).Should().StartWith("Renamed '^POUs^FB_Motor^Run'");
        fake.LastRenameUpdateReferences.Should().Be(updateReferences);
    }

    [Fact]
    public void Rename_AMethodToTheNameOfItsOwnInput_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath["^POUs^FB_Motor^Run"] = new PouBody("METHOD Run : BOOL\nVAR_INPUT\n\tNewStep : BOOL;\nEND_VAR", "Run := NewStep;", "ST");

        Coord(fake).Rename("^POUs^FB_Motor^Run", "MEMBER", "NewStep")
            .Should().StartWith("[Error] '^POUs^FB_Motor^Run' cannot be renamed to 'NewStep': 'NewStep' is already a variable (VAR_INPUT")
            .And.Contain("in its own declaration");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Rename_AMethodToTheNameOfAVariableOfItsPou_IsRefused()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor\nVAR\n\tbStart : BOOL;\nEND_VAR", "", "ST");

        Coord(fake).Rename("^POUs^FB_Motor^Run", "MEMBER", "bStart")
            .Should().Contain("'bStart' is already a variable (VAR").And.Contain("in the declaration of its POU");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Rename_WithTheOldReturnAssignmentLeft_Warns()
    {
        var fake = OpenFake();
        // What the XAE leaves when its refactoring option is off: the node is renamed, the body is not.
        fake.PouBodiesByPath["^POUs^FB_Motor^Start"] = new PouBody("METHOD Start : BOOL", "IF x THEN\n  Run := TRUE;\nEND_IF", "ST");

        Coord(fake).Rename("^POUs^FB_Motor^Run", "MEMBER", "Start")
            .Should().StartWith("Renamed '^POUs^FB_Motor^Run'")
            .And.Contain("[Warn] The body still assigns to the old name 'Run' (line 2)");
    }

    [Fact]
    public void Rename_DefaultIsNotToUpdateReferences()
    {
        var fake = OpenFake();
        Coord(fake).Rename("^POUs^FB_Motor^Run", "MEMBER", "Start").Should().Contain("Refactoring prompt:");
        fake.LastRenameUpdateReferences.Should().BeFalse();
    }

    private static void AssertNoLogoutNoWrite(FakeTwinCatSession fake)
    {
        fake.CallLog.Should().NotContain(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal), "the check must fail before the logout");
        fake.CallLog.Should().NotContain(c =>
            c.StartsWith("WritePouBody", StringComparison.Ordinal) || c.StartsWith("WritePouMember", StringComparison.Ordinal)
            || c.StartsWith("Create", StringComparison.Ordinal) || c.StartsWith("Delete", StringComparison.Ordinal)
            || c.StartsWith("Rename", StringComparison.Ordinal) || c.StartsWith("AddPouMember", StringComparison.Ordinal));
    }

    // ---- kind argument -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Read_MissingKind_IsAnError(string? kind)
    {
        var fake = OpenFake();
        Coord(fake).Read(Dut, kind!).Summary.Should().StartWith("[Error] kind is required");
        fake.CallLog.Should().BeEmpty();
    }

    [Fact]
    public void Write_UnknownKind_ListsTheKinds()
    {
        var fake = OpenFake();
        Coord(fake).Write(Dut, "TABLE", "x", null).Should().StartWith("[Error] Unknown kind 'TABLE'");
        fake.CallLog.Should().BeEmpty();
    }

    // Elements whose content XAE does not hand out: content verbs refuse with the reason, the lifecycle verbs work.
    [Theory]
    [InlineData("TEXTLIST")]
    [InlineData("IMAGEPOOL")]
    [InlineData("VISU")]
    [InlineData("RECIPE")]
    [InlineData("UML")]
    [InlineData("TASKREF")]
    public void ContentlessKinds_RefuseTheContentVerbs_WithAReason(string kind)
    {
        var fake = OpenFake();
        Coord(fake).Read(Dut, kind).Summary.Should().Contain($"kind {kind} is not supported").And.Contain("Automation Interface").And.StartWith("[Error]");
        Coord(fake).Write(Dut, kind, "x", null).Should().Contain("not supported");
        Coord(fake).ListEntries(Dut, kind).Summary.Should().Contain("not supported");
        Coord(fake).EditEntries(Dut, kind, "add", "x").Should().Contain("not supported");
        fake.CallLog.Should().BeEmpty();
    }

    [Theory]
    [InlineData("TEXTLIST", PlcItemType.TextList)]
    [InlineData("IMAGEPOOL", PlcItemType.ImagePool)]
    [InlineData("RECIPE", PlcItemType.RecipeManager)]
    [InlineData("UML", PlcItemType.UmlClassDiagram)]
    public void ContentlessKinds_CanBeRenamedAndDeleted(string kind, int itemType)
    {
        var fake = OpenFake();
        const string path = "^Thing";
        fake.PlcNodeItemTypes[path] = itemType;

        Coord(fake).Rename(path, kind, "Renamed").Should().StartWith("Renamed '^Thing'");
        fake.MissingPlcPaths.Remove(path);
        Coord(fake).Delete(path, kind).Should().StartWith($"Deleted {kind}");
        fake.CallLog.Should().Contain("DeletePlcObject(^Thing)");
    }

    [Fact]
    public void ContentlessKind_WrongKindStillRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Delete(Fb, "TEXTLIST").Should().Be($"[Error] '{Fb}' is a POU, not a TEXTLIST.");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void LibraryKind_PointsToTheLibraryTools()
    {
        var fake = OpenFake();
        Coord(fake).Read(Dut, "LIBRARY").Summary.Should().Contain("twincat_add_library_reference");
    }

    // ---- kind check before the logout ---------------------------------------

    [Fact]
    public void Write_GvlWhereDutWasAsked_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Write(Gvl, "DUT", "VAR_GLOBAL\nEND_VAR", null)
            .Should().Be($"[Error] '{Gvl}' is a GVL, not a DUT.");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Delete_PouWhereGvlWasAsked_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Delete(Fb, "GVL").Should().Be($"[Error] '{Fb}' is a POU, not a GVL.");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Rename_WrongKind_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Rename(Itf, "POU", "I_New").Should().Be($"[Error] '{Itf}' is an INTERFACE, not a POU.");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Read_MissingPath_IsNotFound()
    {
        var fake = OpenFake();
        fake.MissingPlcPaths.Add("^POUs^Nope");
        Coord(fake).Read("^POUs^Nope", "POU").Summary.Should().Be("[Error] PLC tree path not found: ^POUs^Nope");
    }

    [Fact]
    public void Read_SystemNode_IsNeverAnyKind()
    {
        var fake = OpenFake();
        Coord(fake).Read("^TIPC^Plc^Plc Project", "POU").Summary.Should().Contain("PLC system node");
    }

    // ---- read ----------------------------------------------------------------

    [Fact]
    public void Read_Dut_ReturnsOnlyTheDeclaration()
    {
        var fake = OpenFake();
        var (summary, content) = Coord(fake).Read(Dut, "dut");
        summary.Should().Be($"Read DUT/STRUCT '{Dut}'. Revision: {PlcRevision.Of(content!.Declaration, "")}");
        content!.HasDeclaration.Should().BeTrue();
        content.HasImplementation.Should().BeFalse();
        content.Declaration.Should().Contain("ST_Point");
        content.Implementation.Should().BeEmpty();
    }

    [Fact]
    public void Read_Pou_ReturnsBothSectionsAndTheLanguage()
    {
        var fake = OpenFake();
        var (summary, content) = Coord(fake).Read(Fb, "POU");
        summary.Should().Be($"Read POU/FUNCTION_BLOCK '{Fb}' (ST). Revision: {PlcRevision.Of(content!.Declaration, content.Implementation)}");
        content!.Implementation.Should().Be("x := 1;");
    }

    [Fact]
    public void Read_Interface_HasNoImplementationSection()
    {
        var fake = OpenFake();
        var (_, content) = Coord(fake).Read(Itf, "INTERFACE");
        content!.HasImplementation.Should().BeFalse();
        content.Language.Should().BeEmpty();
    }

    [Fact]
    public void Read_Action_HasNoDeclarationSection()
    {
        var fake = OpenFake();
        var (_, content) = Coord(fake).Read("^POUs^FB_Motor^Reset", "MEMBER");
        content!.HasDeclaration.Should().BeFalse();
        content.HasImplementation.Should().BeTrue();
        content.Subtype.Should().Be("ACTION");
    }

    [Fact]
    public void Read_Folder_HasNoContent()
    {
        var fake = OpenFake();
        Coord(fake).Read("^POUs", "FOLDER").Summary.Should().Contain("has no declaration or implementation to read");
    }

    // ---- write ---------------------------------------------------------------

    [Fact]
    public void Write_ImplementationToDut_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Write(Dut, "DUT", null, "x := 1;").Should().Contain("has no implementation section");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Write_ImplementationToInterface_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Write(Itf, "INTERFACE", null, "x := 1;").Should().Contain("has no implementation section");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Write_DeclarationToAction_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Write("^POUs^FB_Motor^Reset", "MEMBER", "VAR\nEND_VAR", null).Should().Contain("has no declaration section");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Write_ToFolder_IsRefused()
    {
        var fake = OpenFake();
        Coord(fake).Write("^POUs", "FOLDER", "x", null).Should().Contain("FOLDER");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Write_DeclarationOfDut_LogsOutThenWritesAndReportsTheKind()
    {
        var fake = OpenFake();
        string result = Coord(fake).Write(Dut, "DUT", "TYPE ST_Point :\nSTRUCT\n  X : INT;\n  Y : INT;\nEND_STRUCT\nEND_TYPE", null);
        result.Should().MatchRegex($"^Wrote DUT '{System.Text.RegularExpressions.Regex.Escape(Dut)}'\\. Revision: [0-9a-f]{{16}}$");
        int logout = fake.CallLog.FindIndex(c => c.StartsWith("LogoutPlc", StringComparison.Ordinal));
        int write = fake.CallLog.FindIndex(c => c.StartsWith("WritePouBody", StringComparison.Ordinal));
        logout.Should().BeGreaterThanOrEqualTo(0);
        write.Should().BeGreaterThan(logout);
    }

    [Fact]
    public void Write_WithTheRevisionOfTheRead_Writes()
    {
        var fake = OpenFake();
        var (_, content) = Coord(fake).Read(Gvl, "GVL");

        string result = Coord(fake).Write(Gvl, "GVL", "VAR_GLOBAL\n  b : BOOL;\nEND_VAR", null, expectedRevision: content!.Revision.ToUpperInvariant());

        result.Should().StartWith($"Wrote GVL '{Gvl}'.");
        fake.CallLog.Should().Contain(c => c.StartsWith("WritePouBody", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_AfterSomeoneElseChangedTheElement_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        var (_, content) = Coord(fake).Read(Gvl, "GVL");
        fake.GvlDeclarationsByPath[Gvl] = "VAR_GLOBAL\n  nOther : INT;\nEND_VAR";

        string result = Coord(fake).Write(Gvl, "GVL", "VAR_GLOBAL\n  b : BOOL;\nEND_VAR", null, expectedRevision: content!.Revision);

        result.Should().StartWith($"[Error] '{Gvl}' changed since it was read (expected revision {content.Revision}, now ");
        result.Should().Contain("Nothing was changed");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Revision_DependsOnBothSectionsAndTheirBoundary()
    {
        PlcRevision.Of("a", "b").Should().NotBe(PlcRevision.Of("ab", ""));
        PlcRevision.Of("a", "b").Should().NotBe(PlcRevision.Of("a", "c"));
        PlcRevision.Of("a", "b").Should().MatchRegex("^[0-9a-f]{16}$");
    }

    [Fact]
    public void Write_Member_UsesTheMemberWritePath()
    {
        var fake = OpenFake();
        Coord(fake).Write("^POUs^FB_Motor^Run", "MEMBER", null, "Run := FALSE;").Should().StartWith("Wrote MEMBER");
        fake.CallLog.Should().Contain(c => c.StartsWith("WritePouMember(^POUs^FB_Motor^Run", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_EmbeddedMemberHeaderInAGvl_IsRefused_ButAMemberMayStartWithItsOwnHeader()
    {
        var fake = OpenFake();
        Coord(fake).Write(Gvl, "GVL", "VAR_GLOBAL\nMETHOD Foo : BOOL\nEND_VAR", null)
            .Should().Contain("embedded 'METHOD' header");
        AssertNoLogoutNoWrite(fake);

        Coord(fake).Write("^POUs^FB_Motor^Run", "MEMBER", "METHOD Run : BOOL\nVAR\nEND_VAR", null)
            .Should().StartWith("Wrote MEMBER");
    }

    [Fact]
    public void Write_ImplementationToNonStPou_IsRefused_ButItsDeclarationIsAccepted()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR", "", "FBD");

        Coord(fake).Write(Fb, "POU", null, "x := 1;").Should().Contain("implemented in FBD");
        AssertNoLogoutNoWrite(fake);

        Coord(fake).Write(Fb, "POU", "FUNCTION_BLOCK FB_Motor\nVAR\n  a : INT;\nEND_VAR", null)
            .Should().StartWith("Wrote POU");
    }

    [Fact]
    public void Write_NothingSupplied_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).Write(Dut, "DUT", null, null).Should().StartWith("[Error] At least one of declaration or implementation");
    }

    [Fact]
    public void Write_StillOnline_IsRefusedAndNothingIsWritten()
    {
        var fake = OpenFake();
        fake.FakeLogoutPlcResult = new LogoutPlcResult(true, true, "still online");
        Coord(fake).Write(Dut, "DUT", "TYPE X : STRUCT END_STRUCT END_TYPE", null).Should().Contain("Online");
        fake.CallLog.Should().NotContain(c => c.StartsWith("WritePouBody", StringComparison.Ordinal));
    }

    // ---- create --------------------------------------------------------------

    [Fact]
    public void Create_PouWithoutSubtype_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).Create("POU", null, "^POUs", "FB_New").Should().StartWith("[Error] subtype is required for kind=POU");
        fake.CallLog.Should().BeEmpty();
    }

    [Fact]
    public void Create_UnknownSubtype_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).Create("DUT", "NORMAL", "^DUTs", "ST_New").Should().Contain("Unknown subtype 'NORMAL' for kind DUT");
    }

    [Fact]
    public void Create_FunctionBlock_WithADeclaration_CreatesAndWritesIt()
    {
        var fake = OpenFake();
        string result = Coord(fake).Create("POU", "FUNCTION_BLOCK", "^POUs", "FB_New",
            declaration: "FUNCTION_BLOCK FB_New\nVAR\nEND_VAR");
        result.Should().Be("Created FUNCTION_BLOCK 'FB_New' at ^POUs^FB_New.");
        fake.CallLog.Should().Contain(c => c.StartsWith("CreatePou(parent=^POUs, name=FB_New, type=FUNCTION_BLOCK", StringComparison.Ordinal));
        fake.CallLog.Should().Contain(c => c.StartsWith("WritePouBody(^POUs^FB_New, decl=<set>", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_Pou_WithAnEmbeddedMemberHeader_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Create("POU", "PROGRAM", "^POUs", "P_New", declaration: "PROGRAM P_New\nMETHOD Foo : BOOL")
            .Should().Contain("embedded 'METHOD' header");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Create_Function_WithAnImplementation_CreatesThenWritesTheBody()
    {
        var fake = OpenFake();
        string result = Coord(fake).Create("POU", "FUNCTION", "^POUs", "F_New", returnType: "INT", implementation: "F_New := 1;");
        result.Should().StartWith("Created FUNCTION 'F_New' at ^POUs^F_New.");
        result.Should().Contain("Wrote POU '^POUs^F_New'.");
        fake.CallLog.Should().Contain(c => c.StartsWith("WritePouBody(^POUs^F_New, decl=<null>, impl=<set>", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("DUT", null, "only a POU and its METHOD")]
    [InlineData("GVL", null, "only a POU and its METHOD")]
    [InlineData("MEMBER", "PROPERTY", "a PROPERTY's code is in its Get/Set accessors")]
    public void Create_AnImplementationForAnElementWithoutOne_IsRefusedBeforeTheLogout(string kind, string? subtype, string why)
    {
        var fake = OpenFake();
        Coord(fake).Create(kind, subtype, "^POUs", "X_New", implementation: "x := 1;")
            .Should().StartWith("[Error] implementation does not apply").And.Contain(why);
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Create_Dut_DefaultsToStruct()
    {
        var fake = OpenFake();
        Coord(fake).Create("DUT", null, "^DUTs", "ST_New").Should().Be("Created DUT STRUCT 'ST_New' at ^DUTs^ST_New.");
    }

    [Fact]
    public void Create_Gvl_DefaultsToNormal_AndPersistentIsASectionOfIt()
    {
        var fake = OpenFake();
        Coord(fake).Create("GVL", null, "^GVLs", "GVL_P", declaration: "VAR_GLOBAL PERSISTENT\nEND_VAR")
            .Should().Be("Created GVL 'GVL_P' at ^GVLs^GVL_P.");
        fake.CallLog.Should().Contain("CreateGvl(parent=^GVLs, name=GVL_P)");
    }

    [Fact]
    public void Create_ParamList_UsesTheGenericCreateWith629()
    {
        var fake = OpenFake();
        fake.PlcNodeItemTypes["^GVLs"] = PlcItemType.Folder;
        Coord(fake).Create("GVL", "PARAMLIST", "^GVLs", "PL_Cfg").Should().Be("Created GVL (parameter list) 'PL_Cfg' at ^GVLs^PL_Cfg.");
        fake.CallLog.Should().Contain("CreatePlcChild(parent=^GVLs, name=PL_Cfg, subType=629, label=GVL (parameter list))");
    }

    [Fact]
    public void Create_ParamList_UnderAPou_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Create("GVL", "PARAMLIST", Fb, "PL_Cfg").Should().Contain("the parent must be a folder or the PLC project node");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Create_Interface_Works()
    {
        var fake = OpenFake();
        Coord(fake).Create("INTERFACE", null, "^POUs", "I_New").Should().Be("Created INTERFACE 'I_New' at ^POUs^I_New.");
    }

    [Fact]
    public void Create_InterfaceWithASubtype_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).Create("INTERFACE", "X", "^POUs", "I_New").Should().Contain("has no subtypes");
    }

    [Fact]
    public void Create_Folder_UsesTheGenericCreateWith601_AndAcceptsNonIdentifierNames()
    {
        var fake = OpenFake();
        Coord(fake).Create("FOLDER", null, "^POUs", "01_Application").Should().Be("Created FOLDER '01_Application' at ^POUs^01_Application.");
        fake.CallLog.Should().Contain("CreatePlcChild(parent=^POUs, name=01_Application, subType=601, label=FOLDER)");
    }

    [Fact]
    public void Create_Folder_UnderAPou_IsAccepted()
    {
        var fake = OpenFake();
        Coord(fake).Create("FOLDER", null, Fb, "Members").Should().StartWith("Created FOLDER");
    }

    [Fact]
    public void Create_Folder_UnderADut_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Create("FOLDER", null, Dut, "X").Should().Contain("the parent must be");
        AssertNoLogoutNoWrite(fake);
    }

    [Theory]
    [InlineData("a^b")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("name.")]
    public void Create_Folder_WithAnUnstorableName_IsRefused(string name)
    {
        var fake = OpenFake();
        Coord(fake).Create("FOLDER", null, "^POUs", name).Should().StartWith("[Error]");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Create_Method_AddsTheMember_AndWritesTheOptionalDeclaration()
    {
        var fake = OpenFake();
        fake.MembersByPou[Fb] = new();
        string result = Coord(fake).Create("MEMBER", "METHOD", Fb, "Stop", declaration: "METHOD Stop : BOOL\nVAR_INPUT\n  force : BOOL;\nEND_VAR");
        result.Should().Contain("Added METHOD 'Stop'").And.EndWith("Declaration written.");
        fake.CallLog.Should().Contain(c => c.StartsWith("WritePouMember(^POUs^FB_Motor^Stop, decl=<set>", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_Property_AcceptsTheAccessorsChoice()
    {
        var fake = OpenFake();
        fake.MembersByPou[Itf] = new();
        Coord(fake).Create("MEMBER", "PROPERTY", Itf, "Speed", returnType: "REAL", accessors: "GET")
            .Should().Contain("(GET only)");
    }

    [Fact]
    public void Create_Transition_UsesTheGenericCreateWith616_WithoutParameters()
    {
        var fake = OpenFake();
        Coord(fake).Create("MEMBER", "TRANSITION", Fb, "Trans1").Should().Be($"Created TRANSITION 'Trans1' at {Fb}^Trans1.");
        fake.CallLog.Should().Contain($"CreatePlcChild(parent={Fb}, name=Trans1, subType=616, label=TRANSITION)");
    }

    [Fact]
    public void Create_Transition_UnderAFolder_OrWithAnImplLanguage_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Create("MEMBER", "TRANSITION", "^POUs", "Trans1").Should().Contain("the parent must be a POU");
        Coord(fake).Create("MEMBER", "TRANSITION", Fb, "Trans1", implLanguage: "ST").Should().Contain("always created in ST");
        AssertNoLogoutNoWrite(fake);
    }

    // ---- elements without content: creation ----------------------------------

    [Theory]
    [InlineData("TEXTLIST", null, PlcItemType.TextList)]
    [InlineData("TEXTLIST", "LOCAL", PlcItemType.TextList)]
    [InlineData("IMAGEPOOL", null, PlcItemType.ImagePool)]
    [InlineData("UML", null, PlcItemType.UmlClassDiagram)]
    [InlineData("VISU", "MANAGER", PlcItemType.VisMan)]
    [InlineData("VISU", "VISUALIZATION", PlcItemType.VisObj)]
    [InlineData("RECIPE", "MANAGER", PlcItemType.RecipeManager)]
    public void Create_ContentlessElement_UsesTheGenericCreate_UnderAFolderOrTheProjectNode(string kind, string? subtype, int itemType)
    {
        var fake = OpenFake();
        string result = Coord(fake).Create(kind, subtype, "^POUs", "Thing");
        result.Should().StartWith("Created ").And.Contain("'Thing' at ^POUs^Thing.");
        fake.CallLog.Should().Contain(c => c.StartsWith($"CreatePlcChild(parent=^POUs, name=Thing, subType={itemType}", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_VisuAndRecipe_NeedASubtype()
    {
        var fake = OpenFake();
        Coord(fake).Create("VISU", null, "^POUs", "V").Should().Contain("subtype is required for kind=VISU");
        Coord(fake).Create("RECIPE", null, "^POUs", "R").Should().Contain("subtype is required for kind=RECIPE");
        fake.CallLog.Should().BeEmpty();
    }

    [Fact]
    public void Create_ChildOfAManager_NeedsTheManagerAsParent()
    {
        var fake = OpenFake();
        const string visMan = "^VisMan";
        const string recipeMan = "^RecipeMgr";
        fake.PlcNodeItemTypes[visMan] = PlcItemType.VisMan;
        fake.PlcNodeItemTypes[recipeMan] = PlcItemType.RecipeManager;

        Coord(fake).Create("VISU", "TARGET", visMan, "TargetVisu").Should().StartWith("Created ");
        Coord(fake).Create("VISU", "WEB", visMan, "WebVisu").Should().StartWith("Created ");
        Coord(fake).Create("RECIPE", "DEFINITION", recipeMan, "Rcp_A").Should().StartWith("Created ");

        Coord(fake).Create("VISU", "TARGET", "^POUs", "TargetVisu").Should().Contain("the parent must be a visualization manager");
        Coord(fake).Create("RECIPE", "DEFINITION", "^POUs", "Rcp_A").Should().Contain("the parent must be a recipe manager");
    }

    [Fact]
    public void Create_ProgramReference_NeedsATaskNode()
    {
        var fake = OpenFake();
        fake.PlcNodeItemTypes["^PlcTask"] = PlcItemType.Task;
        Coord(fake).Create("TASKREF", null, "^PlcTask", "MAIN").Should().Be("Created TASKREF (PROGRAM) 'MAIN' at ^PlcTask^MAIN.");
        Coord(fake).Create("TASKREF", null, "^POUs", "MAIN").Should().Contain("the parent must be a PLC task node");
    }

    [Theory]
    [InlineData("TEXTLIST", "GLOBAL")]
    [InlineData("TEXTLIST", "ENUMERATION")]
    [InlineData("IMAGEPOOL", "GLOBAL")]
    [InlineData("TREND", "MANAGER")]
    [InlineData("TREND", "OBJECT")]
    [InlineData("TASKREF", "TASK")]
    public void Create_ElementsXaeRefusesToCreate_SayWhy(string kind, string subtype)
    {
        var fake = OpenFake();
        Coord(fake).Create(kind, subtype, "^POUs", "X").Should().StartWith("[Error]").And.Contain("cannot be created through the Automation Interface");
        fake.CallLog.Should().BeEmpty();
    }

    [Theory]
    [InlineData("declaration")]
    [InlineData("returnType")]
    [InlineData("implLanguage")]
    [InlineData("accessors")]
    public void Create_ContentlessElement_RejectsParametersThatDoNotApply(string parameter)
    {
        var fake = OpenFake();
        string result = Coord(fake).Create("TEXTLIST", null, "^POUs", "X",
            declaration: parameter == "declaration" ? "x" : null,
            returnType: parameter == "returnType" ? "INT" : null,
            implLanguage: parameter == "implLanguage" ? "ST" : null,
            accessors: parameter == "accessors" ? "GET" : null);
        result.Should().StartWith($"[Error] {parameter} does not apply");
        fake.CallLog.Should().BeEmpty();
    }

    // ---- graphical implementations (SFC, FBD, CFC, LD) are XML ---------------------

    private const string LdXml = "<NWL><XmlArchive><Data><o t=\"NWLImplementationObject\"/></Data></XmlArchive></NWL>";

    [Fact]
    public void Write_ImplementationOfAGraphicalPou_AcceptsXmlWithTheLanguagesRoot()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor\nVAR\nEND_VAR", LdXml, "LD");

        Coord(fake).Write(Fb, "POU", null, LdXml.Replace("NWLImplementationObject", "NWLImplementationObject2")).Should().StartWith("Wrote POU");
        fake.PouBodiesByPath[Fb].Implementation.Should().Contain("NWLImplementationObject2");
    }

    [Theory]
    [InlineData("LD", "NWL")]
    [InlineData("FBD", "NWL")]
    [InlineData("SFC", "SFC")]
    [InlineData("CFC", "CFC")]
    public void Write_ImplementationOfAGraphicalPou_RefusesTextAndTheWrongRoot_BeforeTheLogout(string language, string root)
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor", "<" + root + "/>", language);

        string notXml = Coord(fake).Write(Fb, "POU", null, "x := 1;");
        notXml.Should().StartWith("[Error]").And.Contain($"is implemented in {language}").And.Contain($"<{root}>").And.Contain("Nothing was changed");

        string wrongRoot = Coord(fake).Write(Fb, "POU", null, "<Other/>");
        wrongRoot.Should().Contain("the XML root is <Other>");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Write_ImplementationOfAnIlPou_StaysText()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath[Fb] = new PouBody("FUNCTION_BLOCK FB_Motor", "LD 1", "IL");
        Coord(fake).Write(Fb, "POU", null, "LD 2").Should().StartWith("Wrote POU");
    }

    [Fact]
    public void Write_GraphicalMember_UsesTheMemberLanguage()
    {
        var fake = OpenFake();
        fake.PouBodiesByPath["^POUs^FB_Motor^Run"] = new PouBody("METHOD Run : BOOL", "<NWL/>", "LD");
        Coord(fake).Write("^POUs^FB_Motor^Run", "MEMBER", null, "x := 1;").Should().Contain("is implemented in LD");
        Coord(fake).Write("^POUs^FB_Motor^Run", "MEMBER", null, "<NWL/>").Should().StartWith("Wrote MEMBER");
    }

    // ---- listing every catalog element ---------------------------------------------

    [Fact]
    public void List_ShowsTheContentlessElementsWithTheirSubtype()
    {
        var fake = OpenFake();
        fake.Pous.Add(new PouEntry("^TL", "TL", PlcKind.TextList, null, PlcItemType.TextList));
        fake.Pous.Add(new PouEntry("^VisMan^TargetVisu", "TargetVisu", PlcKind.Visu, null, PlcItemType.TargetVisu));
        fake.Pous.Add(new PouEntry("^PlcTask^MAIN", "MAIN", PlcKind.TaskRef, null, PlcItemType.ProgRef));

        var (summary, entries) = Coord(fake).List(null, null);
        summary.Should().Be("Found 3 elements (1 TEXTLIST, 1 VISU, 1 TASKREF).");
        entries.Select(e => e.Subtype).Should().Equal("LOCAL", "TARGET", "PROGRAM");

        Coord(fake).List("TASKREF", null).Entries.Should().ContainSingle().Which.Path.Should().Be("^PlcTask^MAIN");
        fake.CallLog.Should().Contain("ListPlcElements()");
    }

    [Fact]
    public void List_Members_IncludeTransitions()
    {
        var fake = OpenFake();
        fake.MembersByPou[Fb] = new()
        {
            new PouMemberEntry("^POUs^FB_Motor^Tr1", "Tr1", PlcMemberKind.Transition, PlcItemType.Transition),
        };
        var (_, entries) = Coord(fake).List("MEMBER", Fb);
        entries.Should().ContainSingle().Which.Subtype.Should().Be("TRANSITION");
    }

    [Theory]
    [InlineData("DUT", "STRUCT", "returnType", "does not apply to kind=DUT (subtype STRUCT)")]
    [InlineData("POU", "PROGRAM", "returnType", "only a FUNCTION, a METHOD and a PROPERTY")]
    [InlineData("GVL", "NORMAL", "implLanguage", "only a POU and its members")]
    [InlineData("MEMBER", "METHOD", "accessors", "only a PROPERTY has Get/Set accessors")]
    [InlineData("MEMBER", "ACTION", "declaration", "a ACTION has only an implementation")]
    [InlineData("FOLDER", null, "declaration", "a folder has no declaration")]
    public void Create_ParameterThatDoesNotApply_IsAnErrorBeforeAnythingHappens(string kind, string? subtype, string parameter, string expected)
    {
        var fake = OpenFake();
        string result = Coord(fake).Create(kind, subtype, "^POUs", "X",
            declaration: parameter == "declaration" ? "x" : null,
            returnType: parameter == "returnType" ? "INT" : null,
            implLanguage: parameter == "implLanguage" ? "ST" : null,
            accessors: parameter == "accessors" ? "GET" : null);
        result.Should().StartWith($"[Error] {parameter} does not apply").And.Contain(expected);
        fake.CallLog.Should().BeEmpty();
    }

    // ---- delete --------------------------------------------------------------

    [Fact]
    public void Delete_Dut_DelegatesToTheVerifiedDelete()
    {
        var fake = OpenFake();
        Coord(fake).Delete(Dut, "DUT").Should().StartWith("Deleted DUT (STRUCT)");
        fake.CallLog.Should().Contain($"DeletePlcObject({Dut})");
    }

    [Fact]
    public void Delete_RecursiveOnANonFolder_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).Delete(Dut, "DUT", recursive: true).Should().Contain("recursive does not apply to kind=DUT");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Delete_EmptyFolder_IsDeletedThroughTheFolderPath()
    {
        var fake = OpenFake();
        fake.PlcNodeChildCounts["^POUs"] = 0;
        Coord(fake).Delete("^POUs", "FOLDER").Should().StartWith("Deleted FOLDER '^POUs'");
        fake.CallLog.Should().Contain("DeletePlcFolder(^POUs)");
    }

    [Fact]
    public void Delete_NonEmptyFolder_NeedsRecursive()
    {
        var fake = OpenFake();
        fake.PlcNodeChildCounts["^POUs"] = 3;
        Coord(fake).Delete("^POUs", "FOLDER").Should().Contain("is not empty (3 direct children)").And.Contain("recursive=true");
        AssertNoLogoutNoWrite(fake);

        Coord(fake).Delete("^POUs", "FOLDER", recursive: true).Should().StartWith("Deleted FOLDER");
        fake.CallLog.Should().Contain("DeletePlcFolder(^POUs)");
    }

    [Fact]
    public void Delete_FolderWithUnknownChildCount_IsNotMistakenForEmpty()
    {
        var fake = OpenFake();
        Coord(fake).Delete("^POUs", "FOLDER").Should().Contain("Cannot tell whether folder");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Delete_FolderThatSurvives_FailsTheReadBack()
    {
        var fake = OpenFake();
        fake.PlcNodeChildCounts["^POUs"] = 0;
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.DeletePlcFolder));
        Coord(fake).Delete("^POUs", "FOLDER").Should().StartWith("[Error]").And.Contain("did not land");
    }

    [Fact]
    public void Delete_ProjectRoot_IsNeverDeletable()
    {
        var fake = OpenFake();
        Coord(fake).Delete("^TIPC^Plc^Plc Project", "FOLDER").Should().Contain("PLC system node");
        AssertNoLogoutNoWrite(fake);
    }

    // ---- rename --------------------------------------------------------------

    [Fact]
    public void Rename_Pou_RequiresAnIdentifier()
    {
        var fake = OpenFake();
        Coord(fake).Rename(Fb, "POU", "1bad").Should().StartWith("[Error]");
        AssertNoLogoutNoWrite(fake);
    }

    [Fact]
    public void Rename_Folder_AcceptsANonIdentifierName()
    {
        var fake = OpenFake();
        Coord(fake).Rename("^POUs", "FOLDER", "01_Application").Should().StartWith("Renamed '^POUs'");
    }

    [Fact]
    public void Rename_PropertyAccessor_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coord(fake).Rename("^POUs^FB_Motor^Speed^Get", "MEMBER", "Read").Should().Contain("fixed names");
        AssertNoLogoutNoWrite(fake);
    }

    // ---- list ----------------------------------------------------------------

    [Fact]
    public void List_FiltersByKind_AndFillsTheSubtypeFromTheItemType()
    {
        var fake = OpenFake();
        fake.Pous.Add(new PouEntry("^POUs^FB_Motor", "FB_Motor", PouKind.Pou, "ST", PlcItemType.PouFunctionBlock));
        fake.Pous.Add(new PouEntry(Dut, "ST_Point", PouKind.Dut, null, PlcItemType.DutStruct));
        fake.Pous.Add(new PouEntry(Gvl, "GVL_Main", PouKind.Gvl, null, PlcItemType.Gvl));

        var (summary, entries) = Coord(fake).List("dut", null);
        summary.Should().Be("Found 1 element (1 DUT).");
        entries.Should().ContainSingle().Which.Should().Be(new PlcElementEntry(Dut, "ST_Point", "DUT", "STRUCT", null));
    }

    [Fact]
    public void List_WithoutArguments_ListsTheTreeElements()
    {
        var fake = OpenFake();
        fake.Pous.Add(new PouEntry("^POUs^FB_Motor", "FB_Motor", PouKind.Pou, "ST", PlcItemType.PouFunctionBlock));
        fake.Pous.Add(new PouEntry(Gvl, "GVL_Main", PouKind.Gvl, null, PlcItemType.Gvl));
        Coord(fake).List(null, null).Summary.Should().Be("Found 2 elements (1 POU, 1 GVL).");
    }

    [Fact]
    public void List_ParentPath_RestrictsToTheSubtree_AndRecursiveFalseToDirectChildren()
    {
        var fake = OpenFake();
        fake.Pous.Add(new PouEntry("^POUs", "POUs", PouKind.Folder, null, PlcItemType.Folder));
        fake.Pous.Add(new PouEntry("^POUs^FB_Motor", "FB_Motor", PouKind.Pou, "ST", PlcItemType.PouFunctionBlock));
        fake.Pous.Add(new PouEntry("^POUs^Sub", "Sub", PouKind.Folder, null, PlcItemType.Folder));
        fake.Pous.Add(new PouEntry("^POUs^Sub^FB_Deep", "FB_Deep", PouKind.Pou, "ST", PlcItemType.PouFunctionBlock));
        fake.Pous.Add(new PouEntry(Gvl, "GVL_Main", PouKind.Gvl, null, PlcItemType.Gvl));

        Coord(fake).List(null, "^POUs").Entries.Select(e => e.Name).Should().BeEquivalentTo("FB_Motor", "Sub", "FB_Deep");
        Coord(fake).List(null, "^POUs", recursive: false).Entries.Select(e => e.Name).Should().BeEquivalentTo("FB_Motor", "Sub");
    }

    [Fact]
    public void List_MemberKindWithoutAParent_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).List("MEMBER", null).Summary.Should().StartWith("[Error] parentPath");
    }

    [Fact]
    public void List_UnderAPou_ListsItsMembersWithSubtypes()
    {
        var fake = OpenFake();
        fake.MembersByPou[Fb] = new()
        {
            new PouMemberEntry("^POUs^FB_Motor^Run", "Run", PlcMemberKind.Method, PlcItemType.Method),
            new PouMemberEntry("^POUs^FB_Motor^Speed", "Speed", PlcMemberKind.Property, PlcItemType.Property),
            new PouMemberEntry("^POUs^FB_Motor^Speed^Get", "Get", PlcMemberKind.Get, PlcItemType.PropertyGet),
        };

        var (summary, entries) = Coord(fake).List(null, Fb);
        summary.Should().Be("Found 3 elements (3 MEMBER).");
        entries.Select(e => e.Subtype).Should().Equal("METHOD", "PROPERTY", "GET");

        Coord(fake).List("MEMBER", Fb, recursive: false).Entries.Select(e => e.Name).Should().BeEquivalentTo("Run", "Speed");
    }

    [Fact]
    public void List_UnderAPou_AlsoListsTheFoldersTheMembersAreGroupedIn()
    {
        var fake = OpenFake();
        fake.MembersByPou[Fb] = new()
        {
            new PouMemberEntry("^POUs^FB_Motor^Members^Speed", "Speed", PlcMemberKind.Property, PlcItemType.Property),
        };
        fake.FoldersByPou[Fb] = new() { "^POUs^FB_Motor^Members" };

        var (summary, entries) = Coord(fake).List(null, Fb);
        summary.Should().Be("Found 2 elements (1 MEMBER, 1 FOLDER).");
        entries.Select(e => e.Kind).Should().BeEquivalentTo("MEMBER", "FOLDER");

        Coord(fake).List("FOLDER", Fb).Entries.Should().ContainSingle().Which.Path.Should().Be("^POUs^FB_Motor^Members");
        Coord(fake).List("MEMBER", Fb).Entries.Should().ContainSingle().Which.Kind.Should().Be("MEMBER");
        fake.CallLog.Should().NotContain("ListPous()", "a POU is not walked through the tree listing");
    }

    [Fact]
    public void List_MemberKindUnderAFolder_IsAnError()
    {
        var fake = OpenFake();
        Coord(fake).List("MEMBER", "^POUs").Summary.Should().Contain("cannot own members");
    }

    [Fact]
    public void List_NoSolution_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Coord(fake).List(null, null).Summary.Should().StartWith("[Error]");
    }
}
