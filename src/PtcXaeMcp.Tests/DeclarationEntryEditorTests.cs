using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// Pure text function: structured add / set / remove of declaration entries.
// The property under test everywhere: an edit changes only what it names; the rest of the text stays byte-identical.
public class DeclarationEntryEditorTests
{
    private const DeclarationShape Var = DeclarationShape.VarSections;
    private const DeclarationShape Struct = DeclarationShape.StructBody;
    private const DeclarationShape Enum = DeclarationShape.EnumBody;

    private const string Fb =
        "FUNCTION_BLOCK FB_Motor EXTENDS FB_Base IMPLEMENTS I_A, I_B\n" +
        "VAR_INPUT\n" +
        "    bEnable : BOOL; // enables the drive\n" +
        "    rSpeed : REAL := 1.5;\n" +
        "END_VAR\n" +
        "VAR_OUTPUT\n" +
        "    bBusy : BOOL;\n" +
        "END_VAR\n" +
        "VAR\n" +
        "    nCount : INT;\n" +
        "END_VAR\n";

    private const string StructDut =
        "TYPE ST_Point :\n" +
        "STRUCT\n" +
        "    X : INT; // horizontal\n" +
        "    Y : INT := 7;\n" +
        "END_STRUCT\n" +
        "END_TYPE\n";

    private const string EnumDut =
        "{attribute 'qualified_only'}\n" +
        "TYPE E_Color :\n" +
        "(\n" +
        "    Red := 0, // first\n" +
        "    Green := 1,\n" +
        "    Blue := 2\n" +
        ") UINT;\n" +
        "END_TYPE\n";

    private const string Gvl =
        "VAR_GLOBAL\n" +
        "    gA : INT;\n" +
        "END_VAR\n" +
        "VAR_GLOBAL PERSISTENT\n" +
        "    gP : DINT;\n" +
        "END_VAR\n";

    private static EntryEditResult Apply(string text, DeclarationShape shape, EntryEdit edit) =>
        DeclarationEntryEditor.Apply(text, shape, edit);

    private static string Ok(string text, DeclarationShape shape, EntryEdit edit)
    {
        var r = Apply(text, shape, edit);
        r.Error.Should().BeNull();
        return r.Text!;
    }

    private static string Err(string text, DeclarationShape shape, EntryEdit edit)
    {
        var r = Apply(text, shape, edit);
        r.Text.Should().BeNull("a refused edit returns no text");
        r.Error.Should().StartWith("[Error]");
        return r.Error!;
    }

    // ── list / parse ──────────────────────────────────────────────────────

    [Fact]
    public void List_VarSections_ReadsEverythingAnEntryCarries()
    {
        const string text =
            "FUNCTION_BLOCK FB_X\n" +
            "VAR_INPUT CONSTANT\n" +
            "    {attribute 'hide'}\n" +
            "    {attribute 'displaymode' := 'hex'}\n" +
            "    nMask AT %I* : DWORD := 16#FF; // mask\n" +
            "    aTab : ARRAY[0..3] OF INT := [1, 2, 3, 4];\n" +
            "    pData : POINTER TO BYTE;\n" +
            "    rRef : REFERENCE TO REAL;\n" +
            "    sText : STRING(80) := 'a;b'; (* semicolon in a string *)\n" +
            "    stP : ST_Point := (X := 1, Y := 2);\n" +
            "    fbT : TON := (PT := T#1S);\n" +
            "END_VAR\n";

        var (entries, error) = DeclarationEntryEditor.List(text, Var, null);
        error.Should().BeNull();
        entries!.Select(e => e.Name).Should().Equal("nMask", "aTab", "pData", "rRef", "sText", "stP", "fbT");
        entries.Should().OnlyContain(e => e.Section == "VAR_INPUT CONSTANT" && e.Problem == null);

        var mask = entries[0];
        mask.Type.Should().Be("DWORD");
        mask.Initial.Should().Be("16#FF");
        mask.Address.Should().Be("AT %I*");
        mask.Comment.Should().Be("mask");
        mask.Attributes.Should().Equal("{attribute 'hide'}", "{attribute 'displaymode' := 'hex'}");
        mask.Line.Should().Be(5);

        entries[1].Type.Should().Be("ARRAY[0..3] OF INT");
        entries[1].Initial.Should().Be("[1, 2, 3, 4]");
        entries[2].Type.Should().Be("POINTER TO BYTE");
        entries[3].Type.Should().Be("REFERENCE TO REAL");
        entries[4].Initial.Should().Be("'a;b'");
        entries[4].Comment.Should().Be("semicolon in a string");
        entries[5].Initial.Should().Be("(X := 1, Y := 2)");
        entries[6].Type.Should().Be("TON");
    }

    [Fact]
    public void List_FunctionBlockWithSeveralSections_TagsEachEntryWithItsSection()
    {
        var (entries, _) = DeclarationEntryEditor.List(Fb, Var, null);
        entries!.Select(e => (e.Section, e.Name)).Should().Equal(
            ("VAR_INPUT", "bEnable"), ("VAR_INPUT", "rSpeed"), ("VAR_OUTPUT", "bBusy"), ("VAR", "nCount"));

        var (outputs, _) = DeclarationEntryEditor.List(Fb, Var, "var_output");
        outputs!.Select(e => e.Name).Should().Equal("bBusy");
    }

    [Fact]
    public void List_UnknownSection_NamesTheOnesThatExist()
    {
        var (entries, error) = DeclarationEntryEditor.List(Fb, Var, "VAR_TEMP");
        entries.Should().BeNull();
        error.Should().Contain("No section 'VAR_TEMP'").And.Contain("VAR_INPUT, VAR_OUTPUT, VAR");
    }

    [Fact]
    public void List_Struct_ReadsFieldsAndComments()
    {
        var (entries, _) = DeclarationEntryEditor.List(StructDut, Struct, null);
        entries!.Select(e => (e.Section, e.Name, e.Type, e.Initial, e.Comment)).Should().Equal(
            ("STRUCT", "X", "INT", null, "horizontal"), ("STRUCT", "Y", "INT", "7", null));
    }

    [Fact]
    public void List_Union_UsesTheUnionKeyword()
    {
        const string text = "TYPE U_Raw :\nUNION\n    w : WORD;\n    b : BYTE;\nEND_UNION\nEND_TYPE\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Struct, null);
        entries!.Select(e => e.Section).Should().OnlyContain(s => s == "UNION");
        entries.Select(e => e.Name).Should().Equal("w", "b");
    }

    [Fact]
    public void List_Enum_ReadsValuesAndIgnoresTheBaseType()
    {
        var (entries, _) = DeclarationEntryEditor.List(EnumDut, Enum, null);
        entries!.Select(e => (e.Name, e.Initial, e.Comment)).Should().Equal(
            ("Red", "0", "first"), ("Green", "1", null), ("Blue", "2", null));
        entries.Should().OnlyContain(e => e.Section == "ENUM" && e.Type == null);
    }

    [Fact]
    public void List_EnumWithoutExplicitValues_HasNullInitial()
    {
        const string text = "TYPE E :\n(\n    A,\n    B\n);\nEND_TYPE\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Enum, null);
        entries!.Select(e => (e.Name, e.Initial)).Should().Equal(("A", null), ("B", null));
    }

    [Fact]
    public void List_SeveralNamesInOneEntry_AreFlagged_NotGuessed()
    {
        const string text = "VAR\n    a, b : INT;\n    c : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries![0].Problem.Should().Contain("several names");
        entries[1].Problem.Should().BeNull();
    }

    [Fact]
    public void List_TwoEntriesOnOneLine_AreFlagged()
    {
        const string text = "VAR\n    a : INT; b : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries!.Should().OnlyContain(e => e.Problem != null && e.Problem.Contains("shares its line", StringComparison.Ordinal));
    }

    [Fact]
    public void List_EntryWithoutSemicolon_IsFlagged()
    {
        const string text = "VAR\n    a : INT\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries![0].Problem.Should().Contain("not terminated");
    }

    [Fact]
    public void List_UnterminatedBlockComment_IsAnError()
    {
        var (entries, error) = DeclarationEntryEditor.List("VAR\n (* open\n a : INT;\nEND_VAR\n", Var, null);
        entries.Should().BeNull();
        error.Should().Contain("Unterminated block comment");
    }

    [Fact]
    public void List_SectionWithoutEndVar_IsAnError()
    {
        var (_, error) = DeclarationEntryEditor.List("VAR_INPUT\n a : INT;\n", Var, null);
        error.Should().Contain("has no END_VAR");
    }

    [Fact]
    public void List_NestedBlockComments_AndCommentedOutEntries_AreNotEntries()
    {
        const string text = "VAR\n    (* old : INT; (* nested ; *) still comment ; *)\n    // gone : INT;\n    a : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries!.Select(e => e.Name).Should().Equal("a");
    }

    [Fact]
    public void List_VarWordInAComment_DoesNotOpenASection()
    {
        const string text = "// VAR_INPUT is described below\nVAR\n    a : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries!.Select(e => e.Section).Should().OnlyContain(s => s == "VAR");
    }

    [Fact]
    public void List_AttributeSeparatedByABlankLine_DoesNotBelongToTheEntry()
    {
        const string text = "VAR\n    {attribute 'hide'}\n\n    a : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries![0].Attributes.Should().BeEmpty();
    }

    [Fact]
    public void List_CrLf_IsHandled()
    {
        string text = Fb.Replace("\n", "\r\n", StringComparison.Ordinal);
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries!.Select(e => e.Name).Should().Equal("bEnable", "rSpeed", "bBusy", "nCount");
        entries[0].Comment.Should().Be("enables the drive");
        entries[0].Line.Should().Be(3);
    }

    [Fact]
    public void List_Gvl_ReadsPersistentSectionHeader()
    {
        var (entries, _) = DeclarationEntryEditor.List(Gvl, Var, null);
        entries!.Select(e => (e.Section, e.Name)).Should().Equal(("VAR_GLOBAL", "gA"), ("VAR_GLOBAL PERSISTENT", "gP"));
    }

    [Fact]
    public void List_AliasHasNoBody()
    {
        var (_, error) = DeclarationEntryEditor.List("TYPE A_Handle : UDINT;\nEND_TYPE\n", Struct, null);
        error.Should().Contain("No STRUCT or UNION body");
    }

    // ── add ───────────────────────────────────────────────────────────────

    [Fact]
    public void Add_AppendsToTheEndOfTheSection_AndLeavesTheRestUntouched()
    {
        string result = Ok(Fb, Var, new(EntryOp.Add, "bReset", "VAR_INPUT", Type: "BOOL"));
        result.Should().Be(Fb.Replace(
            "    rSpeed : REAL := 1.5;\nEND_VAR\n",
            "    rSpeed : REAL := 1.5;\n    bReset : BOOL;\nEND_VAR\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_WithEverythingAnEntryCarries()
    {
        string result = Ok(Fb, Var, new(EntryOp.Add, "nIn", "VAR_INPUT", Type: "DWORD", InitialValue: "16#FF",
            Address: "%I*", Comment: "raw input", Attributes: ["hide", "attribute 'TcInitSymbol'", "{attribute 'x' := 'y'}"]));
        result.Should().Contain(
            "    {attribute 'hide'}\n    {attribute 'TcInitSymbol'}\n    {attribute 'x' := 'y'}\n" +
            "    nIn AT %I* : DWORD := 16#FF; // raw input\nEND_VAR\nVAR_OUTPUT");
    }

    [Fact]
    public void Add_AfterANamedEntry()
    {
        string result = Ok(Fb, Var, new(EntryOp.Add, "nMid", "VAR_INPUT", Type: "INT", AfterName: "bEnable"));
        var (entries, _) = DeclarationEntryEditor.List(result, Var, null);
        entries!.Select(e => e.Name).Should().Equal("bEnable", "nMid", "rSpeed", "bBusy", "nCount");
    }

    [Fact]
    public void Add_WithoutSection_UsesTheOnlySection_ButAsksWhenThereAreSeveral()
    {
        const string single = "VAR_INPUT\n    a : INT;\nEND_VAR\n";
        Ok(single, Var, new(EntryOp.Add, "b", Type: "INT")).Should().Contain("    b : INT;");

        Err(Fb, Var, new(EntryOp.Add, "b", Type: "INT")).Should().Contain("Several sections exist; pass section");
    }

    [Fact]
    public void Add_CreatesAMissingSection_AfterTheLastOne()
    {
        string result = Ok(Fb, Var, new(EntryOp.Add, "nTmp", "VAR_TEMP", Type: "INT"));
        result.Should().Be(Fb + "VAR_TEMP\n    nTmp : INT;\nEND_VAR\n");
    }

    [Fact]
    public void Add_CreatesAPersistentSection_InAGvl()
    {
        string result = Ok("VAR_GLOBAL\n    gA : INT;\nEND_VAR\n", Var, new(EntryOp.Add, "gP", "var_global   persistent", Type: "DINT"));
        result.Should().Be("VAR_GLOBAL\n    gA : INT;\nEND_VAR\nVAR_GLOBAL PERSISTENT\n    gP : DINT;\nEND_VAR\n");
    }

    [Fact]
    public void Add_CreatesTheFirstSection_ForAMethodWithoutVariables()
    {
        string result = Ok("METHOD Run : BOOL\n", Var, new(EntryOp.Add, "bGo", "VAR_INPUT", Type: "BOOL"));
        result.Should().Be("METHOD Run : BOOL\nVAR_INPUT\n    bGo : BOOL;\nEND_VAR\n");
    }

    // Live finding (2026-10-07): XAE returns a declaration whose last line is END_VAR WITHOUT a line break. A new section
    // appended after it was glued to END_VAR ("END_VARVAR_TEMP") — and the read-back of the write still matched.
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Add_NewSection_AfterALastEndVarWithoutLineBreak_StartsOnItsOwnLine(string nl)
    {
        string text = string.Join(nl, "FUNCTION_BLOCK FB_X", "VAR_INPUT", "\tbEnable : BOOL;", "END_VAR", "VAR", "\tnCount : INT;", "END_VAR");
        string result = Ok(text, Var, new(EntryOp.Add, "nTmp", "VAR_TEMP", Type: "INT"));

        result.Should().Be(text + nl + string.Join(nl, "VAR_TEMP", "\tnTmp : INT;", "END_VAR") + nl);
        var (entries, _) = DeclarationEntryEditor.List(result, Var, null);
        entries!.Select(e => (e.Section, e.Name)).Should().Equal(("VAR_INPUT", "bEnable"), ("VAR", "nCount"), ("VAR_TEMP", "nTmp"));
    }

    [Fact]
    public void Add_ToTheLastSection_WhenTheTextHasNoTrailingNewline()
    {
        const string text = "VAR_GLOBAL\n\tgA : INT;\nEND_VAR";
        Ok(text, Var, new(EntryOp.Add, "gB", "VAR_GLOBAL", Type: "INT")).Should().Be("VAR_GLOBAL\n\tgA : INT;\n\tgB : INT;\nEND_VAR");
    }

    [Fact]
    public void Add_CreatesTheFirstSection_WhenTheTextHasNoTrailingNewline()
    {
        string result = Ok("METHOD Run : BOOL", Var, new(EntryOp.Add, "bGo", "VAR_INPUT", Type: "BOOL"));
        result.Should().Be("METHOD Run : BOOL\r\nVAR_INPUT\r\n    bGo : BOOL;\r\nEND_VAR\r\n");
    }

    [Fact]
    public void Add_RefusesAnInvalidSectionName()
    {
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR_BANANA", Type: "INT")).Should().Contain("is not a section");
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR_INPUT FANCY", Type: "INT")).Should().Contain("is not a section");
    }

    [Fact]
    public void Add_DuplicateName_IsRefused_AcrossSections_IgnoringCase()
    {
        Err(Fb, Var, new(EntryOp.Add, "BENABLE", "VAR_OUTPUT", Type: "BOOL")).Should().Contain("already exists");
    }

    [Fact]
    public void Add_PreservesCrLf()
    {
        string crlf = Fb.Replace("\n", "\r\n", StringComparison.Ordinal);
        string result = Ok(crlf, Var, new(EntryOp.Add, "bReset", "VAR_INPUT", Type: "BOOL"));
        result.Should().Contain("    bReset : BOOL;\r\nEND_VAR");
        result.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n");
    }

    [Fact]
    public void Add_WhenEndVarSharesTheLastLine_OpensANewLine()
    {
        string result = Ok("VAR a : INT; END_VAR\n", Var, new(EntryOp.Add, "b", "VAR", Type: "INT"));
        var (entries, _) = DeclarationEntryEditor.List(result, Var, null);
        entries!.Select(e => e.Name).Should().Equal("a", "b");
        result.Should().Contain("\n    b : INT;\nEND_VAR");
    }

    [Fact]
    public void Add_ToAnEmptySection_UsesAnIndentFromElsewhere()
    {
        string result = Ok("VAR_INPUT\n    a : INT;\nEND_VAR\nVAR_OUTPUT\nEND_VAR\n", Var, new(EntryOp.Add, "q", "VAR_OUTPUT", Type: "BOOL"));
        result.Should().Contain("VAR_OUTPUT\n    q : BOOL;\nEND_VAR");
    }

    [Fact]
    public void Add_ToAStruct()
    {
        string result = Ok(StructDut, Struct, new(EntryOp.Add, "Z", Type: "INT", InitialValue: "0", Comment: "depth"));
        result.Should().Be(StructDut.Replace("END_STRUCT", "    Z : INT := 0; // depth\nEND_STRUCT", StringComparison.Ordinal));
    }

    // A comment passed with its own markers (often echoed from list_entries or written like in the source) used to
    // get a second "//": "// // x".
    [Theory]
    [InlineData("// depth")]
    [InlineData("//depth")]
    [InlineData("(* depth *)")]
    [InlineData("/* depth */")]
    [InlineData("  // // depth  ")]
    public void Add_CommentPassedWithItsMarkers_IsWrittenOnce(string comment)
    {
        string result = Ok(StructDut, Struct, new(EntryOp.Add, "Z", Type: "INT", Comment: comment));
        result.Should().Contain("    Z : INT; // depth\n").And.NotContain("// //").And.NotContain("(*");
    }

    [Theory]
    [InlineData("// new text")]
    [InlineData("(* new text *)")]
    public void Set_CommentPassedWithItsMarkers_ReplacesWithoutDoubling(string comment)
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "bEnable", Comment: comment));
        result.Should().Be(Fb.Replace("// enables the drive", "// new text", StringComparison.Ordinal));
    }

    [Fact]
    public void Set_CommentWithMarkersOnly_AddsOneComment_WhereThereWasNone()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "nCount", Comment: "// counter"));
        result.Should().Contain("    nCount : INT; // counter\n").And.NotContain("// //");
    }

    [Fact]
    public void Set_CommentOfOnlyMarkers_RemovesTheComment()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "bEnable", Comment: "//"));
        result.Should().Contain("    bEnable : BOOL;\n");
    }

    [Theory]
    [InlineData("x", "x")]
    [InlineData("// x", "x")]
    [InlineData("(* a // b *)", "a // b")]
    [InlineData("a // b", "a // b")]
    [InlineData("/**/", "")]
    public void CommentText_DropsOnlyTheOuterMarkers(string input, string expected)
        => DeclarationEntryEditor.CommentText(input).Should().Be(expected);

    [Fact]
    public void Add_ToAStruct_RejectsASectionThatIsNotTheStruct()
    {
        Err(StructDut, Struct, new(EntryOp.Add, "Z", "VAR_INPUT", Type: "INT")).Should().Contain("has one section, 'STRUCT'");
    }

    [Fact]
    public void Add_RequiresAType_ExceptForEnumValues()
    {
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR_INPUT")).Should().Contain("type is required");
    }

    [Fact]
    public void Add_RejectsInvalidAndReservedNames()
    {
        Err(Fb, Var, new(EntryOp.Add, "1bad", "VAR_INPUT", Type: "INT")).Should().StartWith("[Error]");
        Err(Fb, Var, new(EntryOp.Add, "CALC", "VAR_INPUT", Type: "INT")).Should().StartWith("[Error]");
        Err(Fb, Var, new(EntryOp.Add, "a b", "VAR_INPUT", Type: "INT")).Should().StartWith("[Error]");
    }

    [Theory]
    [InlineData("INT; evil : INT")]
    [InlineData("INT\nevil")]
    [InlineData("ARRAY[0..3 OF INT")]
    [InlineData("INT)")]
    public void Add_RejectsATypeThatWouldBreakTheEntryBoundaries(string type)
    {
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR_INPUT", Type: type)).Should().StartWith("[Error]");
    }

    [Fact]
    public void Add_AcceptsASemicolonInsideAStringInitialValue()
    {
        string result = Ok(Fb, Var, new(EntryOp.Add, "sText", "VAR", Type: "STRING", InitialValue: "'a;b'"));
        result.Should().Contain("sText : STRING := 'a;b';");
    }

    [Fact]
    public void Add_RejectsAMultiLineComment_AndABadAttribute()
    {
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR", Type: "INT", Comment: "a\nb")).Should().Contain("single line");
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR", Type: "INT", Attributes: ["not valid!"])).Should().Contain("Invalid attribute");
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR", Type: "INT", Attributes: ["{notanattribute 'x'}"])).Should().Contain("Invalid attribute");
    }

    [Fact]
    public void Add_RejectsValueOnAVariable_AndInitialValueOnAnEnum()
    {
        Err(Fb, Var, new(EntryOp.Add, "x", "VAR", Type: "INT", Value: "1")).Should().Contain("value applies to enum values only");
        Err(EnumDut, Enum, new(EntryOp.Add, "Pink", InitialValue: "3")).Should().Contain("use value");
        Err(EnumDut, Enum, new(EntryOp.Add, "Pink", Type: "INT")).Should().Contain("type does not apply");
    }

    // enum

    [Fact]
    public void Add_EnumValueAtTheEnd_GivesTheFormerLastValueAComma_BeforeItsComment()
    {
        const string text = "TYPE E :\n(\n    A := 0, // first\n    B := 1 // last\n);\nEND_TYPE\n";
        string result = Ok(text, Enum, new(EntryOp.Add, "C", Value: "2", Comment: "new"));
        result.Should().Be("TYPE E :\n(\n    A := 0, // first\n    B := 1, // last\n    C := 2 // new\n);\nEND_TYPE\n");
    }

    [Fact]
    public void Add_EnumValueInTheMiddle_GetsItsOwnComma()
    {
        string result = Ok(EnumDut, Enum, new(EntryOp.Add, "Pink", Value: "9", AfterName: "Red"));
        var (entries, _) = DeclarationEntryEditor.List(result, Enum, null);
        entries!.Select(e => e.Name).Should().Equal("Red", "Pink", "Green", "Blue");
        result.Should().Contain("    Pink := 9,\n");
        result.Should().Contain("    Blue := 2\n)");
    }

    [Fact]
    public void Add_EnumValueAfterTheLastOne_IsTheNewLast()
    {
        string result = Ok(EnumDut, Enum, new(EntryOp.Add, "Pink", AfterName: "Blue"));
        result.Should().Contain("    Blue := 2,\n    Pink\n) UINT;");
    }

    [Fact]
    public void Add_EnumValueToAnEmptyEnum()
    {
        string result = Ok("TYPE E :\n(\n);\nEND_TYPE\n", Enum, new(EntryOp.Add, "A", Value: "0"));
        result.Should().Be("TYPE E :\n(\n    A := 0\n);\nEND_TYPE\n");
    }

    [Fact]
    public void Add_EnumValue_KeepsTheAttributePragmaAndBaseType()
    {
        string result = Ok(EnumDut, Enum, new(EntryOp.Add, "Pink"));
        result.Should().StartWith("{attribute 'qualified_only'}\nTYPE E_Color :").And.Contain(") UINT;\nEND_TYPE");
    }

    // ── set ───────────────────────────────────────────────────────────────

    [Fact]
    public void Set_Type_ChangesOnlyTheType()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "rSpeed", Type: "LREAL"));
        result.Should().Be(Fb.Replace("rSpeed : REAL := 1.5;", "rSpeed : LREAL := 1.5;", StringComparison.Ordinal));
    }

    [Fact]
    public void Set_InitialValue_Replaced_Added_Removed()
    {
        Ok(Fb, Var, new(EntryOp.Set, "rSpeed", InitialValue: "2.5")).Should().Contain("rSpeed : REAL := 2.5;");
        Ok(Fb, Var, new(EntryOp.Set, "nCount", InitialValue: "10")).Should().Contain("nCount : INT := 10;");
        Ok(Fb, Var, new(EntryOp.Set, "rSpeed", InitialValue: "")).Should().Contain("rSpeed : REAL;");
    }

    [Fact]
    public void Set_TypeAndInitialValueTogether()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "nCount", Type: "DINT", InitialValue: "5"));
        result.Should().Contain("nCount : DINT := 5;");
    }

    [Fact]
    public void Set_Rename_OnlyRenamesTheEntry_AndRefusesADuplicate()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "nCount", NewName: "nTotal"));
        result.Should().Be(Fb.Replace("nCount", "nTotal", StringComparison.Ordinal));
        Err(Fb, Var, new(EntryOp.Set, "nCount", NewName: "bBusy")).Should().Contain("already exists");
    }

    [Fact]
    public void Set_Comment_Replaced_Added_Removed()
    {
        Ok(Fb, Var, new(EntryOp.Set, "bEnable", Comment: "start")).Should().Contain("bEnable : BOOL; // start\n");
        Ok(Fb, Var, new(EntryOp.Set, "rSpeed", Comment: "target")).Should().Contain("rSpeed : REAL := 1.5; // target\n");
        Ok(Fb, Var, new(EntryOp.Set, "bEnable", Comment: "")).Should().Contain("bEnable : BOOL;\n");
    }

    [Fact]
    public void Set_Comment_KeepsABlockCommentStyle()
    {
        const string text = "VAR\n    a : INT; (* old *)\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Set, "a", Comment: "new")).Should().Be("VAR\n    a : INT; (* new *)\nEND_VAR\n");
    }

    [Fact]
    public void Set_Address_Added_Replaced_Removed()
    {
        Ok(Fb, Var, new(EntryOp.Set, "bEnable", Address: "%I*")).Should().Contain("bEnable AT %I* : BOOL;");
        const string text = "VAR\n    nIn AT %IW0 : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Set, "nIn", Address: "%IW4")).Should().Contain("nIn AT %IW4 : INT;");
        Ok(text, Var, new(EntryOp.Set, "nIn", Address: "")).Should().Contain("nIn : INT;");
    }

    [Fact]
    public void Set_Attributes_KeepTheExistingOnesWhenNotMentioned()
    {
        const string text = "VAR\n    {attribute 'hide'}\n    a : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Set, "a", Type: "DINT")).Should().Be("VAR\n    {attribute 'hide'}\n    a : DINT;\nEND_VAR\n");
    }

    [Fact]
    public void Set_Attributes_ReplaceAndRemove()
    {
        const string text = "VAR\n    {attribute 'hide'}\n    a : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Set, "a", Attributes: ["TcInitSymbol"]))
            .Should().Be("VAR\n    {attribute 'TcInitSymbol'}\n    a : INT;\nEND_VAR\n");
        Ok(text, Var, new(EntryOp.Set, "a", Attributes: []))
            .Should().Be("VAR\n    a : INT;\nEND_VAR\n");
        Ok("VAR\n    a : INT;\nEND_VAR\n", Var, new(EntryOp.Set, "a", Attributes: ["hide"]))
            .Should().Be("VAR\n    {attribute 'hide'}\n    a : INT;\nEND_VAR\n");
    }

    [Fact]
    public void Set_Attributes_OnAnEntryWithTwoAttributeLines()
    {
        const string text = "VAR\n    {attribute 'a'}\n    {attribute 'b'}\n    x : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Set, "x", Attributes: ["c"]))
            .Should().Be("VAR\n    {attribute 'c'}\n    x : INT;\nEND_VAR\n");
    }

    [Fact]
    public void Set_InlineAttribute_IsRemovedAloneAndReplacedByALine()
    {
        const string text = "VAR\n    x {attribute 'hide'} : INT;\nEND_VAR\n";
        var (entries, _) = DeclarationEntryEditor.List(text, Var, null);
        entries![0].Attributes.Should().Equal("{attribute 'hide'}");
        Ok(text, Var, new(EntryOp.Set, "x", Attributes: [])).Should().Be("VAR\n    x : INT;\nEND_VAR\n");
    }

    [Fact]
    public void Set_NothingToChange_IsAnError()
    {
        Err(Fb, Var, new(EntryOp.Set, "rSpeed")).Should().Contain("changes nothing");
    }

    [Fact]
    public void Set_UnknownEntry_AndWrongSection_AreErrors()
    {
        Err(Fb, Var, new(EntryOp.Set, "nope", Type: "INT")).Should().Contain("No entry 'nope'");
        Err(Fb, Var, new(EntryOp.Set, "bBusy", "VAR_INPUT", Type: "INT")).Should().Contain("No entry 'bBusy' in section 'VAR_INPUT'");
        Err(Fb, Var, new(EntryOp.Set, "bBusy", "VAR_TEMP", Type: "INT")).Should().Contain("No section 'VAR_TEMP'");
    }

    [Fact]
    public void Set_NameInTwoSections_NeedsTheSection()
    {
        const string text = "VAR_INPUT\n    a : INT;\nEND_VAR\nVAR_OUTPUT\n    a : INT;\nEND_VAR\n";
        Err(text, Var, new(EntryOp.Set, "a", Type: "DINT")).Should().Contain("occurs 2 times");
        Ok(text, Var, new(EntryOp.Set, "a", "VAR_OUTPUT", Type: "DINT")).Should().Contain("VAR_OUTPUT\n    a : DINT;");
    }

    [Fact]
    public void Set_OnAnEntryThatCannotBeEditedSafely_IsRefused()
    {
        Err("VAR\n    a, b : INT;\nEND_VAR\n", Var, new(EntryOp.Set, "a", Type: "DINT")).Should().Contain("several names");
        Err("VAR\n    a : INT; b : INT;\nEND_VAR\n", Var, new(EntryOp.Set, "a", Type: "DINT")).Should().Contain("shares its line");
    }

    [Fact]
    public void Set_StructFieldAndEnumValue()
    {
        Ok(StructDut, Struct, new(EntryOp.Set, "X", Type: "DINT", Comment: "col")).Should().Contain("X : DINT; // col");
        Ok(EnumDut, Enum, new(EntryOp.Set, "Green", Value: "10")).Should().Contain("Green := 10,");
        Ok(EnumDut, Enum, new(EntryOp.Set, "Green", Value: "")).Should().Contain("    Green,\n");
        Ok(EnumDut, Enum, new(EntryOp.Set, "Blue", Value: "7")).Should().Contain("    Blue := 7\n)");
        Ok("TYPE E :\n(\n    A,\n    B\n);\nEND_TYPE\n", Enum, new(EntryOp.Set, "B", Value: "4")).Should().Contain("    B := 4\n)");
    }

    [Fact]
    public void Set_EnumValueComment_OnTheLastValue()
    {
        Ok(EnumDut, Enum, new(EntryOp.Set, "Blue", Comment: "end")).Should().Contain("    Blue := 2 // end\n)");
    }

    [Fact]
    public void Set_PreservesEverythingElse_Byte_ForByte()
    {
        string result = Ok(Fb, Var, new(EntryOp.Set, "bBusy", Comment: "busy flag"));
        string before = Fb.Replace("    bBusy : BOOL;\n", "", StringComparison.Ordinal);
        result.Replace("    bBusy : BOOL; // busy flag\n", "", StringComparison.Ordinal).Should().Be(before);
    }

    // ── remove ────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_RemovesTheWholeLine_IncludingItsComment()
    {
        string result = Ok(Fb, Var, new(EntryOp.Remove, "bEnable"));
        result.Should().Be(Fb.Replace("    bEnable : BOOL; // enables the drive\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_TakesTheAttributeLinesWithIt()
    {
        const string text = "VAR\n    {attribute 'hide'}\n    a : INT;\n    b : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Remove, "a")).Should().Be("VAR\n    b : INT;\nEND_VAR\n");
    }

    [Fact]
    public void Remove_KeepsAnOwnLineCommentAboveTheEntry()
    {
        const string text = "VAR\n    // a counter\n    a : INT;\nEND_VAR\n";
        Ok(text, Var, new(EntryOp.Remove, "a")).Should().Be("VAR\n    // a counter\nEND_VAR\n");
    }

    [Fact]
    public void Remove_TheOnlyEntry_LeavesAnEmptySection()
    {
        Ok("VAR\n    a : INT;\nEND_VAR\n", Var, new(EntryOp.Remove, "a")).Should().Be("VAR\nEND_VAR\n");
    }

    [Fact]
    public void Remove_FromAStruct()
    {
        Ok(StructDut, Struct, new(EntryOp.Remove, "Y")).Should().Be(StructDut.Replace("    Y : INT := 7;\n", "", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_LastEnumValue_DropsTheCommaOfTheOneBefore()
    {
        string result = Ok(EnumDut, Enum, new(EntryOp.Remove, "Blue"));
        result.Should().Contain("    Green := 1\n)");
        DeclarationEntryEditor.List(result, Enum, null).Entries!.Select(e => e.Name).Should().Equal("Red", "Green");
    }

    [Fact]
    public void Remove_LastEnumValue_KeepsTheCommentOfTheOneBefore()
    {
        const string text = "TYPE E :\n(\n    A, // a\n    B // b\n);\nEND_TYPE\n";
        Ok(text, Enum, new(EntryOp.Remove, "B")).Should().Be("TYPE E :\n(\n    A // a\n);\nEND_TYPE\n");
    }

    [Fact]
    public void Remove_MiddleEnumValue_NeedsNoCommaFix()
    {
        Ok(EnumDut, Enum, new(EntryOp.Remove, "Green")).Should().Contain("    Red := 0, // first\n    Blue := 2\n)");
    }

    [Fact]
    public void Remove_TheOnlyEnumValue()
    {
        Ok("TYPE E :\n(\n    A := 0\n);\nEND_TYPE\n", Enum, new(EntryOp.Remove, "A")).Should().Be("TYPE E :\n(\n);\nEND_TYPE\n");
    }

    [Fact]
    public void Remove_OnASharedLine_IsRefused()
    {
        Err("VAR\n    a : INT; b : INT;\nEND_VAR\n", Var, new(EntryOp.Remove, "a")).Should().Contain("shares its line");
        Err("TYPE E : (A, B, C);\nEND_TYPE\n", Enum, new(EntryOp.Remove, "B")).Should().Contain("shares its line");
    }

    [Fact]
    public void Remove_RejectsParametersThatDoNotApply()
    {
        Err(Fb, Var, new(EntryOp.Remove, "bEnable", Type: "INT")).Should().Contain("op=remove takes only name");
    }

    [Fact]
    public void Remove_UnknownName_IsAnError()
    {
        Err(Fb, Var, new(EntryOp.Remove, "nope")).Should().Contain("No entry 'nope'");
    }

    // ── cross-cutting ─────────────────────────────────────────────────────

    [Fact]
    public void AddThenRemove_RestoresTheOriginalText()
    {
        string added = Ok(Fb, Var, new(EntryOp.Add, "bReset", "VAR_INPUT", Type: "BOOL", Comment: "c", Attributes: ["hide"]));
        Ok(added, Var, new(EntryOp.Remove, "bReset")).Should().Be(Fb);

        string enumAdded = Ok(EnumDut, Enum, new(EntryOp.Add, "Pink", Value: "3"));
        Ok(enumAdded, Enum, new(EntryOp.Remove, "Pink")).Should().Be(EnumDut);
    }

    [Fact]
    public void MissingName_IsAnError()
    {
        Err(Fb, Var, new(EntryOp.Add, "  ", "VAR", Type: "INT")).Should().Contain("name is required");
    }

    [Fact]
    public void Edit_OfAnUnparsableDeclaration_ReportsTheParseError()
    {
        Err("VAR\n a : INT;\n", Var, new(EntryOp.Remove, "a")).Should().Contain("no END_VAR");
    }

    [Theory]
    [InlineData("hide", "{attribute 'hide'}")]
    [InlineData("attribute 'hide'", "{attribute 'hide'}")]
    [InlineData("{attribute 'hide'}", "{attribute 'hide'}")]
    [InlineData("attribute 'displaymode' := 'hex'", "{attribute 'displaymode' := 'hex'}")]
    [InlineData("  TcInitSymbol ", "{attribute 'TcInitSymbol'}")]
    public void NormalizeAttribute_AcceptsTheUsualForms(string input, string expected)
    {
        DeclarationEntryEditor.NormalizeAttribute(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("{hide}")]
    [InlineData("{attribute 'x'")]
    [InlineData("{attribute 'x'} {attribute 'y'}")]
    public void NormalizeAttribute_RejectsTheRest(string input)
    {
        DeclarationEntryEditor.NormalizeAttribute(input).Should().BeNull();
    }

    [Fact]
    public void Tokenizer_HandlesEscapedQuotesAndBraceInsidePragmaStrings()
    {
        const string text = "VAR\n    s : STRING := 'it$'s; ok';\n    {attribute 'x' := '}'}\n    n : INT;\nEND_VAR\n";
        var (entries, error) = DeclarationEntryEditor.List(text, Var, null);
        error.Should().BeNull();
        entries!.Select(e => e.Name).Should().Equal("s", "n");
        entries[0].Initial.Should().Be("'it$'s; ok'");
        entries[1].Attributes.Should().Equal("{attribute 'x' := '}'}");
    }
}
