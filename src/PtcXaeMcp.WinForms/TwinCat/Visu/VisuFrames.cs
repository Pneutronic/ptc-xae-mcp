using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>One parameter of a frame: an interface variable of the referenced visualization and the expression it gets.</summary>
internal sealed record VisuFrameAssignment(string Name, string Type, string Expression);

/// <summary>A visualization a frame can reference: its name and the VAR_IN_OUT variables of its interface.</summary>
internal sealed record VisuReferenceTarget(string Name, IReadOnlyList<VisuInterfaceVariable> Parameters);

internal sealed record VisuInterfaceVariable(string Name, string Type);

/// <summary>
/// Writes the reference of a Frame element. The integrated visualization builds a frame from its reference list
/// (m_References): a frame written without it stayed empty. The list is the type-node tree XAE 4026.22 keeps and
/// writes back for a frame with one reference; the reference itself (m_References.pReferences.[0]) is also stored as
/// member 2473092364 with the parameter values, and each parameter as a member whose id is the CRC-32 of
/// "m_References.pReferences.[0].&lt;name&gt;" (checked against the ids in XAE-written files). Type names are mapped to
/// the aliases of the file's TypeList.
/// </summary>
internal static class VisuFrames
{
    public static readonly IReadOnlyList<KeyValuePair<string, string>> FrameTypes =
    [
        new("ArrayList", "System.Collections.ArrayList"),
        new("BasicTypeNode", "{f7e1e748-ea0f-4fcb-b563-94837ee17e8d}"),
        new("CaseInsensitiveHashtable", "{02a85e84-ef2d-46fc-93f2-acb0bbff3eda}"),
        new("DynamicArrayNode", "{6c16f79c-dd98-4c29-b3ba-7042e3055542}"),
        new("Int16", "System.Int16"),
        new("String", "System.String"),
        new("StructuredTypeNode", "{503c5b2e-e80e-4ee7-ae00-c5b93a62b1aa}"),
        new("TypeClass", "{16f7aa24-038f-444e-9d81-b001bc091d35}"),
        new("TypeNodeAttributes2", "{c1464dbe-c10d-4717-be8f-63efe8638434}"),
        new("TypeNodeType", "{b12a9636-e818-4598-ae0d-fb6a2446102c}"),
        new("VisualFrameInfo", "{7fd6515d-f891-4717-b53f-b14197c6706c}"),
        new("VisualizationNode", "{f8db32ff-bdd5-49e9-9014-6d9a6dea5d8c}"),
    ];

    private static readonly Lazy<string> TemplateXml = new(() =>
    {
        using var stream = typeof(VisuFrames).Assembly.GetManifestResourceStream("PtcXaeMcp.WinForms.Visu.FrameReferences.xml")
            ?? throw new InvalidOperationException("The frame reference template is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The member id of a frame parameter: CRC-32 of "m_References.pReferences.[0].&lt;name&gt;".</summary>
    public static uint ParameterId(string name) => Crc32.Of("m_References.pReferences.[0]." + name);

    /// <summary>
    /// Makes <paramref name="element"/> show <paramref name="visualization"/> with <paramref name="parameters"/>: replaces
    /// its reference list, its first reference and the parameter members, and names the visualization in the frame
    /// information.
    /// </summary>
    public static void SetReference(VisuDocument doc, XElement element, string visualization, IReadOnlyList<VisuFrameAssignment> parameters)
    {
        var list = VisuElements.MemberList(element) ?? throw new VisuFormatException("The element has no member list.");
        var alias = FrameTypes.ToDictionary(t => t.Key, t => doc.TypeAlias(t.Key, t.Value), StringComparer.Ordinal);

        // Old reference: its parameter members go with it.
        foreach (var old in VisuElements.Members(element).Where(kv => kv.Key == VisuCatalog.FrameFirstReference).Select(kv => kv.Value).ToList())
            foreach (var id in ParameterIds(old))
                VisuElements.RemoveMemberById(element, id);
        VisuElements.RemoveMemberById(element, VisuCatalog.FrameReferences);
        VisuElements.RemoveMemberById(element, VisuCatalog.FrameFirstReference);

        var references = XElement.Parse(TemplateXml.Value);
        var reference = references.Descendants("l")
            .First(l => (string?)l.Attribute("n") == "TypeNodeChildren" && (string?)l.Attribute("cet") == "VisualizationNode")
            .Elements("o").Single();
        FillReference(reference, visualization, parameters, withValues: false);

        var first = new XElement(reference);
        first.SetAttributeValue("n", "Value");
        first.SetAttributeValue("t", "VisualizationNode");
        FillReference(first, visualization, parameters, withValues: true);

        foreach (var node in new[] { references, first })
            foreach (var e in node.DescendantsAndSelf())
                foreach (string attr in new[] { "t", "cet", "et", "ckt", "cvt" })
                    if ((string?)e.Attribute(attr) is { } name && alias.TryGetValue(name, out string? mapped))
                        e.SetAttributeValue(attr, mapped);

        doc.AppendChild(list, Member(VisuCatalog.FrameReferences, references));
        doc.AppendChild(list, Member(VisuCatalog.FrameFirstReference, first));
        foreach (var p in parameters)
            doc.AppendChild(list, Member(ParameterId(p.Name), new XElement("v", new XAttribute("n", "Value"), VisuDocument.Quote(p.Expression))));

        var info = new XElement("o", new XAttribute("n", "VisualElementFrameInformation"), new XAttribute("t", alias["VisualFrameInfo"]),
            new XElement("l", new XAttribute("n", "ContainedGuids"), new XAttribute("t", alias["ArrayList"])),
            new XElement("l", new XAttribute("n", "ContainedVisualizations"), new XAttribute("t", alias["ArrayList"])),
            new XElement("a", new XAttribute("n", "ContainedVisualizations33"), new XAttribute("cet", alias["String"]),
                new XElement("v", visualization)));
        var oldInfo = VisuDocument.Field(element, "VisualElementFrameInformation") ?? throw new VisuFormatException("The element has no frame information.");
        doc.IndentLike(info, oldInfo);
        oldInfo.ReplaceWith(info);
    }

    /// <summary>Sets the expression of existing parameters (names checked by the caller) in the member and in the first reference.</summary>
    public static void SetParameterValues(VisuDocument doc, XElement element, IEnumerable<KeyValuePair<string, string>> values)
    {
        var members = VisuElements.Members(element);
        members.TryGetValue(VisuCatalog.FrameFirstReference, out var first);
        foreach (var (name, expression) in values)
        {
            // The member id is the one the frame's own parameter node carries (XAE computes it the same way).
            var node = first is null ? null : ParameterNodes(first).FirstOrDefault(p =>
                string.Equals(VisuDocument.StringValue(VisuDocument.Field(p, "TypeNodeName")), name, StringComparison.OrdinalIgnoreCase));
            long? stored = node is null ? null : VisuDocument.NumberValue(VisuDocument.Field(node, "TypeNodeIdLong"));
            uint id = stored is >= 0 and <= uint.MaxValue ? (uint)stored.Value : ParameterId(name);
            VisuElements.SetMemberValue(doc, element, id, new XElement("v", new XAttribute("n", "Value"), VisuDocument.Quote(expression)));
            if (node is not null) SetBasicValue(node, VisuDocument.Quote(expression));
        }
    }

    private static void FillReference(XElement reference, string visualization, IReadOnlyList<VisuFrameAssignment> parameters, bool withValues)
    {
        (VisuDocument.Field(reference, "VisNodeRefs33") ?? throw new VisuFormatException("The frame template has no reference name."))
            .ReplaceWith(new XElement("v", new XAttribute("n", "VisNodeRefs33"), VisuDocument.Quote(visualization)));
        var children = VisuDocument.Field(reference, "TypeNodeChildren") ?? throw new VisuFormatException("The frame template has no parameter list.");
        var template = children.Elements("o").First();
        children.RemoveNodes();
        foreach (var p in parameters)
        {
            var node = new XElement(template);
            var type = VisuDocument.Field(node, "TypeNodeType")!;
            VisuDocument.Field(type, "QualifiedName")!.Value = VisuDocument.Quote(p.Type);
            VisuDocument.Field(type, "Name")!.Value = VisuDocument.Quote(p.Type);
            VisuDocument.Field(node, "TypeNodeName")!.Value = VisuDocument.Quote(p.Name);
            VisuDocument.Field(node, "TypeNodeIdLong")!.Value = ParameterId(p.Name).ToString(CultureInfo.InvariantCulture) + "L";
            if (withValues) SetBasicValue(node, VisuDocument.Quote(p.Expression));
            children.Add(node);
        }
    }

    private static void SetBasicValue(XElement node, string value)
    {
        var old = VisuDocument.Field(node, "BasicTypeNodeValue") ?? throw new VisuFormatException("A frame parameter node has no value.");
        old.ReplaceWith(new XElement("v", new XAttribute("n", "BasicTypeNodeValue"), value));
    }

    private static IEnumerable<XElement> ParameterNodes(XElement reference) =>
        VisuDocument.Field(reference, "TypeNodeChildren")?.Elements("o") ?? [];

    private static IEnumerable<uint> ParameterIds(XElement reference) =>
        ParameterNodes(reference).Select(p => VisuDocument.NumberValue(VisuDocument.Field(p, "TypeNodeIdLong")))
            .Where(n => n is >= 0 and <= uint.MaxValue).Select(n => (uint)n!.Value);

    private static XElement Member(uint id, XElement value)
    {
        value.SetAttributeValue("n", "Value");
        return new XElement("o", new XElement("v", new XAttribute("n", "Id"), id.ToString(CultureInfo.InvariantCulture) + "L"), value);
    }
}

/// <summary>Reads the VAR_IN_OUT variables of a visualization's interface (the frame parameters). Pure.</summary>
internal static class VisuInterfaceParser
{
    /// <summary>The VAR_IN_OUT variables, or the reason the interface cannot be used for a frame.</summary>
    public static (IReadOnlyList<VisuInterfaceVariable> Variables, string? Problem) Parse(string? text)
    {
        var result = new List<VisuInterfaceVariable>();
        if (string.IsNullOrWhiteSpace(text)) return (result, null);
        string code = StripComments(text);
        string? section = null;
        foreach (var raw in code.Split(';', '\n'))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;
            // A section keyword may share a line with the first declaration.
            while (true)
            {
                var m = System.Text.RegularExpressions.Regex.Match(part, @"^(VAR_IN_OUT(\s+CONSTANT)?|VAR_INPUT|VAR_OUTPUT|VAR_GLOBAL|VAR_STAT|VAR_TEMP|VAR|END_VAR)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) break;
                section = m.Value.ToUpperInvariant().Replace("  ", " ", StringComparison.Ordinal);
                part = part[m.Length..].Trim();
            }
            if (part.Length == 0) continue;
            int colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            if (section != "VAR_IN_OUT")
                return (result, $"its interface declares '{part}' in {section ?? "no section"}; frames written by the tools pass VAR_IN_OUT parameters only");
            string type = part[(colon + 1)..].Trim();
            int assign = type.IndexOf(":=", StringComparison.Ordinal);
            if (assign >= 0) type = type[..assign].Trim();
            foreach (var name in part[..colon].Split(','))
            {
                string n = name.Trim();
                if (n.Length > 0) result.Add(new VisuInterfaceVariable(n, type));
            }
        }
        return (result, null);
    }

    private static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*)", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 1;
                continue;
            }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) break;
                i = end - 1;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}

/// <summary>CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320) of the UTF-8 bytes of a member path.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Of(string text)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in Encoding.UTF8.GetBytes(text)) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
