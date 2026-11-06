using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Event logger tools.</summary>
public partial class McpServerTests
{
    // ── Test 84 — xae-event-logger: both tools listed ─────────────────────────

    [SkippableFact]
    public async Task T84_EventLogger_ToolsListed()
    {
        SkipIfNotReachable();
        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("twincat_read_event_logger", names);
        Assert.Contains("twincat_confirm_event_logger", names);
    }

    // ── Test 85 — xae-event-logger: read returns JSON or [Error] ──────────────

    [SkippableFact]
    public async Task T85_ReadEventLogger_ReturnsJsonOrError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_read_event_logger",
            new Dictionary<string, object?> { ["limit"] = 10 });
        var text = GetText(result);
        Assert.False(string.IsNullOrWhiteSpace(text));
        bool isJson = text.TrimStart().StartsWith("{", StringComparison.Ordinal);
        bool isError = text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(isJson || isError, $"Unexpected response: {text}");
        if (isJson)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.True(doc.RootElement.GetProperty("returned").GetInt32() <= 10, text);
            Assert.True(doc.RootElement.TryGetProperty("has_more", out _), text);
        }
    }

    // ── Test 86 — xae-event-logger: confirm invalid mode → [Error] ────────────

    [SkippableFact]
    public async Task T86_ConfirmEventLogger_InvalidMode_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_confirm_event_logger",
            new Dictionary<string, object?> { ["mode"] = "obliterate" });
        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("mode", text, StringComparison.OrdinalIgnoreCase);
    }
}
