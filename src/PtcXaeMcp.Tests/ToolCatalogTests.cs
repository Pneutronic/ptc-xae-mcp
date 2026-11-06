using System.Reflection;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>Every tool has exactly one toolset and an access level that matches what it does.</summary>
public class ToolCatalogTests
{
    private static IEnumerable<(MethodInfo Method, string Name, bool ReadOnly)> Tools() =>
        ToolCatalog.ToolMethods().Select(t => (t.Method, t.Attribute.Name!, t.Attribute.ReadOnly));

    [Fact]
    public void EveryTool_IsAssignedToAToolset()
    {
        var missing = Tools().Select(t => t.Name).Where(n => !ToolCatalog.IsListed(n)).ToList();
        Assert.True(missing.Count == 0, "Not in a toolset (ToolCatalog): " + string.Join(", ", missing));
    }

    [Fact]
    public void TheTables_NameOnlyExistingTools() =>
        Assert.Empty(ToolCatalog.UnknownNamesInTables());

    [Fact]
    public void EveryRegisteredTool_IsInTheCatalog() =>
        Assert.Equal(Tools().Select(t => t.Name).Order(), ToolCatalog.Tools.Keys.Order());

    [Fact]
    public void ReadOnlyTools_AreReadLevel_AndNoRuntimeActionIsReadOnly()
    {
        foreach (var tool in ToolCatalog.Tools.Values)
        {
            if (tool.ReadOnly) Assert.True(tool.Level == AccessLevel.Read, $"{tool.Name} is ReadOnly but {tool.Level}");
            else Assert.True(tool.Level != AccessLevel.Read, $"{tool.Name} changes something but is Read");
        }
    }

    [Fact]
    public void EveryChangingToolWithAnAmsNetId_IsARuntimeActionOrAlwaysAllowed()
    {
        var offenders = Tools()
            .Where(t => !t.ReadOnly && t.Method.GetParameters().Any(p => string.Equals(p.Name, "amsNetId", StringComparison.OrdinalIgnoreCase)))
            .Select(t => ToolCatalog.Tools[t.Name])
            .Where(t => t.Level != AccessLevel.Runtime && !t.AlwaysAllowed)
            .Select(t => t.Name)
            .ToList();
        Assert.True(offenders.Count == 0, "Reaches a target system but is not a runtime action: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RuntimeActionsWithAnAmsNetIdParameter_TakeTheTargetFromIt()
    {
        foreach (var (method, name, _) in Tools())
        {
            var tool = ToolCatalog.Tools[name];
            bool hasParam = method.GetParameters().Any(p => p.Name == "amsNetId");
            if (tool.Target == TargetRule.AmsNetIdArgument)
                Assert.True(hasParam, $"{name} reads its target from amsNetId but has no such parameter");
        }
    }

    [Theory]
    [InlineData("twincat_activate_config", "runtime")]
    [InlineData("twincat_login", "runtime")]
    [InlineData("twincat_write_ads_variable", "runtime")]
    [InlineData("twincat_plc_write", "engineer")]
    [InlineData("twincat_build_plc", "engineer")]
    [InlineData("twincat_logout", "engineer")]
    [InlineData("twincat_plc_read", "read")]
    [InlineData("twincat_read_ads_variable", "read")]
    public void Levels_OfKnownTools(string name, string level) =>
        Assert.Equal(level, AccessPolicy.Name(ToolCatalog.Tools[name].Level));

    [Theory]
    [InlineData("twincat_stop")]
    [InlineData("twincat_shutdown")]
    [InlineData("twincat_force_close_xae")]
    [InlineData("twincat_ibn_abort")]
    [InlineData("twincat_ibn_disarm")]
    [InlineData("twincat_ibn_release_all")]
    public void EmergencyStops_AreAlwaysAllowed(string name) =>
        Assert.True(ToolCatalog.Tools[name].AlwaysAllowed);

    [Fact]
    public void HmiTools_AreInTheHmiToolset() =>
        Assert.All(ToolCatalog.Tools.Values.Where(t => t.Name.StartsWith("twincat_hmi_", StringComparison.Ordinal)),
            t => Assert.Equal(Toolset.Hmi, t.Toolset));

    [Fact]
    public void TheCoreProfile_StaysSmall()
    {
        var core = ToolProfiles.Parse(ToolProfiles.Core, ProfileSource.Default, "test");
        int count = ToolCatalog.Tools.Values.Count(t => core.Shows(t.Toolset));
        Assert.InRange(count, 1, 120);
    }
}
