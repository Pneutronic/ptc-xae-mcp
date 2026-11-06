using System.Text.Json;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// xae-event-logger: validates input and shapes the TC3 EventLogger
/// read/confirm results into JSON for the MCP layer (mirrors
/// ActiveProjectCoordinator). Honest errors are returned as "[Error] ..."
/// strings, never a faked success.
/// </summary>
internal sealed class EventLoggerCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static readonly string[] ValidModes =
        { "confirm", "clear-alarms", "clear-events", "all" };

    /// <summary>Logged events per page: default 100, at most 1000.</summary>
    internal static readonly McpLimit EventPage = new(100, 1000);

    /// <summary>The newest this many logged events are the most a call reads (paging deeper or filtering by severity).</summary>
    internal const int EventScanCap = 10_000;

    // The logger hands out its newest N events (GetLoggedEvents(N)) and has no count of its cache, so a page is filled
    // by reading one more event than the page needs: when the logger returns fewer, everything was seen and the total is
    // known; otherwise total is null and has_more true. A severity filter thins the events out, so the read window
    // doubles until the page is full, the cache is exhausted or EventScanCap is reached.
    public string ReadEventLogger(int? limit, int offset, string? minSeverity, string? amsNetId)
    {
        if (McpPaging.Validate(limit, offset) is { } invalid) return invalid;
        int size = EventPage.Resolve(limit);
        long want = (long)offset + size + 1;
        int read = (int)Math.Min(want, EventScanCap);

        EventLoggerReadResult r;
        while (true)
        {
            r = session.ReadEventLogger((uint)read, minSeverity, amsNetId);
            if (!r.Success)
                return $"[Error] {r.Message}";
            if (r.LoggedCount < read || r.Events.Count >= want || read >= EventScanCap) break;
            read = (int)Math.Min(read * 2L, EventScanCap);
        }

        bool exhausted = r.LoggedCount < read;
        bool scanCapped = !exhausted && read >= EventScanCap && r.Events.Count < want;
        // At the scan cap the newest EventScanCap events are all there is to page through: say so instead of a has_more
        // that would lead to empty pages.
        var page = McpPaging.ApplyPartial(r.Events, size, offset, complete: exhausted || scanCapped);

        string severity = string.IsNullOrWhiteSpace(minSeverity) ? "" : $" (severity ≥ {minSeverity.Trim()})";
        string range = page.Returned == 0 ? "no logged events" : $"logged events {page.Offset + 1}–{page.Offset + page.Returned}";
        string of = page.Total is { } total ? $" of {total}" : " (more in the logger)";
        string message = $"EventLogger read: {range}{of}{severity}, newest first; {r.ActiveAlarms.Count} active alarm(s)." +
                         (scanCapped ? $" Only the newest {EventScanCap} logged events were searched." : "");

        return page.Serialize(new
        {
            success = true,
            loggedCount = r.LoggedCount,
            activeAlarmCount = r.ActiveAlarmCount,
            message,
            events = page.Items,
            activeAlarms = r.ActiveAlarms,
        }, JsonOpts, "events");
    }

    public string ConfirmEventLogger(string mode, string? amsNetId)
    {
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        if (Array.IndexOf(ValidModes, mode) < 0)
            return $"[Error] mode must be one of: {string.Join(", ", ValidModes)}.";

        var r = session.ConfirmEventLogger(mode, amsNetId);
        if (!r.Success)
            return $"[Error] {r.Message}";

        return JsonSerializer.Serialize(new
        {
            success = true,
            mode,
            activeAlarmsBefore = r.ActiveAlarmsBefore,
            activeAlarmsAfter = r.ActiveAlarmsAfter,
            message = r.Message,
        }, JsonOpts);
    }
}
