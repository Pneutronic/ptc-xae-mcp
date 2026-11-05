using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    /// <summary>The PLC project's ProjectInfo fields, in the order the XAE lists them.</summary>
    public List<KeyValuePair<string, string>> ProjectInfo { get; } =
    [
        new("Company", ""), new("Title", ""), new("Version", ""), new("LibraryCategories", ""), new("DefaultNamespace", ""),
        new("Author", ""), new("Description", ""), new("Released", ""), new("DocFormat", ""),
    ];

    public Exception? ConsumePlcProjectXmlThrows { get; set; }
    /// <summary>Fields the fake XAE silently ignores on ConsumeXml (to test the read-back).</summary>
    public HashSet<string> ProjectInfoIgnored { get; } = new(StringComparer.Ordinal);
    public string? LastConsumedPlcProjectXml { get; private set; }

    public string ReadPlcProjectXml(string? plcProjectName)
    {
        CallLog.Add($"ReadPlcProjectXml({plcProjectName})");
        var info = new XElement("ProjectInfo", ProjectInfo.Select(f => new XElement(f.Key, f.Value)));
        return new XElement("TreeItem", new XElement("IECProjectDef", info, new XElement("CompilerSettings"))).ToString();
    }

    public void ConsumePlcProjectXml(string? plcProjectName, string xml)
    {
        CallLog.Add($"ConsumePlcProjectXml({plcProjectName})");
        if (ConsumePlcProjectXmlThrows is not null) throw ConsumePlcProjectXmlThrows;
        LastConsumedPlcProjectXml = xml;
        foreach (var e in XElement.Parse(xml).Descendants("ProjectInfo").Elements())
        {
            if (ProjectInfoIgnored.Contains(e.Name.LocalName)) continue;
            int i = ProjectInfo.FindIndex(f => f.Key == e.Name.LocalName);
            if (i >= 0) ProjectInfo[i] = new(e.Name.LocalName, e.Value);
        }
    }
}
