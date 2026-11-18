using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One definition of an HMI project's <c>tchmi.project.Schema.json</c>.</summary>
/// <param name="Kind">userControl, struct, enum, array, alias or other — see <see cref="HmiProjectDatatypes.Classify"/>.</param>
/// <param name="Namespace">The part before the first '.' of a remote type name (<c>PLC_TMC</c> in
/// <c>PLC_TMC.ST_Rec</c>), null for a name without a dot.</param>
/// <param name="Refs">Every <c>$ref</c> in the definition, in document order, without duplicates.</param>
/// <param name="DanglingRefs">Project references (<c>tchmi:project#/definitions/X</c>) whose target is not a
/// definition of the same file — the leftover TE2000 leaves after a type is deleted.</param>
internal sealed record HmiProjectDatatype(
    string Name,
    string Kind,
    string? Namespace,
    IReadOnlyList<string> Refs,
    IReadOnlyList<string> DanglingRefs);

/// <summary>A schema file as read from disk. <see cref="Error"/> set = unreadable (callers fail closed);
/// <see cref="Exists"/> false = no such file.</summary>
internal sealed record HmiSchemaFile(
    string Path,
    bool Exists,
    string? Error,
    IReadOnlyList<HmiProjectDatatype> Definitions)
{
    public bool Readable => Exists && Error is null;

    /// <summary>The raw <c>definitions</c> object (for the diff and for writes); null unless readable.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonObject? Raw { get; init; }
}

/// <summary>How the two schema files differ. Empty lists = identical definitions.</summary>
internal sealed record HmiSchemaDiff(
    IReadOnlyList<string> OnlyInSchema,
    IReadOnlyList<string> OnlyInTemplate,
    IReadOnlyList<string> Different)
{
    public bool Same => OnlyInSchema.Count == 0 && OnlyInTemplate.Count == 0 && Different.Count == 0;
}

/// <summary>
/// The project datatypes of a TwinCAT HMI project: the <c>definitions</c> of
/// <c>Properties/tchmi.project.Schema.json</c> and its twin <c>tchmi.project.Schema.tpl.json</c>. TE2000 keeps
/// user-control parameter types and the types created "based on a remote type" there, named
/// <c>&lt;PLC&gt;.&lt;Type&gt;</c>. Pure file logic — no XAE needed. Engineering 14.17.22.1 (decompiled) writes only
/// the <c>.json</c>; engineering 1.12 was seen writing both, so a difference is reported, never hidden.
/// <para>Shapes TE2000 writes (read from real projects, engineering 1.12 and 1.14):</para>
/// <list type="bullet">
/// <item>user control: <c>frameworkControlType</c>, <c>frameworkInstanceOf</c>, … ;</item>
/// <item>struct: <c>{"type":"object","properties":{f:{"$ref":…} | {"allOf":[{"$ref":…},{"comment":…}]}}}</c>;</item>
/// <item>enum: <c>{"allOf":[{…PLC attributes…,"enum":["0",…],"options":[{"value":0,"label":"…"}]},
///   {"$ref":"tchmi:general#/definitions/INT16"}]}</c>;</item>
/// <item>array: <c>{"type":"array","items":{"$ref":…},"minItems":n,"maxItems":n,"startOffset":LBound}</c>,
///   named <c>&lt;PLC&gt;.ARRAY_1..5_OF-&lt;Type&gt;</c>.</item>
/// </list>
/// </summary>
internal static class HmiProjectDatatypes
{
    internal const string PropertiesFolder = "Properties";
    internal const string SchemaFileName = "tchmi.project.Schema.json";
    internal const string TemplateFileName = "tchmi.project.Schema.tpl.json";
    internal const string ProjectRefPrefix = "tchmi:project#/definitions/";
    internal const string LocalRefPrefix = "#/definitions/";

    internal static string SchemaPath(string projectDirectory) =>
        Path.Combine(projectDirectory, PropertiesFolder, SchemaFileName);

    internal static string TemplatePath(string projectDirectory) =>
        Path.Combine(projectDirectory, PropertiesFolder, TemplateFileName);

    /// <summary>Reads one schema file. A missing file is <c>Exists=false</c>, not an error; a file that is not a
    /// JSON object with an object <c>definitions</c> (or none) is an error.</summary>
    internal static HmiSchemaFile Read(string path)
    {
        if (!File.Exists(path)) return new HmiSchemaFile(path, false, null, []);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HmiSchemaFile(path, true, ex.Message, []);
        }
        var (defs, error) = ParseDefinitions(text);
        return defs is null
            ? new HmiSchemaFile(path, true, error, [])
            : new HmiSchemaFile(path, true, null, Describe(defs)) { Raw = defs };
    }

    /// <summary>The <c>definitions</c> object of a schema text, or an error. An absent <c>definitions</c> is an
    /// empty object (a fresh project's file has <c>"definitions": { }</c>).</summary>
    internal static (JsonObject? Definitions, string? Error) ParseDefinitions(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { return (null, $"not valid JSON ({ex.Message})"); }
        if (root is not JsonObject obj) return (null, "the root is not a JSON object");
        if (!obj.TryGetPropertyValue("definitions", out JsonNode? defs) || defs is null) return (new JsonObject(), null);
        return defs is JsonObject o ? (o, null) : (null, "'definitions' is not a JSON object");
    }

    internal static IReadOnlyList<HmiProjectDatatype> Describe(JsonObject definitions)
    {
        var names = definitions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var list = new List<HmiProjectDatatype>();
        foreach (var (name, node) in definitions)
        {
            List<string> refs = CollectRefs(node).Distinct(StringComparer.Ordinal).ToList();
            List<string> dangling = refs
                .Select(ProjectRefTarget)
                .Where(t => t is not null && !names.Contains(t))
                .Select(t => t!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            list.Add(new HmiProjectDatatype(name, Classify(node), NamespaceOf(name), refs, dangling));
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>userControl / struct / enum / array / alias / other, by the shape TE2000 writes.</summary>
    internal static string Classify(JsonNode? node)
    {
        if (node is not JsonObject obj) return "other";
        if (obj.ContainsKey("frameworkControlType") || obj.ContainsKey("frameworkInstanceOf")) return "userControl";
        string? type = obj["type"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
        if (type == "array" || obj.ContainsKey("items")) return "array";
        if (obj["allOf"] is JsonArray all && all.OfType<JsonObject>().Any(o => o.ContainsKey("options") || o.ContainsKey("enum")))
            return "enum";
        if (obj.ContainsKey("options") || obj.ContainsKey("enum")) return "enum";
        if (type == "object" || obj.ContainsKey("properties")) return "struct";
        if (obj.ContainsKey("$ref") || obj["allOf"] is JsonArray) return "alias";
        return "other";
    }

    internal static string? NamespaceOf(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? name[..dot] : null;
    }

    /// <summary>The definition name a project reference points at (URL-decoded, as TE2000 encodes
    /// <c>STRING(80)</c> as <c>STRING%2880%29</c>), or null for any other reference.</summary>
    internal static string? ProjectRefTarget(string reference)
    {
        string? raw = reference.StartsWith(ProjectRefPrefix, StringComparison.Ordinal) ? reference[ProjectRefPrefix.Length..]
            : reference.StartsWith(LocalRefPrefix, StringComparison.Ordinal) ? reference[LocalRefPrefix.Length..]
            : null;
        if (raw is null) return null;
        try { return Uri.UnescapeDataString(raw); }
        catch (UriFormatException) { return raw; }
    }

    internal static IEnumerable<string> CollectRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (key == "$ref" && child is JsonValue v && v.TryGetValue(out string? s)) yield return s;
                    else foreach (string r in CollectRefs(child)) yield return r;
                }
                break;
            case JsonArray arr:
                foreach (JsonNode? child in arr)
                    foreach (string r in CollectRefs(child)) yield return r;
                break;
        }
    }

    /// <summary>Compares the definitions of the two files by name and content. Callers pass the raw
    /// <c>definitions</c> objects so "different" means a real content difference, not a formatting one.</summary>
    internal static HmiSchemaDiff Diff(JsonObject schema, JsonObject template)
    {
        var onlySchema = schema.Select(p => p.Key).Where(k => !template.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var onlyTemplate = template.Select(p => p.Key).Where(k => !schema.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var different = schema
            .Where(p => template.ContainsKey(p.Key) && !JsonNode.DeepEquals(p.Value, template[p.Key]))
            .Select(p => p.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new HmiSchemaDiff(onlySchema, onlyTemplate, different);
    }
}
