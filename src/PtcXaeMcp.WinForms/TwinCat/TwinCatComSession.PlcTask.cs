using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- plc-task ----------------------------------------------------------------------------
//
// A real-time task and its PLC task reference (Infosys TE1000):
//   * TIRT.CreateChild(name, 1)                       — a task without image (tc3_automationinterface/242740107)
//   * ITcTaskModule.CycleTime (100 ns) / .Priority    — on the task item (…/21842001931); a setter may return S_OK
//                                                       although the XAE adjusted the value, so every value is read back
//   * ITcPlcTaskReference.LinkedTask = "TIRT^<task>"  — on the PLC task item, ItemType 621 (…/21841442571)
// Late-bound like SaveAsLibrary. CycleTime/Priority fall back to the TaskDef XML (ConsumeXml/ProduceXml) when the
// properties are not on IDispatch. Each step is its own session call, so the coordinator knows exactly what exists
// when a later step fails.
internal sealed partial class TwinCatComSession
{
    private const int RealtimeTaskSubType = 1; // "without image"

    public IReadOnlyList<RealtimeTaskInfo> ListRealtimeTasks() => ComBusyRetry.Run<IReadOnlyList<RealtimeTaskInfo>>(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        object tirt = RequireTirt(sysMan);
        var tasks = new List<RealtimeTaskInfo>();
        foreach (object task in (List<object>)Children(tirt))
            tasks.Add(ReadRealtimeTask(task));
        return tasks;
    }, OpenTimeout);

    public PlcTaskContext ReadPlcTaskContext(string? plcProjectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        List<PlcSourceProject> projects = EnumeratePlcSourceProjects(dte);
        if (projects.Count == 0)
            throw new InvalidOperationException("No PLC project under TIPC.");

        PlcSourceProject project;
        if (string.IsNullOrWhiteSpace(plcProjectName))
        {
            if (projects.Count > 1)
                throw new InvalidOperationException(
                    $"{projects.Count} PLC projects under TIPC ({string.Join(", ", projects.Select(p => p.Name))}) — pass plcProjectName.");
            project = projects[0];
        }
        else
        {
            string wanted = plcProjectName.Trim();
            project = projects.FirstOrDefault(p =>
                    string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.ProjectName, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"PLC project '{wanted}' not found under TIPC (PLCs: {string.Join(", ", projects.Select(p => p.Name))}).");
        }

        object nested = project.Nested;
        string projectPath = ComCall.ReadProp(() => (string?)((dynamic)nested).PathName, null)
            ?? throw new InvalidOperationException($"The tree path of PLC project '{project.Name}' is not readable.");

        var references = new List<PlcTaskReferenceInfo>();
        foreach (object child in (List<object>)Children(nested))
        {
            if ((int)ItemTypeOf(child) != PlcItemType.Task) continue;
            references.Add(new PlcTaskReferenceInfo((string)NameOf(child), (string)PathOf(child), ReadLinkedTask(child)));
        }

        var elements = new List<PouEntry>();
        WalkPlcTree(nested, elements, depth: 0, maxDepth: 12, allKinds: true);
        var programs = elements
            .Where(e => e.ItemType == PlcItemType.PouProgram)
            .Select(e => e.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlcTaskContext(project.Name, projectPath, references, programs);
    }, OpenTimeout);

    public string CreateRealtimeTask(string name) => ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("name is required.");

        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic tirt = RequireTirt(sysMan);

        dynamic? created;
        try { created = tirt.CreateChild(name.Trim(), RealtimeTaskSubType, "", null); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"TIRT.CreateChild('{name.Trim()}', {RealtimeTaskSubType}) failed — the task was not created: {Innermost(ex)}", ex);
        }
        if (created is null)
            throw new InvalidOperationException($"TIRT.CreateChild('{name.Trim()}', {RealtimeTaskSubType}) returned no item.");

        // The read-back runs in its own busy scope: a replay of CreateChild would answer "already exists".
        object item = created;
        return AfterMutation(() => (string)PathOf(item));
    }, OpenTimeout);

    public RealtimeTaskInfo SetRealtimeTaskTiming(string taskPath, uint cycleTime100ns, int priority) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic task = LookupTreeItemOrNull(sysMan, taskPath)
            ?? throw new InvalidOperationException($"Real-time task not found: {taskPath}");

        bool viaProperties = true;
        try
        {
            task.CycleTime = cycleTime100ns;
            task.Priority = priority;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex) && IsMissingDispatchMember(ex))
        {
            viaProperties = false;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"Setting CycleTime={cycleTime100ns.ToString(CultureInfo.InvariantCulture)} (100 ns) / Priority={priority.ToString(CultureInfo.InvariantCulture)} " +
                $"on '{taskPath}' failed: {Innermost(ex)}", ex);
        }

        if (!viaProperties)
        {
            try { task.ConsumeXml(RealtimeTaskXml.BuildTimingXml(cycleTime100ns, priority)); }
            catch (Exception ex) when (ComCall.NotBusy(ex))
            {
                throw new InvalidOperationException(
                    $"ITcTaskModule.CycleTime/Priority are not on IDispatch and ConsumeXml(TaskDef) on '{taskPath}' failed: {Innermost(ex)}", ex);
            }
        }

        // Read back from a fresh lookup, not from the node the setters went through.
        object fresh = (object?)LookupTreeItemOrNull(sysMan, taskPath) ?? (object)task;
        return ReadRealtimeTask(fresh);
    }, OpenTimeout);

    public string CreatePlcTaskReference(string projectPath, string name) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic project = LookupTreeItemWithFallback(sysMan, projectPath)
            ?? throw new InvalidOperationException($"PLC project node not found: {projectPath}");

        dynamic? created;
        try { created = project.CreateChild(name, PlcItemType.Task, "", null); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            throw new InvalidOperationException(
                $"CreateChild('{name}', {PlcItemType.Task}) for the PLC task reference under '{projectPath}' failed — " +
                $"the reference was not created: {Innermost(ex)}", ex);
        }
        if (created is null)
            throw new InvalidOperationException($"CreateChild('{name}', {PlcItemType.Task}) under '{projectPath}' returned no item.");

        object item = created;
        return AfterMutation(() => ComCall.ReadProp(() => (string?)((dynamic)item).PathName, null) ?? $"{projectPath}^{name}");
    }, OpenTimeout);

    public PlcTaskReferenceInfo SetPlcTaskLink(string taskReferencePath, string linkedTask) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object sysMan = RequireSystemManager(dte);
        dynamic reference = LookupTreeItemWithFallback(sysMan, taskReferencePath)
            ?? throw new InvalidOperationException($"PLC task reference not found: {taskReferencePath}");

        try { reference.LinkedTask = linkedTask; }
        catch (Exception ex) when (ComCall.NotBusy(ex) && IsMissingDispatchMember(ex))
        {
            throw new InvalidOperationException(
                $"ITcPlcTaskReference.LinkedTask is not offered on '{taskReferencePath}' by this XAE: {Innermost(ex)}", ex);
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            // TSM_E_ITEMNOTFOUND: no such task; TSM_E_INVALIDITEMTYPE: the path is not a task.
            throw new InvalidOperationException(
                $"LinkedTask = '{linkedTask}' on '{taskReferencePath}' failed: {Innermost(ex)}", ex);
        }

        object fresh = (object?)LookupTreeItemWithFallback(sysMan, taskReferencePath) ?? (object)reference;
        return new PlcTaskReferenceInfo((string)NameOf(fresh), (string)PathOf(fresh), ReadLinkedTask(fresh));
    }, OpenTimeout);

    private static object RequireTirt(object sysMan) =>
        (object?)LookupTreeItemOrNull(sysMan, "TIRT")
        ?? throw new InvalidOperationException("The real-time tasks node (TIRT) was not found in the open project.");

    private static string? ReadLinkedTask(object reference) =>
        ComCall.ReadProp(() => (string?)((dynamic)reference).LinkedTask, null);

    // CycleTime/Priority from ITcTaskModule; the TaskDef XML for whichever of the two is not readable that way.
    private static RealtimeTaskInfo ReadRealtimeTask(object task)
    {
        uint? cycle = ComCall.ReadProp<uint?>(() => Convert.ToUInt32((object)((dynamic)task).CycleTime, CultureInfo.InvariantCulture), null);
        int? priority = ComCall.ReadProp<int?>(() => Convert.ToInt32((object)((dynamic)task).Priority, CultureInfo.InvariantCulture), null);
        if (cycle is null || priority is null)
        {
            string xml = ComCall.ReadProp(() => (string?)((dynamic)task).ProduceXml(false), null) ?? string.Empty;
            var (xmlCycle, xmlPriority) = RealtimeTaskXml.ReadTiming(xml);
            cycle ??= xmlCycle;
            priority ??= xmlPriority;
        }
        return new RealtimeTaskInfo((string)NameOf(task), (string)PathOf(task), cycle, priority);
    }
}
