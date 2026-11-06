using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAR — xae-event-logger against a live XAE/runtime. Requires runtime in
/// Run mode (managed by <see cref="XarRuntimeFixture"/>); both calls pass the stage's runtime target as
/// <c>amsNetId</c> (issue #195). Asserts the real success
/// path; a "built without TwinCAT" or "runtime not in Run" stub is never accepted
/// as a pass — it is a visible skip instead.
/// </summary>
[Collection("Stage XAR")]
[Trait("Stage", "XAR")]
public class EventLoggerIntegrationTests : IAsyncLifetime
{
    private readonly XarRuntimeFixture _xar;
    private McpClient? _client;

    public EventLoggerIntegrationTests(XarRuntimeFixture xar)
    {
        _xar = xar;
    }

    public async Task InitializeAsync()
    {
        if (!_xar.IsReady) return;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch { /* _client stays null; SkipIfNotReady will gate tests */ }
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady()
    {
        _xar.SkipIfNotReady();
        if (_client is null) throw new SkipException("EventLogger: MCP client could not be created for Stage XAR.");
    }

    [Trait("Description", "read_event_logger liefert valides JSON mit success=true gegen die Live-Runtime.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task ReadEventLogger_Live_ReturnsValidJson()
    {
        SkipIfNotReady();

        var result = await _client!.CallToolAsync("twincat_read_event_logger",
            new Dictionary<string, object?> { ["limit"] = 50, ["amsNetId"] = _xar.AmsNetId });
        var text = GetText(result);

        Assert.False(string.IsNullOrWhiteSpace(text), "Empty response");

        // When the XAR stage is live, the runtime is up — assert real success.
        // Regression for issue #43: the native TcEventLoggerAdsProxy.dll is
        // now copied next to the host exe, so the 0x8007007E module-not-found
        // failure must never surface again.
        Assert.DoesNotContain("0x8007007E", text);
        Assert.DoesNotContain("TcEventLoggerAdsProxy.dll could not be loaded", text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean(), $"success=false in: {text}");
        Assert.True(root.TryGetProperty("loggedCount", out _));
        Assert.True(root.TryGetProperty("activeAlarmCount", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("events").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("activeAlarms").ValueKind);
        int returned = root.GetProperty("returned").GetInt32();
        Assert.Equal(root.GetProperty("events").GetArrayLength(), returned);
        Assert.InRange(returned, 0, 50);
        Assert.True(root.TryGetProperty("has_more", out _), text);

        // Paging: the next page starts where this one stopped and repeats none of its events.
        if (root.GetProperty("has_more").GetBoolean())
        {
            int next = root.GetProperty("next_offset").GetInt32();
            var second = await _client!.CallToolAsync("twincat_read_event_logger",
                new Dictionary<string, object?> { ["limit"] = 50, ["offset"] = next, ["amsNetId"] = _xar.AmsNetId });
            using var page2 = JsonDocument.Parse(GetText(second));
            Assert.True(page2.RootElement.GetProperty("success").GetBoolean(), GetText(second));
            Assert.Equal(next, page2.RootElement.GetProperty("offset").GetInt32());
        }
    }

    [Trait("Description", "read_event_logger: die alten Parameter maxEvents werden abgewiesen, limit/offset sind die neuen.")]
    [SkippableFact(Timeout = 60_000)]
    public async Task ReadEventLogger_OldMaxEvents_IsRefused()
    {
        SkipIfNotReady();

        var result = await _client!.CallToolAsync("twincat_read_event_logger",
            new Dictionary<string, object?> { ["maxEvents"] = 5, ["amsNetId"] = _xar.AmsNetId });
        string text = GetText(result);

        Assert.StartsWith("[Error] twincat_read_event_logger has no parameter 'maxEvents'", text);
        Assert.Contains("limit", text);
    }

    [Trait("Description", "confirm (mode=confirm) wirft nicht; aktive Alarme danach <= vorher.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task ConfirmEventLogger_Live_ConfirmDoesNotThrowAndCountNotHigher()
    {
        SkipIfNotReady();

        var result = await _client!.CallToolAsync("twincat_confirm_event_logger",
            new Dictionary<string, object?> { ["mode"] = "confirm", ["amsNetId"] = _xar.AmsNetId });
        var text = GetText(result);

        Assert.False(string.IsNullOrWhiteSpace(text), "Empty response");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean(), $"success=false in: {text}");
        int before = root.GetProperty("activeAlarmsBefore").GetInt32();
        int after = root.GetProperty("activeAlarmsAfter").GetInt32();
        // confirm acknowledges; it never raises new alarms. Allow -1 (unreadable).
        if (before >= 0 && after >= 0)
            Assert.True(after <= before, $"active alarms increased after confirm: {before} → {after}");
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
