using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// One declaration or implementation text of a PLC object, keyed by its member path inside the file:
/// "decl" / "impl" for the object itself, "Reset/decl", "Enabled/Get/impl" for members (folders are not
/// part of the key). A null <see cref="Text"/> means "present but not comparable" (a graphical body).
/// </summary>
internal sealed record PlcTextSection(string Key, string? Text);

/// <summary>A PLC project of the open solution: its tree name (the node under TIPC) and its .plcproj.</summary>
internal sealed record PlcProjectFile(string PlcName, string PlcprojPath);

/// <summary>
/// The PLC source files of the open solution, read from disk: which .plcproj belong to it, which source
/// files a .plcproj compiles, and the declaration/implementation texts inside a .TcPOU/.TcGVL/.TcDUT/.TcIO.
/// </summary>
internal static class PlcSourceFiles
{
    /// <summary>The file kinds whose texts the XAE exposes through ITcPlcDeclaration / ITcPlcImplementation.</summary>
    public static readonly IReadOnlySet<string> ComparableExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".TcPOU", ".TcGVL", ".TcDUT", ".TcIO" };

    /// <summary>
    /// Every PLC project of the solution: the TwinCAT projects (.tsproj) it declares, and per project each
    /// &lt;Plc&gt;&lt;Project Name=… PrjFilePath=…&gt; — inline in the .tsproj or in the independent project
    /// file its File= attribute names. PrjFilePath is relative to the .tsproj folder.
    /// </summary>
    public static IReadOnlyList<PlcProjectFile> FindPlcProjects(string slnPath)
    {
        string slnDir = Path.GetDirectoryName(slnPath) ?? string.Empty;
        var result = new List<PlcProjectFile>();
        foreach (string p in SolutionFile.ProjectPaths(File.ReadAllText(slnPath)))
        {
            if (!p.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) continue;
            string tsproj = Path.GetFullPath(Path.Combine(slnDir, p));
            if (!File.Exists(tsproj)) continue;
            string tsprojDir = Path.GetDirectoryName(tsproj) ?? string.Empty;

            foreach (var project in PlcProjectElements(tsproj))
            {
                string? name = (string?)project.Attribute("Name");
                string? prj = (string?)project.Attribute("PrjFilePath");
                string? file = (string?)project.Attribute("File");
                if (prj is null && file is not null)
                {
                    string? xti = FindIndependentFile(tsprojDir, "PLC", file);
                    XElement? inner = xti is null ? null : LoadOrNull(xti)?.Descendants("Project").FirstOrDefault();
                    name ??= (string?)inner?.Attribute("Name");
                    prj = (string?)inner?.Attribute("PrjFilePath");
                }
                if (string.IsNullOrEmpty(prj)) continue;
                string plcproj = Path.GetFullPath(Path.Combine(tsprojDir, prj));
                result.Add(new PlcProjectFile(name ?? Path.GetFileNameWithoutExtension(plcproj), plcproj));
            }
        }
        return result;
    }

    private static IEnumerable<XElement> PlcProjectElements(string tsproj) =>
        LoadOrNull(tsproj)?.Descendants("Plc").Elements("Project") ?? [];

    /// <summary>An independent project file sits in _Config\&lt;area&gt;\ next to the .tsproj.</summary>
    private static string? FindIndependentFile(string tsprojDir, string area, string file)
    {
        foreach (string candidate in new[] { Path.Combine(tsprojDir, "_Config", area, file), Path.Combine(tsprojDir, file) })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static XDocument? LoadOrNull(string path)
    {
        try { return XDocument.Load(path); }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The comparable source files a .plcproj compiles (&lt;Compile Include=…&gt;), as full paths.</summary>
    public static IReadOnlyList<string> SourceFiles(string plcprojPath)
    {
        string dir = Path.GetDirectoryName(plcprojPath) ?? string.Empty;
        var doc = LoadOrNull(plcprojPath);
        if (doc is null) return [];
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Compile")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(i => i is not null && ComparableExtensions.Contains(Path.GetExtension(i)))
            .Select(i => Path.GetFullPath(Path.Combine(dir, i!)))
            .ToList();
    }

    /// <summary>
    /// The texts of a PLC object file. Members (Method, Property, Get, Set, Action, Transition) are keyed by
    /// their Name chain; Folder elements and FolderPath attributes are ignored, like folders in the tree.
    /// A body that is not ST (FBD, LD, CFC, SFC) is recorded with a null text.
    /// </summary>
    public static IReadOnlyList<PlcTextSection> ParseSections(string xml)
    {
        var root = XDocument.Parse(xml).Root?.Elements().FirstOrDefault()
            ?? throw new XmlException("The file holds no PLC object.");
        var sections = new List<PlcTextSection>();
        Collect(root, string.Empty, sections);
        return sections;
    }

    private static readonly HashSet<string> MemberElements =
        new(StringComparer.Ordinal) { "Method", "Property", "Get", "Set", "Action", "Transition" };

    private static void Collect(XElement element, string prefix, List<PlcTextSection> sections)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Declaration":
                    sections.Add(new PlcTextSection(prefix + "decl", child.Value));
                    break;
                case "Implementation":
                    var st = child.Element("ST");
                    sections.Add(new PlcTextSection(prefix + "impl", st?.Value));
                    break;
                case var name when MemberElements.Contains(name):
                    string? memberName = (string?)child.Attribute("Name");
                    if (!string.IsNullOrEmpty(memberName)) Collect(child, prefix + memberName + "/", sections);
                    break;
            }
        }
    }

    /// <summary>Line endings unified, trailing whitespace per line and trailing empty lines dropped.</summary>
    public static string Normalize(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(l => l.TrimEnd())).TrimEnd('\n');

    /// <summary>
    /// The keys that differ between the file and the XAE: a text that differs, or a section only one side
    /// has. A null text on either side is never compared. Empty = same content.
    /// </summary>
    public static IReadOnlyList<string> Differences(IReadOnlyList<PlcTextSection> file, IReadOnlyList<PlcTextSection> xae)
    {
        var a = file.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
        var b = xae.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
        var diffs = new List<string>();
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase))
        {
            bool inA = a.TryGetValue(key, out var ta), inB = b.TryGetValue(key, out var tb);
            if (!inA || !inB)
            {
                // A graphical body is compared by neither side; its absence on the other side is no evidence.
                if ((inA && ta is null) || (inB && tb is null)) continue;
                diffs.Add(key);
            }
            else if (ta is not null && tb is not null && Normalize(ta) != Normalize(tb))
            {
                diffs.Add(key);
            }
        }
        return diffs;
    }
}
