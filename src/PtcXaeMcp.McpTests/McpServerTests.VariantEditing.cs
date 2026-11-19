using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Creating and deleting project variants.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task VariantEditing_ToolsListed_WithSchemasAndHints()
    {
        SkipIfNotReachable();
        var tools = await _client!.ListToolsAsync();
        var create = tools.Single(t => t.Name == "twincat_variant_create");
        var delete = tools.Single(t => t.Name == "twincat_variant_delete");

        Assert.Contains("variantName", create.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Contains("group", create.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Contains("variantName", delete.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.True(delete.ProtocolTool.Annotations?.DestructiveHint);
        Assert.False(create.ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [SkippableFact]
    public async Task VariantEditing_Create_InvalidName_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_variant_create",
            new Dictionary<string, object?> { ["variantName"] = "[bad]" });
        Assert.StartsWith("[Error]", GetText(result));
    }

    [SkippableFact]
    public async Task VariantEditing_Delete_EmptyName_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_variant_delete",
            new Dictionary<string, object?> { ["variantName"] = "" });
        Assert.StartsWith("[Error]", GetText(result));
    }
}
