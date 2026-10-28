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

    // ReloadReferencedPlcTmc (#14): after the build, a referenced PLC's instance re-reads the TMC.

    [Fact]
    public void ReloadTmc_ReferencedPlc_ReloadsItsInstanceOnly()
    {
        var sysMan = new ComSysManTreeTests.StubSysMan();
        var plc = new ComSysManTreeTests.StubPlcInstance("TIPC^PLC", nestedProject: null);
        var instance = new ComSysManTreeTests.StubItem("TIPC^PLC^PLC Instance", 57);
        var other = new ComSysManTreeTests.StubItem("TIPC^PLC^Something else", 58);
        plc.Kids.Add(instance);
        plc.Kids.Add(other);
        sysMan.Items["TIPC^PLC"] = plc;

        var reloaded = TwinCatComSession.ReloadReferencedPlcTmc(sysMan, "PLC");

        Assert.Equal(["TIPC^PLC^PLC Instance"], reloaded);
        Assert.Equal(["<TreeItem><ReloadTmc>true</ReloadTmc></TreeItem>"], instance.Consumed);
        Assert.Empty(other.Consumed);
    }

    [Fact]
    public void ReloadTmc_InlinePlc_TouchesNothing()
    {
        var sysMan = new ComSysManTreeTests.StubSysMan();
        var plc = new ComSysManTreeTests.StubPlcInstance("TIPC^PLC", nestedProject: new object());
        var instance = new ComSysManTreeTests.StubItem("TIPC^PLC^PLC Instance", 57);
        plc.Kids.Add(instance);
        sysMan.Items["TIPC^PLC"] = plc;

        Assert.Empty(TwinCatComSession.ReloadReferencedPlcTmc(sysMan, "PLC"));
        Assert.Empty(instance.Consumed);
    }

    // The instance node of a referenced PLC has one child of its own, "PLC Instance".
    private static bool OwnChild(string plcInstance, string segment) => segment == plcInstance + " Instance";

    [Fact]
    public void ReferencedPlc_ProjectSubtree_IsWalked()
    {
        var asked = new List<string>();
        bool walk = TwinCatComSession.ShouldWalkPlcPath("TIPC^PLC^PLC Project^POUs^MAIN", name =>
        {
            asked.Add(name);
            return false; // instance found, no NestedProject: referenced stand-alone project
        }, OwnChild);
        Assert.True(walk);
        Assert.Equal(["PLC"], asked);
    }

    [Theory]
    [InlineData("TIPC^PLC^PLC Instance")]
    [InlineData("TIPC^PLC^PLC Instance^PlcTask Inputs")]
    [InlineData("TIPC^PLC^PLC Instance^PlcTask Outputs^IO.Q_Out_1")]
    public void ReferencedPlc_BelowItsOwnInstanceChild_KeepsTheDirectLookup(string path)
    {
        // #14, live: that child lives in the main System Manager, the lookup is safe, and the
        // process-image folders below it are reachable by path only (no Child(i)).
        Assert.False(TwinCatComSession.ShouldWalkPlcPath(path, _ => false, OwnChild));
    }

    [Fact]
    public void InlinePlc_KeepsTheDirectLookup_WithoutEnumeratingChildren()
    {
        bool enumerated = false;
        Assert.False(TwinCatComSession.ShouldWalkPlcPath("TIPC^PLC^PLC Project^POUs^MAIN", _ => true,
            (_, _) => { enumerated = true; return false; }));
        Assert.False(enumerated);
    }

    [Fact]
    public void UnknownInstance_IsNotWalked_TheNormalFallbacksDecide()
    {
        Assert.False(TwinCatComSession.ShouldWalkPlcPath("TIPC^Other^PLC Project^POUs^MAIN", _ => null, OwnChild));
    }

    [Theory]
    [InlineData("TIPC^PLC")]
    [InlineData("TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Channel 1")]
    [InlineData("TIPC^^PLC Project")]
    public void OutsideAPlcInstance_NeverAsksAndNeverWalks(string path)
    {
        bool asked = false;
        Assert.False(TwinCatComSession.ShouldWalkPlcPath(path, _ => { asked = true; return false; },
            (_, _) => { asked = true; return false; }));
        Assert.False(asked);
    }
}
