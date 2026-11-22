using System.Globalization;
using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- io-tasks ----
    /// <summary>Task paths in tree order (their XML lives in <see cref="TreeItemXml"/>, their children in <see cref="TreeChildren"/>).</summary>
    public List<string> TaskPaths { get; } = [];
    public List<string> PlcTaskLinks { get; } = [];
    public Exception? ListPlcTaskLinksThrows { get; set; }

    /// <summary>Exported .xti text per child path ("TIID^Device"); ExportTreeChild writes it to the file.</summary>
    public Dictionary<string, string> ExportXml { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every imported file's content, in order.</summary>
    public List<(string Parent, string Name, string? Before, bool Reconnect, string Content)> Imports { get; } = [];
    public Exception? ImportTreeChildThrows { get; set; }
    /// <summary>When set, ImportTreeChild throws only for the first call (the second, restoring one succeeds).</summary>
    public bool ImportThrowsOnce { get; set; }

    /// <summary>Builds the tree item XML of a new child (parentPath, name, subType, vInfo, newPath); null = a bare TreeItem.</summary>
    public Func<string, string, int, string?, string, string>? CreatedChildXml { get; set; }
    /// <summary>Name the "XAE" gives a new child when the requested one is ignored (e.g. "boxxx1"); null = as requested.</summary>
    public Func<string, string, int, string>? NewChildName { get; set; }
    public Exception? CreateTreeChildThrows { get; set; }

    public static string TaskXml(string path, int priority, int amsPort, uint cycle100ns, bool disabled = false) =>
        $"<TreeItem><ItemName>{path[(path.LastIndexOf('^') + 1)..]}</ItemName><PathName>{path}</PathName><ItemType>1</ItemType>" +
        $"<Disabled>{(disabled ? "true" : "false")}</Disabled><TaskDef><Priority>{priority}</Priority><AmsPort>{amsPort}</AmsPort>" +
        $"<CycleTime>{cycle100ns}</CycleTime><AutoStart>true</AutoStart></TaskDef></TreeItem>";

    /// <summary>Adds a task (path, XML, children with or without Image) to the fake tree.</summary>
    public void AddTask(string path, int priority, int amsPort, uint cycle100ns, bool image, bool disabled = false)
    {
        TaskPaths.Add(path);
        TreeItemXml[path] = TaskXml(path, priority, amsPort, cycle100ns, disabled);
        TreeChildren[path] = image
            ? [new TreeChildInfo("Image", 3), new TreeChildInfo("Inputs", 8), new TreeChildInfo("Outputs", 8)]
            : [];
    }

    /// <summary>A ConsumeXml applier for TaskDef payloads: copies Priority/AmsPort/CycleTime into the stored XML.</summary>
    public static string TaskDefApplier(string path, string current, string payload)
    {
        var doc = XElement.Parse(current);
        var def = doc.Element("TaskDef");
        var input = XElement.Parse(payload).Element("TaskDef");
        if (def is null || input is null) return current;
        foreach (var e in input.Elements()) def.SetElementValue(e.Name, e.Value);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public IReadOnlyList<TreeChildInfo> ListAllTreeChildren(string treePath)
    {
        CallLog.Add($"ListAllTreeChildren({treePath})");
        return TreeChildren.TryGetValue(treePath, out var list)
            ? list.ToList()
            : throw new InvalidOperationException($"Tree path not found: {treePath}");
    }

    public IReadOnlyList<string> ListTaskPaths()
    {
        CallLog.Add("ListTaskPaths()");
        return TaskPaths.ToList();
    }

    public IReadOnlyList<string> ListPlcTaskLinks()
    {
        CallLog.Add("ListPlcTaskLinks()");
        if (ListPlcTaskLinksThrows is not null) throw ListPlcTaskLinksThrows;
        return PlcTaskLinks.ToList();
    }

    public string CreateImageTask(string name)
    {
        CallLog.Add($"CreateImageTask({name})");
        string path = $"TIRT^{name}";
        AddTask(path, 1, 301, 100000, image: true);
        return path;
    }

    public string CreateTreeChild(string parentPath, string name, int subType, string? vInfo)
    {
        CallLog.Add($"CreateTreeChild({parentPath},{name},{subType},{vInfo})");
        if (CreateTreeChildThrows is not null) throw CreateTreeChildThrows;
        string actual = NewChildName?.Invoke(parentPath, name, subType) ?? name;
        string path = $"{parentPath}^{actual}";
        if (!TreeChildren.TryGetValue(parentPath, out var siblings)) TreeChildren[parentPath] = siblings = [];
        siblings.Add(new TreeChildInfo(actual, ItemTypeForSubType(subType)));
        TreeItemXml[path] = CreatedChildXml?.Invoke(parentPath, name, subType, vInfo, path)
            ?? $"<TreeItem><ItemName>{actual}</ItemName><PathName>{path}</PathName><ItemSubType>{subType}</ItemSubType></TreeItem>";
        TreeChildren.TryAdd(path, []);
        return path;
    }

    public void ExportTreeChild(string parentPath, string childName, string filePath)
    {
        CallLog.Add($"ExportTreeChild({parentPath},{childName})");
        string key = $"{parentPath}^{childName}";
        if (!ExportXml.TryGetValue(key, out var xml))
            throw new InvalidOperationException($"ExportChild('{childName}') on '{parentPath}' failed: not found");
        File.WriteAllText(filePath, xml);
    }

    public void DeleteTreeChild(string parentPath, string childName)
    {
        CallLog.Add($"DeleteTreeChild({parentPath},{childName})");
        if (TreeChildren.TryGetValue(parentPath, out var siblings))
            siblings.RemoveAll(c => string.Equals(c.Name, childName, StringComparison.OrdinalIgnoreCase));
        ExportXml.Remove($"{parentPath}^{childName}");
    }

    public string ImportTreeChild(string parentPath, string filePath, string? beforeName, bool reconnect, string name)
    {
        CallLog.Add($"ImportTreeChild({parentPath},{Path.GetFileName(filePath)},{beforeName},{reconnect},{name})");
        if (ImportTreeChildThrows is not null)
        {
            var ex = ImportTreeChildThrows;
            if (ImportThrowsOnce) ImportTreeChildThrows = null;
            throw ex;
        }
        string content = File.ReadAllText(filePath);
        Imports.Add((parentPath, name, beforeName, reconnect, content));
        if (!TreeChildren.TryGetValue(parentPath, out var siblings)) TreeChildren[parentPath] = siblings = [];
        int at = beforeName is null ? -1 : siblings.FindIndex(c => string.Equals(c.Name, beforeName, StringComparison.OrdinalIgnoreCase));
        var info = new TreeChildInfo(name, 2);
        if (at < 0) siblings.Add(info); else siblings.Insert(at, info);
        ExportXml[$"{parentPath}^{name}"] = content;
        return $"{parentPath}^{name}";
    }

    private static int ItemTypeForSubType(int subType) => subType switch
    {
        ProfinetXml.DeviceSubType => ProfinetXml.DeviceItemType,
        ProfinetXml.BoxSubType => ProfinetXml.BoxItemType,
        _ => subType < 1000 && subType > 0 ? ProfinetXml.ModuleItemType : 0,
    };

    internal static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
}
