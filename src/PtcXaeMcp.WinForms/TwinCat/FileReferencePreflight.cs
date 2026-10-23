using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A File="….xti" reference whose independent project file does not exist.</summary>
internal sealed record MissingFileReference(string ReferencingFile, string Reference, string SearchedUnder);

internal sealed record FileReferenceReport(IReadOnlyList<MissingFileReference> Missing, IReadOnlyList<string> Warnings);

/// <summary>
/// Before open_project: every independent project file (.xti) a TwinCAT project references must exist.
/// After IO nodes were renamed outside the XAE the references point nowhere; the XAE then opens a broken
/// configuration, and saving it damages the .tsproj.
///
/// Where TwinCAT stores the file depends on the referencing element (seen in real projects: a .tsproj
/// reference lives in _Config\&lt;area&gt;\, a Box under &lt;folder&gt;\&lt;parent stem&gt;\, an Axis of NC.xti
/// in _Config\NC\Axes\), so the file is searched in the subtree: below _Config for the .tsproj, below
/// the referencing .xti's folder otherwise. Only File= values ending in .xti are references to independent
/// project files (AdditionalTmcFile File= names a .tmc).
/// </summary>
internal static class FileReferencePreflight
{
    public static FileReferenceReport Check(string slnPath)
    {
        var missing = new List<MissingFileReference>();
        var warnings = new List<string>();
        string slnDir = Path.GetDirectoryName(slnPath) ?? string.Empty;

        foreach (string p in SolutionFile.ProjectPaths(File.ReadAllText(slnPath)))
        {
            if (!p.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase)) continue;
            string tsproj = Path.GetFullPath(Path.Combine(slnDir, p));
            if (!File.Exists(tsproj)) continue; // the XAE reports a missing project file itself
            CheckProject(tsproj, missing, warnings);
        }
        return new FileReferenceReport(missing, warnings);
    }

    private static void CheckProject(string tsproj, List<MissingFileReference> missing, List<string> warnings)
    {
        string tsprojDir = Path.GetDirectoryName(tsproj) ?? string.Empty;
        string configDir = Path.Combine(tsprojDir, "_Config");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string File, string SearchRoot)>();
        queue.Enqueue((tsproj, configDir));

        while (queue.Count > 0)
        {
            var (file, searchRoot) = queue.Dequeue();
            if (!visited.Add(file)) continue;

            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{Relative(tsprojDir, file)} not checked: {ex.Message}");
                continue;
            }

            foreach (var attribute in doc.Descendants().Attributes("File"))
            {
                string reference = attribute.Value;
                if (!reference.EndsWith(".xti", StringComparison.OrdinalIgnoreCase)) continue;

                string? found = Find(searchRoot, reference);
                if (found is null)
                {
                    missing.Add(new MissingFileReference(Relative(tsprojDir, file), reference, Relative(tsprojDir, searchRoot)));
                    continue;
                }
                queue.Enqueue((found, Path.GetDirectoryName(found) ?? searchRoot));
            }
        }
    }

    // The direct candidates first (sub-folder named after the referencing file, the folder itself), then the subtree.
    private static string? Find(string searchRoot, string reference)
    {
        if (!Directory.Exists(searchRoot)) return null;
        string direct = Path.Combine(searchRoot, reference);
        if (File.Exists(direct)) return direct;
        try
        {
            return Directory.EnumerateFiles(searchRoot, Path.GetFileName(reference), SearchOption.AllDirectories)
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Relative(string baseDir, string path) => Path.GetRelativePath(baseDir, path);

    /// <summary>The refusal / warning text listing every missing reference.</summary>
    public static string Describe(IReadOnlyList<MissingFileReference> missing) =>
        $"{missing.Count} referenced project file{(missing.Count == 1 ? "" : "s")} (.xti) missing: " +
        string.Join("; ", missing.Select(m => $"{m.ReferencingFile} → File=\"{m.Reference}\" (not found under {m.SearchedUnder})")) + ".";
}
