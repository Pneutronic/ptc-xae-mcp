using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A device access point (DAP) of a GSDML file: its 1-based position, ident number, name and the modules it accepts.</summary>
internal sealed record GsdmlDap(int Number, uint IdentNumber, string Id, string Name, IReadOnlySet<string> UseableModuleIds, int MaxSlot);

/// <summary>
/// A module of a GSDML ModuleList. <see cref="Index"/> is its 1-based position in the ModuleList — the sub type
/// <c>API.CreateChild("", Index, "", slot)</c> expects. <see cref="Names"/> holds the name in every language of the file.
/// </summary>
internal sealed record GsdmlModule(
    int Index, string Id, uint IdentNumber, string Name, IReadOnlyList<string> Names, string? Category,
    int InputBytes, int OutputBytes, bool Modular);

/// <summary>
/// The parts of a PROFINET GSDML file the Profinet-device tools need: the DAPs and the ModuleList. Texts come from the
/// ExternalTextList (primary language first, then every other language for matching).
/// </summary>
internal sealed class GsdmlCatalog
{
    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public required IReadOnlyList<GsdmlDap> Daps { get; init; }
    public required IReadOnlyList<GsdmlModule> Modules { get; init; }

    /// <summary>The DAP with <paramref name="number"/> (1-based) or, for null, the last one (the newest firmware version).</summary>
    public GsdmlDap? Dap(int? number) =>
        number is int n ? Daps.FirstOrDefault(d => d.Number == n) : (Daps.Count > 0 ? Daps[^1] : null);

    public GsdmlDap? DapByIdent(uint ident) => Daps.FirstOrDefault(d => d.IdentNumber == ident);

    /// <summary>Modules the DAP accepts, in ModuleList order.</summary>
    public IEnumerable<GsdmlModule> ModulesFor(GsdmlDap dap) => Modules.Where(m => dap.UseableModuleIds.Contains(m.Id));

    /// <summary>
    /// The module for <paramref name="key"/>: its GSDML ID (e.g. BYTE_I4), its ident number (0x706 or 1798) or its name in
    /// any language of the file, case-insensitive. Null when none matches.
    /// </summary>
    public GsdmlModule? Find(string key)
    {
        string k = key.Trim();
        var byId = Modules.FirstOrDefault(m => string.Equals(m.Id, k, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        if (TryParseNumber(k, out uint ident))
        {
            var byIdent = Modules.FirstOrDefault(m => m.IdentNumber == ident);
            if (byIdent is not null) return byIdent;
        }
        return Modules.FirstOrDefault(m => m.Names.Any(n => string.Equals(n, k, StringComparison.OrdinalIgnoreCase)));
    }

    public static GsdmlCatalog Load(string filePath) => Parse(File.ReadAllText(filePath), filePath);

    public static GsdmlCatalog Parse(string xml, string filePath)
    {
        XElement root;
        try { root = XElement.Parse(xml); }
        catch (XmlException ex) { throw new InvalidOperationException($"GSDML file '{Path.GetFileName(filePath)}' is not valid XML: {ex.Message}", ex); }

        var texts = ReadTexts(root);
        string Text(XElement? nameElement)
        {
            string? id = (string?)nameElement?.Attribute("TextId");
            return id is not null && texts.TryGetValue(id, out var values) && values.Count > 0 ? values[0] : id ?? string.Empty;
        }
        IReadOnlyList<string> AllTexts(XElement? nameElement)
        {
            string? id = (string?)nameElement?.Attribute("TextId");
            return id is not null && texts.TryGetValue(id, out var values) ? values : [];
        }

        var daps = new List<GsdmlDap>();
        foreach (var dap in root.Descendants().Where(e => e.Name.LocalName == "DeviceAccessPointItem"))
        {
            var useable = dap.Elements().Where(e => e.Name.LocalName == "UseableModules")
                .SelectMany(u => u.Elements().Where(e => e.Name.LocalName == "ModuleItemRef"))
                .Select(r => (string?)r.Attribute("ModuleItemTarget") ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            daps.Add(new GsdmlDap(
                daps.Count + 1,
                TryParseNumber((string?)dap.Attribute("ModuleIdentNumber") ?? string.Empty, out uint ident) ? ident : 0,
                (string?)dap.Attribute("ID") ?? string.Empty,
                Text(Child(Child(dap, "ModuleInfo"), "Name")),
                useable,
                MaxOfRange((string?)dap.Attribute("PhysicalSlots"))));
        }

        var modules = new List<GsdmlModule>();
        var list = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ModuleList");
        foreach (var item in list?.Elements().Where(e => e.Name.LocalName == "ModuleItem") ?? [])
        {
            var info = Child(item, "ModuleInfo");
            var ioData = item.Descendants().Where(e => e.Name.LocalName == "IOData").ToList();
            modules.Add(new GsdmlModule(
                modules.Count + 1,
                (string?)item.Attribute("ID") ?? string.Empty,
                TryParseNumber((string?)item.Attribute("ModuleIdentNumber") ?? string.Empty, out uint ident) ? ident : 0,
                Text(Child(info, "Name")),
                AllTexts(Child(info, "Name")),
                (string?)info?.Attribute("CategoryRef"),
                ioData.Sum(d => Bytes(Child(d, "Input"))),
                ioData.Sum(d => Bytes(Child(d, "Output"))),
                item.Elements().Any(e => e.Name.LocalName == "UseableSubmodules")));
        }

        return new GsdmlCatalog { FilePath = filePath, Daps = daps, Modules = modules };
    }

    /// <summary>The GSDML files of the EL6631-0010 Profinet device under <paramref name="profinetDir"/>, newest (by the date in the name) last.</summary>
    public static IReadOnlyList<string> FindEl6631Files(string profinetDir)
    {
        if (!Directory.Exists(profinetDir)) return [];
        return Directory.GetFiles(profinetDir, "GSDML-*-beckhoff-EL6631-*.xml")
            .OrderBy(f => DateStamp(Path.GetFileNameWithoutExtension(f)), StringComparer.Ordinal)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // "GSDML-V2.44-beckhoff-EL6631-20240715" -> "20240715"
    private static string DateStamp(string name)
    {
        int i = name.LastIndexOf('-');
        return i < 0 ? name : name[(i + 1)..];
    }

    internal static bool TryParseNumber(string s, out uint value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    // Primary language first, then the other languages; one list per TextId.
    private static Dictionary<string, List<string>> ReadTexts(XElement root)
    {
        var texts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var list = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "ExternalTextList");
        if (list is null) return texts;
        var languages = list.Elements().Where(e => e.Name.LocalName == "PrimaryLanguage")
            .Concat(list.Elements().Where(e => e.Name.LocalName == "Language"));
        foreach (var language in languages)
        {
            foreach (var text in language.Elements().Where(e => e.Name.LocalName == "Text"))
            {
                string? id = (string?)text.Attribute("TextId");
                string? value = (string?)text.Attribute("Value");
                if (id is null || value is null) continue;
                if (!texts.TryGetValue(id, out var values)) texts[id] = values = [];
                if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
            }
        }
        return texts;
    }

    private static int Bytes(XElement? direction)
    {
        if (direction is null) return 0;
        int sum = 0;
        foreach (var item in direction.Elements().Where(e => e.Name.LocalName == "DataItem"))
        {
            if (int.TryParse((string?)item.Attribute("Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
            {
                sum += length;
                continue;
            }
            sum += ((string?)item.Attribute("DataType")) switch
            {
                "Integer8" or "Unsigned8" or "Boolean" => 1,
                "Integer16" or "Unsigned16" => 2,
                "Integer32" or "Unsigned32" or "Float32" => 4,
                "Integer64" or "Unsigned64" or "Float64" => 8,
                _ => 0,
            };
        }
        return sum;
    }

    private static int MaxOfRange(string? range)
    {
        // "0..255" or "0 1 2" -> 255 / 2
        if (string.IsNullOrWhiteSpace(range)) return 255;
        var numbers = range.Replace("..", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1)
            .Where(v => v >= 0).ToList();
        return numbers.Count == 0 ? 255 : numbers.Max();
    }
}

/// <summary>A module placed under the API node of a Profinet box: its tree path, slot and module ident number.</summary>
internal sealed record PlacedProfinetModule(string Name, string Path, int? Slot, uint? IdentNumber);

/// <summary>
/// ProduceXml and export (.xti) parts of the Profinet device (RT) on an EL6631-0010 and of I/O devices in general, as the
/// XAE 3.1.4026 writes them.
/// </summary>
internal static class ProfinetXml
{
    /// <summary>ItemSubType of the "Profinet I/O device EL6631-0010, EtherCAT" (TwinCAT as Profinet IO device).</summary>
    public const int DeviceSubType = 118;
    /// <summary>ItemSubType of the box under that device ("PROFINET I/O Device - EtherCAT Slave Klemme", from the GSDML).</summary>
    public const int BoxSubType = 9127;
    public const int BoxItemType = 5;
    public const int ApiItemType = 44;
    public const int ModuleItemType = 45;
    public const int DeviceItemType = 2;

    /// <summary>EtherCAT identity of the EL6631-0010: Beckhoff (2), product code 0x19E73052.</summary>
    public const uint Beckhoff = 2;
    public const uint El6631_0010ProductCode = 0x19E73052;

    /// <summary>The EtherCAT identity and ItemId of a box from its ProduceXml (<c>&lt;Info&gt;&lt;VendorId/ProductCode/ProductRevision&gt;</c>).</summary>
    public static (int? ItemId, uint? VendorId, uint? ProductCode, string? ProductRevision) ReadEtherCatBox(string? xml)
    {
        var root = TryParse(xml);
        if (root is null) return (null, null, null, null);
        int? itemId = int.TryParse((string?)root.Element("ItemId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ? id : null;
        var info = root.Descendants("Info").FirstOrDefault(i => i.Element("ProductCode") is not null);
        uint? vendor = GsdmlCatalog.TryParseNumber((string?)info?.Element("VendorId") ?? string.Empty, out uint v) ? v : null;
        uint? product = GsdmlCatalog.TryParseNumber((string?)info?.Element("ProductCode") ?? string.Empty, out uint p) ? p : null;
        return (itemId, vendor, product, ((string?)info?.Element("ProductRevision"))?.Trim());
    }

    /// <summary>ItemSubType and the bound EtherCAT box (<c>DeviceDef/AddressInfo/Ecat</c>) of an I/O device.</summary>
    public static (int? SubType, int? EtherCatDeviceId, string? EtherCatDeviceName) ReadDevice(string? xml)
    {
        var root = TryParse(xml);
        if (root is null) return (null, null, null);
        int? subType = int.TryParse((string?)root.Element("ItemSubType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : null;
        var ecat = root.Element("DeviceDef")?.Element("AddressInfo")?.Element("Ecat");
        int? id = int.TryParse((string?)ecat?.Element("EtherCATDeviceId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;
        string? name = ((string?)ecat?.Element("EtherCATDeviceName"))?.Trim();
        return (subType, id, string.IsNullOrEmpty(name) ? null : name);
    }

    /// <summary>ConsumeXml payload that binds an I/O device to the EtherCAT box with <paramref name="boxItemId"/>.</summary>
    public static string BindXml(int boxItemId) =>
        new XElement("TreeItem", new XElement("DeviceDef", new XElement("AddressInfo", new XElement("Ecat",
            new XElement("EtherCATDeviceId", boxItemId.ToString(CultureInfo.InvariantCulture)))))).ToString(SaveOptions.DisableFormatting);

    /// <summary>The GSDML file name from a Profinet box's comment ("GSDML Name: GSDML-…xml").</summary>
    public static string? ReadGsdmlName(string? boxXml)
    {
        var root = TryParse(boxXml);
        string comment = (string?)root?.Element("Comment") ?? string.Empty;
        foreach (string line in comment.Split('\n'))
        {
            string l = line.Trim();
            foreach (string prefix in new[] { "GSDML Name:", "GSDML:" })
                if (l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return l[prefix.Length..].Trim();
        }
        return null;
    }

    /// <summary>Slot and module ident number of a module item (<c>CPnIoModuleDef/SlotNr</c>, <c>ModuleIdentNr</c>).</summary>
    public static PlacedProfinetModule ReadModule(string? xml, string fallbackPath)
    {
        var root = TryParse(xml);
        var def = root?.Element("CPnIoModuleDef");
        string path = ((string?)root?.Element("PathName"))?.Trim() ?? fallbackPath;
        string name = ((string?)root?.Element("ItemName"))?.Trim() ?? path[(path.LastIndexOf('^') + 1)..];
        int? slot = GsdmlCatalog.TryParseNumber((string?)def?.Element("SlotNr") ?? string.Empty, out uint s) ? (int)s : null;
        uint? ident = GsdmlCatalog.TryParseNumber((string?)def?.Element("ModuleIdentNr") ?? string.Empty, out uint m) ? m : null;
        return new PlacedProfinetModule(name, path, slot, ident);
    }

    /// <summary>The ident number of the DAP (the module in slot 0) of an exported Profinet box (<c>UsedGsdmlModule</c>).</summary>
    public static uint? ReadDapIdent(string? exportXml)
    {
        var root = TryParse(exportXml);
        if (root is null) return null;
        foreach (var used in root.Descendants("UsedGsdmlModule"))
        {
            if (!used.Elements("Slot").Any(s => s.Value.Trim() == "0")) continue;
            if (GsdmlCatalog.TryParseNumber((string?)used.Attribute("ModuleIdentNumber") ?? string.Empty, out uint ident)) return ident;
        }
        return null;
    }

    private static XElement? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XElement.Parse(xml); }
        catch (XmlException) { return null; }
    }
}

/// <summary>
/// The sync task of an I/O device as its export (.xti) stores it: <c>&lt;Ethernet … SpecialTask="true" TaskPort="301"/&gt;</c> in
/// the device's protocol section (Profinet and other Ethernet-protocol devices). Without SpecialTask the device uses the
/// default ("via mapping"). The Automation Interface does not take this setting through ConsumeXml, so it is written by
/// export → patch → import.
/// </summary>
internal static class SyncTaskXml
{
    public sealed record State(bool Supported, bool Special, int? TaskPort);

    /// <summary>The sync task setting in an exported device, or Supported=false when the export has no device-level Ethernet section.</summary>
    public static State Read(string? exportXml)
    {
        var ethernet = DeviceEthernet(Parse(exportXml));
        if (ethernet is null) return new State(false, false, null);
        bool special = string.Equals((string?)ethernet.Attribute("SpecialTask"), "true", StringComparison.OrdinalIgnoreCase);
        int? port = int.TryParse((string?)ethernet.Attribute("TaskPort"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : null;
        return new State(true, special, special ? port : null);
    }

    /// <summary>
    /// The export with the sync task set to the task with <paramref name="taskPort"/>, or reset to the default for null.
    /// Throws when the export has no device-level Ethernet section.
    /// </summary>
    public static string Patch(string exportXml, int? taskPort)
    {
        var doc = XDocument.Parse(exportXml, LoadOptions.PreserveWhitespace);
        var ethernet = DeviceEthernet(doc.Root)
            ?? throw new InvalidOperationException("The exported device has no Ethernet section with a sync task setting.");
        if (taskPort is int port)
        {
            ethernet.SetAttributeValue("SpecialTask", "true");
            ethernet.SetAttributeValue("TaskPort", port.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            ethernet.SetAttributeValue("SpecialTask", null);
            ethernet.SetAttributeValue("TaskPort", null);
        }
        return (doc.Declaration?.ToString() ?? "<?xml version=\"1.0\"?>") + Environment.NewLine + doc.Root!.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XElement.Parse(xml); }
        catch (XmlException) { return null; }
    }

    // The <Ethernet> of the device itself (directly below <Device> or below its protocol element such as <Profinet>),
    // never one inside a <Box>.
    private static XElement? DeviceEthernet(XElement? root)
    {
        var device = root?.DescendantsAndSelf("Device").FirstOrDefault();
        if (device is null) return null;
        return device.Elements("Ethernet")
            .Concat(device.Elements().Where(e => e.Name.LocalName != "Box").SelectMany(e => e.Elements("Ethernet")))
            .FirstOrDefault();
    }
}
