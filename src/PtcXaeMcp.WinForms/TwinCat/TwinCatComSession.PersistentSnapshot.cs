#if TWINCAT
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- persistent-snapshot ------------------------
    //
    // Reads / writes the leaves of PLC symbols with ADS sum commands (SumSymbolRead.ReadRaw /
    // SumSymbolWrite.WriteRaw) over ONE AdsClient and ONE symbol load per call. The sum commands move
    // raw bytes and SnapshotLeafCodec decodes / encodes them, so the result does not depend on the
    // SDK's marshalling. Batches of <= SumBatchSize symbols; a refused write batch is halved and
    // retried, down to single writes. Every written leaf is read back (D-3).
    // Everything decidable without a PLC lives outside this #if in the Snapshot* classes.

#if !TWINCAT
    public SnapshotReadResult ReadSnapshotLeaves(SnapshotReadRequest request, string? amsNetId, int adsPort)
        => new(false, new Dictionary<string, SnapshotLeaf>(), [], [], [], amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public SnapshotWriteResult WriteSnapshotLeaves(IReadOnlyList<SnapshotLeafWrite> writes, string? amsNetId, int adsPort)
        => new(false, [], amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public AdsSymbolListResult ListAdsSymbols(string? root, int maxDepth, bool persistentOnly, int limit, string? amsNetId, int adsPort)
        => new(false, [], false, amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);

    public AdsRunStateResult GetAdsRunState(string? amsNetId, int adsPort)
        => new(false, "unknown", amsNetId ?? "local", adsPort, TwinCatNotBuilt.Message);
#else
    private const int SumBatchSize = 100;
    private const int SnapshotMaxDepth = 32;

    private sealed record SnapLeaf(ISymbol Symbol, string Type, string WireType, int Size, IEnumType? Enum);

    // The symbol table, loaded once per call. Flat gives every exported variable; the VirtualTree is
    // only built when a path is not below a flat symbol (FB members, see LoadSymbol).
    private sealed class SnapshotSymbols
    {
        private readonly AdsClient _client;
        private List<ISymbol>? _flat;
        private List<ISymbol>? _tree;
        public string? LoadError { get; private set; }

        public SnapshotSymbols(AdsClient client) => _client = client;

        public IReadOnlyList<ISymbol> Roots => _flat ??= Load(SymbolsLoadMode.Flat);

        private List<ISymbol> Load(SymbolsLoadMode mode)
        {
            var loader = SymbolLoaderFactory.Create(_client, new SymbolLoaderSettings(mode));
            var r = loader.GetSymbolsAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!r.Succeeded) { LoadError = $"SymbolLoader failed: {r.ErrorCode}"; return []; }
            return (r.Symbols ?? Enumerable.Empty<ISymbol>()).ToList();
        }

        public ISymbol? Resolve(string path)
        {
            var hit = Descend(Roots, path);
            if (hit is not null) return hit;
            _tree ??= Load(SymbolsLoadMode.VirtualTree);
            return Descend(_tree, path);
        }

        private static ISymbol? Descend(IEnumerable<ISymbol> level, string path)
        {
            foreach (var s in level)
            {
                string ip = s.InstancePath;
                if (string.Equals(ip, path, StringComparison.OrdinalIgnoreCase)) return s;
                if (path.Length > ip.Length && path.StartsWith(ip, StringComparison.OrdinalIgnoreCase) &&
                    path[ip.Length] is '.' or '[')
                {
                    IEnumerable<ISymbol>? children = s switch
                    {
                        IStructInstance st => st.MemberInstances,
                        IArrayInstance arr => arr.Elements,
                        _ => null,
                    };
                    if (children is not null)
                    {
                        var found = Descend(children, path);
                        if (found is not null) return found;
                    }
                }
            }
            return null;
        }
    }

    private static SnapLeaf? ClassifySnapshotLeaf(ISymbol sym)
    {
        string name = NormalizeTypeName(sym.TypeName);
        if (sym.DataType is IEnumType en)
        {
            string baseName = NormalizeTypeName(en.BaseTypeName);
            return SnapshotLeafCodec.FixedSize(baseName) > 0 && !SnapshotValues.IsTimeType(baseName)
                ? new SnapLeaf(sym, name, baseName, SnapshotLeafCodec.FixedSize(baseName), en) : null;
        }
        string t = name;
        var dt = sym.DataType;
        for (int i = 0; i < 8; i++)
        {
            if (SnapshotLeafCodec.IsSupported(t))
                return new SnapLeaf(sym, t, t, SnapshotLeafCodec.FixedSize(t) > 0 ? SnapshotLeafCodec.FixedSize(t) : (int)sym.Size, null);
            if (dt is IAliasType alias) { t = NormalizeTypeName(alias.BaseTypeName); dt = alias.BaseType; }
            else break;
        }
        return null;
    }

    private static bool IsContainer(ISymbol s) => s is IStructInstance || s is IArrayInstance;

    private static void FlattenSnapshot(ISymbol sym, List<SnapLeaf> leaves, List<SnapshotSkip> skipped, int depth)
    {
        string path = sym.InstancePath;
        if (depth > SnapshotMaxDepth) { skipped.Add(new(path, "nesting deeper than " + SnapshotMaxDepth)); return; }
        if (sym.IsProperty) { skipped.Add(new(path, "PROPERTY")); return; }
        if (sym.IsPointer || sym.IsReference) { skipped.Add(new(path, "POINTER/REFERENCE")); return; }

        var leaf = IsContainer(sym) ? null : ClassifySnapshotLeaf(sym);
        if (leaf is not null)
        {
            if (IsInputMapped(sym)) { skipped.Add(new(path, "input-mapped (AT %I*)")); return; }
            if (sym.IsReadOnly) { skipped.Add(new(path, "read-only / CONSTANT")); return; }
            leaves.Add(leaf);
            return;
        }

        if (sym is IStructInstance st)
        {
            foreach (var m in st.MemberInstances) FlattenSnapshot(m, leaves, skipped, depth + 1);
            return;
        }
        if (sym is IArrayInstance arr)
        {
            foreach (var e in arr.Elements) FlattenSnapshot(e, leaves, skipped, depth + 1);
            return;
        }
        skipped.Add(new(path, $"unsupported type '{sym.TypeName}'"));
    }

    // Stops at the first persistent ancestor, like TwinCatAdsTool; RETAIN is not visible here.
    private static void CollectPersistentRoots(IEnumerable<ISymbol> level, List<ISymbol> roots, int depth)
    {
        if (depth > SnapshotMaxDepth) return;
        foreach (var s in level)
        {
            if (s.IsPersistent) { roots.Add(s); continue; }
            if (s is IStructInstance st) CollectPersistentRoots(st.MemberInstances, roots, depth + 1);
            else if (s is IArrayInstance arr) CollectPersistentRoots(arr.Elements, roots, depth + 1);
        }
    }

    private static Dictionary<string, SnapshotLeaf> ReadSnapshotValues(
        AdsClient client, IReadOnlyList<SnapLeaf> leaves, List<SnapshotSkip> skipped)
    {
        var values = new Dictionary<string, SnapshotLeaf>(StringComparer.Ordinal);
        foreach (var chunk in leaves.Chunk(SumBatchSize))
        {
            var byInstance = chunk.ToDictionary(l => l.Symbol.InstancePath, StringComparer.Ordinal);
            ResultSumValues2<ISymbol>? res;
            try { res = new SumSymbolRead(client, chunk.Select(l => l.Symbol).ToList()).ReadRaw(); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                foreach (var l in chunk) skipped.Add(new(l.Symbol.InstancePath, "unreadable: " + ex.Message));
                continue;
            }
            foreach (var vr in res.ValueResults)
            {
                string path = vr.Source.InstancePath;
                var leaf = byInstance[path];
                if (vr.Failed) { skipped.Add(new(path, $"unreadable: {vr.ErrorCode}")); continue; }
                try
                {
                    byte[]? raw = vr.Value switch
                    {
                        ReadOnlyMemory<byte> m => m.ToArray(),
                        Memory<byte> m => m.ToArray(),
                        byte[] b => b,
                        _ => null,
                    };
                    if (raw is null) { skipped.Add(new(path, "unreadable: unexpected raw value " + vr.Value?.GetType().Name)); continue; }
                    ReadOnlySpan<byte> bytes = raw;
                    if (bytes.Length < leaf.Size && SnapshotLeafCodec.FixedSize(leaf.WireType) > 0)
                    { skipped.Add(new(path, $"unreadable: short read ({bytes.Length} of {leaf.Size} bytes)")); continue; }
                    var json = SnapshotLeafCodec.Decode(leaf.WireType, bytes);
                    string? name = null;
                    if (leaf.Enum is not null && json.TryGetInt64(out long num))
                        name = leaf.Enum.EnumValues
                            .FirstOrDefault(ev => Convert.ToInt64(ev.Value, System.Globalization.CultureInfo.InvariantCulture) == num)?.Name;
                    values[path] = new SnapshotLeaf(leaf.Type, json, name, SnapshotLeafCodec.Display(leaf.Type, json));
                }
                catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException)
                {
                    skipped.Add(new(path, "unreadable: " + ex.Message));
                }
            }
        }
        return values;
    }

    public SnapshotReadResult ReadSnapshotLeaves(SnapshotReadRequest request, string? amsNetId, int adsPort)
    {
        string netId = ResolveAmsNetId(amsNetId);
        var empty = new Dictionary<string, SnapshotLeaf>();
        try
        {
            using var client = new AdsClient();
            client.Connect(netId, adsPort);
            var symbols = new SnapshotSymbols(client);
            _ = symbols.Roots;
            if (symbols.LoadError is not null)
                return new(false, empty, [], [], [], netId, adsPort, "[Error] " + symbols.LoadError);

            var leaves = new List<SnapLeaf>();
            var skipped = new List<SnapshotSkip>();
            var missing = new List<string>();
            var adsRoots = new List<string>();

            if (request.AdsPersistentRoots)
            {
                var roots = new List<ISymbol>();
                CollectPersistentRoots(symbols.Roots, roots, 0);
                foreach (var r in roots)
                {
                    adsRoots.Add(r.InstancePath);
                    FlattenSnapshot(r, leaves, skipped, 0);
                }
            }
            foreach (string path in request.Paths)
            {
                var sym = symbols.Resolve(path);
                if (sym is null) { missing.Add(path); continue; }
                FlattenSnapshot(sym, leaves, skipped, 0);
            }

            var distinct = leaves.GroupBy(l => l.Symbol.InstancePath, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var values = ReadSnapshotValues(client, distinct, skipped);
            return new(true, values, skipped, missing, adsRoots, netId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new(false, empty, [], [], [], netId, adsPort, $"[Error] ReadSnapshotLeaves failed: {ex.Message}");
        }
    }

    public SnapshotWriteResult WriteSnapshotLeaves(IReadOnlyList<SnapshotLeafWrite> writes, string? amsNetId, int adsPort)
    {
        string netId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(netId, adsPort);
            var symbols = new SnapshotSymbols(client);
            _ = symbols.Roots;
            if (symbols.LoadError is not null)
                return new(false, [], netId, adsPort, "[Error] " + symbols.LoadError);

            var items = new Dictionary<string, SnapshotWriteItem>(StringComparer.Ordinal);
            var ready = new List<(SnapLeaf Leaf, byte[] Bytes, SnapshotLeafWrite Write)>();
            foreach (var w in writes)
            {
                var sym = symbols.Resolve(w.Path);
                var leaf = sym is null || IsContainer(sym) ? null : ClassifySnapshotLeaf(sym);
                if (leaf is null) { items[w.Path] = new(w.Path, "failed", null, "not a writable leaf on the PLC"); continue; }
                if (IsInputMapped(leaf.Symbol)) { items[w.Path] = new(w.Path, "failed", null, "input-mapped (AT %I*) — write refused"); continue; }
                if (leaf.Symbol.IsReadOnly) { items[w.Path] = new(w.Path, "failed", null, "read-only / CONSTANT"); continue; }
                try { ready.Add((leaf, SnapshotLeafCodec.Encode(leaf.WireType, w.Value.Value, leaf.Size), w)); }
                catch (FormatException ex) { items[w.Path] = new(w.Path, "failed", null, ex.Message); }
            }

            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chunk in ready.Chunk(SumBatchSize))
                WriteChunk(client, chunk, errors);

            // D-3: the read-back decides, never the intended value.
            var skipped = new List<SnapshotSkip>();
            var back = ReadSnapshotValues(client, ready.Select(r => r.Leaf).ToList(), skipped);
            foreach (var (leaf, _, w) in ready)
            {
                string path = leaf.Symbol.InstancePath;
                back.TryGetValue(path, out var read);
                if (errors.TryGetValue(path, out string? err))
                    items[w.Path] = new(w.Path, "failed", read, err);
                else if (read is null)
                    items[w.Path] = new(w.Path, "failed", null, "read-back failed");
                else if (!SnapshotValues.ValueEquals(read.Value, w.Value.Value))
                    items[w.Path] = new(w.Path, "failed", read, "read-back differs from the written value (overwritten by the program?)");
                else
                    items[w.Path] = new(w.Path, "written", read, "OK (read back)");
            }
            return new(true, writes.Select(w => items[w.Path]).ToList(), netId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new(false, [], netId, adsPort, $"[Error] WriteSnapshotLeaves failed: {ex.Message}");
        }
    }

    // One sum write; if the whole batch is refused, halve and retry, down to single writes.
    private static void WriteChunk(AdsClient client,
        (SnapLeaf Leaf, byte[] Bytes, SnapshotLeafWrite Write)[] chunk, Dictionary<string, string> errors)
    {
        try
        {
            var syms = chunk.Select(c => c.Leaf.Symbol).ToList();
            var data = chunk.Select(c => (ReadOnlyMemory<byte>)c.Bytes).ToList();
            var res = new SumSymbolWrite(client, syms).WriteRaw(data);
            if (res.OverallSucceeded || res.SubCommandCount == chunk.Length && res.SubErrors is { Length: > 0 })
            {
                for (int i = 0; i < chunk.Length; i++)
                    if (res.SubErrors is { } e && i < e.Length && e[i] != AdsErrorCode.NoError)
                        errors[chunk[i].Leaf.Symbol.InstancePath] = $"ADS error {e[i]}";
                return;
            }
            Retry(client, chunk, errors, res.ErrorCode.ToString());
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            Retry(client, chunk, errors, ex.Message);
        }
    }

    private static void Retry(AdsClient client, (SnapLeaf Leaf, byte[] Bytes, SnapshotLeafWrite Write)[] chunk,
        Dictionary<string, string> errors, string why)
    {
        if (chunk.Length == 1)
        {
            errors[chunk[0].Leaf.Symbol.InstancePath] = "write refused: " + why;
            return;
        }
        int half = chunk.Length / 2;
        WriteChunk(client, chunk[..half], errors);
        WriteChunk(client, chunk[half..], errors);
    }

    public AdsSymbolListResult ListAdsSymbols(string? root, int maxDepth, bool persistentOnly, int limit, string? amsNetId, int adsPort)
    {
        string netId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(netId, adsPort);
            var symbols = new SnapshotSymbols(client);
            IEnumerable<ISymbol> start = symbols.Roots;
            if (symbols.LoadError is not null)
                return new(false, [], false, netId, adsPort, "[Error] " + symbols.LoadError);

            if (!string.IsNullOrWhiteSpace(root))
            {
                var r = symbols.Resolve(root);
                if (r is null) return new(false, [], false, netId, adsPort, $"[Error] Symbol '{root}' not found in the PLC symbol table.");
                start = r switch
                {
                    IStructInstance st => st.MemberInstances,
                    IArrayInstance arr => arr.Elements,
                    _ => [r],
                };
            }

            var list = new List<AdsSymbolEntry>();
            bool truncated = false;
            void Walk(IEnumerable<ISymbol> level, int depth)
            {
                foreach (var s in level)
                {
                    if (truncated) return;
                    if (!persistentOnly || s.IsPersistent)
                    {
                        if (list.Count >= limit) { truncated = true; return; }
                        list.Add(new AdsSymbolEntry(s.InstancePath, s.TypeName, s.Size, s.IsPersistent, s.IsReadOnly,
                            string.IsNullOrWhiteSpace(s.Comment) ? null : s.Comment));
                        if (persistentOnly) continue; // the subtree of a persistent symbol is persistent as a whole
                    }
                    if (depth + 1 < maxDepth || persistentOnly && depth + 1 < Math.Max(maxDepth, SnapshotMaxDepth))
                    {
                        if (s is IStructInstance st) Walk(st.MemberInstances, depth + 1);
                        else if (s is IArrayInstance arr) Walk(arr.Elements, depth + 1);
                    }
                }
            }
            Walk(start, 0);
            return new(true, list, truncated, netId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new(false, [], false, netId, adsPort, $"[Error] ListAdsSymbols failed: {ex.Message}");
        }
    }

    public AdsRunStateResult GetAdsRunState(string? amsNetId, int adsPort)
    {
        string netId = ResolveAmsNetId(amsNetId);
        try
        {
            using var client = new AdsClient();
            client.Connect(netId, adsPort);
            var state = client.ReadState();
            return new(true, state.AdsState.ToString(), netId, adsPort, "OK");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return new(false, "unknown", netId, adsPort, $"[Error] GetAdsRunState failed: {ex.Message}");
        }
    }
#endif
}
