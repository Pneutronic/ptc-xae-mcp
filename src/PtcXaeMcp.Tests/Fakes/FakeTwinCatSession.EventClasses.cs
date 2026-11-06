using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- event-classes ----
    /// <summary>The type system: DataType XML per type (event classes and others).</summary>
    public List<string> TypeSystem { get; } = new();

    /// <summary>The PLC projects (name, .plcproj path); their pins are derived from <see cref="Files"/> like the XAE does on load.</summary>
    public List<(string PlcName, string PlcprojPath)> EventPlcs { get; } = new();

    /// <summary>Files on "disk" (path → text).</summary>
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? CreateTypeSystemTypeThrows { get; set; }
    public Exception? ReplaceTypeSystemTypeThrows { get; set; }
    public Exception? EditFilesThrows { get; set; }
    /// <summary>After the reopen the XAE shows no pin node (simulates a file the XAE did not take).</summary>
    public bool ReopenShowsNoPins { get; set; }
    /// <summary>The type system drops PersistentType on CreateType (what the read-back then shows).</summary>
    public bool CreateDropsPersistent { get; set; }

    private int _guidCounter;

    public IReadOnlyList<string> ReadEventClassXml()
    {
        CallLog.Add("ReadEventClassXml()");
        return TypeSystem.Where(x => x.Contains("<EventId>", StringComparison.Ordinal))
            .Select(x => "<?xml version=\"1.0\"?><DataTypes>" + x + "</DataTypes>").ToList();
    }

    public string CreateTypeSystemType(string name, string dataTypeXml)
    {
        CallLog.Add($"CreateTypeSystemType({name})");
        if (CreateTypeSystemTypeThrows is not null) throw CreateTypeSystemTypeThrows;
        var type = System.Xml.Linq.XElement.Parse(dataTypeXml);
        var n = type.Element("Name")!;
        if (n.Attribute("GUID") is null)
            n.SetAttributeValue("GUID", $"{{00000000-0000-0000-0000-{++_guidCounter:D12}}}");
        if (CreateDropsPersistent) n.Attribute("PersistentType")?.Remove();
        TypeSystem.Add(type.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        return ReadBack(name);
    }

    public string ReplaceTypeSystemType(string guid, string name, string dataTypeXml, string previousXml)
    {
        CallLog.Add($"ReplaceTypeSystemType({guid},{name})");
        if (ReplaceTypeSystemTypeThrows is not null) throw ReplaceTypeSystemTypeThrows;
        int i = TypeSystem.FindIndex(x => x.Contains(guid, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new InvalidOperationException($"DeleteType({guid}): not found");
        TypeSystem.RemoveAt(i);
        TypeSystem.Add(dataTypeXml);
        // The XAE drops the pin of a deleted type from ExternalTypes.tmc when it saves next (seen live).
        _dropPinsOnSave.Add(name);
        return ReadBack(name);
    }

    private readonly List<string> _dropPinsOnSave = new();

    private string ReadBack(string name) =>
        "<?xml version=\"1.0\"?><DataTypes>" +
        TypeSystem.Last(x => EventClassXml.Parse(x)?.Name == name || x.Contains($">{name}</Name>", StringComparison.Ordinal)) +
        "</DataTypes>";

    public IReadOnlyList<PlcExternalTypesInfo> ReadPlcExternalTypes()
    {
        CallLog.Add("ReadPlcExternalTypes()");
        return EventPlcs.Select(p =>
        {
            string tmcPath = Path.Combine(Path.GetDirectoryName(p.PlcprojPath)!, EventClassXml.ExternalTypesFile);
            string? tmc = Files.GetValueOrDefault(tmcPath);
            bool inProject = Files.TryGetValue(p.PlcprojPath, out var proj) && proj.Contains("ExternalTypes.tmc", StringComparison.Ordinal);
            var nodes = inProject && !ReopenShowsNoPins
                ? EventClassXml.EventClassesInTmc(tmc).Select(c => "Global.ST_" + c.Name).ToList()
                : new List<string>();
            return new PlcExternalTypesInfo(p.PlcName, p.PlcprojPath, nodes, tmc);
        }).ToList();
    }

    public void EditFilesWithSolutionClosed(IReadOnlyList<SolutionFileEdit> edits)
    {
        CallLog.Add($"EditFilesWithSolutionClosed({string.Join(",", edits.Select(e => Path.GetFileName(e.Path)))})");
        // The save before the close: the XAE writes ExternalTypes.tmc without the dropped pins.
        foreach (string name in _dropPinsOnSave)
            foreach (var key in Files.Keys.Where(k => k.EndsWith(EventClassXml.ExternalTypesFile, StringComparison.OrdinalIgnoreCase)).ToList())
                if (EventClassXml.EventClassesInTmc(Files[key]).Any(c => c.Name == name))
                    Files[key] = "<?xml version=\"1.0\" encoding=\"utf-8\"?>";
        _dropPinsOnSave.Clear();
        if (EditFilesThrows is not null) throw EditFilesThrows;
        foreach (var edit in edits)
            Files[edit.Path] = edit.Transform(Files.GetValueOrDefault(edit.Path));
    }
}
