using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>plc-task — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_task_create", ReadOnly = false, Destructive = false)]
    [Description("Create a real-time task for a PLC and link it to the PLC project. Creates the task under SYSTEM > Tasks, the " +
                 "PLC task reference linked to it and, with program, the call of that PROGRAM. Refused with [Error] before the logout, " +
                 "nothing changed: name not an IEC identifier or already used by a task, cycle time <= 0 or not a multiple of 100 ns, " +
                 "priority outside 1..63 or used by another task under TIRT or TINC (NC SAF/SVB; listed), unknown PLC project or " +
                 "PROGRAM. Logs the XAE out first. The XAE may adjust cycle time or priority silently: the result shows the read-back " +
                 "values and warns on a difference. If a later step fails, the [Error] names what was created and how to finish or " +
                 "remove it. Saves on success; effective at the next Activate Configuration.")]
    public string PlcTaskCreate(
        [Description("New task name (IEC identifier), e.g. 'FastTask'; the PLC task reference gets the same name.")] string name,
        [Description("Cycle time in ms, > 0 and a multiple of 0.0001 ms (100 ns), e.g. 10 or 0.5.")] double cycleTimeMs,
        [Description("Priority 1..63 (lower = more urgent). Omit for the next free one (highest in use + 1).")] int? priority = null,
        [Description("The PLC by name or project name, e.g. 'PLC'. Optional with exactly one PLC project.")] string? plcProjectName = null,
        [Description("Optional PROGRAM the task calls, e.g. 'MAIN'; must exist in the PLC project.")] string? program = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_task_create", new { name, cycleTimeMs, priority, plcProjectName, program }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcTaskCoordinator(bridge.Dispatcher.Session).Create(name, cycleTimeMs, priority, plcProjectName, program)), cancellationToken);
}
