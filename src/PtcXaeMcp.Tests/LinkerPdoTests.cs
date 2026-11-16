using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat.Apply;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The linker finds an analog channel from the terminal's own PDO list (ProduceXml): an analog terminal names
/// its channel node after the PDO ('AI Standard Channel 1'), not 'Channel 1', and the channel variables are
/// not tree children. The step runs after the exact 'Channel N' candidates and before the scored scan; the
/// entry is the one whose bit width fits the type (or the one an ioLeaf name hint names), never a pick.
/// </summary>
public class LinkerPdoTests
{
    private const string Master = "TIID^Dev (EtherCAT)";
    private const string Coupler = Master + "^K1 (EK1100)";
    private const string Ai = Coupler + "^K2 (EL3174)";
    private const string Ao = Coupler + "^K3 (EL4004)";
    private const string Di = Coupler + "^K4 (EL1008)";
    private const string Instance = "TIPC^Plc^Plc Instance";
    private const string Inputs = Instance + "^PlcTask Inputs";
    private const string Outputs = Instance + "^PlcTask Outputs";
    private const string AiSymbol = "MAIN.fbPress.nRaw";
    private const string AoSymbol = "MAIN.fbValve.nSetpoint";

    private static readonly EthercatNode Schema = new() { Name = "K1 (EK1100)", VInfo = "EK1100" };

    private static FakeSysManTree Tree()
    {
        var tree = new FakeSysManTree();
        tree.Add("TIID");
        tree.Add(Master, itemType: 5, subType: 111);
        tree.Add(Coupler, itemType: 5, subType: 9099);
        tree.Add(Ai, itemType: 5, subType: 9099).Xml = TerminalPdoMapTests.Fixture("EL3174_ProduceXml.xml");
        tree.Add(Ao, itemType: 5, subType: 9099).Xml = TerminalPdoMapTests.Fixture("EL4004_ProduceXml.xml");
        // Channel variables are addressable by path but not listed as children (as in TwinCAT).
        foreach (int ch in new[] { 1, 2, 3, 4 })
        {
            tree.Add($"{Ai}^AI Standard Channel {ch}^Value", enumerable: false);
            tree.Add($"{Ai}^AI Standard Channel {ch}^Status^Error", enumerable: false);
            tree.Add($"{Ai}^AI Standard Channel {ch}^Status^Underrange", enumerable: false);
            tree.Add($"{Ao}^AO Outputs Channel {ch}^Analog output", enumerable: false);
        }
        tree.Add("TIRT");
        tree.Add("TIRT^PlcTask", itemType: 1);
        tree.Add("TIPC");
        tree.Add("TIPC^Plc", itemType: 56);
        tree.Add(Instance, itemType: 57);
        tree.Add($"{Inputs}^{AiSymbol}", itemType: 7);
        tree.Add($"{Inputs}^MAIN.fbPress.bError", itemType: 7);
        tree.Add($"{Outputs}^{AoSymbol}", itemType: 7);
        return tree;
    }

    private static IoPoint Point(string terminal, int channel, string direction, string type, string symbol, string? ioLeaf = null) => new()
    {
        TerminalName = terminal,
        Channel = channel,
        Direction = direction,
        Type = type,
        Symbol = symbol,
        IoLeaf = ioLeaf,
    };

    private static LinkOutcome Link(FakeSysManTree tree, params IoPoint[] points) =>
        new Linker(tree, new LinkTarget("Plc", Master, true), Schema).LinkAll(points, failIfUnresolved: true);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void AnIntPointOnAnAnalogInput_LinksTheValueOfItsChannel(int channel)
    {
        var tree = Tree();

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", channel, "I", "INT", AiSymbol));

        outcome.HadUnresolvedFailure.Should().BeFalse(string.Join(" | ", outcome.Unresolved));
        outcome.Links.Should().ContainSingle().Which.IoPath.Should().Be($"{Ai}^AI Standard Channel {channel}^Value");
        tree.Mutations.Should().Equal($"link {Inputs}^{AiSymbol} {Ai}^AI Standard Channel {channel}^Value");
    }

    [Fact]
    public void AnIntPointOnAnAnalogOutput_UsesTheRxPdo()
    {
        var tree = Tree();

        LinkOutcome outcome = Link(tree, Point("K3 (EL4004)", 2, "Q", "INT", AoSymbol));

        outcome.Links.Should().ContainSingle().Which.IoPath.Should().Be($"{Ao}^AO Outputs Channel 2^Analog output");
    }

    [Fact]
    public void ABoolPointOnAnAnalogInput_IsUnresolved_AndListsTheEntries()
    {
        var tree = Tree();

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", 1, "I", "BOOL", "MAIN.fbPress.bError"));

        outcome.Links.Should().BeEmpty();
        string reason = outcome.Unresolved.Should().ContainSingle().Subject;
        reason.Should().Contain("'AI Standard Channel 1'").And.Contain("entries of 1 bit")
            .And.Contain("'AI Standard Channel 1^Status^Error' (BIT, 1 bit)")
            .And.Contain("'AI Standard Channel 1^Value' (INT, 16 bit)");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void AnIoLeafNameHint_PicksThatEntry()
    {
        var tree = Tree();

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", 1, "I", "BOOL", "MAIN.fbPress.bError", ioLeaf: "Error"));

        outcome.Links.Should().ContainSingle().Which.IoPath.Should().Be($"{Ai}^AI Standard Channel 1^Status^Error");
    }

    [Fact]
    public void AnIoLeafPath_StaysExact_ThePdoListIsNotConsulted()
    {
        var tree = Tree();
        tree.Get(Ai).Xml = "<broken";

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", 1, "I", "BOOL", "MAIN.fbPress.bError", ioLeaf: "AI Standard Channel 1^Status^Underrange"));

        outcome.Links.Should().ContainSingle().Which.IoPath.Should().Be($"{Ai}^AI Standard Channel 1^Status^Underrange");
    }

    [Fact]
    public void ADigitalTerminalWithChannelNodes_KeepsTheChannelPath()
    {
        var tree = Tree();
        tree.Add(Di, itemType: 5, subType: 9099).Xml = "<TreeItem><EtherCAT><Slave><ProcessData>" +
            "<TxPdo Sm=\"3\"><Index>#x1a00</Index><Name>Channel 1</Name><Entry><Index>#x6000</Index><BitLen>1</BitLen><Name>Other</Name></Entry></TxPdo>" +
            "</ProcessData></Slave></EtherCAT></TreeItem>";
        tree.Add(Di + "^Channel 1^Input", enumerable: false);

        LinkOutcome outcome = Link(tree, Point("K4 (EL1008)", 1, "I", "BOOL", "MAIN.fbPress.bError"));

        outcome.Links.Should().ContainSingle().Which.IoPath.Should().Be($"{Di}^Channel 1^Input");
    }

    [Fact]
    public void ABuiltPathThatIsNotInTheTree_IsAnError_NoScanFallback()
    {
        var tree = Tree();
        tree.Get(Ai).Xml = tree.Get(Ai).Xml.Replace("AI Standard Channel 4", "AI Renamed Channel 4", StringComparison.Ordinal);

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", 4, "I", "INT", AiSymbol));

        outcome.Unresolved.Should().ContainSingle().Which.Should()
            .Contain("'AI Renamed Channel 4'").And.Contain($"'{Ai}^AI Renamed Channel 4^Value' is not in the tree");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void AChannelTheTerminalDoesNotHave_NamesItsActivePdos()
    {
        var tree = Tree();

        LinkOutcome outcome = Link(tree, Point("K2 (EL3174)", 7, "I", "INT", AiSymbol));

        outcome.Unresolved.Should().ContainSingle().Which.Should()
            .Contain("IO leaf not found").And.Contain("Active input PDOs of this terminal: 'AI Standard Channel 1'");
    }

    [Fact]
    public void TheProduceXmlIsReadOncePerTerminal()
    {
        var tree = Tree();
        var counting = new CountingTree(tree);

        new Linker(counting, new LinkTarget("Plc", Master, true), Schema).LinkAll(
            [Point("K2 (EL3174)", 1, "I", "INT", AiSymbol), Point("K2 (EL3174)", 2, "I", "INT", "MAIN.fbPress.bError", ioLeaf: null)],
            failIfUnresolved: false);

        counting.ProduceXmlCalls.Should().Be(1);
    }

    // Wraps the fake tree to count ProduceXml reads of the terminal nodes.
    private sealed class CountingTree(FakeSysManTree inner) : WinForms.TwinCat.Tree.ISysManTree
    {
        public int ProduceXmlCalls { get; private set; }

        public WinForms.TwinCat.Tree.ITreeNode? Lookup(string path)
        {
            WinForms.TwinCat.Tree.ITreeNode? node = inner.Lookup(path);
            return node is null ? null : new CountingNode(this, node);
        }

        public void Delete(WinForms.TwinCat.Tree.ITreeNode node) => inner.Delete(node);
        public void LinkVariables(string pathA, string pathB) => inner.LinkVariables(pathA, pathB);

        private sealed class CountingNode(CountingTree owner, WinForms.TwinCat.Tree.ITreeNode node) : WinForms.TwinCat.Tree.ITreeNode
        {
            public string Name => node.Name;
            public string Path => node.Path;
            public int ItemType => node.ItemType;
            public int SubType => node.SubType;
            public int ChildCount => node.ChildCount;
            public WinForms.TwinCat.Tree.ITreeNode? Child(int index) =>
                node.Child(index) is { } c ? new CountingNode(owner, c) : null;
            public WinForms.TwinCat.Tree.ITreeNode CreateChild(string name, int subType, string? before, object? vInfo) =>
                node.CreateChild(name, subType, before, vInfo);
            public string ProduceXml(bool recursive = true)
            {
                owner.ProduceXmlCalls++;
                return node.ProduceXml(recursive);
            }
            public void ConsumeXml(string xml) => node.ConsumeXml(xml);
            public void ExportChild(string childName, string filePath) => node.ExportChild(childName, filePath);
            public object Raw => node.Raw;
        }
    }
}
