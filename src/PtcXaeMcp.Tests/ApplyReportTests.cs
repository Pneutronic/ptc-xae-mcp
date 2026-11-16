using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat.Schema;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Coverage for ApplyReport.Build, the single end-of-run pass/fail decision introduced in
/// Hardening 9 step 2 (replaces per-call-site decisions that let a direct EtherCAT creation
/// failure (E-4) or an unresolved link under failIfUnresolved=true (E-11) read as success).
/// </summary>
public class ApplyReportTests
{
    private static MissingEthercatNodeInfo DirectFailure(string name) => new() { Name = name, IsDirectFailure = true };
    private static MissingEthercatNodeInfo Skipped(string name) => new() { Name = name, IsDirectFailure = false };

    [Fact]
    public void Build_NoMissingNodesNoUnresolvedLinks_IsOk()
    {
        var report = ApplyReport.Build([], [], [], hadUnresolvedLinkFailure: false);

        report.Ok.Should().BeTrue();
    }

    [Fact]
    public void Build_DirectEthercatFailurePresent_IsNotOk()
    {
        // E-4: a partially built topology (e.g. missing ESI) must not read as success.
        var report = ApplyReport.Build([DirectFailure("Dev")], [], [], hadUnresolvedLinkFailure: false);

        report.Ok.Should().BeFalse();
    }

    [Fact]
    public void Build_OnlySkippedConsequenceNodes_NoDirectFailure_IsStillOk()
    {
        // A node skipped only as a consequence of something else, with no direct failure in
        // the list at all, isn't itself evidence of a broken run.
        var report = ApplyReport.Build([Skipped("Child")], [], [], hadUnresolvedLinkFailure: false);

        report.Ok.Should().BeTrue();
    }

    [Fact]
    public void Build_UnresolvedLinkFailureFlagTrue_IsNotOk()
    {
        // E-11: failIfUnresolved=true and at least one point failed to link.
        var report = ApplyReport.Build([], [], [], hadUnresolvedLinkFailure: true);

        report.Ok.Should().BeFalse();
    }

    [Fact]
    public void Build_UnresolvedLinkFailureFlagFalse_LinksStillSucceed_IsOk()
    {
        // E-11: failIfUnresolved=false means unresolved links are warnings only, never fatal.
        var report = ApplyReport.Build([], [], ["warning: could not resolve x"], hadUnresolvedLinkFailure: false);

        report.Ok.Should().BeTrue();
        report.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void Build_CarriesThroughLinksAndWarningsUnchanged()
    {
        var links = new List<LinkResult> { new() { VarName = "x" } };
        var warnings = new List<string> { "w1" };
        var missing = new List<MissingEthercatNodeInfo> { DirectFailure("A"), Skipped("B") };

        var report = ApplyReport.Build(missing, links, warnings, hadUnresolvedLinkFailure: false);

        report.Links.Should().BeSameAs(links);
        report.Warnings.Should().BeSameAs(warnings);
        report.MissingNodes.Should().BeSameAs(missing);
    }

    [Fact]
    public void Summarize_CountsDirectFailuresAndSkippedSeparately()
    {
        var missing = new List<MissingEthercatNodeInfo> { DirectFailure("A"), Skipped("B"), Skipped("C") };
        var report = ApplyReport.Build(missing, [], [], hadUnresolvedLinkFailure: false);

        string summary = report.Summarize();

        summary.Should().Contain("1 directly failed");
        summary.Should().Contain("2 skipped");
    }

    // Live 2026-09-26 (#155 d/e): the MCP tool returns only the exception message of a failed
    // run, so "[Error] Apply failed: Ok=False | ... Links: 3 ok" did not say WHICH
    // point or node failed. The failure text now carries the details.
    [Fact]
    public void Summarize_CountsUnresolvedLinks()
    {
        var report = ApplyReport.Build([], [new LinkResult { VarName = "x" }], [], hadUnresolvedLinkFailure: true,
            unresolvedLinks: ["I_Ghost (K2 Ch12 I): not found"]);

        report.Summarize().Should().Contain("Links: 1 ok, 1 unresolved");
    }

    [Fact]
    public void NotLinkedPoints_AreCountedAsInformation_NeverAFailure()
    {
        var report = ApplyReport.Build([], [new LinkResult { VarName = "x" }], [], hadUnresolvedLinkFailure: false, notLinked: 3);

        report.Ok.Should().BeTrue();
        report.NotLinked.Should().Be(3);
        report.Summarize().Should().Contain("Links: 1 ok, 0 unresolved, 3 notLinked");
    }

    [Fact]
    public void Describe_NamesDirectFailuresAndUnresolvedLinks()
    {
        var missing = new List<MissingEthercatNodeInfo>
        {
            new() { Name = "K9 (EL9999)", VInfo = "EL9999", ParentName = "K1", Reason = "no ESI", IsDirectFailure = true },
            Skipped("K10"),
        };
        var report = ApplyReport.Build(missing, [], [], hadUnresolvedLinkFailure: true,
            unresolvedLinks: ["I_Ghost (K2 Ch12 I): not found"]);

        string text = report.Describe();

        text.Should().StartWith(report.Summarize());
        text.Should().Contain("K9 (EL9999)").And.Contain("EL9999").And.Contain("no ESI");
        text.Should().Contain("I_Ghost (K2 Ch12 I): not found");
        text.Should().Contain("K10");
    }

    [Fact]
    public void Describe_CleanRun_IsJustTheSummary()
    {
        var report = ApplyReport.Build([], [], [], hadUnresolvedLinkFailure: false);

        report.Describe().Should().Be(report.Summarize());
    }

    [Fact]
    public void Describe_ListsEveryAnsweredXaeDialog()
    {
        var report = ApplyReport.Build([], [], [], hadUnresolvedLinkFailure: false,
            dialogsAnswered: ["Sync Master info: BM_CLICK 'OK' — 'TcXaeShell: ...'"]);

        report.Ok.Should().BeTrue();
        report.Describe().Should().Contain("XAE dialogs answered: Sync Master info: BM_CLICK 'OK'");
    }
}
