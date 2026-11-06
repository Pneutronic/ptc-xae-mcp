using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_event_logger", ReadOnly = true)]
    [Description("Read the TwinCAT 3 RUNTIME EventLogger (logged events + currently active alarms) " +
                 "via the EventLogger ADS proxy. This is NOT the Visual Studio Error List " +
                 "(use twincat_clear_error_list for that) — it is the runtime event/alarm log: " +
                 "message text, severity, source, timestamps, alarm/confirmation state. " +
                 "Returns JSON { success, loggedCount, activeAlarmCount, message, total, offset, returned, has_more, " +
                 "next_offset, events[], activeAlarms[] } or an [Error] string. events are the logged events, newest " +
                 "first, paged with limit/offset (call again with offset=next_offset while has_more is true); total is " +
                 "null while the logger holds more events than were read. At most the newest 10000 logged events are " +
                 "searched. activeAlarms is always complete. loggedCount = events read from the logger for this page. " +
                 "Requires the TwinCAT runtime reachable on the target.")]
    public string ReadEventLogger(
        [Description("Logged events per page: default 100, at most 1000 (0 = 1000).")] int? limit = null,
        [Description("Logged events to skip, newest first, for the next page (next_offset of the previous call). Default 0.")] int offset = 0,
        [Description("Optional minimum severity filter: Verbose | Info | Warning | Error | Critical. " +
                     "Omit for all severities.")] string? minSeverity = null,
        [Description("Optional AMS NetId of the target runtime. Omit/empty = local runtime " +
                     "(single-XAE constraint: local only).")] string? amsNetId = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_event_logger", new { limit, offset, minSeverity, amsNetId }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                new EventLoggerCoordinator(bridge.Dispatcher.Session)
                    .ReadEventLogger(limit, offset, minSeverity, amsNetId)), cancellationToken);

    [McpServerTool(Name = "twincat_confirm_event_logger", ReadOnly = false, Destructive = true)]
    [Description("Acknowledge/clear the TwinCAT 3 runtime EventLogger. " +
                 "mode: 'confirm' = ConfirmAllAlarms (acknowledge alarms waiting for confirmation); " +
                 "'clear-alarms' = ClearAllAlarms (clear raised alarms, reset confirmation); " +
                 "'clear-events' = ClearLoggedEvents (empty the event cache); " +
                 "'all' = confirm + clear-alarms + clear-events. " +
                 "Self-verifies via a pre/post active-alarm count delta. " +
                 "Returns JSON { success, mode, activeAlarmsBefore, activeAlarmsAfter, message } " +
                 "or an [Error] string. Requires the TwinCAT runtime reachable.")]
    public string ConfirmEventLogger(
        [Description("One of: confirm | clear-alarms | clear-events | all.")] string mode,
        [Description("Optional AMS NetId of the target runtime. Omit/empty = local runtime.")] string? amsNetId = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_confirm_event_logger", new { mode, amsNetId }, () =>
            bridge.Dispatcher.Run(() =>
                new EventLoggerCoordinator(bridge.Dispatcher.Session)
                    .ConfirmEventLogger(mode, amsNetId)), cancellationToken);
}
