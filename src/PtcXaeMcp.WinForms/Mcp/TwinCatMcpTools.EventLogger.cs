using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_event_logger", ReadOnly = true)]
    [Description("Read the TwinCAT runtime EventLogger: logged events and active alarms. " +
                 "Not the Visual Studio Error List (twincat_get_error_list / twincat_clear_error_list). Needs the runtime " +
                 "reachable on the target. Per entry: message text, severity, source, timestamps, alarm/confirmation state. " +
                 "Returns JSON { success, loggedCount, activeAlarmCount, message, total, offset, returned, has_more, " +
                 "next_offset, events[], activeAlarms[] } or an [Error]. events: newest first, paged, at most the newest " +
                 "10000 searched; total is null while the logger holds more than were read; loggedCount = events read for " +
                 "this page. activeAlarms is always complete.")]
    public string ReadEventLogger(
        [Description("Logged events per page: default 100, max 1000 (0 = 1000).")] int? limit = null,
        [Description("Logged events to skip, newest first: next_offset of the previous call while has_more. Default 0.")] int offset = 0,
        [Description("Minimum severity: Verbose | Info | Warning | Error | Critical. Omit for all.")] string? minSeverity = null,
        [Description("AMS NetId of the target runtime. Omit/empty = local runtime (single-XAE constraint: local only).")] string? amsNetId = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_event_logger", new { limit, offset, minSeverity, amsNetId }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                new EventLoggerCoordinator(bridge.Dispatcher.Session)
                    .ReadEventLogger(limit, offset, minSeverity, amsNetId)), cancellationToken);

    [McpServerTool(Name = "twincat_confirm_event_logger", ReadOnly = false, Destructive = true)]
    [Description("Acknowledge or clear alarms/events of the TwinCAT runtime EventLogger. " +
                 "Needs the runtime reachable. Checked by the active-alarm count before/after. Returns JSON { success, mode, " +
                 "activeAlarmsBefore, activeAlarmsAfter, message } or an [Error].")]
    public string ConfirmEventLogger(
        [Description("'confirm' acknowledges alarms awaiting confirmation; 'clear-alarms' clears raised alarms and resets " +
                     "confirmation; 'clear-events' empties the logged-event cache; 'all' does all three.")] string mode,
        [Description("AMS NetId of the target runtime. Omit/empty = local runtime.")] string? amsNetId = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_confirm_event_logger", new { mode, amsNetId }, () =>
            bridge.Dispatcher.Run(() =>
                new EventLoggerCoordinator(bridge.Dispatcher.Session)
                    .ConfirmEventLogger(mode, amsNetId)), cancellationToken);
}
