using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// persistent-snapshot: save / compare / restore against an in-memory PLC (no ADS, no XAE).
public sealed class PersistentSnapshotCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptc-snap-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTwinCatSession _plc = new();
    private readonly SnapshotRestoreCoordinator _restore;
    private readonly PersistentSnapshotCoordinator _sut;
    private DateTime _now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);

    public PersistentSnapshotCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _restore = new SnapshotRestoreCoordinator(_plc, () => _now);
        _sut = new PersistentSnapshotCoordinator(_plc, _restore, () => _now);
        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "3");
        _plc.PlcLeaves["GVL.stRecipe.rSpeed"] = Leaf("LREAL", "12.5");
        _plc.PlcLeaves["GVL.stRecipe.sName"] = Leaf("STRING", "\"alpha\"");
        _plc.PlcLeaves["GVL.other.nX"] = Leaf("INT", "1");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SnapshotLeaf Leaf(string type, string json) => new(type, FakeTwinCatSession.Json(json));
    private string File_(string name) => Path.Combine(_dir, name);
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private string SaveRoots(string file = "snap.json")
    {
        string r = _sut.Save("both", ["GVL.stRecipe"], "roots", null, File_(file), null, null);
        r.Should().NotStartWith("[Error]");
        return File_(file);
    }

    // ---- save ------------------------------------------------------------------------

    [Fact]
    public void Save_with_roots_writes_a_snapshot_of_exactly_the_subtree()
    {
        string result = _sut.Save("both", ["GVL.stRecipe"], "roots", null, File_("a.json"), null, null);

        var j = Parse(result);
        j.GetProperty("success").GetBoolean().Should().BeTrue();
        j.GetProperty("leafCount").GetInt32().Should().Be(3);
        j.GetProperty("selectionSource").GetString().Should().Be("roots");
        SnapshotFile.TryParse(File.ReadAllText(File_("a.json")), out var doc, out _).Should().BeTrue();
        doc!.Values.Keys.Should().BeEquivalentTo("GVL.stRecipe.nCount", "GVL.stRecipe.rSpeed", "GVL.stRecipe.sName");
        doc.Meta.Utc.Should().Be("2026-10-22T10:00:00Z");
    }

    [Fact]
    public void Save_refuses_a_path_outside_the_allowed_roots_and_a_non_json_name()
    {
        _sut.Save("both", ["GVL.stRecipe"], "roots", null, @"C:\Windows\System32\snap.json", null, null)
            .Should().StartWith("[Error] path refused");
        _sut.Save("both", ["GVL.stRecipe"], "roots", null, File_("snap.txt"), null, null)
            .Should().StartWith("[Error]").And.Contain(".json");
    }

    [Fact]
    public void Save_with_nothing_selected_is_an_error_not_an_empty_file()
    {
        _sut.Save("both", ["GVL.nothing"], "roots", null, File_("e.json"), null, null)
            .Should().StartWith("[Error] Nothing to save");
        File.Exists(File_("e.json")).Should().BeFalse();
    }

    [Fact]
    public void Save_rejects_bad_selection_and_kind_and_retain_over_ads_alone()
    {
        _sut.Save("both", null, "bogus", null, null, null, null).Should().StartWith("[Error] selection");
        _sut.Save("nope", null, "auto", null, null, null, null).Should().StartWith("[Error] kind");
        _sut.Save("retain", null, "ads", null, File_("r.json"), null, null).Should().StartWith("[Error] RETAIN");
    }

    private string WriteTmc(params string[] persistentPaths)
    {
        string symbols = string.Concat(persistentPaths.Select(p =>
            $"<Symbol><Name>{p}</Name><BaseType>INT</BaseType><Properties><Property><Name>TcPersistent</Name></Property></Properties></Symbol>"));
        string xml = "<TcModuleClass><DataTypes/><Modules><Module><DataAreas><DataArea><AreaNo AreaType=\"Internal\">3</AreaNo>" +
                     symbols + "</DataArea></DataAreas></Module></Modules></TcModuleClass>";
        string path = File_("Plc.tmc");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void Save_by_tmc_uses_the_compiler_view_and_the_guard_passes_when_the_plc_matches()
    {
        string tmc = WriteTmc("GVL.stRecipe.nCount", "GVL.other.nX");

        string result = _sut.Save("both", null, "tmc", tmc, File_("t.json"), null, null);

        Parse(result).GetProperty("selectionSource").GetString().Should().Be("tmc");
        SnapshotFile.TryParse(File.ReadAllText(File_("t.json")), out var doc, out _);
        doc!.Values.Keys.Should().BeEquivalentTo("GVL.stRecipe.nCount", "GVL.other.nX");
    }

    [Fact]
    public void Save_by_tmc_refuses_a_stale_tmc_instead_of_a_partial_snapshot()
    {
        string tmc = WriteTmc("GVL.stRecipe.nCount", "GVL.removed.nGone");

        string r1 = _sut.Save("both", null, "tmc", tmc, File_("s.json"), null, null);
        r1.Should().StartWith("[Error] TMC does not match the running PLC").And.Contain("GVL.removed.nGone");

        _plc.PlcLeaves["GVL.removed.nGone"] = Leaf("STRING", "\"x\"");
        string r2 = _sut.Save("both", null, "tmc", tmc, File_("s.json"), null, null);
        r2.Should().StartWith("[Error] TMC does not match").And.Contain("TMC INT, PLC STRING");
        File.Exists(File_("s.json")).Should().BeFalse();
    }

    // ---- compare ---------------------------------------------------------------------

    [Fact]
    public void Compare_reports_exactly_the_changed_leaves()
    {
        string snap = SaveRoots();
        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "9");

        var j = Parse(_sut.Compare(snap, null, null, null, null, null, null));

        j.GetProperty("equal").GetBoolean().Should().BeFalse();
        j.GetProperty("differenceCount").GetInt32().Should().Be(1);
        var d = j.GetProperty("differences")[0];
        d.GetProperty("path").GetString().Should().Be("GVL.stRecipe.nCount");
        d.GetProperty("snapshot").GetInt32().Should().Be(3);
        d.GetProperty("live").GetInt32().Should().Be(9);
    }

    [Fact]
    public void Compare_of_unchanged_plc_is_equal_and_snapshot_to_snapshot_needs_no_plc()
    {
        string a = SaveRoots("a.json");
        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "4");
        string b = SaveRoots("b.json");

        Parse(_sut.Compare(a, null, null, "x.tmc", null, null, null)).GetProperty("differenceCount").GetInt32().Should().Be(1);

        _plc.CallLog.Clear();
        var j = Parse(_sut.Compare(a, b, null, null, null, null, null));
        j.GetProperty("differenceCount").GetInt32().Should().Be(1);
        _plc.CallLog.Should().NotContain(c => c.StartsWith("ReadSnapshotLeaves", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_applies_pathMap()
    {
        string snap = SaveRoots();
        _plc.PlcLeaves.Clear();
        _plc.PlcLeaves["GVL.stNew.nCount"] = Leaf("INT", "3");
        _plc.PlcLeaves["GVL.stNew.rSpeed"] = Leaf("LREAL", "12.5");
        _plc.PlcLeaves["GVL.stNew.sName"] = Leaf("STRING", "\"alpha\"");

        var j = Parse(_sut.Compare(snap, null, "{\"GVL.stRecipe\":\"GVL.stNew\"}", null, null, null, null));

        j.GetProperty("onlyInSnapshotCount").GetInt32().Should().Be(0);
        j.GetProperty("differenceCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Compare_refuses_missing_or_foreign_files()
    {
        _sut.Compare(File_("nope.json"), null, null, null, null, null, null).Should().Contain("does not exist");
        File.WriteAllText(File_("x.json"), "{}");
        _sut.Compare(File_("x.json"), null, null, null, null, null, null).Should().StartWith("[Error]");
        _sut.Compare(null, null, null, null, null, null, null).Should().Contain("snapshotPath is required");
    }

    // ---- restore ---------------------------------------------------------------------

    private string DriftPlc()
    {
        string snap = SaveRoots();
        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "9");
        _plc.PlcLeaves["GVL.stRecipe.sName"] = Leaf("STRING", "\"beta\"");
        return snap;
    }

    private string DryRunHash(string snap)
    {
        var j = Parse(_sut.Restore(snap, true, null, null, null, null, null));
        j.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        return j.GetProperty("planHash").GetString()!;
    }

    [Fact]
    public void Restore_dry_run_lists_the_plan_and_writes_nothing()
    {
        string snap = DriftPlc();

        var j = Parse(_sut.Restore(snap, true, null, null, null, null, null));

        j.GetProperty("writeCount").GetInt32().Should().Be(2);
        j.GetProperty("plcState").GetString().Should().Be("Run");
        _plc.PlcLeaves["GVL.stRecipe.nCount"].Value.GetInt32().Should().Be(9);
        _plc.CallLog.Should().NotContain(c => c.StartsWith("WriteSnapshotLeaves", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_apply_needs_the_current_planHash()
    {
        string snap = DriftPlc();
        _restore.ProposalRaised += _ => { };

        _sut.Restore(snap, false, null, null, null, null, null).Should().StartWith("[Error] planHash is required");
        _sut.Restore(snap, false, "deadbeef", null, null, null, null).Should().StartWith("[Error] planHash does not match");
        _restore.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Restore_without_a_host_window_is_refused_because_nobody_can_confirm()
    {
        string snap = DriftPlc();
        string hash = DryRunHash(snap);

        _sut.Restore(snap, false, hash, null, null, null, null).Should().Contain("No host window");
    }

    [Fact]
    public void Restore_only_writes_after_the_human_confirms_and_reports_read_back_values()
    {
        string snap = DriftPlc();
        string hash = DryRunHash(snap);
        RestoreProposal? raised = null;
        _restore.ProposalRaised += p => raised = p;

        var j = Parse(_sut.Restore(snap, false, hash, null, null, null, null));

        j.GetProperty("status").GetString().Should().Be("pending");
        raised.Should().NotBeNull();
        raised!.PlcState.Should().Be("Run");
        _plc.PlcLeaves["GVL.stRecipe.nCount"].Value.GetInt32().Should().Be(9, "nothing is written before the confirm");

        var done = _restore.ConfirmProposal(raised.RestoreId);

        done.State.Should().Be(SnapshotRestoreState.Executed);
        _plc.PlcLeaves["GVL.stRecipe.nCount"].Value.GetInt32().Should().Be(3);
        _plc.PlcLeaves["GVL.stRecipe.sName"].Value.GetString().Should().Be("alpha");
        var status = Parse(_sut.RestoreStatus(raised.RestoreId));
        status.GetProperty("status").GetString().Should().Be("executed");
        status.GetProperty("report").GetProperty("written").GetInt32().Should().Be(2);
        // a restored PLC compares equal
        Parse(_sut.Compare(snap, null, null, "x.tmc", null, null, null)).GetProperty("equal").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Restore_is_rejected_when_the_plc_changed_between_dry_run_and_confirm()
    {
        string snap = DriftPlc();
        string hash = DryRunHash(snap);
        RestoreProposal? raised = null;
        _restore.ProposalRaised += p => raised = p;
        _sut.Restore(snap, false, hash, null, null, null, null);

        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "77"); // somebody changed it meanwhile

        var done = _restore.ConfirmProposal(raised!.RestoreId);

        done.State.Should().Be(SnapshotRestoreState.Rejected);
        done.Reason.Should().Contain("PLC changed since the dry run");
        _plc.PlcLeaves["GVL.stRecipe.nCount"].Value.GetInt32().Should().Be(77);
        _plc.CallLog.Should().NotContain(c => c.StartsWith("WriteSnapshotLeaves", StringComparison.Ordinal));
    }

    [Fact]
    public void Restore_reject_and_expiry_write_nothing()
    {
        string snap = DriftPlc();
        string hash = DryRunHash(snap);
        var raised = new List<RestoreProposal>();
        _restore.ProposalRaised += raised.Add;

        _sut.Restore(snap, false, hash, null, null, null, null);
        _restore.RejectProposal(raised[0].RestoreId).State.Should().Be(SnapshotRestoreState.Rejected);
        _restore.ConfirmProposal(raised[0].RestoreId).State.Should().Be(SnapshotRestoreState.Rejected, "a rejected proposal stays rejected");

        _sut.Restore(snap, false, hash, null, null, null, null);
        _now = _now.AddSeconds(SnapshotRestoreCoordinator.DefaultTtlSeconds + 1);
        _restore.Get(raised[1].RestoreId)!.State.Should().Be(SnapshotRestoreState.Expired);
        _restore.ConfirmProposal(raised[1].RestoreId).State.Should().Be(SnapshotRestoreState.Expired);

        _plc.CallLog.Should().NotContain(c => c.StartsWith("WriteSnapshotLeaves", StringComparison.Ordinal));
        _plc.PlcLeaves["GVL.stRecipe.nCount"].Value.GetInt32().Should().Be(9);
    }

    [Fact]
    public void Restore_reports_a_leaf_that_did_not_land_as_failed()
    {
        string snap = DriftPlc();
        string hash = DryRunHash(snap);
        RestoreProposal? raised = null;
        _restore.ProposalRaised += p => raised = p;
        _sut.Restore(snap, false, hash, null, null, null, null);
        _plc.WriteDoesNotLand.Add("GVL.stRecipe.nCount");

        var done = _restore.ConfirmProposal(raised!.RestoreId);

        done.State.Should().Be(SnapshotRestoreState.Executed);
        done.Report!.Single(r => r.Path == "GVL.stRecipe.nCount").Outcome.Should().Be("failed");
        done.Report!.Single(r => r.Path == "GVL.stRecipe.sName").Outcome.Should().Be("written");
    }

    [Fact]
    public void Restore_include_limits_the_plan_and_skips_are_reported()
    {
        string snap = SaveRoots();
        _plc.PlcLeaves["GVL.stRecipe.nCount"] = Leaf("INT", "9");
        _plc.PlcLeaves["GVL.stRecipe.rSpeed"] = Leaf("LREAL", "1.0");
        _plc.PlcSkipped["GVL.stRecipe.sName"] = "read-only";

        var j = Parse(_sut.Restore(snap, true, null, null, ["GVL.stRecipe.nCount"], null, null));

        j.GetProperty("writeCount").GetInt32().Should().Be(1);
        j.GetProperty("writes")[0].GetProperty("path").GetString().Should().Be("GVL.stRecipe.nCount");
    }

    [Fact]
    public void Restore_with_nothing_to_write_is_an_error_when_proposing()
    {
        string snap = SaveRoots();
        string hash = DryRunHash(snap);
        _restore.ProposalRaised += _ => { };

        _sut.Restore(snap, false, hash, null, null, null, null).Should().Contain("nothing to write");
    }

    [Fact]
    public void Status_of_an_unknown_id_is_an_error()
        => _sut.RestoreStatus("nope").Should().StartWith("[Error] Unknown restoreId");
}
