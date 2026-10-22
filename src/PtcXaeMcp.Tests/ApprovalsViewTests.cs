using System.Windows.Forms;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.Views;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #272: the Approvals view and its confirm popup. Safety-relevant parts are checked
/// without a message loop: the popup never confirms by default, the GUI holds exactly one
/// ConfirmProposal call (behind the popup's OK), the manual step buttons are gone, and the
/// badge follows pending approvals.
/// </summary>
public sealed class ApprovalsViewTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private static IbnProposal Proposal(string? chain = "chainA", string? actuator = "Cyl1") =>
        new("ab12cd34", "next_step", chain, actuator, IbnProposalState.Pending, T0, T0.AddSeconds(60), null);

    // ── confirm popup ───────────────────────────────────────────────────────────

    [Fact]
    public void ConfirmDialog_IsTopMost_AndNeverConfirmsByDefault()
    {
        using var dialog = new IbnConfirmDialog(Proposal(), () => true);

        dialog.TopMost.Should().BeTrue();
        dialog.AcceptButton.Should().BeNull("Enter must never confirm a motion");
        var cancel = dialog.CancelButton.Should().BeOfType<Button>().Subject;
        cancel.DialogResult.Should().Be(DialogResult.Cancel);
        dialog.ActiveControl.Should().BeSameAs(cancel, "focus starts on Cancel");

        var execute = dialog.Controls.Find("_btnIbnConfirmExecute", searchAllChildren: true).Should().ContainSingle().Subject;
        ((Button)execute).DialogResult.Should().Be(DialogResult.OK);
    }

    [Fact]
    public void ConfirmText_NamesTheStep_AndWarnsThatTheMachineMayMove()
    {
        string details = IbnConfirmText.Details(Proposal());

        details.Should().Contain("next_step").And.Contain("chainA").And.Contain("Cyl1").And.Contain("ab12cd34");
        IbnConfirmText.Details(Proposal(chain: null, actuator: null)).Should().Contain("Chain     : -");
        IbnConfirmText.Effect.Should().Contain("MOVE").And.Contain("writes nothing");
        IbnConfirmText.Countdown(TimeSpan.FromSeconds(41.2)).Should().Contain("42 s");
    }

    // ── source invariants ───────────────────────────────────────────────────────

    [Fact]
    public void Gui_CallsConfirmProposal_OnlyOnce_BehindThePopupsOk()
    {
        // The persistent-snapshot restore has its own confirm path (ApprovalsView.Restore.cs), pinned below.
        var hits = GuiSources()
            .Where(f => !f.EndsWith("ApprovalsView.Restore.cs", StringComparison.Ordinal))
            .SelectMany(f => Occurrences(File.ReadAllText(f), "ConfirmProposal(").Select(i => (File: f, Index: i)))
            .ToList();

        hits.Should().ContainSingle();
        hits[0].File.Should().EndWith(Path.Combine("Views", "ApprovalsView.cs"));
        string text = File.ReadAllText(hits[0].File);
        string before = text[..hits[0].Index];
        int okBranch = before.LastIndexOf("if (answer == DialogResult.OK)", StringComparison.Ordinal);
        okBranch.Should().BeGreaterThan(before.LastIndexOf("dialog.ShowDialog(", StringComparison.Ordinal),
            "the confirm must follow the popup's OK, after ShowDialog returned");
    }

    [Fact]
    public void Gui_CallsRestoreConfirmProposal_OnlyOnce_BehindTheRestorePopupsOk()
    {
        var hits = GuiSources()
            .SelectMany(f => Occurrences(File.ReadAllText(f), "SnapshotRestore.ConfirmProposal(").Select(i => (File: f, Index: i)))
            .ToList();

        hits.Should().ContainSingle();
        hits[0].File.Should().EndWith(Path.Combine("Views", "ApprovalsView.Restore.cs"));
        string before = File.ReadAllText(hits[0].File)[..hits[0].Index];
        before.LastIndexOf("if (answer == DialogResult.OK)", StringComparison.Ordinal)
            .Should().BeGreaterThan(before.LastIndexOf("dialog.ShowDialog(", StringComparison.Ordinal),
                "the restore confirm must follow the popup's OK, after ShowDialog returned");
    }

    [Fact]
    public void Gui_HasNoManualProposeOrSnapshotControls()
    {
        foreach (var file in GuiSources())
        {
            string text = File.ReadAllText(file);
            text.Should().NotContain(".Propose(", $"{Path.GetFileName(file)}: the manual chain/step buttons were removed (#272)");
            text.Should().NotContain("_btnIbnStartChain").And.NotContain("_btnIbnNextStep").And.NotContain("_btnIbnHold")
                .And.NotContain("_btnIbnSnapshot");
        }
    }

    // ── the view itself ─────────────────────────────────────────────────────────

    [Fact]
    public void View_ShowsAbortFirst_AndHasNoManualStepControls()
    {
        using var host = new ServerHost(new FakeTwinCatSession(), enableTimers: false);
        using var view = new ApprovalsView();
        view.Attach(host, new GuiComDispatchRunner(host.Dispatcher), owner: null!, post: a => a());

        var abort = (Button)view.Controls.Find("_btnIbnAbort", true).Single();
        abort.Tag.Should().Be("danger");
        abort.Enabled.Should().BeTrue("stopping is never gated");
        var root = (TableLayoutPanel)view.Controls.Find("_pnlApprovals", true).Single();
        root.GetRow(abort.Parent!).Should().Be(0, "ABORT sits in the top row, above everything that can grow");

        foreach (var removed in new[] { "_btnIbnStartChain", "_btnIbnNextStep", "_btnIbnHold", "_btnIbnSnapshot", "_gridIbn", "_txtIbnWatch" })
            view.Controls.Find(removed, true).Should().BeEmpty(removed + " was removed with #272");
        view.Controls.Find("_btnIbnArm", true).Should().ContainSingle();
        view.Controls.Find("_gridHmiApprovals", true).Should().ContainSingle();
    }

    [Fact]
    public void View_ListsHmiApprovals_AndRaisesTheBadgeWhilePending()
    {
        using var host = new ServerHost(new FakeTwinCatSession(), enableTimers: false);
        using var view = new ApprovalsView();
        var seen = new List<ApprovalsAttention>();
        view.Attach(host, new GuiComDispatchRunner(host.Dispatcher), owner: null!, post: a => a());
        view.AttentionChanged += seen.Add;
        var request = new HmiWriteRequest(HmiWriteKind.WriteSymbol, "Hmi", @"C:\Projects\Hmi", "ADS.PLC1.MAIN.b", "ADS", "true", null, true);

        long id = host.HmiApprovals.Begin(request);
        view.Attention.PendingHmi.Should().Be(1);
        view.Attention.TabCaption.Should().Be("Approvals (1)");

        host.HmiApprovals.Complete(id, request, HmiConfirmOutcome.Rejected);
        view.Attention.Should().Be(ApprovalsAttention.None);
        seen.Should().HaveCount(2);

        var grid = (DataGridView)view.Controls.Find("_gridHmiApprovals", true).Single();
        grid.Rows.Count.Should().Be(1);
        grid.Rows[0].Cells["Outcome"].Value.Should().Be("Rejected");
        grid.Rows[0].Cells["Item"].Value.Should().Be("ADS.PLC1.MAIN.b");
    }

    [Fact]
    public void Attention_TextsForTheBadge()
    {
        ApprovalsAttention.None.NeedsAttention.Should().BeFalse();
        ApprovalsAttention.None.TabCaption.Should().Be("Approvals");
        ApprovalsAttention.None.ChipText.Should().BeEmpty();

        var pending = new ApprovalsAttention(1, 1, Armed: true, RemainingSeconds: 42);
        pending.TabCaption.Should().Be("Approvals (2)");
        pending.ChipText.Should().Be("2 approvals pending · IBN armed 42 s");

        var armedOnly = new ApprovalsAttention(0, 0, Armed: true, RemainingSeconds: 7);
        armedOnly.NeedsAttention.Should().BeTrue();
        armedOnly.TabCaption.Should().Be("Approvals");
        armedOnly.ChipText.Should().Be("IBN armed — 7 s");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
            yield return i;
    }

    private static IEnumerable<string> GuiSources()
    {
        string root = FindWinFormsSourceDirectory();
        return Directory.EnumerateFiles(root, "Form1*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Views"), "*.cs"));
    }

    private static string FindWinFormsSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "PtcXaeMcp.WinForms");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "Form1.cs")))
                return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate src/PtcXaeMcp.WinForms from " + AppContext.BaseDirectory);
    }
}
