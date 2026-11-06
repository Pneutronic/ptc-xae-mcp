using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>One real-time task under TIRT. Cycle time in 100 ns units and priority as read from the item; null when unreadable.</summary>
internal sealed record RealtimeTaskInfo(string Name, string Path, uint? CycleTime100ns, int? Priority);

/// <summary>One PLC task reference (ItemType 621) under a PLC project and the real-time task it is linked to (null = not readable).</summary>
internal sealed record PlcTaskReferenceInfo(string Name, string Path, string? LinkedTask);

/// <summary>
/// The PLC project a task is created for: its name under TIPC, the tree path of its project node (the parent of the PLC
/// task references), the task references it has and the names of its PROGRAM POUs.
/// </summary>
internal sealed record PlcTaskContext(
    string PlcName, string ProjectPath, IReadOnlyList<PlcTaskReferenceInfo> TaskReferences, IReadOnlyList<string> Programs);

/// <summary>
/// The TaskDef part of a real-time task's ProduceXml/ConsumeXml (<c>&lt;TreeItem&gt;&lt;TaskDef&gt;&lt;CycleTime&gt;</c>, 100 ns units).
/// The fallback when ITcTaskModule.CycleTime/Priority are not reachable through IDispatch.
/// </summary>
internal static class RealtimeTaskXml
{
    public static string BuildTimingXml(uint cycleTime100ns, int priority) =>
        "<TreeItem><TaskDef>" +
        $"<CycleTime>{cycleTime100ns.ToString(CultureInfo.InvariantCulture)}</CycleTime>" +
        $"<Priority>{priority.ToString(CultureInfo.InvariantCulture)}</Priority>" +
        "</TaskDef></TreeItem>";

    /// <summary>CycleTime and Priority from the TaskDef element; null for a value that is missing or not a number.</summary>
    public static (uint? CycleTime100ns, int? Priority) ReadTiming(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return (null, null);
        XElement? def;
        try { def = XDocument.Parse(xml).Descendants("TaskDef").FirstOrDefault(); }
        catch (XmlException) { return (null, null); }
        if (def is null) return (null, null);

        uint? cycle = uint.TryParse((string?)def.Element("CycleTime"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint c) ? c : null;
        int? priority = int.TryParse((string?)def.Element("Priority"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : null;
        return (cycle, priority);
    }
}
