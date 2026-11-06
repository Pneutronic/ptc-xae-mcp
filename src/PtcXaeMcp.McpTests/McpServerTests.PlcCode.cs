using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>PLCopen interchange and editor open/close. The twincat_plc_* verbs are covered in McpServerTests.PlcObjects.</summary>
public partial class McpServerTests
{
    // ── Test 49 — plcopen_export missing selector ──────────────────────────────

    [SkippableFact]
    public async Task T49_PlcOpenExport_MissingSelector_ReturnsError()
    {
        SkipIfNotReachable();

        // filePath must resolve under an allowed PathPolicy root (#173) so this test
        // actually reaches the selector check instead of failing on the path itself.
        var filePath = Path.Combine(Path.GetTempPath(), "export_" + Guid.NewGuid().ToString("N") + ".xml");
        var result = await CallToolAsync("twincat_plcopen_export",
            new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["selector"] = "",
            });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("selector", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 50 — plcopen_import unknown conflictMode ──────────────────────────

    [SkippableFact]
    public async Task T50_PlcOpenImport_UnknownConflictMode_ReturnsError()
    {
        SkipIfNotReachable();

        // filePath must resolve under an allowed PathPolicy root (#173) so this test
        // actually reaches the conflictMode check instead of failing on the path itself.
        var filePath = Path.Combine(Path.GetTempPath(), "import_" + Guid.NewGuid().ToString("N") + ".xml");
        var result = await CallToolAsync("twincat_plcopen_import",
            new Dictionary<string, object?>
            {
                ["filePath"] = filePath,
                ["conflictMode"] = "clobber",
            });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("clobber", text);
    }

    // ── Removed in 3.0.0 ────────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("twincat_list_pous")]
    [InlineData("twincat_read_pou")]
    [InlineData("twincat_read_gvl")]
    [InlineData("twincat_write_pou_body")]
    [InlineData("twincat_write_gvl")]
    [InlineData("twincat_create_pou")]
    [InlineData("twincat_create_dut")]
    [InlineData("twincat_create_gvl")]
    [InlineData("twincat_create_interface")]
    [InlineData("twincat_list_pou_members")]
    [InlineData("twincat_add_pou_member")]
    [InlineData("twincat_read_pou_member")]
    [InlineData("twincat_write_pou_member")]
    [InlineData("twincat_delete_plc_object")]
    [InlineData("twincat_rename_plc_object")]
    [InlineData("twincat_open_plc_object")]
    [InlineData("twincat_close_plc_object")]
    public async Task RemovedTools_AreNotListed(string tool)
    {
        SkipIfNotReachable();

        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.DoesNotContain(tool, names);
    }

    // ── open-object ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task OpenObject_AndCloseObject_AreListed()
    {
        SkipIfNotReachable();

        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Contains("twincat_open_object", names);
        Assert.Contains("twincat_close_object", names);
    }

    [SkippableFact]
    public async Task OpenObject_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("treePath", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task CloseObject_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_close_object",
            new Dictionary<string, object?> { ["treePath"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("treePath", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task OpenObject_UnknownPath_ReturnsStructuredError()
    {
        SkipIfNotReachable();

        // An unresolvable, non-TIID/TIPC path: the leaf can't be type-resolved and is not
        // in the SE tree, so the UIAutomation select fails → clean structured [Error],
        // never an empty string or a transport fault. (A 2-segment TIID^* path would be
        // type-resolved to the EtherCAT master by LookupTreeItemWithFallback, so it is
        // deliberately NOT used here.)
        var result = await CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = "ZZZ^NoSuchNode 9999" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.True(text.Length > 8, $"Error must be structured: '{text}'");
    }
}
