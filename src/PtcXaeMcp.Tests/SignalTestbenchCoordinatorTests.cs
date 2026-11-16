using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>signal testbench: argument validation, the hard limits and the JSON the four tools answer, on a fake session.</summary>
public class SignalTestbenchCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptc-signal-tests-" + Guid.NewGuid().ToString("N"));

    public SignalTestbenchCoordinatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static WatchedSymbol B(string name, bool v = false) => ScriptedAdsWatch.Sym(name, "BOOL", ScriptedAdsWatch.Bool(v));
    private static WatchedSymbol N(string name, long v = 0) => ScriptedAdsWatch.Sym(name, "UDINT", ScriptedAdsWatch.Num(v));

    private static (FakeTwinCatSession Session, SignalTestbenchCoordinator Coordinator) With(ScriptedAdsWatch? watch,
        Func<Func<string>, string>? runLocked = null, Func<DateTime>? clock = null)
    {
        var session = new FakeTwinCatSession { FakeAdsWatch = watch };
        return (session, new SignalTestbenchCoordinator(session, runLocked, clock));
    }

    private static JsonElement Ok(string text)
    {
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static bool Opened(FakeTwinCatSession s) => s.CallLog.Any(c => c.StartsWith("OpenAdsWatch", StringComparison.Ordinal));

    // ---- the tools in the catalog ----

    [Theory]
    [InlineData("twincat_ads_wait_until")]
    [InlineData("twincat_ads_expect")]
    [InlineData("twincat_ads_record")]
    [InlineData("twincat_ads_snapshot_compare")]
    public void TheTools_AreReadOnlyAdsTools_ThatTheSystemServiceCheckCovers(string name)
    {
        var tool = PtcXaeMcp.WinForms.Mcp.ToolCatalog.Find(name);
        Assert.NotNull(tool);
        Assert.Equal(PtcXaeMcp.WinForms.Mcp.Toolset.Ads, tool.Toolset);
        Assert.Equal(PtcXaeMcp.WinForms.Mcp.AccessLevel.Read, tool.Level);
        Assert.True(tool.ReadOnly);
        Assert.Contains(name, TwinCatSystemService.AdsTools);
    }

    // ---- wait_until ----

    [Fact]
    public void WaitUntil_Answers_MetElapsedAndTheValues()
    {
        var watch = new ScriptedAdsWatch(N("MAIN.nCount"), B("GVL.bDone")).Change(120, 0, ScriptedAdsWatch.Num(9));
        var (session, c) = With(watch);

        var json = Ok(c.WaitUntil(["MAIN.nCount > 5", "GVL.bDone == false"], null, 2000, "5.6.7.8.1.1", 852, default));

        Assert.True(json.GetProperty("met").GetBoolean());
        Assert.False(json.GetProperty("timedOut").GetBoolean());
        Assert.Equal(120, json.GetProperty("elapsedMs").GetDouble(), 3);
        Assert.Equal("all", json.GetProperty("combine").GetString());
        Assert.Equal(9, json.GetProperty("values").GetProperty("MAIN.nCount").GetInt32());
        Assert.False(json.GetProperty("values").GetProperty("GVL.bDone").GetBoolean());
        Assert.All(json.GetProperty("conditions").EnumerateArray(), e => Assert.True(e.GetProperty("met").GetBoolean()));
        Assert.Equal(852, json.GetProperty("adsPort").GetInt32());
        Assert.Contains("OpenAdsWatch(n=2, syms=[MAIN.nCount,GVL.bDone], cycle=1, net=5.6.7.8.1.1, port=852)", session.CallLog);
        Assert.True(watch.Disposed);
    }

    [Fact]
    public void WaitUntil_ATimeout_IsNotAnError()
    {
        var watch = new ScriptedAdsWatch(N("MAIN.nCount"));
        var (_, c) = With(watch);
        var json = Ok(c.WaitUntil(["MAIN.nCount > 5"], null, 300, null, null, default));
        Assert.False(json.GetProperty("met").GetBoolean());
        Assert.True(json.GetProperty("timedOut").GetBoolean());
        Assert.Equal(300, json.GetProperty("elapsedMs").GetDouble(), 3);
        Assert.False(json.GetProperty("conditions")[0].GetProperty("met").GetBoolean());
    }

    [Fact]
    public void WaitUntil_AnEnumValue_CarriesItsMemberName()
    {
        var mode = ScriptedAdsWatch.Sym("MAIN.eMode", "E_Mode", ScriptedAdsWatch.EnumValue(1, "Run"), wire: "INT", isEnum: true);
        var (_, c) = With(new ScriptedAdsWatch(mode));
        var json = Ok(c.WaitUntil(["MAIN.eMode == Run"], null, 100, null, null, default));
        var v = json.GetProperty("values").GetProperty("MAIN.eMode");
        Assert.Equal("Run", v.GetProperty("name").GetString());
        Assert.Equal(1, v.GetProperty("value").GetInt32());
    }

    [Fact]
    public void WaitUntil_RefusesTooManyConditions_BeforeAnythingIsOpened()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        var nine = Enumerable.Range(1, SignalLimits.MaxConditions + 1).Select(i => $"S{i} == 1").ToList();
        string text = c.WaitUntil(nine, null, 100, null, null, default);
        Assert.StartsWith("[Error] At most 8 conditions", text);
        Assert.False(Opened(session));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(300_001)]
    public void WaitUntil_TheTimeoutLimit_IsFixedInCode(int timeoutMs)
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        string text = c.WaitUntil(["A > 1"], null, timeoutMs, null, null, default);
        Assert.StartsWith("[Error] timeoutMs must be 1..300000", text);
        Assert.Contains("fixed limit", text);
        Assert.False(Opened(session));
    }

    [Fact]
    public void WaitUntil_TheLongestTimeout_IsAccepted()
    {
        var (_, c) = With(new ScriptedAdsWatch(N("A")));
        var json = Ok(c.WaitUntil(["A > 1"], null, 300_000, null, null, default));
        Assert.True(json.GetProperty("timedOut").GetBoolean());
    }

    [Fact]
    public void WaitUntil_RefusesAnUnknownCombine_AndAMalformedCondition()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        Assert.StartsWith("[Error] combine must be", c.WaitUntil(["A > 1"], "xor", 100, null, null, default));
        Assert.StartsWith("[Error] Condition 'A'", c.WaitUntil(["A"], null, 100, null, null, default));
        Assert.False(Opened(session));
    }

    [Fact]
    public void WaitUntil_ACombineOfAny_IsAccepted_AndReported()
    {
        var watch = new ScriptedAdsWatch(N("A"), N("B")).Change(50, 1, ScriptedAdsWatch.Num(7));
        var (_, c) = With(watch);
        var json = Ok(c.WaitUntil(["A > 5", "B > 5"], "any", 500, null, null, default));
        Assert.True(json.GetProperty("met").GetBoolean());
        Assert.Equal("any", json.GetProperty("combine").GetString());
    }

    [Fact]
    public void WaitUntil_AMissingSymbol_IsAnErrorNamingTheTarget()
    {
        var (_, c) = With(null);
        string text = c.WaitUntil(["MAIN.nNope > 1"], null, 100, "5.6.7.8.1.1", null, default);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("'MAIN.nNope' is not in the PLC symbol table", text);
        Assert.Contains("1.2.3.4.1.1", text); // the target the fake session reports
    }

    [Fact]
    public void WaitUntil_AConditionThatDoesNotFitTheType_IsRefusedBeforeWaiting()
    {
        var watch = new ScriptedAdsWatch(B("A"));
        var (_, c) = With(watch);
        string text = c.WaitUntil(["A > 3"], null, 100, null, null, default);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("only == and !=", text);
        Assert.True(watch.Disposed);
        Assert.Equal(TimeSpan.Zero, watch.Elapsed);
    }

    [Fact]
    public void WaitUntil_ARuntimeThatStops_IsAnError()
    {
        var watch = new ScriptedAdsWatch(N("A")) { FaultAt = TimeSpan.FromMilliseconds(100) };
        var (_, c) = With(watch);
        string text = c.WaitUntil(["A > 3"], null, 1000, null, null, default);
        Assert.StartsWith("[Error] The notifications stopped after 100 ms", text);
        Assert.Contains("left Run", text);
    }

    [Fact]
    public void LongCalls_HoldTheLockOnlyWhileRegistering()
    {
        bool inLock = false;
        int locked = 0;
        var watch = new LockProbeWatch(new ScriptedAdsWatch(N("A")), () => inLock);
        var (session, c) = With(null, body => { locked++; inLock = true; try { return body(); } finally { inLock = false; } });
        session.FakeAdsWatch = watch;

        var json = Ok(c.WaitUntil(["A > 3"], null, 200, null, null, default));

        Assert.True(json.GetProperty("timedOut").GetBoolean());
        Assert.True(watch.Takes > 0, "the wait never read the watch");
        Assert.Equal(0, watch.TakesInsideTheLock);
        Assert.Equal(1, locked); // only the registration (no solution path is asked for here)
    }

    private sealed class LockProbeWatch(ScriptedAdsWatch inner, Func<bool> inLock) : IAdsWatch
    {
        public int Takes { get; private set; }
        public int TakesInsideTheLock { get; private set; }
        public IReadOnlyList<WatchedSymbol> Symbols => inner.Symbols;
        public TimeSpan Elapsed => inner.Elapsed;
        public string? Fault => inner.Fault;
        public void Dispose() => inner.Dispose();

        public bool TryTake(TimeSpan timeout, out SignalSample sample)
        {
            Takes++;
            if (inLock()) TakesInsideTheLock++;
            return inner.TryTake(timeout, out sample);
        }
    }

    // ---- expect ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("always")]
    public void Expect_NeedsAKnownMode(string? mode)
    {
        var (_, c) = With(new ScriptedAdsWatch(N("A")));
        Assert.StartsWith("[Error] mode must be", c.Expect(mode, 100, ["A > 1"], null, null, null, null, default));
    }

    [Fact]
    public void Expect_NeedsADuration_WithinTheFixedLimit()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        Assert.StartsWith("[Error] durationMs is required", c.Expect("within", null, ["A > 1"], null, null, null, null, default));
        Assert.StartsWith("[Error] durationMs must be 1..300000", c.Expect("within", 300_001, ["A > 1"], null, null, null, null, default));
        Assert.StartsWith("[Error] durationMs must be 1..300000", c.Expect("never", 0, ["A > 1"], null, null, null, null, default));
        Assert.False(Opened(session));
    }

    [Fact]
    public void Expect_StableTakesSymbols_TheOthersTakeConditions()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        Assert.StartsWith("[Error] mode 'stable' takes symbols", c.Expect("stable", 100, ["A > 1"], null, null, null, null, default));
        Assert.StartsWith("[Error] mode 'within' takes conditions", c.Expect("within", 100, null, ["A"], null, null, null, default));
        Assert.StartsWith("[Error] symbols is required", c.Expect("stable", 100, null, null, null, null, null, default));
        Assert.False(Opened(session));
    }

    [Fact]
    public void Expect_StableWithMoreThanEightSymbols_IsRefused()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        var nine = Enumerable.Range(1, SignalLimits.MaxWatchSymbols + 1).Select(i => $"S{i}").ToList();
        Assert.StartsWith("[Error] At most 8 symbols", c.Expect("stable", 100, null, nine, null, null, null, default));
        Assert.False(Opened(session));
    }

    [Fact]
    public void Expect_StablePasses_AConstant()
    {
        var watch = new ScriptedAdsWatch(N("K", 42));
        var (_, c) = With(watch);
        var json = Ok(c.Expect("stable", 2000, null, ["K"], null, null, null, default));
        Assert.True(json.GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("firstViolation").ValueKind);
        Assert.Equal(2000, json.GetProperty("elapsedMs").GetDouble(), 3);
        Assert.Equal(42, json.GetProperty("values").GetProperty("K").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("conditions").ValueKind);
    }

    [Fact]
    public void Expect_StableFails_WithTheFirstViolation()
    {
        var watch = new ScriptedAdsWatch(N("K", 42)).Change(700, 0, ScriptedAdsWatch.Num(43));
        var (_, c) = With(watch);
        var json = Ok(c.Expect("stable", 2000, null, ["K"], null, null, null, default));
        Assert.False(json.GetProperty("passed").GetBoolean());
        var v = json.GetProperty("firstViolation");
        Assert.Equal("K", v.GetProperty("symbol").GetString());
        Assert.Equal(700, v.GetProperty("atMs").GetDouble(), 3);
        Assert.Equal(42, v.GetProperty("from").GetInt32());
        Assert.Equal(43, v.GetProperty("to").GetInt32());
    }

    [Fact]
    public void Expect_NeverPasses_AFlagThatIsNeverSet()
    {
        var (_, c) = With(new ScriptedAdsWatch(B("GVL.bFault")));
        var json = Ok(c.Expect("never", 1500, ["GVL.bFault == true"], null, null, null, null, default));
        Assert.True(json.GetProperty("passed").GetBoolean());
        Assert.Equal("never", json.GetProperty("mode").GetString());
        Assert.False(json.GetProperty("conditions")[0].GetProperty("met").GetBoolean());
    }

    [Fact]
    public void Expect_WithinFails_WithTheReason()
    {
        var (_, c) = With(new ScriptedAdsWatch(N("A")));
        var json = Ok(c.Expect("within", 400, ["A > 1"], null, null, null, null, default));
        Assert.False(json.GetProperty("passed").GetBoolean());
        Assert.Contains("did not hold within 400 ms", json.GetProperty("firstViolation").GetProperty("reason").GetString());
    }

    // ---- record ----

    [Fact]
    public void Record_StatisticsAndACsvFile()
    {
        var watch = new ScriptedAdsWatch(B("MAIN.bBlink"), N("GVL.nCycleCount"));
        for (int i = 0; i < 4; i++)
        {
            watch.Change(10 + i * 140, 0, ScriptedAdsWatch.Bool(true));
            watch.Change(110 + i * 140, 0, ScriptedAdsWatch.Bool(false));
        }
        string csv = Path.Combine(_dir, "rec.csv");
        var (session, c) = With(watch);

        var json = Ok(c.Record(["MAIN.bBlink", "GVL.nCycleCount"], 2000, 5, csv, true, null, null, default));

        Assert.Equal("completed", json.GetProperty("endedBy").GetString());
        Assert.Equal("plc", json.GetProperty("timeBase").GetString());
        Assert.Equal(8, json.GetProperty("changes").GetInt32());
        var blink = json.GetProperty("symbols")[0];
        Assert.Equal("MAIN.bBlink", blink.GetProperty("symbol").GetString());
        Assert.Equal(4, blink.GetProperty("rising").GetInt32());
        Assert.Equal(100, blink.GetProperty("highMs").GetProperty("avg").GetDouble(), 3);
        Assert.Equal(140, blink.GetProperty("periodMs").GetProperty("avg").GetDouble(), 3);
        Assert.Equal(7.143, blink.GetProperty("frequencyHz").GetDouble(), 3);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("symbols")[1].GetProperty("rising").ValueKind);
        Assert.Contains("OpenAdsWatch(n=2, syms=[MAIN.bBlink,GVL.nCycleCount], cycle=5", string.Join("\n", session.CallLog));

        Assert.Equal(Path.GetFullPath(csv), json.GetProperty("csv").GetProperty("path").GetString());
        string[] rows = File.ReadAllLines(csv);
        Assert.Equal(SignalCsv.Header, rows[0]);
        Assert.Equal(1 + 2 + 8, rows.Length); // header, two opening values, eight changes
        Assert.True(watch.Disposed);
    }

    [Fact]
    public void Record_WithoutCsv_WritesNoFile()
    {
        var (_, c) = With(new ScriptedAdsWatch(N("A")));
        var json = Ok(c.Record(["A"], 100, null, null, writeCsv: false, null, null, default));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("csv").ValueKind);
    }

    [Fact]
    public void Record_TheDefaultCsv_GoesUnderTempAndNamesTheTarget()
    {
        var (_, c) = With(new ScriptedAdsWatch(N("A")), clock: () => new DateTime(2026, 11, 16, 8, 30, 15, DateTimeKind.Utc));
        var json = Ok(c.Record(["A"], 100, null, null, true, null, null, default));
        string path = json.GetProperty("csv").GetProperty("path").GetString()!;
        try
        {
            Assert.Equal(Path.GetFullPath(SignalTestbenchCoordinator.DefaultCsvDirectory), Path.GetDirectoryName(path));
            Assert.Equal("20261116T083015Z_1.2.3.4.1.1.csv", Path.GetFileName(path));
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120_001)]
    public void Record_TheDurationLimit_IsFixedInCode(int durationMs)
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        string text = c.Record(["A"], durationMs, null, null, false, null, null, default);
        Assert.StartsWith("[Error] durationMs must be 1..120000", text);
        Assert.False(Opened(session));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Record_TheCycleLimit_IsFixedInCode(int cycleMs)
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        Assert.StartsWith("[Error] cycleMs must be 1..1000", c.Record(["A"], 100, cycleMs, null, false, null, null, default));
        Assert.False(Opened(session));
    }

    [Fact]
    public void Record_AtMost32Symbols()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        var many = Enumerable.Range(1, 33).Select(i => $"S{i}").ToList();
        Assert.StartsWith("[Error] At most 32 symbols", c.Record(many, 100, null, null, false, null, null, default));
        Assert.False(Opened(session));
        var thirtyTwo = many.Take(32).ToList();
        var (_, ok) = With(new ScriptedAdsWatch(Enumerable.Range(1, 32).Select(i => N($"S{i}")).ToArray()));
        Ok(ok.Record(thirtyTwo, 100, null, null, false, null, null, default));
    }

    [Fact]
    public void Record_DuplicateSymbols_AreWatchedOnce()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        Ok(c.Record(["A", " a ", "A"], 100, null, null, false, null, null, default));
        Assert.Contains("OpenAdsWatch(n=1, syms=[A]", string.Join("\n", session.CallLog));
    }

    [Fact]
    public void Record_ACsvOutsideTheAllowedDirectories_IsRefused_BeforeRecording()
    {
        var (session, c) = With(new ScriptedAdsWatch(N("A")));
        string outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "ptc-signal-forbidden", "x.csv");
        string text = c.Record(["A"], 100, null, outside, true, null, null, default);
        Assert.StartsWith("[Error] csvPath refused", text);
        Assert.False(Opened(session));
        Assert.StartsWith("[Error] csvPath must end in .csv", c.Record(["A"], 100, null, Path.Combine(_dir, "x.txt"), true, null, null, default));
    }

    [Fact]
    public void Record_TheChangeLimit_EndsTheRecordingAndSaysSo()
    {
        var watch = new ScriptedAdsWatch(N("A"));
        for (int i = 1; i <= SignalLimits.MaxRecordEvents + 50; i++) watch.Change(i / 100.0, 0, ScriptedAdsWatch.Num(i));
        var (_, c) = With(watch);
        var json = Ok(c.Record(["A"], 60_000, null, null, false, null, null, default));
        Assert.Equal("changeLimit", json.GetProperty("endedBy").GetString());
        Assert.Equal(SignalLimits.MaxRecordEvents, json.GetProperty("changes").GetInt32());
    }

    [Fact]
    public void Record_ARuntimeThatStops_KeepsTheStatistics_AndNamesTheFault()
    {
        var watch = new ScriptedAdsWatch(N("A")) { FaultAt = TimeSpan.FromMilliseconds(500) };
        watch.Change(100, 0, ScriptedAdsWatch.Num(1));
        var (_, c) = With(watch);
        var json = Ok(c.Record(["A"], 2000, null, null, false, null, null, default));
        Assert.Equal("fault", json.GetProperty("endedBy").GetString());
        Assert.Contains("left Run", json.GetProperty("fault").GetString());
        Assert.Equal(1, json.GetProperty("changes").GetInt32());
    }

    [Fact]
    public void PruneOldRecordings_RemovesOnlyOldCsvFiles()
    {
        string oldCsv = Path.Combine(_dir, "old.csv");
        string newCsv = Path.Combine(_dir, "new.csv");
        string other = Path.Combine(_dir, "old.txt");
        foreach (string f in new[] { oldCsv, newCsv, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(oldCsv, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-8));

        SignalTestbenchCoordinator.PruneOldRecordings(_dir, DateTime.UtcNow);

        Assert.False(File.Exists(oldCsv));
        Assert.True(File.Exists(newCsv));
        Assert.True(File.Exists(other));
    }

    // ---- snapshot_compare ----

    private static SnapshotLeaf Leaf(string type, string json, string? name = null) =>
        new(type, FakeTwinCatSession.Json(json), name);

    private static FakeTwinCatSession Plc(params (string Path, SnapshotLeaf Leaf)[] leaves)
    {
        var s = new FakeTwinCatSession();
        foreach (var (p, l) in leaves) s.PlcLeaves[p] = l;
        return s;
    }

    [Fact]
    public void SnapshotCompare_NeedsABaselineOrASavePath()
    {
        var c = new SignalTestbenchCoordinator(Plc());
        Assert.StartsWith("[Error] Nothing to do", c.SnapshotCompare(["A"], null, null, null, null, null, null));
        Assert.StartsWith("[Error] Give baselinePath or baseline, not both", c.SnapshotCompare(["A"], "x.json", "{}", null, null, null, null));
        Assert.StartsWith("[Error] tolerance must be", c.SnapshotCompare(["A"], null, "{\"A\":1}", null, -1, null, null));
    }

    [Fact]
    public void SnapshotCompare_AnInlineBaseline_ReportsTheDifferences_AndReadsInputsToo()
    {
        var session = Plc(
            ("GVL.nMode", Leaf("UINT", "2")),
            ("GVL.bReady", Leaf("BOOL", "true")),
            ("GVL.fGain", Leaf("REAL", "1.5")),
            ("GVL.sName", Leaf("STRING", "\"abc\"")));
        var c = new SignalTestbenchCoordinator(session);

        var json = Ok(c.SnapshotCompare(["GVL.nMode", "GVL.bReady", "GVL.fGain", "GVL.sName"], null,
            "{\"GVL.nMode\": 2, \"GVL.bReady\": false, \"gvl.fgain\": 1.5, \"GVL.sName\": \"abd\"}", null, null, null, null));

        Assert.False(json.GetProperty("equal").GetBoolean());
        Assert.Equal(2, json.GetProperty("equalCount").GetInt32());
        Assert.Equal(2, json.GetProperty("differenceCount").GetInt32());
        var diffs = json.GetProperty("differences").EnumerateArray().ToDictionary(d => d.GetProperty("symbol").GetString()!);
        Assert.False(diffs["GVL.bReady"].GetProperty("baseline").GetBoolean());
        Assert.True(diffs["GVL.bReady"].GetProperty("live").GetBoolean());
        Assert.Equal("abd", diffs["GVL.sName"].GetProperty("baseline").GetString());
        Assert.Contains("observe=True", string.Join("\n", session.CallLog));
    }

    [Fact]
    public void SnapshotCompare_Equal_WhenEverythingMatches_AndTheSymbolsComeFromTheBaseline()
    {
        var session = Plc(("GVL.nMode", Leaf("UINT", "2")), ("GVL.bReady", Leaf("BOOL", "true")));
        var c = new SignalTestbenchCoordinator(session);
        var json = Ok(c.SnapshotCompare(null, null, "{\"GVL.nMode\": 2, \"GVL.bReady\": true}", null, null, null, null));
        Assert.True(json.GetProperty("equal").GetBoolean());
        Assert.Contains("paths=[GVL.nMode,GVL.bReady]", string.Join("\n", session.CallLog));
    }

    [Fact]
    public void SnapshotCompare_ABaselineSymbolThatIsNotOnThePlc_IsNotEqual()
    {
        var session = Plc(("GVL.nMode", Leaf("UINT", "2")));
        var c = new SignalTestbenchCoordinator(session);
        var json = Ok(c.SnapshotCompare(["GVL.nMode", "GVL.nGone"], null, "{\"GVL.nMode\": 2, \"GVL.nGone\": 1}", null, null, null, null));
        Assert.False(json.GetProperty("equal").GetBoolean());
        Assert.Equal("GVL.nGone", json.GetProperty("missingOnPlc")[0].GetString());
        Assert.Equal("GVL.nGone", json.GetProperty("onlyInBaseline")[0].GetString());
    }

    [Fact]
    public void SnapshotCompare_TheToleranceAppliesToNumbersOnly()
    {
        var session = Plc(("GVL.fA", Leaf("LREAL", "10.04")), ("GVL.fB", Leaf("LREAL", "10.5")), ("GVL.sC", Leaf("STRING", "\"x\"")));
        var c = new SignalTestbenchCoordinator(session);
        var json = Ok(c.SnapshotCompare(["GVL.fA", "GVL.fB", "GVL.sC"], null, "{\"GVL.fA\": 10.0, \"GVL.fB\": 10.0, \"GVL.sC\": \"y\"}", null, 0.1, null, null));
        Assert.Equal(1, json.GetProperty("withinToleranceCount").GetInt32());
        Assert.Equal(2, json.GetProperty("differenceCount").GetInt32());
        Assert.Equal(["GVL.fB", "GVL.sC"],
            json.GetProperty("differences").EnumerateArray().Select(d => d.GetProperty("symbol").GetString()).Order().ToArray());
    }

    [Fact]
    public void SnapshotCompare_SavesACapture_AndComparesAgainstItLater()
    {
        var session = Plc(("GVL.st.nA", Leaf("DINT", "5")), ("GVL.st.bB", Leaf("BOOL", "false")), ("GVL.nOther", Leaf("DINT", "9")));
        var c = new SignalTestbenchCoordinator(session, clock: () => new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));
        string file = Path.Combine(_dir, "base.json");

        var saved = Ok(c.SnapshotCompare(["GVL.st"], null, null, file, null, null, null));
        Assert.Equal(2, saved.GetProperty("leafCount").GetInt32());
        Assert.Equal(Path.GetFullPath(file), saved.GetProperty("saved").GetProperty("path").GetString());
        Assert.True(File.Exists(file));

        session.PlcLeaves["GVL.st.nA"] = Leaf("DINT", "6");
        var diff = Ok(c.SnapshotCompare(["GVL.st"], file, null, null, null, null, null));
        Assert.False(diff.GetProperty("equal").GetBoolean());
        Assert.Equal("GVL.st.nA", diff.GetProperty("differences")[0].GetProperty("symbol").GetString());
        Assert.Equal(5, diff.GetProperty("differences")[0].GetProperty("baseline").GetInt32());
        Assert.Equal(6, diff.GetProperty("differences")[0].GetProperty("live").GetInt32());

        // Without symbols, the file's own leaves are read; the unrelated GVL.nOther is not in the comparison.
        var all = Ok(c.SnapshotCompare(null, file, null, null, null, null, null));
        Assert.Equal(1, all.GetProperty("differenceCount").GetInt32());
        Assert.Equal(0, all.GetProperty("notInBaselineCount").GetInt32());
    }

    [Fact]
    public void SnapshotCompare_ABaselineFile_IsCheckedAgainstThePathPolicy_AndItsFormat()
    {
        var c = new SignalTestbenchCoordinator(Plc());
        string outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "ptc-signal-forbidden", "x.json");
        Assert.StartsWith("[Error] baselinePath refused", c.SnapshotCompare(["A"], outside, null, null, null, null, null));
        Assert.StartsWith("[Error] baselinePath", c.SnapshotCompare(["A"], Path.Combine(_dir, "missing.json"), null, null, null, null, null));

        string bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{\"hello\": 1}");
        Assert.Contains("not a 'ptc-persistent-snapshot/1' file", c.SnapshotCompare(["A"], bad, null, null, null, null, null));
        Assert.StartsWith("[Error] savePath refused", c.SnapshotCompare(["A"], null, null, outside, null, null, null));
        Assert.StartsWith("[Error] savePath must end in .json", c.SnapshotCompare(["A"], null, null, Path.Combine(_dir, "x.txt"), null, null, null));
    }

    [Fact]
    public void SnapshotCompare_RefusesMoreThan256Symbols_AndABadInlineBaseline()
    {
        var c = new SignalTestbenchCoordinator(Plc());
        var many = Enumerable.Range(1, SignalLimits.MaxSnapshotSymbols + 1).Select(i => $"S{i}").ToList();
        Assert.StartsWith("[Error] At most 256 symbols", c.SnapshotCompare(many, null, "{\"S1\":1}", null, null, null, null));
        Assert.StartsWith("[Error] baseline must be a JSON object", c.SnapshotCompare(["A"], null, "[1]", null, null, null, null));
        Assert.StartsWith("[Error] baseline is not valid JSON", c.SnapshotCompare(["A"], null, "{", null, null, null, null));
        Assert.Contains("must be a number, a boolean or a string", c.SnapshotCompare(["A"], null, "{\"A\": [1]}", null, null, null, null));
    }

    [Fact]
    public void SnapshotCompare_AnEnumValueFromTheReadTools_IsTakenAsItsNumber()
    {
        var session = Plc(("GVL.eMode", Leaf("E_Mode", "1", "Run")));
        var c = new SignalTestbenchCoordinator(session);
        var json = Ok(c.SnapshotCompare(["GVL.eMode"], null, "{\"GVL.eMode\": {\"name\": \"Run\", \"value\": 1}}", null, null, null, null));
        Assert.True(json.GetProperty("equal").GetBoolean());
    }

    [Fact]
    public void SnapshotCompare_ASessionFailure_IsAnErrorNamingTheTarget()
    {
        var session = Plc();
        session.SnapshotReadError = "[Error] SymbolLoader failed: DeviceNotFound";
        var c = new SignalTestbenchCoordinator(session);
        string text = c.SnapshotCompare(["A"], null, "{\"A\":1}", null, null, "5.6.7.8.1.1", null);
        Assert.StartsWith("[Error] SymbolLoader failed", text);
    }
}
