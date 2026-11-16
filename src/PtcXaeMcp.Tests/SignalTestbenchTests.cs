using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// signal testbench: wait_until / expect / record decisions on a scripted watch with a virtual clock — no PLC, no
/// sleeping, so a window of minutes costs nothing and the outcome never depends on the machine.
/// </summary>
public class SignalTestbenchTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private static SignalConditionSet Set(ScriptedAdsWatch watch, bool all, params string[] conditions)
    {
        Assert.True(SignalConditionParser.TryParseAll(conditions, out var parsed, out string error), error);
        var set = SignalConditionSet.Bind(parsed, watch.Symbols, all, out error);
        Assert.True(set is not null, error);
        return set!;
    }

    private static ScriptedAdsWatch Watch(params WatchedSymbol[] symbols) => new(symbols);

    private static WatchedSymbol B(string name, bool v = false) => ScriptedAdsWatch.Sym(name, "BOOL", ScriptedAdsWatch.Bool(v));
    private static WatchedSymbol N(string name, long v = 0) => ScriptedAdsWatch.Sym(name, "UDINT", ScriptedAdsWatch.Num(v));

    // ---- wait_until ----

    [Fact]
    public void WaitUntil_ACondition_ThatAlreadyHolds_EndsAtOnce()
    {
        var watch = Watch(B("A", true));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "A == true"), Window, default);
        Assert.True(o.Passed);
        Assert.Equal(TimeSpan.Zero, o.Elapsed);
        Assert.Equal(TimeSpan.Zero, watch.Elapsed); // it did not wait at all
    }

    [Fact]
    public void WaitUntil_Meets_AtTheTimeOfTheChange()
    {
        var watch = Watch(N("C", 0)).Change(100, 0, ScriptedAdsWatch.Num(3)).Change(250, 0, ScriptedAdsWatch.Num(7));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), Window, default);
        Assert.True(o.Passed);
        Assert.False(o.TimedOut);
        Assert.Equal(250, o.Elapsed.TotalMilliseconds, 3);
        Assert.Equal(7, o.State.Current(0).Value.GetInt32());
    }

    [Fact]
    public void WaitUntil_TimesOut_WithTheLastValues()
    {
        var watch = Watch(N("C", 0)).Change(100, 0, ScriptedAdsWatch.Num(3));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), TimeSpan.FromMilliseconds(400), default);
        Assert.False(o.Passed);
        Assert.True(o.TimedOut);
        Assert.Equal(400, o.Elapsed.TotalMilliseconds, 3);
        Assert.Equal(3, o.State.Current(0).Value.GetInt32());
    }

    [Fact]
    public void WaitUntil_AChangeAfterTheDeadline_DoesNotCount()
    {
        var watch = Watch(N("C", 0)).Change(500, 0, ScriptedAdsWatch.Num(9));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), TimeSpan.FromMilliseconds(400), default);
        Assert.True(o.TimedOut);
    }

    [Fact]
    public void WaitUntil_CatchesAShortPulse_BetweenTwoPolls()
    {
        // 2 ms high between two changes: a poll every few ms would miss it, a notification does not.
        var watch = Watch(B("P")).Change(300, 0, ScriptedAdsWatch.Bool(true)).Change(302, 0, ScriptedAdsWatch.Bool(false));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "P == true"), Window, default);
        Assert.True(o.Passed);
        Assert.Equal(300, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void WaitUntil_Any_NeedsOneCondition_All_NeedsBoth()
    {
        var script = (ScriptedAdsWatch w) => w.Change(100, 0, ScriptedAdsWatch.Bool(true)).Change(300, 1, ScriptedAdsWatch.Num(9));

        var any = script(Watch(B("A"), N("C")));
        Assert.Equal(100, SignalTestbench.WaitUntil(any, Set(any, false, "A == true", "C > 5"), Window, default).Elapsed.TotalMilliseconds, 3);

        var all = script(Watch(B("A"), N("C")));
        Assert.Equal(300, SignalTestbench.WaitUntil(all, Set(all, true, "A == true", "C > 5"), Window, default).Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void WaitUntil_Changed_WaitsForAnyDifferentValue()
    {
        var watch = Watch(N("C", 5)).Change(80, 0, ScriptedAdsWatch.Num(5)).Change(120, 0, ScriptedAdsWatch.Num(6));
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C changed"), Window, default);
        Assert.True(o.Passed);
        Assert.Equal(120, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void WaitUntil_ARuntimeThatStops_IsAFaultNotATimeout()
    {
        var watch = Watch(N("C", 0));
        watch.FaultAt = TimeSpan.FromMilliseconds(200);
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), Window, default);
        Assert.False(o.Passed);
        Assert.False(o.TimedOut);
        Assert.Contains("left Run", o.Fault);
        Assert.Equal(200, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void WaitUntil_Cancels_BetweenTwoSlices()
    {
        var watch = Watch(N("C", 0));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), Window, cts.Token));
    }

    [Fact]
    public void WaitUntil_ALongTimeout_CostsNoRealTime()
    {
        var watch = Watch(N("C", 0));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = SignalTestbench.WaitUntil(watch, Set(watch, true, "C > 5"), TimeSpan.FromMilliseconds(SignalLimits.MaxWaitTimeoutMs), default);
        Assert.True(o.TimedOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(SignalLimits.MaxWaitTimeoutMs, watch.Elapsed.TotalMilliseconds, 3);
    }

    // ---- expect ----

    [Fact]
    public void Stable_Passes_WhenNothingChanges_AndRepeatedValuesAreNoChange()
    {
        var watch = Watch(N("K", 42), B("R", true)).Change(100, 0, ScriptedAdsWatch.Num(42)).Change(200, 1, ScriptedAdsWatch.Bool(true));
        var o = SignalTestbench.Stable(watch, Window, default);
        Assert.True(o.Passed);
        Assert.Null(o.Violation);
        Assert.Equal(1000, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void Stable_Fails_AtTheFirstChange_NamingSymbolAndValues()
    {
        var watch = Watch(N("K", 42), B("R", true)).Change(300, 1, ScriptedAdsWatch.Bool(false)).Change(400, 0, ScriptedAdsWatch.Num(1));
        var o = SignalTestbench.Stable(watch, Window, default);
        Assert.False(o.Passed);
        var v = o.Violation!;
        Assert.Equal("R", v.Symbol);
        Assert.Equal(300, v.At.TotalMilliseconds, 3);
        Assert.Equal(JsonValueKindOf(v.From!.Value), "True");
        Assert.Equal(JsonValueKindOf(v.To!.Value), "False");
        Assert.Equal(300, o.Elapsed.TotalMilliseconds, 3); // ended at once, did not run on to 1000
    }

    private static string JsonValueKindOf(SignalReading r) => r.Value.ValueKind.ToString();

    [Fact]
    public void Stable_ARuntimeThatStops_IsAFault_NotAPass()
    {
        var watch = Watch(N("K", 42));
        watch.FaultAt = TimeSpan.FromMilliseconds(500);
        var o = SignalTestbench.Stable(watch, Window, default);
        Assert.False(o.Passed);
        Assert.NotNull(o.Fault);
    }

    [Fact]
    public void Within_Passes_WhenTheConditionHoldsInsideTheWindow()
    {
        var watch = Watch(N("C")).Change(400, 0, ScriptedAdsWatch.Num(10));
        var o = SignalTestbench.Within(watch, Set(watch, true, "C >= 10"), Window, default);
        Assert.True(o.Passed);
        Assert.Null(o.Violation);
        Assert.Equal(400, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void Within_Fails_WhenItComesTooLate_WithAReason()
    {
        var watch = Watch(N("C")).Change(1500, 0, ScriptedAdsWatch.Num(10));
        var o = SignalTestbench.Within(watch, Set(watch, true, "C >= 10"), Window, default);
        Assert.False(o.Passed);
        Assert.False(o.Fault is not null);
        Assert.Contains("did not hold within 1000 ms", o.Violation!.Reason);
    }

    [Fact]
    public void Never_Passes_AWindowWithoutTheCondition()
    {
        var watch = Watch(B("F")).Change(100, 0, ScriptedAdsWatch.Bool(false)).Change(300, 0, ScriptedAdsWatch.Bool(false));
        var o = SignalTestbench.Never(watch, Set(watch, true, "F == true"), Window, default);
        Assert.True(o.Passed);
        Assert.Equal(1000, o.Elapsed.TotalMilliseconds, 3);
    }

    [Fact]
    public void Never_Fails_AtTheFirstTimeItHolds()
    {
        var watch = Watch(B("F")).Change(600, 0, ScriptedAdsWatch.Bool(true)).Change(700, 0, ScriptedAdsWatch.Bool(false));
        var o = SignalTestbench.Never(watch, Set(watch, true, "F == true"), Window, default);
        Assert.False(o.Passed);
        Assert.Equal(600, o.Violation!.At.TotalMilliseconds, 3);
    }

    [Fact]
    public void Never_FailsAtOnce_WhenTheOpeningStateIsTheCondition()
    {
        var watch = Watch(B("F", true));
        var o = SignalTestbench.Never(watch, Set(watch, true, "F == true"), Window, default);
        Assert.False(o.Passed);
        Assert.Equal(TimeSpan.Zero, o.Violation!.At);
        Assert.Contains("when the observation started", o.Violation.Reason);
    }

    // ---- record ----

    private static ScriptedAdsWatch Blinker(double highMs, double lowMs, int cycles)
    {
        var watch = Watch(B("LED"), N("Cnt"));
        double t = 50; // first rising edge at 50 ms
        for (int i = 0; i < cycles; i++)
        {
            watch.Change(t, 0, ScriptedAdsWatch.Bool(true));
            t += highMs;
            watch.Change(t, 0, ScriptedAdsWatch.Bool(false));
            t += lowMs;
        }
        return watch;
    }

    [Fact]
    public void Record_ABlinker_GivesPulseWidthsAndPeriod_FromTheRuntimeTimeStamps()
    {
        var watch = Blinker(highMs: 100, lowMs: 40, cycles: 5);
        var sum = SignalRecorder.Run(watch, TimeSpan.FromSeconds(1), 1000, default);
        var led = sum.Stats[0];

        Assert.Equal("plc", sum.TimeBase);
        Assert.Equal(10, led.Changes);
        Assert.Equal(5, led.Rising);
        Assert.Equal(5, led.Falling);
        Assert.Equal(5, led.HighMs!.Count);
        Assert.Equal(100, led.HighMs.Min, 3);
        Assert.Equal(100, led.HighMs.Max, 3);
        Assert.Equal(40, led.LowMs!.Avg, 3);          // 4 complete low pulses: the first low has no known start
        Assert.Equal(4, led.LowMs.Count);
        Assert.Equal(140, led.PeriodMs!.Avg, 3);
        Assert.Equal(4, led.PeriodMs.Count);
    }

    [Fact]
    public void Record_TheMean_IsTimeWeighted()
    {
        // LED high for 100 ms of a 1000 ms window → mean 0.1.
        var watch = Watch(B("LED")).Change(0.5, 0, ScriptedAdsWatch.Bool(true)).Change(100.5, 0, ScriptedAdsWatch.Bool(false));
        var sum = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(1000), 1000, default);
        Assert.Equal(0.1, sum.Stats[0].Mean!.Value, 2);
        Assert.Equal(0.0, sum.Stats[0].Min);
        Assert.Equal(1.0, sum.Stats[0].Max);
    }

    [Fact]
    public void Record_ACounter_GivesTheChangeInterval()
    {
        var watch = Watch(N("Cnt", 0));
        for (int i = 1; i <= 50; i++) watch.Change(i * 10, 0, ScriptedAdsWatch.Num(i)); // one count per 10 ms cycle
        var sum = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(600), 1000, default);
        var s = sum.Stats[0];
        Assert.Equal(50, s.Changes);
        Assert.Equal(10, s.ChangeIntervalMs!.Avg, 3);
        Assert.Equal(10, s.ChangeIntervalMs.Min, 3);
        Assert.Equal(10, s.ChangeIntervalMs.Max, 3);
        Assert.Equal(0.0, s.Min);
        Assert.Equal(50.0, s.Max);
        Assert.Equal(50, s.Last.Value.GetInt32());
        Assert.Null(s.HighMs); // not a BOOL
    }

    [Fact]
    public void Record_AStringSymbol_HasChangesButNoNumbers()
    {
        var watch = Watch(ScriptedAdsWatch.Sym("S", "STRING", ScriptedAdsWatch.Text("a"))).Change(10, 0, ScriptedAdsWatch.Text("b"));
        var s = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(100), 1000, default).Stats[0];
        Assert.Equal(1, s.Changes);
        Assert.Null(s.Min);
        Assert.Null(s.Mean);
    }

    [Fact]
    public void Record_StopsAtTheChangeLimit_AndSaysSo()
    {
        var watch = Watch(N("Cnt"));
        for (int i = 1; i <= 100; i++) watch.Change(i, 0, ScriptedAdsWatch.Num(i));
        var sum = SignalRecorder.Run(watch, TimeSpan.FromSeconds(1), maxChanges: 10, default);
        Assert.True(sum.LimitReached);
        Assert.Equal(10, sum.Stats[0].Changes);
    }

    [Fact]
    public void Record_AFault_KeepsWhatWasRecorded()
    {
        var watch = Watch(N("Cnt")).Change(10, 0, ScriptedAdsWatch.Num(1)).Change(20, 0, ScriptedAdsWatch.Num(2));
        watch.FaultAt = TimeSpan.FromMilliseconds(300);
        var sum = SignalRecorder.Run(watch, TimeSpan.FromSeconds(1), 1000, default);
        Assert.Contains("left Run", sum.Fault);
        Assert.Equal(2, sum.Stats[0].Changes);
    }

    [Fact]
    public void Record_WithoutRuntimeTimeStamps_FallsBackToTheHostClock()
    {
        var watch = new NoStampWatch(N("Cnt"));
        var sum = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(200), 1000, default);
        Assert.Equal("host", sum.TimeBase);
        Assert.Equal(20, sum.Stats[0].ChangeIntervalMs!.Avg, 3);
    }

    private sealed class NoStampWatch(WatchedSymbol symbol) : IAdsWatch
    {
        private TimeSpan _now;
        private int _n;
        public IReadOnlyList<WatchedSymbol> Symbols { get; } = [symbol];
        public TimeSpan Elapsed => _now;
        public string? Fault => null;
        public void Dispose() { }

        public bool TryTake(TimeSpan timeout, out SignalSample sample)
        {
            sample = default;
            TimeSpan next = TimeSpan.FromMilliseconds((_n + 1) * 20);
            if (_n < 5 && next <= _now + timeout)
            {
                _n++;
                _now = next;
                sample = new SignalSample(0, _now, 0, ScriptedAdsWatch.Num(_n));
                return true;
            }
            _now += timeout;
            return false;
        }
    }

    // ---- csv ----

    [Fact]
    public void Csv_HasOneRowPerChange_WithTheOpeningValuesAtZero()
    {
        // The first sample (an unchanged value) only starts the runtime clock; times are relative to it.
        var watch = Watch(B("A.b"), ScriptedAdsWatch.Sym("S;x", "STRING", ScriptedAdsWatch.Text("p\"q")))
            .Change(10, 1, ScriptedAdsWatch.Text("p\"q"))
            .Change(22.5, 0, ScriptedAdsWatch.Bool(true));
        var sum = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(100), 1000, default);
        var lines = SignalCsv.Build(watch.Symbols, sum.Changes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            [SignalCsv.Header, "0;A.b;false;", "0;\"S;x\";\"p\"\"q\";", "12.5;A.b;true;"],
            lines);
    }

    [Fact]
    public void Csv_AnEnumRow_CarriesItsMemberName()
    {
        var mode = ScriptedAdsWatch.Sym("E", "E_Mode", ScriptedAdsWatch.EnumValue(1, "Run"), wire: "INT", isEnum: true);
        var watch = Watch(mode).Change(1, 0, ScriptedAdsWatch.EnumValue(1, "Run")).Change(6, 0, ScriptedAdsWatch.EnumValue(2, "Stop"));
        var sum = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(100), 1000, default);
        var lines = SignalCsv.Build(watch.Symbols, sum.Changes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("0;E;1;Run", lines[1]);
        Assert.Equal("5;E;2;Stop", lines[2]);
    }
}
