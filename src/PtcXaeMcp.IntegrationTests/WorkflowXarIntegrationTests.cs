using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — twincat_deploy against the live PLC runtime. It re-activates the fixture on the runtime (the runtime
/// restarts), so it only runs with <c>PTC_XAE_MCP_LIVE_REACTIVATE=1</c> and skips cleanly otherwise or when the stage
/// has no runtime (<see cref="XarRuntimeFixture.SkipIfNotReady"/>).
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class WorkflowXarIntegrationTests : IAsyncLifetime
{
    private const string CycleCounter = FixtureLayout.GvlName + ".nCycleCount";

    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;

    public WorkflowXarIntegrationTests(XarRuntimeFixture xar) => _xar = xar;

    public async Task InitializeAsync()
    {
        if (!_xar.IsReady) return;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            });
            _client = await McpClient.CreateAsync(transport, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch { /* _client stays null; SkipIfNotReady gates the tests */ }
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady()
    {
        _xar.SkipIfNotReady();
        if (_client is null) throw new SkipException("Workflow: the MCP client could not be created for Stage XAR.");
        Skip.IfNot(Environment.GetEnvironmentVariable("PTC_XAE_MCP_LIVE_REACTIVATE") == "1",
            "twincat_deploy re-activates the fixture and restarts the runtime; set PTC_XAE_MCP_LIVE_REACTIVATE=1 to run this test");
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, int timeoutSeconds = 900)
    {
        var result = await _client!.CallToolAsync(tool, args, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)).Token);
        string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[workflow] {tool}: {(text.Length > 900 ? text[..900] + "..." : text)}");
        return text;
    }

    private static JsonElement JsonOf(string text)
    {
        int start = text.StartsWith('{') ? 0 : text.IndexOf("\n{", StringComparison.Ordinal) + 1;
        return JsonDocument.Parse(text[start..]).RootElement.Clone();
    }

    private static int DeploysInActivityLog()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var health = JsonDocument.Parse(http.GetStringAsync(LiveXae.ServerUrl + "health").GetAwaiter().GetResult());
        string? path = health.RootElement.GetProperty("activityLog").GetString();
        if (path is null || !File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("\"tool\":\"twincat_deploy\"", StringComparison.Ordinal));
    }

    [Trait("Description", "deploy gegen die Runtime: Build, expectSymbols, Aktivierung mit Neustart, System und PLC lesen Run, lifeSign (Zykluszähler ändert sich); Schritt-Tabelle vollständig und ok, Zykluszähler läuft danach.")]
    [SkippableFact(Timeout = 1_500_000)]
    public async Task Deploy_BuildsActivatesAndProvesTheLifeSign()
    {
        SkipIfNotReady();

        string text = await CallAsync("twincat_deploy", new()
        {
            ["expectSymbols"] = new[] { FixtureLayout.UintSymbol, CycleCounter },
            ["lifeSign"] = new[] { $"{CycleCounter} changed" },
            ["lifeSignTimeoutMs"] = 30_000,
            ["requestKey"] = Guid.NewGuid().ToString(),
        });

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        var json = JsonOf(text);
        Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(_xar.AmsNetId, json.GetProperty("amsNetId").GetString());
        var steps = json.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(
            ["build", "expect_symbols", "target", "activate", "verify_target", "plc_state", "life_sign"],
            steps.Select(s => s.GetProperty("step").GetString()).ToArray());
        Assert.All(steps, s => Assert.True(s.GetProperty("ok").GetBoolean(), s.ToString()));

        string wait = await CallAsync("twincat_ads_wait_until", new()
        {
            ["conditions"] = new[] { $"{CycleCounter} changed" }, ["timeoutMs"] = 5000, ["amsNetId"] = _xar.AmsNetId,
        }, 30);
        Assert.True(JsonOf(wait).GetProperty("met").GetBoolean(), "the cycle counter counts after the deploy");
    }

    [Trait("Description", "deploy zweimal mit demselben requestKey: aktiviert genau einmal (Activity-Log zählt den Aufruf einmal, die Wiederholung antwortet in < 5 s aus dem Beleg), receipt_get zeigt outcome ok für twincat_deploy.")]
    [SkippableFact(Timeout = 1_500_000)]
    public async Task Deploy_TwiceWithTheSameKey_RunsOnce()
    {
        SkipIfNotReady();
        string key = Guid.NewGuid().ToString();
        var args = new Dictionary<string, object?> { ["requestKey"] = key };

        int before = DeploysInActivityLog();
        string first = await CallAsync("twincat_deploy", args);
        Assert.False(first.StartsWith("[Error]", StringComparison.Ordinal), first);
        Assert.Equal(before + 1, DeploysInActivityLog());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string again = await CallAsync("twincat_deploy", args, 120);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the repeat took {watch.Elapsed.TotalSeconds:F1} s: it ran again");
        Assert.Contains("[Receipt] Repeated request", again);
        Assert.Equal(before + 1, DeploysInActivityLog());

        using var receipt = JsonDocument.Parse(await CallAsync("twincat_receipt_get", new() { ["requestKey"] = key }, 30));
        Assert.Equal("ok", receipt.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("twincat_deploy", receipt.RootElement.GetProperty("tool").GetString());
    }

    [Trait("Description", "deploy mit einer Bedingung, die nie wahr wird: bricht im Schritt life_sign ab (nach der Aktivierung), die Antwort trägt kein 'Nothing was done' und der Beleg steht auf error.")]
    [SkippableFact(Timeout = 1_500_000)]
    public async Task Deploy_ALifeSignThatNeverHolds_FailsTheRun_AfterTheActivation()
    {
        SkipIfNotReady();
        string key = Guid.NewGuid().ToString();

        string text = await CallAsync("twincat_deploy", new()
        {
            ["lifeSign"] = new[] { $"{FixtureLayout.UintSymbol} == 65535" }, ["lifeSignTimeoutMs"] = 3000, ["requestKey"] = key,
        });

        Assert.StartsWith("[Error] twincat_deploy stopped at step 'life_sign'", text);
        Assert.DoesNotContain("Nothing was done", text);
        using var receipt = JsonDocument.Parse(await CallAsync("twincat_receipt_get", new() { ["requestKey"] = key }, 30));
        Assert.Equal("error", receipt.RootElement.GetProperty("outcome").GetString());
    }
}
