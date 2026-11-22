using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class VariantConfigEditorTests
{
    private const string TwoVariantsOneGroup =
        "<ProjectVariants>" +
        "<Variant><Name>Machine</Name></Variant>" +
        "<Variant><Name>Simulation</Name></Variant>" +
        "<VariantGroup><Name>AllSim</Name><Variant><Name>Simulation</Name></Variant></VariantGroup>" +
        "</ProjectVariants>";

    // ── ValidateName ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Machine")]
    [InlineData("Simulation_2")]
    [InlineData("Line-A")]
    public void ValidateName_Accepts(string name) => Assert.Null(VariantConfigEditor.ValidateName(name));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[Group]")]
    [InlineData("a<b")]
    [InlineData("a&b")]
    [InlineData("a^b")]
    [InlineData("a\"b")]
    public void ValidateName_Rejects(string name) => Assert.NotNull(VariantConfigEditor.ValidateName(name));

    // ── AddVariant ──────────────────────────────────────────────────────────

    [Fact]
    public void AddVariant_ToEmptyConfig_CreatesVariant()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(string.Empty, "Machine", null);
        Assert.Null(error);
        var (variants, groups) = VariantConfigParser.Parse(xml!);
        Assert.Equal(["Machine"], variants);
        Assert.Empty(groups);
    }

    [Fact]
    public void AddVariant_AppendsAndKeepsExisting()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "Test", null);
        Assert.Null(error);
        var (variants, groups) = VariantConfigParser.Parse(xml!);
        Assert.Equal(["Machine", "Simulation", "Test"], variants);
        Assert.Equal(["Simulation"], groups.Single().Members);
    }

    [Fact]
    public void AddVariant_Duplicate_IsRefusedCaseInsensitive()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "machine", null);
        Assert.Null(xml);
        Assert.Contains("already exists", error);
    }

    [Fact]
    public void AddVariant_NameOfAGroup_IsRefused()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "AllSim", null);
        Assert.Null(xml);
        Assert.Contains("group", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddVariant_InvalidName_IsRefused()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "[x]", null);
        Assert.Null(xml);
        Assert.StartsWith("[Error]", error);
    }

    [Fact]
    public void AddVariant_WithGroup_AddsMember()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "Sim2", "allsim");
        Assert.Null(error);
        var (variants, groups) = VariantConfigParser.Parse(xml!);
        Assert.Contains("Sim2", variants);
        Assert.Equal(["Simulation", "Sim2"], groups.Single().Members);
    }

    [Fact]
    public void AddVariant_UnknownGroup_IsRefusedAndListsGroups()
    {
        var (xml, error) = VariantConfigEditor.AddVariant(TwoVariantsOneGroup, "Sim2", "Nope");
        Assert.Null(xml);
        Assert.Contains("Unknown group 'Nope'", error);
        Assert.Contains("AllSim", error);
    }

    [Fact]
    public void AddVariant_GroupWithMemberElements_UsesMemberElement()
    {
        const string cfg =
            "<ProjectVariants><Variant><Name>A</Name></Variant>" +
            "<Group><Name>G</Name><Member><Name>A</Name></Member></Group></ProjectVariants>";
        var (xml, error) = VariantConfigEditor.AddVariant(cfg, "B", "G");
        Assert.Null(error);
        Assert.Contains("<Member><Name>B</Name></Member>", xml);
        var (_, groups) = VariantConfigParser.Parse(xml!);
        Assert.Equal(["A", "B"], groups.Single().Members);
    }

    [Fact]
    public void AddVariant_MalformedXml_IsRefused()
    {
        var (xml, error) = VariantConfigEditor.AddVariant("<ProjectVariants><Variant>", "A", null);
        Assert.Null(xml);
        Assert.StartsWith("[Error]", error);
    }

    // ── RemoveVariant ───────────────────────────────────────────────────────

    [Fact]
    public void RemoveVariant_RemovesItAndItsGroupMemberships()
    {
        var (xml, error, groups) = VariantConfigEditor.RemoveVariant(TwoVariantsOneGroup, "simulation");
        Assert.Null(error);
        Assert.Equal(["AllSim"], groups);
        var (variants, parsedGroups) = VariantConfigParser.Parse(xml!);
        Assert.Equal(["Machine"], variants);
        Assert.Empty(parsedGroups.Single().Members);
    }

    [Fact]
    public void RemoveVariant_Unknown_IsRefusedAndListsVariants()
    {
        var (xml, error, _) = VariantConfigEditor.RemoveVariant(TwoVariantsOneGroup, "Nope");
        Assert.Null(xml);
        Assert.Contains("Unknown variant 'Nope'", error);
        Assert.Contains("Machine", error);
    }

    [Fact]
    public void RemoveVariant_NoVariantsAtAll_SaysSo()
    {
        var (xml, error, _) = VariantConfigEditor.RemoveVariant(string.Empty, "A");
        Assert.Null(xml);
        Assert.Contains("no variants", error);
    }
}
