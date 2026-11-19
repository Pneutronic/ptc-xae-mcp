using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Pure XML edits of the <c>ProjectVariantConfig</c> document (add a variant, add it to a group, remove it).
/// The document is written back to the XAE as a whole, so every edit starts from the XML just read and keeps
/// all other elements untouched.
/// </summary>
internal static class VariantConfigEditor
{
    private static readonly char[] ForbiddenNameChars = ['[', ']', '^', '<', '>', '&', '"', '\'', '\\', '/'];

    /// <summary>Null when the name can be used for a variant; otherwise the "[Error] ..." text.</summary>
    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "[Error] variantName is required.";
        string n = name.Trim();
        if (n.Any(char.IsControl) || n.IndexOfAny(ForbiddenNameChars) >= 0)
            return $"[Error] Invalid variant name '{n}': it must not contain [ ] ^ < > & \" ' \\ / or control characters " +
                   "('[Name]' is reserved for variant groups).";
        return null;
    }

    private static bool Is(XElement e, string localName) =>
        string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static bool IsGroup(XElement e) => Is(e, "VariantGroup") || Is(e, "Group");

    private static bool IsVariantLike(XElement e) => Is(e, "Variant") || Is(e, "Member");

    private static (XDocument? Doc, string? Error) Load(string xml)
    {
        try
        {
            return (XDocument.Parse(string.IsNullOrWhiteSpace(xml) ? "<ProjectVariants/>" : xml), null);
        }
        catch (Exception ex)
        {
            return (null, $"[Error] ProjectVariantConfig is not well-formed XML ({ex.Message}). Nothing was changed.");
        }
    }

    private static string Serialize(XDocument doc) =>
        (doc.Declaration?.ToString() ?? string.Empty) + doc.Root!.ToString(SaveOptions.DisableFormatting);

    private static XElement Entry(string elementName, string name) =>
        new(elementName, new XElement("Name", name));

    private static IEnumerable<XElement> TopLevelVariants(XElement root) =>
        root.Elements().Where(e => Is(e, "Variant"));

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list);
    }

    internal static (string? Xml, string? Error) AddVariant(string xml, string name, string? group)
    {
        string? invalid = ValidateName(name);
        if (invalid is not null) return (null, invalid);
        string requested = name.Trim();

        var (doc, loadError) = Load(xml);
        if (doc is null) return (null, loadError);
        var root = doc.Root!;

        var groups = root.Elements().Where(IsGroup).ToList();
        if (TopLevelVariants(root).Any(v => string.Equals(VariantConfigParser.GetVariantName(v), requested, StringComparison.OrdinalIgnoreCase)))
            return (null, $"[Error] Variant '{requested}' already exists. Nothing was changed.");
        if (groups.Any(g => string.Equals(VariantConfigParser.GetVariantName(g), requested, StringComparison.OrdinalIgnoreCase)))
            return (null, $"[Error] '{requested}' is already the name of a variant group. Nothing was changed.");

        XElement? target = null;
        if (!string.IsNullOrWhiteSpace(group))
        {
            string g = group.Trim().Trim('[', ']').Trim();
            target = groups.FirstOrDefault(e =>
                string.Equals(VariantConfigParser.GetVariantName(e), g, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                return (null, $"[Error] Unknown group '{g}'. Existing groups: " +
                              $"{Names(groups.Select(e => VariantConfigParser.GetVariantName(e) ?? string.Empty))}. Nothing was changed.");
        }

        root.Add(Entry("Variant", requested));
        if (target is not null)
        {
            // Follow the spelling the group already uses for its members.
            string memberName = target.Elements().FirstOrDefault(IsVariantLike)?.Name.LocalName
                                ?? (Is(target, "Group") ? "Member" : "Variant");
            target.Add(Entry(memberName, requested));
        }
        return (Serialize(doc), null);
    }

    internal static (string? Xml, string? Error, IReadOnlyList<string> Groups) RemoveVariant(string xml, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, "[Error] variantName is required.", []);
        string requested = name.Trim();

        var (doc, loadError) = Load(xml);
        if (doc is null) return (null, loadError, []);
        var root = doc.Root!;

        var variants = TopLevelVariants(root).ToList();
        var match = variants.FirstOrDefault(v =>
            string.Equals(VariantConfigParser.GetVariantName(v), requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            string list = variants.Count == 0
                ? "none — this project defines no variants"
                : Names(variants.Select(v => VariantConfigParser.GetVariantName(v) ?? string.Empty));
            return (null, $"[Error] Unknown variant '{requested}'. Expected one of: {list}. Nothing was changed.", []);
        }

        string canonical = VariantConfigParser.GetVariantName(match)!;
        var touched = new List<string>();
        foreach (var g in root.Elements().Where(IsGroup))
        {
            var members = g.Elements()
                .Where(e => IsVariantLike(e) &&
                            string.Equals(VariantConfigParser.GetVariantName(e), canonical, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (members.Count == 0) continue;
            foreach (var m in members) m.Remove();
            touched.Add(VariantConfigParser.GetVariantName(g) ?? string.Empty);
        }
        match.Remove();
        return (Serialize(doc), null, touched);
    }
}
