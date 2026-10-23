using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Text and JSON output of <c>twincat_list_library_references</c>.</summary>
internal static class LibraryReferenceFormat
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Null when <paramref name="format"/> is valid ("text" or "json", default text); an [Error] text otherwise.</summary>
    public static string? Validate(string? format, out bool json)
    {
        json = false;
        if (string.IsNullOrWhiteSpace(format) || string.Equals(format.Trim(), "text", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(format.Trim(), "json", StringComparison.OrdinalIgnoreCase)) { json = true; return null; }
        return $"[Error] format '{format}' is invalid — expected 'text' or 'json'.";
    }

    /// <summary>The suffix "[resolved: Tc2_Standard 3.4.5.0 (Beckhoff Automation GmbH), effective]".</summary>
    public static string ResolvedSuffix(ResolvedReference r)
    {
        if (r.Name is null || r.Version is null) return $"[resolved: -, {r.Resolution}]";
        string distributor = string.IsNullOrWhiteSpace(r.Distributor) ? string.Empty : $" ({r.Distributor})";
        return $"[resolved: {r.Name} {r.Version}{distributor}, {r.Resolution}]";
    }

    public static string Text(string summary, IReadOnlyList<ResolvedLibraryReference> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine(summary);
        foreach (var item in items)
        {
            var r = item.Reference;
            string fileNote = r.FilePinnedVersion is null
                ? string.Empty
                : (r.VersionMatchesFile == true
                    ? "  [file: match]"
                    : $"  [file: {r.FilePinnedVersion} — mismatch]");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {r.Placeholder}: {r.Name} {r.Version} ({r.Publisher}) [src={r.Source.ToString().ToLowerInvariant()}]{fileNote} {ResolvedSuffix(item.Resolved)}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Json(string summary, IReadOnlyList<ResolvedLibraryReference> items, IReadOnlyList<string> warnings) =>
        JsonSerializer.Serialize(new
        {
            success = true,
            summary,
            count = items.Count,
            references = items.Select(i => new
            {
                placeholder = i.Reference.Placeholder,
                name = i.Reference.Name,
                version = i.Reference.Version,
                publisher = i.Reference.Publisher,
                source = i.Reference.Source.ToString().ToLowerInvariant(),
                filePinnedVersion = i.Reference.FilePinnedVersion,
                versionMatchesFile = i.Reference.VersionMatchesFile,
                resolvedName = i.Resolved.Name,
                resolvedVersion = i.Resolved.Version,
                resolvedDistributor = i.Resolved.Distributor,
                resolution = i.Resolved.Resolution,
            }),
            warnings,
        }, JsonOpts);
}
