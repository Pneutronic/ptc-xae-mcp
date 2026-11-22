using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Reads and sets the axis "Simulation" flag in an axis <c>ProduceXml</c> document. The flag is stored as a
/// <c>SimulationMode</c> attribute or element; when the XAE build does not put it into the axis XML there is no
/// way to reach it, and <see cref="TrySet"/> says so by returning null instead of adding the flag by guess.
/// </summary>
internal static class AxisSimulationXml
{
    private const string Name = "SimulationMode";

    private static bool Parse(string? text) =>
        string.Equals(text?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || text?.Trim() == "1";

    private static (XElement? Element, XAttribute? Attribute) Find(XDocument doc)
    {
        var attribute = doc.Descendants().SelectMany(e => e.Attributes())
            .FirstOrDefault(a => string.Equals(a.Name.LocalName, Name, StringComparison.OrdinalIgnoreCase));
        if (attribute is not null) return (null, attribute);
        var element = doc.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, Name, StringComparison.OrdinalIgnoreCase));
        return (element, null);
    }

    /// <summary>The flag's value, or null when the axis XML does not carry it.</summary>
    internal static bool? TryRead(string axisXml)
    {
        try
        {
            var (element, attribute) = Find(XDocument.Parse(axisXml));
            if (attribute is not null) return Parse(attribute.Value);
            if (element is not null) return Parse(element.Value);
            return null;
        }
        catch { return null; }
    }

    /// <summary>The axis XML with the flag set, or null when the axis XML does not carry the flag.</summary>
    internal static string? TrySet(string axisXml, bool value)
    {
        XDocument doc;
        try { doc = XDocument.Parse(axisXml, LoadOptions.PreserveWhitespace); }
        catch { return null; }
        var (element, attribute) = Find(doc);
        string text = value ? "true" : "false";
        if (attribute is not null) attribute.Value = text;
        else if (element is not null) element.Value = text;
        else return null;
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
