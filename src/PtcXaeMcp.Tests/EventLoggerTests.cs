using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class EventLoggerTests
{
    private static EventLogEntry SampleEvent() => new(
        Type: "Alarm",
        Severity: "Error",
        Source: "PLC.MAIN",
        SourceId: 7,
        EventClass: "{11111111-2222-3333-4444-555555555555}",
        EventId: 42,
        Text: "Motor overtemp",
        TimeRaised: "2026-05-17T05:00:00Z",
        TimeConfirmed: null,
        TimeCleared: null,
        WithConfirmation: true,
        ConfirmationState: "WaitForConfirmation",
        IsActive: true);

    // ---- ReadEventLogger (session plumbing) ----------------------------------

    [Fact]
    public void ReadEventLogger_HappyPath_RecordsCallAndReturnsResult()
    {
        var fake = new FakeTwinCatSession
        {
            FakeReadEventLoggerResult = new EventLoggerReadResult(
                true, 1, 1, new[] { SampleEvent() }, new[] { SampleEvent() },
                "EventLogger read: 1 events, 1 active alarms."),
        };

        var result = fake.ReadEventLogger(100, null, null);

        Assert.Contains("ReadEventLogger(max=100", fake.CallLog[0]);
        Assert.True(result.Success);
        Assert.Equal(1, result.LoggedCount);
        Assert.Single(result.ActiveAlarms);
    }

    [Fact]
    public void ReadEventLogger_Throws_Propagates()
    {
        var fake = new FakeTwinCatSession
        {
            ReadEventLoggerThrows = new InvalidOperationException("ADS proxy unreachable"),
        };

        Assert.Throws<InvalidOperationException>(() => fake.ReadEventLogger(10, null, null));
        Assert.Contains("ReadEventLogger", fake.CallLog[0]);
    }

    // ---- ConfirmEventLogger (session plumbing) -------------------------------

    [Fact]
    public void ConfirmEventLogger_HappyPath_RecordsModeAndReturnsResult()
    {
        var fake = new FakeTwinCatSession
        {
            FakeConfirmEventLoggerResult = new EventLoggerActionResult(
                true, 3, 0, "Confirmed all alarms. 3 → 0 active."),
        };

        var result = fake.ConfirmEventLogger("confirm", null);

        Assert.Contains("ConfirmEventLogger(mode=confirm", fake.CallLog[0]);
        Assert.True(result.Success);
        Assert.Equal(3, result.ActiveAlarmsBefore);
        Assert.Equal(0, result.ActiveAlarmsAfter);
    }

    [Fact]
    public void ConfirmEventLogger_Throws_Propagates()
    {
        var fake = new FakeTwinCatSession
        {
            ConfirmEventLoggerThrows = new InvalidOperationException("not connected"),
        };

        Assert.Throws<InvalidOperationException>(() => fake.ConfirmEventLogger("all", null));
    }

    // ---- EventLoggerCoordinator (JSON shaping + validation) ------------------

    [Fact]
    public void Coordinator_Read_SerializesEventsAsJson()
    {
        var fake = new FakeTwinCatSession
        {
            FakeReadEventLoggerResult = new EventLoggerReadResult(
                true, 1, 1, new[] { SampleEvent() }, new[] { SampleEvent() },
                "EventLogger read: 1 events, 1 active alarms."),
        };

        var json = new EventLoggerCoordinator(fake).ReadEventLogger(null, 0, null, null);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("loggedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("activeAlarmCount").GetInt32());
        Assert.Equal(1, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
        var ev = root.GetProperty("events")[0];
        Assert.Equal("Alarm", ev.GetProperty("type").GetString());
        Assert.Equal("Motor overtemp", ev.GetProperty("text").GetString());
        Assert.Equal("WaitForConfirmation", ev.GetProperty("confirmationState").GetString());
    }

    [Fact]
    public void Coordinator_Read_DefaultPage_ReadsOneEventMoreThanItShows()
    {
        var fake = new FakeTwinCatSession();

        _ = new EventLoggerCoordinator(fake).ReadEventLogger(null, 0, null, null);
        _ = new EventLoggerCoordinator(fake).ReadEventLogger(0, 0, null, null);

        // Default page 100 (+1 to learn whether more follow); limit 0 asks for the maximum of 1000.
        Assert.Contains("ReadEventLogger(max=101,", fake.CallLog[0]);
        Assert.Contains("ReadEventLogger(max=1001,", fake.CallLog[1]);
    }

    private static List<EventLogEntry> Cache(int count, Func<int, string>? severity = null) =>
        Enumerable.Range(1, count).Select(i => SampleEvent() with { EventId = (uint)i, Severity = severity?.Invoke(i) ?? "Info" }).ToList();

    private static JsonElement Read(FakeTwinCatSession fake, int? limit, int offset, string? minSeverity = null)
    {
        string json = new EventLoggerCoordinator(fake).ReadEventLogger(limit, offset, minSeverity, null);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static List<uint> Ids(JsonElement root) =>
        root.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetUInt32()).ToList();

    [Fact]
    public void Coordinator_Read_PageWithMoreInTheLogger_HasNoTotalButANextOffset()
    {
        var fake = new FakeTwinCatSession { FakeEventCache = Cache(25) };

        var root = Read(fake, 10, 0);

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (uint)i), Ids(root));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("total").ValueKind);
        Assert.Equal(10, root.GetProperty("returned").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(10, root.GetProperty("next_offset").GetInt32());
        Assert.Contains("ReadEventLogger(max=11,", fake.CallLog[0]);
    }

    [Fact]
    public void Coordinator_Read_LastPage_KnowsTheTotal()
    {
        var fake = new FakeTwinCatSession { FakeEventCache = Cache(25) };

        var root = Read(fake, 10, 20);

        Assert.Equal(Enumerable.Range(21, 5).Select(i => (uint)i), Ids(root));
        Assert.Equal(25, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_offset").ValueKind);
    }

    [Fact]
    public void Coordinator_Read_PagingThroughAll_VisitsEveryEventOnce()
    {
        var fake = new FakeTwinCatSession { FakeEventCache = Cache(23) };
        var seen = new List<uint>();
        int? offset = 0;
        while (offset is { } o)
        {
            var root = Read(fake, 5, o);
            seen.AddRange(Ids(root));
            offset = root.GetProperty("has_more").GetBoolean() ? root.GetProperty("next_offset").GetInt32() : null;
        }
        Assert.Equal(Enumerable.Range(1, 23).Select(i => (uint)i), seen);
    }

    [Fact]
    public void Coordinator_Read_SeverityFilter_WidensTheReadUntilThePageIsFull()
    {
        // Only every tenth event is an error: a page of 3 errors needs more than the first 4 events.
        var fake = new FakeTwinCatSession { FakeEventCache = Cache(100, i => i % 10 == 0 ? "Error" : "Info") };

        var root = Read(fake, 3, 0, "Error");

        Assert.Equal(new uint[] { 10, 20, 30 }, Ids(root));
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(3, root.GetProperty("next_offset").GetInt32());
        Assert.True(fake.CallLog.Count > 1, string.Join(" | ", fake.CallLog));
    }

    [Fact]
    public void Coordinator_Read_NegativeOffset_IsRefusedWithoutReading()
    {
        var fake = new FakeTwinCatSession();

        Assert.StartsWith("[Error] offset must be 0 or more", new EventLoggerCoordinator(fake).ReadEventLogger(null, -1, null, null));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void Coordinator_Read_FailureReturnsErrorString()
    {
        var fake = new FakeTwinCatSession
        {
            FakeReadEventLoggerResult = new EventLoggerReadResult(
                false, -1, -1, Array.Empty<EventLogEntry>(), Array.Empty<EventLogEntry>(),
                "Not connected to the TC3 EventLogger."),
        };

        var text = new EventLoggerCoordinator(fake).ReadEventLogger(50, 0, null, null);

        Assert.StartsWith("[Error]", text);
        Assert.Contains("Not connected", text);
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("clear-alarms")]
    [InlineData("clear-events")]
    [InlineData("all")]
    public void Coordinator_Confirm_AcceptsValidModes(string mode)
    {
        var fake = new FakeTwinCatSession
        {
            FakeConfirmEventLoggerResult = new EventLoggerActionResult(true, 2, 0, "ok"),
        };

        var json = new EventLoggerCoordinator(fake).ConfirmEventLogger(mode, null);

        Assert.DoesNotContain("[Error]", json);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Coordinator_Confirm_InvalidMode_ReturnsErrorAndDoesNotCallSession()
    {
        var fake = new FakeTwinCatSession();

        var text = new EventLoggerCoordinator(fake).ConfirmEventLogger("nuke", null);

        Assert.StartsWith("[Error]", text);
        Assert.Contains("mode", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ConfirmEventLogger", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_Confirm_FailureReturnsErrorString()
    {
        var fake = new FakeTwinCatSession
        {
            FakeConfirmEventLoggerResult = new EventLoggerActionResult(
                false, -1, -1, "ConfirmEventLogger failed: not connected."),
        };

        var text = new EventLoggerCoordinator(fake).ConfirmEventLogger("confirm", null);

        Assert.StartsWith("[Error]", text);
    }
}
