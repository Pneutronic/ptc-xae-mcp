using System.Runtime.InteropServices;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #155 step 3: the real <see cref="ComSysManTree"/> driven through late binding against
/// plain .NET stand-ins for ITcSysManager / ITcSmTreeItem (public, so the runtime binder can
/// reach them) — exact lookup, exact delete, busy propagation.
/// </summary>
public class ComSysManTreeTests
{
    public sealed class StubItem
    {
        public StubItem(string path, int itemType = -1) { PathName = path; ItemType = itemType; }
        public string PathName { get; }
        public string Name => PathName.Split('^')[^1];
        public int ItemType { get; }
        public List<StubItem> Kids { get; } = [];
        public List<string> DeletedChildren { get; } = [];
        public bool DeleteChildFails { get; set; }
        public bool SelfDeleted { get; private set; }
        public List<string> Consumed { get; } = [];
        public void ConsumeXml(string xml) => Consumed.Add(xml);
        public int ChildCount => Kids.Count;
        public StubItem Child(int index) => Kids[index - 1];
        public void DeleteChild(string name)
        {
            if (DeleteChildFails) throw new COMException("E_FAIL", unchecked((int)0x80004005));
            DeletedChildren.Add(name);
            Kids.RemoveAll(k => k.Name == name);
        }
        public void Delete() => SelfDeleted = true;
        public object CreateChild(string name, int subType, string? before, object? vInfo)
        {
            var child = new StubItem($"{PathName}^{name}", subType);
            Kids.Add(child);
            return new object[] { child }; // one-element SAFEARRAY, as TwinCAT returns it
        }
    }

    /// <summary>A PLC instance under TIPC; NestedProject is null for a referenced stand-alone project.</summary>
    public sealed class StubPlcInstance
    {
        public StubPlcInstance(string path, object? nestedProject) { PathName = path; NestedProject = nestedProject; }
        public string PathName { get; }
        public string Name => PathName.Split('^')[^1];
        public object? NestedProject { get; }
        public List<StubItem> Kids { get; } = [];
        public int ChildCount => Kids.Count;
        public StubItem Child(int index) => Kids[index - 1];
    }

    public sealed class StubSysMan
    {
        public Dictionary<string, object?> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Throws { get; set; }
        public List<string> Links { get; } = [];
        public List<string> Lookups { get; } = [];
        public object? LookupTreeItem(string path)
        {
            Lookups.Add(path);
            if (Throws is not null) throw Throws;
            return Items.TryGetValue(path, out var item) ? item : throw new COMException("not found", unchecked((int)0x80004005));
        }
        public void LinkVariables(string a, string b, int o1, int o2, int size) => Links.Add($"{a}|{b}");
    }

    /// <summary>A tree item whose PathName read is rejected as busy a given number of times.</summary>
    public sealed class BusyPathItem
    {
        private readonly string _path;
        private int _busyReads;
        public BusyPathItem(string path, int busyReads) { _path = path; _busyReads = busyReads; }
        public int PathReads { get; private set; }
        public string PathName
        {
            get
            {
                PathReads++;
                if (_busyReads-- > 0) throw new COMException("call rejected", unchecked((int)0x80010001));
                return _path;
            }
        }
    }

    private static ComSysManTree Tree(StubSysMan sysMan) => new(sysMan, CancellationToken.None);

    [Fact]
    public void Lookup_ReturnsTheNodeAtTheExactPath()
    {
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC^Plc"] = new StubItem("TIPC^Plc", 56);

        ITreeNode? node = Tree(sysMan).Lookup("tipc^plc");

        node.Should().NotBeNull();
        node!.Path.Should().Be("TIPC^Plc");
        node.Name.Should().Be("Plc");
        node.ItemType.Should().Be(56);
        node.SubType.Should().Be(-1, "the stub has no ItemSubType/SubType");
    }

    [Fact]
    public void NodeProperties_Busy_IsRetriedOnTheSeamInsteadOfEscaping()
    {
        // Issue #190: a busy Path/Name read right after a CreateChild used to escape raw to the
        // caller's outer busy retry, which replayed the create. It settles on the seam now.
        var item = new BusyPathItem("TIID^Box 1", busyReads: 1);
        ITreeNode node = new ComTreeNode(Tree(new StubSysMan()), item);

        node.Path.Should().Be("TIID^Box 1");
        item.PathReads.Should().Be(2);
    }

    [Fact]
    public void Lookup_MissingPath_IsNull()
        => Tree(new StubSysMan()).Lookup("TIPC^Nope").Should().BeNull();

    [Fact]
    public void Lookup_ANodeWithADifferentPath_IsNotAMatch()
    {
        // LookupTreeItem answering with some other item (e.g. a parent) must not count as found.
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC^Plc^Plc Project^GVLs^GVL_IO"] = new StubItem("TIPC^Plc^Plc Project^GVLs");

        Tree(sysMan).Lookup("TIPC^Plc^Plc Project^GVLs^GVL_IO").Should().BeNull();
    }

    [Fact]
    public void Lookup_BelowAReferencedPlc_NeverPassesThePathToLookupTreeItem()
    {
        // Issue #14: LookupTreeItem on a path below a referenced stand-alone PLC project crashes
        // the XAE. The instance has no NestedProject, so the path is walked from TIPC instead (the
        // stub has no TIPC node, so the walk ends there: not found).
        const string path = "TIPC^Plc^Plc Project^GVLs^GVL_IO";
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC^Plc"] = new StubPlcInstance("TIPC^Plc", nestedProject: null);
        sysMan.Items[path] = new StubItem(path, 615);

        Tree(sysMan).Lookup(path).Should().BeNull();

        sysMan.Lookups.Should().NotContain(path).And.Contain("TIPC^Plc").And.Contain("TIPC");
    }

    [Fact]
    public void Lookup_ProcessImageOfAReferencedPlc_KeepsTheDirectLookup()
    {
        // Issue #14, live: below the referenced PLC's own "<plc> Instance" child the lookup is safe,
        // and the image folders there are reachable by path only — the linker needs this lookup.
        const string path = "TIPC^Plc^Plc Instance^PlcTask Inputs^IO.I_In_1";
        var sysMan = new StubSysMan();
        var instance = new StubPlcInstance("TIPC^Plc", nestedProject: null);
        instance.Kids.Add(new StubItem("TIPC^Plc^Plc Instance", 57));
        sysMan.Items["TIPC^Plc"] = instance;
        sysMan.Items[path] = new StubItem(path);

        Tree(sysMan).Lookup(path)!.Path.Should().Be(path);

        sysMan.Lookups.Should().Equal("TIPC^Plc", "TIPC^Plc", path);
    }

    [Fact]
    public void Lookup_BelowAnInlinePlc_KeepsTheDirectLookup()
    {
        const string path = "TIPC^Plc^Plc Project^GVLs^GVL_IO";
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC^Plc"] = new StubPlcInstance("TIPC^Plc", nestedProject: new object());
        sysMan.Items[path] = new StubItem(path, 615);

        Tree(sysMan).Lookup(path)!.Path.Should().Be(path);

        sysMan.Lookups.Should().Equal("TIPC^Plc", path);
    }

    [Fact]
    public void Lookup_Cancelled_PropagatesInsteadOfNotFound()
    {
        // E-12: cancellation is never turned into "not found".
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC"] = new StubItem("TIPC");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        new ComSysManTree(sysMan, cts.Token).Invoking(t => t.Lookup("TIPC"))
            .Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Children_AndCreateChild_UnwrapTheSafeArray()
    {
        var sysMan = new StubSysMan();
        var folder = new StubItem("TIPC^Plc^Plc Project^GVLs", 601);
        sysMan.Items[folder.PathName] = folder;
        var tree = Tree(sysMan);

        ITreeNode node = tree.Lookup(folder.PathName)!;
        ITreeNode created = node.CreateChild("GVL_IO", 615, null, "VAR_GLOBAL END_VAR");

        created.Path.Should().Be("TIPC^Plc^Plc Project^GVLs^GVL_IO");
        created.ItemType.Should().Be(615);
        node.ChildCount.Should().Be(1);
        node.Children().Select(c => c.Name).Should().Equal("GVL_IO");
    }

    [Fact]
    public void Delete_UsesDeleteChildOnTheExactParent()
    {
        var sysMan = new StubSysMan();
        var folder = new StubItem("TIPC^Plc^Plc Project^GVLs", 601);
        var gvl = new StubItem("TIPC^Plc^Plc Project^GVLs^GVL_IO", 615);
        folder.Kids.Add(gvl);
        sysMan.Items[folder.PathName] = folder;
        sysMan.Items[gvl.PathName] = gvl;
        var tree = Tree(sysMan);

        tree.Delete(tree.Lookup(gvl.PathName)!);

        folder.DeletedChildren.Should().Equal("GVL_IO");
        gvl.SelfDeleted.Should().BeFalse();
    }

    [Fact]
    public void Delete_FallsBackToTheNodesOwnDelete()
    {
        var sysMan = new StubSysMan();
        // Not under TIPC: "TIPC^GVLs" would read as a PLC instance without a NestedProject (#14).
        var folder = new StubItem("TIID^GVLs", 601) { DeleteChildFails = true };
        var gvl = new StubItem("TIID^GVLs^GVL_IO", 615);
        sysMan.Items[folder.PathName] = folder;
        sysMan.Items[gvl.PathName] = gvl;
        var tree = Tree(sysMan);

        tree.Delete(tree.Lookup(gvl.PathName)!);

        gvl.SelfDeleted.Should().BeTrue();
    }

    [Fact]
    public void Delete_RefusesATopLevelNode()
    {
        var sysMan = new StubSysMan();
        sysMan.Items["TIPC"] = new StubItem("TIPC");
        var tree = Tree(sysMan);

        tree.Invoking(t => t.Delete(t.Lookup("TIPC")!))
            .Should().Throw<InvalidOperationException>().WithMessage("*top-level*");
    }

    [Fact]
    public void LinkVariables_PassesBothPaths()
    {
        var sysMan = new StubSysMan();
        Tree(sysMan).LinkVariables("TIPC^a", "TIID^b");
        sysMan.Links.Should().Equal("TIPC^a|TIID^b");
    }

    [Fact]
    public void UnwrapSingle_OnlyUnwrapsOneElementArrays()
    {
        ComSysManTree.UnwrapSingle(new object[] { "x" }).Should().Be("x");
        ComSysManTree.UnwrapSingle(new object[] { "x", "y" }).Should().BeNull();
        ComSysManTree.UnwrapSingle("x").Should().Be("x");
    }

    [Fact]
    public void ReadName_FallsBackToTheLastPathSegment()
    {
        ComSysManTree.ReadName(new StubItem("TIID^Device 1 (EtherCAT)")).Should().Be("Device 1 (EtherCAT)");
        ComSysManTree.ReadName(new object()).Should().Be("<unknown>");
        ComSysManTree.ReadPath(new object()).Should().BeEmpty();
        ComSysManTree.ReadItemType(new object()).Should().Be(-1);
    }
}
