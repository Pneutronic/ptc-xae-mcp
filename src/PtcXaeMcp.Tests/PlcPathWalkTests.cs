using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A path below a PLC instance is walked child by child from TIPC and never passed to
/// LookupTreeItem: with a referenced stand-alone PLC project the direct lookup crashes the XAE.
/// </summary>
public class PlcPathWalkTests
{
    [Theory]
    [InlineData("TIPC^PLC^PLC Project", true)]
    [InlineData("TIPC^PLC^PLC Project^POUs^MAIN", true)]
    [InlineData("tipc^PLC^PLC Project^POUs^FB_Step^Execute", true)]
    [InlineData("TIPC^PLC^PLC Instance^PlcTask Inputs", true)]
    [InlineData("TIPC^PLC", false)]
    [InlineData("TIPC", false)]
    [InlineData("TIID^Device 1 (EtherCAT)^Term 1 (EK1100)", false)]
    [InlineData("", false)]
    public void Walked_OnlyBelowThePlcInstance(string path, bool walked) =>
        Assert.Equal(walked, TwinCatComSession.IsBelowPlcInstance(path));
}
