namespace PtcXaeMcp.McpTests;

/// <summary>The event-class tools over the wire: annotations, and argument refusals that are tool errors. The mutating ones
/// only run without an open solution.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task EventClassTools_Annotations()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        Assert.True(tools.Single(t => t.Name == "twincat_event_class_read").ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        foreach (string name in new[] { "twincat_event_class_create", "twincat_event_class_update", "twincat_plc_add_global_data_type" })
        {
            var tool = tools.Single(t => t.Name == name);
            Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false, name);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint ?? true, name);
        }
    }

    [SkippableFact]
    public async Task EventClassCreate_InvalidName_IsAToolError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_event_class_create", new Dictionary<string, object?>
        {
            ["name"] = "1 bad",
            ["events"] = new[] { new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Started" } },
        });
        var text = GetText(result);

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] Event class name", text);
    }

    [SkippableFact]
    public async Task EventClassCreate_DuplicateEventId_IsAToolError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_event_class_create", new Dictionary<string, object?>
        {
            ["name"] = "McpTestEvents",
            ["events"] = new[]
            {
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "A", ["severity"] = "Error", ["text"] = "a" },
                new Dictionary<string, object?> { ["id"] = 1, ["name"] = "B" },
            },
        });
        var text = GetText(result);

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("id 1 is used twice", text);
    }

    [SkippableFact]
    public async Task EventClassUpdate_NothingToUpdate_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_event_class_update", new Dictionary<string, object?> { ["name"] = "McpTestEvents" }));

        Assert.StartsWith("[Error] Nothing to update", text);
    }

    [SkippableFact]
    public async Task EventClassRead_IsAToolAnswer()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_event_class_read", new Dictionary<string, object?> { ["name"] = "1 bad" }));

        Assert.StartsWith("[Error] Event class name", text);
    }

    [SkippableFact]
    public async Task PlcAddGlobalDataType_InvalidName_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_plc_add_global_data_type", new Dictionary<string, object?> { ["typeName"] = "1 bad" }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] typeName", text);
    }
}
