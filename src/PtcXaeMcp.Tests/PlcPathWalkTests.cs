using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A path below a PLC instance is walked child by child from TIPC, instead of being passed to
/// LookupTreeItem, only when that PLC is a referenced stand-alone project (no NestedProject on
/// the instance node): there the direct lookup crashes the XAE (PR #9). An inline PLC keeps the
/// single direct lookup.
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
    public void BelowPlcInstance_OnlyWithThreeOrMoreSegmentsUnderTipc(string path, bool below) =>
        Assert.Equal(below, TwinCatComSession.IsBelowPlcInstance(path));

    [Fact]
    public void ReferencedPlc_IsWalked()
    {
        var asked = new List<string>();
        bool walk = TwinCatComSession.ShouldWalkPlcPath("TIPC^PLC^PLC Project^POUs^MAIN", name =>
        {
            asked.Add(name);
            return false; // instance found, no NestedProject: referenced stand-alone project
        });
        Assert.True(walk);
        Assert.Equal(["PLC"], asked);
    }

    [Fact]
    public void InlinePlc_KeepsTheDirectLookup()
    {
        Assert.False(TwinCatComSession.ShouldWalkPlcPath("TIPC^PLC^PLC Project^POUs^MAIN", _ => true));
    }

    [Fact]
    public void UnknownInstance_IsNotWalked_TheNormalFallbacksDecide()
    {
        Assert.False(TwinCatComSession.ShouldWalkPlcPath("TIPC^Other^PLC Project^POUs^MAIN", _ => null));
    }

    [Theory]
    [InlineData("TIPC^PLC")]
    [InlineData("TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Channel 1")]
    [InlineData("TIPC^^PLC Project")]
    public void OutsideAPlcInstance_NeverAsksAndNeverWalks(string path)
    {
        bool asked = false;
        Assert.False(TwinCatComSession.ShouldWalkPlcPath(path, _ => { asked = true; return false; }));
        Assert.False(asked);
    }
}
