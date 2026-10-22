using ModelContextProtocol.Client;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// MCP protocol tests for the persistent-snapshot group against the live MCP server
/// (http://localhost:5010). No PLC needed: tool registration, flags, the dry-run default and the
/// gate-relevant NEGATIVE invariant (no tool confirms a restore). Skipped when the server is not reachable.
/// </summary>
[Collection("McpServer")]
public class PersistentSnapshotMcpTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private McpClient? _client;
    private bool _serverReachable;

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
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReachable()
    {
        if (!_serverReachable)
            throw new Xunit.SkipException("MCP server not reachable on " + ServerUrl);
    }

    [SkippableFact]
    public async Task All_five_tools_are_registered_with_the_right_annotations()
    {
        SkipIfNotReachable();

        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (var name in new[]
                 {
                     "twincat_list_ads_symbols", "twincat_persistent_snapshot_save", "twincat_persistent_snapshot_compare",
                     "twincat_persistent_snapshot_restore", "twincat_persistent_snapshot_restore_status",
                 })
            Assert.Contains(name, tools.Keys);

        Assert.True(tools["twincat_persistent_snapshot_save"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["twincat_persistent_snapshot_compare"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools["twincat_persistent_snapshot_restore_status"].ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tools["twincat_persistent_snapshot_restore"].ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.True(tools["twincat_persistent_snapshot_restore"].ProtocolTool.Annotations?.DestructiveHint);
    }

    [SkippableFact]
    public async Task No_tool_confirms_or_executes_a_restore()
    {
        SkipIfNotReachable();

        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToList();
        var forbidden = names.Where(n =>
            (n.Contains("snapshot", StringComparison.OrdinalIgnoreCase) || n.Contains("restore", StringComparison.OrdinalIgnoreCase)) &&
            (n.Contains("confirm", StringComparison.OrdinalIgnoreCase) || n.Contains("execute", StringComparison.OrdinalIgnoreCase) ||
             n.Contains("apply", StringComparison.OrdinalIgnoreCase) || n.Contains("approve", StringComparison.OrdinalIgnoreCase))).ToList();

        // A --diagnostics host (ptc_test_marker) also offers the live suite's test hook; a released host must not.
        if (names.Contains("ptc_test_marker"))
            forbidden.Remove("twincat_persistent_snapshot_test_confirm");

        Assert.True(forbidden.Count == 0, $"Forbidden AI-reachable execute surface found: {string.Join(", ", forbidden)}");
    }

    [SkippableFact]
    public async Task Restore_input_schema_does_not_require_dryRun()
    {
        SkipIfNotReachable();

        var tool = (await _client!.ListToolsAsync()).Single(t => t.Name == "twincat_persistent_snapshot_restore");
        var required = tool.JsonSchema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()).ToList()
            : new List<string?>();
        Assert.DoesNotContain("dryRun", required);
        Assert.Contains("snapshotPath", required);
    }
}
