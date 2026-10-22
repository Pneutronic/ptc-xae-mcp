using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// persistent-snapshot: remanent-leaf selection from hand-written TMC snippets (no PLC).
public class TmcRemanentSelectorTests
{
    private const string Mark = "<Properties><Property><Name>TcPersistent</Name></Property></Properties>";
    private const string Ignore = "<Properties><Property><Name>TcIgnorePersistent</Name></Property></Properties>";

    private static string Tmc(string dataTypes, string internalSymbols, string retainSymbols = "") =>
        "<TcModuleClass><DataTypes>" + dataTypes + "</DataTypes><Modules><Module><DataAreas>" +
        "<DataArea><AreaNo AreaType=\"Internal\" CreateSymbols=\"true\">3</AreaNo><Name>Internal</Name>" + internalSymbols + "</DataArea>" +
        "<DataArea><AreaNo AreaType=\"RetainSrc\" CreateSymbols=\"true\">4</AreaNo><Name>Retains</Name>" + retainSymbols + "</DataArea>" +
        "</DataAreas></Module></Modules></TcModuleClass>";

    private static string Sym(string name, string type, string extra = "") =>
        $"<Symbol><Name>{name}</Name><BitSize>8</BitSize><BaseType>{type}</BaseType>{extra}</Symbol>";

    private static string Arr(int lo, int n) => $"<ArrayInfo><LBound>{lo}</LBound><Elements>{n}</Elements></ArrayInfo>";

    private static string Sub(string name, string type, string extra = "") =>
        $"<SubItem><Name>{name}</Name><Type>{type}</Type><BitSize>8</BitSize>{extra}</SubItem>";

    private static TmcSelection Select(string xml, string kind = "both")
    {
        TmcRemanentSelector.TrySelect(xml, kind, 1000, out var sel, out string error).Should().BeTrue(error);
        return sel;
    }

    [Fact]
    public void Retain_symbol_in_the_retain_area_is_selected()
    {
        var sel = Select(Tmc("", Sym("GVL.nNormal", "DINT"), retainSymbols: Sym("GVL_Settings.nRetainCounter", "UDINT")));

        sel.Leaves.Should().ContainSingle()
            .Which.Should().Be(new TmcLeaf("GVL_Settings.nRetainCounter", "UDINT", false, "retain"));
    }

    [Fact]
    public void TcPersistent_on_a_symbol_selects_the_whole_struct_with_arrays_by_declared_index()
    {
        string dts = "<DataType><Name>ST_Pos</Name>" + Sub("rX", "LREAL") + Sub("nId", "INT") + "</DataType>" +
                     "<DataType><Name>ST_Recipe</Name>" + Sub("sName", "STRING(20)") +
                     Sub("aPos", "ST_Pos", Arr(1, 2)) + "</DataType>";
        var sel = Select(Tmc(dts, Sym("GVL_Recipe.stActive", "ST_Recipe", Mark)));

        sel.Leaves.Select(l => l.Path).Should().Equal(
            "GVL_Recipe.stActive.aPos[1].nId", "GVL_Recipe.stActive.aPos[1].rX",
            "GVL_Recipe.stActive.aPos[2].nId", "GVL_Recipe.stActive.aPos[2].rX",
            "GVL_Recipe.stActive.sName");
        sel.Leaves.Should().OnlyContain(l => l.Kind == "persistent");
        sel.Leaves.Single(l => l.Path.EndsWith("sName", StringComparison.Ordinal)).Type.Should().Be("STRING");
    }

    [Fact]
    public void TcPersistent_on_a_SubItem_marks_every_instance_of_the_type_including_nested_and_arrayed()
    {
        string dts = "<DataType><Name>FB_Axis</Name>" + Sub("nPersistent", "DINT", Mark) + Sub("nTemp", "DINT") + "</DataType>" +
                     "<DataType><Name>FB_Cell</Name>" + Sub("fbAxis", "FB_Axis", Arr(0, 2)) + "</DataType>";
        var sel = Select(Tmc(dts, Sym("MAIN.fbCell", "FB_Cell") + Sym("MAIN.fbSolo", "FB_Axis")));

        sel.Leaves.Select(l => l.Path).Should().Equal(
            "MAIN.fbCell.fbAxis[0].nPersistent", "MAIN.fbCell.fbAxis[1].nPersistent", "MAIN.fbSolo.nPersistent");
    }

    [Fact]
    public void TcIgnorePersistent_excludes_the_subtree_even_inside_a_persistent_instance()
    {
        string dts = "<DataType><Name>FB_X</Name>" + Sub("nKeep", "INT") + Sub("pPtr", "INT", Ignore) + "</DataType>";
        var sel = Select(Tmc(dts, Sym("G.fb", "FB_X", Mark)));

        sel.Leaves.Select(l => l.Path).Should().Equal("G.fb.nKeep");
    }

    [Fact]
    public void Lower_bound_other_than_zero_and_multidimensional_arrays_keep_declared_indices()
    {
        string dts = "<DataType><Name>ST_M</Name>" + Sub("a", "INT", Arr(-1, 2) + Arr(5, 2)) + "</DataType>";
        var sel = Select(Tmc(dts, Sym("G.m", "ST_M", Mark)));

        sel.Leaves.Select(l => l.Path).Should().Equal(
            "G.m.a[-1,5]", "G.m.a[-1,6]", "G.m.a[0,5]", "G.m.a[0,6]");
    }

    [Fact]
    public void Enums_aliases_and_time_types_are_leaves_pointers_are_skipped_with_reason()
    {
        string dts = "<DataType><Name>E_Mode</Name><BaseType>INT</BaseType><EnumInfo><Text>a</Text><Enum>0</Enum></EnumInfo></DataType>" +
                     "<DataType><Name>T_Count</Name><BaseType>UDINT</BaseType></DataType>" +
                     "<DataType><Name>ST_All</Name>" + Sub("eMode", "E_Mode") + Sub("nCount", "T_Count") +
                     Sub("tDelay", "TIME") + Sub("dtAt", "DT") + Sub("wName", "WSTRING(10)") +
                     Sub("pData", "POINTER TO BYTE") + "</DataType>";
        var sel = Select(Tmc(dts, Sym("G.s", "ST_All", Mark)));

        sel.Leaves.Select(l => (l.Path, l.Type, l.IsEnum)).Should().Equal(
            ("G.s.dtAt", "DT", false), ("G.s.eMode", "E_MODE", true), ("G.s.nCount", "UDINT", false),
            ("G.s.tDelay", "TIME", false), ("G.s.wName", "WSTRING", false));
        sel.Skipped.Should().ContainSingle(s => s.Path == "G.s.pData" && s.Reason.Contains("POINTER"));
    }

    [Fact]
    public void Kind_filter_separates_persistent_from_retain()
    {
        string xml = Tmc("", Sym("G.nP", "INT", Mark), Sym("G.nR", "INT"));

        Select(xml, "persistent").Leaves.Select(l => l.Path).Should().Equal("G.nP");
        Select(xml, "retain").Leaves.Select(l => l.Path).Should().Equal("G.nR");
        Select(xml, "both").Leaves.Should().HaveCount(2);
    }

    [Fact]
    public void Leaf_limit_and_invalid_xml_are_errors_not_partial_results()
    {
        string dts = "<DataType><Name>ST_A</Name>" + Sub("a", "INT", Arr(0, 50)) + "</DataType>";
        TmcRemanentSelector.TrySelect(Tmc(dts, Sym("G.s", "ST_A", Mark)), "both", 10, out _, out string e1)
            .Should().BeFalse();
        e1.Should().Contain("10");

        TmcRemanentSelector.TrySelect("<broken", "both", 10, out _, out string e2).Should().BeFalse();
        e2.Should().Contain("XML");
    }
}
