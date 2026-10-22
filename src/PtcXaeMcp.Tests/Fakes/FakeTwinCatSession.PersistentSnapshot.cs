using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

// persistent-snapshot: a small in-memory PLC. Leaves live in PlcLeaves; a path request matches the
// leaf itself or its subtree ('.' / '[' boundary). Writes land (and are read back) unless listed in
// WriteDoesNotLand / WriteFails.
internal sealed partial class FakeTwinCatSession
{
    public Dictionary<string, SnapshotLeaf> PlcLeaves { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PlcSkipped { get; } = new(StringComparer.Ordinal);
    public List<string> PlcAdsRoots { get; } = new();
    public HashSet<string> WriteFails { get; } = new(StringComparer.Ordinal);
    public HashSet<string> WriteDoesNotLand { get; } = new(StringComparer.Ordinal);
    public string PlcRunState { get; set; } = "Run";
    public string? SnapshotReadError { get; set; }
    public Action? OnWriteSnapshotLeaves { get; set; }

    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public SnapshotReadResult ReadSnapshotLeaves(SnapshotReadRequest request, string? amsNetId, int adsPort)
    {
        lock (CallLog)
            CallLog.Add($"ReadSnapshotLeaves(paths=[{string.Join(",", request.Paths)}], adsRoots={request.AdsPersistentRoots}, net={amsNetId ?? "<null>"}, port={adsPort})");
        if (SnapshotReadError is not null)
            return new(false, new Dictionary<string, SnapshotLeaf>(), [], [], [], amsNetId ?? "1.2.3.4.1.1", adsPort, SnapshotReadError);

        var paths = request.Paths.ToList();
        var adsRoots = request.AdsPersistentRoots ? PlcAdsRoots.ToList() : new List<string>();
        paths.AddRange(adsRoots);
        var leaves = new Dictionary<string, SnapshotLeaf>(StringComparer.Ordinal);
        var skipped = new List<SnapshotSkip>();
        var missing = new List<string>();
        foreach (string path in paths)
        {
            bool any = false;
            foreach (var (p, leaf) in PlcLeaves)
                if (p == path || (p.StartsWith(path, StringComparison.Ordinal) && p.Length > path.Length && p[path.Length] is '.' or '['))
                { leaves[p] = leaf; any = true; }
            foreach (var (p, why) in PlcSkipped)
                if (p == path || (p.StartsWith(path, StringComparison.Ordinal) && p.Length > path.Length && p[path.Length] is '.' or '['))
                { skipped.Add(new(p, why)); any = true; }
            if (!any) missing.Add(path);
        }
        return new(true, leaves, skipped, missing, adsRoots, amsNetId ?? "1.2.3.4.1.1", adsPort, "OK");
    }

    public SnapshotWriteResult WriteSnapshotLeaves(IReadOnlyList<SnapshotLeafWrite> writes, string? amsNetId, int adsPort)
    {
        lock (CallLog)
            CallLog.Add($"WriteSnapshotLeaves(n={writes.Count}, net={amsNetId ?? "<null>"}, port={adsPort})");
        OnWriteSnapshotLeaves?.Invoke();
        var items = new List<SnapshotWriteItem>();
        foreach (var w in writes)
        {
            if (WriteFails.Contains(w.Path)) { items.Add(new(w.Path, "failed", null, "ADS error 1793")); continue; }
            if (!WriteDoesNotLand.Contains(w.Path)) PlcLeaves[w.Path] = w.Value;
            items.Add(new(w.Path, WriteDoesNotLand.Contains(w.Path) ? "failed" : "written", PlcLeaves[w.Path],
                WriteDoesNotLand.Contains(w.Path) ? "read-back differs (overwritten by the program?)" : "OK"));
        }
        return new(true, items, amsNetId ?? "1.2.3.4.1.1", adsPort, "OK");
    }

    public AdsSymbolListResult ListAdsSymbols(string? root, int maxDepth, bool persistentOnly, int limit, string? amsNetId, int adsPort)
    {
        CallLog.Add($"ListAdsSymbols(root={root ?? "<null>"}, depth={maxDepth}, persistentOnly={persistentOnly}, limit={limit})");
        return new(true, [], false, amsNetId ?? "1.2.3.4.1.1", adsPort, "OK");
    }

    public AdsRunStateResult GetAdsRunState(string? amsNetId, int adsPort)
        => new(true, PlcRunState, amsNetId ?? "1.2.3.4.1.1", adsPort, "OK");
}
