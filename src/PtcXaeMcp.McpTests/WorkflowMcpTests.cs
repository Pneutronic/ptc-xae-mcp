using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>
/// MCP protocol tests for the two workflow tools against the live MCP server (http://localhost:5010). No XAE project
/// or runtime needed: registration, annotations, the schema and the arguments that are refused before anything runs.
/// Skipped when the server is not reachable.
/// </summary>
[Collection("McpServer")]
public class WorkflowMcpTests : IAsyncLifetime
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

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    [SkippableFact]
    public async Task Deploy_is_a_destructive_tool_that_takes_a_request_key_and_a_confirmation_token()
    {
        SkipIfNotReachable();

        var tool = (await _client!.ListToolsAsync()).Single(t => t.Name == "twincat_deploy");

        Assert.False(tool.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
        Assert.True(tool.ProtocolTool.Annotations?.DestructiveHint);
        var props = tool.JsonSchema.GetProperty("properties");
        foreach (string name in new[] { "requestKey", "confirmationToken", "expectSymbols", "lifeSign", "buildSolution", "restart", "startPlc" })
            Assert.True(props.TryGetProperty(name, out _), $"twincat_deploy has no {name}");
        Assert.False(tool.JsonSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0,
            "a deploy needs no argument");
    }

    [SkippableFact]
    public async Task ReleaseCheck_is_read_only_and_takes_no_argument()
    {
        SkipIfNotReachable();

        var tool = (await _client!.ListToolsAsync()).Single(t => t.Name == "twincat_release_check");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint ?? false);
        Assert.False(tool.JsonSchema.TryGetProperty("properties", out var props) && props.EnumerateObject().Any(),
            "the release check takes no argument");
    }

    [SkippableFact]
    public async Task Deploy_refuses_invalid_arguments_before_anything_runs()
    {
        SkipIfNotReachable();

        Assert.StartsWith("[Error] Unknown loginMode 'sometimes'", await CallAsync("twincat_deploy", new() { ["loginMode"] = "sometimes" }));
        Assert.StartsWith("[Error] lifeSign takes at most 8 conditions", await CallAsync("twincat_deploy",
            new() { ["lifeSign"] = Enumerable.Range(1, 9).Select(i => $"MAIN.n{i} > 1").ToArray() }));
        Assert.StartsWith("[Error] lifeSignTimeoutMs must be 1..300000", await CallAsync("twincat_deploy",
            new() { ["lifeSign"] = new[] { "MAIN.n > 1" }, ["lifeSignTimeoutMs"] = 300_001 }));
        Assert.StartsWith("[Error] plcProjectName names one PLC project", await CallAsync("twincat_deploy",
            new() { ["plcProjectName"] = "PLC", ["buildSolution"] = true }));
    }
}
