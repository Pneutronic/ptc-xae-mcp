using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat.Apply;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #155 step 6: the linker on the ISysManTree seam. E-3 — the PLC side of a point is the exact
/// process-image symbol its <c>symbol</c> names, never a same-named member of another instance; arrays
/// of FB instances are looked up flat and exactly; E-17 — LinkVariables is called once per point (no
/// side-effecting probe); E-16 — the master subtree is read once per run. A point without a symbol
/// builds hardware only.
/// </summary>
public class LinkerTests
{
    private const string Master = "TIID^Dev (EtherCAT)";
    private const string Coupler = Master + "^K1 (EK1100)";
    private const string Terminal = Coupler + "^K2 (EL1008)";
    private const string Leaf1 = Terminal + "^Channel 1^Input";
    private const string Leaf2 = Terminal + "^Channel 2^Input";
    private const string OutLeaf1 = Terminal + "^Channel 1^Output";
    private const string Instance = "TIPC^Plc^Plc Instance";
    private const string Inputs = Instance + "^PlcTask Inputs";
    private const string Outputs = Instance + "^PlcTask Outputs";

    private const string Cyl = "MAIN.fbCyl.LimitSwitchWorkingPosition";

    private static readonly EthercatNode Schema = new()
    {
        Name = "K1 (EK1100)",
        VInfo = "EK1100",
        Children = [new EthercatNode { Name = "K2 (EL1008)", VInfo = "EL1008" }],
    };

    private static FakeSysManTree Tree(params string[] inputSymbols)
    {
        var tree = new FakeSysManTree();
        tree.Add("TIID");
        tree.Add(Master, itemType: 5, subType: 111);
        tree.Add(Coupler, itemType: 5, subType: 9099);
        tree.Add(Terminal, itemType: 5, subType: 9099);
        // Channel variables are addressable by path but not listed as children (as in TwinCAT).
        tree.Add(Terminal + "^Channel 1", enumerable: false);
        tree.Add(Leaf1, enumerable: false);
        tree.Add(OutLeaf1, enumerable: false);
        tree.Add(Terminal + "^Channel 2", enumerable: false);
        tree.Add(Leaf2, enumerable: false);
        tree.Add("TIRT");
        tree.Add("TIRT^PlcTask", itemType: 1);
        tree.Add("TIPC");
        tree.Add("TIPC^Plc", itemType: 56);
        tree.Add(Instance, itemType: 57);
        tree.Add(Inputs, itemType: 8);
        tree.Add(Outputs, itemType: 9);
        foreach (string symbol in inputSymbols) tree.Add($"{Inputs}^{symbol}", itemType: 7);
        return tree;
    }

    private static IoPoint Point(string? symbol, int channel = 1, string direction = "I", string varName = "") => new()
    {
        TerminalName = "K2 (EL1008)",
        Channel = channel,
        Direction = direction,
        Symbol = symbol,
        VarName = varName,
    };

    private static Linker Create(FakeSysManTree tree, bool strict = true, List<string>? warnings = null) =>
        new(tree, new LinkTarget("Plc", Master, strict), Schema, warn: (warnings ?? []).Add);

    // ---- member symbols --------------------------------------------------------------------

    [Fact]
    public void LinksTheMemberSymbolExactly_Once()
    {
        var tree = Tree(Cyl);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl)], failIfUnresolved: true);

        outcome.HadUnresolvedFailure.Should().BeFalse();
        LinkResult link = outcome.Links.Should().ContainSingle().Subject;
        link.PlcPath.Should().Be($"{Inputs}^{Cyl}");
        link.Symbol.Should().Be(Cyl);
        // E-17: exactly one LinkVariables call, no probe link.
        tree.Mutations.Should().Equal($"link {Inputs}^{Cyl} {Leaf1}");
    }

    [Fact]
    public void SameSuffixMemberOfAnotherInstance_IsNeverLinked()
    {
        var tree = Tree("MAIN.fbOther.LimitSwitchWorkingPosition", "MAIN.fbCyl.LimitSwitchWorkingPosition2");

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl)], failIfUnresolved: true);

        outcome.HadUnresolvedFailure.Should().BeTrue();
        outcome.Links.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Should()
            .Contain($"'{Cyl}' not found in the input process image").And.Contain(Inputs)
            .And.Contain("twincat_build_plc");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void TheDirectionDecidesTheFolders_AQPointNeverFindsAnInputSymbol()
    {
        var tree = Tree(Cyl);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl, direction: "Q")], failIfUnresolved: true);

        outcome.Links.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Should()
            .Contain("output process image").And.Contain(Outputs).And.NotContain(Inputs);
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void TheDirectionDecidesTheFolders_AnIPointNeverFindsAnOutputSymbol()
    {
        var tree = Tree();
        tree.Add($"{Outputs}^{Cyl}", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl)], failIfUnresolved: true);

        outcome.Links.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain(Inputs).And.NotContain(Outputs);
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void AQPointLinksAnOutputSymbol()
    {
        var tree = Tree();
        tree.Add($"{Outputs}^{Cyl}", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl, direction: "Q")], failIfUnresolved: true);

        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Outputs}^{Cyl}");
        tree.Mutations.Should().Equal($"link {Outputs}^{Cyl} {OutLeaf1}");
    }

    [Fact]
    public void SymbolInTwoTasks_IsAnAmbiguityError_AndLinkVariablesIsNeverCalled()
    {
        var tree = Tree(Cyl);
        tree.Add("TIRT^FastTask", itemType: 1);
        tree.Add($"{Instance}^FastTask Inputs^{Cyl}", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl)], failIfUnresolved: false);

        outcome.HadUnresolvedFailure.Should().BeFalse();
        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("ambiguous")
            .And.Contain($"{Inputs}^{Cyl}").And.Contain($"FastTask Inputs^{Cyl}");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void PointWithoutSymbol_IsTopologyOnly_NotLinked_NotAFailure()
    {
        var tree = Tree(Cyl);
        var points = new[] { Point(null, channel: 2), Point(" ", channel: 3), Point(Cyl) };

        LinkOutcome outcome = Create(tree).LinkAll(points, failIfUnresolved: true);

        outcome.NotLinked.Should().Be(2);
        outcome.Links.Should().ContainSingle();
        outcome.Unresolved.Should().BeEmpty();
        outcome.HadUnresolvedFailure.Should().BeFalse();
        tree.Mutations.Should().Equal($"link {Inputs}^{Cyl} {Leaf1}");
    }

    [Fact]
    public void OnlyPointsWithoutSymbol_NothingIsLinkedOrLookedUp()
    {
        var tree = Tree();

        LinkOutcome outcome = Create(tree).LinkAll([Point(null), Point(null, channel: 2)], failIfUnresolved: true);

        outcome.NotLinked.Should().Be(2);
        outcome.Links.Should().BeEmpty();
        tree.Mutations.Should().BeEmpty();
        tree.ChildReads.Should().Be(0);
    }

    [Fact]
    public void TheLabelIsTheVarNameElseTheSymbolElseTheTerminalChannel()
    {
        Point(Cyl, varName: "Cyl1 limit").Label.Should().Be("Cyl1 limit");
        Point(Cyl).Label.Should().Be(Cyl);
        Point(null, channel: 7).Label.Should().Be("K2 (EL1008) Ch7");
    }

    [Fact]
    public void ALinkedPointCarriesItsLabelAndSymbol()
    {
        var tree = Tree(Cyl);

        LinkResult link = Create(tree).LinkAll([Point(Cyl, varName: "Cyl1 limit")], true).Links.Single();

        link.VarName.Should().Be("Cyl1 limit");
        link.Symbol.Should().Be(Cyl);
    }

    [Fact]
    public void ASymbolThatBreaksTheGrammar_IsUnresolved_NamingTheRule()
    {
        var tree = Tree("MAIN.aCyl[ 1 ].bIn");

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aCyl[ 1 ].bIn")], failIfUnresolved: true);

        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("without blanks");
        tree.Mutations.Should().BeEmpty();
    }

    // ---- arrays of FB instances (the process image names an element flat) -------------------

    [Theory]
    [InlineData("MAIN.aCyl[1].bIn")]
    [InlineData("MAIN.aCyl[0].bIn")]
    [InlineData("MAIN.aNeg[-1].bIn")]
    [InlineData("MAIN.aGrid[2,1].bIn")]
    [InlineData("MAIN.aGrid[2][1].bIn")]
    [InlineData("MAIN.aStation[2].aCyl[1].bIn")]
    [InlineData("MAIN.fbStation.aCyl[3].fbValve.bIn")]
    [InlineData("PRG_X.fbOne.aIn[2]")]
    public void AnArrayElement_IsLookedUpFlatAndLinkedExactly(string symbol)
    {
        var tree = Tree(symbol);

        LinkOutcome outcome = Create(tree).LinkAll([Point(symbol)], failIfUnresolved: true);

        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Inputs}^{symbol}");
        tree.Mutations.Should().Equal($"link {Inputs}^{symbol} {Leaf1}");
    }

    [Fact]
    public void AnElementNeverResolvesToANeighbour_NotAnotherIndex_NotALongerMember()
    {
        // aCyl[10].bIn and aCyl[1].bIn2 share a prefix with aCyl[1].bIn; aCyl[11] ends like it.
        var tree = Tree("MAIN.aCyl[10].bIn", "MAIN.aCyl[1].bIn2", "MAIN.aCyl[11].bIn", "MAIN.xCyl[1].bIn", "MAIN.aCyl[1].bIn.x");

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aCyl[1].bIn")], failIfUnresolved: true);

        outcome.Links.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("'MAIN.aCyl[1].bIn' not found")
            .And.Contain("flat");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void TheNeighbourIsLinkedWhenItIsTheOneNamed()
    {
        var tree = Tree("MAIN.aCyl[1].bIn", "MAIN.aCyl[10].bIn");

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aCyl[10].bIn")], failIfUnresolved: true);

        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Inputs}^MAIN.aCyl[10].bIn");
    }

    [Fact]
    public void ANestedTreeLayoutIsNotUsed_TheProcessImageListsTheElementFlat()
    {
        // TwinCAT 3.1.4026 answers ITEMNOTFOUND for <folder>^MAIN.aCyl^MAIN.aCyl[2]^MAIN.aCyl[2].bIn; a
        // tree that offers the element only that way is not linked.
        var tree = Tree();
        tree.Add($"{Inputs}^MAIN.aCyl^MAIN.aCyl[2]^MAIN.aCyl[2].bIn", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aCyl[2].bIn")], failIfUnresolved: true);

        outcome.Links.Should().BeEmpty();
        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("'MAIN.aCyl[2].bIn' not found");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void AMemberOfAStructAtVariable_IsLinkedAsAChildNodeOfTheFlatVariable()
    {
        // TwinCAT 3.1.4026: 'stIn AT %I* : ST_In' is listed flat, its member bB as a child node
        // (<folder>^MAIN.aWrap[1].stIn^bB); the mapping stores 'PlcTask Inputs^MAIN.aWrap[1].stIn^bB'.
        var tree = Tree("MAIN.aWrap[1].stIn");
        tree.Add($"{Inputs}^MAIN.aWrap[1].stIn^bB", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aWrap[1].stIn.bB")], failIfUnresolved: true);

        outcome.HadUnresolvedFailure.Should().BeFalse();
        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Inputs}^MAIN.aWrap[1].stIn^bB");
    }

    [Fact]
    public void ANestedStructMember_IsLinkedAsAGrandchild()
    {
        var tree = Tree("MAIN.fbX.stIn");
        tree.Add($"{Inputs}^MAIN.fbX.stIn^stSub", itemType: 7);
        tree.Add($"{Inputs}^MAIN.fbX.stIn^stSub^bX", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.fbX.stIn.stSub.bX")], failIfUnresolved: true);

        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Inputs}^MAIN.fbX.stIn^stSub^bX");
    }

    [Fact]
    public void TheFlatNameWins_TheStructSpellingIsNotTriedWhenTheFlatOneExists()
    {
        var tree = Tree("MAIN.fbX.stIn.bB");
        tree.Add($"{Inputs}^MAIN.fbX.stIn^bB", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.fbX.stIn.bB")], failIfUnresolved: true);

        outcome.Links.Should().ContainSingle().Which.PlcPath.Should().Be($"{Inputs}^MAIN.fbX.stIn.bB");
    }

    [Fact]
    public void TwoStructSpellingsThatBothExist_AreAnAmbiguityError()
    {
        var tree = Tree();
        tree.Add($"{Inputs}^MAIN.fbX.stIn^bB", itemType: 7);
        tree.Add($"{Inputs}^MAIN.fbX^stIn^bB", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.fbX.stIn.bB")], failIfUnresolved: true);

        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("ambiguous");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void AnElementInTwoTasks_IsAnAmbiguityError()
    {
        var tree = Tree("MAIN.aCyl[1].bIn");
        tree.Add("TIRT^FastTask", itemType: 1);
        tree.Add($"{Instance}^FastTask Inputs^MAIN.aCyl[1].bIn", itemType: 7);

        LinkOutcome outcome = Create(tree).LinkAll([Point("MAIN.aCyl[1].bIn")], failIfUnresolved: true);

        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("ambiguous");
        tree.Mutations.Should().BeEmpty();
    }

    // ---- IO side, caching, terminals --------------------------------------------------------

    [Fact]
    public void MissingLeaf_ReasonNamesTheTriedPaths_NotAChildCount()
    {
        var tree = Tree(Cyl);

        LinkOutcome outcome = Create(tree).LinkAll([Point(Cyl, channel: 7)], failIfUnresolved: true);

        string reason = outcome.Unresolved.Should().ContainSingle().Subject;
        reason.Should().Contain($"'{Terminal}^Channel 7^Input'").And.NotContain("ChildCount");
    }

    [Fact]
    public void E16_MasterSubtreeIsReadOnce_ForManyPoints()
    {
        var tree = Tree("MAIN.a.bA", "MAIN.a.bB", "MAIN.a.bC");
        // A schema whose coupler name does not match the tree forces the name index.
        var schema = new EthercatNode { Name = "K9 (EK1100)", VInfo = "EK1100", Children = [new EthercatNode { Name = "K2 (EL1008)", VInfo = "EL1008" }] };
        var linker = new Linker(tree, new LinkTarget("Plc", Master, true), schema);

        linker.LinkAll([Point("MAIN.a.bA")], failIfUnresolved: true).Links.Should().HaveCount(1);
        int readsAfterFirst = tree.ChildReads;
        linker.LinkAll([Point("MAIN.a.bB", 2), Point("MAIN.a.bC")], failIfUnresolved: true).Links.Should().HaveCount(2);

        tree.ChildReads.Should().Be(readsAfterFirst);
    }

    [Fact]
    public void Strict_AmbiguousTerminalName_IsAnError()
    {
        var tree = Tree(Cyl);
        tree.Add(Master + "^K5 (EK1100)", itemType: 5, subType: 9099);
        tree.Add(Master + "^K5 (EK1100)^K2 (EL1008)", itemType: 5, subType: 9099);
        var schema = new EthercatNode { Name = "Kx (EK1100)", VInfo = "EK1100", Children = [new EthercatNode { Name = "K2 (EL1008)", VInfo = "EL1008" }] };
        var linker = new Linker(tree, new LinkTarget("Plc", Master, true), schema);

        LinkOutcome outcome = linker.LinkAll([Point(Cyl)], failIfUnresolved: true);

        outcome.Unresolved.Should().ContainSingle().Which.Should().Contain("ambiguous");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void SchemaIndex_UsesTheResolvedMasterPath()
    {
        var index = Linker.BuildSchemaTerminalPathIndex(Schema, "TIID^Other (EtherCAT)");

        index["K2 (EL1008)"].Should().Be("TIID^Other (EtherCAT)^K1 (EK1100)^K2 (EL1008)");
        index["__KEY__K2"].Should().Be("TIID^Other (EtherCAT)^K1 (EK1100)^K2 (EL1008)");
    }
}
