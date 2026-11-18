using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: localized texts of an HMI project. tchmiconfig.json "languages" maps a locale to its
// Localization/<locale>.localization file; each file holds { "locale": …, "localizedText": { key: text } }.
// Pages use a text with %l%Key%/l%.

internal sealed record HmiLanguageTexts(string Locale, string Path, bool Exists, IReadOnlyDictionary<string, string> Texts, string? Problem);

internal sealed record HmiLocalizationState(IReadOnlyList<HmiLanguageTexts> Languages);

/// <summary>Analysis for twincat_hmi_list_localization.</summary>
internal sealed record HmiLocalizationReport(
    IReadOnlyList<object> Languages,
    IReadOnlyList<string> AllKeys,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MissingByLanguage,
    IReadOnlyList<object> UsedButUndefined,
    IReadOnlyList<string> DefinedButUnused);

/// <summary>Outcome of applying changes to one .localization file.</summary>
internal sealed record HmiLocalizationEdit(string Text, IReadOnlyList<string> Added, IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed, IReadOnlyList<string> AlreadyAbsent);

internal static class HmiLocalization
{
    internal const string TextsProperty = "localizedText";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static HmiLocalizationState Read(HmiProjectSnapshot snap, HmiComponentIndex index)
    {
        var list = new List<HmiLanguageTexts>();
        foreach (var lang in index.Languages)
        {
            string? text = snap.Text(lang.Path);
            if (text is null)
            {
                list.Add(new HmiLanguageTexts(lang.Locale, lang.Path, false, new Dictionary<string, string>(), $"'{lang.Path}' does not exist"));
                continue;
            }
            var (texts, problem) = Parse(text);
            list.Add(new HmiLanguageTexts(lang.Locale, lang.Path, true, texts ?? new Dictionary<string, string>(), problem));
        }
        return new HmiLocalizationState(list);
    }

    internal static (IReadOnlyDictionary<string, string>? Texts, string? Problem) Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, "not a JSON object");
            if (!doc.RootElement.TryGetProperty(TextsProperty, out var t)) return (new Dictionary<string, string>(), null);
            if (t.ValueKind != JsonValueKind.Object) return (null, $"\"{TextsProperty}\" is not an object");
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in t.EnumerateObject())
                d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText();
            return (d, null);
        }
        catch (JsonException ex) { return (null, $"not valid JSON ({ex.Message})"); }
    }

    public static HmiLocalizationReport Analyze(HmiProjectSnapshot snap, HmiComponentIndex index)
    {
        var state = Read(snap, index);
        var all = state.Languages.SelectMany(l => l.Texts.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var missing = state.Languages.ToDictionary(l => l.Locale,
            l => (IReadOnlyList<string>)all.Where(k => !l.Texts.ContainsKey(k)).ToList());

        var used = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // key → "file:line"
        foreach (var page in index.Pages)
            foreach (var b in HmiMarkup.Bindings(snap.Files[page.Path]).Where(b => b.Kind == "l"))
                (used.TryGetValue(b.Target, out var w) ? w : used[b.Target] = []).Add($"{page.Path}:{b.Line}");

        var undefined = used.Where(u => !all.Contains(u.Key, StringComparer.Ordinal))
            .OrderBy(u => u.Key, StringComparer.Ordinal)
            .Select(u => (object)new { key = u.Key, usedAt = u.Value }).ToList();
        var unused = all.Where(k => !used.ContainsKey(k)).ToList();
        var langs = state.Languages.Select(l => (object)new
        {
            locale = l.Locale, path = l.Path, exists = l.Exists, keyCount = l.Texts.Count, problem = l.Problem,
            texts = l.Texts,
        }).ToList();
        return new HmiLocalizationReport(langs, all, missing, undefined, unused);
    }

    /// <summary>
    /// Applies <paramref name="changes"/> (key → text; null removes the key) to a .localization file. Existing keys
    /// keep their position, new keys are appended in ordinal order, every other property ("$schema", "locale")
    /// is kept. Returns null with an error when the file is not a localization object.
    /// </summary>
    public static (HmiLocalizationEdit? Edit, string? Error) Apply(string current, IReadOnlyDictionary<string, string?> changes)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(current, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return (null, $"not valid JSON ({ex.Message})"); }
        if (root is not JsonObject obj) return (null, "not a JSON object");

        if (obj[TextsProperty] is null) obj[TextsProperty] = new JsonObject();
        if (obj[TextsProperty] is not JsonObject texts) return (null, $"\"{TextsProperty}\" is not an object");

        var added = new List<string>();
        var changed = new List<string>();
        var removed = new List<string>();
        var absent = new List<string>();
        foreach (var (key, value) in changes.Where(c => texts.ContainsKey(c.Key)))
        {
            if (value is null) { texts.Remove(key); removed.Add(key); continue; }
            if (texts[key] is JsonValue v && v.TryGetValue(out string? old) && old == value) continue;
            texts[key] = value;
            changed.Add(key);
        }
        foreach (var (key, value) in changes.Where(c => !texts.ContainsKey(c.Key) && !removed.Contains(c.Key))
                     .OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            if (value is null) { absent.Add(key); continue; }
            texts[key] = value;
            added.Add(key);
        }
        string text = root.ToJsonString(WriteOptions);
        return (new HmiLocalizationEdit(text, added, changed, removed, absent), null);
    }

    /// <summary>Parses the tool input { "de": { "Key": "Text" | null } } into locale → key → text.</summary>
    public static (Dictionary<string, Dictionary<string, string?>>? Changes, string? Error) ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (null, "texts is required, e.g. { \"de\": { \"Start\": \"Start\" }, \"en\": { \"Start\": \"Start\" } } (null removes a key).");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, "texts must be a JSON object keyed by locale.");
            var result = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var lang in doc.RootElement.EnumerateObject())
            {
                if (lang.Value.ValueKind != JsonValueKind.Object)
                    return (null, $"texts.{lang.Name} must be an object of key → text.");
                var d = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var k in lang.Value.EnumerateObject())
                {
                    if (string.IsNullOrWhiteSpace(k.Name) || k.Name.Contains('%'))
                        return (null, $"texts.{lang.Name}: invalid key '{k.Name}'.");
                    d[k.Name] = k.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.String => k.Value.GetString(),
                        _ => throw new FormatException($"texts.{lang.Name}.{k.Name} must be a string or null."),
                    };
                }
                if (d.Count > 0) result[lang.Name] = d;
            }
            return result.Count == 0 ? (null, "texts contains no key.") : (result, null);
        }
        catch (JsonException ex) { return (null, $"texts is not valid JSON: {ex.Message}"); }
        catch (FormatException ex) { return (null, ex.Message); }
    }
}
