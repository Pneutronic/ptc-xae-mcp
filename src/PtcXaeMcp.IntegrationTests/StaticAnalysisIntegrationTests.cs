using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_plc_static_analysis, live against the neutral fixture: the analysis runs, its findings come from SARIF
/// (TwinCAT >= 3.1.4026.20) or — with the reason — from the Error List, and the project files are not saved.
/// Which rules fire depends on the TE1200 license of the box, so the test checks the shape, not a rule count.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class StaticAnalysisIntegrationTests : IAsyncLifetime
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
                ConnectionTimeout = TimeSpan.FromSeconds(5),
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

    private static string GetText(CallToolResult result) =>
        string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));

    [Trait("Description", "Static Analysis über das Fixture → Ergebnis aus SARIF oder (mit Grund) aus der Error List, Format Zähler/Regeln/Befunde, Projektdateien nicht gespeichert.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task StaticAnalysis_OnTheFixture_ReportsFromSarifOrSaysWhyNot()
    {
        LiveXae.GuardReady(_ready);

        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client);

        string fixtureDir = Path.GetDirectoryName(_fixtureSln)!;
        var before = ProjectFiles(fixtureDir);

        var result = await _client.CallToolAsync("twincat_plc_static_analysis",
            new Dictionary<string, object?> { ["minLevel"] = "note", ["limit"] = 20 });
        string text = GetText(result);
        Console.WriteLine($"[static_analysis] {text}");

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        Assert.StartsWith("Static analysis (check all objects): ", text);
        Assert.True(text.Contains("read from SARIF ", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("the Error List (no SARIF: ", StringComparison.Ordinal), text);

        Assert.Equal(before, ProjectFiles(fixtureDir));
    }

    private static Dictionary<string, DateTime> ProjectFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".TcPOU", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);
}
