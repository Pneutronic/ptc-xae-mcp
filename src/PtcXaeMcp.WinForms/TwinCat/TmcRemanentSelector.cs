using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A remanent leaf the compiler resolved for one instance. <c>Kind</c> is "persistent" or "retain".</summary>
internal sealed record TmcLeaf(string Path, string Type, bool IsEnum, string Kind);

internal sealed record TmcSelection(IReadOnlyList<TmcLeaf> Leaves, IReadOnlyList<SnapshotSkip> Skipped);

/// <summary>
/// persistent-snapshot: which leaves of a PLC project are remanent, read from the compiled
/// <c>.tmc</c>. RETAIN = the symbol sits in a
/// <c>DataArea</c> whose <c>AreaNo AreaType="RetainSrc"</c>; PERSISTENT = a <c>TcPersistent</c>
/// property on a <c>Symbol</c> or on a type's <c>SubItem</c> (inherited by every instance of the
/// type); <c>TcIgnorePersistent</c> on a SubItem excludes its subtree. Structs and arrays are
/// flattened depth-first, arrays with their declared indices. Pure XML — no PLC needed.
/// </summary>
internal static class TmcRemanentSelector
{
    public const string RetainAreaType = "RetainSrc";
    public const string PersistentMark = "TcPersistent";
    public const string IgnoreMark = "TcIgnorePersistent";
    public const int MaxDepth = 32;
    public const int DefaultLeafLimit = 250_000;

    /// <param name="kind">"persistent", "retain" or "both".</param>
    public static bool TrySelect(string tmcXml, string kind, int leafLimit, out TmcSelection selection, out string error)
    {
        selection = new TmcSelection([], []);
        error = string.Empty;
        XDocument doc;
        try { doc = XDocument.Parse(tmcXml); }
        catch (XmlException ex) { error = $"TMC is not valid XML: {ex.Message}"; return false; }

        var types = IndexTypes(doc);
        var leaves = new Dictionary<string, TmcLeaf>(StringComparer.Ordinal);
        var skipped = new List<SnapshotSkip>();
        var ctx = new Walk(types, leaves, skipped, leafLimit);

        foreach (var area in doc.Descendants("DataArea"))
        {
            bool retainArea = string.Equals(
                (string?)area.Element("AreaNo")?.Attribute("AreaType"), RetainAreaType, StringComparison.Ordinal);
            foreach (var sym in area.Elements("Symbol"))
            {
                string? name = (string?)sym.Element("Name");
                string? type = (string?)sym.Element("BaseType");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) continue;
                bool persistent = HasMark(sym, PersistentMark);
                string? mark = persistent ? "persistent" : retainArea ? "retain" : null;
                // An unmarked symbol can still hold a type whose SubItems carry the mark (VAR PERSISTENT in an FB).
                if (mark is null && !ctx.ContainsMark(type.Trim())) continue;
                try { ctx.Descend(name, type.Trim(), ArrayDims(sym), mark, 0); }
                catch (LeafLimitException)
                {
                    error = $"more than {leafLimit} remanent leaves — narrow the selection or raise the limit";
                    return false;
                }
            }
        }

        var chosen = leaves.Values
            .Where(l => kind == "both" || l.Kind == kind)
            .OrderBy(l => l.Path, StringComparer.Ordinal).ToList();
        selection = new TmcSelection(chosen, skipped);
        return true;
    }

    private sealed class LeafLimitException : Exception { }

    private sealed class Walk(Dictionary<string, XElement> types, Dictionary<string, TmcLeaf> leaves,
        List<SnapshotSkip> skipped, int limit)
    {
        public void Descend(string path, string typeName, List<(int Lo, int Count)> dims, string? mark, int depth)
        {
            if (dims.Count > 0)
            {
                foreach (string idx in ExpandIndices(dims))
                    Descend(path + "[" + idx + "]", typeName, [], mark, depth);
                return;
            }
            if (mark is null && !ContainsMark(typeName)) return;
            if (depth > MaxDepth) { if (mark is not null) skipped.Add(new(path, "nesting deeper than " + MaxDepth)); return; }

            string norm = AdsTypeNames.NormalizeTypeName(typeName);
            if (IsIndirect(norm)) { if (mark is not null) skipped.Add(new(path, "POINTER/REFERENCE/INTERFACE")); return; }
            if (SnapshotValues.IsSupportedPrimitive(norm)) { AddLeaf(path, norm, false, mark); return; }

            if (!types.TryGetValue(typeName.Trim(), out var dt) && !types.TryGetValue(norm, out dt))
            { if (mark is not null) skipped.Add(new(path, $"unsupported type '{typeName}'")); return; }

            if (dt.Element("EnumInfo") is not null) { AddLeaf(path, norm, true, mark); return; }

            var subs = dt.Elements("SubItem").ToList();
            if (subs.Count == 0)
            {
                // alias of another type (possibly an array alias)
                string? baseType = (string?)dt.Element("BaseType");
                var aliasDims = ArrayDims(dt);
                if (!string.IsNullOrWhiteSpace(baseType))
                { Descend(path, baseType.Trim(), aliasDims, mark, depth + 1); return; }
                if (mark is not null) skipped.Add(new(path, $"unsupported type '{typeName}'"));
                return;
            }

            foreach (var sub in subs)
            {
                string? sn = (string?)sub.Element("Name");
                string? st = (string?)sub.Element("Type") ?? (string?)sub.Element("BaseType");
                if (string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(st)) continue;
                if (HasMark(sub, IgnoreMark)) continue;
                string? subMark = HasMark(sub, PersistentMark) ? "persistent" : mark;
                Descend(path + "." + sn.Trim().TrimStart('.'), st.Trim(), ArrayDims(sub), subMark, depth + 1);
            }
        }

        private readonly Dictionary<string, bool> _containsMark = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True if instances of the type can hold a remanent leaf by themselves (a TcPersistent SubItem somewhere below).</summary>
        public bool ContainsMark(string typeName)
        {
            string key = typeName.Trim();
            if (_containsMark.TryGetValue(key, out bool known)) return known;
            _containsMark[key] = false; // recursion guard; a cycle adds nothing
            bool result = false;
            if (types.TryGetValue(key, out var dt) || types.TryGetValue(AdsTypeNames.NormalizeTypeName(key), out dt))
            {
                foreach (var sub in dt.Elements("SubItem"))
                {
                    if (HasMark(sub, IgnoreMark)) continue;
                    string? st = (string?)sub.Element("Type") ?? (string?)sub.Element("BaseType");
                    if (HasMark(sub, PersistentMark) || (!string.IsNullOrWhiteSpace(st) && ContainsMark(st))) { result = true; break; }
                }
                string? alias = dt.Elements("SubItem").Any() ? null : (string?)dt.Element("BaseType");
                if (!result && !string.IsNullOrWhiteSpace(alias)) result = ContainsMark(alias);
            }
            _containsMark[key] = result;
            return result;
        }

        private void AddLeaf(string path, string type, bool isEnum, string? mark)
        {
            if (mark is null) return;
            if (leaves.Count >= limit) throw new LeafLimitException();
            if (leaves.TryGetValue(path, out var existing) && existing.Kind == "persistent") return;
            leaves[path] = new TmcLeaf(path, type, isEnum, mark);
        }
    }

    // Types are looked up by bare name and by "Namespace.Name" (library types carry a Namespace attribute).
    private static Dictionary<string, XElement> IndexTypes(XDocument doc)
    {
        var map = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var dt in doc.Descendants("DataType"))
        {
            var n = dt.Element("Name");
            string? name = (string?)n;
            if (string.IsNullOrWhiteSpace(name)) continue;
            name = name.Trim();
            map.TryAdd(name, dt);
            string? ns = (string?)n!.Attribute("Namespace");
            if (!string.IsNullOrWhiteSpace(ns)) map.TryAdd(ns + "." + name, dt);
        }
        return map;
    }

    private static bool IsIndirect(string norm) =>
        norm.StartsWith("POINTER TO", StringComparison.Ordinal) ||
        norm.StartsWith("REFERENCE TO", StringComparison.Ordinal) ||
        norm is "PVOID" or "__SYSTEM.IQUERYINTERFACE" or "IQUERYINTERFACE" ||
        norm.StartsWith("__SYSTEM.", StringComparison.Ordinal);

    private static bool HasMark(XElement e, string mark) =>
        e.Element("Properties")?.Elements("Property").Any(p => (string?)p.Element("Name") == mark) == true;

    private static List<(int Lo, int Count)> ArrayDims(XElement e) =>
        e.Elements("ArrayInfo").Select(a => (
            int.Parse((string?)a.Element("LBound") ?? "0", CultureInfo.InvariantCulture),
            int.Parse((string?)a.Element("Elements") ?? "1", CultureInfo.InvariantCulture))).ToList();

    private static IEnumerable<string> ExpandIndices(List<(int Lo, int Count)> dims)
    {
        IEnumerable<string> acc = [""];
        foreach (var (lo, count) in dims)
        {
            var current = acc;
            acc = current.SelectMany(prefix =>
                Enumerable.Range(lo, count).Select(i =>
                    (prefix.Length == 0 ? "" : prefix + ",") + i.ToString(CultureInfo.InvariantCulture)));
        }
        return acc;
    }
}
