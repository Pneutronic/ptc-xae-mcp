using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.Views;

namespace PtcXaeMcp.Tests;

// persistent-snapshot: the MCP surface (flags, defaults, the "nobody but the host confirms" invariant)
// and the popup text.
public sealed class PersistentSnapshotSurfaceTests
{
    private static readonly string[] SnapshotTools =
    [
        "twincat_list_ads_symbols", "twincat_persistent_snapshot_save", "twincat_persistent_snapshot_compare",
        "twincat_persistent_snapshot_restore", "twincat_persistent_snapshot_restore_status",
    ];

    private static Dictionary<string, MethodInfo> Tools() =>
        typeof(TwinCatMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr?.Name is not null)
            .ToDictionary(x => x.Attr!.Name!, x => x.Method, StringComparer.Ordinal);

    [Fact]
    public void All_five_tools_are_registered_with_the_right_flags()
    {
        var tools = Tools();
        tools.Keys.Should().Contain(SnapshotTools);

        foreach (string name in SnapshotTools.Where(n => n != "twincat_persistent_snapshot_restore"))
            tools[name].GetCustomAttribute<McpServerToolAttribute>()!.ReadOnly.Should().BeTrue(name);

        var restore = tools["twincat_persistent_snapshot_restore"].GetCustomAttribute<McpServerToolAttribute>()!;
        restore.ReadOnly.Should().BeFalse();
        restore.Destructive.Should().BeTrue();
    }

    [Fact]
    public void Restore_defaults_to_dry_run()
    {
        var p = Tools()["twincat_persistent_snapshot_restore"].GetParameters().Single(x => x.Name == "dryRun");
        p.HasDefaultValue.Should().BeTrue();
        p.DefaultValue.Should().Be(true);
    }

    [Fact]
    public void No_mcp_tool_can_confirm_or_execute_a_restore()
    {
        Tools().Keys
            .Where(n => n.Contains("snapshot", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("restore", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("apply", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("approve", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("a restore is executed only after a human clicks the host popup");
    }

    [Fact]
    public void The_test_confirm_hook_lives_only_on_the_diagnostics_surface_marked_as_test_support()
    {
        const string name = "twincat_persistent_snapshot_test_confirm";
        Tools().Keys.Should().NotContain(name);

        var diagnostics = typeof(TwinCatMcpToolsDiagnostics)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == name);
        string description = diagnostics.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
        description.Should().StartWith("TEST SUPPORT ONLY").And.Contain("never registered for released clients");
    }

    [Fact]
    public void The_confirm_path_is_not_reachable_from_any_tool_signature()
    {
        // ConfirmProposal / RejectProposal exist on the coordinator for the host only; no tool takes the coordinator.
        var tools = Tools().Where(t => SnapshotTools.Contains(t.Key)).Select(t => t.Value);
        tools.SelectMany(m => m.GetParameters()).Select(p => p.ParameterType)
            .Should().NotContain(typeof(SnapshotRestoreCoordinator));
    }

    // ---- popup text ----------------------------------------------------------------------

    private static RestoreProposal Proposal(string plcState, int writes, int skips = 0)
    {
        var w = Enumerable.Range(0, writes).Select(i => new RestoreWrite($"G.n{i}", $"G.n{i}", "INT",
            new SnapshotLeaf("INT", JsonDocument.Parse("1").RootElement.Clone()),
            new SnapshotLeaf("INT", JsonDocument.Parse("2").RootElement.Clone()))).ToList();
        var s = Enumerable.Range(0, skips).Select(i => new RestoreSkip($"G.s{i}", "read-only")).ToList();
        var now = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        return new RestoreProposal("abc12345", "5.1.2.3.1.1", 851, @"C:\t\snap.json", "2026-10-22T09:00:00Z", plcState,
            new RestorePlan(w, s, 4, "hash"), SnapshotRestoreState.Pending, now, now.AddMinutes(5), null, null);
    }

    [Fact]
    public void Popup_shows_target_state_snapshot_and_the_first_differences()
    {
        string text = RestoreConfirmText.Details(Proposal("Run", 20, 2));

        text.Should().Contain("5.1.2.3.1.1:851").And.Contain(@"C:\t\snap.json").And.Contain("2026-10-22T09:00:00Z");
        text.Should().Contain("20 leaf/leaves").And.Contain("G.n0: 1 -> 2").And.Contain("... and 5 more");
        text.Should().Contain("G.s0: read-only");
    }

    [Fact]
    public void Popup_state_line_flags_RUN_prominently()
    {
        RestoreConfirmText.StateLine("Run").Should().StartWith("PLC state: RUN").And.Contain("overwritten");
        RestoreConfirmText.StateLine("Stop").Should().Be("PLC state: STOP");
        RestoreConfirmText.IsRunning("run").Should().BeTrue();
        RestoreConfirmText.IsRunning("Stop").Should().BeFalse();
    }

    [Fact]
    public void Executed_restore_is_summarised_with_counts_and_other_states_with_the_reason()
    {
        var p = Proposal("Run", 1);
        var done = p with
        {
            State = SnapshotRestoreState.Executed,
            Report = [new("a", "written", null, "OK"), new("b", "failed", null, "x"), new("c", "skipped", null, "y")],
        };
        ApprovalsView.SummariseRestore(done).Should().Be("Restore abc12345 executed: 1 written, 1 failed, 1 skipped.");
        ApprovalsView.SummariseRestore(p with { State = SnapshotRestoreState.Rejected, Reason = "PLC changed since the dry run" })
            .Should().Be("Restore abc12345 rejected: PLC changed since the dry run");
    }
}
