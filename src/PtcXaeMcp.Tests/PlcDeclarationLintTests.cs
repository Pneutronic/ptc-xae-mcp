using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// Issue #157 item 7: PlcDeclarationLint had no unit coverage of its own (only exercised
// indirectly through PouMembersCoordinator/write_pou_body live tests).
public class PlcDeclarationLintTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CheckNoEmbeddedMemberHeader_EmptyDeclaration_ReturnsNull(string? declaration)
    {
        Assert.Null(PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration));
    }

    [Fact]
    public void CheckNoEmbeddedMemberHeader_PlainVarBlock_ReturnsNull()
    {
        const string declaration = "FUNCTION_BLOCK FB_Foo\nVAR\n    x : INT;\nEND_VAR";
        Assert.Null(PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration));
    }

    [Theory]
    [InlineData("METHOD DoThing : BOOL")]
    [InlineData("  METHOD DoThing : BOOL")] // leading whitespace
    [InlineData("method DoThing : BOOL")] // case-insensitive
    [InlineData("PROPERTY Foo : INT")]
    [InlineData("ACTION Act1")]
    public void CheckNoEmbeddedMemberHeader_EmbeddedMemberHeader_ReturnsError(string line)
    {
        string declaration = $"FUNCTION_BLOCK FB_Foo\nVAR\n    x : INT;\nEND_VAR\n{line}";
        string? result = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
        Assert.NotNull(result);
        Assert.StartsWith("[Error]", result);
        Assert.Contains("twincat_plc_create", result);
    }

    // The exact false-positive a live test hit: a variable literally named Method/
    // Property/Action must not be refused just because it starts with the keyword.
    [Theory]
    [InlineData("Method : INT;")]
    [InlineData("Property : BOOL;")]
    [InlineData("Action := TRUE;")]
    public void CheckNoEmbeddedMemberHeader_VariableNamedLikeKeyword_ReturnsNull(string line)
    {
        string declaration = $"VAR\n    {line}\nEND_VAR";
        Assert.Null(PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration));
    }

    [Fact]
    public void CheckNoEmbeddedMemberHeader_KeywordAsPartOfLongerIdentifier_ReturnsNull()
    {
        // "METHODOLOGY" must not match METHOD as a prefix without a word boundary.
        const string declaration = "VAR\n    METHODOLOGY : INT;\nEND_VAR";
        Assert.Null(PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration));
    }

    [Fact]
    public void CheckNoEmbeddedMemberHeader_ReportsTheOffendingKeywordAndLine()
    {
        const string declaration = "VAR\nEND_VAR\nMETHOD DoThing : BOOL";
        string? result = PlcDeclarationLint.CheckNoEmbeddedMemberHeader(declaration);
        Assert.Contains("METHOD", result);
        Assert.Contains("DoThing", result);
    }
}
