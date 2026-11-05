using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// MCP protocol tests for the hmi-server feature group against the live MCP server
/// (http://localhost:5010). XAE is NOT required: these cover tool registration, the
/// gate-relevant NEGATIVE invariant (no parameter can bypass the engineer's popup)
/// and the input validation that must answer before any COM call. Skipped
/// automatically when the server is not reachable.
/// <para>A partial of <see cref="McpServerTests"/>, so every call goes through its guarded
/// <c>CallToolAsync</c> (issue #157 item 7): read-only HMI tools are on <c>NonMutatingTools</c>,
/// every other HMI tool only runs when no solution is open.</para>
/// </summary>
public partial class McpServerTests
{
    private static readonly string[] HmiTools =
    [
        "twincat_hmi_list_projects",
        "twincat_hmi_get_project_info",
        "twincat_hmi_list_mapped_symbols",
        "twincat_hmi_read_symbols",
        "twincat_hmi_server_control",
        "twincat_hmi_map_symbol",
        "twincat_hmi_unmap_symbol",
        "twincat_hmi_write_symbol",
        // hmi-views
        "twincat_hmi_read_file",
        "twincat_hmi_add_item",
        "twincat_hmi_write_file",
        "twincat_hmi_import_file",
        "twincat_hmi_build",
        "twincat_hmi_apply_bundle",
        "twincat_hmi_delete_item",
        // hmi-ads-tmc
        "twincat_hmi_list_tmc_files",
        "twincat_hmi_list_ads_runtimes",
        "twincat_hmi_list_plc_tmc_candidates",
        "twincat_hmi_attach_tmc_file",
        "twincat_hmi_detach_tmc_file",
        "twincat_hmi_enable_runtime_tmc",
        "twincat_hmi_disable_runtime_tmc",
        // hmi-internal-symbols (#165)
        "twincat_hmi_list_internal_symbols",
        "twincat_hmi_add_internal_symbol",
        "twincat_hmi_remove_internal_symbol",
        "twincat_hmi_map_offline_symbol",
    ];

    private static readonly string[] HmiWriteTools =
    [
        "twincat_hmi_map_symbol",
        "twincat_hmi_unmap_symbol",
        "twincat_hmi_write_symbol",
        "twincat_hmi_add_item",
        "twincat_hmi_write_file",
        "twincat_hmi_import_file",
        "twincat_hmi_apply_bundle",
        "twincat_hmi_delete_item",
        "twincat_hmi_attach_tmc_file",
        "twincat_hmi_detach_tmc_file",
        "twincat_hmi_enable_runtime_tmc",
        "twincat_hmi_disable_runtime_tmc",
        "twincat_hmi_add_internal_symbol",
        "twincat_hmi_remove_internal_symbol",
        "twincat_hmi_map_offline_symbol",
    ];

    /// <summary>Calls <paramref name="tool"/> through the guarded <see cref="CallToolAsync"/> and
    /// returns its text.</summary>
    private async Task<string> CallAsync(string tool, Dictionary<string, object?>? args = null) =>
        GetText(await CallToolAsync(tool, args));

    // ── registration ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task HmiTools_AreAllRegistered()
    {
        SkipIfNotReachable();
        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        foreach (string tool in HmiTools)
            Assert.Contains(tool, names);
    }

    // ── gate-relevant NEGATIVE invariant ───────────────────────────────────────

    [SkippableFact]
    public async Task HmiWriteTools_HaveNoConfirmationBypassParameter()
    {
        SkipIfNotReachable();
        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (string tool in HmiWriteTools)
        {
            var properties = tools[tool].JsonSchema.GetProperty("properties")
                .EnumerateObject().Select(p => p.Name).ToList();

            // "Ask every time": the engineer confirms every write in the host popup. The
            // only confirmation-related parameter is how long the popup waits.
            var bypass = properties.Where(p =>
                    p != "confirmTimeoutSeconds" &&
                    (p.Contains("confirm", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("force", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("skip", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("bypass", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("auto", StringComparison.OrdinalIgnoreCase) ||
                     p.Contains("noPrompt", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.True(bypass.Count == 0, $"{tool} exposes a confirmation bypass: {string.Join(", ", bypass)}");
            Assert.Contains("confirmTimeoutSeconds", properties);
        }

        Assert.DoesNotContain(tools.Keys, n =>
            n.StartsWith("twincat_hmi_", StringComparison.Ordinal) &&
            n.Contains("confirm", StringComparison.OrdinalIgnoreCase));
    }

    // ── validation answers before any COM call (XAE not needed) ────────────────

    [SkippableFact]
    public async Task MapSymbol_WithoutNames_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_map_symbol");
        Assert.StartsWith("[Error] Pass plcPath", text);
    }

    [SkippableFact]
    public async Task MapSymbol_ArrayPath_NeedsExplicitNames()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_map_symbol",
            new Dictionary<string, object?> { ["plcPath"] = "MAIN.aValues[1]" });
        Assert.StartsWith("[Error]", text);
        Assert.Contains("mapName + internalName", text);
    }

    [SkippableFact]
    public async Task UnmapSymbol_BothNames_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_unmap_symbol",
            new Dictionary<string, object?> { ["mapName"] = "ADS.PLC1.MAIN.x", ["plcPath"] = "MAIN.x" });
        Assert.StartsWith("[Error] Pass exactly one", text);
    }

    [SkippableFact]
    public async Task WriteSymbol_InvalidJson_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_write_symbol",
            new Dictionary<string, object?> { ["symbol"] = "ADS.PLC1.MAIN.x", ["valueJson"] = "text" });
        Assert.StartsWith("[Error] valueJson is not valid JSON", text);
    }

    [SkippableTheory]
    [InlineData(5)]
    [InlineData(601)]
    public async Task WriteTools_ConfirmTimeoutOutOfRange_IsRefused(int seconds)
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_write_symbol", new Dictionary<string, object?>
        {
            ["symbol"] = "UserSelectType",
            ["valueJson"] = "0",
            ["confirmTimeoutSeconds"] = seconds,
        });
        Assert.StartsWith("[Error] confirmTimeoutSeconds", text);
    }

    [SkippableFact]
    public async Task ReadSymbols_Empty_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_read_symbols",
            new Dictionary<string, object?> { ["symbols"] = Array.Empty<string>() });
        Assert.StartsWith("[Error] symbols is required", text);
    }

    // ── hmi-ads-tmc: the file is identified before any COM call ────────────────

    [SkippableFact]
    public async Task AttachTmcFile_RelativePath_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_attach_tmc_file",
            new Dictionary<string, object?> { ["tmcFile"] = @"PLC\PLC.tmc" });
        Assert.StartsWith("[Error]", text);
        Assert.Contains("absolute path", text);
    }

    [SkippableFact]
    public async Task AttachTmcFile_MissingFile_IsRefused()
    {
        SkipIfNotReachable();
        // Under %TEMP% (an allowed root) so the PathPolicy lets it through to the file check.
        string missing = Path.Combine(Path.GetTempPath(),
            "ptc-xae-mcp-no-such-dir-" + Guid.NewGuid().ToString("N"), "PLC.tmc");
        string text = await CallAsync("twincat_hmi_attach_tmc_file",
            new Dictionary<string, object?> { ["tmcFile"] = missing });
        Assert.StartsWith("[Error]", text);
        Assert.Contains("does not exist", text);
    }

    [SkippableFact]
    public async Task AttachTmcFile_OutsideAllowedRoots_IsRefusedByThePathPolicy()
    {
        SkipIfNotReachable();
        string outside = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
            "ptc-xae-mcp-tmc-outside-" + Guid.NewGuid().ToString("N"), "PLC.tmc");
        string text = await CallAsync("twincat_hmi_attach_tmc_file",
            new Dictionary<string, object?> { ["tmcFile"] = outside });
        Assert.StartsWith("[Error] tmcFile: ", text);
        Assert.Contains("PTC_XAE_MCP_ALLOWED_PATHS", text);
    }

    [SkippableTheory]
    [InlineData("PLC 1")]
    [InlineData("PLC1::MAIN")]
    public async Task DetachTmcFile_InvalidVirtualPlcName_IsRefused(string name)
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_detach_tmc_file",
            new Dictionary<string, object?> { ["virtualPlcName"] = name });
        Assert.StartsWith("[Error] virtualPlcName is required", text);
    }

    [SkippableFact]
    public async Task AttachTmcFile_RequiresTheTmcFileParameter()
    {
        SkipIfNotReachable();
        var tool = (await _client!.ListToolsAsync()).Single(t => t.Name == "twincat_hmi_attach_tmc_file");
        var required = tool.JsonSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("tmcFile", required);
        Assert.DoesNotContain("virtualPlcName", required);   // defaults to the file name
    }

    // #161 server-side TMC: validation answers before any COM call

    [SkippableTheory]
    [InlineData("twincat_hmi_enable_runtime_tmc", "PLC 1")]
    [InlineData("twincat_hmi_enable_runtime_tmc", "PLC1::TMC")]
    [InlineData("twincat_hmi_disable_runtime_tmc", "ADS.PLC1")]
    public async Task RuntimeTmc_InvalidRuntime_IsRefused(string tool, string runtime)
    {
        SkipIfNotReachable();
        var arguments = new Dictionary<string, object?> { ["runtime"] = runtime };
        // Only the enable tool takes a TMC file; an argument a tool does not declare is refused.
        if (tool == "twincat_hmi_enable_runtime_tmc") arguments["tmcFile"] = Path.Combine(Path.GetTempPath(), "PLC.tmc");
        string text = await CallAsync(tool, arguments);
        Assert.StartsWith("[Error] runtime is required", text);
    }

    [SkippableFact]
    public async Task EnableRuntimeTmc_OutsideAllowedRoots_IsRefusedByThePathPolicy()
    {
        SkipIfNotReachable();
        string outside = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\",
            "ptc-xae-mcp-rttmc-outside-" + Guid.NewGuid().ToString("N"), "PLC.tmc");
        string text = await CallAsync("twincat_hmi_enable_runtime_tmc",
            new Dictionary<string, object?> { ["runtime"] = "PLC1", ["tmcFile"] = outside });
        Assert.StartsWith("[Error] tmcFile: ", text);
        Assert.Contains("PTC_XAE_MCP_ALLOWED_PATHS", text);
    }

    [SkippableFact]
    public async Task RuntimeTmc_RequiredParameters()
    {
        SkipIfNotReachable();
        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        List<string?> Required(string tool) => tools[tool].JsonSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).ToList();

        Assert.Equal(["runtime", "tmcFile"], Required("twincat_hmi_enable_runtime_tmc").Order());
        Assert.Equal(["runtime"], Required("twincat_hmi_disable_runtime_tmc"));
    }

    // hmi-internal-symbols (#165): validation answers before any COM call

    [SkippableFact]
    public async Task AddInternalSymbol_InvalidName_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_add_internal_symbol",
            new Dictionary<string, object?> { ["name"] = "bad name", ["dataType"] = "boolean" });
        Assert.StartsWith("[Error] name 'bad name' is not a valid internal symbol name", text);
    }

    [SkippableFact]
    public async Task MapOfflineSymbol_InvalidDataType_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_map_offline_symbol",
            new Dictionary<string, object?> { ["mapName"] = "Recipe.Mode", ["dataType"] = "BOOL" });
        Assert.StartsWith("[Error] dataType must be", text);
    }

    [SkippableFact]
    public async Task MapOfflineSymbol_ObjectDefault_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_map_offline_symbol",
            new Dictionary<string, object?>
            {
                ["mapName"] = "Recipe.Mode", ["dataType"] = "boolean", ["defaultValueJson"] = "{\"a\":1}",
            });
        Assert.StartsWith("[Error] defaultValueJson must be a JSON scalar", text);
    }

    [SkippableFact]
    public async Task ServerControl_UnknownAction_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_server_control",
            new Dictionary<string, object?> { ["action"] = "publish" });
        Assert.StartsWith("[Error] action must be one of", text);
    }

    [SkippableFact]
    public async Task GetProjectInfo_DepthOutOfRange_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_get_project_info",
            new Dictionary<string, object?> { ["includeFiles"] = true, ["maxDepth"] = 9 });
        Assert.StartsWith("[Error] maxDepth", text);
    }

    // ── hmi-views: validation answers before any COM call ─────────────────────

    [SkippableTheory]
    [InlineData("../Desktop.view")]
    [InlineData(@"C:\Temp\x.view")]
    [InlineData("/Desktop.view")]
    public async Task ReadFile_PathOutsideTheProject_IsRefused(string path)
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_read_file", new Dictionary<string, object?> { ["path"] = path });
        Assert.StartsWith("[Error] Invalid project path", text);
    }

    [SkippableFact]
    public async Task AddItem_UnknownKind_IsRefusedWithTheKindList()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_add_item", new Dictionary<string, object?>
        {
            ["kind"] = "page", ["folder"] = "UserControls", ["name"] = "X",
        });
        Assert.StartsWith("[Error] kind must be one of", text);
        Assert.Contains("usercontrol", text);
    }

    [SkippableFact]
    public async Task AddItem_NameWithFolder_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_add_item", new Dictionary<string, object?>
        {
            ["kind"] = "html", ["folder"] = "Docs", ["name"] = "sub/x",
        });
        Assert.StartsWith("[Error] name must be a plain file name", text);
    }

    [SkippableFact]
    public async Task WriteFile_NoSource_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_write_file", new Dictionary<string, object?> { ["path"] = "Desktop.view" });
        Assert.StartsWith("[Error] Pass exactly one of source", text);
    }

    [SkippableFact]
    public async Task ImportFile_RelativeSource_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_import_file", new Dictionary<string, object?> { ["sourcePath"] = "doc.html" });
        Assert.StartsWith("[Error] sourcePath must be an absolute path", text);
    }

    [SkippableFact]
    public async Task ApplyBundle_MissingManifest_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_apply_bundle", new Dictionary<string, object?>
        {
            ["manifestPath"] = @"C:\does-not-exist\bundle.json",
        });
        Assert.StartsWith("[Error] manifest not found", text);
    }

    [SkippableTheory]
    [InlineData("Server", "project infrastructure")]
    [InlineData("../Desktop.view", "Invalid project path")]
    public async Task DeleteItem_ProtectedOrInvalidPath_IsRefusedBeforeAnyCall(string path, string expected)
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_delete_item", new Dictionary<string, object?> { ["path"] = path });
        Assert.StartsWith("[Error]", text);
        Assert.Contains(expected, text);
    }

    [SkippableFact]
    public async Task ViewsWriteTools_ConfirmTimeoutOutOfRange_IsRefused()
    {
        SkipIfNotReachable();
        string text = await CallAsync("twincat_hmi_add_item", new Dictionary<string, object?>
        {
            ["kind"] = "html", ["folder"] = "Docs", ["name"] = "x", ["confirmTimeoutSeconds"] = 3,
        });
        Assert.StartsWith("[Error] confirmTimeoutSeconds", text);
    }
}
