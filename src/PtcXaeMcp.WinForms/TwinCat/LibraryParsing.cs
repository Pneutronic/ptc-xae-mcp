using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

internal static class LibraryParsing
{
    // B-6 fix: the publisher parenthesis is optional — a reference can resolve to
    // "Name, Version" with no "(Publisher)" suffix at all, and the previous regex required
    // that group, so those entries parsed to null and were silently dropped from every
    // listing (which in turn broke add/remove/availability checks against them). Version is
    // matched lazily so a version that itself contains a trailing "(newest)" annotation
    // (e.g. "* (newest)") is kept together with the version, not mistaken for the publisher.
    // XAE may append a category tag such as " [Standard]" (seen live on TC 4026.22 for system
    // libraries: "Tc2_Standard, * (Beckhoff Automation GmbH) [Standard]") — it is optional and
    // ignored, so the publisher is still parsed.
    private static readonly Regex ResolutionRegex = new(
        @"^(?<name>[^,]+?)\s*,\s*(?<version>.+?)\s*(?:\((?<publisher>[^()]*)\))?\s*(?:\[(?<tag>[^\]]*)\])?\s*$",
        RegexOptions.Compiled);

    private static readonly XNamespace MsBuildNs =
        "http://schemas.microsoft.com/developer/msbuild/2003";

    public static (string Name, string Version, string Publisher)? ParseResolution(string resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return null;
        var m = ResolutionRegex.Match(resolution.Trim());
        if (!m.Success) return null;
        return (
            m.Groups["name"].Value.Trim(),
            m.Groups["version"].Value.Trim(),
            m.Groups["publisher"].Value.Trim());
    }

    public static LibraryReference? ParseSingleReferenceXml(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var doc = XDocument.Parse(xml);

            var pref = doc.Descendants("PlaceholderReference").FirstOrDefault();
            string placeholder = (string?)pref?.Element("PlaceholderName") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(placeholder)) return null;

            string resolution = doc.Descendants("VSProperty")
                .Where(p => (string?)p.Element("Name") == "Resolution")
                .Select(p => (string?)p.Element("Value") ?? string.Empty)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(resolution))
                resolution = (string?)pref?.Element("DefaultResolution") ?? string.Empty;

            return BuildReference(placeholder, resolution, LibrarySource.Xae);
        }
        catch { return null; }
    }

    public static List<LibraryReference> ListReferencesFromPlcproj(string plcprojPath)
    {
        var results = new List<LibraryReference>();
        XDocument doc;
        try { doc = XDocument.Load(plcprojPath); }
        catch { return results; }

        var resolutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pr in doc.Descendants(MsBuildNs + "PlaceholderResolution"))
        {
            string include = (string?)pr.Attribute("Include") ?? string.Empty;
            string resolution = (string?)pr.Element(MsBuildNs + "Resolution") ?? string.Empty;
            if (include.Length > 0 && resolution.Length > 0)
                resolutions[include] = resolution;
        }

        foreach (var pref in doc.Descendants(MsBuildNs + "PlaceholderReference"))
        {
            string placeholder = (string?)pref.Attribute("Include") ?? string.Empty;
            if (placeholder.Length == 0) continue;

            string? resolution = resolutions.TryGetValue(placeholder, out var pinned)
                ? pinned
                : (string?)pref.Element(MsBuildNs + "DefaultResolution");
            if (string.IsNullOrWhiteSpace(resolution)) continue;

            var entry = BuildReference(placeholder, resolution, LibrarySource.Plcproj);
            if (entry is not null) results.Add(entry);
        }

        // Also handle legacy LibraryReference elements (Include="Name,version,company").
        // AddLibrary (COM) creates these; AddPlaceholder creates PlaceholderReference.
        // We surface them so they can be removed via RemoveReference.
        var knownNames = new HashSet<string>(results.Select(r => r.Placeholder), StringComparer.OrdinalIgnoreCase);
        foreach (var lref in doc.Descendants(MsBuildNs + "LibraryReference"))
        {
            string include = (string?)lref.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrEmpty(include)) continue;
            var parts = include.Split(',');
            if (parts.Length < 1) continue;
            string lname = parts[0].Trim();
            if (string.IsNullOrEmpty(lname) || knownNames.Contains(lname)) continue;
            string lver = parts.Length > 1 ? parts[1].Trim() : "*";
            string lcompany = parts.Length > 2 ? parts[2].Trim() : string.Empty;
            string resolution = string.IsNullOrEmpty(lcompany)
                ? $"{lname}, {lver}"
                : $"{lname}, {lver} ({lcompany})";
            var entry = BuildReference(lname, resolution, LibrarySource.Plcproj);
            if (entry is not null) { results.Add(entry); knownNames.Add(lname); }
        }

        return results;
    }

    // Namespace-agnostic element lookup: the XAE has been observed writing <Parameter>/<Key>/
    // <Value>/<Parameters> nodes with an explicit xmlns="" that cancels the inherited MSBuild
    // default namespace for that subtree (live-verified: three overrides existed on disk but
    // ListParametersFromPlcproj(MsBuildNs + "Parameter") found none, because the actual nodes
    // carried no namespace). Matching by LocalName finds them regardless of which namespace —
    // or none — the writer used.
    private static IEnumerable<XElement> ByLocalName(XElement parent, string localName) =>
        parent.Descendants().Where(e => e.Name.LocalName == localName);

    private static XElement? ChildByLocalName(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    // Parameter-list overrides of one placeholder reference, read from the .plcproj
    // (<Parameter ListName="GVL_PARAMETER"><Key>NUMBER_OF_ITEMS</Key><Value>101</Value>),
    // regardless of the namespace the individual Parameter/Key/Value nodes carry.
    public static List<LibraryParameter> ListParametersFromPlcproj(string plcprojPath, string placeholder)
    {
        var results = new List<LibraryParameter>();
        XDocument doc;
        try { doc = XDocument.Load(plcprojPath); }
        catch { return results; }

        foreach (var pref in doc.Descendants(MsBuildNs + "PlaceholderReference"))
        {
            string include = (string?)pref.Attribute("Include") ?? string.Empty;
            // XAE placeholder and .plcproj Include can differ (e.g. XAE "PTC" vs file
            // "Sample-Library") — also accept a match on the DefaultResolution library name.
            string resolutionName = ParseResolution(
                (string?)pref.Element(MsBuildNs + "DefaultResolution") ?? string.Empty)?.Name ?? string.Empty;
            bool matches =
                string.Equals(include, placeholder, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolutionName, placeholder, StringComparison.OrdinalIgnoreCase);
            if (!matches) continue;

            foreach (var par in ByLocalName(pref, "Parameter"))
            {
                string listName = (string?)par.Attribute("ListName") ?? string.Empty;
                string key = ChildByLocalName(par, "Key")?.Value ?? string.Empty;
                string value = ChildByLocalName(par, "Value")?.Value ?? string.Empty;
                if (key.Length > 0)
                    results.Add(new LibraryParameter(include, listName, key, value));
            }
        }
        return results;
    }

    // Upserts one <Parameter> override into the .plcproj on disk (MSBuild schema:
    // <PlaceholderReference Include="…"><Parameters><Parameter ListName="…"><Key/><Value/>).
    // Only safe while the solution is CLOSED in XAE — the caller owns that lifecycle.
    // The XAE placeholder name can differ from the file Include (e.g. "PTC" vs "Sample-Library"),
    // so matching is exact-first, then unique-substring with an ambiguity guard.
    public static void UpsertParameterInPlcproj(
        string plcprojPath, string placeholder, string listName, string key, string value)
    {
        var doc = XDocument.Load(plcprojPath);
        ApplyParameter(doc, Path.GetFileName(plcprojPath), placeholder, listName, key, value);
        doc.Save(plcprojPath);
    }

    /// <summary>
    /// Dry run of <see cref="UpsertParameterInPlcproj"/> (issue #148, B-2): proves the file is
    /// readable, writable and contains a unique matching placeholder BEFORE the caller closes
    /// the solution. Throws with the same message the real upsert would.
    /// </summary>
    public static void ValidateParameterUpsert(
        string plcprojPath, string placeholder, string listName, string key, string value)
    {
        RequireWritable(plcprojPath);
        var doc = XDocument.Load(plcprojPath);
        ApplyParameter(doc, Path.GetFileName(plcprojPath), placeholder, listName, key, value);
    }

    /// <summary>Dry run of <see cref="UpsertPlaceholderResolutionInPlcproj"/>; see <see cref="ValidateParameterUpsert"/>.</summary>
    public static void ValidatePlaceholderResolutionUpsert(string plcprojPath, string placeholder, string resolution)
    {
        RequireWritable(plcprojPath);
        var doc = XDocument.Load(plcprojPath);
        ApplyPlaceholderResolution(doc, Path.GetFileName(plcprojPath), placeholder, resolution);
    }

    private static void RequireWritable(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new InvalidOperationException($"{fi.Name} not found: {path}");
        if (fi.IsReadOnly)
            throw new InvalidOperationException($"{fi.Name} is read-only — cannot update it: {path}");
    }

    private static void ApplyParameter(
        XDocument doc, string fileName, string placeholder, string listName, string key, string value)
    {
        var refs = doc.Descendants(MsBuildNs + "PlaceholderReference").ToList();

        static string Inc(XElement e) => (string?)e.Attribute("Include") ?? string.Empty;
        string ResName(XElement e) => ParseResolution(
            (string?)e.Element(MsBuildNs + "DefaultResolution") ?? string.Empty)?.Name ?? string.Empty;

        var exact = refs.Where(r =>
            string.Equals(Inc(r), placeholder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ResName(r), placeholder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0)
            exact = refs.Where(r =>
                Inc(r).Contains(placeholder, StringComparison.OrdinalIgnoreCase) ||
                placeholder.Contains(Inc(r), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0)
            throw new InvalidOperationException(
                $"No <PlaceholderReference> matching '{placeholder}' in {fileName}.");
        if (exact.Count > 1)
            throw new InvalidOperationException(
                $"Placeholder '{placeholder}' is ambiguous in {fileName}: " +
                string.Join(", ", exact.Select(Inc)));

        var pref = exact[0];
        // Reuse an existing <Parameters> wrapper regardless of its namespace — creating a
        // second, MSBuild-namespaced <Parameters> sibling next to an XAE-written xmlns=""
        // one would silently split future overrides across two containers.
        var parameters = ChildByLocalName(pref, "Parameters");
        XNamespace parameterNs = parameters?.Name.Namespace ?? MsBuildNs;
        if (parameters is null)
        {
            parameters = new XElement(MsBuildNs + "Parameters");
            pref.Add(parameters);
        }

        var existing = ByLocalName(parameters, "Parameter").FirstOrDefault(p =>
            string.Equals((string?)p.Attribute("ListName") ?? string.Empty, listName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ChildByLocalName(p, "Key")?.Value ?? string.Empty, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var valueEl = ChildByLocalName(existing, "Value");
            if (valueEl is not null)
                valueEl.Value = value;
            else
                existing.Add(new XElement(existing.Name.Namespace + "Value", value));
        }
        else
        {
            parameters.Add(new XElement(parameterNs + "Parameter",
                new XAttribute("ListName", listName),
                new XElement(parameterNs + "Key", key),
                new XElement(parameterNs + "Value", value)));
        }
    }

    // Sets (or adds) the machine-local <PlaceholderResolution> pin of a placeholder in the
    // .plcproj on disk. Only safe while the solution is CLOSED in XAE — caller owns that.
    public static void UpsertPlaceholderResolutionInPlcproj(
        string plcprojPath, string placeholder, string resolution)
    {
        var doc = XDocument.Load(plcprojPath);
        ApplyPlaceholderResolution(doc, Path.GetFileName(plcprojPath), placeholder, resolution);
        doc.Save(plcprojPath);
    }

    private static void ApplyPlaceholderResolution(XDocument doc, string fileName, string placeholder, string resolution)
    {
        var existing = doc.Descendants(MsBuildNs + "PlaceholderResolution").FirstOrDefault(r =>
            string.Equals((string?)r.Attribute("Include") ?? string.Empty, placeholder, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.SetElementValue(MsBuildNs + "Resolution", resolution);
            return;
        }

        var pref = doc.Descendants(MsBuildNs + "PlaceholderReference").FirstOrDefault(r =>
            string.Equals((string?)r.Attribute("Include") ?? string.Empty, placeholder, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No <PlaceholderReference> '{placeholder}' in {fileName}.");
        pref.Parent!.Add(new XElement(MsBuildNs + "PlaceholderResolution",
            new XAttribute("Include", placeholder),
            new XElement(MsBuildNs + "Resolution", resolution)));
    }

    // All parameter overrides in the .plcproj, regardless of placeholder naming — used to
    // verify a write when the XAE placeholder does not textually match the file Include.
    public static List<LibraryParameter> ListAllParametersFromPlcproj(string plcprojPath)
    {
        var results = new List<LibraryParameter>();
        XDocument doc;
        try { doc = XDocument.Load(plcprojPath); }
        catch { return results; }

        foreach (var pref in doc.Descendants(MsBuildNs + "PlaceholderReference"))
        {
            string include = (string?)pref.Attribute("Include") ?? string.Empty;
            foreach (var par in ByLocalName(pref, "Parameter"))
            {
                string listName = (string?)par.Attribute("ListName") ?? string.Empty;
                string key = ChildByLocalName(par, "Key")?.Value ?? string.Empty;
                string value = ChildByLocalName(par, "Value")?.Value ?? string.Empty;
                if (key.Length > 0)
                    results.Add(new LibraryParameter(include, listName, key, value));
            }
        }
        return results;
    }

    // Upserts one <Parameter> into the XAE TreeItem XML of a placeholder reference
    // (ProduceXml → edit → ConsumeXml). The TreeItem schema differs from the .plcproj:
    //   <PlcLibPlaceholder><PlaceholderReference id="…">…
    //     <ParameterList><Parameter><Name>GVL_PARAMETER.NUMBER_OF_ITEMS</Name><Value>101</Value>
    // (Name = "<ListName>.<Key>"; verified live against XAE 3.1 / TREEITEMTYPE_PLCLIBPLACEHOLDER).
    public static string UpsertParameterInReferenceXml(
        string xml, string listName, string key, string value, string? placeholderFilter = null)
    {
        var doc = XDocument.Parse(xml);
        var candidates = doc.Descendants("PlaceholderReference");
        var pref = (placeholderFilter is null
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(p =>
                    string.Equals((string?)p.Element("PlaceholderName"), placeholderFilter, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(((string?)p.Element("LibItemName") ?? string.Empty).TrimStart('#'), placeholderFilter, StringComparison.OrdinalIgnoreCase) ||
                    (((string?)p.Element("DefaultResolution")) ?? string.Empty)
                        .StartsWith(placeholderFilter + ",", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException(
                "TreeItem XML has no matching <PlaceholderReference> — parameters require a placeholder reference.");

        var parameterList = pref.Element("ParameterList");
        if (parameterList is null)
        {
            parameterList = new XElement("ParameterList");
            pref.Add(parameterList);
        }

        string qualified = $"{listName}.{key}";
        var existing = parameterList.Elements("Parameter").FirstOrDefault(p =>
            string.Equals((string?)p.Element("Name") ?? string.Empty, qualified, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.SetElementValue("Value", value);
        }
        else
        {
            parameterList.Add(new XElement("Parameter",
                new XElement("Name", qualified),
                new XElement("Value", value)));
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    // B-10 fix: a solution with more than one PLC project (e.g. two PLC instances under one
    // TwinCAT solution) previously resolved to whichever .plcproj Directory.EnumerateFiles
    // happened to return first — silently reading/writing the wrong PLC's library references.
    // With exactly one candidate we still return it (unchanged single-PLC behaviour); with
    // more than one, an exact filename match against the solution's own base name is a safe
    // disambiguator (the common one-PLC-per-solution naming convention), and anything else
    // throws instead of guessing.
    // plcName (the PLC instance name under TIPC) picks that PLC's .plcproj: the file named after it, else the one in a
    // folder named after it (the XAE's layout, "<plc>\<plc>.plcproj"). Without a match it throws, never guesses.
    public static string? FindPlcprojNear(string solutionPath, string? plcName = null)
    {
        if (string.IsNullOrEmpty(solutionPath)) return null;
        string? solutionDir = Path.GetDirectoryName(solutionPath);
        if (string.IsNullOrEmpty(solutionDir)) return null;

        List<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(solutionDir, "*.plcproj", SearchOption.AllDirectories).ToList();
        }
        catch { return null; }

        if (plcName is not null)
        {
            var named = candidates.Where(c => string.Equals(Path.GetFileNameWithoutExtension(c), plcName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                named = candidates.Where(c => string.Equals(Path.GetFileName(Path.GetDirectoryName(c)), plcName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 1) return named[0];
            throw new InvalidOperationException(named.Count == 0
                ? $"No .plcproj of PLC '{plcName}' found under '{solutionDir}' (candidates: {string.Join(", ", candidates.Select(Path.GetFileName))})."
                : $"Ambiguous .plcproj for PLC '{plcName}': {string.Join(", ", named)}.");
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        string solutionBaseName = Path.GetFileNameWithoutExtension(solutionPath);
        var byName = candidates
            .Where(c => string.Equals(Path.GetFileNameWithoutExtension(c), solutionBaseName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1) return byName[0];

        throw new InvalidOperationException(
            $"Ambiguous .plcproj — {candidates.Count} candidates found under '{solutionDir}': " +
            string.Join(", ", candidates.Select(Path.GetFileName)) +
            ". Name the PLC with plcProjectName (library tools), or rename one to match the solution.");
    }

    public static List<LibraryReference> MergeWithFile(
        IReadOnlyList<LibraryReference> xaeRefs,
        IReadOnlyList<LibraryReference> fileRefs)
    {
        var fileByPlaceholder = fileRefs.ToDictionary(
            r => r.Placeholder, r => r, StringComparer.OrdinalIgnoreCase);

        var result = xaeRefs.Select(r =>
        {
            if (!fileByPlaceholder.TryGetValue(r.Placeholder, out var fileRef))
                return r with { FilePinnedVersion = null, VersionMatchesFile = null };
            bool match = string.Equals(r.Version, fileRef.Version, StringComparison.OrdinalIgnoreCase);
            return r with { FilePinnedVersion = fileRef.Version, VersionMatchesFile = match };
        }).ToList();

        // Surface file-only refs (e.g. LibraryReference entries not parsed by XAE tree reader).
        // Deduplicate by both Placeholder and Name — the XAE PlaceholderName may differ from
        // the plcproj Include attribute (e.g. XAE "PTC" vs file "Sample-Library").
        var xaePlaceholders = new HashSet<string>(xaeRefs.Select(r => r.Placeholder), StringComparer.OrdinalIgnoreCase);
        var xaeLibNames = new HashSet<string>(xaeRefs.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var fr in fileRefs)
        {
            if (!xaePlaceholders.Contains(fr.Placeholder) && !xaeLibNames.Contains(fr.Name))
                result.Add(fr);
        }

        return result;
    }

    private static LibraryReference? BuildReference(string placeholder, string resolution, LibrarySource source)
    {
        var parsed = ParseResolution(resolution);
        if (parsed is null) return null;
        return new LibraryReference(
            Name: parsed.Value.Name,
            Version: parsed.Value.Version,
            Publisher: parsed.Value.Publisher,
            Placeholder: placeholder,
            EffectiveResolution: resolution,
            Source: source,
            FilePinnedVersion: null,
            VersionMatchesFile: null);
    }
}
