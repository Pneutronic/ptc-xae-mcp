namespace PtcXaeMcp.McpTests;

/// <summary>twincat_plc_task_create over the wire: registered as a non-read-only tool, argument refusals are tool errors.
/// It is mutating, so it only runs without an open solution.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task PlcTaskCreate_IsListedAsNotReadOnly()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var tool = tools.Single(t => t.Name == "twincat_plc_task_create");
        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint ?? true);
    }

    [SkippableFact]
    public async Task PlcTaskCreate_InvalidName_IsAToolError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_task_create",
            new Dictionary<string, object?> { ["name"] = "1 bad name", ["cycleTimeMs"] = 10.0 });
        var text = GetText(result);

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] Task name", text);
    }

    [SkippableFact]
    public async Task PlcTaskCreate_NonPositiveCycleTime_IsAToolError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_plc_task_create",
            new Dictionary<string, object?> { ["name"] = "FastTask", ["cycleTimeMs"] = 0.0 });
        var text = GetText(result);

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] cycleTimeMs must be greater than 0", text);
    }
}
