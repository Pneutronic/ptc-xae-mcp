using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_release_check and the abort rules of twincat_deploy that end before anything is activated, live against the
/// neutral fixture. Nothing here reaches a runtime: the deploy tests stop at the build or the symbol check (the fixture
/// has no target, so the activation would reach the local kernel runtime, which these tests never allow). The run that
/// activates is in <see cref="WorkflowXarIntegrationTests"/>.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class WorkflowIntegrationTests : IAsyncLifetime
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

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, int timeoutSeconds = 600)
    {
        var result = await _client!.CallToolAsync(tool, args, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)).Token);
        string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[workflow] {tool}: {(text.Length > 700 ? text[..700] + "..." : text)}");
        return text;
    }

    private async Task OpenFixtureAsync()
    {
        string open = await CallAsync("twincat_open_project", new() { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true }, 300);
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    private static JsonElement JsonOf(string text)
    {
        int start = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        return JsonDocument.Parse(text[start..]).RootElement.Clone();
    }

    private static Dictionary<string, DateTime> ProjectFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\_CompileInfo\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\.git\", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);

    [Trait("Description", "release_check am Fixture: acht Ampeln, nichts davon grün, was nur ein Compile zeigen kann (check_all_objects unbekannt), Projektdateien unverändert.")]
    [SkippableFact(Timeout = 900_000)]
    public async Task ReleaseCheck_OnTheFixture_AnswersEveryItem_AndChangesNoFile()
    {
        LiveXae.GuardReady(_ready);
        await OpenFixtureAsync();
        // A build first, so that the build item has something to compare: the check itself never builds.
        string build = await CallAsync("twincat_build_plc", new());
        Assert.Contains("Build succeeded", build);

        string fixtureDir = Path.GetDirectoryName(_fixtureSln)!;
        var before = ProjectFiles(fixtureDir);

        string text = await CallAsync("twincat_release_check", new(), 120);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        var json = JsonOf(text);
        Assert.True(json.GetProperty("success").GetBoolean());
        var items = json.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetString()!, i => i.GetProperty("status").GetString()!);
        Assert.Equal(
            ["build", "check_all_objects", "static_analysis", "libraries", "licenses", "boot_project", "target_runtime", "xae_dialogs"],
            items.Keys.ToArray());
        Assert.All(items.Values, s => Assert.Contains(s, new[] { "green", "yellow", "red", "unknown" }));
        Assert.Equal("unknown", items["check_all_objects"]);
        Assert.NotEqual("green", json.GetProperty("overall").GetString());
        Assert.NotEqual("red", items["xae_dialogs"]);
        Assert.Equal(before, ProjectFiles(fixtureDir));
    }

    [Trait("Description", "deploy mit einem Symbol, das es nicht gibt: bricht nach dem Build ab (expect_symbols), aktiviert nichts, Schritt-Tabelle ohne activate, der requestKey bleibt frei (receipt_get: kein Beleg).")]
    [SkippableFact(Timeout = 900_000)]
    public async Task Deploy_AMissingExpectedSymbol_StopsBeforeTheActivation_AndLeavesTheKeyFree()
    {
        LiveXae.GuardReady(_ready);
        await OpenFixtureAsync();
        string key = Guid.NewGuid().ToString();
        string missing = "MAIN.fbDoesNotExist_" + Guid.NewGuid().ToString("N")[..6];

        string text = await CallAsync("twincat_deploy", new()
        {
            ["expectSymbols"] = new[] { missing }, ["requestKey"] = key,
        });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'expect_symbols'", text);
        Assert.Contains(missing, text);
        var json = JsonOf(text);
        var steps = json.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("step").GetString()).ToArray();
        Assert.Equal(["build", "expect_symbols"], steps);
        Assert.True(json.GetProperty("steps")[0].GetProperty("ok").GetBoolean(), "the build itself passed");
        Assert.False(json.GetProperty("steps")[1].GetProperty("ok").GetBoolean());
        Assert.Contains("activate", json.GetProperty("notRun").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("Nothing was done", text);

        // A refusal that did nothing frees the key: there is no receipt for it.
        string receipt = await CallAsync("twincat_receipt_get", new() { ["requestKey"] = key }, 30);
        Assert.StartsWith("[Error] No receipt", receipt);
    }

    [Trait("Description", "deploy mit unbekanntem loginMode und mit zu vielen lifeSign-Bedingungen: abgelehnt, bevor gebaut wird.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Deploy_InvalidArguments_AreRefusedBeforeAnythingRuns()
    {
        LiveXae.GuardReady(_ready);
        await OpenFixtureAsync();

        string login = await CallAsync("twincat_deploy", new() { ["loginMode"] = "sometimes" }, 60);
        Assert.StartsWith("[Error] Unknown loginMode 'sometimes'", login);

        string tooMany = await CallAsync("twincat_deploy", new()
        {
            ["lifeSign"] = Enumerable.Range(0, 9).Select(i => $"GVL_Fixture.nCount == {i}").ToArray(),
        }, 60);
        Assert.StartsWith("[Error] lifeSign takes at most 8 conditions", tooMany);
    }
}
