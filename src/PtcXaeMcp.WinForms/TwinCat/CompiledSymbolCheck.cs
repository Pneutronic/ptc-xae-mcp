using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// What a PLC project's .tmc lists after a build: every top-level symbol with its type, every data type with
/// its members. Keys compare case-insensitively.
/// </summary>
internal sealed record TmcSymbolIndex(
    string Path,
    DateTime LastWriteUtc,
    IReadOnlyDictionary<string, string?> Symbols,
    IReadOnlyDictionary<string, TmcDataType> DataTypes);

/// <summary>A data type's members (name → type) and the type it extends, as the .tmc lists them.</summary>
internal sealed record TmcDataType(IReadOnlyDictionary<string, string?> Members, string? Extends);

/// <summary>
/// "Build succeeded" plus a fresh compile artifact still does not prove that a just-created object is
/// in the compiled application: TwinCAT translates only what a task's program reaches, so an unused GVL,
/// an FB nobody instantiates or a function leaves no trace — 0 errors, no symbol. This check answers the
/// question the caller actually has ("is GVL_Recipe.nCount in the PLC now?") from the PLC project's .tmc,
/// the module description the XAE writes next to the project: <c>&lt;Symbol&gt;&lt;Name&gt;</c> for every
/// variable the application exposes, <c>&lt;DataType&gt;&lt;Name&gt;</c> for every type it uses.
/// Names are IEC identifiers and compared case-insensitively.
/// </summary>
internal static class CompiledSymbolCheck
{
    internal const string LinkAlwaysHint =
        "TwinCAT compiles only what a task's program reaches: an unused GVL, an FB nobody instantiates and a " +
        "function leave no symbol. Use the object from a called program, or put {attribute 'linkalways'} on the " +
        "GVL/POU; a variable marked {attribute 'hide'} is never listed.";

    /// <summary>Splits the caller's list; blank entries are dropped, duplicates kept once (first spelling wins).</summary>
    internal static IReadOnlyList<string> Normalize(IEnumerable<string>? expected)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in expected ?? [])
        {
            foreach (var part in (raw ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(part)) result.Add(part);
        }
        return result;
    }

    /// <summary>Reads symbols and data types out of a .tmc document; throws <see cref="XmlException"/> on malformed XML.</summary>
    internal static TmcSymbolIndex ReadIndex(string path, DateTime lastWriteUtc, string tmcXml)
    {
        var doc = XDocument.Parse(tmcXml);
        var symbols = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, TmcDataType>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in doc.Descendants())
        {
            string kind = e.Name.LocalName;
            if (kind != "Symbol" && kind != "DataType") continue;
            string? name = Child(e, "Name");
            if (string.IsNullOrEmpty(name)) continue;
            if (kind == "Symbol") { symbols.TryAdd(name, Child(e, "BaseType")); continue; }

            var members = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in e.Elements().Where(c => c.Name.LocalName == "SubItem"))
                if (Child(sub, "Name") is { Length: > 0 } member) members.TryAdd(member, Child(sub, "Type"));
            types.TryAdd(name, new TmcDataType(members, Child(e, "ExtendsType")));
        }
        return new TmcSymbolIndex(path, lastWriteUtc, symbols, types);

        static string? Child(XElement e, string name) =>
            e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim();
    }

    /// <summary>Where one expected name was found, or null when no .tmc lists it.</summary>
    internal static string? Locate(string expected, TmcSymbolIndex tmc)
    {
        if (tmc.Symbols.ContainsKey(expected)) return "symbol";
        if (tmc.DataTypes.ContainsKey(expected)) return "data type";
        string prefix = expected + ".";
        int below = tmc.Symbols.Keys.Count(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (below > 0) return $"{below} symbol{(below == 1 ? "" : "s")} below it";
        return MemberOf(expected, tmc) is string symbol ? $"member of {symbol}" : null;
    }

    /// <summary>
    /// A path into an instance (MAIN.fbAxis.nState, GVL.arItems[2].fValue): the .tmc lists only the top-level
    /// symbol, its members live in the data types. Walks from the longest symbol the path starts with through
    /// the members (inherited ones included); array indices are ignored. Returns that symbol, or null.
    /// </summary>
    private static string? MemberOf(string expected, TmcSymbolIndex tmc)
    {
        string[] parts = System.Text.RegularExpressions.Regex.Replace(expected, @"\[[^\]]*\]", "").Split('.');
        for (int n = parts.Length - 1; n >= 1; n--)
        {
            string symbol = string.Join(".", parts[..n]);
            if (!tmc.Symbols.TryGetValue(symbol, out string? type)) continue;
            foreach (string member in parts[n..])
            {
                type = MemberType(tmc, type, member);
                if (type is null) break;
            }
            // The .tmc's spelling of the symbol, not the caller's.
            return type is null ? null : tmc.Symbols.Keys.First(k => string.Equals(k, symbol, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static string? MemberType(TmcSymbolIndex tmc, string? type, string member)
    {
        // Depth-limited: an inheritance chain is short, a cycle in a broken .tmc must not hang the build result.
        for (int depth = 0; type is not null && depth < 32; depth++)
        {
            if (!TryType(tmc, type, out var dataType)) return null;
            if (dataType.Members.TryGetValue(member, out string? memberType)) return memberType ?? "";
            type = dataType.Extends;
        }
        return null;
    }

    // A type may be named with its library namespace (TcUnit.FB_TestSuite) while the .tmc lists it plain.
    private static bool TryType(TmcSymbolIndex tmc, string type, out TmcDataType dataType)
    {
        if (tmc.DataTypes.TryGetValue(type, out dataType!)) return true;
        int dot = type.LastIndexOf('.');
        return dot >= 0 && tmc.DataTypes.TryGetValue(type[(dot + 1)..], out dataType!);
    }

    /// <summary>
    /// The report block appended to a build result. A missing name, or no readable .tmc at all, makes the
    /// first line an [Error] — the expectation failed, whatever the compiler said.
    /// </summary>
    internal static string Report(
        IReadOnlyList<string> expected, IReadOnlyList<TmcSymbolIndex> tmcs, IReadOnlyList<string> tmcProblems, DateTime buildStartedUtc)
    {
        var sb = new StringBuilder();
        if (tmcs.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"[Error] Expected symbols not verified: no readable .tmc for the built PLC project");
            sb.Append(tmcProblems.Count > 0 ? $" ({string.Join("; ", tmcProblems)})." : ".");
            return sb.ToString();
        }

        var found = new List<string>();
        var missing = new List<string>();
        foreach (var name in expected)
        {
            var hit = tmcs.Select(t => (Tmc: t, Where: Locate(name, t))).FirstOrDefault(x => x.Where is not null);
            if (hit.Where is null) missing.Add(name);
            else found.Add(tmcs.Count == 1 ? $"{name} ({hit.Where})" : $"{name} ({hit.Where}, {System.IO.Path.GetFileName(hit.Tmc.Path)})");
        }

        sb.AppendLine(missing.Count == 0
            ? $"Expected symbols: all {expected.Count} found in the compiled PLC."
            : $"[Error] Expected symbols: {missing.Count} of {expected.Count} missing from the compiled PLC: {string.Join(", ", missing)}. {LinkAlwaysHint}");
        if (found.Count > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"Found: {string.Join(", ", found)}");
        foreach (var tmc in tmcs)
        {
            string freshness = tmc.LastWriteUtc >= buildStartedUtc.AddSeconds(-2)
                ? "written by this build"
                : "not rewritten by this build (the XAE rewrites it only when the compiled content changes)";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"TMC: {tmc.Path} ({tmc.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z, {freshness}; {tmc.Symbols.Count} symbols, {tmc.DataTypes.Count} data types)");
        }
        foreach (var problem in tmcProblems) sb.AppendLine(CultureInfo.InvariantCulture, $"[Note] {problem}");
        return sb.ToString().TrimEnd();
    }
}
