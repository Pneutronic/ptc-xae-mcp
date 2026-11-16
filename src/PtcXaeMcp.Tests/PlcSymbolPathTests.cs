using PtcXaeMcp.WinForms.TwinCat.Schema;

namespace PtcXaeMcp.Tests;

/// <summary>Grammar of <c>ioPoints[].symbol</c>: a full PLC instance path, array elements included.</summary>
public class PlcSymbolPathTests
{
    [Theory]
    [InlineData("MAIN.fbCyl.LimitSwitchWorkingPosition")]
    [InlineData("PRG_X.fbOne.bIn")]
    [InlineData("MAIN.aCyl[1].bOut")]
    [InlineData("MAIN.aCyl[0].bIn")]
    [InlineData("MAIN.aCyl[-1].bIn")]
    [InlineData("MAIN.aCyl[10].bIn")]
    [InlineData("MAIN.aGrid[2,3].bIn")]
    [InlineData("MAIN.aGrid[-1,0].bIn")]
    [InlineData("MAIN.aGrid[2][3].bIn")]
    [InlineData("MAIN.a[2].b[1].c")]
    [InlineData("MAIN.fbStation.aCyl[3].fbValve.bOut")]
    [InlineData("MAIN.aStation[2].aCyl[1].fbValve.bOut")]
    [InlineData("PRG_X.fbOne.aIn[2]")]
    [InlineData("_g.x1")]
    public void Valid(string symbol)
    {
        PlcSymbolPath.Check(symbol).Should().BeNull();
        PlcSymbolPath.IsValid(symbol).Should().BeTrue();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("MAIN.a^MAIN.b", "'^'")]
    [InlineData("MAIN.fbCyl.^bIn", "'^'")]
    [InlineData("MAIN. fbCyl.bIn", "blanks")]
    [InlineData(" MAIN.fbCyl.bIn", "blanks")]
    [InlineData("MAIN.fbCyl.bIn ", "blanks")]
    [InlineData("MAIN.aCyl[ 1 ].bIn", "[2,1] without blanks")]
    [InlineData("MAIN.aGrid[2, 1].bIn", "[2,1] without blanks")]
    [InlineData("MAIN.aCyl[01].bIn", "leading zero")]
    [InlineData("MAIN.aCyl[-01].bIn", "leading zero")]
    [InlineData("MAIN.aGrid[2,01].bIn", "leading zero")]
    [InlineData("MAIN.aCyl[-0].bIn", "integer literal")]
    [InlineData("MAIN.aCyl[].bIn", "empty index")]
    [InlineData("MAIN.aCyl[n].bIn", "integer literal")]
    [InlineData("MAIN.aCyl[cMax].bIn", "integer literal")]
    [InlineData("MAIN.aCyl[1+1].bIn", "integer literal")]
    [InlineData("MAIN.aCyl[1..2].bIn", "range")]
    [InlineData("MAIN.aCyl[1.bIn", "integer literal")]
    [InlineData("MAIN.aCyl1].bIn", "not a PLC instance path")]
    [InlineData(".MAIN.fbCyl.bIn", "start with '.'")]
    [InlineData("MAIN.fbCyl.bIn.", "end with '.'")]
    [InlineData("MAIN..bIn", "empty segment")]
    [InlineData("1MAIN.bIn", "not a PLC instance path")]
    [InlineData("MAIN.b-In", "not a PLC instance path")]
    [InlineData("MAIN.aCyl[1]bIn", "needs a '.' after an index")]
    public void Invalid_NamesWhatToWrite(string symbol, string expectedHint)
    {
        string? reason = PlcSymbolPath.Check(symbol);

        reason.Should().NotBeNull();
        reason.Should().Contain(expectedHint);
        PlcSymbolPath.IsValid(symbol).Should().BeFalse();
    }

    [Fact]
    public void Null_IsInvalid() => PlcSymbolPath.IsValid(null).Should().BeFalse();

    [Fact]
    public void StructMemberCandidates_LongestFlatPrefixFirst_IndexesKept()
    {
        PlcSymbolPath.StructMemberCandidates("MAIN.aWrap[2,1].stIn.bB").Should().Equal(
            "MAIN.aWrap[2,1].stIn^bB",
            "MAIN.aWrap[2,1]^stIn^bB",
            "MAIN^aWrap[2,1]^stIn^bB");
    }

    [Fact]
    public void StructMemberCandidates_SingleSegment_IsEmpty() =>
        PlcSymbolPath.StructMemberCandidates("bIn").Should().BeEmpty();
}
