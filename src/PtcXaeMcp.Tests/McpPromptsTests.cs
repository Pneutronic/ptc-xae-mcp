using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>The prompts name only existing tools and nothing internal; the resources decode their URIs.</summary>
public class McpPromptsTests
{
    private static IEnumerable<string> PromptNames() =>
        typeof(McpPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!);

    [Fact]
    public void EveryPrompt_HasATextAndAToolset()
    {
        var names = PromptNames().ToList();
        Assert.Equal(McpPrompts.Needs.Keys.Order(), names.Order());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(McpPrompts.Text(n))));
    }

    [Fact]
    public void ThePromptTexts_NameOnlyRegisteredTools()
    {
        foreach (var name in PromptNames())
        {
            var unknown = Regex.Matches(McpPrompts.Text(name), @"\btwincat_[a-z0-9_]+")
                .Select(m => m.Value)
                .Where(t => !ToolCatalog.Tools.ContainsKey(t))
                .Distinct()
                .ToList();
            Assert.True(unknown.Count == 0, $"{name} names unknown tools: {string.Join(", ", unknown)}");
        }
    }

    [Fact]
    public void ThePromptTexts_ContainNothingInternal()
    {
        string[] forbidden = ["/livetest", "/ship", "/build-feature", "orchestrator", "CLAUDE.md", "docs/specs", "WORKFLOW.md"];
        var terms = forbidden.Concat(PrivateTerms()).ToList();
        foreach (var name in PromptNames())
        {
            string text = McpPrompts.Text(name);
            foreach (var term in terms)
                Assert.False(text.Contains(term, StringComparison.OrdinalIgnoreCase), $"{name} contains '{term}'");
        }
    }

    // The maintainers' list of private terms lives outside every repository; on other machines it is absent.
    private static IEnumerable<string> PrivateTerms()
    {
        string? path = Environment.GetEnvironmentVariable("PTC_XAE_MCP_PRIVATE_TERMS");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ptc", "private-terms.txt");
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length >= 3 && !l.StartsWith('#'));
    }

    [Fact]
    public void Render_FillsEveryPlaceholder()
    {
        string text = McpPrompts.CommissionEtherCat("Device 1 (EtherCAT)", "10.1.1.1.1.1");
        Assert.Contains("Master: Device 1 (EtherCAT). Target system: 10.1.1.1.1.1.", text);
        Assert.DoesNotContain("{{", text);
        Assert.Contains("(not given)", McpPrompts.PlcChange(" "));
        Assert.DoesNotContain("{{", McpPrompts.ScopeMeasure("MAIN.a", "5"));
    }

    [Theory]
    [InlineData("TIPC%5EPlc%5EPlc%20Project%5EPOUs%5EMAIN", "TIPC^Plc^Plc Project^POUs^MAIN")]
    [InlineData("TIPC^Plc^Plc Project^POUs^MAIN", "TIPC^Plc^Plc Project^POUs^MAIN")]
    public void ElementPaths_AreDecoded(string raw, string expected) =>
        Assert.Equal(expected, McpResources.DecodePath(raw));

    [Fact]
    public void TheToolsetResource_ShowsWhatTheProfileHides()
    {
        string text = McpResources.DescribeToolsets(ToolProfiles.Resolve(null, null, "cursor"));
        Assert.Contains("hmi (hidden", text);
        Assert.Contains("plc (shown", text);
        Assert.Contains("twincat_plc_read", text);
    }
}
