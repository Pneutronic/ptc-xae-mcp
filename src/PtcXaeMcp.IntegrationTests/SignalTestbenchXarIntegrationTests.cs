using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — the signal testbench tools (ADS notifications) against a live PLC runtime. Preconditions as for
/// <see cref="PersistentSnapshotXarIntegrationTests"/>: the neutral fixture activated on the stage's explicit runtime
/// target and in Run, MCP server on port 5010. Every call passes the target as <c>amsNetId</c> (issue #195).
///
/// The fixture has no dedicated toggling symbol, so the tests use what MAIN already does:
/// <c>GVL_Fixture.nCycleCount</c> counts once per task cycle, and while <c>GVL_Fixture.bEnable</c> is TRUE the step
/// chain of <c>MAIN.fbStepChain</c> loops: its TON <c>tonWait.IN</c> is high for the 100 ms preset (rounded up to a
/// cycle) and low for four cycles. The tests leave bEnable FALSE.
///
/// The task is configured at 10 ms, and the runtime's own time stamps (timeBase "plc") show exactly that. A user-mode
/// runtime need not keep pace with the wall clock, though: on 2026-11-16 UmRT_AI took about 95 ms of wall time per
/// 10 ms cycle. So statistics from PLC time stamps are checked against the configured cycle, while everything counted
/// against the wall clock (how many changes or pulses fit into a recording, how long a wait takes) uses the wall time
/// per cycle measured first from two plain reads of the counter (<see cref="MeasureCycleMsAsync"/>).
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class SignalTestbenchXarIntegrationTests : IAsyncLifetime
{
    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;
    private string _snapshotFile = string.Empty;

    private const string CycleCounter = FixtureLayout.GvlName + ".nCycleCount";
    private const string Constant = FixtureLayout.UintSymbol;
    private const string Enable = FixtureLayout.BoolSymbol;
    private const string TonIn = FixtureLayout.StepChainInstanceSymbol + ".tonWait.IN";
    private const string Step = FixtureLayout.StepChainStepMember;
    private const string NeverSet = FixtureLayout.IbnProbeGvlName + ".bFault";

    /// <summary>The fixture's PlcTask cycle in PLC time (PlcTask.TcTTO).</summary>
    private const double PlcCycleMs = 10.0;

    public SignalTestbenchXarIntegrationTests(XarRuntimeFixture xar) => _xar = xar;

    public async Task InitializeAsync()
    {
        _snapshotFile = Path.Combine(Path.GetTempPath(), $"ptc-signal-live-{Guid.NewGuid():N}.json");
        if (!_xar.IsReady) return;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch { /* _client stays null; SkipIfNotReady gates tests */ }
    }

    public async Task DisposeAsync()
    {
        try { File.Delete(_snapshotFile); } catch (IOException) { }
        if (_client is not null)
        {
            await SetEnableAsync(false);
            await _client.DisposeAsync();
        }
    }

    private void SkipIfNotReady()
    {
        _xar.SkipIfNotReady();
        if (_client is null) throw new SkipException("signal testbench: MCP client could not be created for Stage XAR.");
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args, int timeoutSeconds = 120)
    {
        args["amsNetId"] = _xar.AmsNetId;
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)).Token);
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[signal-testbench] {tool}: {(text.Length > 900 ? text[..900] + "…" : text)}");
        return text;
    }

    private static JsonDocument Json(string text)
    {
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        return JsonDocument.Parse(text);
    }

    private async Task SetEnableAsync(bool on)
    {
        try
        {
            await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = Enable, ["value"] = on ? "true" : "false" });
        }
        catch (Exception ex) when (ex is McpException or OperationCanceledException or HttpRequestException)
        {
            // Best effort on teardown: the next run activates the fixture again anyway.
        }
    }

    private async Task<long> ReadCounterAsync()
    {
        using var doc = Json(await CallAsync("twincat_read_ads_variable", new() { ["symbolPath"] = CycleCounter }));
        return doc.RootElement.GetProperty("value").GetInt64();
    }

    /// <summary>Wall-clock ms per task cycle, from two plain reads of the cycle counter about 1.5 s apart.</summary>
    private async Task<double> MeasureCycleMsAsync()
    {
        long first = await ReadCounterAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(1500);
        long last = await ReadCounterAsync();
        sw.Stop();
        Assert.True(last - first >= 3, $"the cycle counter barely moves ({first} -> {last} in {sw.ElapsedMilliseconds} ms)");
        double cycle = sw.Elapsed.TotalMilliseconds / (last - first);
        Console.WriteLine($"[signal-testbench] measured task cycle: {cycle:F1} ms ({last - first} counts in {sw.ElapsedMilliseconds} ms)");
        return cycle;
    }

    [Trait("Description", "record misst die Zykluszeit der Fixture-Task am Zykluszähler per ADS-Notification: Änderungsintervall passt zum vorher per Lesen gemessenen Zyklus, kein Zählschritt geht verloren.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Record_CycleCounter_MeasuresTheTaskCycle()
    {
        SkipIfNotReady();
        double cycle = await MeasureCycleMsAsync();
        using var doc = Json(await CallAsync("twincat_ads_record",
            new() { ["symbols"] = new[] { CycleCounter }, ["durationMs"] = 2000, ["writeCsv"] = false }));
        var root = doc.RootElement;
        var s = root.GetProperty("symbols")[0];

        Assert.Equal("completed", root.GetProperty("endedBy").GetString());
        Assert.Equal("plc", root.GetProperty("timeBase").GetString());
        int changes = s.GetProperty("changes").GetInt32();
        double interval = s.GetProperty("changeIntervalMs").GetProperty("avg").GetDouble();
        long counted = s.GetProperty("last").GetInt64() - s.GetProperty("first").GetInt64();

        // About 2 s of wall time / wall ms per cycle counts, one change each; the runtime's own time stamps give the
        // interval in PLC time, which is the configured cycle however fast the runtime runs.
        double expected = 2000 / cycle;
        Assert.InRange(changes, (int)(expected * 0.7), (int)Math.Ceiling(expected * 1.3) + 1);
        Assert.InRange(interval, PlcCycleMs * 0.9, PlcCycleMs * 1.1);
        // Nearly every count arrives as its own change. Over ADS-over-MQTT to UmRT_AI a notification now and then carried
        // two counts at once (21 changes for 22 counts, 2026-11-16), so up to 10 % may merge; more would be lost data.
        Assert.InRange(counted - changes, 0, Math.Max(1, counted / 10));
        // The window runs from the first to the last delivered sample, so it can miss up to a few wall cycles at either end.
        Assert.InRange(root.GetProperty("windowMs").GetDouble(), 2000 - 3 * cycle - 100, 2300);
    }

    [Trait("Description", "record an tonWait.IN der laufenden Schrittkette: Pulsbreite = 100-ms-Preset (auf Zyklen gerundet), Low = 4 Zyklen, Periode = Summe, CSV mit allen Änderungen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Record_StepChainTimer_MeasuresPulseWidthAndPeriod_AndWritesTheCsv()
    {
        SkipIfNotReady();
        double cycle = await MeasureCycleMsAsync();
        await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = Enable, ["value"] = "true" });
        try
        {
            string csv = Path.Combine(Path.GetTempPath(), $"ptc-signal-live-{Guid.NewGuid():N}.csv");
            using var doc = Json(await CallAsync("twincat_ads_record",
                new() { ["symbols"] = new[] { TonIn, Step }, ["durationMs"] = 3000, ["csvPath"] = csv }));
            var root = doc.RootElement;
            var ton = root.GetProperty("symbols")[0];

            Assert.Equal("plc", root.GetProperty("timeBase").GetString());
            double high = ton.GetProperty("highMs").GetProperty("avg").GetDouble();
            double low = ton.GetProperty("lowMs").GetProperty("avg").GetDouble();
            double period = ton.GetProperty("periodMs").GetProperty("avg").GetDouble();
            // In PLC time: PT 100 ms, rounded up to whole 10 ms cycles (plus the cycle that starts it); low for four cycles.
            Assert.InRange(high, 100, 120);
            Assert.InRange(low, 30, 50);
            Assert.InRange(period, 130, 170);
            Assert.Equal(high + low, period, 1.0);
            Assert.InRange(ton.GetProperty("frequencyHz").GetDouble(), 5.9, 7.7);
            // In wall time a period lasts period / PlcCycleMs cycles of `cycle` ms each.
            double periodWallMs = period / PlcCycleMs * cycle;
            int expectedPulses = (int)(3000 / periodWallMs);
            Assert.True(ton.GetProperty("rising").GetInt32() >= Math.Max(1, expectedPulses - 1),
                $"expected about {expectedPulses} pulses in 3 s at {cycle:F1} ms wall time per cycle");

            // The step counter walks 0 → 10 → 1010 → 1020 → 0 → …: four distinct values.
            var step = root.GetProperty("symbols")[1];
            Assert.Equal(0, step.GetProperty("min").GetDouble());
            Assert.Equal(1020, step.GetProperty("max").GetDouble());

            string path = root.GetProperty("csv").GetProperty("path").GetString()!;
            Assert.Equal(Path.GetFullPath(csv), path);
            var rows = File.ReadAllLines(path);
            Assert.Equal("t_ms;symbol;value;name", rows[0]);
            Assert.Equal(root.GetProperty("csv").GetProperty("rows").GetInt32() + 1, rows.Length);
            Assert.Contains(rows, r => r.Contains(";" + TonIn + ";true;", StringComparison.Ordinal));
            File.Delete(path);
        }
        finally
        {
            await SetEnableAsync(false);
        }
    }

    [Trait("Description", "wait_until auf den Zykluszähler: erfüllt nach etwa der erwarteten Zeit; Zeitüberschreitung ist kein Fehler; changed wird beim ersten Zählschritt wahr.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task WaitUntil_CycleCounter_IsMetAfterTheExpectedTime_AndTimesOutOtherwise()
    {
        SkipIfNotReady();
        double cycle = await MeasureCycleMsAsync();
        int counts = Math.Max(5, (int)Math.Round(1500 / cycle)); // about 1.5 s of cycles
        long now = await ReadCounterAsync();

        using (var met = Json(await CallAsync("twincat_ads_wait_until",
                   new() { ["conditions"] = new[] { $"{CycleCounter} > {(now + counts).ToString(CultureInfo.InvariantCulture)}" }, ["timeoutMs"] = 10_000 })))
        {
            Assert.True(met.RootElement.GetProperty("met").GetBoolean());
            double elapsed = met.RootElement.GetProperty("elapsedMs").GetDouble();
            Assert.InRange(elapsed, counts * cycle * 0.5, counts * cycle * 1.5 + 500); // the counts plus the call overhead
            Assert.True(met.RootElement.GetProperty("values").GetProperty(CycleCounter).GetInt64() > now + counts);
        }

        using (var changed = Json(await CallAsync("twincat_ads_wait_until",
                   new() { ["conditions"] = new[] { $"{CycleCounter} changed" }, ["timeoutMs"] = 2000 })))
        {
            Assert.True(changed.RootElement.GetProperty("met").GetBoolean());
            Assert.InRange(changed.RootElement.GetProperty("elapsedMs").GetDouble(), 0, 3 * cycle + 500);
        }

        using var timedOut = Json(await CallAsync("twincat_ads_wait_until",
            new() { ["conditions"] = new[] { $"{NeverSet} == true" }, ["timeoutMs"] = 400 }));
        Assert.False(timedOut.RootElement.GetProperty("met").GetBoolean());
        Assert.True(timedOut.RootElement.GetProperty("timedOut").GetBoolean());
        Assert.InRange(timedOut.RootElement.GetProperty("elapsedMs").GetDouble(), 399, 400.1);
    }

    [Trait("Description", "wait_until fängt den Start der Schrittkette: bEnable per write_ads_variable setzen, dann Step == 1010 abwarten (Zustand durchläuft ~100 ms).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task WaitUntil_TheStepChain_ReachesItsWorkStep_AfterTheTimerPreset()
    {
        SkipIfNotReady();
        await CallAsync("twincat_write_ads_variable", new() { ["symbolPath"] = Enable, ["value"] = "true" });
        try
        {
            using var doc = Json(await CallAsync("twincat_ads_wait_until",
                new() { ["conditions"] = new[] { $"{Step} == 1010" }, ["timeoutMs"] = 5000 }));
            Assert.True(doc.RootElement.GetProperty("met").GetBoolean());
        }
        finally
        {
            await SetEnableAsync(false);
        }
    }

    [Trait("Description", "expect: stable hält bei einer Konstanten und schlägt beim Zähler sofort mit Symbol und Werten an; never besteht beim nie gesetzten Merker; within erreicht den Zählerstand.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Expect_Stable_Never_Within_OnTheFixture()
    {
        SkipIfNotReady();
        double cycle = await MeasureCycleMsAsync();

        using (var stable = Json(await CallAsync("twincat_ads_expect",
                   new() { ["mode"] = "stable", ["durationMs"] = 1500, ["symbols"] = new[] { Constant } })))
        {
            Assert.True(stable.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(1500, stable.RootElement.GetProperty("elapsedMs").GetDouble(), 1.0);
        }

        using (var unstable = Json(await CallAsync("twincat_ads_expect",
                   new() { ["mode"] = "stable", ["durationMs"] = 2000, ["symbols"] = new[] { CycleCounter } })))
        {
            Assert.False(unstable.RootElement.GetProperty("passed").GetBoolean());
            var v = unstable.RootElement.GetProperty("firstViolation");
            Assert.Equal(CycleCounter, v.GetProperty("symbol").GetString(), ignoreCase: true);
            Assert.InRange(v.GetProperty("atMs").GetDouble(), 0, 3 * cycle + 500);
            // Usually one count; a count can fall between reading the start value and the first notification.
            Assert.InRange(v.GetProperty("to").GetInt64() - v.GetProperty("from").GetInt64(), 1, 3);
        }

        using (var never = Json(await CallAsync("twincat_ads_expect",
                   new() { ["mode"] = "never", ["durationMs"] = 1000, ["conditions"] = new[] { $"{NeverSet} == true" } })))
            Assert.True(never.RootElement.GetProperty("passed").GetBoolean());

        using (var neverFails = Json(await CallAsync("twincat_ads_expect",
                   new() { ["mode"] = "never", ["durationMs"] = 2000, ["conditions"] = new[] { $"{CycleCounter} changed" } })))
        {
            Assert.False(neverFails.RootElement.GetProperty("passed").GetBoolean());
            Assert.InRange(neverFails.RootElement.GetProperty("firstViolation").GetProperty("atMs").GetDouble(), 0, 3 * cycle + 500);
        }

        int counts = Math.Max(5, (int)Math.Round(1000 / cycle)); // about 1 s of cycles
        long now = await ReadCounterAsync();
        using (var within = Json(await CallAsync("twincat_ads_expect",
                   new() { ["mode"] = "within", ["durationMs"] = 5000, ["conditions"] = new[] { $"{CycleCounter} > {(now + counts).ToString(CultureInfo.InvariantCulture)}" } })))
        {
            Assert.True(within.RootElement.GetProperty("passed").GetBoolean());
            Assert.InRange(within.RootElement.GetProperty("elapsedMs").GetDouble(), counts * cycle * 0.3, 5000);
        }

        using var tooEarly = Json(await CallAsync("twincat_ads_expect",
            new() { ["mode"] = "within", ["durationMs"] = 100, ["conditions"] = new[] { $"{CycleCounter} > {(now + 100_000).ToString(CultureInfo.InvariantCulture)}" } }));
        Assert.False(tooEarly.RootElement.GetProperty("passed").GetBoolean());
    }

    [Trait("Description", "Fehlerfälle gegen die echte Runtime: unbekanntes Symbol, Struct statt Skalar und eine Bedingung, die zum Typ nicht passt, werden vor dem Warten abgelehnt.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task BadSymbolsAndConditions_AreRefusedBeforeWaiting()
    {
        SkipIfNotReady();

        string missing = await CallAsync("twincat_ads_wait_until",
            new() { ["conditions"] = new[] { "GVL_Fixture.nDoesNotExist > 1" }, ["timeoutMs"] = 5000 });
        Assert.StartsWith("[Error]", missing);
        Assert.Contains("nDoesNotExist", missing);
        Assert.Contains(_xar.AmsNetId, missing);

        string aggregate = await CallAsync("twincat_ads_record",
            new() { ["symbols"] = new[] { "MAIN.stRecord" }, ["durationMs"] = 100, ["writeCsv"] = false });
        Assert.StartsWith("[Error]", aggregate);
        Assert.Contains("not a scalar", aggregate);

        string wrongType = await CallAsync("twincat_ads_wait_until",
            new() { ["conditions"] = new[] { $"{Enable} > 1" }, ["timeoutMs"] = 5000 });
        Assert.StartsWith("[Error]", wrongType);
        Assert.Contains("only == and !=", wrongType);
    }

    [Trait("Description", "Eine Aufnahme über mehrere Symbole verschiedener Typen (BOOL, UINT, UDINT, LREAL, STRING, input-gemapptes BOOL) läuft durch und liefert je Symbol Statistik.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Record_ManyTypes_IncludingAnInputMappedBool_AndAString()
    {
        SkipIfNotReady();
        double cycle = await MeasureCycleMsAsync();
        using var doc = Json(await CallAsync("twincat_ads_record", new()
        {
            ["symbols"] = new[] { FixtureLayout.BoolSymbol, Constant, CycleCounter, FixtureLayout.LrealSymbol,
                                  FixtureLayout.StringSymbol, FixtureLayout.InputMappedBoolSymbol },
            ["durationMs"] = 1000, ["writeCsv"] = false,
        }));
        var symbols = doc.RootElement.GetProperty("symbols").EnumerateArray().ToList();
        Assert.Equal(6, symbols.Count);
        var byName = symbols.ToDictionary(s => s.GetProperty("symbol").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, byName[Constant].GetProperty("changes").GetInt32());
        Assert.True(byName[FixtureLayout.LrealSymbol].GetProperty("changes").GetInt32() >= (int)(1000 / cycle * 0.5),
            $"fValue grows every cycle ({cycle:F1} ms)");
        Assert.Equal("fixture", byName[FixtureLayout.StringSymbol].GetProperty("last").GetString());
        Assert.Equal("BOOL", byName[FixtureLayout.InputMappedBoolSymbol].GetProperty("plcType").GetString());
    }

    [Trait("Description", "snapshot_compare: Capture der GVL_Fixture (inkl. input-gemapptem BOOL) per savePath, danach Vergleich meldet genau die sich ändernden Symbole; Toleranz; Inline-Baseline.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task SnapshotCompare_CapturesAndComparesArbitrarySymbols()
    {
        SkipIfNotReady();
        string[] symbols = [FixtureLayout.BoolSymbol, FixtureLayout.InputMappedBoolSymbol, Constant, CycleCounter, FixtureLayout.LrealSymbol];

        using (var saved = Json(await CallAsync("twincat_ads_snapshot_compare",
                   new() { ["symbols"] = symbols, ["savePath"] = _snapshotFile })))
        {
            Assert.Equal(symbols.Length, saved.RootElement.GetProperty("leafCount").GetInt32());
            Assert.Equal(Path.GetFullPath(_snapshotFile), saved.RootElement.GetProperty("saved").GetProperty("path").GetString());
        }

        await Task.Delay(300); // the program keeps counting
        using (var diff = Json(await CallAsync("twincat_ads_snapshot_compare",
                   new() { ["symbols"] = symbols, ["baselinePath"] = _snapshotFile })))
        {
            var changed = diff.RootElement.GetProperty("differences").EnumerateArray()
                .Select(d => d.GetProperty("symbol").GetString()!).Order(StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(new[] { CycleCounter, FixtureLayout.LrealSymbol }.Order(StringComparer.OrdinalIgnoreCase).ToList(), changed);
            Assert.False(diff.RootElement.GetProperty("equal").GetBoolean());
            Assert.Equal(3, diff.RootElement.GetProperty("equalCount").GetInt32());
        }

        // The baseline's own leaves are read when no symbols are given; a wide tolerance accepts the drift.
        using (var tolerant = Json(await CallAsync("twincat_ads_snapshot_compare",
                   new() { ["baselinePath"] = _snapshotFile, ["tolerance"] = 1_000_000 })))
            Assert.True(tolerant.RootElement.GetProperty("equal").GetBoolean());

        using var inline = Json(await CallAsync("twincat_ads_snapshot_compare", new()
        {
            ["symbols"] = new[] { Constant, FixtureLayout.StringSymbol },
            ["baseline"] = $"{{\"{Constant}\": {FixtureLayout.UintSymbolValue}, \"{FixtureLayout.StringSymbol}\": \"fixture\"}}",
        }));
        Assert.True(inline.RootElement.GetProperty("equal").GetBoolean());
    }
}
