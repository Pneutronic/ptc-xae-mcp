using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Pure, COM-free work on the project structure of a PLCopen export (twincat_plc_move). An export of one
/// top-level object carries, at its end, <c>&lt;ProjectStructure&gt;&lt;Object Name="FB_X" ObjectId="…"&gt;&lt;Folder
/// Name="Members"&gt;&lt;Object Name="Reset" ObjectId="…" /&gt;…</c>: the object WITHOUT its parent folders, its members
/// with the folders they sit in inside the POU. The import places the object by that structure (live 2026-11-05,
/// TC 3.1.4026.22): wrapped into <c>&lt;Folder Name="POUs"&gt;&lt;Folder Name="Target"&gt;</c> it lands in POUs/Target, and a
/// member moved into another <c>&lt;Folder&gt;</c> of the object lands in that folder — with the ObjectIds unchanged.
/// <para>Only the ProjectStructure element is rewritten; every byte before and after it stays as exported.</para>
/// </summary>
internal static class PlcOpenStructure
{
    /// <summary>A member to place into <paramref name="Folders"/> (a chain inside its POU; empty = directly under the POU).</summary>
    internal sealed record MemberMove(string Name, IReadOnlyList<string> Folders);

    private const string OpenTag = "<ProjectStructure";
    private const string CloseTag = "</ProjectStructure>";

    /// <summary>
    /// Places the export's top-level object <paramref name="objectName"/> into the folder chain <paramref name="folders"/>
    /// (from the PLC project root; empty = the project root) and, with <paramref name="member"/>, moves that member of the
    /// object into its folder chain (missing folders of that chain are added). Returns the new XML or the reason why not
    /// (without the "[Error] " prefix).
    /// </summary>
    public static (string? Xml, string? Error) Rewrite(string xml, string objectName, IReadOnlyList<string> folders,
        MemberMove? member = null)
    {
        var (start, end, error) = Locate(xml);
        if (error is not null) return (null, error);
        string fragment = xml[start..end];

        XElement structure;
        try { structure = XElement.Parse(fragment, LoadOptions.PreserveWhitespace); }
        catch (XmlException ex) { return (null, $"the project structure of the export is not readable XML: {ex.Message}"); }
        XNamespace ns = structure.Name.Namespace;

        var tops = TopLevelObjects(structure);
        if (tops.Count != 1)
            return (null, $"the project structure of the export holds {tops.Count} top-level objects, expected exactly one ('{objectName}')");
        XElement obj = tops[0];
        if (!NameIs(obj, objectName))
            return (null, $"the project structure of the export holds '{NameOf(obj)}', not '{objectName}'");

        if (member is not null)
        {
            var hits = obj.Descendants().Where(e => e.Name.LocalName == "Object" && NameIs(e, member.Name)).ToList();
            if (hits.Count == 0)
                return (null, $"the member '{member.Name}' is not in the project structure of the export of '{objectName}'");
            if (hits.Count > 1)
                return (null, $"the member name '{member.Name}' occurs {hits.Count} times in the project structure of the export of '{objectName}'");

            XElement m = hits[0];
            RemoveWithLeadingWhitespace(m);
            XElement container = obj;
            foreach (string folder in member.Folders)
            {
                XElement? next = container.Elements().FirstOrDefault(e => e.Name.LocalName == "Folder" && NameIs(e, folder));
                if (next is null)
                {
                    next = new XElement(ns + "Folder", new XAttribute("Name", folder));
                    container.Add(next);
                }
                container = next;
            }
            container.Add(m);
        }

        // The object (with any folder wrappers the export may already have put around it) is replaced by the object in
        // the requested chain.
        XElement anchor = obj.Ancestors().TakeWhile(a => a != structure).LastOrDefault() ?? obj;
        var placeholder = new XElement(ns + "placeholder");
        anchor.ReplaceWith(placeholder);
        if (anchor != obj) obj.Remove();
        XElement content = obj;
        for (int i = folders.Count - 1; i >= 0; i--)
            content = new XElement(ns + "Folder", new XAttribute("Name", folders[i]), content);
        placeholder.ReplaceWith(content);

        string text = structure.ToString(SaveOptions.DisableFormatting);
        // The parser turned CRLF in the whitespace into LF; give the file its own line ends back.
        if (fragment.Contains("\r\n", StringComparison.Ordinal))
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        return (xml[..start] + text + xml[end..], null);
    }

    /// <summary>
    /// The ObjectId of every object in the project structure, keyed by its chain of object names (folders left out, so the
    /// key does not change when an object moves between folders): "FB_X", "FB_X/Reset".
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Ids, string? Error) ObjectIds(string xml)
    {
        var (start, end, error) = Locate(xml);
        if (error is not null) return (null, error);

        XElement structure;
        try { structure = XElement.Parse(xml[start..end]); }
        catch (XmlException ex) { return (null, $"the project structure of the export is not readable XML: {ex.Message}"); }

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement o in structure.Descendants().Where(e => e.Name.LocalName == "Object"))
        {
            string key = string.Join("/", o.AncestorsAndSelf().TakeWhile(a => a != structure)
                .Where(a => a.Name.LocalName == "Object").Reverse().Select(NameOf));
            string id = (string?)o.Attribute("ObjectId") ?? string.Empty;
            if (!ids.TryAdd(key, id))
                return (null, $"the object '{key}' occurs twice in the project structure of the export");
        }
        return ids.Count == 0 ? (null, "the project structure of the export holds no object") : (ids, null);
    }

    /// <summary>Null when both exports carry the same objects with the same ObjectIds, otherwise what differs.</summary>
    public static string? CompareIds(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        var problems = new List<string>();
        foreach (var (key, id) in before)
        {
            if (!after.TryGetValue(key, out string? now)) problems.Add($"'{key}' is missing");
            else if (!string.Equals(id, now, StringComparison.OrdinalIgnoreCase)) problems.Add($"'{key}' has ObjectId {now}, was {id}");
        }
        problems.AddRange(after.Keys.Where(k => !before.ContainsKey(k)).Select(k => $"'{k}' is new"));
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static (int Start, int End, string? Error) Locate(string xml)
    {
        int start = IndexOfTag(xml, 0);
        if (start < 0) return (0, 0, "the export has no project structure (<ProjectStructure>)");
        if (IndexOfTag(xml, start + OpenTag.Length) >= 0)
            return (0, 0, "the export has more than one project structure (<ProjectStructure>)");
        int close = xml.IndexOf(CloseTag, start, StringComparison.Ordinal);
        if (close < 0) return (0, 0, "the project structure of the export is empty");
        return (start, close + CloseTag.Length, null);
    }

    // "<ProjectStructure" followed by '>', '/' or whitespace — not a longer element name.
    private static int IndexOfTag(string xml, int from)
    {
        for (int i = xml.IndexOf(OpenTag, from, StringComparison.Ordinal); i >= 0;
             i = xml.IndexOf(OpenTag, i + OpenTag.Length, StringComparison.Ordinal))
        {
            int next = i + OpenTag.Length;
            if (next < xml.Length && (xml[next] is '>' or '/' || char.IsWhiteSpace(xml[next]))) return i;
        }
        return -1;
    }

    // Objects not nested in another object: the exported top-level element(s).
    private static List<XElement> TopLevelObjects(XElement structure) =>
        structure.Descendants()
            .Where(e => e.Name.LocalName == "Object"
                        && e.Ancestors().TakeWhile(a => a != structure).All(a => a.Name.LocalName == "Folder"))
            .ToList();

    private static string NameOf(XElement e) => (string?)e.Attribute("Name") ?? string.Empty;

    private static bool NameIs(XElement e, string name) =>
        string.Equals(NameOf(e), name, StringComparison.OrdinalIgnoreCase);

    private static void RemoveWithLeadingWhitespace(XElement e)
    {
        if (e.PreviousNode is XText t && string.IsNullOrWhiteSpace(t.Value)) t.Remove();
        e.Remove();
    }
}
