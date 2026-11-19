using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>io-tasks — real-time tasks under TIRT/TINC, MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_task_list", ReadOnly = true)]
    [Description("List every real-time task: PLC, I/O and NC SAF/SVB tasks alike. The tasks under SYSTEM > Tasks (TIRT) and " +
                 "under TINC. Read-only; " +
                 "needs an open solution. Returns a summary line and JSON { tasks: [{ name, path, kind (plc | io | nc | other), image, " +
                 "priority, cycleTimeMs, cycleTime100ns, amsPort, autoStart, disabled }], warnings }. kind plc = a PLC task reference " +
                 "links it; io = a task with process image that no PLC calls; nc = under TINC. Values the XAE does not show are null.")]
    public string TaskList(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_task_list", null, () =>
            bridge.Dispatcher.Run(() => new TaskCoordinator(bridge.Dispatcher.Session).List()), cancellationToken);

    [McpServerTool(Name = "twincat_task_configure", ReadOnly = false, Destructive = true)]
    [Description("Change priority, cycle time, AMS port or disabled flag of a real-time task. Any task under TIRT or TINC (e.g. " +
                 "the NC SAF/SVB tasks); only the values passed change. Refused with [Error] before the logout, nothing changed: unknown or ambiguous " +
                 "task, nothing to change, priority outside 1..63 or used by another task (TIRT and TINC), cycle time <= 0 or not a " +
                 "multiple of 100 ns, AMS port outside 1..65535 or used by another task. Logs the XAE out first, reads every value back " +
                 "and saves; a value the XAE did not take is reported with [Warn], none taken is an [Error] without saving. Effective " +
                 "at the next Activate Configuration.")]
    public string TaskConfigure(
        [Description("The task: its name (e.g. 'PlcTask') or tree path (e.g. 'TINC^NC-Task 1 SAF') from twincat_task_list.")] string task,
        [Description("New priority 1..63 (lower = more urgent); unique across all tasks.")] int? priority = null,
        [Description("New cycle time in ms, > 0 and a multiple of 0.0001 ms (100 ns), e.g. 2 or 0.5.")] double? cycleTimeMs = null,
        [Description("New AMS port of the task, e.g. 301; unique across all tasks.")] int? amsPort = null,
        [Description("true disables the task, false enables it.")] bool? disabled = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_task_configure", new { task, priority, cycleTimeMs, amsPort, disabled }, () =>
            bridge.Dispatcher.Run(() =>
                new TaskCoordinator(bridge.Dispatcher.Session).Configure(task, priority, cycleTimeMs, amsPort, disabled)), cancellationToken);

    [McpServerTool(Name = "twincat_io_task_create", ReadOnly = false, Destructive = false)]
    [Description("Create an I/O task: a real-time task with image that no PLC calls. It goes under SYSTEM > Tasks and serves as " +
                 "sync task of an I/O device (e.g. a Profinet device, AMS port 301). Refused with [Error] before the logout, nothing changed: name not " +
                 "an IEC identifier or already used by a task, cycle time <= 0 or not a multiple of 100 ns, priority outside 1..63 or " +
                 "used (TIRT and TINC, listed), AMS port outside 1..65535 or used. Logs the XAE out first. The XAE gives a new image " +
                 "task priority 1 and AMS port 301 by itself; priority (and the port when passed) are written afterwards and read " +
                 "back. Saves on success; effective at the next Activate Configuration.")]
    public string IoTaskCreate(
        [Description("New task name (IEC identifier), e.g. 'IoTask'.")] string name,
        [Description("Cycle time in ms, > 0 and a multiple of 0.0001 ms (100 ns), e.g. 2.")] double cycleTimeMs,
        [Description("Priority 1..63 (lower = more urgent). Omit for the next free one (highest in use + 1).")] int? priority = null,
        [Description("AMS port, e.g. 301. Omit to keep the one the XAE assigns (301 for the first image task).")] int? amsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_io_task_create", new { name, cycleTimeMs, priority, amsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new TaskCoordinator(bridge.Dispatcher.Session).CreateIoTask(name, cycleTimeMs, priority, amsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_io_device_sync_task", ReadOnly = false, Destructive = true)]
    [Description("Read or set the sync task of an I/O device (Settings > sync task). Without task: reads it (default via mapping, or the " +
                 "task with its AMS port) and warns when no task has that port, which makes activation fail with 'target port not " +
                 "found'. With task: sets it; 'default' goes back to via mapping. The task needs a process image (twincat_io_task_create). " +
                 "Supported: I/O devices with an Ethernet protocol section, such as the Profinet device on an EL6631-0010; others are " +
                 "refused. The Automation Interface does not take this setting directly, so the device is exported, changed, removed " +
                 "and imported again at the same place with its variable links reconnected; a failed import puts the original back. " +
                 "Logs the XAE out first, reads back and saves; effective at the next Activate Configuration.")]
    public string IoDeviceSyncTask(
        [Description("Tree path of the I/O device, e.g. 'TIID^PN Device', from twincat_get_fieldbus_topology.")] string device,
        [Description("Task name or tree path from twincat_task_list, or 'default'. Omit to read the current sync task.")] string? task = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_io_device_sync_task", new { device, task }, () =>
            bridge.Dispatcher.Run(() =>
                new ProfinetCoordinator(bridge.Dispatcher.Session).SyncTask(device, task)), cancellationToken);
}
