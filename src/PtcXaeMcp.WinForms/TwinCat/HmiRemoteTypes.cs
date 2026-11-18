using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Builds the project datatypes TE2000's "Create new project datatype → based on a remote type" produces for a
/// PLC type, from the <c>&lt;DataTypes&gt;</c> of a .tmc file. Pure XML → JSON; no XAE.
/// <para>What TE2000 does (decompiled engineering 14.17.22.1, <c>EditDatatypeWindow.CreateProjectDatatype</c>):
/// it copies the server definition of the type and of every sub-type it references, each as its own project
/// datatype named <c>&lt;PLC&gt;.&lt;Type&gt;</c>, with the references rewritten to
/// <c>tchmi:project#/definitions/…</c>. The server definitions themselves are produced in native code, so the
/// shapes here are the ones found in projects TE2000 wrote (see <see cref="HmiProjectDatatypes"/>):</para>
/// <list type="bullet">
/// <item>struct → <c>{"properties":{…},"type":"object"}</c>, a commented member as <c>allOf [ref, {comment}]</c>;
///   an <c>EXTENDS</c> base contributes its members first;</item>
/// <item>enum → <c>allOf [{&lt;attributes&gt;:"", enum, options}, {$ref INT16…}]</c>, the integer type from the
///   enum's base type;</item>
/// <item>array member → its own type <c>&lt;PLC&gt;.ARRAY_&lt;lo&gt;..&lt;hi&gt;_OF-&lt;Type&gt;</c> with
///   <c>startOffset</c> = the lower bound (the HMI counts from 0);</item>
/// <item>alias → resolved to its base type, never a definition of its own;</item>
/// <item><c>STRING(80)</c> → <c>tchmi:general#/definitions/STRING%2880%29</c>; another length has no general
///   definition and becomes <c>{"type":"string","maxLength":n}</c> (derived, not seen in a TE2000 file).</item>
/// </list>
/// Refused: function blocks, interfaces, pointers/references, multi-dimensional arrays, unknown types — the
/// result would not match what the engineering builds.
/// </summary>
internal static class HmiRemoteTypes
{
    internal const string GeneralRef = "tchmi:general#/definitions/";
    internal const int MaxDepth = 32;

    /// <summary>PLC elementary types that have a definition of the same name in tchmi.general.Schema.json.</summary>
    private static readonly HashSet<string> GeneralTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT",
        "REAL", "LREAL", "TIME", "LTIME", "TOD", "TIME_OF_DAY", "LTOD", "LTIME_OF_DAY", "DATE", "LDATE", "DT",
        "DATE_AND_TIME", "LDT", "LDATE_AND_TIME", "BIT",
    };

    /// <summary>The general integer type an enum is carried as (live: an INT enum is INT16).</summary>
    private static readonly Dictionary<string, string> EnumCarrier = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SINT"] = "INT8", ["USINT"] = "UINT8", ["BYTE"] = "UINT8",
        ["INT"] = "INT16", ["UINT"] = "UINT16", ["WORD"] = "UINT16",
        ["DINT"] = "INT32", ["UDINT"] = "UINT32", ["DWORD"] = "UINT32",
        ["LINT"] = "INT64", ["ULINT"] = "UINT64", ["LWORD"] = "UINT64",
    };

    /// <summary>The generated definitions, the requested type first, then its sub-types in the order met.</summary>
    internal sealed record Result(string RootName, IReadOnlyList<KeyValuePair<string, JsonNode>> Definitions);

    /// <summary>A PLC type name of the .tmc → the project datatypes for it under <paramref name="ns"/>.</summary>
    internal static (Result? Result, string? Error) Build(string tmcXml, string ns, string typeName)
    {
        XDocument doc;
        try { doc = XDocument.Parse(tmcXml); }
        catch (XmlException ex) { return (null, $"the TMC file is not valid XML ({ex.Message})"); }

        var types = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement dt in doc.Descendants("DataTypes").Elements("DataType"))
        {
            string? name = ((string?)dt.Element("Name"))?.Trim();
            if (!string.IsNullOrEmpty(name)) types.TryAdd(name, dt);
        }

        var builder = new Builder(ns, types);
        try
        {
            if (!types.TryGetValue(typeName, out XElement? root))
                return (null, $"the TMC file has no data type '{typeName}'" + Similar(types.Keys, typeName));
            string kind = KindOf(root);
            if (kind is not ("struct" or "enum"))
                return (null, $"'{typeName}' is {Article(kind)} — only a STRUCT or an ENUM becomes a project datatype " +
                              "(an alias is used through its base type)");
            builder.Define(root, 0);
            return (new Result(builder.NameOf(root), builder.Definitions), null);
        }
        catch (UnsupportedTypeException ex) { return (null, ex.Message); }
    }

    private static string Article(string kind) => kind switch
    {
        "alias" => "an alias",
        "functionBlock" => "a function block",
        "interface" => "an interface",
        _ => $"a {kind}",
    };

    private static string Similar(IEnumerable<string> names, string wanted)
    {
        var close = names.Where(n => n.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                     || wanted.Contains(n, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        return close.Count == 0 ? "" : $" (similar: {string.Join(", ", close)})";
    }

    /// <summary>struct, enum, alias, functionBlock, interface or gvl, by what the TMC carries.</summary>
    internal static string KindOf(XElement dt)
    {
        if (dt.Elements("EnumInfo").Any()) return "enum";
        if (dt.Elements("Method").Any() || dt.Element("Implements") is not null) return "functionBlock";
        if (dt.Element("Name")?.Attribute("Interface") is not null || dt.Element("Itf") is not null) return "interface";
        if (dt.Elements("SubItem").Any(s => (string?)s.Element("Name")?.Attribute("Static") == "true")) return "gvl";
        if (dt.Elements("SubItem").Any() || dt.Element("ExtendsType") is not null) return "struct";
        if (dt.Element("BaseType") is not null) return "alias";
        return "struct";
    }

    private sealed class UnsupportedTypeException(string message) : Exception(message);

    private sealed class Builder(string ns, Dictionary<string, XElement> types)
    {
        private readonly List<KeyValuePair<string, JsonNode>> _definitions = [];
        private readonly HashSet<string> _defined = new(StringComparer.Ordinal);
        private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<KeyValuePair<string, JsonNode>> Definitions => _definitions;

        public string NameOf(XElement dt) => $"{ns}.{((string)dt.Element("Name")!).Trim()}";

        /// <summary>Adds the definition of a struct or enum (once) and returns its project name.</summary>
        public string Define(XElement dt, int depth)
        {
            string name = NameOf(dt);
            if (_defined.Contains(name)) return name;
            string plcName = ((string)dt.Element("Name")!).Trim();
            if (depth > MaxDepth || !_inProgress.Add(plcName))
                throw new UnsupportedTypeException($"'{plcName}' references itself (recursive type) — not supported");

            int slot = _definitions.Count;
            _definitions.Add(new(name, new JsonObject())); // reserve the slot: the requested type stays first
            _defined.Add(name);
            JsonNode body = KindOf(dt) == "enum" ? Enum(dt) : Struct(dt, depth);
            _definitions[slot] = new(name, body);
            _inProgress.Remove(plcName);
            return name;
        }

        private JsonObject Struct(XElement dt, int depth)
        {
            var properties = new JsonObject();
            foreach (XElement member in Members(dt, depth))
            {
                string field = ((string?)member.Element("Name"))?.Trim() ?? "";
                string type = ((string?)member.Element("Type"))?.Trim() ?? "";
                if (field.Length == 0 || type.Length == 0) continue;
                List<XElement> dims = member.Elements("ArrayInfo").ToList();
                JsonNode reference = dims.Count switch
                {
                    0 => Reference(type, depth + 1),
                    1 => ArrayReference(type, dims[0], depth + 1),
                    _ => throw new UnsupportedTypeException(
                        $"member '{field}' is a {dims.Count}-dimensional array — not supported"),
                };
                string? comment = ((string?)member.Element("Comment"))?.Trim();
                properties[field] = string.IsNullOrEmpty(comment)
                    ? reference
                    : new JsonObject { ["allOf"] = new JsonArray(reference, new JsonObject { ["comment"] = comment }) };
            }
            return new JsonObject { ["properties"] = properties, ["type"] = "object" };
        }

        /// <summary>The members of a struct, those of its EXTENDS base first.</summary>
        private IEnumerable<XElement> Members(XElement dt, int depth)
        {
            string? extends = ((string?)dt.Element("ExtendsType"))?.Trim();
            if (!string.IsNullOrEmpty(extends))
            {
                if (depth > MaxDepth || !types.TryGetValue(extends, out XElement? baseType))
                    throw new UnsupportedTypeException($"the base type '{extends}' is not in the TMC file");
                foreach (XElement m in Members(baseType, depth + 1)) yield return m;
            }
            foreach (XElement m in dt.Elements("SubItem")) yield return m;
        }

        private static JsonObject Enum(XElement dt)
        {
            var head = new JsonObject();
            foreach (XElement p in dt.Element("Properties")?.Elements("Property")
                                       .OrderBy(p => (string?)p.Element("Name"), StringComparer.Ordinal)
                                   ?? Enumerable.Empty<XElement>())
            {
                string? key = ((string?)p.Element("Name"))?.Trim();
                if (!string.IsNullOrEmpty(key) && !head.ContainsKey(key))
                    head[key] = ((string?)p.Element("Value"))?.Trim() ?? "";
            }
            var values = new JsonArray();
            var options = new JsonArray();
            foreach (XElement e in dt.Elements("EnumInfo"))
            {
                string label = ((string?)e.Element("Text"))?.Trim() ?? "";
                string raw = ((string?)e.Element("Enum"))?.Trim() ?? "";
                if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    throw new UnsupportedTypeException($"enum value '{raw}' of '{label}' is not an integer");
                values.Add(value.ToString(CultureInfo.InvariantCulture));
                options.Add(new JsonObject { ["value"] = value, ["label"] = label });
            }
            head["enum"] = values;
            head["options"] = options;
            string baseType = ((string?)dt.Element("BaseType"))?.Trim() ?? "INT";
            string carrier = EnumCarrier.TryGetValue(baseType, out string? c) ? c : "INT16";
            return new JsonObject
            {
                ["allOf"] = new JsonArray(head, new JsonObject { ["$ref"] = GeneralRef + carrier }),
            };
        }

        /// <summary>The reference for a member type: general, inline string, or a project datatype.</summary>
        private JsonNode Reference(string type, int depth)
        {
            if (GeneralTypes.Contains(type)) return new JsonObject { ["$ref"] = GeneralRef + type.ToUpperInvariant() };
            if (TryString(type, out JsonNode? text)) return text!;
            if (type.StartsWith("POINTER TO", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("REFERENCE TO", StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedTypeException($"'{type}' (pointer/reference) has no HMI representation — not supported");
            if (!types.TryGetValue(type, out XElement? dt))
                throw new UnsupportedTypeException($"the type '{type}' is not in the TMC file and is not an elementary type");
            return KindOf(dt) switch
            {
                "struct" or "enum" => new JsonObject { ["$ref"] = HmiProjectDatatypes.ProjectRefPrefix + Escape(Define(dt, depth)) },
                "alias" => AliasReference(dt, depth),
                string other => throw new UnsupportedTypeException(
                    $"'{type}' is {Article(other)} — only structs, enums, aliases and elementary types are supported"),
            };
        }

        private JsonNode AliasReference(XElement dt, int depth)
        {
            if (depth > MaxDepth) throw new UnsupportedTypeException("alias chain too deep");
            XElement baseType = dt.Element("BaseType")!;
            string target = baseType.Value.Trim();
            List<XElement> dims = dt.Elements("ArrayInfo").ToList();
            return dims.Count switch
            {
                0 => Reference(target, depth + 1),
                1 => ArrayReference(target, dims[0], depth + 1),
                _ => throw new UnsupportedTypeException($"alias '{(string?)dt.Element("Name")}' is a multi-dimensional array — not supported"),
            };
        }

        private JsonNode ArrayReference(string elementType, XElement info, int depth)
        {
            if (!int.TryParse((string?)info.Element("LBound"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
                || !int.TryParse((string?)info.Element("Elements"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || count < 1)
                throw new UnsupportedTypeException($"array of '{elementType}' has no readable LBound/Elements");
            JsonNode items = Reference(elementType, depth);
            string name = $"{ns}.ARRAY_{lo.ToString(CultureInfo.InvariantCulture)}..{(lo + count - 1).ToString(CultureInfo.InvariantCulture)}_OF-{elementType}";
            if (_defined.Add(name))
                _definitions.Add(new(name, new JsonObject
                {
                    ["items"] = items,
                    ["type"] = "array",
                    ["minItems"] = count,
                    ["maxItems"] = count,
                    ["startOffset"] = lo,
                }));
            return new JsonObject { ["$ref"] = HmiProjectDatatypes.ProjectRefPrefix + Escape(name) };
        }

        private static bool TryString(string type, out JsonNode? node)
        {
            node = null;
            string upper = type.ToUpperInvariant().Replace(" ", "", StringComparison.Ordinal);
            bool wide = upper.StartsWith("WSTRING", StringComparison.Ordinal);
            if (!wide && !upper.StartsWith("STRING", StringComparison.Ordinal)) return false;
            string rest = upper[(wide ? 7 : 6)..];
            int length = 80;
            if (rest.Length > 0)
            {
                if (!(rest.StartsWith('(') && rest.EndsWith(')'))
                    || !int.TryParse(rest[1..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                    return false;
            }
            node = !wide && length == 80
                ? new JsonObject { ["$ref"] = GeneralRef + "STRING%2880%29" }
                : new JsonObject { ["type"] = "string", ["maxLength"] = length };
            return true;
        }

        /// <summary>A definition name inside a reference, encoded the way TE2000 writes it (brackets).</summary>
        private static string Escape(string name) =>
            name.Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);
    }
}
