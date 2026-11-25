using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Verifies twincat_get_active_project against a live XAE: open/query/close cycle.
/// Skips unless PTC_XAE_MCP_FIXTURE_SLN and REQUIRE_LIVE=1 are set.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class ActiveProjectIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    [Trait("Description", "Nach OpenProject: solutionOpen=true, Pfad endet auf Fixture-sln.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task GetActiveProject_AfterOpen_ReportsOpenAndMatchingPath()
    {
        SkipIfNotReady();

        await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });

        var result = await _client.CallToolAsync("twincat_get_active_project");
        var text = GetText(result);

        Assert.False(result.IsError == true, $"Tool returned error: {text}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("solutionOpen").GetBoolean(),
            $"Expected solutionOpen=true, got: {text}");

        var path = root.GetProperty("solutionPath").GetString();
        Assert.NotNull(path);
        Assert.True(
            path.EndsWith(Path.GetFileName(_fixtureSln), StringComparison.OrdinalIgnoreCase),
            $"solutionPath '{path}' does not end with fixture '{_fixtureSln}'");

        var name = root.GetProperty("solutionName").GetString();
        Assert.False(string.IsNullOrWhiteSpace(name), $"solutionName was empty in: {text}");

        // The active solution platform and the target are reported, and the fixture's pair fits.
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("targetPlatform").GetString()),
            $"targetPlatform was empty in: {text}");
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("targetNetId").GetString()),
            $"targetNetId was empty in: {text}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("platformWarning").ValueKind);
    }

    [Trait("Description", "Nach CloseProject: solutionOpen=false, path/name sind null.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task GetActiveProject_AfterClose_ReportsClosedAndNulls()
    {
        SkipIfNotReady();

        await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        await _client.CallToolAsync("twincat_close_project",
            new Dictionary<string, object?> { ["saveFirst"] = false });

        var result = await _client.CallToolAsync("twincat_get_active_project");
        var text = GetText(result);

        Assert.False(result.IsError == true, $"Tool returned error: {text}");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("solutionOpen").GetBoolean(),
            $"Expected solutionOpen=false after close, got: {text}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("solutionPath").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("solutionName").ValueKind);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
