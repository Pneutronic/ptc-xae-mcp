using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat.Apply;

/// <summary>One entry of a PDO (<c>ProcessData/TxPdo|RxPdo/Entry</c>).</summary>
/// <param name="Index">CoE index; 0 marks a gap entry (padding), which is never a link target.</param>
internal sealed record PdoEntry(int Index, int SubIndex, int BitLen, string Name, string DataType)
{
    /// <summary>A padding entry (<c>Index #x0</c>), not a variable.</summary>
    public bool IsGap => Index == 0;

    /// <summary>
    /// Tree path of the entry below its PDO node: a sub-item <c>Status__Error</c> is the node
    /// <c>Status^Error</c> (confirmed on an EL3174), a plain entry keeps its name (<c>Value</c>).
    /// </summary>
    public string TreeName => Name.Replace("__", "^", StringComparison.Ordinal);
}

/// <summary>One PDO of a terminal.</summary>
/// <param name="Direction"><c>I</c> for a TxPdo (inputs), <c>Q</c> for an RxPdo (outputs).</param>
/// <param name="Active">The PDO is assigned to a sync manager (<c>Sm</c> attribute set); an alternative
/// (excluded) PDO is not and has no variables in the tree.</param>
internal sealed record TerminalPdo(string Direction, int Index, string Name, bool Active, IReadOnlyList<PdoEntry> Entries);

/// <summary>
/// The PDOs of one EtherCAT terminal, parsed from its own <c>ProduceXml(false)</c>, and which of them
/// belong to a channel. Analog terminals name their channel node after the PDO
/// (<c>AI Standard Channel 1</c>, <c>AO Outputs Channel 1</c>), not <c>Channel 1</c>, and the channel
/// variables are not tree children of the terminal, so the linker reads the PDO list to build the leaf
/// path <c>&lt;terminal&gt;^&lt;PDO name&gt;^&lt;entry&gt;</c>. Pure, no COM.
/// <list type="bullet">
/// <item>Channel → PDO: the selected <c>AlternativeSmMapping</c> (<c>Sm/Pdo[@ChannelNo]</c>) when it
/// assigns PDOs of the requested direction; otherwise the active PDOs whose name contains
/// <c>Channel N</c> / <c>Kanal N</c> as whole words (so 1 never matches 10).</item>
/// <item>Excluded and inactive PDOs are never returned.</item>
/// </list>
/// </summary>
internal sealed class TerminalPdoMap
{
    private readonly Dictionary<(string Direction, int Channel), List<int>> _selectedChannelPdos;

    private TerminalPdoMap(IReadOnlyList<TerminalPdo> pdos, Dictionary<(string, int), List<int>> selectedChannelPdos)
    {
        Pdos = pdos;
        _selectedChannelPdos = selectedChannelPdos;
    }

    /// <summary>Every PDO of the terminal, active and alternative, in document order.</summary>
    public IReadOnlyList<TerminalPdo> Pdos { get; }

    /// <summary>
    /// Parses a terminal's ProduceXml. Returns null when the XML has no <c>ProcessData</c> with a
    /// TxPdo or RxPdo (not an EtherCAT terminal with process data) or cannot be parsed.
    /// </summary>
    public static TerminalPdoMap? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (XmlException) { return null; }

        XElement? processData = doc.Descendants("ProcessData").FirstOrDefault();
        if (processData is null) return null;

        var pdos = new List<TerminalPdo>();
        foreach (XElement pdo in processData.Elements())
        {
            string? direction = pdo.Name.LocalName switch { "TxPdo" => "I", "RxPdo" => "Q", _ => null };
            if (direction is null) continue;
            int? index = ParseNumber((string?)pdo.Element("Index"));
            string name = ((string?)pdo.Element("Name"))?.Trim() ?? string.Empty;
            if (index is null || name.Length == 0) continue;

            var entries = pdo.Elements("Entry")
                .Select(e => new PdoEntry(
                    ParseNumber((string?)e.Element("Index")) ?? 0,
                    ParseNumber((string?)e.Element("SubIndex")) ?? 0,
                    ParseNumber((string?)e.Element("BitLen")) ?? 0,
                    ((string?)e.Element("Name"))?.Trim() ?? string.Empty,
                    ((string?)e.Element("DataType"))?.Trim() ?? string.Empty))
                .ToList();
            bool active = !string.IsNullOrWhiteSpace((string?)pdo.Attribute("Sm"));
            pdos.Add(new TerminalPdo(direction, index.Value, name, active, entries));
        }
        if (pdos.Count == 0) return null;

        var selected = new Dictionary<(string, int), List<int>>();
        XElement? mapping = processData.Element("AlternativeSmMappings")?.Elements("AlternativeSmMapping")
            .FirstOrDefault(m => string.Equals((string?)m.Attribute("Selected"), "true", StringComparison.OrdinalIgnoreCase));
        if (mapping is not null)
        {
            foreach (XElement assigned in mapping.Elements("Sm").Elements("Pdo"))
            {
                int? channel = ParseNumber((string?)assigned.Attribute("ChannelNo"));
                int? index = ParseNumber(assigned.Value);
                if (channel is null || index is null) continue;
                TerminalPdo? pdo = pdos.FirstOrDefault(p => p.Index == index.Value);
                if (pdo is null) continue;
                var key = (pdo.Direction, channel.Value);
                if (!selected.TryGetValue(key, out var list)) selected[key] = list = [];
                if (!list.Contains(index.Value)) list.Add(index.Value);
            }
        }
        return new TerminalPdoMap(pdos, selected);
    }

    /// <summary>
    /// The PDOs that carry <paramref name="channel"/> in <paramref name="direction"/> (<c>I</c>/<c>Q</c>).
    /// Empty when the terminal names no such channel.
    /// </summary>
    public IReadOnlyList<TerminalPdo> ForChannel(int channel, string direction)
    {
        string dir = NormalizeDirection(direction);
        if (_selectedChannelPdos.Keys.Any(k => k.Direction == dir))
        {
            return _selectedChannelPdos.TryGetValue((dir, channel), out var indexes)
                ? Pdos.Where(p => p.Direction == dir && indexes.Contains(p.Index)).ToList()
                : [];
        }

        var byName = new Regex($@"\b(Channel|Kanal)\s+{channel.ToString(CultureInfo.InvariantCulture)}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Pdos.Where(p => p.Direction == dir && p.Active && byName.IsMatch(p.Name)).ToList();
    }

    /// <summary>The active PDOs of <paramref name="direction"/> (for error texts).</summary>
    public IReadOnlyList<TerminalPdo> Active(string direction)
    {
        string dir = NormalizeDirection(direction);
        return Pdos.Where(p => p.Direction == dir && p.Active).ToList();
    }

    /// <summary>
    /// Bit width of an IEC type for matching a PDO entry (<c>BOOL</c> 1, <c>INT</c> 16, …); an empty
    /// type is <c>BOOL</c>. Null for a type without a fixed scalar width (STRING, a struct, …).
    /// </summary>
    public static int? BitWidth(string? type)
    {
        string t = string.IsNullOrWhiteSpace(type) ? "BOOL" : type.Trim().ToUpperInvariant();
        return t switch
        {
            "BOOL" or "BIT" => 1,
            "BYTE" or "SINT" or "USINT" => 8,
            "INT" or "UINT" or "WORD" => 16,
            "DINT" or "UDINT" or "DWORD" or "REAL" => 32,
            "LINT" or "ULINT" or "LWORD" or "LREAL" => 64,
            _ => null,
        };
    }

    private static string NormalizeDirection(string direction) =>
        string.Equals(direction?.Trim(), "Q", StringComparison.OrdinalIgnoreCase) ? "Q" : "I";

    // "#x1a00" (hex) or "6656" (decimal).
    private static int? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) ? dec : null;
    }
}
