using System.Globalization;
using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Parses the user-configured Startup SDO-download entries from an EtherCAT slave's
/// ProduceXml(false). "User-configured" means: not Fixed (system PDO bookkeeping) and
/// not DefaultSettings (ESI-derived defaults written for every slave of this type).
/// </summary>
internal static class CoeStartupParser
{
    public static CoeStartupList Parse(string treePath, string slaveType, XmlDocument produceXml)
    {
        var entries = new List<CoeStartupEntry>();

        // Source 1: <Mailbox><CoE><InitCmds><InitCmd> NOT Fixed="true"
        var mailboxInitCmds = produceXml.SelectNodes(
            "//TreeItem/EtherCAT/Slave/Mailbox/CoE/InitCmds/InitCmd");
        if (mailboxInitCmds is not null)
        {
            foreach (XmlNode cmd in mailboxInitCmds)
            {
                if (cmd is not XmlElement elem) continue;
                if (elem.GetAttribute("Fixed") == "true") continue;

                string indexStr = cmd["Index"]?.InnerText?.Trim() ?? "";
                string? indexHex = NormalizeIndexToHex(indexStr);
                if (indexHex is null) continue;

                string subIndex = cmd["SubIndex"]?.InnerText?.Trim() ?? "0";
                string data = cmd["Data"]?.InnerText?.Trim() ?? "";
                string transition = GetFirstTransition(cmd);
                string? comment = ExtractComment(cmd);
                bool complete = cmd["Ccs"]?.InnerText?.Trim() == "1";

                entries.Add(new CoeStartupEntry(
                    Index: indexHex,
                    SubIndex: subIndex,
                    ValueHex: data,
                    BitLength: data.Length * 4,
                    Transition: transition,
                    Comment: comment,
                    Complete: complete));
            }
        }

        // Source 2: <OperationMode><InitCmds> NOT DefaultSettings="1"
        // Under the currently selected AlternativeSmMapping
        var opModeInitCmds = produceXml.SelectNodes(
            "//TreeItem/EtherCAT/Slave/ProcessData/AlternativeSmMappings" +
            "/AlternativeSmMapping[@Selected='true']/OperationMode/InitCmds");
        if (opModeInitCmds is not null)
        {
            foreach (XmlNode icGroup in opModeInitCmds)
            {
                if (icGroup is not XmlElement elem) continue;
                if (elem.GetAttribute("DefaultSettings") == "1") continue;

                string indexStr = icGroup["Index"]?.InnerText?.Trim() ?? "";
                string? indexHex = NormalizeIndexToHex(indexStr);
                if (indexHex is null) continue;

                string subIndex = icGroup["SubIndex"]?.InnerText?.Trim() ?? "1";
                string data = icGroup["Data"]?.InnerText?.Trim() ?? "";
                string transition = icGroup["Transition"]?.InnerText?.Trim() ?? "";
                bool complete = elem.GetAttribute("CompleteAccess") == "1";

                entries.Add(new CoeStartupEntry(
                    Index: indexHex,
                    SubIndex: subIndex,
                    ValueHex: data,
                    BitLength: data.Length * 4,
                    Transition: transition,
                    Comment: null,
                    Complete: complete));
            }
        }

        return new CoeStartupList(treePath, slaveType, entries.Count, entries);
    }

    internal static string? NormalizeIndexToHex(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        int val;
        if (s.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(s[2..], NumberStyles.HexNumber, null, out val)) return null;
        }
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(s[2..], NumberStyles.HexNumber, null, out val)) return null;
        }
        else
        {
            if (!int.TryParse(s, out val)) return null;
        }
        return val.ToString("X4", CultureInfo.InvariantCulture);
    }

    private static string GetFirstTransition(XmlNode cmd)
    {
        var nodes = cmd.SelectNodes("Transition");
        if (nodes is null || nodes.Count == 0) return "";
        return nodes[0]?.InnerText?.Trim() ?? "";
    }

    private static string? ExtractComment(XmlNode cmd)
    {
        string? text = cmd["Comment"]?.InnerText?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
