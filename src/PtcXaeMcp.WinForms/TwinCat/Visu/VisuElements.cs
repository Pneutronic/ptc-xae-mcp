using System.Globalization;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>A parameter a Frame passes to a referenced visualization: its name there and the expression assigned.</summary>
internal sealed record VisuFrameParameter(string Visualization, string Name, string? Value, uint MemberId);

/// <summary>What a Frame references.</summary>
internal sealed record VisuFrameView(IReadOnlyList<string> Visualizations, IReadOnlyList<VisuFrameParameter> Parameters);

/// <summary>One element as read from the file.</summary>
/// <param name="Id">VisualElementId; null when the file has none.</param>
/// <param name="Identifier">VisualElementIdentifier, e.g. "GenElemInst_3".</param>
/// <param name="Def">The catalog type; null for an element the tools do not know (reported unsupported, never changed).</param>
/// <param name="Properties">The catalog properties the element has in the file (absent = XAE's default), in catalog order.</param>
/// <param name="OtherMembers">Members the catalog does not know (kept as they are).</param>
/// <param name="InputActions">Configured input actions as "event: action type", e.g. "OnMouseClick: ToggleVarInputAction".</param>
internal sealed record VisuElementView(
    int? Id,
    string Identifier,
    string ElementName,
    string TypeName,
    VisuElementTypeDef? Def,
    IReadOnlyList<KeyValuePair<string, object>> Properties,
    int OtherMembers,
    IReadOnlyList<string> InputActions,
    VisuFrameView? Frame)
{
    public bool Supported => Def is not null;
}

/// <summary>
/// Reads and writes single visualization elements inside a <see cref="VisuDocument"/>. Pure and COM-free; only the
/// members a call names are changed.
/// </summary>
internal static class VisuElements
{
    // ---- reading ------------------------------------------------------------------

    public static VisuElementView Read(XElement element)
    {
        string elementName = VisuDocument.StringValue(VisuDocument.Field(element, "VisualElementName")) ?? string.Empty;
        string typeName = VisuDocument.StringValue(VisuDocument.Field(element, "VisualElementTypeName")) ?? string.Empty;
        string identifier = VisuDocument.StringValue(VisuDocument.Field(element, "VisualElementIdentifier")) ?? string.Empty;
        long? id = VisuDocument.NumberValue(VisuDocument.Field(element, "VisualElementId"));

        var members = Members(element);
        string? shape = members.TryGetValue(VisuCatalog.ShapeMember, out var shapeNode) ? VisuDocument.StringValue(shapeNode) : null;
        var def = VisuCatalog.Identify(typeName, shape);

        var props = new List<KeyValuePair<string, object>>();
        var known = new HashSet<uint> { VisuCatalog.CenterX, VisuCatalog.CenterY, VisuCatalog.ShapeMember, VisuCatalog.TextId };
        if (def is not null)
        {
            if (def.Type == "Line")
            {
                known.UnionWith([VisuCatalog.LineStartX, VisuCatalog.LineStartY, VisuCatalog.LineEndX, VisuCatalog.LineEndY, VisuCatalog.LineAngle]);
            }
            foreach (var p in VisuCatalog.Properties)
            {
                if (!def.Members.TryGetValue(p.Name, out uint memberId)) continue;
                if (VisuCatalog.IsInput(memberId))
                {
                    if (InputVariable(element, memberId) is { } variable) props.Add(new(p.Name, variable));
                    continue;
                }
                known.Add(memberId);
                if (!members.TryGetValue(memberId, out var valueNode)) continue;
                object? value = ReadValue(p.Kind, valueNode);
                if (value is not null) props.Add(new(p.Name, value));
            }
        }

        var frame = def?.Type == "Frame" ? ReadFrame(element, members) : null;
        if (frame is not null)
        {
            known.UnionWith(frame.Parameters.Select(p => p.MemberId));
            known.Add(VisuCatalog.FrameFirstReference);
        }
        int other = def is null ? members.Count : members.Keys.Count(k => !known.Contains(k) && !IsTypeNodeMember(members[k]));

        return new VisuElementView(id is null ? null : (int)id, identifier, elementName, typeName, def, props, other, InputActions(element), frame);
    }

    /// <summary>The element's members by id (the Value node of each).</summary>
    public static Dictionary<uint, XElement> Members(XElement element)
    {
        var result = new Dictionary<uint, XElement>();
        var list = MemberList(element);
        if (list is null) return result;
        foreach (var o in list.Elements("o"))
        {
            long? id = VisuDocument.NumberValue(VisuDocument.Field(o, "Id"));
            var value = VisuDocument.Field(o, "Value");
            if (id is >= 0 and <= uint.MaxValue && value is not null) result[(uint)id] = value;
        }
        return result;
    }

    /// <summary>The <c>l n="VisualElemMemberList"</c> list of an element; null when the element has another structure.</summary>
    public static XElement? MemberList(XElement element)
    {
        var holder = VisuDocument.Field(element, "VisualElemMemberList");
        return holder is null ? null : VisuDocument.Field(holder, "VisualElemMemberList");
    }

    private static bool IsTypeNodeMember(XElement value) =>
        // StructuredTypeNode, VisualizationNode …: the type-node trees XAE keeps for frames, polygons and lines.
        value.Name.LocalName == "o" && ((string?)value.Attribute("t"))?.EndsWith("Node", StringComparison.Ordinal) == true;

    public static object? ReadValue(VisuValueKind kind, XElement value) => kind switch
    {
        VisuValueKind.Integer => VisuDocument.NumberValue(value) is long n ? n : null,
        VisuValueKind.Text or VisuValueKind.Expression => VisuDocument.StringValue(value),
        VisuValueKind.Color => ReadColor(value) is uint c ? FormatColor(c) : null,
        _ => null,
    };

    /// <summary>A colour in any of the forms XAE writes: plain UInt32, a list of UInt32, a (list of) NamedStyleColor.</summary>
    public static uint? ReadColor(XElement value)
    {
        switch (value.Name.LocalName)
        {
            case "v":
                return VisuDocument.NumberValue(value) is long n ? unchecked((uint)n) : null;
            case "l":
                var first = value.Elements().FirstOrDefault();
                return first is null ? null : ReadColor(first);
            case "o":
                var color = VisuDocument.Field(value, "Color");
                return VisuDocument.NumberValue(color) is long c ? unchecked((uint)c) : null;
            default:
                return null;
        }
    }

    public static string FormatColor(uint argb) => "#" + argb.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>"#RRGGBB" (opaque) or "#AARRGGBB" → ARGB. Null when the text is no such colour.</summary>
    public static uint? ParseColor(string text)
    {
        string t = text.Trim();
        if (!t.StartsWith('#')) return null;
        t = t[1..];
        if (t.Length is not (6 or 8) || !uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        return t.Length == 6 ? 0xFF000000u | v : v;
    }

    private static IReadOnlyList<string> InputActions(XElement element)
    {
        var actions = VisuDocument.Field(element, "VisualElementInputActions");
        if (actions is null) return [];
        var result = new List<string>();
        string? evt = null;
        foreach (var child in actions.Elements())
        {
            if (child.Name.LocalName == "v") { evt = child.Value; continue; }
            string type = (string?)child.Attribute("cet") ?? (string?)child.Attribute("t") ?? child.Name.LocalName;
            result.Add($"{evt}: {type}");
            evt = null;
        }
        return result;
    }

    private static VisuFrameView ReadFrame(XElement element, Dictionary<uint, XElement> members)
    {
        var visus = new List<string>();
        var info = VisuDocument.Field(element, "VisualElementFrameInformation");
        if (info is not null)
        {
            var names = VisuDocument.Field(info, "ContainedVisualizations33");
            if (names is not null) visus.AddRange(names.Elements("v").Select(v => v.Value).Where(v => v.Length > 0));
        }

        var parameters = new List<VisuFrameParameter>();
        foreach (var node in members.Values.Where(v => (string?)v.Attribute("t") == "VisualizationNode"))
        {
            string visu = VisuDocument.StringValue(VisuDocument.Field(node, "VisNodeRefs33")) ?? string.Empty;
            var children = VisuDocument.Field(node, "TypeNodeChildren");
            if (children is null) continue;
            foreach (var p in children.Elements("o"))
            {
                string? name = VisuDocument.StringValue(VisuDocument.Field(p, "TypeNodeName"));
                long? idLong = VisuDocument.NumberValue(VisuDocument.Field(p, "TypeNodeIdLong"));
                if (name is null || idLong is null) continue;
                string? value = members.TryGetValue((uint)idLong, out var assigned)
                    ? VisuDocument.StringValue(assigned)
                    : VisuDocument.StringValue(VisuDocument.Field(p, "BasicTypeNodeValue"));
                parameters.Add(new VisuFrameParameter(visu, name, value, (uint)idLong));
            }
        }
        return new VisuFrameView(visus, parameters);
    }

    // ---- writing ------------------------------------------------------------------

    /// <summary>A new element of <paramref name="def"/> without members (XAE fills its defaults when it loads the file).</summary>
    public static XElement CreateSkeleton(VisuElementTypeDef def, int id, string visuObjectId)
    {
        var element = new XElement("o",
            new XElement("a", new XAttribute("n", "ConfiguredComplexInputs"), new XAttribute("et", "ComplexInput")),
            new XElement("l", new XAttribute("n", "Elements"), new XAttribute("t", "ArrayList")),
            new XElement("n", new XAttribute("n", "VisualElementDescription")),
            new XElement("o", new XAttribute("n", "VisualElemMemberList"), new XAttribute("t", "VisualElemMemberList"),
                new XElement("l", new XAttribute("n", "VisualElemMemberList"), new XAttribute("t", "VisualElemMemberCollection"),
                    new XAttribute("cet", "VisualElemMember"))),
            new XElement("v", new XAttribute("n", "VisualElementName"), VisuDocument.Quote(def.ElementName)),
            new XElement("v", new XAttribute("n", "VisualElementTypeName"), VisuDocument.Quote(def.TypeNames[0])),
            new XElement("v", new XAttribute("n", "VisualElementIsRectangle"), def.IsRectangle ? "true" : "false"),
            new XElement("v", new XAttribute("n", "VisualElementIdentifier"), VisuDocument.Quote(Identifier(id))),
            new XElement("n", new XAttribute("n", "VisualElementOfflinePaintCommands")),
            new XElement("n", new XAttribute("n", "VisualElementFrameInformation")),
            new XElement("d", new XAttribute("n", "VisualElementInputActions"), new XAttribute("t", "Hashtable")),
            new XElement("v", new XAttribute("n", "VisualElementIdentification"), NewGuid()),
            new XElement("v", new XAttribute("n", "VisualElementOwningObjectGuid"), visuObjectId),
            new XElement("a", new XAttribute("n", "LMGuids"), new XAttribute("et", "Guid")),
            new XElement("d", new XAttribute("n", "SubElements"), new XAttribute("t", "Hashtable")),
            new XElement("v", new XAttribute("n", "VisualElementId"), id.ToString(CultureInfo.InvariantCulture)),
            new XElement("l", new XAttribute("n", "UserManagementAccessRights"), new XAttribute("t", "ArrayList")));
        return element;
    }

    public static string Identifier(int id) => "GenElemInst_" + id.ToString(CultureInfo.InvariantCulture);

    private static string NewGuid() => "{" + Guid.NewGuid().ToString("D") + "}";

    /// <summary>
    /// A copy of <paramref name="source"/> as element <paramref name="id"/>: new id, identifier and identification,
    /// owned by <paramref name="visuObjectId"/>; every member and setting as in the source.
    /// </summary>
    public static XElement Clone(XElement source, int id, string visuObjectId)
    {
        var copy = new XElement(source);
        // Drop the indentation (whitespace between child elements); the copy is indented again where it is inserted.
        foreach (var ws in copy.DescendantNodes().OfType<XText>()
                     .Where(t => string.IsNullOrWhiteSpace(t.Value) && t.Parent is { HasElements: true }).ToList())
            ws.Remove();
        SetField(copy, "VisualElementIdentifier", VisuDocument.Quote(Identifier(id)));
        SetField(copy, "VisualElementIdentification", NewGuid());
        SetField(copy, "VisualElementOwningObjectGuid", visuObjectId);
        SetField(copy, "VisualElementId", id.ToString(CultureInfo.InvariantCulture));
        return copy;
    }

    private static void SetField(XElement element, string field, string value)
    {
        var v = VisuDocument.Field(element, field) ?? throw new VisuFormatException($"The element has no {field}.");
        v.Value = value;
    }

    /// <summary>
    /// Sets (or, with null, removes) one property. The value is already validated for its kind. Geometry keeps the
    /// element's centre (and a line's end points) consistent; a static text gets a text-list id that XAE files in the
    /// global text list when it loads the file (a Button text without one makes XAE refuse the whole file).
    /// </summary>
    public static void Set(VisuDocument doc, XElement element, VisuElementTypeDef def, string property, object? value)
    {
        if (!def.Members.TryGetValue(property, out uint memberId))
            throw new ArgumentException($"{def.Type} has no property '{property}'.", nameof(property));
        var kind = VisuCatalog.Property(property)!.Kind;

        bool remove = value is null || (value is string s && s.Length == 0 && kind == VisuValueKind.Expression);
        if (VisuCatalog.IsInput(memberId))
        {
            SetInput(doc, element, memberId, remove ? null : (string)value!);
            return;
        }
        if (remove)
        {
            RemoveMember(element, memberId);
            if (memberId == VisuCatalog.TextMember) RemoveMember(element, VisuCatalog.TextId);
            return;
        }

        SetMember(doc, element, memberId, ValueNode(kind, value!));
        if (memberId == VisuCatalog.TextMember) SetTextIdPlaceholder(doc, element, VisuCatalog.TextId, (string)value!);
    }

    /// <summary>After x/y/width/height changed: the centre members (all types) and the end points of a line.</summary>
    public static void UpdateDerivedGeometry(VisuDocument doc, XElement element, VisuElementTypeDef def)
    {
        var members = Members(element);
        long? Get(uint id) => members.TryGetValue(id, out var v) ? VisuDocument.NumberValue(v) : null;
        if (Get(VisuCatalog.X) is not long x || Get(VisuCatalog.Y) is not long y
            || Get(VisuCatalog.Width) is not long w || Get(VisuCatalog.Height) is not long h) return;

        SetMember(doc, element, VisuCatalog.CenterX, IntegerNode(x + w / 2));
        SetMember(doc, element, VisuCatalog.CenterY, IntegerNode(y + h / 2));
        if (def.Type == "Line")
        {
            SetMember(doc, element, VisuCatalog.LineStartX, IntegerNode(x));
            SetMember(doc, element, VisuCatalog.LineStartY, IntegerNode(y));
            SetMember(doc, element, VisuCatalog.LineEndX, IntegerNode(x + w));
            SetMember(doc, element, VisuCatalog.LineEndY, IntegerNode(y + h));
        }
    }

    /// <summary>The shape member of a simple element (rectangle, rounded rectangle, ellipse).</summary>
    public static void SetShape(VisuDocument doc, XElement element, VisuElementTypeDef def)
    {
        if (def.Shape is not null) SetMember(doc, element, VisuCatalog.ShapeMember, StringNode(def.Shape));
    }

    // ---- line point list --------------------------------------------------------------

    /// <summary>The archive types the point list of a line uses (name as XAE writes it → type id), all format constants.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> LinePointTypes =
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
    ];

    private static readonly Lazy<string> LinePointsXml = new(() =>
    {
        using var stream = typeof(VisuElements).Assembly.GetManifestResourceStream("PtcXaeMcp.WinForms.Visu.LinePoints.xml")
            ?? throw new InvalidOperationException("The line point template is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Gives a line its point list (m_StaticPositionPolygon) when it has none. The integrated visualization draws a line
    /// from that type-node tree, which XAE otherwise builds only in its editor: a line with just its start and end
    /// members was not drawn on the running PLC, and the XAE crashed painting such a line offline (ArgumentNullException
    /// in the visualization's init-assignment collector). The tree is the one XAE 4026.22 writes; its point values come
    /// from the start/end members, its type names are mapped to the aliases this file's TypeList uses.
    /// </summary>
    public static void EnsureLinePoints(VisuDocument doc, XElement element)
    {
        var list = MemberList(element) ?? throw new VisuFormatException("The element has no member list.");
        if (FindMember(list, VisuCatalog.LinePoints) is not null) return;

        var alias = LinePointTypes.ToDictionary(t => t.Key, t => doc.TypeAlias(t.Key, t.Value), StringComparer.Ordinal);
        var node = XElement.Parse(LinePointsXml.Value);
        foreach (var e in node.DescendantsAndSelf())
            foreach (string attr in new[] { "t", "cet", "et", "ckt", "cvt" })
                if ((string?)e.Attribute(attr) is { } name && alias.TryGetValue(name, out string? mapped))
                    e.SetAttributeValue(attr, mapped);

        if (FindMember(list, VisuCatalog.LineAngle) is null) SetMember(doc, element, VisuCatalog.LineAngle, IntegerNode(0));
        var member = new XElement("o", new XElement("v", new XAttribute("n", "Id"), VisuCatalog.LinePoints.ToString(CultureInfo.InvariantCulture) + "L"), node);
        doc.AppendChild(list, member);
    }

    // ---- tap / toggle inputs -----------------------------------------------------------

    private static (string Signature, string Name, string Description) InputSignature(uint input) => input == VisuCatalog.TapInput
        ? ("Visu_TapInput", "Tap", "Configure the tapping of a Boolean variable")
        : ("Visu_ToggleInput", "Toggle", "Configure the toggling of a Boolean variable");

    private static XElement? InputEntry(XElement element, uint input)
    {
        var inputs = VisuDocument.Field(element, "ConfiguredComplexInputs");
        string signature = VisuDocument.Quote(InputSignature(input).Signature);
        return inputs?.Elements("o").FirstOrDefault(o => VisuDocument.Field(o, "SignatureName")?.Value == signature);
    }

    /// <summary>The variable of the element's tap or toggle input; null when it has none.</summary>
    private static string? InputVariable(XElement element, uint input)
    {
        var entry = InputEntry(element, input);
        if (entry is null) return null;
        var members = Members(entry);
        return members.TryGetValue(VisuCatalog.InputVariable, out var v) ? VisuDocument.StringValue(v) : null;
    }

    /// <summary>
    /// Sets (or, with null, removes) the tap or toggle input. An existing entry keeps its other members (e.g. "tap
    /// FALSE"); a new one is written the way XAE writes it (ComplexInput with SignatureName, Name and Description).
    /// </summary>
    private static void SetInput(VisuDocument doc, XElement element, uint input, string? variable)
    {
        var inputs = VisuDocument.Field(element, "ConfiguredComplexInputs") ?? throw new VisuFormatException("The element has no input list.");
        var entry = InputEntry(element, input);
        if (variable is null)
        {
            if (entry is null) return;
            VisuDocument.RemoveNode(entry);
            if (!inputs.Elements().Any())
            {
                inputs.RemoveNodes();
                inputs.SetAttributeValue("cet", null);
                inputs.SetAttributeValue("et", "ComplexInput");
            }
            return;
        }
        if (entry is not null)
        {
            SetMember(doc, entry, VisuCatalog.InputVariable, StringNode(variable));
            return;
        }
        var (signature, name, description) = InputSignature(input);
        entry = new XElement("o",
            new XElement("n", new XAttribute("n", "DescriptionTree")),
            new XElement("o", new XAttribute("n", "VisualElemMemberList"), new XAttribute("t", "VisualElemMemberList"),
                new XElement("l", new XAttribute("n", "VisualElemMemberList"), new XAttribute("t", "VisualElemMemberCollection"),
                    new XAttribute("cet", "VisualElemMember"),
                    new XElement("o",
                        new XElement("v", new XAttribute("n", "Id"), VisuCatalog.InputVariable.ToString(CultureInfo.InvariantCulture) + "L"),
                        StringNode(variable)))),
            new XElement("v", new XAttribute("n", "SignatureName"), VisuDocument.Quote(signature)),
            new XElement("v", new XAttribute("n", "Name"), VisuDocument.Quote(name)),
            new XElement("v", new XAttribute("n", "Description"), VisuDocument.Quote(description)));
        if (inputs.Attribute("et") is not null)
        {
            inputs.SetAttributeValue("et", null);
            inputs.SetAttributeValue("cet", "ComplexInput");
        }
        doc.AppendChild(inputs, entry);
    }

    // ---- member plumbing ------------------------------------------------------------

    /// <summary>Sets a member by id (for the frame writer).</summary>
    public static void SetMemberValue(VisuDocument doc, XElement element, uint memberId, XElement valueNode) =>
        SetMember(doc, element, memberId, valueNode);

    /// <summary>Removes a member by id (for the frame writer).</summary>
    public static void RemoveMemberById(XElement element, uint memberId) => RemoveMember(element, memberId);

    private static XElement ValueNode(VisuValueKind kind, object value) => kind switch
    {
        VisuValueKind.Integer => IntegerNode(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        VisuValueKind.Text or VisuValueKind.Expression => StringNode((string)value),
        VisuValueKind.Color => new XElement("v", new XAttribute("n", "Value"), ((uint)value).ToString(CultureInfo.InvariantCulture) + "U"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static XElement IntegerNode(long n) => new("v", new XAttribute("n", "Value"), n.ToString(CultureInfo.InvariantCulture));

    private static XElement StringNode(string s) => new("v", new XAttribute("n", "Value"), VisuDocument.Quote(s));

    // The text-list id has to be present (a Button text without one makes XAE refuse the file). A changed text gets "0"
    // here, never the old text's id; the write path then gives every text its real id (VisuTextIds.Assign), because XAE
    // assigns ids of its own only in memory and saves the file with the ids it was given.
    private static void SetTextIdPlaceholder(VisuDocument doc, XElement element, uint idMember, string text)
    {
        if (text.Length == 0) { RemoveMember(element, idMember); return; }
        SetMember(doc, element, idMember, StringNode("0"));
    }

    private static void SetMember(VisuDocument doc, XElement element, uint memberId, XElement valueNode)
    {
        var list = MemberList(element) ?? throw new VisuFormatException("The element has no member list.");
        var existing = FindMember(list, memberId);
        if (existing is not null)
        {
            var old = VisuDocument.Field(existing, "Value");
            if (old is null)
            {
                doc.AppendChild(existing, valueNode);
            }
            else
            {
                // Unchanged value: leave the node (and its exact text) alone.
                if (!old.HasElements && old.ToString(SaveOptions.DisableFormatting) == valueNode.ToString(SaveOptions.DisableFormatting))
                    return;
                doc.IndentLike(valueNode, old);
                old.ReplaceWith(valueNode);
            }
            return;
        }
        var member = new XElement("o",
            new XElement("v", new XAttribute("n", "Id"), memberId.ToString(CultureInfo.InvariantCulture) + "L"),
            valueNode);
        doc.AppendChild(list, member);
    }

    private static void RemoveMember(XElement element, uint memberId)
    {
        var list = MemberList(element);
        var existing = list is null ? null : FindMember(list, memberId);
        if (existing is not null) VisuDocument.RemoveNode(existing);
    }

    private static XElement? FindMember(XElement list, uint memberId) =>
        list.Elements("o").FirstOrDefault(o => VisuDocument.NumberValue(VisuDocument.Field(o, "Id")) == memberId);
}
