using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

// Extracted as internal static so unit tests can drive it with real Beckhoff XML.
// Beckhoff ProjectVariantConfig uses <Name> child elements, not Name="..." attributes.
internal static class VariantConfigParser
{
    // Child element <Name>...</Name> takes priority (actual Beckhoff format),
    // falls back to Name="..." attribute for forward-compatibility.
    internal static string? GetVariantName(XElement e) =>
        e.Elements()
         .FirstOrDefault(c => string.Equals(c.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))
         ?.Value
         ?? e.Attributes()
             .FirstOrDefault(a => string.Equals(a.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))
             ?.Value;

    // A group is <VariantGroup> (captured format) or <Group>; its members are <Variant> or <Member>.
    private static bool IsGroup(XElement e) =>
        string.Equals(e.Name.LocalName, "VariantGroup", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(e.Name.LocalName, "Group", StringComparison.OrdinalIgnoreCase);

    private static bool IsMember(XElement e) =>
        string.Equals(e.Name.LocalName, "Variant", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(e.Name.LocalName, "Member", StringComparison.OrdinalIgnoreCase);

    internal static (IReadOnlyList<string> Variants, IReadOnlyList<VariantGroupEntry> Groups)
        Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return ([], []);

            var groups = root.Descendants()
                .Where(IsGroup)
                .Select(g =>
                {
                    string name = GetVariantName(g) ?? string.Empty;
                    var members = g.Descendants()
                        .Where(IsMember)
                        .Select(e => GetVariantName(e) ?? string.Empty)
                        .Where(n => n.Length > 0)
                        .ToList();
                    return new VariantGroupEntry(name, members);
                })
                .Where(g => g.Name.Length > 0)
                .ToList();

            var variants = root.Descendants()
                .Where(e =>
                    string.Equals(e.Name.LocalName, "Variant", StringComparison.OrdinalIgnoreCase) &&
                    !(e.Parent is { } parent && IsGroup(parent)))
                .Select(e => GetVariantName(e) ?? string.Empty)
                .Where(n => n.Length > 0)
                .ToList();

            return (variants, groups);
        }
        catch
        {
            return ([], []);
        }
    }
}
