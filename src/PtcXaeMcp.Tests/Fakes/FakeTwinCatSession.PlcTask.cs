using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- plc-task ----
    public List<RealtimeTaskInfo> RealtimeTasks { get; } = new();
    public List<PlcTaskReferenceInfo> PlcTaskReferences { get; } = new();
    public List<string> PlcPrograms { get; } = new() { "MAIN" };
    public string PlcTaskPlcName { get; set; } = "Plc";
    public string PlcTaskProjectPath { get; set; } = "TIPC^Plc^Plc Project";

    public Exception? ListRealtimeTasksThrows { get; set; }
    public Exception? CreateRealtimeTaskThrows { get; set; }
    public Exception? SetRealtimeTaskTimingThrows { get; set; }
    public Exception? CreatePlcTaskReferenceThrows { get; set; }
    public Exception? SetPlcTaskLinkThrows { get; set; }

    /// <summary>What the XAE stores instead of the requested cycle time (it rounds to its base time); null = as requested.</summary>
    public uint? RealtimeTaskCycleTimeStored { get; set; }
    /// <summary>What the XAE stores instead of the requested priority; null = as requested.</summary>
    public int? RealtimeTaskPriorityStored { get; set; }
    /// <summary>SetPlcTaskLink returns without linking (the read-back shows no link).</summary>
    public bool PlcTaskLinkIsNoOp { get; set; }

    public IReadOnlyList<RealtimeTaskInfo> ListRealtimeTasks()
    {
        CallLog.Add("ListRealtimeTasks()");
        if (ListRealtimeTasksThrows is not null) throw ListRealtimeTasksThrows;
        return RealtimeTasks.ToList();
    }

    public PlcTaskContext ReadPlcTaskContext(string? plcProjectName)
    {
        CallLog.Add($"ReadPlcTaskContext({plcProjectName})");
        if (plcProjectName is not null
            && !string.Equals(plcProjectName, PlcTaskPlcName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(plcProjectName, PlcTaskPlcName + " Project", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PLC project '{plcProjectName}' not found under TIPC (PLCs: {PlcTaskPlcName}).");
        return new PlcTaskContext(PlcTaskPlcName, PlcTaskProjectPath, PlcTaskReferences.ToList(), PlcPrograms.ToList());
    }

    public string CreateRealtimeTask(string name)
    {
        CallLog.Add($"CreateRealtimeTask({name})");
        if (CreateRealtimeTaskThrows is not null) throw CreateRealtimeTaskThrows;
        string path = $"TIRT^{name}";
        RealtimeTasks.Add(new RealtimeTaskInfo(name, path, 0, 0));
        return path;
    }

    public RealtimeTaskInfo SetRealtimeTaskTiming(string taskPath, uint cycleTime100ns, int priority)
    {
        CallLog.Add($"SetRealtimeTaskTiming({taskPath},{cycleTime100ns},{priority})");
        if (SetRealtimeTaskTimingThrows is not null) throw SetRealtimeTaskTimingThrows;
        int i = RealtimeTasks.FindIndex(t => string.Equals(t.Path, taskPath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new InvalidOperationException($"Real-time task not found: {taskPath}");
        RealtimeTasks[i] = RealtimeTasks[i] with
        {
            CycleTime100ns = RealtimeTaskCycleTimeStored ?? cycleTime100ns,
            Priority = RealtimeTaskPriorityStored ?? priority,
        };
        return RealtimeTasks[i];
    }

    public string CreatePlcTaskReference(string projectPath, string name)
    {
        CallLog.Add($"CreatePlcTaskReference({projectPath},{name})");
        if (CreatePlcTaskReferenceThrows is not null) throw CreatePlcTaskReferenceThrows;
        string path = $"{projectPath}^{name}";
        PlcTaskReferences.Add(new PlcTaskReferenceInfo(name, path, null));
        return path;
    }

    public PlcTaskReferenceInfo SetPlcTaskLink(string taskReferencePath, string linkedTask)
    {
        CallLog.Add($"SetPlcTaskLink({taskReferencePath},{linkedTask})");
        if (SetPlcTaskLinkThrows is not null) throw SetPlcTaskLinkThrows;
        int i = PlcTaskReferences.FindIndex(r => string.Equals(r.Path, taskReferencePath, StringComparison.OrdinalIgnoreCase));
        if (i < 0) throw new InvalidOperationException($"PLC task reference not found: {taskReferencePath}");
        if (!PlcTaskLinkIsNoOp)
            PlcTaskReferences[i] = PlcTaskReferences[i] with { LinkedTask = linkedTask };
        return PlcTaskReferences[i];
    }
}
