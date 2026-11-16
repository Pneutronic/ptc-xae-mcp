using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// MCP protocol tests for the signal testbench group against the live MCP server (http://localhost:5010). No PLC
/// needed: registration, annotations, the schema and the limits that are refused before anything is opened.
/// Skipped when the server is not reachable.
/// </summary>
[Collection("McpServer")]
public class SignalTestbenchMcpTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private static readonly string[] Tools =
        ["twincat_ads_wait_until", "twincat_ads_expect", "twincat_ads_record", "twincat_ads_snapshot_compare"];

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

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    [SkippableFact]
    public async Task The_four_tools_are_registered_read_only_and_never_write()
    {
        SkipIfNotReachable();

        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (string name in Tools)
        {
            Assert.Contains(name, tools.Keys);
            Assert.True(tools[name].ProtocolTool.Annotations?.ReadOnlyHint, $"{name} is not read-only");
            Assert.False(tools[name].ProtocolTool.Annotations?.DestructiveHint ?? false, $"{name} is destructive");
        }

        // No stimulus in this group: whoever stimulates calls twincat_write_ads_variable (policy, allowlist).
        var group = (await _client.ListToolsAsync()).Select(t => t.Name)
            .Where(n => n.StartsWith("twincat_ads_", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(group, n => n.Contains("write", StringComparison.Ordinal) || n.Contains("stimulate", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_target_is_chosen_by_amsNetId_and_adsPort_like_the_other_ads_tools()
    {
        SkipIfNotReachable();

        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        foreach (string name in Tools)
        {
            var props = tools[name].JsonSchema.GetProperty("properties");
            Assert.True(props.TryGetProperty("amsNetId", out _), $"{name} has no amsNetId");
            Assert.True(props.TryGetProperty("adsPort", out _), $"{name} has no adsPort");
        }
        var required = (string name) => tools[name].JsonSchema.TryGetProperty("required", out var r)
            ? r.EnumerateArray().Select(e => e.GetString()).ToList()
            : new List<string?>();
        Assert.Contains("conditions", required("twincat_ads_wait_until"));
        Assert.Contains("mode", required("twincat_ads_expect"));
        Assert.Contains("durationMs", required("twincat_ads_expect"));
        Assert.Contains("symbols", required("twincat_ads_record"));
        Assert.DoesNotContain("timeoutMs", required("twincat_ads_wait_until"));
    }

    [SkippableFact]
    public async Task The_hard_limits_are_refused_before_anything_is_opened()
    {
        SkipIfNotReachable();

        Assert.StartsWith("[Error] timeoutMs must be 1..300000", await CallAsync("twincat_ads_wait_until",
            new() { ["conditions"] = new[] { "MAIN.n > 1" }, ["timeoutMs"] = 300_001 }));
        Assert.StartsWith("[Error] At most 8 conditions", await CallAsync("twincat_ads_wait_until",
            new() { ["conditions"] = Enumerable.Range(1, 9).Select(i => $"MAIN.n{i} > 1").ToArray() }));
        Assert.StartsWith("[Error] mode must be", await CallAsync("twincat_ads_expect",
            new() { ["mode"] = "sometimes", ["durationMs"] = 100 }));
        Assert.StartsWith("[Error] durationMs must be 1..300000", await CallAsync("twincat_ads_expect",
            new() { ["mode"] = "stable", ["durationMs"] = 400_000, ["symbols"] = new[] { "MAIN.n" } }));
        Assert.StartsWith("[Error] At most 32 symbols", await CallAsync("twincat_ads_record",
            new() { ["symbols"] = Enumerable.Range(1, 33).Select(i => $"MAIN.n{i}").ToArray() }));
        Assert.StartsWith("[Error] durationMs must be 1..120000", await CallAsync("twincat_ads_record",
            new() { ["symbols"] = new[] { "MAIN.n" }, ["durationMs"] = 120_001 }));
        Assert.StartsWith("[Error] cycleMs must be 1..1000", await CallAsync("twincat_ads_record",
            new() { ["symbols"] = new[] { "MAIN.n" }, ["cycleMs"] = 0 }));
        Assert.StartsWith("[Error] Nothing to do", await CallAsync("twincat_ads_snapshot_compare", new() { ["symbols"] = new[] { "MAIN.n" } }));
    }
}
