using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-controls: the controls an HMI project can use, read from the Description.json files of its NuGet packages
// (packages.config → <solution>\Packages\<id>.<version> or the global NuGet cache). Seen on the fixture
// (framework 14.6.3, controls 14.8.3): runtimes\native1.12-tchmi\<Control>\Description.json for the Beckhoff
// controls and ...\Controls\System\<Control>\Description.json for the framework's; each names its "base", and its own
// "attributes" only — the full set is the base chain (TcHmiButton → TcHmiControl). "properties.containerControl"
// tells whether children are allowed, "properties.geometry" the designer's default size.

internal sealed record HmiControlAttribute(string Name, string? PropertyName, string? DisplayName, string? Type, string? Category,
    bool Bindable, bool ReadOnly, bool Visible, string? Description, string DeclaredIn);

internal sealed record HmiControlType(string FullName, string Name, string? DisplayName, string? Base, string Package, bool Visible,
    bool Container, int? DesignWidth, int? DesignHeight, string? Description, IReadOnlyList<HmiControlAttribute> OwnAttributes);

internal sealed class HmiControlCatalog
{
    private readonly Dictionary<string, HmiControlType> _types;

    public IReadOnlyCollection<HmiControlType> Types => _types.Values;
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyList<string> Problems { get; }

    internal HmiControlCatalog(IEnumerable<HmiControlType> types, IReadOnlyList<string> sources, IReadOnlyList<string> problems)
    {
        _types = new Dictionary<string, HmiControlType>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in types) _types.TryAdd(t.FullName, t);
        Sources = sources;
        Problems = problems;
    }

    /// <summary>By full name (TcHmi.Controls.Beckhoff.TcHmiButton) or, when unique, by short name (TcHmiButton).</summary>
    public (HmiControlType? Type, string? Error) Find(string name)
    {
        string n = name.Trim();
        if (_types.TryGetValue(n, out var t)) return (t, null);
        var shortMatches = _types.Values.Where(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).ToList();
        if (shortMatches.Count == 1) return (shortMatches[0], null);
        if (shortMatches.Count > 1)
            return (null, $"'{name}' is ambiguous: {string.Join(", ", shortMatches.Select(x => x.FullName))}.");
        var near = _types.Values.Select(x => x.Name).Where(x => x.Contains(n.Replace("TcHmi", "", StringComparison.OrdinalIgnoreCase),
            StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        return (null, $"'{name}' is not a control of the project's packages" + (near.Count > 0 ? $" — did you mean {string.Join(", ", near)}?" : "."));
    }

    /// <summary>Own attributes first, then those of the base chain; a name declared again lower down is shadowed.</summary>
    public IReadOnlyList<HmiControlAttribute> AllAttributes(HmiControlType type)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<HmiControlAttribute>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (HmiControlType? t = type; t is not null && visited.Add(t.FullName);
             t = t.Base is not null && _types.TryGetValue(t.Base, out var b) ? b : null)
        {
            foreach (var a in t.OwnAttributes)
                if (seen.Add(a.Name)) list.Add(a);
        }
        return list;
    }

    /// <summary>The attribute named by attribute (data-tchmi-text), propertyName (Text) or displayName.</summary>
    public static HmiControlAttribute? Attribute(IReadOnlyList<HmiControlAttribute> attributes, string key)
    {
        string k = key.Trim();
        return attributes.FirstOrDefault(a => a.Name.Equals(k, StringComparison.OrdinalIgnoreCase))
            ?? attributes.FirstOrDefault(a => a.Name.Equals("data-tchmi-" + k, StringComparison.OrdinalIgnoreCase))
            ?? attributes.FirstOrDefault(a => string.Equals(a.PropertyName, k, StringComparison.OrdinalIgnoreCase))
            ?? attributes.FirstOrDefault(a => string.Equals(a.DisplayName, k, StringComparison.OrdinalIgnoreCase));
    }

    // ---- loading -------------------------------------------------------------------

    public static HmiControlCatalog Load(string projectDirectory)
    {
        var problems = new List<string>();
        var sources = new List<string>();
        var types = new List<HmiControlType>();
        string config = Path.Combine(projectDirectory, "packages.config");
        if (!File.Exists(config))
            return new HmiControlCatalog(types, sources, ["packages.config not found — the project uses no NuGet packages (or PackageReference)."]);

        List<(string Id, string Version)> packages;
        try
        {
            packages = XDocument.Load(config).Root?.Elements("package")
                .Select(p => ((string?)p.Attribute("id") ?? "", (string?)p.Attribute("version") ?? ""))
                .Where(p => p.Item1.Length > 0 && p.Item2.Length > 0).ToList() ?? [];
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            return new HmiControlCatalog(types, sources, [$"packages.config could not be read: {ex.Message}"]);
        }

        string solutionDir = Path.GetDirectoryName(Path.GetFullPath(projectDirectory)) ?? projectDirectory;
        string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } np ? np
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var (id, version) in packages)
        {
            string? root = new[]
            {
                Path.Combine(solutionDir, "Packages", $"{id}.{version}"),
                Path.Combine(solutionDir, "packages", $"{id}.{version}"),
                Path.Combine(nugetRoot, id.ToLowerInvariant(), version),
            }.FirstOrDefault(Directory.Exists);
            if (root is null) { problems.Add($"package {id} {version} is not restored (not in Packages\\ nor the NuGet cache)."); continue; }
            string runtimes = Path.Combine(root, "runtimes");
            if (!Directory.Exists(runtimes)) continue;
            int before = types.Count;
            foreach (string file in Directory.EnumerateFiles(runtimes, "Description.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (Parse(File.ReadAllText(file), $"{id} {version}") is { } t) types.Add(t);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{Path.GetRelativePath(root, file)} in {id}: {ex.Message}");
                }
            }
            if (types.Count > before) sources.Add($"{id} {version} ({types.Count - before} control(s))");
        }
        return new HmiControlCatalog(types, sources, problems);
    }

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>A control description; null for other Description.json files (the framework's own has no name).</summary>
    internal static HmiControlType? Parse(string json, string package)
    {
        JsonNode? d = JsonNode.Parse(json, documentOptions: Lenient);
        string? name = Str(d?["name"]), ns = Str(d?["namespace"]);
        if (name is null || ns is null || (d?["attributes"] is null && d?["base"] is null)) return null;
        string full = ns + "." + name;
        var attrs = new List<HmiControlAttribute>();
        if (d?["attributes"] is JsonArray arr)
        {
            foreach (var a in arr)
            {
                string? an = Str(a?["name"]);
                if (an is null) continue;
                attrs.Add(new HmiControlAttribute(an, Str(a?["propertyName"]), Str(a?["displayName"]), Str(a?["type"]), Str(a?["category"]),
                    Bool(a?["bindable"], true), Bool(a?["readOnly"], false), Bool(a?["visible"], true), Str(a?["description"]), full));
            }
        }
        JsonNode? props = d?["properties"];
        return new HmiControlType(full, name, Str(d?["displayName"]), Str(d?["base"]), package, Bool(d?["visible"], true),
            Bool(props?["containerControl"], false), Int(props?["geometry"]?["width"]), Int(props?["geometry"]?["height"]),
            Str(d?["description"]), attrs);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;
    private static bool Bool(JsonNode? n, bool fallback) => n is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) && d > 0 ? (int)d : null;
}
