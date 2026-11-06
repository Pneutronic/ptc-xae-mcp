using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>plc-task — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_task_create", ReadOnly = false, Destructive = false)]
    [Description("Create a real-time task for a PLC: the task under SYSTEM > Tasks (TIRT.CreateChild) with cycle time and priority " +
                 "(ITcTaskModule), the PLC task reference under the PLC project linked to it (ITcPlcTaskReference.LinkedTask = 'TIRT^<name>') " +
                 "and, with program, the call of that PROGRAM. Refused with [Error] before the automatic logout and without a change: " +
                 "a name that is no IEC identifier or that a task already has, a cycle time <= 0 or not a whole multiple of 100 ns, " +
                 "a priority outside 1..63 or used by another task under TIRT (they are listed), an unknown PLC project or PROGRAM. " +
                 "Without priority the next free one is taken (highest priority in use + 1). The XAE may adjust cycle time or " +
                 "priority without an error, so the result shows the values read back and warns about a difference. If a later step " +
                 "fails, the [Error] names what was already created and how to finish or remove it. Saves on success; takes effect with " +
                 "the next Activate Configuration. Priorities of tasks outside TIRT (NC SAF/SVB) are not checked.")]
    public string PlcTaskCreate(
        [Description("Name of the new task (IEC identifier), e.g. 'FastTask'. The PLC task reference gets the same name.")] string name,
        [Description("Cycle time in milliseconds, > 0 and a whole multiple of 0.0001 ms (100 ns), e.g. 10 or 0.5.")] double cycleTimeMs,
        [Description("Priority 1..63 (a lower number is more urgent). Omit for the next free one.")] int? priority = null,
        [Description("The PLC by its name under PLC or its project name (e.g. 'PLC' or 'PLC Project'). Optional when the solution has exactly one PLC project.")] string? plcProjectName = null,
        [Description("Optional PROGRAM the task calls, e.g. 'MAIN'. It must exist in the PLC project.")] string? program = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_task_create", new { name, cycleTimeMs, priority, plcProjectName, program }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcTaskCoordinator(bridge.Dispatcher.Session).Create(name, cycleTimeMs, priority, plcProjectName, program)), cancellationToken);
}
