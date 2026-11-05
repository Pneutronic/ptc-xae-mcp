using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class RenameChecksTests
{
    [Theory]
    [InlineData("newstep")]
    [InlineData("NEWSTEP")]
    public void Collision_FindsTheVariable_CaseInsensitively(string newName) =>
        Assert.StartsWith("'NewStep' is already a variable (VAR_INPUT",
            RenameChecks.Collision(newName, "METHOD M : BOOL\nVAR_INPUT\n\tNewStep : BOOL;\nEND_VAR", null));

    [Fact]
    public void Collision_FindsANameInAListDeclaration() =>
        Assert.NotNull(RenameChecks.Collision("b", null, "FUNCTION_BLOCK FB\nVAR\n\ta, b : INT;\nEND_VAR"));

    [Fact]
    public void Collision_None_IsNull() =>
        Assert.Null(RenameChecks.Collision("Start", "METHOD Run : BOOL\nVAR_INPUT\n\tx : BOOL;\nEND_VAR", "FUNCTION_BLOCK FB\nVAR\n\ty : INT;\nEND_VAR"));

    [Fact]
    public void AssignmentsTo_FindsTheOldReturnAssignment_NotAMemberOrALongerName()
    {
        string body = "Run := TRUE;\nfb.Run := FALSE;\nRunning := 1;\nIF x THEN Run:=FALSE; END_IF\n// Run is done";
        Assert.Equal([1, 4], RenameChecks.AssignmentsTo(body, "Run"));
    }

    [Fact]
    public void AssignmentsTo_EmptyBody_IsNone() => Assert.Empty(RenameChecks.AssignmentsTo("", "Run"));
}
