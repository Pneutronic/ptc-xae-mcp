using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// persistent-snapshot: rename rules old → new applied to snapshot leaf paths before they are
/// matched against the PLC. A rule matches the exact path or a prefix that ends on a segment
/// boundary ('.' or '['); the longest matching rule wins. Produced by the caller, only applied here.
/// </summary>
internal sealed class SnapshotPathMap
{
    public static readonly SnapshotPathMap Empty = new(new Dictionary<string, string>());

    private readonly List<KeyValuePair<string, string>> _rules;

    public SnapshotPathMap(IReadOnlyDictionary<string, string> rules)
        => _rules = rules.OrderByDescending(r => r.Key.Length).ToList();

    public int Count => _rules.Count;

    /// <summary>Parses the tool parameter: a JSON object {"old":"new", …}; null/blank = no rules.</summary>
    public static bool TryParse(string? json, out SnapshotPathMap map, out string error)
    {
        map = Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            { error = "pathMap must be a JSON object {\"oldPath\":\"newPath\", ...}"; return false; }
            var rules = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.Name) ||
                    string.IsNullOrWhiteSpace(p.Value.GetString()))
                { error = $"pathMap rule '{p.Name}' needs a non-empty string target"; return false; }
                rules[p.Name] = p.Value.GetString()!;
            }
            map = new SnapshotPathMap(rules);
            return true;
        }
        catch (JsonException ex)
        {
            error = $"pathMap is not valid JSON: {ex.Message}";
            return false;
        }
    }

    public string Apply(string path)
    {
        foreach (var (from, to) in _rules)
        {
            if (path.Equals(from, StringComparison.Ordinal)) return to;
            if (path.Length > from.Length && path.StartsWith(from, StringComparison.Ordinal) &&
                (path[from.Length] is '.' or '['))
                return to + path[from.Length..];
        }
        return path;
    }

    /// <summary>Maps every key; two snapshot paths landing on the same target are a conflict
    /// (returned, never silently merged).</summary>
    public (Dictionary<string, string> TargetToSource, List<string> Conflicts) MapAll(IEnumerable<string> paths)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (string source in paths)
        {
            string target = Apply(source);
            if (map.TryGetValue(target, out string? other))
                conflicts.Add($"'{other}' and '{source}' both map to '{target}'");
            else
                map[target] = source;
        }
        return (map, conflicts);
    }
}
