using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// One real-time task as its ProduceXml shows it: under TIRT (PLC and I/O tasks) or TINC (NC SAF and SVB). Values the XML does
/// not carry are null. <see cref="CycleTime100ns"/> is in 100 ns units like <c>TaskDef/CycleTime</c>.
/// </summary>
internal sealed record TaskSettings(
    string Name,
    string Path,
    int? Priority,
    int? AmsPort,
    uint? CycleTime100ns,
    bool? AutoStart,
    bool? Disabled);

/// <summary>What a task is used for, as the task list reports it.</summary>
internal static class TaskKind
{
    public const string Plc = "plc";
    public const string Io = "io";
    public const string Nc = "nc";
    public const string Other = "other";

    /// <summary>
    /// nc for every task under TINC; plc when a PLC task reference links it; io for a TIRT task with process image that no
    /// PLC calls; other for the rest (a TIRT task without image and without a PLC).
    /// </summary>
    public static string Of(string path, bool hasImage, bool plcLinked)
    {
        if (path.StartsWith("TINC^", StringComparison.OrdinalIgnoreCase)) return Nc;
        if (plcLinked) return Plc;
        return hasImage ? Io : Other;
    }
}

/// <summary>
/// The <c>&lt;TreeItem&gt;&lt;TaskDef&gt;</c> part of a task's ProduceXml/ConsumeXml: Priority, AmsPort, CycleTime (100 ns), AutoStart,
/// plus <c>&lt;TreeItem&gt;&lt;Disabled&gt;</c>. Element names as the XAE 3.1.4026 writes them for TIRT and TINC tasks alike.
/// </summary>
internal static class TaskDefXml
{
    /// <summary>The settings of the task in <paramref name="treeItemXml"/>, or null when the XML has no TaskDef (not a task).</summary>
    public static TaskSettings? Parse(string? treeItemXml, string fallbackPath = "")
    {
        if (string.IsNullOrWhiteSpace(treeItemXml)) return null;
        XElement root;
        try { root = XElement.Parse(treeItemXml); }
        catch (XmlException) { return null; }
        var def = root.Descendants("TaskDef").FirstOrDefault();
        if (def is null) return null;

        string name = ((string?)root.Element("ItemName"))?.Trim() ?? string.Empty;
        string path = ((string?)root.Element("PathName"))?.Trim() ?? fallbackPath;
        if (name.Length == 0)
        {
            int i = path.LastIndexOf('^');
            name = i < 0 ? path : path[(i + 1)..];
        }
        return new TaskSettings(
            name,
            path,
            ParseInt(def.Element("Priority")),
            ParseInt(def.Element("AmsPort")),
            uint.TryParse((string?)def.Element("CycleTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint c) ? c : null,
            ParseBool(def.Element("AutoStart")),
            ParseBool(root.Element("Disabled")));
    }

    /// <summary>A ConsumeXml payload that sets only the given TaskDef values (null = left as is); null when none is given.</summary>
    public static string? Build(int? priority, int? amsPort, uint? cycleTime100ns)
    {
        if (priority is null && amsPort is null && cycleTime100ns is null) return null;
        var def = new XElement("TaskDef");
        if (priority is int p) def.Add(new XElement("Priority", p.ToString(CultureInfo.InvariantCulture)));
        if (amsPort is int a) def.Add(new XElement("AmsPort", a.ToString(CultureInfo.InvariantCulture)));
        if (cycleTime100ns is uint t) def.Add(new XElement("CycleTime", t.ToString(CultureInfo.InvariantCulture)));
        return new XElement("TreeItem", def).ToString(SaveOptions.DisableFormatting);
    }

    private static int? ParseInt(XElement? e) =>
        int.TryParse((string?)e, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static bool? ParseBool(XElement? e)
    {
        string? s = ((string?)e)?.Trim();
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
        return null;
    }
}
