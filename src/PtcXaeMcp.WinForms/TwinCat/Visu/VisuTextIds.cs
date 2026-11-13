using System.Globalization;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Visu;

/// <summary>
/// Text-list ids of the static texts of a visualization (member <see cref="VisuCatalog.TextId"/>). At runtime an
/// element shows the text the global text list has under its id, so every distinct text needs an id of its own in the
/// file. XAE gives a text an id when it loads the visualization, but only in memory: a visualization that was taken
/// over from a file and not edited in XAE keeps the ids of the file when the project is saved. With "0" on every
/// element, all elements showed the text of id 0 after the project was opened again (seen live on 4026.22). The tools
/// therefore write the ids themselves, the way XAE files them: a text the global text list already has gets that
/// text's id, a text that is new gets a free id, and the same text on several elements shares one id.
/// </summary>
internal static class VisuTextIds
{
    /// <summary>The ids in use outside the visualization being written.</summary>
    internal sealed class Known
    {
        /// <summary>Global text list: id → text.</summary>
        public Dictionary<string, string> TextById { get; } = new(StringComparer.Ordinal);
        /// <summary>Ids used by other visualizations of the project (their text may not be in the text list yet).</summary>
        public HashSet<string> OtherIds { get; } = new(StringComparer.Ordinal);
        /// <summary>Files read, for the report.</summary>
        public List<string> Sources { get; } = [];

        public static Known Empty => new();
    }

    /// <summary>
    /// Reads the global text list(s) and the other visualizations of the PLC project the file <paramref name="visuFile"/>
    /// belongs to (the folder holding the .plcproj above it). Unreadable files are skipped.
    /// </summary>
    public static Known Load(string visuFile)
    {
        var known = new Known();
        string? root = ProjectFolder(visuFile);
        if (root is null) return known;
        string self = Path.GetFullPath(visuFile);
        foreach (string file in ProjectFiles(root))
        {
            string ext = Path.GetExtension(file);
            try
            {
                if (ext.Equals(".TcGTLO", StringComparison.OrdinalIgnoreCase))
                {
                    ReadTextList(XDocument.Parse(File.ReadAllText(file)), known.TextById);
                    known.Sources.Add(file);
                }
                else if (ext.Equals(".TcVIS", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var (_, id) in Texts(VisuDocument.Parse(File.ReadAllText(file))))
                        if (id is not null) known.OtherIds.Add(id);
                    known.Sources.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or VisuFormatException)
            {
                // A file that cannot be read adds nothing; ids are still unique within the page.
            }
        }
        return known;
    }

    /// <summary>The rows of a global text list: TextID → TextDefault.</summary>
    internal static void ReadTextList(XDocument textList, Dictionary<string, string> into)
    {
        foreach (var row in textList.Descendants("o"))
        {
            string? id = VisuDocument.StringValue(VisuDocument.Field(row, "TextID"));
            string? text = VisuDocument.StringValue(VisuDocument.Field(row, "TextDefault"));
            if (id is not null && text is not null && !into.ContainsKey(id)) into[id] = text;
        }
    }

    /// <summary>
    /// Gives every element with a static text the text-list id it needs (see the class summary). Returns one line per
    /// id that changed. Elements without a text and members the catalog does not know are not touched.
    /// </summary>
    public static IReadOnlyList<string> Assign(VisuDocument doc, Known known)
    {
        var lines = new List<string>();
        var textOfId = new Dictionary<string, string>(StringComparer.Ordinal);   // ids given out on this page
        var idOfText = new Dictionary<string, string>(StringComparer.Ordinal);
        var listIdOfText = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, text) in known.TextById.OrderBy(p => Number(p.Key) ?? long.MaxValue))
            listIdOfText.TryAdd(text, id);

        long next = new[] { known.TextById.Keys, known.OtherIds, Texts(doc).Select(t => t.Id ?? "") }
            .SelectMany(ids => ids).Select(Number).Where(n => n is not null).Select(n => n!.Value)
            .DefaultIfEmpty(-1).Max() + 1;

        bool FreeFor(string id, string text) =>
            !(textOfId.TryGetValue(id, out var t) && t != text);

        foreach (var element in doc.Elements)
        {
            var members = VisuElements.Members(element);
            if (!members.TryGetValue(VisuCatalog.TextMember, out var textNode)) continue;
            string? text = VisuDocument.StringValue(textNode);
            if (string.IsNullOrEmpty(text)) continue;
            string? current = members.TryGetValue(VisuCatalog.TextId, out var idNode) ? VisuDocument.StringValue(idNode) : null;
            if (current is not null && Number(current) is null) current = null;

            string id;
            if (current is not null && known.TextById.TryGetValue(current, out var listed) && listed == text && FreeFor(current, text))
                id = current;                                   // filed in the text list under this id already
            else if (idOfText.TryGetValue(text, out var shared))
                id = shared;                                    // same text earlier on this page
            else if (listIdOfText.TryGetValue(text, out var listId) && FreeFor(listId, text))
                id = listId;                                    // the text list knows the text
            else if (current is not null && !known.TextById.ContainsKey(current) && !known.OtherIds.Contains(current) && FreeFor(current, text))
                id = current;                                   // an id nobody else uses
            else
                id = (next++).ToString(CultureInfo.InvariantCulture);

            textOfId[id] = text;
            idOfText.TryAdd(text, id);
            if (id == current) continue;
            VisuElements.SetMemberValue(doc, element, VisuCatalog.TextId,
                new XElement("v", new XAttribute("n", "Value"), VisuDocument.Quote(id)));
            lines.Add(string.Format(CultureInfo.InvariantCulture, "element {0}: text-list id {1} for \"{2}\"",
                VisuEditor.ElementId(element)?.ToString(CultureInfo.InvariantCulture) ?? "?", id, text));
        }
        return lines;
    }

    /// <summary>(text, text-list id) of every element with a static text; id null when the element has none.</summary>
    internal static IEnumerable<(string Text, string? Id)> Texts(VisuDocument doc)
    {
        foreach (var element in doc.Elements)
        {
            var members = VisuElements.Members(element);
            if (!members.TryGetValue(VisuCatalog.TextMember, out var textNode)) continue;
            string? text = VisuDocument.StringValue(textNode);
            if (string.IsNullOrEmpty(text)) continue;
            yield return (text, members.TryGetValue(VisuCatalog.TextId, out var idNode) ? VisuDocument.StringValue(idNode) : null);
        }
    }

    private static long? Number(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : null;

    /// <summary>The folder of the .plcproj above <paramref name="file"/>; null when there is none within 6 levels.</summary>
    internal static string? ProjectFolder(string file)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(file))!);
        for (int i = 0; dir is not null && i < 6; i++, dir = dir.Parent)
        {
            try { if (dir.EnumerateFiles("*.plcproj").Any()) return dir.FullName; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    // Text lists and visualizations of the project; library and build folders (_Libraries, _CompileInfo, …) are skipped.
    private static IEnumerable<string> ProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files)
                if (f.EndsWith(".TcGTLO", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".TcVIS", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            foreach (string d in dirs)
                if (!Path.GetFileName(d).StartsWith('_') && !Path.GetFileName(d).StartsWith('.'))
                    pending.Push(d);
        }
    }
}
