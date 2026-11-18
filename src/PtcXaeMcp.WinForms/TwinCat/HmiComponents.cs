using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: what an HMI project offers — pages, user controls with their parameters, functions,
// languages — read from the project files on disk. Pure over HmiProjectSnapshot, so every rule is
// unit-tested with in-memory files.

/// <summary>The text files of an HMI project that the authoring tools read, keyed by project-relative path
/// with '/'.</summary>
internal sealed record HmiProjectSnapshot(
    string ProjectDirectory,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Problems)
{
    public string? Text(string path) => Files.TryGetValue(path, out string? t) ? t : null;
}

/// <param name="PropertyName">The name tools take (".usercontrol.json" propertyName, e.g. "Safety").</param>
/// <param name="Attribute">The markup attribute (".usercontrol.json" name, e.g. "data-tchmi-safety").</param>
internal sealed record HmiUcParameter(
    string PropertyName,
    string Attribute,
    string? DisplayName,
    string? Type,
    bool Bindable,
    bool RequiredOnCompile,
    string? DefaultJson,
    string? Category,
    string? Description);

internal sealed record HmiUserControlInfo(
    string Path,
    string? Description,
    int? DesignWidth,
    int? DesignHeight,
    IReadOnlyList<HmiUcParameter> Parameters,
    string? Problem);

internal sealed record HmiFunctionArgument(string Name, string? Type, bool Required);

internal sealed record HmiFunctionInfo(
    string Path,
    string FullName,
    string? Description,
    string? ReturnType,
    IReadOnlyList<HmiFunctionArgument> Arguments);

/// <param name="Kind">usercontrol (a host), content (a region's target), function.</param>
internal sealed record HmiPageUse(string Kind, string Target, int Line);

/// <param name="Kind">view, content or usercontrol.</param>
internal sealed record HmiPageInfo(
    string Path,
    string Kind,
    string? RootId,
    string? RootType,
    int ControlCount,
    IReadOnlyList<string> ControlTypes,
    IReadOnlyList<HmiPageUse> Uses);

internal sealed record HmiLanguageFile(string Locale, string Path, bool Exists);

internal sealed record HmiComponentIndex(
    string ProjectDirectory,
    string? StartupView,
    string? ActiveTheme,
    IReadOnlyList<string> Themes,
    IReadOnlyList<HmiLanguageFile> Languages,
    IReadOnlyList<HmiPageInfo> Pages,
    IReadOnlyList<HmiUserControlInfo> UserControls,
    IReadOnlyList<HmiFunctionInfo> Functions,
    IReadOnlyList<string> Problems)
{
    public HmiUserControlInfo? UserControl(string path) =>
        UserControls.FirstOrDefault(u => u.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}

internal static class HmiComponents
{
    internal const string ConfigPath = "Properties/tchmiconfig.json";
    internal const int MaxFiles = 5000;
    internal const long MaxFileBytes = 5 * 1024 * 1024;

    // Top-level folders that are build output, packages or server state — never authored pages.
    private static readonly string[] SkippedTopLevel = ["bin", "obj", "Packages", "packages", "node_modules", "Server", "Imports"];

    internal static readonly string[] PageExtensions = [".view", ".content", ".usercontrol"];

    public static string? PageKind(string path) =>
        path.EndsWith(".view", StringComparison.OrdinalIgnoreCase) ? "view"
        : path.EndsWith(".content", StringComparison.OrdinalIgnoreCase) ? "content"
        : path.EndsWith(".usercontrol", StringComparison.OrdinalIgnoreCase) ? "usercontrol"
        : null;

    private static bool Wanted(string path) =>
        PageKind(path) is not null
        || path.EndsWith(".usercontrol.json", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".function.json", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".localization", StringComparison.OrdinalIgnoreCase)
        || path.Equals(ConfigPath, StringComparison.OrdinalIgnoreCase);

    // ---- disk ------------------------------------------------------------------

    /// <summary>Reads the authored text files of the project directory. Never throws for a single file —
    /// unreadable files become problems.</summary>
    public static HmiProjectSnapshot Load(string projectDirectory)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        if (!Directory.Exists(projectDirectory))
            return new HmiProjectSnapshot(projectDirectory, files, [$"project directory not found: {projectDirectory}"]);

        int seen = 0;
        foreach (string full in Directory.EnumerateFiles(projectDirectory, "*", new EnumerationOptions
                 { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }))
        {
            string rel = Path.GetRelativePath(projectDirectory, full).Replace('\\', '/');
            string top = rel.Split('/')[0];
            if (rel.Contains('/') && (SkippedTopLevel.Contains(top, StringComparer.OrdinalIgnoreCase) || top.StartsWith('.')))
                continue;
            if (!Wanted(rel)) continue;
            if (++seen > MaxFiles) { problems.Add($"more than {MaxFiles} files — the rest is not read"); break; }
            try
            {
                if (new FileInfo(full).Length > MaxFileBytes) { problems.Add($"{rel}: larger than 5 MB, not read"); continue; }
                files[rel] = File.ReadAllText(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{rel}: {ex.Message}");
            }
        }
        return new HmiProjectSnapshot(projectDirectory, files, problems);
    }

    // ---- index -----------------------------------------------------------------

    public static HmiComponentIndex Build(HmiProjectSnapshot snap)
    {
        var problems = new List<string>(snap.Problems);
        JsonElement config = ParseJson(snap.Text(ConfigPath), ConfigPath, problems);

        string? startup = Str(config, "startupView") is { } sv ? HmiPaths.Normalize(sv) : null;
        string? activeTheme = Str(config, "activeTheme");
        var themes = config.ValueKind == JsonValueKind.Object && config.TryGetProperty("themes", out var th) && th.ValueKind == JsonValueKind.Object
            ? th.EnumerateObject().Select(p => p.Name).ToList() : [];

        var languages = new List<HmiLanguageFile>();
        if (config.ValueKind == JsonValueKind.Object && config.TryGetProperty("languages", out var langs) && langs.ValueKind == JsonValueKind.Object)
            foreach (var l in langs.EnumerateObject())
            {
                string? p = l.Value.ValueKind == JsonValueKind.String ? HmiPaths.Normalize(l.Value.GetString()) : null;
                if (p is null) { problems.Add($"{ConfigPath}: language '{l.Name}' has no valid file path"); continue; }
                languages.Add(new HmiLanguageFile(l.Name, p, snap.Files.ContainsKey(p)));
            }

        var userControls = snap.Files.Keys
            .Where(p => PageKind(p) == "usercontrol")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => UserControl(snap, p))
            .ToList();

        var functions = new List<HmiFunctionInfo>();
        foreach (string p in snap.Files.Keys.Where(k => k.EndsWith(".function.json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            JsonElement f = ParseJson(snap.Text(p), p, problems);
            if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{p}: no \"function\" object");
                continue;
            }
            string? name = Str(fn, "name");
            if (name is null) { problems.Add($"{p}: function without name"); continue; }
            string? ns = Str(fn, "namespace");
            var args = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                    .Select(x => new HmiFunctionArgument(Str(x, "name") ?? "?", Str(x, "type"), Bool(x, "required") ?? false)).ToList()
                : [];
            string? ret = fn.TryGetProperty("returnValue", out var r) && r.ValueKind == JsonValueKind.Object ? Str(r, "type") : null;
            functions.Add(new HmiFunctionInfo(p, string.IsNullOrEmpty(ns) ? name : ns + "." + name, Str(fn, "description"), ret, args));
        }

        var pages = new List<HmiPageInfo>();
        foreach (string p in snap.Files.Keys.Where(k => PageKind(k) is not null).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            string text = snap.Files[p];
            var doc = HmiMarkup.Parse(text);
            problems.AddRange(doc.Problems.Select(x => $"{p}: {x}"));
            var uses = new List<HmiPageUse>();
            foreach (var e in doc.Elements)
            {
                if (e.Value(HmiMarkup.TargetUserControlAttribute) is { Length: > 0 } uc)
                    uses.Add(new HmiPageUse("usercontrol", HmiPaths.Normalize(uc) ?? uc, e.Line));
                if (e.Value(HmiMarkup.TargetContentAttribute) is { Length: > 0 } ct)
                    uses.Add(new HmiPageUse("content", HmiPaths.Normalize(ct) ?? ct, e.Line));
            }
            foreach (var b in HmiMarkup.Bindings(text).Where(b => b.Kind == "f"))
                if (!uses.Any(u => u.Kind == "function" && u.Target == b.Target))
                    uses.Add(new HmiPageUse("function", b.Target, b.Line));
            pages.Add(new HmiPageInfo(p, PageKind(p)!, doc.Root?.Id, doc.Root?.Type, doc.Elements.Count,
                doc.Elements.Select(e => e.Type).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                uses));
        }

        return new HmiComponentIndex(snap.ProjectDirectory, startup, activeTheme, themes, languages, pages, userControls,
            functions, problems);
    }

    private static HmiUserControlInfo UserControl(HmiProjectSnapshot snap, string path)
    {
        var doc = HmiMarkup.Parse(snap.Files[path]);
        int? w = Int(doc.Root?.Value("data-tchmi-creator-viewport-width"));
        int? h = Int(doc.Root?.Value("data-tchmi-creator-viewport-height"));
        string jsonPath = path + ".json";
        string? json = snap.Text(jsonPath);
        if (json is null) return new HmiUserControlInfo(path, null, w, h, [], $"'{jsonPath}' is missing");

        var problems = new List<string>();
        JsonElement root = ParseJson(json, jsonPath, problems);
        if (root.ValueKind != JsonValueKind.Object)
            return new HmiUserControlInfo(path, null, w, h, [], problems.FirstOrDefault() ?? $"'{jsonPath}' is not an object");

        var parameters = new List<HmiUcParameter>();
        if (root.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
            {
                string? attr = Str(p, "name");
                if (string.IsNullOrEmpty(attr)) continue;
                string prop = Str(p, "propertyName") ?? Str(p, "displayName") ?? AttributeToName(attr);
                string? def = p.TryGetProperty("defaultValue", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    ? d.GetRawText() : null;
                parameters.Add(new HmiUcParameter(prop, attr, Str(p, "displayName"), Str(p, "type"), Bool(p, "bindable") ?? true,
                    Bool(p, "requiredOnCompile") ?? false, def, Str(p, "category"), Str(p, "description")));
            }
        return new HmiUserControlInfo(path, Str(root, "description"), w, h, parameters, null);
    }

    /// <summary>"data-tchmi-bdc-window" → "BdcWindow".</summary>
    internal static string AttributeToName(string attribute)
    {
        string s = attribute.StartsWith("data-tchmi-", StringComparison.OrdinalIgnoreCase) ? attribute[11..] : attribute;
        return string.Concat(s.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
    }

    // ---- json helpers ----------------------------------------------------------

    internal static JsonElement ParseJson(string? text, string path, List<string> problems)
    {
        if (text is null) return default;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            problems.Add($"{path}: not valid JSON ({ex.Message})");
            return default;
        }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static int? Int(string? s) => int.TryParse(s, out int i) ? i : null;
}
