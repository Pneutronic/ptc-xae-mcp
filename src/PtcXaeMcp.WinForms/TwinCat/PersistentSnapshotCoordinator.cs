using System.Text.Json;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// persistent-snapshot: validates input and shapes the results of list / save / compare / restore
/// into JSON for the MCP layer. Honest errors are "[Error] ..." strings, never a faked success.
/// The restore itself is only ever PROPOSED here — see <see cref="SnapshotRestoreCoordinator"/>.
/// </summary>
internal sealed class PersistentSnapshotCoordinator(
    ITwinCatSession session, SnapshotRestoreCoordinator restore, Func<DateTime>? clock = null)
{
    private const int DefaultAdsPort = 851;
    private const int ReportCap = 200;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- list ------------------------------------------------------------------------

    public string ListSymbols(string? root, int? maxDepth, bool persistentOnly, int? limit, string? amsNetId, int? adsPort)
    {
        int port = adsPort ?? DefaultAdsPort;
        var r = session.ListAdsSymbols(string.IsNullOrWhiteSpace(root) ? null : root.Trim(),
            Math.Clamp(maxDepth ?? 1, 0, 16), persistentOnly, Math.Clamp(limit ?? 500, 1, 5000), amsNetId, port);
        if (!r.Success) return AdsVariableCoordinator.FailedAt(r.Message, r.AmsNetId, r.AdsPort);
        return JsonSerializer.Serialize(new
        {
            success = true,
            count = r.Symbols.Count,
            truncated = r.Truncated,
            symbols = r.Symbols,
            amsNetId = r.AmsNetId,
            adsPort = r.AdsPort,
        }, JsonOpts);
    }

    // ---- selection ---------------------------------------------------------------------

    private sealed record Selection(
        string Source, SnapshotReadRequest Request, IReadOnlyList<TmcLeaf> TmcLeaves, List<string> Warnings, List<string> Roots);

    /// <summary>Picks the TMC: the explicit path, else the newest <c>Port_&lt;port&gt;.tmc</c> under the solution's
    /// <c>_Boot</c>, else the one PLC-project TMC. Several candidates are ambiguous, never guessed.</summary>
    internal static (string? Path, string? Error) FindTmc(string? solutionDirectory, int adsPort)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
            return (null, null);
        try
        {
            var boot = Directory.EnumerateFiles(solutionDirectory, $"Port_{adsPort}.tmc", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (boot is not null) return (boot.FullName, null);

            var candidates = Directory.EnumerateFiles(solutionDirectory, "*.tmc", SearchOption.AllDirectories)
                .Where(f => !f.Contains(@"\_CompileInfo\", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains(@"\_Boot\", StringComparison.OrdinalIgnoreCase))
                .Where(f => TmcFile.Inspect(f).Facts is not null).ToList();
            return candidates.Count switch
            {
                0 => (null, null),
                1 => (candidates[0], null),
                _ => (null, "several PLC .tmc files found — pass tmcPath: " + string.Join("; ", candidates.Take(5))),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private (Selection? Selection, string? Error) Select(
        string selection, string kind, IReadOnlyList<string>? roots, string? tmcPath, string? amsNetId, int port)
    {
        selection = (string.IsNullOrWhiteSpace(selection) ? "auto" : selection.Trim()).ToLowerInvariant();
        kind = (string.IsNullOrWhiteSpace(kind) ? "both" : kind.Trim()).ToLowerInvariant();
        if (selection is not ("auto" or "tmc" or "ads" or "roots"))
            return (null, "[Error] selection must be auto, tmc, ads or roots.");
        if (kind is not ("persistent" or "retain" or "both"))
            return (null, "[Error] kind must be persistent, retain or both.");

        var explicitRoots = (roots ?? []).Select(r => (r ?? "").Trim()).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var warnings = new List<string>();

        if (selection == "roots")
        {
            if (explicitRoots.Count == 0) return (null, "[Error] selection=roots needs roots[].");
            return (new Selection("roots", new SnapshotReadRequest(explicitRoots), [], warnings, explicitRoots), null);
        }

        if (selection is "auto" or "tmc")
        {
            string? path = string.IsNullOrWhiteSpace(tmcPath) ? null : tmcPath.Trim();
            string? solDir = SolutionDirectory();
            if (path is not null)
            {
                if (!PathPolicy.IsAllowed(path, solDir, out string why)) return (null, $"[Error] tmcPath refused: {why}");
            }
            else
            {
                var (found, ambiguous) = FindTmc(solDir, port);
                if (ambiguous is not null && selection == "tmc") return (null, "[Error] " + ambiguous);
                path = found;
            }

            if (path is null && selection == "tmc")
                return (null, "[Error] No PLC .tmc found for the open solution — pass tmcPath.");

            if (path is not null)
            {
                string xml;
                try { xml = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return (null, $"[Error] Cannot read '{path}': {ex.Message}"); }

                if (!TmcRemanentSelector.TrySelect(xml, kind, TmcRemanentSelector.DefaultLeafLimit, out var sel, out string error))
                    return (null, "[Error] " + error);
                var paths = sel.Leaves.Select(l => l.Path).Concat(explicitRoots).Distinct(StringComparer.Ordinal).ToList();
                // IsPersistent on the runtime is a cross-check: anything it flags that the TMC did not is reported, not dropped.
                var req = new SnapshotReadRequest(paths, AdsPersistentRoots: kind != "retain");
                var list = new List<string>(explicitRoots);
                return (new Selection(explicitRoots.Count > 0 ? "tmc+roots" : "tmc", req, sel.Leaves, warnings, list), null);
            }
        }

        // ads
        if (kind == "retain" && explicitRoots.Count == 0)
            return (null, "[Error] RETAIN cannot be detected over ADS alone — give a TMC (tmcPath / open solution) or roots[].");
        if (kind != "persistent")
            warnings.Add("selection source 'ads': RETAIN variables are not detectable over ADS — add them via roots[] or a TMC.");
        return (new Selection(explicitRoots.Count > 0 ? "ads+roots" : "ads",
            new SnapshotReadRequest(explicitRoots, AdsPersistentRoots: true), [], warnings, new List<string>(explicitRoots)), null);
    }

    private string? SolutionDirectory()
    {
        string? sln = session.ActiveSolutionPath();
        return string.IsNullOrWhiteSpace(sln) ? null : Path.GetDirectoryName(sln);
    }

    // ---- save --------------------------------------------------------------------------

    public string Save(string? kind, IReadOnlyList<string>? roots, string? selection, string? tmcPath, string? path,
        string? amsNetId, int? adsPort)
    {
        int port = adsPort ?? DefaultAdsPort;
        var (sel, selError) = Select(selection ?? "auto", kind ?? "both", roots, tmcPath, amsNetId, port);
        if (sel is null) return selError!;

        DateTime now = _clock();
        string? solDir = SolutionDirectory();

        var read = session.ReadSnapshotLeaves(sel.Request, amsNetId, port);
        if (!read.Success) return AdsVariableCoordinator.FailedAt(read.Message, read.AmsNetId, read.AdsPort);

        var warnings = new List<string>(sel.Warnings);
        if (sel.TmcLeaves.Count > 0)
        {
            string? stale = StalenessError(sel.TmcLeaves, read);
            if (stale is not null) return stale;
            var tmcPaths = new HashSet<string>(sel.TmcLeaves.Select(l => l.Path), StringComparer.Ordinal);
            var onlyOnPlc = read.Leaves.Keys.Where(k => !tmcPaths.Contains(k) && !sel.Roots.Any(r => IsUnder(k, r))).Take(20).ToList();
            if (onlyOnPlc.Count > 0)
                warnings.Add($"{onlyOnPlc.Count}+ persistent leaves on the PLC are not in the TMC (rebuilt/activated?) and were added: " +
                             string.Join(", ", onlyOnPlc.Take(5)));
        }

        var roots2 = sel.Roots.Concat(read.AdsRoots).Distinct(StringComparer.Ordinal).ToList();
        if (read.Missing.Count > 0)
            warnings.Add("requested paths not on the PLC: " + string.Join(", ", read.Missing.Take(10)));
        if (read.Leaves.Count == 0)
            return $"[Error] Nothing to save: the selection ({sel.Source}) produced no readable leaf." +
                   (warnings.Count > 0 ? " " + string.Join(" ", warnings) : "");

        string target;
        if (string.IsNullOrWhiteSpace(path))
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ptc-xae-mcp", "snapshots");
            target = Path.Combine(dir, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{now:yyyyMMdd'T'HHmmss'Z'}_{read.AmsNetId.Replace(':', '_')}.json"));
        }
        else
        {
            target = path.Trim();
            if (!PathPolicy.IsAllowed(target, solDir, out string why)) return $"[Error] path refused: {why}";
            if (!target.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "[Error] path must end in .json.";
        }

        var doc = new SnapshotDocument(
            new SnapshotMeta(now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), read.AmsNetId, read.AdsPort,
                solDir is null ? null : Path.GetFileName(session.ActiveSolutionPath() ?? ""), null,
                roots2, read.Leaves.Count, read.Skipped),
            read.Leaves);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.WriteAllText(target, SnapshotFile.Serialize(doc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] Cannot write '{target}': {ex.Message}";
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            path = Path.GetFullPath(target),
            leafCount = read.Leaves.Count,
            selectionSource = sel.Source,
            skipped = read.Skipped.Take(ReportCap),
            skippedCount = read.Skipped.Count,
            warnings,
            amsNetId = read.AmsNetId,
            adsPort = read.AdsPort,
        }, JsonOpts);
    }

    private static bool IsUnder(string leaf, string root) =>
        leaf == root || (leaf.StartsWith(root, StringComparison.Ordinal) && leaf.Length > root.Length && leaf[root.Length] is '.' or '[');

    /// <summary>Every TMC leaf must exist on the PLC with the same type — otherwise the TMC is not what runs.</summary>
    private static string? StalenessError(IReadOnlyList<TmcLeaf> tmcLeaves, SnapshotReadResult read)
    {
        var skippedPaths = new HashSet<string>(read.Skipped.Select(s => s.Path), StringComparer.Ordinal);
        var bad = new List<string>();
        foreach (var l in tmcLeaves)
        {
            if (skippedPaths.Contains(l.Path)) continue;
            if (!read.Leaves.TryGetValue(l.Path, out var live)) { bad.Add($"{l.Path} (missing on the PLC)"); continue; }
            if (!string.Equals(AdsTypeNames.NormalizeTypeName(live.Type), l.Type, StringComparison.Ordinal))
                bad.Add($"{l.Path} (TMC {l.Type}, PLC {live.Type})");
        }
        return bad.Count == 0 ? null
            : $"[Error] TMC does not match the running PLC (rebuild/activate?): {bad.Count} leaf/leaves differ, e.g. " +
              string.Join("; ", bad.Take(5));
    }

    // ---- load helpers --------------------------------------------------------------------

    private (SnapshotDocument? Doc, string? Error) Load(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, $"[Error] {label} is required.");
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return (null, $"[Error] {label} is not a valid path: {ex.Message}"); }
        if (!PathPolicy.IsAllowed(full, SolutionDirectory(), out string why)) return (null, $"[Error] {label} refused: {why}");
        if (!File.Exists(full)) return (null, $"[Error] {label} '{full}' does not exist.");
        string json;
        try { json = File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (null, $"[Error] Cannot read '{full}': {ex.Message}"); }
        return SnapshotFile.TryParse(json, out var doc, out string error) ? (doc, null) : (null, $"[Error] {error}.");
    }

    // ---- compare -------------------------------------------------------------------------

    public string Compare(string? snapshotPath, string? otherPath, string? pathMapJson, string? tmcPath,
        string? amsNetId, int? adsPort, int? limit)
    {
        int port = adsPort ?? DefaultAdsPort;
        if (!SnapshotPathMap.TryParse(pathMapJson, out var map, out string mapError)) return "[Error] " + mapError;
        var (snap, error) = Load(snapshotPath, "snapshotPath");
        if (snap is null) return error!;

        IReadOnlyDictionary<string, SnapshotLeaf> live;
        string against;
        var warnings = new List<string>();
        string netId = amsNetId ?? "";
        if (!string.IsNullOrWhiteSpace(otherPath))
        {
            var (other, otherError) = Load(otherPath, "otherPath");
            if (other is null) return otherError!;
            live = other.Values;
            against = Path.GetFullPath(otherPath.Trim());
        }
        else
        {
            var (targets, _) = map.MapAll(snap.Values.Keys);
            var paths = targets.Keys.ToList();
            // Re-select the remanent set so leaves that exist on the PLC but not in the snapshot show as onlyOnPlc.
            var (sel, selError) = Select("auto", "both", null, tmcPath, amsNetId, port);
            if (sel is null) warnings.Add("live remanent set not re-selected: " + selError!.Replace("[Error] ", ""));
            var request = sel is null
                ? new SnapshotReadRequest(paths, AdsPersistentRoots: true)
                : new SnapshotReadRequest(paths.Concat(sel.Request.Paths).Distinct(StringComparer.Ordinal).ToList(), sel.Request.AdsPersistentRoots);
            var read = session.ReadSnapshotLeaves(request, amsNetId, port);
            if (!read.Success) return AdsVariableCoordinator.FailedAt(read.Message, read.AmsNetId, read.AdsPort);
            live = read.Leaves;
            against = $"{read.AmsNetId}:{read.AdsPort}";
            netId = read.AmsNetId;
        }

        var diff = SnapshotDiff.Compare(snap.Values, live, map);
        int cap = Math.Clamp(limit ?? ReportCap, 1, 5000);
        return JsonSerializer.Serialize(new
        {
            success = true,
            equal = diff.Equal,
            against,
            equalCount = diff.EqualCount,
            differenceCount = diff.Differences.Count,
            differences = diff.Differences.Take(cap).Select(d => new { path = d.Path, snapshotPath = d.SnapshotPath, type = d.Type, snapshot = d.Snapshot, live = d.Live }),
            onlyInSnapshot = diff.OnlyInSnapshot.Take(cap),
            onlyInSnapshotCount = diff.OnlyInSnapshot.Count,
            onlyOnPlc = diff.OnlyOnPlc.Take(cap),
            onlyOnPlcCount = diff.OnlyOnPlc.Count,
            typeMismatches = diff.TypeMismatches.Take(cap),
            conflicts = diff.Conflicts,
            warnings,
            amsNetId = netId,
        }, JsonOpts);
    }

    // ---- restore -------------------------------------------------------------------------

    public string Restore(string? snapshotPath, bool dryRun, string? planHash, string? pathMapJson,
        IReadOnlyList<string>? include, string? amsNetId, int? adsPort)
    {
        int port = adsPort ?? DefaultAdsPort;
        if (!SnapshotPathMap.TryParse(pathMapJson, out var map, out string mapError)) return "[Error] " + mapError;
        var (snap, error) = Load(snapshotPath, "snapshotPath");
        if (snap is null) return error!;
        var includeList = include?.Select(i => (i ?? "").Trim()).Where(i => i.Length > 0).ToList();

        var (plan, planError, netId, plcPort) = BuildLivePlan(snap, map, includeList, amsNetId, port);
        if (plan is null) return planError!;

        if (dryRun)
        {
            var state = session.GetAdsRunState(amsNetId, port);
            return JsonSerializer.Serialize(new
            {
                success = true,
                dryRun = true,
                planHash = plan.PlanHash,
                target = $"{netId}:{plcPort}",
                plcState = state.Success ? state.State : "unknown",
                writeCount = plan.Writes.Count,
                unchanged = plan.Unchanged,
                writes = plan.Writes.Take(ReportCap).Select(w => new { path = w.Path, type = w.Type, current = w.Old.Value, snapshot = w.New.Value }),
                skipCount = plan.Skips.Count,
                skips = plan.Skips.Take(ReportCap),
                note = "Nothing was written. To restore, call again with dryRun=false and this planHash; a human then confirms in the host window.",
            }, JsonOpts);
        }

        if (string.IsNullOrWhiteSpace(planHash))
            return "[Error] planHash is required when dryRun=false — run a dry run first and pass its planHash.";
        if (!string.Equals(planHash.Trim(), plan.PlanHash, StringComparison.OrdinalIgnoreCase))
            return "[Error] planHash does not match the current plan (the PLC, the snapshot, pathMap or include changed) — run a dry run again.";

        var runState = session.GetAdsRunState(amsNetId, port);
        var (proposal, proposeError) = restore.Propose(netId, plcPort, Path.GetFullPath(snapshotPath!.Trim()), snap.Meta.Utc,
            runState.Success ? runState.State : "unknown", plan,
            () =>
            {
                var (fresh, err, _, _) = BuildLivePlan(snap, map, includeList, amsNetId, port);
                return (fresh, err);
            });
        if (proposal is null) return proposeError!;

        return JsonSerializer.Serialize(new
        {
            success = true,
            restoreId = proposal.RestoreId,
            status = "pending",
            expiresUtc = proposal.ExpiresUtc,
            writeCount = plan.Writes.Count,
            note = "Waiting for a human to confirm in the host window (Approvals). Poll twincat_persistent_snapshot_restore_status.",
        }, JsonOpts);
    }

    private (RestorePlan? Plan, string? Error, string NetId, int Port) BuildLivePlan(
        SnapshotDocument snap, SnapshotPathMap map, IReadOnlyCollection<string>? include, string? amsNetId, int port)
    {
        var (targets, _) = map.MapAll(snap.Values.Keys);
        var read = session.ReadSnapshotLeaves(new SnapshotReadRequest(targets.Keys.ToList()), amsNetId, port);
        if (!read.Success)
            return (null, AdsVariableCoordinator.FailedAt(read.Message, read.AmsNetId, read.AdsPort), read.AmsNetId, read.AdsPort);
        var skipped = read.Skipped.GroupBy(s => s.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.Ordinal);
        string target = $"{read.AmsNetId}:{read.AdsPort}";
        return (SnapshotDiff.BuildPlan(target, snap.Values, read.Leaves, skipped, map, include), null, read.AmsNetId, read.AdsPort);
    }

    // ---- status -------------------------------------------------------------------------

    public string RestoreStatus(string? restoreId) => RestoreStatus(restore, restoreId);

    /// <summary>Registry read only (no session): the status poll must not wait for the dispatcher lock.</summary>
    public static string RestoreStatus(SnapshotRestoreCoordinator restore, string? restoreId)
    {
        var p = restore.Get(restoreId);
        if (p is null) return $"[Error] Unknown restoreId '{restoreId}'.";
        var report = p.Report;
        return JsonSerializer.Serialize(new
        {
            success = true,
            restoreId = p.RestoreId,
            status = SnapshotRestoreCoordinator.StateName(p.State),
            reason = p.Reason,
            target = $"{p.AmsNetId}:{p.AdsPort}",
            plcState = p.PlcState,
            snapshot = p.SnapshotPath,
            createdUtc = p.CreatedUtc,
            expiresUtc = p.ExpiresUtc,
            writeCount = p.Plan.Writes.Count,
            report = report is null ? null : new
            {
                written = report.Count(r => r.Outcome == "written"),
                failed = report.Count(r => r.Outcome == "failed"),
                skipped = report.Count(r => r.Outcome == "skipped"),
                items = report.Take(ReportCap * 2),
                truncated = report.Count > ReportCap * 2,
            },
        }, JsonOpts);
    }
}
