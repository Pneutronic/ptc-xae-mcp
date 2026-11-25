using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-views: COM-free building blocks for creating and
// changing HMI project files. Everything here is pure and unit-tested.

/// <summary>Source text of one HMI project file (ITcHmiFile.GetSource).</summary>
internal sealed record HmiFileContent(string Path, string Source);

/// <summary>An item about to be deleted: text file (ITcHmiFile) or not, and how many
/// items lie below it.</summary>
internal sealed record HmiItemInfo(string Path, bool IsFile, int Descendants);

/// <summary>Result of <c>ImportHmiFile</c>.</summary>
/// <param name="Created">Project-relative paths of the imported item.</param>
/// <param name="EngineError">The error TE2000 raised although the item IS in the project afterwards
/// (#247: a .js is added, then a WPF-owned object fails on the COM thread). Null for a clean import;
/// a genuine failure — nothing added — is an exception, never this.</param>
internal sealed record HmiImportResult(IReadOnlyList<string> Created, string? EngineError);

/// <summary>
/// TE2000 item templates the MCP can create. Dependent files are what the template creates
/// next to the main file (live: usercontrol → .usercontrol.json, function → .function.json).
/// <para>
/// Measured live 2026-09-25 (TE2000 14.17.2), file name after <c>ITcHmiItem.AddItem</c> — with
/// or without the extension in the name: json/css/content/usercontrol/function correct, but
/// html → "x.html.html", view → "x.view.view", text → "x.text.txt" (although
/// <c>HmiTemplatesUtils.GetFileExtension</c> says ".text"). Those three kinds are therefore
/// created by importing a correctly named temporary file (<c>AddExistingItem</c>, proven) —
/// <see cref="Kind.ViaImport"/>.
/// </para>
/// </summary>
internal static class HmiItemKinds
{
    internal sealed record Kind(string Name, string TemplateKey, string Extension, IReadOnlyList<string> DependentSuffixes,
        bool ViaImport = false);

    public static readonly IReadOnlyList<Kind> All =
    [
        new("view", "View", ".view", [], ViaImport: true),
        new("content", "Content", ".content", []),
        new("usercontrol", "UserControl", ".usercontrol", [".usercontrol.json"]),
        new("function_js", "FunctionJavaScript_Hmi", ".js", [".function.json"]),
        new("function_ts", "FunctionTypeScript_Hmi", ".ts", [".function.json"]),
        new("codebehind_js", "CodeBehindJavaScript", ".js", []),
        new("codebehind_ts", "CodeBehindTypeScript", ".ts", []),
        new("css", "GeneralBlankCss", ".css", []),
        new("html", "GeneralBlankHtml", ".html", [], ViaImport: true),
        new("js", "GeneralBlankJavaScript", ".js", []),
        new("json", "GeneralBlankJson", ".json", []),
        new("ts", "GeneralBlankTypeScript", ".ts", []),
        new("text", "GeneralBlankText", ".txt", [], ViaImport: true),
        new("xml", "GeneralBlankXml", ".xml", []),
    ];

    // Vendor item template of a view (…\Templates\ItemTemplates\TwinCAT HMI\View_Hmi\View.view),
    // read at runtime; the copy below is the same text for machines where the path differs.
    private const string VendorViewTemplatePath =
        @"C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE2000-HMI-Engineering\Templates\ItemTemplates\TwinCAT HMI\View_Hmi\View.view";

    private const string ViewTemplateCopy =
        "<div\n  id=\"$TargetIdentifier$\"\n  data-tchmi-type=\"TcHmi.Controls.System.TcHmiView\"\n  data-tchmi-top=\"0\"\n" +
        "  data-tchmi-left=\"0\"\n  data-tchmi-width-mode=\"Content\"\n  data-tchmi-min-width=\"100\"\n  data-tchmi-min-width-unit=\"%\"\n" +
        "  data-tchmi-height-mode=\"Content\"\n  data-tchmi-min-height=\"100\"\n  data-tchmi-min-height-unit=\"%\"\n" +
        "  data-tchmi-background-image-height=\"100\"\n  data-tchmi-background-image-width=\"100\"\n" +
        "  data-tchmi-background-image-height-unit=\"%\"\n  data-tchmi-background-image-width-unit=\"%\"\n></div>\n";

    /// <summary>Initial content for a <see cref="Kind.ViaImport"/> file created without a source.</summary>
    public static string Skeleton(Kind kind, string baseName)
    {
        if (kind.Name != "view") return "";
        string template;
        try { template = File.Exists(VendorViewTemplatePath) ? File.ReadAllText(VendorViewTemplatePath) : ViewTemplateCopy; }
        catch (IOException) { template = ViewTemplateCopy; }
        return template.Replace("$TargetIdentifier$", baseName, StringComparison.Ordinal);
    }

    public static Kind? Find(string? name) =>
        All.FirstOrDefault(k => k.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(k => k.Name));

    /// <summary>File path of the main item (folder + name + extension) and its dependents.</summary>
    public static (string Main, IReadOnlyList<string> Dependents) Paths(Kind kind, string folder, string name)
    {
        string baseName = name.EndsWith(kind.Extension, StringComparison.OrdinalIgnoreCase)
            ? name[..^kind.Extension.Length]
            : name;
        string prefix = folder.Length == 0 ? "" : folder + "/";
        string main = prefix + baseName + kind.Extension;
        return (main, kind.DependentSuffixes.Select(s => prefix + baseName + s).ToList());
    }
}

internal static partial class HmiPaths
{
    [GeneratedRegex(@"[<>:""|?*\x00-\x1F]")]
    private static partial Regex InvalidChars();

    /// <summary>
    /// Project-relative path with '/' separators, no leading/trailing separator; null when
    /// empty, absolute (drive, leading slash, UNC), containing '.'/'..' segments or invalid
    /// characters. <paramref name="allowEmpty"/> admits the project root (folders only).
    /// </summary>
    public static string? Normalize(string? path, bool allowEmpty = false)
    {
        string p = (path ?? "").Trim().Replace('\\', '/');
        if (p.Length == 0) return allowEmpty ? "" : null;
        if (p.StartsWith('/') || (p.Length > 1 && p[1] == ':')) return null;
        p = p.TrimEnd('/');
        if (p.Length == 0) return allowEmpty ? "" : null;
        string[] segments = p.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Trim() != s || InvalidChars().IsMatch(s)))
            return null;
        return string.Join('/', segments);
    }

    public static (string Folder, string FileName) Split(string normalized)
    {
        int i = normalized.LastIndexOf('/');
        return i < 0 ? ("", normalized) : (normalized[..i], normalized[(i + 1)..]);
    }
}

internal static partial class HmiText
{
    [GeneratedRegex(@"""\$schema""\s*:\s*""(?:[^""\\]|\\.)*""")]
    private static partial Regex SchemaProperty();

    /// <summary>
    /// For JSON files: keep the "$schema" value of the CURRENT project file in the new text.
    /// The schema path points into the project's Packages folder (framework version
    /// specific), so a source written for another project would otherwise break editor
    /// validation. Text-level replacement keeps the new file's formatting.
    /// </summary>
    public static string KeepSchema(string current, string replacement)
    {
        Match old = SchemaProperty().Match(current);
        if (!old.Success) return replacement;
        return SchemaProperty().IsMatch(replacement)
            ? SchemaProperty().Replace(replacement, old.Value.Replace("$", "$$", StringComparison.Ordinal), 1)
            : replacement;
    }

    /// <summary>Line-ending and trailing-whitespace insensitive comparison for read-backs.</summary>
    public static bool Same(string a, string b) => Canon(a) == Canon(b);

    /// <summary>
    /// The replacement with the line endings of the current file when that file uses one kind only (all CRLF or
    /// all LF), so a source taken with other line endings (git show, LF) does not convert the whole file. A file
    /// with mixed or no line breaks takes the replacement as it is, which is how a mixed file gets normalised.
    /// </summary>
    public static string MatchLineEndings(string current, string replacement)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < current.Length; i++)
        {
            if (current[i] != '\n') continue;
            if (i > 0 && current[i - 1] == '\r') crlf++; else lf++;
        }
        if (crlf > 0 && lf == 0) return replacement.ReplaceLineEndings("\r\n");
        if (lf > 0 && crlf == 0) return replacement.ReplaceLineEndings("\n");
        return replacement;
    }

    /// <summary>
    /// Whether a write would change the file: exact, line endings included (only trailing whitespace at the end
    /// is ignored). Call with a target made by <see cref="MatchLineEndings"/>, so a line-ending-only difference
    /// counts only where the write really changes the line endings (a mixed file).
    /// </summary>
    public static bool Unchanged(string current, string target) =>
        string.Equals(current.TrimEnd(), target.TrimEnd(), StringComparison.Ordinal);

    /// <summary>
    /// Every "url" string value in a JSON document (any depth) that names <paramref name="path"/> or lies below it,
    /// compared as normalised project paths. Empty for JSON that cannot be parsed.
    /// </summary>
    public static IReadOnlyList<string> UrlsPointingAt(string json, string path)
    {
        System.Text.Json.Nodes.JsonNode? root;
        try
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions:
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
        }
        catch (System.Text.Json.JsonException) { return []; }

        var hits = new List<string>();
        Walk(root);
        return hits;

        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "url" && value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? url)
                            && HmiPaths.Normalize(url) is { } n
                            && (n.Equals(path, StringComparison.OrdinalIgnoreCase) || n.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                            hits.Add(url);
                        else Walk(value);
                    }
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }
    }

    private static string Canon(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    /// <summary>Lines only in the new text / only in the old text (multiset difference).</summary>
    public static (int Added, int Removed) LineDiff(string oldText, string newText)
    {
        var oldLines = Canon(oldText).Split('\n');
        var newLines = Canon(newText).Split('\n');
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string l in oldLines) counts[l] = counts.GetValueOrDefault(l) + 1;
        int added = 0;
        foreach (string l in newLines)
        {
            if (counts.TryGetValue(l, out int c) && c > 0) counts[l] = c - 1;
            else added++;
        }
        return (added, counts.Values.Sum());
    }

    /// <summary>First differing line (1-based) of two texts, for read-back error messages.</summary>
    public static int FirstDifference(string a, string b)
    {
        var x = Canon(a).Split('\n');
        var y = Canon(b).Split('\n');
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
            if (x[i] != y[i]) return i + 1;
        return n + 1;
    }
}

/// <summary>One entry of an apply_bundle manifest after validation.</summary>
internal sealed record HmiBundleEntry(string Path, string SourceFile, string Source, HmiItemKinds.Kind? Kind);
