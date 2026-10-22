using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed record SnapshotDifference(string Path, string SnapshotPath, string Type, JsonElement Snapshot, JsonElement Live);
internal sealed record SnapshotTypeMismatch(string Path, string SnapshotPath, string SnapshotType, string LiveType, string Reason);

internal sealed record SnapshotCompareResult(
    IReadOnlyList<SnapshotDifference> Differences,
    IReadOnlyList<string> OnlyInSnapshot,
    IReadOnlyList<string> OnlyOnPlc,
    IReadOnlyList<SnapshotTypeMismatch> TypeMismatches,
    IReadOnlyList<string> Conflicts,
    int EqualCount)
{
    public bool Equal => Differences.Count == 0 && OnlyInSnapshot.Count == 0 && OnlyOnPlc.Count == 0 &&
                         TypeMismatches.Count == 0 && Conflicts.Count == 0;
}

internal sealed record RestoreWrite(string Path, string SnapshotPath, string Type, SnapshotLeaf Old, SnapshotLeaf New);
internal sealed record RestoreSkip(string Path, string Reason);

internal sealed record RestorePlan(
    IReadOnlyList<RestoreWrite> Writes,
    IReadOnlyList<RestoreSkip> Skips,
    int Unchanged,
    string PlanHash);

/// <summary>
/// persistent-snapshot: pure compare and restore-plan logic over two flat leaf maps. Types are
/// decided by the PLC as it is now (<see cref="SnapshotValues.TryCoerce"/>).
/// </summary>
internal static class SnapshotDiff
{
    public static SnapshotCompareResult Compare(
        IReadOnlyDictionary<string, SnapshotLeaf> snapshot,
        IReadOnlyDictionary<string, SnapshotLeaf> live,
        SnapshotPathMap map,
        Func<string, bool>? liveIsEnum = null)
    {
        var (targetToSource, conflicts) = map.MapAll(snapshot.Keys);
        var differences = new List<SnapshotDifference>();
        var onlyInSnapshot = new List<string>();
        var typeMismatches = new List<SnapshotTypeMismatch>();
        int equal = 0;

        foreach (var (target, source) in targetToSource.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!live.TryGetValue(target, out var liveLeaf)) { onlyInSnapshot.Add(source); continue; }
            var snap = snapshot[source];
            bool isEnum = liveIsEnum?.Invoke(target) ?? !SnapshotValues.IsSupportedPrimitive(liveLeaf.Type);
            if (!SnapshotValues.TryCoerce(snap, liveLeaf.Type, isEnum, out var coerced, out string reason))
            { typeMismatches.Add(new(target, source, snap.Type, liveLeaf.Type, reason)); continue; }
            if (SnapshotValues.ValueEquals(coerced.Value, liveLeaf.Value)) equal++;
            else differences.Add(new(target, source, liveLeaf.Type, coerced.Value, liveLeaf.Value));
        }

        var onlyOnPlc = live.Keys.Where(k => !targetToSource.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new(differences, onlyInSnapshot, onlyOnPlc, typeMismatches, conflicts, equal);
    }

    /// <param name="liveSkipped">Leaves the PLC side refuses to write (read-only, input-mapped, …) → reason.</param>
    /// <param name="include">Optional subset (live or snapshot leaf paths); null = everything.</param>
    public static RestorePlan BuildPlan(
        string target,
        IReadOnlyDictionary<string, SnapshotLeaf> snapshot,
        IReadOnlyDictionary<string, SnapshotLeaf> live,
        IReadOnlyDictionary<string, string> liveSkipped,
        SnapshotPathMap map,
        IReadOnlyCollection<string>? include = null)
    {
        var includeSet = include is { Count: > 0 } ? new HashSet<string>(include, StringComparer.Ordinal) : null;
        var (targetToSource, conflicts) = map.MapAll(snapshot.Keys);
        var writes = new List<RestoreWrite>();
        var skips = new List<RestoreSkip>();
        int unchanged = 0;

        foreach (string c in conflicts) skips.Add(new("(pathMap)", c));

        foreach (var (tgt, source) in targetToSource.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (includeSet is not null && !includeSet.Contains(tgt) && !includeSet.Contains(source)) continue;
            if (liveSkipped.TryGetValue(tgt, out string? why)) { skips.Add(new(tgt, why)); continue; }
            if (!live.TryGetValue(tgt, out var liveLeaf)) { skips.Add(new(tgt, "not on the PLC (missing or index out of bounds)")); continue; }

            var snap = snapshot[source];
            if (!SnapshotValues.TryCoerce(snap, liveLeaf.Type, !SnapshotValues.IsSupportedPrimitive(liveLeaf.Type),
                    out var coerced, out string reason))
            { skips.Add(new(tgt, reason)); continue; }

            if (SnapshotValues.ValueEquals(coerced.Value, liveLeaf.Value)) { unchanged++; continue; }
            writes.Add(new(tgt, source, liveLeaf.Type, liveLeaf, coerced));
        }

        return new(writes, skips, unchanged, Hash(target, writes));
    }

    /// <summary>Hash over (target, ordered writes incl. old and new value): binds what a human reviewed to what runs.</summary>
    public static string Hash(string target, IReadOnlyList<RestoreWrite> writes)
    {
        var sb = new StringBuilder(target).Append('\n');
        foreach (var w in writes.OrderBy(w => w.Path, StringComparer.Ordinal))
            sb.Append(w.Path).Append('\t').Append(w.Type).Append('\t')
              .Append(w.Old.Value.GetRawText()).Append('\t').Append(w.New.Value.GetRawText()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
