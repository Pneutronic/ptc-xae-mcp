using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.ToolConsole;

/// <summary>Kind of input control a tool argument gets in the console form (issue #271).</summary>
internal enum ToolFieldKind
{
    Text,
    Boolean,
    Choice,
    Integer,
    Number,
    Json,
}

/// <summary>
/// One argument of a tool as derived from its JSON input schema. Pure data: no WinForms, no
/// MCP client, so the schema-to-form mapping is unit-testable.
/// </summary>
/// <param name="Name">Property name in the arguments object.</param>
/// <param name="Kind">Control kind.</param>
/// <param name="Description">Schema description, if any.</param>
/// <param name="Required">Listed in the schema's <c>required</c> array.</param>
/// <param name="DefaultText">Pre-fill text from the schema's <c>default</c> (null = none).</param>
/// <param name="Choices">Enum values as text (only for <see cref="ToolFieldKind.Choice"/>).</param>
/// <param name="ChoiceBase">Underlying type of the enum values (Text, Integer or Number).</param>
internal sealed record ToolField(
    string Name,
    ToolFieldKind Kind,
    string? Description,
    bool Required,
    string? DefaultText,
    IReadOnlyList<string> Choices,
    ToolFieldKind ChoiceBase = ToolFieldKind.Text);

/// <summary>Schema to form-field mapping.</summary>
internal static class ToolFormModel
{
    /// <summary>
    /// Maps a tool's <c>inputSchema</c> (an object schema) to its form fields, in schema order.
    /// A schema without properties yields no fields; anything unrecognised becomes a JSON field.
    /// </summary>
    public static IReadOnlyList<ToolField> FromSchema(JsonElement inputSchema)
    {
        var fields = new List<ToolField>();
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var props) ||
            props.ValueKind != JsonValueKind.Object)
            return fields;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (inputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && r.GetString() is { } rs)
                    required.Add(rs);

        foreach (var p in props.EnumerateObject())
            fields.Add(MapProperty(p.Name, p.Value, required.Contains(p.Name)));
        return fields;
    }

    /// <summary>Initial text per field: the schema default, or "false" for booleans without one.</summary>
    public static IReadOnlyDictionary<string, string> InitialValues(IReadOnlyList<ToolField> fields)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in fields)
            map[f.Name] = f.DefaultText ?? (f.Kind == ToolFieldKind.Boolean ? "false" : string.Empty);
        return map;
    }

    private static ToolField MapProperty(string name, JsonElement prop, bool required)
    {
        var eff = Effective(prop);
        string? description = StringProp(prop, "description") ?? StringProp(eff, "description");
        string? defaultText = DefaultOf(prop) ?? DefaultOf(eff);

        string? type = TypeOf(eff);
        JsonElement enumEl = default;
        bool hasEnum = eff.ValueKind == JsonValueKind.Object &&
                       eff.TryGetProperty("enum", out enumEl) && enumEl.ValueKind == JsonValueKind.Array;

        if (hasEnum && (type is null or "string" or "integer" or "number"))
        {
            var choices = new List<string>();
            foreach (var v in enumEl.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Null) continue;
                choices.Add(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText());
            }
            var baseKind = type switch
            {
                "integer" => ToolFieldKind.Integer,
                "number" => ToolFieldKind.Number,
                _ => ToolFieldKind.Text,
            };
            return new ToolField(name, ToolFieldKind.Choice, description, required, defaultText, choices, baseKind);
        }

        var kind = type switch
        {
            "string" => ToolFieldKind.Text,
            "boolean" => ToolFieldKind.Boolean,
            "integer" => ToolFieldKind.Integer,
            "number" => ToolFieldKind.Number,
            _ => ToolFieldKind.Json, // object, array, untyped
        };
        return new ToolField(name, kind, description, required, defaultText, []);
    }

    /// <summary>
    /// The sub-schema that carries the type: descends into the first non-null branch of
    /// <c>anyOf</c>/<c>oneOf</c> (how nullable parameters are usually expressed).
    /// </summary>
    private static JsonElement Effective(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return p;
        foreach (var key in new[] { "anyOf", "oneOf" })
        {
            if (!p.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var sub in arr.EnumerateArray())
                if (!IsNullSchema(sub)) return Effective(sub);
        }
        return p;
    }

    private static bool IsNullSchema(JsonElement s) =>
        s.ValueKind == JsonValueKind.Object &&
        s.TryGetProperty("type", out var t) &&
        t.ValueKind == JsonValueKind.String && t.GetString() == "null";

    private static string? TypeOf(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) return null;
        if (!s.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
            foreach (var e in t.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { } es && es != "null")
                    return es;
        return null;
    }

    private static string? StringProp(JsonElement s, string key) =>
        s.ValueKind == JsonValueKind.Object && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? DefaultOf(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty("default", out var d)) return null;
        return d.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => d.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => d.GetRawText(),
        };
    }
}

/// <summary>Result of turning form values into a <c>tools/call</c> arguments object.</summary>
internal sealed record ToolArgumentsResult(JsonObject Arguments, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Recorded arguments mapped onto a form; see <see cref="ToolArgumentBuilder.Replay"/>.</summary>
/// <param name="Values">Text per known field.</param>
/// <param name="Unknown">Recorded keys the schema does not declare (never pre-filled).</param>
/// <param name="Warnings">Human-readable problems.</param>
/// <param name="Incomplete">True when this cannot be treated as an exact replay (also: required or all arguments absent).</param>
/// <param name="HasMismatch">True when a recorded key is unknown to the schema or an enum value did not match.</param>
internal sealed record ToolReplay(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> Warnings,
    bool Incomplete,
    bool HasMismatch);

/// <summary>Form values to arguments JSON, and back (for "Run again").</summary>
internal static class ToolArgumentBuilder
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Builds the arguments object. Empty optional fields are omitted, empty required fields
    /// and unparsable numbers/JSON are reported in <see cref="ToolArgumentsResult.Errors"/>.
    /// </summary>
    public static ToolArgumentsResult Build(
        IReadOnlyList<ToolField> fields, IReadOnlyDictionary<string, string?> values)
    {
        var args = new JsonObject();
        var errors = new List<string>();
        foreach (var f in fields)
        {
            values.TryGetValue(f.Name, out var raw);
            switch (f.Kind)
            {
                case ToolFieldKind.Boolean:
                {
                    bool value = string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    bool dflt = string.Equals(f.DefaultText, "true", StringComparison.OrdinalIgnoreCase);
                    // A checkbox cannot be "unset": send it when required or when it differs from the default.
                    if (f.Required || value != dflt) args[f.Name] = value;
                    break;
                }
                case ToolFieldKind.Text:
                    if (raw == EmptyStringToken) args[f.Name] = string.Empty; // explicit "" (e.g. to clear a value)
                    else if (string.IsNullOrEmpty(raw)) MissingIfRequired(f, errors);
                    else args[f.Name] = raw;
                    break;
                case ToolFieldKind.Choice:
                    if (string.IsNullOrEmpty(raw)) { MissingIfRequired(f, errors); break; }
                    AddNumberOrText(f, f.ChoiceBase, raw, args, errors);
                    break;
                case ToolFieldKind.Integer:
                case ToolFieldKind.Number:
                    if (string.IsNullOrWhiteSpace(raw)) { MissingIfRequired(f, errors); break; }
                    AddNumberOrText(f, f.Kind, raw, args, errors);
                    break;
                case ToolFieldKind.Json:
                    if (string.IsNullOrWhiteSpace(raw)) { MissingIfRequired(f, errors); break; }
                    try
                    {
                        args[f.Name] = JsonNode.Parse(raw);
                    }
                    catch (JsonException ex)
                    {
                        errors.Add(Loc.Format("toolconsole.error.invalidjson", f.Name, ex.Message));
                    }
                    break;
            }
        }
        return new ToolArgumentsResult(args, errors);
    }

    /// <summary>Text a user types in a text field to send an explicit empty string.</summary>
    public const string EmptyStringToken = "\"\"";

    /// <summary>
    /// Maps recorded arguments (an Activity entry or a favourite) onto the form. Only keys the
    /// schema declares are pre-filled; everything else is reported, never filled silently.
    /// An Activity entry holds the summary the tool traced, not necessarily its real arguments,
    /// so <see cref="ToolReplay.Incomplete"/> flags anything that cannot be an exact replay.
    /// </summary>
    public static ToolReplay Replay(IReadOnlyList<ToolField> fields, JsonObject arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var known = new HashSet<string>(fields.Select(f => f.Name), StringComparer.Ordinal);
        bool incomplete = false;
        bool mismatch = false;

        foreach (var f in fields)
        {
            if (!arguments.TryGetPropertyValue(f.Name, out var node) || node is null)
            {
                if (f.Required) { incomplete = true; warnings.Add(Loc.Format("toolconsole.warning.requirednotrecorded", f.Name)); }
                continue;
            }

            switch (f.Kind)
            {
                case ToolFieldKind.Json:
                    values[f.Name] = node.ToJsonString(Indented);
                    break;
                case ToolFieldKind.Boolean:
                    values[f.Name] = node.ToJsonString().ToLowerInvariant();
                    break;
                case ToolFieldKind.Choice:
                {
                    string recorded = node is JsonValue cv && cv.TryGetValue<string>(out var cs) ? cs : node.ToJsonString();
                    // Case-insensitive match; a number is never guessed to be an enum index.
                    string? match = f.Choices.FirstOrDefault(c => string.Equals(c, recorded, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        incomplete = mismatch = true;
                        warnings.Add(Loc.Format("toolconsole.warning.notachoice", f.Name, node.ToJsonString(), string.Join(", ", f.Choices)));
                    }
                    else
                    {
                        values[f.Name] = match;
                    }
                    break;
                }
                case ToolFieldKind.Text:
                {
                    string s = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
                    values[f.Name] = s.Length == 0 ? EmptyStringToken : s;
                    break;
                }
                default:
                    values[f.Name] = node is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : node.ToJsonString();
                    break;
            }
        }

        var unknown = arguments.Select(p => p.Key).Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            incomplete = mismatch = true;
            warnings.Add(Loc.Format("toolconsole.warning.unknownkeys", string.Join(", ", unknown)));
        }
        if (arguments.Count == 0 && fields.Count > 0)
        {
            incomplete = true;
            warnings.Add(Loc.Get("toolconsole.warning.noarguments"));
        }
        return new ToolReplay(values, unknown, warnings, incomplete, mismatch);
    }

    /// <summary>Parses recorded arguments text; null when not a JSON object. Empty text is an empty object.</summary>
    public static JsonObject? TryParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static void MissingIfRequired(ToolField f, List<string> errors)
    {
        if (f.Required) errors.Add(Loc.Format("toolconsole.error.required", f.Name));
    }

    private static void AddNumberOrText(
        ToolField f, ToolFieldKind kind, string raw, JsonObject args, List<string> errors)
    {
        string t = raw.Trim();
        switch (kind)
        {
            case ToolFieldKind.Integer:
                if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) args[f.Name] = l;
                else errors.Add(Loc.Format("toolconsole.error.notinteger", f.Name, raw));
                break;
            case ToolFieldKind.Number:
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) &&
                    double.IsFinite(d)) args[f.Name] = d;
                else errors.Add(Loc.Format("toolconsole.error.notnumber", f.Name, raw));
                break;
            default:
                args[f.Name] = raw;
                break;
        }
    }
}
