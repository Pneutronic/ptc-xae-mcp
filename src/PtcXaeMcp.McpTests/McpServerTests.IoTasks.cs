namespace PtcXaeMcp.McpTests;

/// <summary>
/// Real-time tasks, I/O tasks, the Profinet device and the sync task over the wire: annotations, parameter schemas and
/// argument refusals that come before any XAE access. The mutating ones only run without an open solution.
/// </summary>
public partial class McpServerTests
{
    [SkippableTheory]
    [InlineData("twincat_task_list", true, false)]
    [InlineData("twincat_profinet_list_modules", true, false)]
    [InlineData("twincat_task_configure", false, true)]
    [InlineData("twincat_io_device_sync_task", false, true)]
    [InlineData("twincat_io_task_create", false, false)]
    [InlineData("twincat_profinet_device_add", false, false)]
    [InlineData("twincat_profinet_module_add", false, false)]
    public async Task IoTaskTools_HaveTheirAnnotations(string name, bool readOnly, bool destructive)
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var tool = tools.Single(t => t.Name == name);
        Assert.Equal(readOnly, tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        if (!readOnly)
            Assert.Equal(destructive, tool.ProtocolTool.Annotations?.DestructiveHint ?? true);
    }

    [SkippableTheory]
    [InlineData("twincat_task_configure", "task")]
    [InlineData("twincat_io_task_create", "name")]
    [InlineData("twincat_io_task_create", "cycleTimeMs")]
    [InlineData("twincat_io_device_sync_task", "device")]
    [InlineData("twincat_profinet_device_add", "boxPath")]
    [InlineData("twincat_profinet_module_add", "slot")]
    [InlineData("twincat_profinet_list_modules", "device")]
    public async Task IoTaskTools_RequireTheirKeyParameter(string name, string parameter)
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var schema = tools.Single(t => t.Name == name).JsonSchema;
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), e => e.GetString() == parameter);
    }

    [SkippableFact]
    public async Task TaskConfigure_NothingToChange_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_task_configure",
            new Dictionary<string, object?> { ["task"] = "PlcTask" }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] Nothing to change", text);
    }

    [SkippableFact]
    public async Task IoTaskCreate_InvalidName_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_io_task_create",
            new Dictionary<string, object?> { ["name"] = "1 bad", ["cycleTimeMs"] = 2.0 }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] Task name", text);
    }

    [SkippableFact]
    public async Task ProfinetModuleAdd_SlotZero_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_profinet_module_add",
            new Dictionary<string, object?> { ["device"] = "TIID^PN Device", ["module"] = "BYTE_I4", ["slot"] = 0 }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] slot 0 is not allowed", text);
    }

    [SkippableFact]
    public async Task IoDeviceSyncTask_NotADevicePath_IsAToolError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_io_device_sync_task",
            new Dictionary<string, object?> { ["device"] = "PN Device" }));

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] 'PN Device' is not an I/O device path", text);
    }
}
