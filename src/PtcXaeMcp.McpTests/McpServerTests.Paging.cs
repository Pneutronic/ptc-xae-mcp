using System.Text.Json;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// The paging convention of the list tools: limit/offset in, total/returned/has_more/next_offset out. The old cap
/// parameters (maxResults, maxEvents) are gone and refused by name, with the valid parameters listed.
/// </summary>
public partial class McpServerTests
{
    [SkippableTheory]
    [InlineData("twincat_list_ethercat_box_catalog", "maxResults", 10)]
    [InlineData("twincat_read_event_logger", "maxEvents", 10)]
    public async Task Paging_TheOldCapParameters_AreRefusedByName(string tool, string oldName, int value)
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync(tool, new Dictionary<string, object?> { [oldName] = value }));

        Assert.StartsWith($"[Error] {tool} has no parameter '{oldName}'", text);
        Assert.Contains("limit", text);
        Assert.Contains("offset", text);
    }

    [SkippableTheory]
    [InlineData("twincat_list_ethercat_box_catalog")]
    [InlineData("twincat_read_event_logger")]
    [InlineData("twincat_list_ads_symbols")]
    [InlineData("twincat_hmi_list_mapped_symbols")]
    [InlineData("twincat_plc_search")]
    [InlineData("twincat_plc_static_analysis")]
    public async Task Paging_ANegativeOffset_IsRefusedBeforeAnythingIsRead(string tool)
    {
        SkipIfNotReachable();

        var args = new Dictionary<string, object?> { ["offset"] = -1 };
        if (tool == "twincat_plc_search") args["pattern"] = "x";
        var text = GetText(await CallToolAsync(tool, args));

        Assert.StartsWith("[Error] offset must be 0 or more", text);
    }

    [SkippableFact]
    public async Task Paging_EveryListToolDeclaresLimitAndOffset()
    {
        SkipIfNotReachable();
        string[] listTools =
        [
            "twincat_plc_list", "twincat_get_error_list", "twincat_plc_search", "twincat_plc_static_analysis",
            "twincat_list_ads_symbols", "twincat_list_ethercat_box_catalog", "twincat_read_event_logger",
            "twincat_persistent_snapshot_compare", "twincat_hmi_list_mapped_symbols", "twincat_read_coe_dictionary",
        ];

        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (string name in listTools)
        {
            Assert.True(tools.ContainsKey(name), $"{name} is not listed");
            var props = tools[name].JsonSchema.GetProperty("properties");
            Assert.True(props.TryGetProperty("limit", out _), $"{name} has no limit");
            Assert.True(props.TryGetProperty("offset", out _), $"{name} has no offset");
            foreach (string old in new[] { "maxResults", "maxEvents" })
                Assert.False(props.TryGetProperty(old, out _), $"{name} still has {old}");
        }
    }

    [SkippableFact]
    public async Task Paging_EtherCatCatalog_PagesWithoutASolution()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_list_ethercat_box_catalog",
            new Dictionary<string, object?> { ["filter"] = "EL", ["limit"] = 2, ["offset"] = 1 }));
        // A host without TwinCAT ESI files answers with an error; everything else must carry the page fields.
        Skip.If(text.StartsWith("[Error] ESI directory not found", StringComparison.Ordinal), text);

        int nl = text.IndexOf('\n');
        Assert.True(nl > 0, text);
        using var doc = JsonDocument.Parse(text[(nl + 1)..]);
        var root = doc.RootElement;
        int total = root.GetProperty("total").GetInt32();
        Assert.True(total > 3, text[..nl]);
        Assert.Equal(1, root.GetProperty("offset").GetInt32());
        Assert.Equal(2, root.GetProperty("returned").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(3, root.GetProperty("next_offset").GetInt32());
        Assert.Equal(2, root.GetProperty("devices").GetArrayLength());
    }
}
