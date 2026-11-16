#if TWINCAT
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- signal testbench: ADS notifications ------------------------------------------------
    //
    // The symbols are resolved once over one AdsClient (the snapshot reader's symbol table and leaf classification, so
    // a symbol means the same here as in twincat_persistent_snapshot_*), their values read once with a sum command, and
    // then one on-change notification is registered per symbol. The returned watch owns the client.

#if !TWINCAT
    public AdsWatchOpenResult OpenAdsWatch(IReadOnlyList<string> symbols, string? amsNetId, int adsPort, int cycleMs)
        => new(false, null, amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);
#else
    public AdsWatchOpenResult OpenAdsWatch(IReadOnlyList<string> symbols, string? amsNetId, int adsPort, int cycleMs)
    {
        string netId = ResolveAmsNetId(amsNetId);
        AdsClient? client = null;
        AdsNotificationWatch? watch = null;
        try
        {
            client = new AdsClient();
            client.Connect(netId, adsPort);
            var table = new SnapshotSymbols(client);
            _ = table.Roots;
            if (table.LoadError is not null)
                return Fail(client, netId, adsPort, "[Error] " + table.LoadError);

            var leaves = new List<SnapLeaf>();
            var problems = new List<string>();
            foreach (string path in symbols)
            {
                var sym = table.Resolve(path);
                if (sym is null) { problems.Add($"'{path}' is not in the PLC symbol table"); continue; }
                if (sym.IsProperty || sym.IsPointer || sym.IsReference) { problems.Add($"'{path}' is a PROPERTY, POINTER or REFERENCE"); continue; }
                var leaf = IsContainer(sym) ? null : ClassifySnapshotLeaf(sym);
                if (leaf is null)
                {
                    problems.Add($"'{path}' is not a scalar (BOOL, number, time type, STRING or ENUM; its type is {sym.TypeName}) — " +
                                 "name a member or element of it");
                    continue;
                }
                leaves.Add(leaf);
            }
            if (problems.Count > 0)
                return Fail(client, netId, adsPort, "[Error] " + string.Join("; ", problems) + ".");

            var skipped = new List<SnapshotSkip>();
            var initial = ReadSnapshotValues(client, leaves, skipped);
            var watched = new List<WatchedSymbol>();
            var targets = new List<WatchTarget>();
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                if (!initial.TryGetValue(leaf.Symbol.InstancePath, out var value))
                {
                    string why = skipped.FirstOrDefault(s => string.Equals(s.Path, leaf.Symbol.InstancePath, StringComparison.OrdinalIgnoreCase))?.Reason
                                 ?? "no value";
                    return Fail(client, netId, adsPort, $"[Error] '{symbols[i]}' could not be read: {why}.");
                }
                watched.Add(new WatchedSymbol(symbols[i], leaf.Type, leaf.WireType, leaf.Enum is not null,
                    new SignalReading(value.Value, value.Name)));
                targets.Add(new WatchTarget(leaf.Symbol.InstancePath, leaf.Size, bytes => DecodeWatched(leaf, bytes)));
            }

            watch = new AdsNotificationWatch(client, watched, targets);
            string? startFailure = watch.Start(cycleMs);
            if (startFailure is not null)
            {
                watch.Dispose();
                return new(false, null, netId, adsPort,
                    $"[Error] {startFailure}. The router or the runtime may allow only a limited number of notifications.");
            }
            return new(true, watch, netId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            watch?.Dispose();
            if (watch is null) client?.Dispose();
            return new(false, null, netId, adsPort, $"[Error] OpenAdsWatch failed: {ex.Message}");
        }
    }

    private static AdsWatchOpenResult Fail(AdsClient client, string netId, int adsPort, string message)
    {
        client.Dispose();
        return new(false, null, netId, adsPort, message);
    }

    private static SignalReading DecodeWatched(SnapLeaf leaf, byte[] bytes)
    {
        if (bytes.Length < leaf.Size && SnapshotLeafCodec.FixedSize(leaf.WireType) > 0)
            throw new FormatException($"short value ({bytes.Length} of {leaf.Size} bytes)");
        var json = SnapshotLeafCodec.Decode(leaf.WireType, bytes);
        string? name = null;
        if (leaf.Enum is not null && json.TryGetInt64(out long number))
            name = leaf.Enum.EnumValues
                .FirstOrDefault(ev => Convert.ToInt64(ev.Value, System.Globalization.CultureInfo.InvariantCulture) == number)?.Name;
        return new SignalReading(json, name);
    }
#endif
}
