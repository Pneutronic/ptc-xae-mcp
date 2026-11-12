using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>A .TcVIS file that is not a PLC visualization this tool can read or write.</summary>
internal sealed class VisuFormatException(string message) : Exception(message);

/// <summary>
/// One PLC visualization file (.TcVIS) as XAE stores it: <c>&lt;TcPlcObject&gt;&lt;Visu Name Id&gt;&lt;XmlArchive&gt;
/// &lt;Data&gt;</c> holding a generic object archive (<c>o</c> object, <c>v</c> value, <c>n</c> null, <c>l</c>/<c>a</c>
/// list/array, <c>d</c> dictionary; the <c>n</c> attribute names a field) and a <c>&lt;TypeList&gt;</c> that maps the
/// short type names used in <c>t</c>/<c>cet</c>/<c>et</c> attributes to type GUIDs.
/// <para>
/// Pure and COM-free. The file is kept as an XML tree with its whitespace, so a page that is read and written back
/// unchanged gives the same text; edits touch only the nodes they address and everything else (unknown elements,
/// unknown members, XAE's generated tables) stays as it was.
/// </para>
/// </summary>
internal sealed class VisuDocument
{
    private const string Bom = "﻿";

    private readonly XDocument _doc;
    private readonly string _prolog;
    private readonly string _epilog;
    private readonly string _newLine;

    private VisuDocument(XDocument doc, string prolog, string epilog, string newLine)
    {
        _doc = doc;
        _prolog = prolog;
        _epilog = epilog;
        _newLine = newLine;
        Root = doc.Root!;
        Visu = Root.Element("Visu") ?? throw new VisuFormatException("The file has no <Visu> element: it is not a PLC visualization.");
        var archive = Visu.Element("XmlArchive") ?? throw new VisuFormatException("The <Visu> element has no <XmlArchive>.");
        Data = archive.Element("Data") ?? throw new VisuFormatException("The archive has no <Data> element.");
        TypeList = archive.Element("TypeList") ?? throw new VisuFormatException("The archive has no <TypeList> element.");
        VisualObject = Data.Elements("o").FirstOrDefault(o => (string?)o.Attribute("t") == "VisualObject")
            ?? throw new VisuFormatException("The archive holds no VisualObject.");
        var elemList = Field(VisualObject, "VisualElemList")
            ?? throw new VisuFormatException("The VisualObject has no VisualElemList.");
        ElementList = Field(elemList, "VisualElementList")
            ?? throw new VisuFormatException("The VisualElemList has no VisualElementList.");
    }

    public XElement Root { get; }
    public XElement Visu { get; }
    public XElement Data { get; }
    public XElement TypeList { get; }
    public XElement VisualObject { get; }
    public XElement ElementList { get; }

    public string Name => (string?)Visu.Attribute("Name") ?? string.Empty;
    public string ObjectId => (string?)Visu.Attribute("Id") ?? string.Empty;
    /// <summary>Version attribute of &lt;TcPlcObject&gt; (the file format, "1.1.0.1").</summary>
    public string ObjectVersion => (string?)Root.Attribute("Version") ?? string.Empty;
    /// <summary>ProductVersion attribute of &lt;TcPlcObject&gt; (the XAE build that wrote it); null when XAE did not write it.</summary>
    public string? ProductVersion => (string?)Root.Attribute("ProductVersion");

    /// <summary>The visualization elements, in file order (each an <c>o</c> of type GenericVisualElem).</summary>
    public IReadOnlyList<XElement> Elements => ElementList.Elements("o").ToList();

    // ---- parse / serialize -----------------------------------------------------

    public static VisuDocument Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new VisuFormatException("The file is empty.");
        int rootStart = text.IndexOf("<TcPlcObject", StringComparison.Ordinal);
        int rootEnd = text.LastIndexOf("</TcPlcObject>", StringComparison.Ordinal);
        if (rootStart < 0 || rootEnd < 0)
            throw new VisuFormatException("The file is not a TwinCAT PLC object (no <TcPlcObject> root).");
        rootEnd += "</TcPlcObject>".Length;

        XDocument doc;
        try { doc = XDocument.Parse(text.StartsWith(Bom, StringComparison.Ordinal) ? text[1..] : text, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { throw new VisuFormatException($"The file is not well-formed XML: {ex.Message}"); }
        if (doc.Root?.Name.LocalName != "TcPlcObject")
            throw new VisuFormatException("The file is not a TwinCAT PLC object (no <TcPlcObject> root).");

        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return new VisuDocument(doc, text[..rootStart], text[rootEnd..], newLine);
    }

    /// <summary>The file text: the original prolog (BOM, XML declaration) and line endings, the tree as it is now.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = _newLine,
            ConformanceLevel = ConformanceLevel.Fragment,
        };
        using (var writer = XmlWriter.Create(new StringWriter(sb, CultureInfo.InvariantCulture), settings))
            Root.WriteTo(writer);
        return _prolog + sb + _epilog;
    }

    // ---- archive helpers (static, shared with the element codec) ------------------

    /// <summary>The direct child with field name <paramref name="name"/> (attribute n), any node kind.</summary>
    public static XElement? Field(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(e => (string?)e.Attribute("n") == name);

    /// <summary>A string value as the archive writes it ("text" with the quotes); null for a null node or a non-string.</summary>
    public static string? StringValue(XElement? v)
    {
        if (v is null || v.Name.LocalName != "v") return null;
        string raw = v.Value;
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : null;
    }

    /// <summary>The archive form of a string value.</summary>
    public static string Quote(string value) => "\"" + value + "\"";

    /// <summary>
    /// Numeric text of a <c>v</c> node without its type suffix (L, U, UL): "4278190080U" → 4278190080. Null when the
    /// node is no plain number.
    /// </summary>
    public static long? NumberValue(XElement? v)
    {
        if (v is null || v.Name.LocalName != "v") return null;
        string raw = v.Value.Trim().TrimEnd('L', 'U', 'l', 'u');
        return long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n) ? n : null;
    }

    // ---- interface (VAR_IN_OUT …) -----------------------------------------------

    private XElement? TextDocument => Field(VisualObject, "TextDocument");

    /// <summary>
    /// The visualization interface (the declaration text, e.g. "VAR_IN_OUT … END_VAR"). XAE 4026 stores it as
    /// <c>TextBlobForSerialisation</c>, older builds as <c>TextLines</c>; both are read. Null when the file has neither.
    /// </summary>
    public string? InterfaceText
    {
        get
        {
            var td = TextDocument;
            if (td is null) return null;
            var blob = Field(td, "TextBlobForSerialisation");
            if (blob is not null) return StringValue(blob);
            var lines = Field(td, "TextLines");
            if (lines is null) return null;
            return string.Join("\n", lines.Elements("o").Select(o => StringValue(Field(o, "Text")) ?? string.Empty));
        }
    }

    /// <summary>Replaces the interface text in the form the file already uses. Throws when the file has no interface field.</summary>
    public void SetInterfaceText(string text)
    {
        var td = TextDocument ?? throw new VisuFormatException("The visualization has no TextDocument (interface) field.");
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var blob = Field(td, "TextBlobForSerialisation");
        if (blob is not null)
        {
            blob.Value = Quote(text);
            return;
        }
        var lines = Field(td, "TextLines") ?? throw new VisuFormatException("The interface field has neither TextBlobForSerialisation nor TextLines.");
        int depth = Depth(lines);
        lines.RemoveNodes();
        long id = 1;
        foreach (string line in text.Split('\n'))
        {
            var o = new XElement("o",
                new XElement("v", new XAttribute("n", "Id"), $"{id++}L"),
                new XElement("n", new XAttribute("n", "Tag")),
                new XElement("v", new XAttribute("n", "Text"), Quote(line)));
            AppendIndented(lines, o, depth);
        }
    }

    // ---- ids --------------------------------------------------------------------

    /// <summary>The counter XAE draws element ids from (<c>UniqueIdGenerator</c>, a quoted number).</summary>
    public int UniqueIdGenerator
    {
        get => int.TryParse(StringValue(Field(VisualObject, "UniqueIdGenerator")), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
        set
        {
            var v = Field(VisualObject, "UniqueIdGenerator") ?? throw new VisuFormatException("The VisualObject has no UniqueIdGenerator.");
            v.Value = Quote(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    // ---- element list -----------------------------------------------------------

    /// <summary>Appends <paramref name="element"/> (an unindented tree) at the end of the element list, indented like XAE.</summary>
    public void AddElement(XElement element)
    {
        EnsureListHoldsElements();
        AppendIndented(ElementList, element, Depth(ElementList));
    }

    /// <summary>Inserts <paramref name="element"/> right after <paramref name="after"/>.</summary>
    public void InsertElementAfter(XElement after, XElement element)
    {
        int depth = Depth(ElementList) + 1;
        Indent(element, depth);
        after.AddAfterSelf(new XText(_newLine + Spaces(depth)), element);
    }

    public void RemoveElement(XElement element)
    {
        RemoveWithLeadingWhitespace(element);
        if (!ElementList.Elements().Any())
        {
            ElementList.RemoveNodes();
            ElementList.Attribute("cet")?.Remove();
        }
    }

    public void RemoveAllElements()
    {
        ElementList.RemoveNodes();
        ElementList.Attribute("cet")?.Remove();
    }

    private void EnsureListHoldsElements()
    {
        if (ElementList.Attribute("cet") is null)
            ElementList.SetAttributeValue("cet", "GenericVisualElem");
    }

    /// <summary>
    /// Adds the TypeList entries <paramref name="types"/> (name → type id) that are missing, in name order like XAE
    /// writes them. An entry that exists with another value is left alone.
    /// </summary>
    public void EnsureTypes(IEnumerable<KeyValuePair<string, string>> types)
    {
        int depth = Depth(TypeList);
        foreach (var (name, value) in types)
        {
            if (TypeList.Elements("Type").Any(t => (string?)t.Attribute("n") == name)) continue;
            var entry = new XElement("Type", new XAttribute("n", name), value);
            var before = TypeList.Elements("Type").FirstOrDefault(t => string.CompareOrdinal((string?)t.Attribute("n"), name) > 0);
            if (before is null)
            {
                AppendIndented(TypeList, entry, depth);
            }
            else
            {
                // Same indentation as the entry it goes in front of.
                before.AddBeforeSelf(entry, new XText(_newLine + Spaces(depth + 1)));
            }
        }
    }

    /// <summary>Type names the archive uses (t, cet, et attributes) that have no TypeList entry.</summary>
    public IReadOnlyList<string> UndeclaredTypes()
    {
        var declared = TypeList.Elements("Type").Select(t => (string?)t.Attribute("n")).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Data.Descendants())
            foreach (string attr in new[] { "t", "cet", "et", "ckt", "cvt" })
                if ((string?)e.Attribute(attr) is { Length: > 0 } t) used.Add(t);
        return used.Where(t => !declared.Contains(t!)).Order(StringComparer.Ordinal).ToList();
    }

    // ---- indentation --------------------------------------------------------------

    /// <summary>Nesting depth of <paramref name="e"/> below the document root (root = 0).</summary>
    private static int Depth(XElement e) => e.Ancestors().Count();

    private static string Spaces(int depth) => new(' ', depth * 2);

    /// <summary>Appends <paramref name="child"/> as the last child of <paramref name="parent"/> (at <paramref name="parentDepth"/>), indented.</summary>
    private void AppendIndented(XElement parent, XElement child, int parentDepth)
    {
        Indent(child, parentDepth + 1);
        // An element written as <x /> or <x></x> has no whitespace yet; otherwise the last node is the
        // newline + indentation in front of the closing tag, which stays last.
        if (parent.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value))
        {
            closing.AddBeforeSelf(new XText(_newLine + Spaces(parentDepth + 1)), child);
        }
        else
        {
            parent.Add(new XText(_newLine + Spaces(parentDepth + 1)), child, new XText(_newLine + Spaces(parentDepth)));
        }
    }

    /// <summary>Indents an element tree that has no whitespace nodes yet, for a place at <paramref name="depth"/>.</summary>
    private void Indent(XElement element, int depth)
    {
        var children = element.Elements().ToList();
        if (children.Count == 0 || element.Nodes().OfType<XText>().Any()) return;
        foreach (var c in children)
        {
            c.AddBeforeSelf(new XText(_newLine + Spaces(depth + 1)));
            Indent(c, depth + 1);
        }
        element.Add(new XText(_newLine + Spaces(depth)));
    }

    private static void RemoveWithLeadingWhitespace(XElement element)
    {
        if (element.PreviousNode is XText ws && string.IsNullOrWhiteSpace(ws.Value)) ws.Remove();
        element.Remove();
    }

    /// <summary>Indents a free-standing element tree for a place at the depth of <paramref name="sibling"/>.</summary>
    public void IndentLike(XElement element, XElement sibling) => Indent(element, Depth(sibling));

    /// <summary>Appends a child to a node inside this document, indented.</summary>
    public void AppendChild(XElement parent, XElement child) => AppendIndented(parent, child, Depth(parent));

    /// <summary>Removes a node and the whitespace in front of it.</summary>
    public static void RemoveNode(XElement node) => RemoveWithLeadingWhitespace(node);

    /// <summary>The newline the file uses ("\r\n" or "\n").</summary>
    public string NewLine => _newLine;
}
