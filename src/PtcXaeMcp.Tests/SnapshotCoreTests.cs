using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// persistent-snapshot: value rules, file round-trip, pathMap, compare and restore plan.
public class SnapshotCoreTests
{
    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
    private static SnapshotLeaf L(string type, string raw, string? name = null, string? display = null) =>
        new(type, J(raw), name, display);

    private static Dictionary<string, SnapshotLeaf> Map(params (string Path, SnapshotLeaf Leaf)[] items) =>
        items.ToDictionary(i => i.Path, i => i.Leaf, StringComparer.Ordinal);

    // ---- values -------------------------------------------------------------------

    [Theory]
    [InlineData("1", "1", true)]
    [InlineData("1", "1.0", true)]
    [InlineData("1", "2", false)]
    [InlineData("18446744073709551615", "18446744073709551615", true)]
    [InlineData("1.5", "1.5", true)]
    [InlineData("1.5", "1.6", false)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("\"a\"", "\"A\"", false)]
    [InlineData("true", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("1", "true", false)]
    public void ValueEquals_compares_by_value(string a, string b, bool expected)
        => SnapshotValues.ValueEquals(J(a), J(b)).Should().Be(expected);

    [Fact]
    public void Coerce_integer_family_within_range_and_refuses_out_of_range()
    {
        SnapshotValues.TryCoerce(L("INT", "100"), "DINT", false, out var c, out _).Should().BeTrue();
        c.Type.Should().Be("DINT");
        SnapshotValues.TryCoerce(L("DINT", "70000"), "INT", false, out _, out string reason).Should().BeFalse();
        reason.Should().Contain("out of range");
        SnapshotValues.TryCoerce(L("INT", "-1"), "UINT", false, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Coerce_real_to_lreal_widens_and_lreal_to_real_needs_exact()
    {
        SnapshotValues.TryCoerce(L("REAL", "1.5"), "LREAL", false, out _, out _).Should().BeTrue();
        SnapshotValues.TryCoerce(L("LREAL", "1.5"), "REAL", false, out _, out _).Should().BeTrue();
        SnapshotValues.TryCoerce(L("LREAL", "0.1"), "REAL", false, out _, out string r).Should().BeFalse();
        r.Should().Contain("REAL");
    }

    [Fact]
    public void Coerce_refuses_across_families_enums_and_time_types()
    {
        SnapshotValues.TryCoerce(L("INT", "1"), "BOOL", false, out _, out _).Should().BeFalse();
        SnapshotValues.TryCoerce(L("INT", "1"), "E_MODE", true, out _, out _).Should().BeFalse();
        SnapshotValues.TryCoerce(L("E_OLD", "1"), "E_MODE", true, out _, out _).Should().BeFalse();
        SnapshotValues.TryCoerce(L("E_MODE", "2"), "E_MODE", true, out _, out _).Should().BeTrue();
        SnapshotValues.TryCoerce(L("TIME", "1500"), "LTIME", false, out _, out _).Should().BeFalse();
        SnapshotValues.TryCoerce(L("STRING", "\"x\""), "WSTRING", false, out _, out _).Should().BeTrue();
    }

    // ---- file ----------------------------------------------------------------------

    [Fact]
    public void File_round_trips_every_supported_type_exactly()
    {
        var values = Map(
            ("G.bFlag", L("BOOL", "true")),
            ("G.nBig", L("ULINT", "18446744073709551615")),
            ("G.nNeg", L("LINT", "-9223372036854775808")),
            ("G.rX", L("LREAL", "0.1")),
            ("G.rY", L("REAL", "3.5")),
            ("G.sText", L("STRING", "\"Grüße \\\"q\\\"\"")),
            ("G.wText", L("WSTRING", "\"日本語\"")),
            ("G.eMode", L("E_MODE", "2", name: "eAuto")),
            ("G.tDelay", L("TIME", "1500", display: "T#1s500ms")),
            ("G.ltLong", L("LTIME", "123456789012")),
            ("G.dtStamp", L("DT", "1700000000", display: "DT#2023-11-14-22:13:20")),
            ("G.aPos[1].rX", L("LREAL", "12.5")),
            ("G.aGrid[1,2]", L("INT", "7")));
        var doc = new SnapshotDocument(
            new SnapshotMeta("2026-10-22T10:00:00Z", "5.1.2.3.1.1", 851, "sol", "plc", ["G"], values.Count,
                [new SnapshotSkip("G.pPtr", "POINTER")]),
            values);

        SnapshotFile.TryParse(SnapshotFile.Serialize(doc), out var back, out string error).Should().BeTrue(error);

        back!.Meta.Should().BeEquivalentTo(doc.Meta);
        back.Values.Keys.Should().BeEquivalentTo(values.Keys);
        foreach (var (path, leaf) in values)
        {
            back.Values[path].Type.Should().Be(leaf.Type, path);
            back.Values[path].Name.Should().Be(leaf.Name, path);
            back.Values[path].Display.Should().Be(leaf.Display, path);
            SnapshotValues.ValueEquals(back.Values[path].Value, leaf.Value).Should().BeTrue(path);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"format\":\"other/1\"}")]
    public void File_parse_refuses_foreign_or_malformed_input(string json)
    {
        SnapshotFile.TryParse(json, out var doc, out string error).Should().BeFalse();
        doc.Should().BeNull();
        error.Should().NotBeEmpty();
    }

    // ---- pathMap -------------------------------------------------------------------

    [Fact]
    public void PathMap_maps_exact_and_prefix_on_segment_boundaries_only()
    {
        SnapshotPathMap.TryParse("{\"GVL_Old.stRecipe\":\"GVL_New.stRecipe\",\"GVL_Old.nA\":\"GVL_New.nAlpha\"}",
            out var map, out _).Should().BeTrue();

        map.Apply("GVL_Old.stRecipe").Should().Be("GVL_New.stRecipe");
        map.Apply("GVL_Old.stRecipe.rX").Should().Be("GVL_New.stRecipe.rX");
        map.Apply("GVL_Old.stRecipe[3].rX").Should().Be("GVL_New.stRecipe[3].rX");
        map.Apply("GVL_Old.stRecipeExtra.rX").Should().Be("GVL_Old.stRecipeExtra.rX"); // not a boundary
        map.Apply("GVL_Old.nA").Should().Be("GVL_New.nAlpha");
        map.Apply("GVL_Old.nAB").Should().Be("GVL_Old.nAB");
    }

    [Fact]
    public void PathMap_longest_rule_wins_and_conflicts_are_reported()
    {
        SnapshotPathMap.TryParse("{\"A\":\"B\",\"A.x\":\"C.y\"}", out var map, out _).Should().BeTrue();
        map.Apply("A.x").Should().Be("C.y");
        map.Apply("A.z").Should().Be("B.z");

        var (_, conflicts) = map.MapAll(["A.x", "C.y"]);
        conflicts.Should().ContainSingle();
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"a\":\"\"}")]
    [InlineData("{oops")]
    public void PathMap_parse_refuses_bad_input(string json)
        => SnapshotPathMap.TryParse(json, out _, out string error).Should().BeFalse(error);

    [Fact]
    public void PathMap_blank_means_no_rules()
    {
        SnapshotPathMap.TryParse(null, out var map, out _).Should().BeTrue();
        map.Count.Should().Be(0);
    }

    // ---- compare -------------------------------------------------------------------

    [Fact]
    public void Compare_classifies_equal_different_orphans_and_type_changes()
    {
        var snap = Map(
            ("G.a", L("INT", "1")),
            ("G.b", L("INT", "2")),
            ("G.gone", L("INT", "9")),
            ("G.t", L("INT", "5")));
        var live = Map(
            ("G.a", L("INT", "1")),
            ("G.b", L("INT", "3")),
            ("G.new", L("INT", "0")),
            ("G.t", L("STRING", "\"x\"")));

        var r = SnapshotDiff.Compare(snap, live, SnapshotPathMap.Empty);

        r.Equal.Should().BeFalse();
        r.EqualCount.Should().Be(1);
        r.Differences.Should().ContainSingle(d => d.Path == "G.b");
        r.OnlyInSnapshot.Should().Equal("G.gone");
        r.OnlyOnPlc.Should().Equal("G.new");
        r.TypeMismatches.Should().ContainSingle(t => t.Path == "G.t");
    }

    [Fact]
    public void Compare_applies_pathMap_and_reports_equal()
    {
        var snap = Map(("Old.x", L("INT", "4")));
        var live = Map(("New.x", L("DINT", "4")));
        SnapshotPathMap.TryParse("{\"Old\":\"New\"}", out var map, out _);

        SnapshotDiff.Compare(snap, live, map).Equal.Should().BeTrue();
    }

    [Fact]
    public void Compare_of_identical_maps_is_equal()
    {
        var m = Map(("G.a", L("LREAL", "1.5")));
        SnapshotDiff.Compare(m, m, SnapshotPathMap.Empty).Equal.Should().BeTrue();
    }

    // ---- restore plan --------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> NoSkips = new Dictionary<string, string>();

    [Fact]
    public void Plan_writes_only_differing_leaves_and_skips_with_reason()
    {
        var snap = Map(
            ("G.same", L("INT", "1")),
            ("G.diff", L("INT", "2")),
            ("G.missing", L("INT", "3")),
            ("G.retyped", L("INT", "4")),
            ("G.ro", L("INT", "5")));
        var live = Map(
            ("G.same", L("INT", "1")),
            ("G.diff", L("INT", "9")),
            ("G.retyped", L("BOOL", "false")));
        var liveSkipped = new Dictionary<string, string> { ["G.ro"] = "read-only" };

        var plan = SnapshotDiff.BuildPlan("1.2.3.4.1.1:851", snap, live, liveSkipped, SnapshotPathMap.Empty);

        plan.Writes.Should().ContainSingle(w => w.Path == "G.diff" && w.New.Value.GetInt32() == 2);
        plan.Unchanged.Should().Be(1);
        plan.Skips.Select(s => s.Path).Should().BeEquivalentTo("G.missing", "G.retyped", "G.ro");
        plan.Skips.Single(s => s.Path == "G.ro").Reason.Should().Be("read-only");
    }

    [Fact]
    public void Plan_include_limits_to_the_chosen_leaves()
    {
        var snap = Map(("G.a", L("INT", "1")), ("G.b", L("INT", "2")));
        var live = Map(("G.a", L("INT", "0")), ("G.b", L("INT", "0")));

        var plan = SnapshotDiff.BuildPlan("t", snap, live, NoSkips, SnapshotPathMap.Empty, ["G.b"]);

        plan.Writes.Select(w => w.Path).Should().Equal("G.b");
    }

    [Fact]
    public void Plan_hash_is_stable_and_changes_with_target_values_and_live_state()
    {
        var snap = Map(("G.a", L("INT", "1")));
        var live0 = Map(("G.a", L("INT", "0")));
        var live7 = Map(("G.a", L("INT", "7")));
        var snap2 = Map(("G.a", L("INT", "2")));

        string h = SnapshotDiff.BuildPlan("t", snap, live0, NoSkips, SnapshotPathMap.Empty).PlanHash;

        SnapshotDiff.BuildPlan("t", snap, live0, NoSkips, SnapshotPathMap.Empty).PlanHash.Should().Be(h);
        SnapshotDiff.BuildPlan("other", snap, live0, NoSkips, SnapshotPathMap.Empty).PlanHash.Should().NotBe(h);
        SnapshotDiff.BuildPlan("t", snap, live7, NoSkips, SnapshotPathMap.Empty).PlanHash.Should().NotBe(h);
        SnapshotDiff.BuildPlan("t", snap2, live0, NoSkips, SnapshotPathMap.Empty).PlanHash.Should().NotBe(h);
    }

    [Fact]
    public void Plan_array_bound_shift_writes_only_the_overlap()
    {
        // snapshot had ARRAY[0..2]; the PLC now has ARRAY[1..3]
        var snap = Map(("G.a[0]", L("INT", "10")), ("G.a[1]", L("INT", "11")), ("G.a[2]", L("INT", "12")));
        var live = Map(("G.a[1]", L("INT", "0")), ("G.a[2]", L("INT", "0")), ("G.a[3]", L("INT", "0")));

        var plan = SnapshotDiff.BuildPlan("t", snap, live, NoSkips, SnapshotPathMap.Empty);

        plan.Writes.Select(w => w.Path).Should().Equal("G.a[1]", "G.a[2]");
        plan.Skips.Select(s => s.Path).Should().Equal("G.a[0]");
    }

    [Fact]
    public void Plan_pathMap_conflict_is_a_skip_not_a_silent_merge()
    {
        var snap = Map(("A.x", L("INT", "1")), ("B.x", L("INT", "2")));
        var live = Map(("C.x", L("INT", "0")));
        SnapshotPathMap.TryParse("{\"A\":\"C\",\"B\":\"C\"}", out var map, out _);

        var plan = SnapshotDiff.BuildPlan("t", snap, live, NoSkips, map);

        plan.Skips.Should().Contain(s => s.Path == "(pathMap)");
        plan.Writes.Should().HaveCount(1);
    }
}
