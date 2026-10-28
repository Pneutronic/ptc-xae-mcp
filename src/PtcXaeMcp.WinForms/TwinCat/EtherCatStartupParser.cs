using System.Buffers.Binary;
using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Reads the complete Startup list of an EtherCAT box (the Startup tab: CoE, SoE, EoE and FoE mailbox commands) out of
/// the node's ProduceXml(false). The saved .xti holds the SoE/EoE entries as binary blobs, so only ProduceXml is read.
/// The ESC register commands of <c>Slave/InitCmds</c> are not part of the Startup tab and are ignored.
/// </summary>
internal static class EtherCatStartupParser
{
    private const string SlaveRoot = "//TreeItem/EtherCAT/Slave";

    /// <summary>The box with its (filtered) entries, or null when the XML is no EtherCAT slave.</summary>
    public static EtherCatStartupBox? Parse(
        string treePath, string name, XmlDocument doc, bool includeFixed = true, string? protocol = null)
    {
        var info = doc.SelectSingleNode($"{SlaveRoot}/Info");
        if (info is null) return null;

        bool Wants(string p) => protocol is null || protocol == p;

        var entries = new List<EtherCatStartupEntry>();
        if (Wants(EtherCatStartupProtocol.CoE))
        {
            entries.AddRange(CoeMailbox(doc));
            entries.AddRange(CoeOperationMode(doc));
        }
        if (Wants(EtherCatStartupProtocol.SoE)) entries.AddRange(Soe(doc));
        if (Wants(EtherCatStartupProtocol.EoE)) entries.AddRange(Generic(doc, "EoE", EtherCatStartupProtocol.EoE));
        if (Wants(EtherCatStartupProtocol.FoE)) entries.AddRange(Generic(doc, "FoE", EtherCatStartupProtocol.FoE));
        if (!includeFixed) entries.RemoveAll(e => e.Fixed);

        long? phys = EtherCatProjection.ParseNumber(info["PhysAddr"]?.InnerText);
        string? linkedAxis = Text(doc.SelectSingleNode("//TreeItem/EtherCAT/LinkedAxis"));
        int? soeChannels = EtherCatProjection.ParseNumber(doc.SelectSingleNode("//TreeItem/EtherCAT/SoeChannels")?.InnerText) is { } c
            ? (int)c : null;

        return new EtherCatStartupBox(treePath, name, SlaveType(doc, info), phys is { } p ? (int)p : null,
            linkedAxis, soeChannels, Wants(EtherCatStartupProtocol.EoE) ? VirtualPort(doc) : null, entries);
    }

    // Same name rule as the CoE startup/dictionary tools: ItemSubTypeName, else ProductRevision, without the "-0000-0017" tail.
    private static string SlaveType(XmlDocument doc, XmlNode info)
    {
        string type = doc.SelectSingleNode("//TreeItem/ItemSubTypeName")?.InnerText?.Trim()
                      ?? info["ProductRevision"]?.InnerText?.Trim()
                      ?? "Unknown";
        return type.Contains('-') ? type.Split('-')[0].Trim() : type;
    }

    private static string? Text(XmlNode? node)
    {
        string? text = node?.InnerText?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static List<string> Transitions(XmlNode cmd) =>
        cmd.SelectNodes("Transition")!.Cast<XmlNode>()
            .Select(t => t.InnerText.Trim()).Where(t => t.Length > 0).ToList();

    private static int? Int(XmlNode? node) =>
        EtherCatProjection.ParseNumber(node?.InnerText) is { } n and >= 0 and <= int.MaxValue ? (int)n : null;

    /// <summary>Data as upper-case hex without blanks, as written in the XML ("0800 0800" or "08000800").</summary>
    private static string DataHex(XmlNode cmd) =>
        string.Concat((cmd["Data"]?.InnerText ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch))).ToUpperInvariant();

    private static byte[]? Bytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private static IEnumerable<XmlElement> InitCmds(XmlDocument doc, string xpath) =>
        doc.SelectNodes(xpath)!.OfType<XmlElement>();

    private static IEnumerable<EtherCatStartupEntry> CoeMailbox(XmlDocument doc)
    {
        foreach (var cmd in InitCmds(doc, $"{SlaveRoot}/Mailbox/CoE/InitCmds/InitCmd"))
        {
            string? index = CoeStartupParser.NormalizeIndexToHex(cmd["Index"]?.InnerText?.Trim() ?? "");
            if (index is null) continue;
            yield return new EtherCatStartupEntry(
                EtherCatStartupProtocol.CoE, cmd.GetAttribute("Fixed") == "true", Transitions(cmd), Text(cmd["Comment"]),
                Int(cmd["Timeout"]), DataHex(cmd),
                Index: index, SubIndex: cmd["SubIndex"]?.InnerText?.Trim() ?? "0",
                CompleteAccess: cmd["Ccs"]?.InnerText?.Trim() == "1", Source: "mailbox");
        }
    }

    // The operation-mode settings of the selected SM mapping; DefaultSettings are the ESI defaults, not project entries.
    private static IEnumerable<EtherCatStartupEntry> CoeOperationMode(XmlDocument doc)
    {
        foreach (var cmd in InitCmds(doc,
                     $"{SlaveRoot}/ProcessData/AlternativeSmMappings/AlternativeSmMapping[@Selected='true']/OperationMode/InitCmds"))
        {
            if (cmd.GetAttribute("DefaultSettings") == "1") continue;
            string? index = CoeStartupParser.NormalizeIndexToHex(cmd["Index"]?.InnerText?.Trim() ?? "");
            if (index is null) continue;
            yield return new EtherCatStartupEntry(
                EtherCatStartupProtocol.CoE, false, Transitions(cmd), null, Int(cmd["Timeout"]), DataHex(cmd),
                Index: index, SubIndex: cmd["SubIndex"]?.InnerText?.Trim() ?? "1",
                CompleteAccess: cmd.GetAttribute("CompleteAccess") == "1", Source: "operationMode");
        }
    }

    private static IEnumerable<EtherCatStartupEntry> Generic(XmlDocument doc, string mailbox, string protocol)
    {
        foreach (var cmd in InitCmds(doc, $"{SlaveRoot}/Mailbox/{mailbox}/InitCmds/InitCmd"))
            yield return new EtherCatStartupEntry(
                protocol, cmd.GetAttribute("Fixed") == "true", Transitions(cmd), Text(cmd["Comment"]),
                Int(cmd["Timeout"]), DataHex(cmd));
    }

    private static IEnumerable<EtherCatStartupEntry> Soe(XmlDocument doc)
    {
        foreach (var cmd in InitCmds(doc, $"{SlaveRoot}/Mailbox/SoE/InitCmds/InitCmd"))
        {
            string hex = DataHex(cmd);
            byte[]? data = Bytes(hex);
            long? idnValue = EtherCatProjection.ParseNumber(cmd["IDN"]?.InnerText);
            int? idnRaw = idnValue is >= 0 and <= 0xFFFF ? (int)idnValue.Value : null;
            int? elements = Int(cmd["Elements"]);

            SoeListHeader? list = data is null ? null : ListHeader(data);
            yield return new EtherCatStartupEntry(
                EtherCatStartupProtocol.SoE, cmd.GetAttribute("Fixed") == "true", Transitions(cmd), Text(cmd["Comment"]),
                Int(cmd["Timeout"]), hex,
                Idn: idnRaw is null ? null : SoeIdn.Format(idnRaw.Value), IdnRaw: idnRaw,
                ParameterSet: idnRaw is null ? null : SoeIdn.ParameterSet(idnRaw.Value),
                DriveNo: Int(cmd["DriveNo"]), OpCode: Int(cmd["OpCode"]),
                Elements: elements is null ? null : SoeElement.Name(elements.Value), Attribute: Int(cmd["Attribute"]),
                Value: data is null ? null : Scalar(data), List: list,
                IdnList: data is null || list is null || !IsIdnListParameter(idnRaw) ? null : IdnList(data, list));
        }
    }

    // S-0-0016 (AT list) and S-0-0024 (MDT list): their data are IDNs, UINT16 each.
    private static bool IsIdnListParameter(int? idnRaw) => idnRaw is 16 or 24;

    /// <summary>Unsigned little endian value of 2, 4 or 8 data bytes; other lengths are no scalar.</summary>
    private static ulong? Scalar(byte[] data) => data.Length switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
        8 => BinaryPrimitives.ReadUInt64LittleEndian(data),
        _ => null,
    };

    // A SoE list starts with UINT16 actual length and UINT16 maximum length; it is reported only when the actual length
    // matches the data that follow (a plain 4 byte value that happens to fit is reported too, see the tool description).
    private static SoeListHeader? ListHeader(byte[] data)
    {
        if (data.Length < 4) return null;
        int current = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int max = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        return current == data.Length - 4 && max >= current ? new SoeListHeader(current, max) : null;
    }

    private static IReadOnlyList<string>? IdnList(byte[] data, SoeListHeader list)
    {
        if (list.CurrentLength % 2 != 0) return null;
        var idns = new List<string>(list.CurrentLength / 2);
        for (int i = 0; i < list.CurrentLength; i += 2)
            idns.Add(SoeIdn.Format(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4 + i))));
        return idns;
    }

    private static EtherCatEoeVirtualPort? VirtualPort(XmlDocument doc)
    {
        var vp = doc.SelectSingleNode($"{SlaveRoot}/Mailbox/EoE/VirtualPort");
        if (vp is null) return null;
        var ip = vp.SelectSingleNode("IpPort");
        return new EtherCatEoeVirtualPort(
            Text(vp["MacAddr"]), Text(ip?["IpAddr"]), Text(ip?["Subnet"]), Text(ip?["Gateway"]),
            Text(ip?["DnsAddr"]), Text(ip?["DnsName"]));
    }
}

/// <summary>Walks the boxes below EtherCAT masters and parses every box's Startup list.</summary>
internal static class EtherCatStartupReader
{
    /// <summary>
    /// All boxes with at least one entry (after the filters), each box once. Nodes whose XML is missing, malformed or no
    /// EtherCAT slave are skipped; <see cref="EtherCatStartupResult.SlavesScanned"/> counts the boxes that were looked at.
    /// </summary>
    public static EtherCatStartupResult ReadAll(
        IEnumerable<EtherCatProjection.IProjectionNode> masters, bool includeFixed, string? protocol, int maxDepth = 24)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boxes = new List<EtherCatStartupBox>();
        int scanned = 0;

        void Walk(EtherCatProjection.IProjectionNode node, int depth)
        {
            if (depth >= maxDepth) return;
            foreach (var child in node.Children)
            {
                if (!seen.Add(child.Path)) continue;
                var box = ParseNode(child, includeFixed, protocol);
                if (box is not null)
                {
                    scanned++;
                    if (box.Entries.Count > 0) boxes.Add(box);
                }
                Walk(child, depth + 1);
            }
        }

        foreach (var master in masters) Walk(master, 0);
        return new EtherCatStartupResult(boxes, scanned);
    }

    /// <summary>The box of one node (also one without entries), or null when the node is no readable EtherCAT slave.</summary>
    public static EtherCatStartupBox? ParseNode(EtherCatProjection.IProjectionNode node, bool includeFixed, string? protocol)
    {
        string xml = node.Xml;
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var doc = new XmlDocument();
        try { doc.LoadXml(xml); }
        catch (XmlException) { return null; }
        return EtherCatStartupParser.Parse(node.Path, node.Name, doc, includeFixed, protocol);
    }
}
