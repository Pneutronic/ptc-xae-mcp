using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- signal testbench: watch, state and engine ---------------------------------------------
// The tools wait for, check and record live values from ADS notifications. The engine below works on an
// IAdsWatch (a stream of value changes with a clock), so the whole decision logic runs in unit tests with a scripted
// watch and a virtual clock; only the watch itself (AdsWatch.cs) talks to a PLC.

/// <summary>One value change of a watched symbol.</summary>
/// <param name="Index">Index into <see cref="IAdsWatch.Symbols"/>.</param>
/// <param name="Host">When the host received it, on the watch's clock.</param>
/// <param name="PlcTicks">The runtime's own time stamp (100 ns ticks), 0 when there is none. Intervals between samples
/// are exact to a task cycle on this clock, not on <paramref name="Host"/>.</param>
internal readonly record struct SignalSample(int Index, TimeSpan Host, long PlcTicks, SignalReading Reading);

/// <summary>An open set of notifications on one runtime. Disposing deletes them.</summary>
internal interface IAdsWatch : IDisposable
{
    IReadOnlyList<WatchedSymbol> Symbols { get; }

    /// <summary>The watch's clock, started when the notifications were registered.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Why the notifications stopped (runtime stopped or restarted, router gone); null while they run.</summary>
    string? Fault { get; }

    /// <summary>The next change, waiting at most <paramref name="timeout"/> on the watch's clock; false when none came.</summary>
    bool TryTake(TimeSpan timeout, out SignalSample sample);
}

/// <summary>Outcome of opening a watch. <c>Watch</c> is set only on success and owned by the caller.</summary>
internal sealed record AdsWatchOpenResult(bool Success, IAdsWatch? Watch, string AmsNetId, int AdsPort, string Message);

/// <summary>The current and the first value of each watched symbol, and whether it changed since the start.</summary>
internal sealed class SignalWatchState
{
    private readonly SignalReading[] _current;
    private readonly bool[] _changed;

    public SignalWatchState(IReadOnlyList<WatchedSymbol> symbols)
    {
        Symbols = symbols;
        _current = symbols.Select(s => s.Initial).ToArray();
        _changed = new bool[symbols.Count];
    }

    public IReadOnlyList<WatchedSymbol> Symbols { get; }

    public SignalReading Current(int index) => _current[index];

    /// <summary>The value differed from the opening value at some point (stays true once it did).</summary>
    public bool HasChanged(int index) => _changed[index];

    /// <summary>Takes the sample in. True when its value differs from the one held before (<paramref name="previous"/>).</summary>
    public bool Apply(SignalSample sample, out SignalReading previous)
    {
        previous = _current[sample.Index];
        if (SnapshotValues.ValueEquals(previous.Value, sample.Reading.Value)) return false;
        _current[sample.Index] = sample.Reading;
        _changed[sample.Index] = true;
        return true;
    }
}

/// <summary>Conditions bound to the symbols of a watch, evaluated together (all must hold, or any one).</summary>
internal sealed class SignalConditionSet
{
    private readonly IReadOnlyList<(SignalCondition Condition, int Index)> _bound;

    private SignalConditionSet(IReadOnlyList<(SignalCondition, int)> bound, bool all)
    {
        _bound = bound;
        All = all;
    }

    public bool All { get; }

    public IReadOnlyList<SignalCondition> Conditions => _bound.Select(b => b.Condition).ToList();

    /// <summary>Distinct symbols (case-insensitive) the conditions name, in order of first mention.</summary>
    public static List<string> SymbolsOf(IReadOnlyList<SignalCondition> conditions) =>
        conditions.Select(c => c.Symbol).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Binds <paramref name="conditions"/> to the opened symbols; an error names the first condition that cannot apply.</summary>
    public static SignalConditionSet? Bind(IReadOnlyList<SignalCondition> conditions, IReadOnlyList<WatchedSymbol> symbols,
        bool all, out string error)
    {
        error = string.Empty;
        var bound = new List<(SignalCondition, int)>();
        foreach (var c in conditions)
        {
            int index = -1;
            for (int i = 0; i < symbols.Count; i++)
                if (string.Equals(symbols[i].Symbol, c.Symbol, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            if (index < 0) { error = $"'{c.Text}': symbol '{c.Symbol}' is not among the watched symbols."; return null; }
            if (SignalCompare.Validate(c, symbols[index]) is { } why) { error = why; return null; }
            bound.Add((c, index));
        }
        return new SignalConditionSet(bound, all);
    }

    /// <summary>Whether the set holds now, and which conditions hold.</summary>
    public (bool Met, bool[] Each) Evaluate(SignalWatchState state)
    {
        var each = new bool[_bound.Count];
        for (int i = 0; i < _bound.Count; i++)
        {
            var (c, index) = _bound[i];
            each[i] = c.Op == SignalOp.Changed
                ? state.HasChanged(index)
                : SignalCompare.Test(c, state.Symbols[index], state.Current(index));
        }
        bool met = All ? each.All(x => x) : each.Any(x => x);
        return (met, each);
    }
}

internal sealed record SignalViolation(string? Symbol, TimeSpan At, string Reason, SignalReading? From, SignalReading? To);

/// <summary>Result of a wait_until / expect run.</summary>
/// <param name="Elapsed">When the deciding event happened (or the full window).</param>
internal sealed record SignalRunOutcome(bool Passed, bool TimedOut, TimeSpan Elapsed, string? Fault,
    SignalWatchState State, bool[] Each, SignalViolation? Violation);

internal static class SignalTestbench
{
    /// <summary>How long the engine waits on the watch before it looks at the clock, cancellation and faults again.</summary>
    internal static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(50);

    private enum PumpEnd { Stopped, Deadline, Fault }

    private static PumpEnd Pump(IAdsWatch watch, TimeSpan deadline, CancellationToken ct, Func<SignalSample, bool> onSample)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Fault is not null) return PumpEnd.Fault;
            TimeSpan remaining = deadline - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) return PumpEnd.Deadline;
            if (!watch.TryTake(remaining < Slice ? remaining : Slice, out var sample)) continue;
            // A change that arrives after the window ended does not count for it.
            if (sample.Host > deadline) return PumpEnd.Deadline;
            if (onSample(sample)) return PumpEnd.Stopped;
        }
    }

    /// <summary>Waits until the conditions hold (already true counts: elapsed 0), or <paramref name="timeout"/> passes.</summary>
    public static SignalRunOutcome WaitUntil(IAdsWatch watch, SignalConditionSet set, TimeSpan timeout, CancellationToken ct)
    {
        var state = new SignalWatchState(watch.Symbols);
        TimeSpan start = watch.Elapsed;
        var (met, each) = set.Evaluate(state);
        if (met) return new(true, false, TimeSpan.Zero, null, state, each, null);

        TimeSpan at = TimeSpan.Zero;
        var end = Pump(watch, start + timeout, ct, s =>
        {
            at = s.Host - start;
            state.Apply(s, out _);
            (met, each) = set.Evaluate(state);
            return met;
        });
        return end switch
        {
            PumpEnd.Stopped => new(true, false, at, null, state, each, null),
            PumpEnd.Fault => new(false, false, watch.Elapsed - start, watch.Fault, state, each, null),
            _ => new(false, true, timeout, null, state, each, null),
        };
    }

    /// <summary>within: the conditions hold at some time inside the window (the same decision as wait_until).</summary>
    public static SignalRunOutcome Within(IAdsWatch watch, SignalConditionSet set, TimeSpan window, CancellationToken ct)
    {
        var o = WaitUntil(watch, set, window, ct);
        return o.Passed || o.Fault is not null ? o
            : o with { Violation = new SignalViolation(null, window, $"the conditions did not hold within {Ms(window)} ms", null, null) };
    }

    /// <summary>never: the conditions do not hold at any time of the window (the opening state counts).</summary>
    public static SignalRunOutcome Never(IAdsWatch watch, SignalConditionSet set, TimeSpan window, CancellationToken ct)
    {
        var state = new SignalWatchState(watch.Symbols);
        TimeSpan start = watch.Elapsed;
        var (met, each) = set.Evaluate(state);
        if (met)
            return new(false, false, TimeSpan.Zero, null, state, each,
                new SignalViolation(null, TimeSpan.Zero, "the conditions held when the observation started", null, null));

        SignalViolation? violation = null;
        TimeSpan at = TimeSpan.Zero;
        var end = Pump(watch, start + window, ct, s =>
        {
            at = s.Host - start;
            state.Apply(s, out _);
            (met, each) = set.Evaluate(state);
            if (!met) return false;
            violation = new SignalViolation(null, at, "the conditions held", null, null);
            return true;
        });
        return end switch
        {
            PumpEnd.Stopped => new(false, false, at, null, state, each, violation),
            PumpEnd.Fault => new(false, false, watch.Elapsed - start, watch.Fault, state, each, null),
            _ => new(true, false, window, null, state, each, null),
        };
    }

    /// <summary>stable: no watched value changes during the window.</summary>
    public static SignalRunOutcome Stable(IAdsWatch watch, TimeSpan window, CancellationToken ct)
    {
        var state = new SignalWatchState(watch.Symbols);
        TimeSpan start = watch.Elapsed;
        SignalViolation? violation = null;
        TimeSpan at = TimeSpan.Zero;
        var end = Pump(watch, start + window, ct, s =>
        {
            if (!state.Apply(s, out var previous)) return false;
            at = s.Host - start;
            violation = new SignalViolation(state.Symbols[s.Index].Symbol, at, "the value changed", previous, s.Reading);
            return true;
        });
        return end switch
        {
            PumpEnd.Stopped => new(false, false, at, null, state, [], violation),
            PumpEnd.Fault => new(false, false, watch.Elapsed - start, watch.Fault, state, [], null),
            _ => new(true, false, window, null, state, [], null),
        };
    }

    internal static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 1);
}

// ---- recording ------------------------------------------------------------------------------

/// <summary>A count/min/max/average of durations or values, in the unit of the field that carries it.</summary>
internal sealed record SignalSpan(int Count, double Min, double Max, double Avg)
{
    public static SignalSpan? Of(IReadOnlyList<double> values) =>
        values.Count == 0 ? null : new(values.Count, Math.Round(values.Min(), 3), Math.Round(values.Max(), 3), Math.Round(values.Average(), 3));
}

internal sealed record SignalChange(int Index, long Ticks, SignalReading Reading);

internal sealed record SymbolStats(
    WatchedSymbol Symbol,
    int Changes,
    SignalReading First,
    SignalReading Last,
    double? Min,
    double? Max,
    double? Mean,
    int Rising,
    int Falling,
    SignalSpan? HighMs,
    SignalSpan? LowMs,
    SignalSpan? PeriodMs,
    SignalSpan? ChangeIntervalMs);

internal sealed record RecordSummary(
    IReadOnlyList<SymbolStats> Stats, IReadOnlyList<SignalChange> Changes, double WindowMs, bool LimitReached,
    string? Fault, string TimeBase);

/// <summary>
/// Collects the value changes of a recording and derives the statistics. Times come from the runtime's own time
/// stamps (relative to the first sample received), so a pulse width is exact to a task cycle however late the host
/// scheduled its thread; without stamps the host clock stands in.
/// </summary>
internal sealed class RecordAggregator
{
    private const long TicksPerMs = TimeSpan.TicksPerMillisecond;

    private readonly IReadOnlyList<WatchedSymbol> _symbols;
    private readonly int _maxChanges;
    private readonly SignalWatchState _state;
    private readonly List<SignalChange> _changes = [];
    private readonly long[] _lastTicks;
    private bool _originSet;
    private long _originPlc;
    private TimeSpan _originHost;
    private bool _usedPlcClock;
    private bool _usedHostClock;
    private long _lastTicksAny;
    private bool _limitReached;

    public RecordAggregator(IReadOnlyList<WatchedSymbol> symbols, int maxChanges)
    {
        _symbols = symbols;
        _maxChanges = maxChanges;
        _state = new SignalWatchState(symbols);
        _lastTicks = new long[symbols.Count];
        for (int i = 0; i < symbols.Count; i++)
            _changes.Add(new SignalChange(i, 0, symbols[i].Initial));
    }

    public bool LimitReached => _limitReached;

    /// <summary>Takes a sample in. False when the change limit was reached (the run must stop).</summary>
    public bool Add(SignalSample s)
    {
        if (!_originSet)
        {
            _originSet = true;
            _originHost = s.Host;
            _originPlc = s.PlcTicks;
        }
        long t;
        if (s.PlcTicks > 0 && _originPlc > 0)
        {
            _usedPlcClock = true;
            t = Math.Max(0, s.PlcTicks - _originPlc);
        }
        else
        {
            _usedHostClock = true;
            t = Math.Max(0, (s.Host - _originHost).Ticks);
        }
        t = Math.Max(t, _lastTicks[s.Index]);

        if (!_state.Apply(s, out _)) return true;
        if (_changes.Count - _symbols.Count >= _maxChanges) { _limitReached = true; return false; }
        _lastTicks[s.Index] = t;
        _lastTicksAny = Math.Max(_lastTicksAny, t);
        _changes.Add(new SignalChange(s.Index, t, s.Reading));
        return true;
    }

    public RecordSummary Finish(TimeSpan hostElapsed, string? fault)
    {
        // The window ends at the later of the last change and the host time that passed since the first sample.
        long hostWindow = _originSet ? Math.Max(0, (hostElapsed - _originHost).Ticks) : hostElapsed.Ticks;
        long endTicks = Math.Max(_lastTicksAny, hostWindow);
        var stats = new List<SymbolStats>();
        for (int i = 0; i < _symbols.Count; i++)
            stats.Add(Stats(i, endTicks));
        return new RecordSummary(stats, _changes, Math.Round(endTicks / (double)TicksPerMs, 1), _limitReached, fault,
            _usedPlcClock ? "plc" : _usedHostClock ? "host" : "none");
    }

    private SymbolStats Stats(int index, long endTicks)
    {
        var symbol = _symbols[index];
        var own = _changes.Where(c => c.Index == index).ToList();
        var first = own[0].Reading;
        var last = own[^1].Reading;
        int changes = own.Count - 1;

        // Intervals between consecutive changes (the opening value has no known start, so it is not one of them).
        var intervals = new List<double>();
        for (int k = 2; k < own.Count; k++) intervals.Add((own[k].Ticks - own[k - 1].Ticks) / (double)TicksPerMs);

        double? min = null, max = null, mean = null;
        var values = own.Select(c => AsNumber(symbol, c.Reading)).ToList();
        if (values.All(v => v is not null))
        {
            min = Math.Round(values.Min()!.Value, 6);
            max = Math.Round(values.Max()!.Value, 6);
            double weighted = 0;
            for (int k = 0; k < own.Count; k++)
            {
                long until = k + 1 < own.Count ? own[k + 1].Ticks : endTicks;
                weighted += values[k]!.Value * Math.Max(0, until - own[k].Ticks);
            }
            mean = endTicks > 0 ? Math.Round(weighted / endTicks, 6) : values[0];
        }

        int rising = 0, falling = 0;
        SignalSpan? high = null, low = null, period = null;
        if (symbol.IsBool)
        {
            var highs = new List<double>();
            var lows = new List<double>();
            var periods = new List<double>();
            long? lastRise = null, lastFall = null;
            for (int k = 1; k < own.Count; k++)
            {
                bool nowHigh = own[k].Reading.Value.ValueKind == JsonValueKind.True;
                long t = own[k].Ticks;
                if (nowHigh)
                {
                    rising++;
                    if (lastFall is { } f) lows.Add((t - f) / (double)TicksPerMs);
                    if (lastRise is { } r) periods.Add((t - r) / (double)TicksPerMs);
                    lastRise = t;
                }
                else
                {
                    falling++;
                    if (lastRise is { } r) highs.Add((t - r) / (double)TicksPerMs);
                    lastFall = t;
                }
            }
            high = SignalSpan.Of(highs);
            low = SignalSpan.Of(lows);
            period = SignalSpan.Of(periods);
        }

        return new SymbolStats(symbol, changes, first, last, min, max, mean, rising, falling, high, low, period,
            SignalSpan.Of(intervals));
    }

    internal static double? AsNumber(WatchedSymbol s, SignalReading r)
    {
        if (r.Value.ValueKind is JsonValueKind.True) return 1;
        if (r.Value.ValueKind is JsonValueKind.False) return 0;
        if (r.Value.ValueKind == JsonValueKind.Number && !s.IsText) return r.Value.GetDouble();
        return null;
    }
}

/// <summary>Runs a recording: pumps the watch for the window into a <see cref="RecordAggregator"/>.</summary>
internal static class SignalRecorder
{
    public static RecordSummary Run(IAdsWatch watch, TimeSpan duration, int maxChanges, CancellationToken ct)
    {
        var aggregator = new RecordAggregator(watch.Symbols, maxChanges);
        TimeSpan start = watch.Elapsed;
        TimeSpan deadline = start + duration;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (watch.Fault is not null || aggregator.LimitReached) break;
            TimeSpan remaining = deadline - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            if (!watch.TryTake(remaining < SignalTestbench.Slice ? remaining : SignalTestbench.Slice, out var sample)) continue;
            if (sample.Host > deadline) break;
            if (!aggregator.Add(sample)) break;
        }
        return aggregator.Finish(watch.Elapsed - start, watch.Fault);
    }
}

/// <summary>The change log of a recording as CSV (<c>;</c>-separated, one row per change, the opening values at 0).</summary>
internal static class SignalCsv
{
    public const string Header = "t_ms;symbol;value;name";

    public static string Build(IReadOnlyList<WatchedSymbol> symbols, IReadOnlyList<SignalChange> changes)
    {
        var sb = new StringBuilder(Header).Append("\r\n");
        foreach (var c in changes.OrderBy(c => c.Ticks).ThenBy(c => c.Index))
        {
            sb.Append((c.Ticks / (double)TimeSpan.TicksPerMillisecond).ToString("0.###", CultureInfo.InvariantCulture)).Append(';')
              .Append(Field(symbols[c.Index].Symbol)).Append(';')
              .Append(Field(Text(c.Reading.Value))).Append(';')
              .Append(Field(c.Reading.EnumName ?? string.Empty)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };

    private static string Field(string text) =>
        text.IndexOfAny([';', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
}
