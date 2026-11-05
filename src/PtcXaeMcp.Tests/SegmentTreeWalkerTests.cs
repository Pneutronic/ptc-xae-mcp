using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Issue #150 C-9: open_object walks the path segment by segment and restores expansion on a miss.</summary>
public class SegmentTreeWalkerTests
{
    private sealed class Node(string name, bool isItem = true, bool collapsed = true, params Node[] children) : IWalkNode
    {
        public string Name { get; } = name;
        public bool IsItem { get; } = isItem;
        public bool IsCollapsed { get; private set; } = collapsed && children.Length > 0;
        public int Expands { get; private set; }
        public int Collapses { get; private set; }
        public void Expand() { Expands++; IsCollapsed = false; }
        public void Collapse() { Collapses++; IsCollapsed = true; }
        // Like a virtualized UIA tree: collapsed nodes expose no children.
        public IReadOnlyList<IWalkNode> Children() => IsCollapsed ? Array.Empty<IWalkNode>() : children;
    }

    private static Node Leaf(string name) => new(name);

    // Solution Explorer shape of the fixture: Solution > Project > E/A > Geräte > Device 1 > Term 1 >
    // Term 2 / Term 3, both with a "WcState" child; plus SYSTEM > Tasks.
    private sealed class Fixture
    {
        public readonly Node WcState2 = Leaf("WcState");
        public readonly Node WcState3 = Leaf("WcState");
        public readonly Node Term2, Term3, Term1, Device, Geraete, EA, Tasks, System, Project, Solution, Tree;

        public Fixture()
        {
            Term2 = new Node("Term 2 (EL1008)", children: WcState2);
            Term3 = new Node("Term 3 (EL3174)", children: WcState3);
            Term1 = new Node("Term 1 (EK1100)", children: [Term2, Term3]);
            Device = new Node("Device 1 (EtherCAT)", children: Term1);
            Geraete = new Node("Geräte", children: Device);
            EA = new Node("E/A", children: Geraete);
            Tasks = new Node("Tasks", children: Leaf("PlcTask"));
            System = new Node("SYSTEM", children: Tasks);
            Project = new Node("PtcXaeMcpFixture", collapsed: false, children: [System, EA]);
            Solution = new Node("Projektmappe 'PtcXaeMcpFixture'", collapsed: false, children: Project);
            Tree = new Node("SolutionExplorer", isItem: false, collapsed: false, children: Solution);
        }
    }

    [Fact]
    public void DuplicateLeafName_SelectsTheOneUnderTheGivenParents()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree,
            ["Geräte", "Device 1 (EtherCAT)", "Term 1 (EK1100)", "Term 3 (EL3174)", "WcState"], mayExpand: true);
        Assert.Same(f.WcState3, r.Match);
    }

    [Fact]
    public void DuplicateLeafName_OtherParent_SelectsTheOtherOne()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree,
            ["Geräte", "Device 1 (EtherCAT)", "Term 1 (EK1100)", "Term 2 (EL1008)", "WcState"], mayExpand: true);
        Assert.Same(f.WcState2, r.Match);
    }

    [Fact]
    public void OnlyThePathIsLeftExpanded_SideBranchesAreCollapsedAgain()
    {
        var f = new Fixture();
        SegmentTreeWalker.Find(f.Tree, ["Geräte", "Device 1 (EtherCAT)", "Term 1 (EK1100)", "Term 3 (EL3174)", "WcState"], mayExpand: true);
        Assert.False(f.EA.IsCollapsed);
        Assert.False(f.Term1.IsCollapsed);
        Assert.False(f.Term3.IsCollapsed);
        Assert.True(f.System.IsCollapsed);   // expanded while anchoring, not on the path
        Assert.True(f.Term2.IsCollapsed);    // never expanded
        Assert.Equal(0, f.Term2.Expands);
    }

    [Fact]
    public void Miss_RestoresEveryExpansionAndNamesTheMissingSegment()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree, ["Geräte", "Device 1 (EtherCAT)", "Term 9 (EL9999)"], mayExpand: true);
        Assert.Null(r.Match);
        Assert.Contains("'Term 9 (EL9999)' not found under 'Device 1 (EtherCAT)'", r.Note);
        foreach (var n in new[] { f.EA, f.Geraete, f.Device, f.System })
            Assert.True(n.IsCollapsed, $"{n.Name} left expanded");
        Assert.False(f.Project.IsCollapsed); // was expanded before the walk — untouched
        Assert.Equal(0, f.Project.Collapses);
    }

    [Fact]
    public void UnknownAnchor_IsAMiss_WithEverythingRestored()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree, ["MOTION", "NC-Task 1 SAF"], mayExpand: true);
        Assert.Null(r.Match);
        Assert.True(f.EA.IsCollapsed);
        Assert.True(f.System.IsCollapsed);
    }

    [Fact]
    public void SingleSegment_FindsTheShallowestNode()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree, ["Tasks"], mayExpand: true);
        Assert.Same(f.Tasks, r.Match);
    }

    [Fact]
    public void NamesAreCaseInsensitive()
    {
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree, ["geräte", "device 1 (ethercat)"], mayExpand: true);
        Assert.Same(f.Device, r.Match);
    }

    [Fact]
    public void WithoutMayExpand_NothingIsExpanded_AndTheCollapsedNodeIsNamed()
    {
        // Expanding a Solution-Explorer node pulls the XAE to the foreground (live 2026-11-05).
        var f = new Fixture();
        var r = SegmentTreeWalker.Find(f.Tree, ["Geräte", "Device 1 (EtherCAT)"]);
        Assert.Null(r.Match);
        Assert.Contains("is collapsed in the Solution Explorer", r.Note);
        Assert.Contains("bringToFront=true", r.Note);
        foreach (var n in new[] { f.EA, f.System, f.Geraete })
            Assert.Equal(0, n.Expands);
    }

    [Fact]
    public void WithoutMayExpand_AVisibleNode_IsFound()
    {
        var f = new Fixture();
        f.EA.Expand();
        f.Geraete.Expand();
        var r = SegmentTreeWalker.Find(f.Tree, ["Geräte", "Device 1 (EtherCAT)"]);
        Assert.Same(f.Device, r.Match);
    }
}
