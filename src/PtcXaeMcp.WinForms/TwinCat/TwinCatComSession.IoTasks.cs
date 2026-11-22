using System.Collections;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- io-tasks ----------------------------------------------------------------------------
//
// Real-time tasks, I/O devices and their tree items (Infosys TE1000, tc3_automationinterface):
//   * TIRT.CreateChild(name, 0)                        — a task with image (…/242740107); the XAE gives it AmsPort 301
//                                                         and priority 1 by itself, so callers set both afterwards
//   * TaskDef in ProduceXml/ConsumeXml                 — Priority, AmsPort, CycleTime (100 ns) for TIRT and TINC tasks
//   * CreateChild / ExportChild / DeleteChild / ImportChild on ITcSmTreeItem (…/242740107, …/21840417035)
// The NC tasks live under TINC: the SAF task is a child of TINC, its SVB task a child of the SAF task.
internal sealed partial class TwinCatComSession
{
    private const int ImageTaskSubType = 0; // "with image"
    private const int TaskItemType = 1;

    public IReadOnlyList<string> ListTaskPaths() => ComBusyRetry.Run<IReadOnlyList<string>>(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        return TaskItems(sysMan).Select(t => (string)PathOf(t)).ToList();
    }, OpenTimeout);

    public IReadOnlyList<string> ListPlcTaskLinks() => ComBusyRetry.Run<IReadOnlyList<string>>(() =>
    {
        dynamic dte = GetActiveTcDte();
        var links = new List<string>();
        foreach (PlcSourceProject project in EnumeratePlcSourceProjects(dte))
        {
            foreach (object child in (List<object>)Children(project.Nested))
            {
                if ((int)ItemTypeOf(child) != PlcItemType.Task) continue;
                string? link = ReadLinkedTask(child);
                if (!string.IsNullOrWhiteSpace(link)) links.Add(link.Trim());
            }
        }
        return links;
    }, OpenTimeout);

    public string CreateImageTask(string name) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic tirt = RequireTirt(sysMan);

        dynamic? created;
        try { created = tirt.CreateChild(name.Trim(), ImageTaskSubType, "", null); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"TIRT.CreateChild('{name.Trim()}', {ImageTaskSubType}) failed — the task was not created: {Innermost(ex)}", ex);
        }
        if (created is null)
            throw new InvalidOperationException($"TIRT.CreateChild('{name.Trim()}', {ImageTaskSubType}) returned no item.");

        object item = created;
        return AfterMutation(() => (string)PathOf(item));
    }, OpenTimeout);

    public string CreateTreeChild(string parentPath, string name, int subType, string? vInfo) => ComBusyRetry.Run(() =>
    {
        dynamic parent = RequireTreeItem(parentPath);
        dynamic? created;
        try { created = parent.CreateChild(name, subType, "", vInfo); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"CreateChild('{name}', {subType}) under '{parentPath}' failed — nothing was created: {Innermost(ex)}", ex);
        }
        if (created is null)
            throw new InvalidOperationException($"CreateChild('{name}', {subType}) under '{parentPath}' returned no item.");

        object item = created;
        return AfterMutation(() => (string)PathOf(item));
    }, OpenTimeout);

    public void ExportTreeChild(string parentPath, string childName, string filePath) => ComBusyRetry.Run<object?>(() =>
    {
        dynamic parent = RequireTreeItem(parentPath);
        try { parent.ExportChild(childName, filePath); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"ExportChild('{childName}') on '{parentPath}' failed: {Innermost(ex)}", ex);
        }
        return null;
    }, OpenTimeout);

    public void DeleteTreeChild(string parentPath, string childName) => ComBusyRetry.Run<object?>(() =>
    {
        dynamic parent = RequireTreeItem(parentPath);
        try { parent.DeleteChild(childName); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"DeleteChild('{childName}') on '{parentPath}' failed: {Innermost(ex)}", ex);
        }
        return null;
    }, OpenTimeout);

    public string ImportTreeChild(string parentPath, string filePath, string? beforeName, bool reconnect, string name) => ComBusyRetry.Run(() =>
    {
        dynamic parent = RequireTreeItem(parentPath);
        dynamic? imported;
        try { imported = parent.ImportChild(filePath, beforeName ?? string.Empty, reconnect, name); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException($"ImportChild('{filePath}') on '{parentPath}' failed: {Innermost(ex)}", ex);
        }
        if (imported is null)
            throw new InvalidOperationException($"ImportChild('{filePath}') on '{parentPath}' returned no item.");
        object item = imported;
        return AfterMutation(() => (string)PathOf(item));
    }, OpenTimeout);

    public IReadOnlyList<TreeChildInfo> ListAllTreeChildren(string treePath) => ComBusyRetry.Run<IReadOnlyList<TreeChildInfo>>(() =>
    {
        dynamic node = RequireTreeItem(treePath);
        object item = node;
        return AllChildren(item).Select(c => new TreeChildInfo((string)NameOf(c), (int)ItemTypeOf(c))).ToList();
    }, OpenTimeout);

    // Every child through the item's _NewEnum. ChildCount/Child(i) leave out the process-image folders (Image, Inputs,
    // Outputs) and the SVB task below the SAF task (live on 3.1.4026.22); the enumerator returns them. Child(i) is the
    // fallback when the item offers no enumerator.
    private static List<object> AllChildren(object node)
    {
        if (node is IEnumerable sequence)
        {
            var items = new List<object>();
            try
            {
                foreach (object? o in sequence)
                    if (o is not null) items.Add(o);
                return items;
            }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { }
        }
        return Children(node);
    }

    // Every task item: the children of TIRT, then under TINC each child and grandchild whose ProduceXml has a TaskDef
    // (SAF is a child of TINC, SVB a child of SAF). Axes, tables and images below the SAF task are skipped by item type.
    private static List<object> TaskItems(object sysMan)
    {
        var items = new List<object>(AllChildren(RequireTirt(sysMan)).Where(t => (int)ItemTypeOf(t) == TaskItemType));
        object? tinc = LookupTreeItemOrNull(sysMan, "TINC");
        if (tinc is null) return items;
        foreach (object nc in AllChildren(tinc))
        {
            if (!HasTaskDef(nc)) continue;
            items.Add(nc);
            foreach (object sub in AllChildren(nc))
                if ((int)ItemTypeOf(sub) == TaskItemType && HasTaskDef(sub)) items.Add(sub);
        }
        return items;
    }

    private static bool HasTaskDef(object item)
    {
        string xml = ComCall.ReadProp(() => (string?)((dynamic)item).ProduceXml(false), null) ?? string.Empty;
        return xml.Contains("<TaskDef>", StringComparison.Ordinal);
    }
}
