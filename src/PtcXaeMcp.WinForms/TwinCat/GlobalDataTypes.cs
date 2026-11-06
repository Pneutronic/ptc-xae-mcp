using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A type a DataType refers to: a SubItem's Type, a BaseType or an ExtendsType (name, GUID when given, library namespace).</summary>
internal sealed record TypeReference(string Name, string Guid, string? Namespace);

/// <summary>
/// One type of the TwinCAT type system as ExportXml(false) gives it. <c>Persistent</c>: <c>PersistentType="true"</c>, a
/// type of the project kept in the .tsproj. <c>BuiltIn</c>: <c>TcBaseType="true"</c>. <c>DataTypeXml</c> is the DataType
/// element alone, <c>References</c> the types it uses.
/// </summary>
internal sealed record TypeSystemType(
    string Name, string Guid, string Kind, bool Persistent, bool BuiltIn, string? Namespace,
    string DataTypeXml, IReadOnlyList<TypeReference> References)
{
    public bool IsEventClass => Kind == GlobalDataTypeXml.KindEventClass;

    /// <summary>"STRUCT 'ST_Axis' {GUID}".</summary>
    public string Label => Guid.Length == 0 ? $"{Kind} '{Name}'" : $"{Kind} '{Name}' {Guid}";

    /// <summary>Where the type comes from, for messages.</summary>
    public string Origin =>
        BuiltIn ? "built into TwinCAT"
        : Persistent ? "the project (.tsproj)"
        : Namespace is { Length: > 0 } ns ? $"the library '{ns}'"
        : Guid.Length == 0 ? "a PLC project (its own DUT, exported through the PLC's TMC)"
        : "a TMC file (AdditionalTmcFile, a C++ module or a library)";
}

/// <summary>
/// DataType XML of the type system (TcModuleClass schema) for "Add Global Data Type" of any type: kind, references,
/// the comparison of two definitions and the pin nodes. Event classes keep their own reader in <see cref="EventClassXml"/>.
/// </summary>
internal static class GlobalDataTypeXml
{
    public const string KindEventClass = "event class";
    public const string KindStruct = "STRUCT";
    public const string KindUnion = "UNION";
    public const string KindEnum = "ENUM";
    public const string KindAlias = "ALIAS";
    public const string KindFunctionBlock = "FUNCTION_BLOCK";
    public const string KindOther = "data type";

    // The GUID prefix TwinCAT gives its elementary types (BOOL {18071995-…-000000000030}, INT, STRING(n) …).
    private const string ElementaryGuidPrefix = "18071995-";

    private static readonly HashSet<string> Elementary = new(StringComparer.OrdinalIgnoreCase)
    {
        "BOOL", "BIT", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "USINT", "INT", "UINT", "DINT", "UDINT", "LINT", "ULINT",
        "REAL", "LREAL", "TIME", "LTIME", "DATE", "LDATE", "TIME_OF_DAY", "TOD", "LTOD", "DATE_AND_TIME", "DT", "LDT",
        "STRING", "WSTRING", "XINT", "UXINT", "XWORD", "PVOID", "OTCID", "HRESULT", "ANY",
    };

    /// <summary>
    /// The type in <paramref name="exportXml"/> (a DataType, or ExportXml's <c>&lt;DataTypes&gt;</c> wrapper — with
    /// <paramref name="name"/> the DataType of that name, else the first); null when there is none or the XML is not readable.
    /// </summary>
    public static TypeSystemType? Parse(string? exportXml, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(exportXml)) return null;
        XElement? root;
        try { root = XDocument.Parse(exportXml).Root; }
        catch (XmlException) { return null; }
        if (root is null) return null;
        var types = root.Name.LocalName == "DataType"
            ? [root]
            : root.Descendants().Where(e => e.Name.LocalName == "DataType").ToList();
        var type = (name is null ? null : types.FirstOrDefault(t => string.Equals(NameOf(t), name, StringComparison.OrdinalIgnoreCase)))
                   ?? types.FirstOrDefault();
        return type is null ? null : FromElement(type);
    }

    /// <summary>Every DataType of a TMC document such as ExternalTypes.tmc; empty when none or not readable.</summary>
    public static IReadOnlyList<TypeSystemType> DataTypesInTmc(string? tmc)
    {
        if (string.IsNullOrWhiteSpace(tmc)) return [];
        XDocument doc;
        try { doc = XDocument.Parse(tmc); }
        catch (XmlException) { return []; }
        if (doc.Root is null) return [];
        return doc.Root.Descendants().Where(e => e.Name.LocalName == "DataType").Select(FromElement).OfType<TypeSystemType>().ToList();
    }

    private static TypeSystemType? FromElement(XElement type)
    {
        var name = Child(type, "Name");
        if (name is null || name.Value.Trim().Length == 0) return null;
        return new TypeSystemType(
            name.Value.Trim(),
            EventClassXml.FormatGuid((string?)name.Attribute("GUID") ?? string.Empty),
            KindOf(type),
            IsTrue(name.Attribute("PersistentType")),
            IsTrue(name.Attribute("TcBaseType")),
            NullIfEmpty((string?)name.Attribute("Namespace")),
            type.ToString(SaveOptions.DisableFormatting),
            ReferencesOf(type));
    }

    private static string KindOf(XElement type)
    {
        if (type.Elements().Any(e => e.Name.LocalName == "EventId")) return KindEventClass;
        if (type.Elements().Any(e => e.Name.LocalName == "EnumInfo")) return KindEnum;
        bool functionBlock = type.Elements().Where(e => e.Name.LocalName == "Properties").Elements()
            .Any(p => string.Equals(Child(p, "Name")?.Value.Trim(), "PouType", StringComparison.OrdinalIgnoreCase));
        if (functionBlock) return KindFunctionBlock;
        if (type.Elements().Any(e => e.Name.LocalName == "SubItem"))
        {
            // A UNION's members all start at offset 0.
            var offsets = type.Elements().Where(e => e.Name.LocalName == "SubItem")
                .Select(s => Child(s, "BitOffs")?.Value.Trim() ?? string.Empty).ToList();
            return offsets.Count > 1 && offsets.All(o => o == "0") ? KindUnion : KindStruct;
        }
        if (Child(type, "BaseType") is not null) return KindAlias;
        return KindOther;
    }

    private static IReadOnlyList<TypeReference> ReferencesOf(XElement type)
    {
        var refs = new List<TypeReference>();
        foreach (var e in type.Descendants().Where(e => e.Name.LocalName is "Type" or "BaseType" or "ExtendsType"))
        {
            if (e.HasElements) continue;
            string name = e.Value.Trim();
            if (name.Length == 0) continue;
            var r = new TypeReference(name, EventClassXml.FormatGuid((string?)e.Attribute("GUID") ?? string.Empty),
                NullIfEmpty((string?)e.Attribute("Namespace")));
            if (!refs.Any(x => SameReference(x, r))) refs.Add(r);
        }
        return refs;
    }

    private static bool SameReference(TypeReference a, TypeReference b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Namespace, b.Namespace, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An elementary type the type system need not hold: a GUID of TwinCAT's elementary range, an elementary name, a sized
    /// STRING(n)/WSTRING(n), or an ARRAY/POINTER/REFERENCE spelled out in the reference itself.
    /// </summary>
    public static bool IsElementary(TypeReference r)
    {
        if (r.Guid.TrimStart('{').StartsWith(ElementaryGuidPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        string n = r.Name.Trim();
        if (Elementary.Contains(n)) return true;
        int paren = n.IndexOfAny(['(', '[']);
        if (paren > 0 && Elementary.Contains(n[..paren].Trim())) return true;
        return n.StartsWith("ARRAY", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("POINTER TO", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("REFERENCE TO", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether two DataType definitions are the same: element names, attribute values (in any order, without
    /// <c>PersistentType</c>, which only steers the .tsproj) and trimmed texts (CDATA or not) all match.
    /// </summary>
    public static bool SameDefinition(string a, string b)
    {
        XElement x, y;
        try { x = XElement.Parse(a); y = XElement.Parse(b); }
        catch (XmlException) { return false; }
        return Canonical(x) == Canonical(y);
    }

    private static string Canonical(XElement e)
    {
        var sb = new System.Text.StringBuilder();
        Write(e, sb);
        return sb.ToString();

        static void Write(XElement e, System.Text.StringBuilder sb)
        {
            sb.Append('<').Append(e.Name.LocalName);
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "PersistentType")
                         .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal))
            {
                string value = a.Name.LocalName == "GUID" ? EventClassXml.FormatGuid(a.Value) : a.Value;
                sb.Append(' ').Append(a.Name.LocalName).Append("=\"").Append(value).Append('"');
            }
            sb.Append('>');
            if (e.HasElements)
                foreach (var c in e.Elements()) Write(c, sb);
            else
                sb.Append(e.Value.Trim());
            sb.Append("</").Append(e.Name.LocalName).Append('>');
        }
    }

    /// <summary>
    /// Whether a node under ExternalTypes.tmc (<c>Global.ST_Axis</c>) stands for <paramref name="type"/>. An event class
    /// shows as <c>ST_&lt;class&gt;</c> once the PLC has mapped it.
    /// </summary>
    public static bool PinNodeIs(string node, TypeSystemType type)
    {
        string leaf = node.Contains('.') ? node[(node.LastIndexOf('.') + 1)..] : node;
        return string.Equals(leaf, type.Name, StringComparison.OrdinalIgnoreCase)
               || (type.IsEventClass && string.Equals(leaf, "ST_" + type.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Same GUID (both given), or else the same name.</summary>
    public static bool SameType(TypeSystemType a, TypeSystemType b) =>
        a.Guid.Length > 0 && b.Guid.Length > 0
            ? string.Equals(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement e, string localName) => e.Elements().FirstOrDefault(x => x.Name.LocalName == localName);

    private static string NameOf(XElement type) => Child(type, "Name")?.Value.Trim() ?? string.Empty;

    private static bool IsTrue(XAttribute? a) => string.Equals((string?)a, "true", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>A type a pinned type uses that is not taken along, and why.</summary>
internal sealed record SkippedReference(TypeReference Reference, string UsedBy, string Why);

/// <summary>The types to pin: the dependencies first (each before the types that use it), the requested type last.</summary>
internal sealed record GlobalDataTypeClosure(IReadOnlyList<TypeSystemType> Types, IReadOnlyList<SkippedReference> Skipped)
{
    public TypeSystemType Target => Types[^1];
    public IEnumerable<TypeSystemType> Dependencies => Types.Take(Types.Count - 1);

    /// <summary>
    /// <paramref name="target"/> with every project type (PersistentType) it uses, recursively. Built-in and elementary
    /// types are not copied (every project has them); a type from a library, a TMC file or a PLC is not copied either and
    /// is listed in <see cref="Skipped"/>, as is a reference the type system does not resolve.
    /// </summary>
    public static GlobalDataTypeClosure Resolve(TypeSystemType target, IReadOnlyList<TypeSystemType> all)
    {
        var ordered = new List<TypeSystemType>();
        var skipped = new List<SkippedReference>();
        var visiting = new HashSet<TypeSystemType>(ReferenceEqualityComparer.Instance);
        var done = new HashSet<TypeSystemType>(ReferenceEqualityComparer.Instance);
        Visit(target);
        return new GlobalDataTypeClosure(ordered, skipped);

        void Visit(TypeSystemType type)
        {
            if (done.Contains(type) || !visiting.Add(type)) return; // a cycle (POINTER TO itself) is already on the way
            foreach (var r in type.References)
            {
                var found = Find(r, all);
                if (found is null)
                {
                    if (!GlobalDataTypeXml.IsElementary(r))
                        AddSkipped(r, type, "it is not in the type system");
                    continue;
                }
                if (ReferenceEquals(found, type) || found.BuiltIn) continue;
                if (!found.Persistent)
                {
                    AddSkipped(r, type, $"it comes from {found.Origin}");
                    continue;
                }
                Visit(found);
            }
            visiting.Remove(type);
            done.Add(type);
            ordered.Add(type);
        }

        void AddSkipped(TypeReference r, TypeSystemType by, string why)
        {
            if (!skipped.Any(s => string.Equals(s.Reference.Name, r.Name, StringComparison.OrdinalIgnoreCase)))
                skipped.Add(new SkippedReference(r, by.Name, why));
        }
    }

    // By GUID when the reference has one; else by name (and namespace), a project type before a built-in before the rest.
    private static TypeSystemType? Find(TypeReference r, IReadOnlyList<TypeSystemType> all)
    {
        if (r.Guid.Length > 0)
        {
            var byGuid = all.FirstOrDefault(t => string.Equals(t.Guid, r.Guid, StringComparison.OrdinalIgnoreCase));
            if (byGuid is not null) return byGuid;
        }
        return all.Where(t => string.Equals(t.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                              && (r.Namespace is null || string.Equals(t.Namespace, r.Namespace, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Persistent ? 0 : t.BuiltIn ? 1 : 2)
            .ThenBy(t => t.Namespace is null ? 0 : 1)
            .FirstOrDefault();
    }
}
