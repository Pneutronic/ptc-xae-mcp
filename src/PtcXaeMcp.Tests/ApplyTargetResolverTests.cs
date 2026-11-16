using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Apply;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #155 step 4, E-15: the PLC is resolved exactly; ambiguity or a
/// miss is an error naming the candidates, never a silent pick of the first match or the PLC root.
/// </summary>
public class ApplyTargetResolverTests
{
    private static FakeSysManTree OnePlc(string plc = "Plc", string projectSuffix = " Project")
    {
        var tree = new FakeSysManTree();
        tree.Add("TIPC");
        AddPlc(tree, plc, projectSuffix);
        return tree;
    }

    private static void AddPlc(FakeSysManTree tree, string plc, string projectSuffix = " Project")
    {
        string project = $"TIPC^{plc}^{plc}{projectSuffix}";
        tree.Add($"TIPC^{plc}", itemType: 56);
        tree.Add(project, itemType: PlcItemType.PlcApp);
        tree.Add($"{project}^DUTs", itemType: PlcItemType.Folder);
        tree.Add($"{project}^GVLs", itemType: PlcItemType.Folder);
        tree.Add($"{project}^POUs", itemType: PlcItemType.Folder);
        tree.Add($"TIPC^{plc}^{plc} Instance", itemType: 58);
    }

    [Theory]
    [InlineData("Plc")]
    [InlineData("PLC")]
    [InlineData("Plc Project")]
    public void ResolvePlc_ExactNameCaseInsensitive(string requested)
    {
        var tree = OnePlc();
        AddPlc(tree, "Other");

        var plc = new ApplyTargetResolver(tree).ResolvePlc(requested, strict: true);

        plc.PlcName.Should().Be("Plc");
        plc.ProjectNodePath.Should().Be("TIPC^Plc^Plc Project");
    }

    [Fact]
    public void ResolvePlc_Misspelled_Strict_ThrowsWithCandidates()
    {
        var tree = OnePlc();
        var resolver = new ApplyTargetResolver(tree);

        resolver.Invoking(r => r.ResolvePlc("Plcc", strict: true))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*'Plcc' not found*")
            .WithMessage("*'Plc'*");
        tree.Mutations.Should().BeEmpty();
    }

    [Fact]
    public void ResolvePlc_Misspelled_Lenient_SinglePlc_WarnsAndUsesIt()
    {
        var warnings = new List<string>();
        var plc = new ApplyTargetResolver(OnePlc(), warnings.Add).ResolvePlc("Plcc", strict: false);

        plc.PlcName.Should().Be("Plc");
        warnings.Should().ContainSingle().Which.Should().Contain("'Plcc' not found");
    }

    [Fact]
    public void ResolvePlc_Misspelled_Lenient_SeveralPlcs_NeverGuesses()
    {
        var tree = OnePlc();
        AddPlc(tree, "Other");

        new ApplyTargetResolver(tree).Invoking(r => r.ResolvePlc("Plcc", strict: false))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*'Plc', 'Other'*");
    }

    [Fact]
    public void ResolvePlc_NoPlcAtAll_Throws()
    {
        var tree = new FakeSysManTree();
        tree.Add("TIPC");

        new ApplyTargetResolver(tree).Invoking(r => r.ResolvePlc("Plc", strict: false))
            .Should().Throw<InvalidOperationException>().WithMessage("*No PLC project*");
    }

    [Fact]
    public void ResolvePlc_ProjectNodeOnlyReachableByPath_IsFound()
    {
        // On the live XAE the source-project node hangs off NestedProject and may not be listed
        // by Child(i); its conventional path ("<plc> Projekt" on a German XAE) still resolves.
        var tree = new FakeSysManTree();
        tree.Add("TIPC");
        tree.Add("TIPC^PLC", itemType: 56);
        tree.Add("TIPC^PLC^PLC Projekt", itemType: PlcItemType.PlcApp, enumerable: false);

        var resolver = new ApplyTargetResolver(tree);
        var plc = resolver.ResolvePlc("PLC", strict: true);

        plc.ProjectNodePath.Should().Be("TIPC^PLC^PLC Projekt");
    }

    [Fact]
    public void MissingTipc_Throws()
    {
        new ApplyTargetResolver(new FakeSysManTree()).Invoking(r => r.ListPlcs())
            .Should().Throw<InvalidOperationException>().WithMessage("*'TIPC'*");
    }
    // ---- ResolveMaster (issue #155 step 7: moved from the runner's dynamic code) -----------

    private static FakeSysManTree Masters(params string[] names)
    {
        var tree = new FakeSysManTree();
        tree.Add("TIID");
        foreach (string name in names) tree.Add($"TIID^{name}", itemType: 5, subType: 111);
        tree.Add("TIID^Device 9 (EtherCAT Automation Protocol)", itemType: 5, subType: 112);
        return tree;
    }

    [Fact]
    public void ResolveMaster_ExactName_WinsInBothModes()
    {
        var tree = Masters("DevA (EtherCAT)", "DevB (EtherCAT)");
        foreach (bool strict in new[] { true, false })
            new ApplyTargetResolver(tree).ResolveMaster("devb (ethercat)", null, strict).Path.Should().Be("TIID^DevB (EtherCAT)");
    }

    [Fact]
    public void ResolveMaster_Strict_NoExactMatch_Throws()
    {
        var tree = Masters("DevA (EtherCAT)", "DevB (EtherCAT)");
        new ApplyTargetResolver(tree).Invoking(r => r.ResolveMaster("DevC (EtherCAT)", null, strict: true))
            .Should().Throw<InvalidOperationException>().WithMessage("*DevC (EtherCAT)*");
    }

    [Fact]
    public void ResolveMaster_Lenient_FallsBackToTheFirstMaster()
    {
        var tree = Masters("DevA (EtherCAT)", "DevB (EtherCAT)");
        new ApplyTargetResolver(tree).ResolveMaster("", null, strict: false).Name.Should().Be("DevA (EtherCAT)");
    }

    [Fact]
    public void ResolveMaster_PinnedPath_IsAuthoritative()
    {
        var tree = Masters("DevA (EtherCAT)", "DevB (EtherCAT)");
        var resolver = new ApplyTargetResolver(tree);

        resolver.ResolveMaster("DevA (EtherCAT)", "TIID^DevB (EtherCAT)", strict: true).Name.Should().Be("DevB (EtherCAT)");
        resolver.Invoking(r => r.ResolveMaster("", "TIID^Device 9 (EtherCAT Automation Protocol)", strict: true))
            .Should().Throw<InvalidOperationException>().WithMessage("*not an EtherCAT master*");
    }
}
