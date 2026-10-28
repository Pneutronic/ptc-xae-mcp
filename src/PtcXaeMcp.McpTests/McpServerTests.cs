using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// MCP protocol tests against the live Pneutronic XAE MCP server (http://localhost:5010).
/// Exercises the Claude-Code-to-C# layer; XAE is not required for these tests.
/// All tests are skipped automatically when the server is not reachable.
/// Start ptc-xae-mcp.exe --diagnostics before running (some tests here call the
/// diagnostics-only tool surface — ptc_test_marker, twincat_show_step,
/// twincat_safety_recon, twincat_safety_probe_create_alias, twincat_dump_nc_tree,
/// twincat_open_plc_object/twincat_close_plc_object — which a plain launch does not
/// register; see issue #157 item 1).
/// <para>
/// The tests are split per feature into <c>McpServerTests.&lt;Feature&gt;.cs</c> partials; this file holds
/// the connection plumbing and the mutating-call guard (<see cref="CallToolAsync"/>): a tool that is not
/// on <see cref="NonMutatingTools"/> is only called when no solution is open, so the suite never builds,
/// activates, saves, closes or edits whatever project the developer has open (issue #157 item 7).
/// </para>
/// </summary>
[Collection("McpServer")]
public partial class McpServerTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private McpClient? _client;
    private bool _serverReachable;

    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly string ValidSchemaPath =
        Path.Combine(FixturesDir, "valid_schema.json");

    private static readonly string NonExistentSchemaPath =
        Path.Combine(FixturesDir, "does_not_exist.json");

    public async Task InitializeAsync()
    {
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _serverReachable = true;
        }
        catch
        {
            _serverReachable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }

    private void SkipIfNotReachable()
    {
        if (!_serverReachable)
            throw new Xunit.SkipException("MCP server not reachable on " + ServerUrl);
    }

    private static void SkipIfXaeNotRunning(string toolResponse)
    {
        // Catches both the English guard ("No TwinCAT XAE instance is running.")
        // and the raw German COM throw ("Keine laufende TwinCAT XAE Instanz gefunden.")
        // that surfaces when the DTE cannot be acquired without going through a guard.
        // Also skips when XAE is running but has no solution open — several tools
        // require an open solution and would give "[Error] No solution is currently open."
        // Uses Xunit.SkipException so [SkippableFact] tests are reported as SKIP not FAIL.
        if (toolResponse.Contains("No TwinCAT XAE instance", StringComparison.OrdinalIgnoreCase)
         || toolResponse.Contains("Keine laufende TwinCAT XAE", StringComparison.OrdinalIgnoreCase)
         || toolResponse.Contains("No solution is currently open", StringComparison.OrdinalIgnoreCase)
         || toolResponse.Contains("Keine Lösung ist geöffnet", StringComparison.OrdinalIgnoreCase))
            throw new Xunit.SkipException("XAE not ready — skipping test that requires a live XAE with open solution");
    }

    /// <summary>Returns true when the MCP server reports no XAE process is running.</summary>
    private static bool ProbeXaeAbsent(string getActiveProjectResponse) =>
        getActiveProjectResponse.Contains("No TwinCAT XAE instance", StringComparison.OrdinalIgnoreCase)
     || getActiveProjectResponse.Contains("Keine laufende TwinCAT XAE", StringComparison.OrdinalIgnoreCase);

    // ── Mutating-call guard ────────────────────────────────────────────────────

    /// <summary>
    /// Tools that neither change the open project/solution nor the target/runtime/license state, so
    /// they may be called whatever is open. Editor-navigation tools only open/close editor tabs.
    /// Every tool NOT on this list is treated as mutating (fail-safe default).
    /// </summary>
    internal static readonly IReadOnlySet<string> NonMutatingTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "twincat_get_status", "twincat_get_log", "twincat_stop", "twincat_get_client_info",
        "twincat_get_active_project", "twincat_resolve_host", "ptc_test_marker",
        "twincat_validate_schema",
        "twincat_list_library_references", "twincat_list_installed_libraries", "twincat_check_libraries_installed",
        "twincat_ipc_read_inventory", "twincat_ethercat_read_identities", "twincat_list_library_repositories",
        "twincat_get_fieldbus_topology", "twincat_get_device_xml", "twincat_get_iolink_config",
        "twincat_read_ethercat_startup",
        "twincat_check_iodd_present",
        "twincat_list_pous", "twincat_read_pou", "twincat_read_gvl", "twincat_list_pou_members",
        "twincat_read_pou_member", "twincat_plcopen_export",
        "twincat_plc_list", "twincat_plc_read", "twincat_plc_list_entries",
        "twincat_get_scope_status", "twincat_list_scope_channels", "twincat_read_scope_acquisition",
        "twincat_broadcast_search", "twincat_read_event_logger", "twincat_get_license_info",
        "twincat_list_nc_tasks_axes", "twincat_read_axis_params", "twincat_read_ads_variable",
        "twincat_locate_step", "twincat_goto_line", "twincat_show_step",
        "twincat_open_object", "twincat_close_object", "twincat_open_plc_object", "twincat_close_plc_object",
        "twincat_hmi_list_projects", "twincat_hmi_get_project_info", "twincat_hmi_list_mapped_symbols",
        "twincat_hmi_read_symbols", "twincat_hmi_read_file",
        "twincat_hmi_list_tmc_files", "twincat_hmi_list_ads_runtimes", "twincat_hmi_list_plc_tmc_candidates",
        "twincat_hmi_list_internal_symbols",
        "twincat_read_init_symbols", "twincat_read_retain_config", "twincat_get_type_file_generation",
    };

    /// <summary>
    /// Calls a tool through the MCP client. A mutating tool (not on <see cref="NonMutatingTools"/>)
    /// is only called when the server reports no open solution; otherwise the test is skipped, so the
    /// suite never touches the project the developer happens to have open.
    /// </summary>
    private async Task<CallToolResult> CallToolAsync(string toolName, Dictionary<string, object?>? arguments = null)
    {
        if (!NonMutatingTools.Contains(toolName))
            await SkipIfSolutionOpenAsync(toolName);
        return await _client!.CallToolAsync(toolName, arguments);
    }

    private async Task SkipIfSolutionOpenAsync(string toolName)
    {
        var probe = GetText(await _client!.CallToolAsync("twincat_get_active_project"));
        if (!IsSafeForMutatingCall(probe))
            throw new Xunit.SkipException(
                $"{toolName} is a mutating tool and a solution is open (or its state is unknown) — " +
                "McpTests only call mutating tools when no solution is open. Close the solution to run this test.");
    }

    /// <summary>
    /// True when the <c>twincat_get_active_project</c> answer proves no solution is open: no XAE at all,
    /// or the JSON payload with <c>"solutionOpen":false</c>. Anything else (open solution, busy XAE,
    /// unparseable answer) is treated as unsafe.
    /// </summary>
    internal static bool IsSafeForMutatingCall(string activeProjectResponse)
    {
        if (ProbeXaeAbsent(activeProjectResponse)) return true;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(activeProjectResponse);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("solutionOpen", out var open)
                && open.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0)
            return string.Empty;
        return string.Join("\n", content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));
    }
}
