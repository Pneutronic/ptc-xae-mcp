using System.Globalization;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// signal testbench: validates the arguments of twincat_ads_wait_until / _expect / _record / _snapshot_compare, runs
/// them and shapes the answers into JSON. Honest errors are "[Error] ..." strings, never a faked success. None of
/// them writes to the PLC; record and snapshot_compare write local files only.
/// <para>
/// The long calls (wait, expect, record) hold the dispatcher lock only while the notifications are registered
/// (<paramref name="runLocked"/>); the wait itself runs without it, so other tools stay available meanwhile.
/// </para>
/// </summary>
internal sealed class SignalTestbenchCoordinator(
    ITwinCatSession session, Func<Func<string>, string>? runLocked = null, Func<DateTime>? clock = null)
{
    private const int DefaultAdsPort = 851;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Where record writes its CSV when no path is given.</summary>
    internal static string DefaultCsvDirectory => Path.Combine(Path.GetTempPath(), "ptc-xae-mcp", "rec");

    /// <summary>Recordings older than this are removed from <see cref="DefaultCsvDirectory"/> by the next recording.</summary>
    internal static readonly TimeSpan CsvRetention = TimeSpan.FromDays(7);

    // ---- shared ----------------------------------------------------------------------------

    private T Locked<T>(Func<T> body)
    {
        T result = default!;
        string Run() { result = body(); return "OK"; }
        if (runLocked is null) Run(); else runLocked(Run);
        return result;
    }

    private string? SolutionDirectory()
    {
        string? sln = Locked(session.ActiveSolutionPath);
        return string.IsNullOrWhiteSpace(sln) ? null : Path.GetDirectoryName(sln);
    }

    private (IAdsWatch? Watch, string? Error, string NetId, int Port) OpenWatch(
        IReadOnlyList<string> symbols, string? amsNetId, int? adsPort, int cycleMs)
    {
        int port = adsPort ?? DefaultAdsPort;
        var open = Locked(() => session.OpenAdsWatch(symbols, amsNetId, port, cycleMs));
        return open.Success && open.Watch is not null
            ? (open.Watch, null, open.AmsNetId, open.AdsPort)
            : (null, AdsVariableCoordinator.FailedAt(open.Message, open.AmsNetId, open.AdsPort), open.AmsNetId, open.AdsPort);
    }

    private static bool TryCombine(string? combine, out bool all, out string error)
    {
        all = true;
        error = string.Empty;
        switch ((combine ?? "all").Trim().ToLowerInvariant())
        {
            case "all" or "and": return true;
            case "any" or "or": all = false; return true;
            default:
                error = $"combine must be 'all' (every condition holds) or 'any' (one holds), not '{combine}'.";
                return false;
        }
    }

    private static string? CheckRange(string name, int value, int min, int max) =>
        value < min || value > max
            ? $"[Error] {name} must be {min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)} " +
              $"(a fixed limit of this tool), not {value.ToString(CultureInfo.InvariantCulture)}."
            : null;

    /// <summary>The distinct, trimmed symbol paths (case-insensitive); an error when none, or more than <paramref name="max"/>.</summary>
    private static string? CleanSymbols(IReadOnlyList<string>? raw, int max, out List<string> symbols)
    {
        symbols = (raw ?? [])
            .Select(s => (s ?? string.Empty).Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (symbols.Count == 0) return "[Error] symbols is required (at least one symbol path).";
        if (symbols.Count > max)
            return $"[Error] At most {max} symbols per call (a fixed limit of this tool), got {symbols.Count}.";
        return null;
    }

    private static object? ValueJson(SignalReading r) =>
        r.EnumName is null ? r.Value : new { name = r.EnumName, value = r.Value };

    private static Dictionary<string, object?> ValuesJson(SignalWatchState state)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < state.Symbols.Count; i++) values[state.Symbols[i].Symbol] = ValueJson(state.Current(i));
        return values;
    }

    private static object[] EachJson(SignalConditionSet set, bool[] each) =>
        set.Conditions.Select((c, i) => (object)new { condition = c.Text, met = i < each.Length && each[i] }).ToArray();

    private static string FaultError(SignalRunOutcome o, string target) =>
        $"[Error] The notifications stopped after {SignalTestbench.Ms(o.Elapsed)} ms: {o.Fault}. Nothing can be said about the " +
        $"signals after that point ({target}).";

    // ---- wait_until ------------------------------------------------------------------------

    public string WaitUntil(IReadOnlyList<string>? conditions, string? combine, int? timeoutMs, string? amsNetId, int? adsPort,
        CancellationToken ct)
    {
        if (!SignalConditionParser.TryParseAll(conditions, out var parsed, out string error)) return McpErrors.EnsureError(error);
        if (!TryCombine(combine, out bool all, out error)) return McpErrors.EnsureError(error);
        var symbols = SignalConditionSet.SymbolsOf(parsed);
        if (symbols.Count > SignalLimits.MaxWatchSymbols)
            return $"[Error] The conditions name {symbols.Count} symbols; at most {SignalLimits.MaxWatchSymbols} (a fixed limit of this tool).";
        int timeout = timeoutMs ?? SignalLimits.DefaultWaitTimeoutMs;
        if (CheckRange("timeoutMs", timeout, 1, SignalLimits.MaxWaitTimeoutMs) is { } bad) return bad;

        var (watch, openError, netId, port) = OpenWatch(symbols, amsNetId, adsPort, SignalLimits.MinCycleMs);
        if (watch is null) return openError!;
        using (watch)
        {
            var set = SignalConditionSet.Bind(parsed, watch.Symbols, all, out error);
            if (set is null) return McpErrors.EnsureError(error);

            var o = SignalTestbench.WaitUntil(watch, set, TimeSpan.FromMilliseconds(timeout), ct);
            if (o.Fault is not null) return FaultError(o, $"{netId}:{port}");
            return JsonSerializer.Serialize(new
            {
                success = true,
                met = o.Passed,
                timedOut = o.TimedOut,
                elapsedMs = SignalTestbench.Ms(o.Elapsed),
                timeoutMs = timeout,
                combine = all ? "all" : "any",
                conditions = EachJson(set, o.Each),
                values = ValuesJson(o.State),
                amsNetId = netId,
                adsPort = port,
            }, JsonOpts);
        }
    }

    // ---- expect ----------------------------------------------------------------------------

    public string Expect(string? mode, int? durationMs, IReadOnlyList<string>? conditions, IReadOnlyList<string>? symbols,
        string? combine, string? amsNetId, int? adsPort, CancellationToken ct)
    {
        string kind = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (kind is not ("stable" or "within" or "never"))
            return "[Error] mode must be 'stable' (the values stay the same), 'within' (the conditions hold inside the window) " +
                   "or 'never' (the conditions do not hold during the window).";
        if (durationMs is null) return "[Error] durationMs is required (the length of the observation window in ms).";
        if (CheckRange("durationMs", durationMs.Value, 1, SignalLimits.MaxWaitTimeoutMs) is { } bad) return bad;
        if (!TryCombine(combine, out bool all, out string error)) return McpErrors.EnsureError(error);

        List<SignalCondition> parsed = [];
        List<string> names;
        if (kind == "stable")
        {
            if (conditions is { Count: > 0 }) return "[Error] mode 'stable' takes symbols, not conditions.";
            if (CleanSymbols(symbols, SignalLimits.MaxWatchSymbols, out names) is { } symbolError) return symbolError;
        }
        else
        {
            if (symbols is { Count: > 0 }) return $"[Error] mode '{kind}' takes conditions, not symbols.";
            if (!SignalConditionParser.TryParseAll(conditions, out parsed, out error)) return McpErrors.EnsureError(error);
            names = SignalConditionSet.SymbolsOf(parsed);
            if (names.Count > SignalLimits.MaxWatchSymbols)
                return $"[Error] The conditions name {names.Count} symbols; at most {SignalLimits.MaxWatchSymbols} (a fixed limit of this tool).";
        }

        var (watch, openError, netId, port) = OpenWatch(names, amsNetId, adsPort, SignalLimits.MinCycleMs);
        if (watch is null) return openError!;
        using (watch)
        {
            var window = TimeSpan.FromMilliseconds(durationMs.Value);
            SignalConditionSet? set = null;
            if (kind != "stable")
            {
                set = SignalConditionSet.Bind(parsed, watch.Symbols, all, out error);
                if (set is null) return McpErrors.EnsureError(error);
            }

            var o = kind switch
            {
                "stable" => SignalTestbench.Stable(watch, window, ct),
                "within" => SignalTestbench.Within(watch, set!, window, ct),
                _ => SignalTestbench.Never(watch, set!, window, ct),
            };
            if (o.Fault is not null) return FaultError(o, $"{netId}:{port}");

            return JsonSerializer.Serialize(new
            {
                success = true,
                mode = kind,
                passed = o.Passed,
                elapsedMs = SignalTestbench.Ms(o.Elapsed),
                durationMs = durationMs.Value,
                firstViolation = o.Violation is { } v
                    ? new
                    {
                        symbol = v.Symbol,
                        atMs = SignalTestbench.Ms(v.At),
                        reason = v.Reason,
                        from = v.From is { } f ? ValueJson(f) : null,
                        to = v.To is { } t ? ValueJson(t) : null,
                    }
                    : null,
                combine = set is null ? null : all ? "all" : "any",
                conditions = set is null ? null : EachJson(set, o.Each),
                values = ValuesJson(o.State),
                amsNetId = netId,
                adsPort = port,
            }, JsonOpts);
        }
    }

    // ---- record ----------------------------------------------------------------------------

    public string Record(IReadOnlyList<string>? symbols, int? durationMs, int? cycleMs, string? csvPath, bool writeCsv,
        string? amsNetId, int? adsPort, CancellationToken ct)
    {
        if (CleanSymbols(symbols, SignalLimits.MaxRecordSymbols, out var names) is { } symbolError) return symbolError;
        int duration = durationMs ?? SignalLimits.DefaultRecordDurationMs;
        if (CheckRange("durationMs", duration, 1, SignalLimits.MaxRecordDurationMs) is { } badDuration) return badDuration;
        int cycle = cycleMs ?? SignalLimits.MinCycleMs;
        if (CheckRange("cycleMs", cycle, SignalLimits.MinCycleMs, SignalLimits.MaxCycleMs) is { } badCycle) return badCycle;

        string? csvTarget = null;
        if (writeCsv && !string.IsNullOrWhiteSpace(csvPath))
        {
            csvTarget = csvPath.Trim();
            if (!PathPolicy.IsAllowed(csvTarget, SolutionDirectory(), out string why)) return $"[Error] csvPath refused: {why}";
            if (!csvTarget.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return "[Error] csvPath must end in .csv.";
        }

        var (watch, openError, netId, port) = OpenWatch(names, amsNetId, adsPort, cycle);
        if (watch is null) return openError!;
        using (watch)
        {
            DateTime started = _clock();
            var summary = SignalRecorder.Run(watch, TimeSpan.FromMilliseconds(duration), SignalLimits.MaxRecordEvents, ct);

            object? csv = null;
            string? csvError = null;
            if (writeCsv)
            {
                csvTarget ??= Path.Combine(DefaultCsvDirectory,
                    string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMdd'T'HHmmss'Z'}_{netId.Replace(':', '_')}.csv"));
                try
                {
                    string full = Path.GetFullPath(csvTarget);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(DefaultCsvDirectory), StringComparison.OrdinalIgnoreCase))
                        PruneOldRecordings(DefaultCsvDirectory, _clock());
                    string text = SignalCsv.Build(watch.Symbols, summary.Changes);
                    File.WriteAllText(full, text, new UTF8Encoding(false));
                    csv = new { path = full, rows = summary.Changes.Count, bytes = text.Length };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    csvError = $"Cannot write '{csvTarget}': {ex.Message}";
                }
            }

            string endedBy = summary.Fault is not null ? "fault" : summary.LimitReached ? "changeLimit" : "completed";
            return JsonSerializer.Serialize(new
            {
                success = true,
                endedBy,
                fault = summary.Fault,
                durationMs = duration,
                windowMs = summary.WindowMs,
                cycleMs = cycle,
                timeBase = summary.TimeBase,
                changes = summary.Changes.Count - watch.Symbols.Count,
                changeLimit = SignalLimits.MaxRecordEvents,
                csv,
                csvError,
                symbols = summary.Stats.Select(StatsJson).ToArray(),
                note = "Changes are seen at the resolution of the PLC task that owns the symbol: a pulse shorter than one task cycle is not visible.",
                amsNetId = netId,
                adsPort = port,
            }, JsonOpts);
        }
    }

    private static object StatsJson(SymbolStats s)
    {
        double? frequency = s.PeriodMs is { Avg: > 0 } p ? Math.Round(1000.0 / p.Avg, 3) : null;
        return new
        {
            symbol = s.Symbol.Symbol,
            plcType = s.Symbol.PlcType,
            changes = s.Changes,
            first = ValueJson(s.First),
            last = ValueJson(s.Last),
            min = s.Min,
            max = s.Max,
            mean = s.Mean,
            rising = s.Symbol.IsBool ? s.Rising : (int?)null,
            falling = s.Symbol.IsBool ? s.Falling : (int?)null,
            highMs = s.HighMs,
            lowMs = s.LowMs,
            periodMs = s.PeriodMs,
            frequencyHz = frequency,
            changeIntervalMs = s.ChangeIntervalMs,
        };
    }

    internal static void PruneOldRecordings(string directory, DateTime now)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.csv"))
                if (now - file.LastWriteTimeUtc > CsvRetention) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping only: an old recording that cannot be removed is left where it is.
        }
    }

    // ---- snapshot_compare ------------------------------------------------------------------

    public string SnapshotCompare(IReadOnlyList<string>? symbols, string? baselinePath, string? baseline, string? savePath,
        double? tolerance, string? amsNetId, int? adsPort)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(baselinePath);
        bool hasInline = !string.IsNullOrWhiteSpace(baseline);
        bool hasSave = !string.IsNullOrWhiteSpace(savePath);
        if (hasPath && hasInline) return "[Error] Give baselinePath or baseline, not both.";
        if (!hasPath && !hasInline && !hasSave)
            return "[Error] Nothing to do: give a baseline (baselinePath or baseline) to compare with, and/or savePath to store the current values.";
        double tol = tolerance ?? 0;
        if (double.IsNaN(tol) || double.IsInfinity(tol) || tol < 0) return "[Error] tolerance must be 0 or more.";

        int port = adsPort ?? DefaultAdsPort;
        string? solDir = SolutionDirectory();

        // The baseline.
        IReadOnlyDictionary<string, SnapshotLeaf>? fileLeaves = null;
        Dictionary<string, JsonElement>? inline = null;
        if (hasPath)
        {
            var (doc, error) = LoadBaseline(baselinePath!, solDir);
            if (doc is null) return error!;
            fileLeaves = doc.Values;
        }
        else if (hasInline)
        {
            if (!TryParseInline(baseline!, out inline, out string error)) return "[Error] " + error;
        }

        string? savePathFull = null;
        if (hasSave)
        {
            string target = savePath!.Trim();
            if (!PathPolicy.IsAllowed(target, solDir, out string why)) return $"[Error] savePath refused: {why}";
            if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "[Error] savePath must end in .json.";
            savePathFull = Path.GetFullPath(target);
        }

        // The symbols to read: the explicit list, else everything the baseline names.
        List<string> paths;
        if (symbols is { Count: > 0 })
        {
            if (CleanSymbols(symbols, SignalLimits.MaxSnapshotSymbols, out paths) is { } symbolError) return symbolError;
        }
        else
        {
            var fromBaseline = (fileLeaves?.Keys ?? inline?.Keys)?.ToList();
            if (fromBaseline is null || fromBaseline.Count == 0) return "[Error] symbols is required when there is no baseline to take the symbols from.";
            if (fromBaseline.Count > SignalLimits.MaxSnapshotLeaves)
                return $"[Error] The baseline names {fromBaseline.Count} leaves; pass symbols (at most {SignalLimits.MaxSnapshotSymbols}) or use " +
                       "twincat_persistent_snapshot_compare for a remanent snapshot (at most " + SignalLimits.MaxSnapshotLeaves + " leaves here).";
            paths = fromBaseline;
        }

        var read = Locked(() => session.ReadSnapshotLeaves(new SnapshotReadRequest(paths, Observe: true), amsNetId, port));
        if (!read.Success) return AdsVariableCoordinator.FailedAt(read.Message, read.AmsNetId, read.AdsPort);
        if (read.Leaves.Count > SignalLimits.MaxSnapshotLeaves)
            return $"[Error] The symbols hold {read.Leaves.Count} leaves; at most {SignalLimits.MaxSnapshotLeaves} (a fixed limit of this tool). Name fewer.";

        object? saved = null;
        if (savePathFull is not null)
        {
            if (read.Leaves.Count == 0) return "[Error] Nothing to save: no symbol produced a readable leaf.";
            var doc = new SnapshotDocument(
                new SnapshotMeta(_clock().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), read.AmsNetId, read.AdsPort,
                    null, null, paths, read.Leaves.Count, read.Skipped),
                read.Leaves);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePathFull)!);
                File.WriteAllText(savePathFull, SnapshotFile.Serialize(doc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"[Error] Cannot write '{savePathFull}': {ex.Message}";
            }
            saved = new { path = savePathFull, leafCount = read.Leaves.Count };
        }

        var cap = SignalLimits.SnapshotReportCap;
        if (!hasPath && !hasInline)
        {
            return JsonSerializer.Serialize(new
            {
                success = true,
                saved,
                leafCount = read.Leaves.Count,
                skipped = read.Skipped.Take(cap),
                skippedCount = read.Skipped.Count,
                missing = read.Missing,
                amsNetId = read.AmsNetId,
                adsPort = read.AdsPort,
            }, JsonOpts);
        }

        var baselineLeaves = fileLeaves is not null
            ? RestrictTo(fileLeaves, symbols is { Count: > 0 } ? paths : null)
            : InlineLeaves(inline!, read.Leaves);
        var diff = SnapshotDiff.Compare(baselineLeaves, read.Leaves, SnapshotPathMap.Empty);

        var differences = new List<SnapshotDifference>();
        int withinTolerance = 0;
        foreach (var d in diff.Differences)
        {
            if (tol > 0 && SignalCompare.TryJsonDecimal(d.Snapshot, out decimal a) && SignalCompare.TryJsonDecimal(d.Live, out decimal b)
                && Math.Abs(a - b) <= (decimal)tol)
                withinTolerance++;
            else
                differences.Add(d);
        }
        bool equal = differences.Count == 0 && diff.OnlyInSnapshot.Count == 0 && diff.TypeMismatches.Count == 0
                     && diff.Conflicts.Count == 0 && read.Missing.Count == 0;

        return JsonSerializer.Serialize(new
        {
            success = true,
            equal,
            equalCount = diff.EqualCount + withinTolerance,
            withinToleranceCount = withinTolerance,
            tolerance = tol,
            differenceCount = differences.Count,
            onlyInBaselineCount = diff.OnlyInSnapshot.Count,
            notInBaselineCount = diff.OnlyOnPlc.Count,
            typeMismatchCount = diff.TypeMismatches.Count,
            missingOnPlcCount = read.Missing.Count,
            differences = differences.Take(cap).Select(d => new { symbol = d.Path, type = d.Type, baseline = d.Snapshot, live = d.Live }),
            onlyInBaseline = diff.OnlyInSnapshot.Take(cap),
            notInBaseline = diff.OnlyOnPlc.Take(cap),
            typeMismatches = diff.TypeMismatches.Take(cap),
            missingOnPlc = read.Missing.Take(cap),
            skipped = read.Skipped.Take(cap),
            saved,
            amsNetId = read.AmsNetId,
            adsPort = read.AdsPort,
        }, JsonOpts);
    }

    private (SnapshotDocument? Doc, string? Error) LoadBaseline(string path, string? solutionDirectory)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, $"[Error] baselinePath is not a valid path: {ex.Message}"); }
        if (!PathPolicy.IsAllowed(full, solutionDirectory, out string why)) return (null, $"[Error] baselinePath refused: {why}");
        if (!File.Exists(full)) return (null, $"[Error] baselinePath '{full}' does not exist.");
        string json;
        try { json = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (null, $"[Error] Cannot read '{full}': {ex.Message}"); }
        return SnapshotFile.TryParse(json, out var doc, out string error) ? (doc, null) : (null, $"[Error] baselinePath: {error}.");
    }

    /// <summary>The baseline leaves at or below one of <paramref name="roots"/> (all of them when null).</summary>
    internal static IReadOnlyDictionary<string, SnapshotLeaf> RestrictTo(IReadOnlyDictionary<string, SnapshotLeaf> leaves, IReadOnlyList<string>? roots)
    {
        if (roots is null) return leaves;
        return leaves.Where(kv => roots.Any(r => IsAtOrBelow(kv.Key, r)))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static bool IsAtOrBelow(string leaf, string root) =>
        string.Equals(leaf, root, StringComparison.OrdinalIgnoreCase)
        || (leaf.Length > root.Length && leaf.StartsWith(root, StringComparison.OrdinalIgnoreCase) && leaf[root.Length] is '.' or '[');

    internal static bool TryParseInline(string json, out Dictionary<string, JsonElement> values, out string error)
    {
        values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        error = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "baseline must be a JSON object { \"<symbol>\": <value>, ... }.";
                return false;
            }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // The shape the read tools answer for an ENUM ({ name, value }) is accepted as its number.
                var v = p.Value;
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner)) v = inner;
                if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    error = $"baseline value of '{p.Name}' must be a number, a boolean or a string.";
                    return false;
                }
                values[p.Name] = v.Clone();
            }
            if (values.Count == 0) { error = "baseline is empty."; return false; }
            return true;
        }
        catch (JsonException ex)
        {
            error = $"baseline is not valid JSON: {ex.Message}";
            return false;
        }
    }

    /// <summary>A written baseline has no types: each value takes the type the symbol has on the PLC now.</summary>
    internal static IReadOnlyDictionary<string, SnapshotLeaf> InlineLeaves(
        IReadOnlyDictionary<string, JsonElement> inline, IReadOnlyDictionary<string, SnapshotLeaf> live)
    {
        var liveKeys = live.Keys.ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        var leaves = new Dictionary<string, SnapshotLeaf>(StringComparer.Ordinal);
        foreach (var (symbol, value) in inline)
        {
            // The live key, so the compare finds the leaf whatever case the baseline used.
            if (liveKeys.TryGetValue(symbol, out string? key)) leaves[key] = new SnapshotLeaf(live[key].Type, value);
            else leaves[symbol] = new SnapshotLeaf("UNKNOWN", value);
        }
        return leaves;
    }
}
