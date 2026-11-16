using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — the symbol lists of the access policy and the request receipts against the live PLC runtime. The symbol
/// test needs a host started with <c>PTC_XAE_MCP_ADS_WRITE_ALLOW=GVL_Fixture.bEnable*</c> and skips otherwise. The
/// activation test re-activates the fixture on the runtime (the runtime restarts), so it only runs with
/// <c>PTC_XAE_MCP_LIVE_REACTIVATE=1</c>.
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class SafetyXarIntegrationTests : IAsyncLifetime
{
    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;

    public SafetyXarIntegrationTests(XarRuntimeFixture xar) => _xar = xar;

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
        if (_client is null) throw new SkipException("Safety: the MCP client could not be created for Stage XAR.");
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, int timeoutSeconds = 60)
    {
        var result = await _client!.CallToolAsync(tool, args, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)).Token);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[safety] {tool}: {(text.Length > 400 ? text[..400] + "..." : text)}");
        return text;
    }

    private Task<string> WriteAsync(string symbol, string value, string? requestKey = null) =>
        CallAsync("twincat_write_ads_variable", new()
        {
            ["symbolPath"] = symbol, ["value"] = value, ["amsNetId"] = _xar.AmsNetId, ["requestKey"] = requestKey,
        });

    private async Task<string> ReadAsync(string symbol)
    {
        string text = await CallAsync("twincat_read_ads_variable", new() { ["symbolPath"] = symbol, ["amsNetId"] = _xar.AmsNetId });
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("value").GetRawText().Trim('"').ToLowerInvariant();
    }

    [Trait("Description", "Symbol-Allowlist (Host mit PTC_XAE_MCP_ADS_WRITE_ALLOW=GVL_Fixture.bEnable*): Write auf bEnable geht, auf nCount wird abgelehnt und der Wert bleibt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task AdsWriteAllow_OnlyTheListedSymbolIsWritten()
    {
        SkipIfNotReady();
        string status = await CallAsync("twincat_get_status", new());
        Skip.IfNot(status.Contains("variable writes only to: GVL_Fixture.bEnable*", StringComparison.Ordinal),
            "the host was not started with PTC_XAE_MCP_ADS_WRITE_ALLOW=GVL_Fixture.bEnable*");

        string original = await ReadAsync(FixtureLayout.BoolSymbol);
        string flipped = original == "true" ? "false" : "true";
        string countBefore = await ReadAsync(FixtureLayout.UintSymbol);
        try
        {
            string allowed = await WriteAsync(FixtureLayout.BoolSymbol, flipped);
            Assert.DoesNotContain("[Error]", allowed);
            Assert.Equal(flipped, await ReadAsync(FixtureLayout.BoolSymbol));

            string refused = await WriteAsync(FixtureLayout.UintSymbol, "77");
            Assert.StartsWith("[Error] Access policy: twincat_write_ads_variable may write only GVL_Fixture.bEnable*", refused);
            Assert.Contains(FixtureLayout.UintSymbol, refused);
            Assert.Equal(countBefore, await ReadAsync(FixtureLayout.UintSymbol));
        }
        finally
        {
            await WriteAsync(FixtureLayout.BoolSymbol, original);
        }
    }

    [Trait("Description", "requestKey: derselbe Schlüssel mit denselben Argumenten schreibt nur einmal; die Wiederholung liefert den Beleg, ein anderer Wert mit dem Schlüssel wird abgelehnt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task RequestKey_ARepeatedWriteIsNotRunAgain()
    {
        SkipIfNotReady();
        string original = await ReadAsync(FixtureLayout.BoolSymbol);
        string mine = original == "true" ? "false" : "true";
        string key = Guid.NewGuid().ToString();
        try
        {
            string first = await WriteAsync(FixtureLayout.BoolSymbol, mine, key);
            Assert.DoesNotContain("[Error]", first);
            Assert.Equal(mine, await ReadAsync(FixtureLayout.BoolSymbol));

            // Somebody else sets the old value; a second execution of the keyed call would write 'mine' again.
            Assert.DoesNotContain("[Error]", await CallAsync("twincat_write_ads_variable", new()
            {
                ["symbolPath"] = FixtureLayout.BoolSymbol, ["value"] = original, ["amsNetId"] = _xar.AmsNetId,
            }));

            string again = await WriteAsync(FixtureLayout.BoolSymbol, mine, key);
            Assert.StartsWith(first.Split('\n')[0], again);
            Assert.Contains("[Receipt] Repeated request", again);
            Assert.Equal(original, await ReadAsync(FixtureLayout.BoolSymbol));

            string other = await WriteAsync(FixtureLayout.BoolSymbol, original, key);
            Assert.StartsWith("[Error] requestKey '" + key + "' belongs to another request", other);

            using var receipt = JsonDocument.Parse(await CallAsync("twincat_receipt_get", new() { ["requestKey"] = key }));
            Assert.Equal("ok", receipt.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("twincat_write_ads_variable", receipt.RootElement.GetProperty("tool").GetString());
            Assert.Contains("[Error] No receipt", await CallAsync("twincat_receipt_get", new() { ["requestKey"] = Guid.NewGuid().ToString() }));
        }
        finally
        {
            await CallAsync("twincat_write_ads_variable", new()
            {
                ["symbolPath"] = FixtureLayout.BoolSymbol, ["value"] = original, ["amsNetId"] = _xar.AmsNetId,
            });
        }
    }

    [Trait("Description", "requestKey: twincat_activate_config zweimal mit demselben Schlüssel gegen die Runtime aktiviert genau einmal (Activity-Log zählt den Aufruf einmal; die Wiederholung antwortet in < 5 s aus dem Beleg).")]
    [SkippableFact(Timeout = 900_000)]
    public async Task RequestKey_ActivateConfigTwice_ActivatesOnce()
    {
        SkipIfNotReady();
        Skip.IfNot(Environment.GetEnvironmentVariable("PTC_XAE_MCP_LIVE_REACTIVATE") == "1",
            "re-activating the fixture restarts the runtime; set PTC_XAE_MCP_LIVE_REACTIVATE=1 to run this test");
        string key = Guid.NewGuid().ToString();
        var args = new Dictionary<string, object?> { ["autoConfirmRestart"] = true, ["requestKey"] = key };

        int before = ActivationsInActivityLog();
        string first = await CallAsync("twincat_activate_config", args, timeoutSeconds: 600);
        Assert.DoesNotContain("[Error]", first);
        Assert.Equal(before + 1, ActivationsInActivityLog());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string again = await CallAsync("twincat_activate_config", args, timeoutSeconds: 120);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"the repeat took {watch.Elapsed.TotalSeconds:F1} s: it ran again");
        Assert.StartsWith(first.Split('\n')[0], again);
        Assert.Contains("[Receipt] Repeated request", again);
        Assert.Equal(before + 1, ActivationsInActivityLog());
        Console.WriteLine($"[safety] activate_config entries in the activity log: before {before}, after the first call {before + 1}, after the repeat {ActivationsInActivityLog()}; repeat answered in {watch.Elapsed.TotalMilliseconds:F0} ms");

        using var receipt = JsonDocument.Parse(await CallAsync("twincat_receipt_get", new() { ["requestKey"] = key }));
        Assert.Equal("ok", receipt.RootElement.GetProperty("outcome").GetString());
        Assert.True(await PlcReadsAsync(), "the PLC runs again after the activation");
    }

    private async Task<bool> PlcReadsAsync()
    {
        var (up, _) = await RuntimeReadiness.WaitForRuntimeAsync(_client!, _xar.AmsNetId, TimeSpan.FromSeconds(90));
        return up;
    }

    // The host's current activity log (named by /health) holds one line per traced tool body run: '"tool":"<name>"'.
    private static int ActivationsInActivityLog()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var health = JsonDocument.Parse(http.GetStringAsync(LiveXae.ServerUrl + "health").GetAwaiter().GetResult());
        string? path = health.RootElement.GetProperty("activityLog").GetString();
        if (path is null || !File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n').Count(l => l.Contains("\"tool\":\"twincat_activate_config\"", StringComparison.Ordinal));
    }
}
