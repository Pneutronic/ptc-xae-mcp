using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

// twincat_library_signatures: the public API (type signatures) of the libraries the PLC project references, as the XAE
// serialises it. Compiled vendor libraries have no readable source; this is how an agent learns FB inputs and outputs
// before the first compile instead of guessing them. Read-only: nothing in the project is saved or added; the XML goes
// to a file outside the project unless outputPath says otherwise.
internal sealed class LibrarySignatureCoordinator(ITwinCatSession session, string? defaultDirectory = null)
{
    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", "library-signatures");

    public string Export(string? library, string? outputPath, string? typeName, string? plcProjectName = null)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        LibrarySignatureExport export;
        try { export = session.ProduceLibrarySignatures(plcProjectName); }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        catch (Exception ex) { return $"[Error] The XAE could not serialise the library signatures: {ex.Message}"; }

        const string none = "[Error] The XAE returned no library signatures (no resolved library in the PLC project).";
        if (string.IsNullOrWhiteSpace(export.Xml)) return none;

        IReadOnlyList<LibrarySignatureSet> all;
        try { all = LibrarySignatureXml.Parse(export.Xml); }
        catch (FormatException ex) { return $"[Error] {ex.Message}"; }
        if (all.Count == 0) return none;

        IReadOnlyList<LibraryReference> references;
        try { references = session.ListLibraryReferences(plcProjectName); }
        catch (Exception ex) when (!ComCall.IsBusy(ex)) { references = []; }

        var selected = all;
        if (!string.IsNullOrWhiteSpace(library))
        {
            string wanted = library.Trim();
            // A placeholder names the library it resolves to through its reference.
            string? viaPlaceholder = references.FirstOrDefault(r => r.Placeholder.Equals(wanted, StringComparison.OrdinalIgnoreCase))?.Name;
            selected = all.Where(l => l.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                   || (viaPlaceholder is not null && l.Name.Equals(viaPlaceholder, StringComparison.OrdinalIgnoreCase))).ToList();
            if (selected.Count == 0)
                return $"[Error] Library '{wanted}' is not in the export. Libraries: {string.Join(", ", all.Select(l => l.Name))}.";
        }

        string path = string.IsNullOrWhiteSpace(outputPath) ? DefaultPath() : outputPath.Trim();
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, ReferenceEquals(selected, all) ? export.Xml : string.Join(Environment.NewLine, selected.Select(l => l.Element.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"[Error] The signatures could not be written to '{path}': {ex.Message}";
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Exported the signatures of {selected.Count} librar{(selected.Count == 1 ? "y" : "ies")} to {path} (read on the {export.Node} node, XAE: {export.Host}).");
        foreach (var lib in selected)
        {
            string counts = string.Join(", ", lib.Types.GroupBy(t => t.Type).Select(g => $"{g.Count()} {g.Key}"));
            string distributor = lib.Distributor.Length == 0 ? "" : $" ({lib.Distributor})";
            sb.Append(CultureInfo.InvariantCulture, $"\n  {lib.Name} {lib.Version}{distributor}: {(counts.Length == 0 ? "no public types" : counts)}");
        }

        if (string.IsNullOrWhiteSpace(library))
        {
            var missing = references
                .Where(r => !all.Any(l => l.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                sb.Append(CultureInfo.InvariantCulture,
                    $"\n[Warning] Referenced but not in the export (not resolved — installed?): {string.Join(", ", missing)}. Check with twincat_check_libraries_installed.");
        }

        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var hits = selected.SelectMany(l => l.Types.Where(t => t.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase)).Select(t => (Lib: l, Type: t))).ToList();
            if (hits.Count == 0)
                return $"[Error] No type '{typeName.Trim()}' in {string.Join(", ", selected.Select(l => l.Name))}. The signatures were written to {path}.";
            foreach (var (lib, type) in hits) AppendType(sb, lib, type);
        }
        return sb.ToString();
    }

    private string DefaultPath()
    {
        string? sln = session.ActiveSolutionPath();
        string name = string.IsNullOrWhiteSpace(sln) ? "project" : Path.GetFileNameWithoutExtension(sln);
        return Path.Combine(defaultDirectory ?? DefaultDirectory, $"{name}.library-signatures.xml");
    }

    private static void AppendType(StringBuilder sb, LibrarySignatureSet lib, TypeSignatureInfo type)
    {
        string comment = type.Comment is null ? "" : $" — {type.Comment}";
        sb.Append(CultureInfo.InvariantCulture, $"\n{type.Name} ({type.Type}) in {lib.Name} {lib.Version}{comment}");
        if (type.Sections.Count == 0) sb.Append("\n  (no inputs, outputs or constants in the export)");
        foreach (var section in type.Sections)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n  {section.Name}:");
            foreach (var item in section.Items)
                sb.Append(CultureInfo.InvariantCulture, $"\n    {item.Name} : {item.DataType}{(item.Comment is null ? "" : $" — {item.Comment}")}");
        }
    }
}
