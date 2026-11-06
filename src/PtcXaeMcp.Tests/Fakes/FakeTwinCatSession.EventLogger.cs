using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

internal sealed partial class FakeTwinCatSession
{
    // ---- xae-event-logger ----
    public EventLoggerReadResult FakeReadEventLoggerResult { get; set; } =
        new EventLoggerReadResult(true, 0, 0,
            Array.Empty<EventLogEntry>(), Array.Empty<EventLogEntry>(),
            "EventLogger read: 0 events, 0 active alarms.");
    public EventLoggerActionResult FakeConfirmEventLoggerResult { get; set; } =
        new EventLoggerActionResult(true, 0, 0, "Nothing to confirm — no active alarms.");
    public Exception? ReadEventLoggerThrows { get; set; }
    public Exception? ConfirmEventLoggerThrows { get; set; }

    public EventLoggerReadResult ReadEventLogger(uint maxEvents, string? minSeverity, string? amsNetId)
    {
        CallLog.Add($"ReadEventLogger(max={maxEvents}, minSev={minSeverity ?? "<null>"}, net={amsNetId ?? "<null>"})");
        if (ReadEventLoggerThrows is not null) throw ReadEventLoggerThrows;
        if (FakeEventCache is null) return FakeReadEventLoggerResult;

        // Like the logger: the newest maxEvents events, then the severity filter on what was read.
        var read = FakeEventCache.Take((int)maxEvents).ToList();
        int min = SeverityRank(minSeverity);
        var events = read.Where(e => SeverityRank(e.Severity) >= min).ToList();
        return new EventLoggerReadResult(true, read.Count, 0, events, Array.Empty<EventLogEntry>(), "fake");
    }

    /// <summary>When set, ReadEventLogger serves this cache (newest first) instead of FakeReadEventLoggerResult.</summary>
    public List<EventLogEntry>? FakeEventCache { get; set; }

    private static int SeverityRank(string? severity) =>
        Array.IndexOf(new[] { "verbose", "info", "warning", "error", "critical" }, (severity ?? "verbose").Trim().ToLowerInvariant());

    public EventLoggerActionResult ConfirmEventLogger(string mode, string? amsNetId)
    {
        CallLog.Add($"ConfirmEventLogger(mode={mode}, net={amsNetId ?? "<null>"})");
        if (ConfirmEventLoggerThrows is not null) throw ConfirmEventLoggerThrows;
        return FakeConfirmEventLoggerResult;
    }
}
