using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — twincat_login integration tests against a live XAE with
/// the neutral fixture open.
///
/// Preconditions: REQUIRE_LIVE=1, PTC_XAE_MCP_FIXTURE_SLN set,
/// the neutral fixture open, MCP server on port 5010, and a reachable runtime to log in to.
/// Without one, login is not possible and the tests skip with the tool's reason (SkipIfNoTarget).
/// Nothing in Stage XAE activates a runtime any more: the activation moved to Stage XAR
/// (RuntimeActivationXarIntegrationTests, issue #195).
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcLoginIntegrationTests : IAsyncLifetime
{
    private McpClient? _client;
    private bool _ready;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(LiveXae.ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, fixture);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private async Task<string> CallAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // Issue #148 (LIVE-4): with no reachable target runtime, Login and Logout are both
    // disabled and twincat_login now fails honestly (success=false, isOnlineNow=false)
    // instead of claiming "already online". That is a missing precondition of these
    // tests (a runtime to log in to), not a tool defect — skip with the tool's reason.
    private static void SkipIfNoTarget(string loginText)
    {
        if (loginText.StartsWith("[Error]", StringComparison.Ordinal)) return;
        using var doc = JsonDocument.Parse(loginText);
        var root = doc.RootElement;
        bool success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        Skip.If(!success && message.Contains("no login is possible", StringComparison.OrdinalIgnoreCase),
            $"No reachable PLC runtime to log in to: {message}");
    }

    private async Task EnsureOfflineAsync()
    {
        // Logout first so the login tests start from a known state.
        await CallAsync("twincat_logout");
        await AwaitOnlineStateAsync(online: false);
    }

    // Condition polling instead of a fixed pause (issue #157 item 7): wait until
    // twincat_get_plc_online_state reports the expected state, bounded at 10 s.
    // The caller's own assertions decide pass/fail; a timeout just proceeds.
    private Task<string> AwaitOnlineStateAsync(bool online) =>
        Poll.UntilAsync(() => CallAsync("twincat_get_plc_online_state"),
            text => ReadIsOnline(text) == online,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(250));

    private static bool? ReadIsOnline(string onlineStateText)
    {
        try
        {
            using var doc = JsonDocument.Parse(onlineStateText);
            return doc.RootElement.TryGetProperty("isOnline", out var v)
                && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean() : null;
        }
        catch (JsonException) { return null; }
    }

    [Trait("Description", "Nach activate → logout → login(online_change): isOnlineNow=true.")]
    [SkippableFact(Timeout = 90_000)]
    public async Task Login_OnlineChange_AfterLogout_BecomesOnline()
    {
        SkipIfNotReady();
        await EnsureOfflineAsync();

        var text = await CallAsync("twincat_login", new Dictionary<string, object?> { ["mode"] = "online_change" });

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"Tool returned error: {text}");
        SkipIfNoTarget(text);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        bool? isOnlineNow = root.GetProperty("isOnlineNow").ValueKind == JsonValueKind.Null
            ? null : root.GetProperty("isOnlineNow").GetBoolean();
        Assert.True(isOnlineNow == true,
            $"Expected isOnlineNow=true after online_change login, got: {text}");

        // The PLC's own online-change counter judges the login; "not_applied" would mean the change never landed.
        Assert.True(root.TryGetProperty("onlineChange", out var evidence) && evidence.ValueKind == JsonValueKind.Object,
            $"Missing onlineChange evidence: {text}");
        Assert.NotEqual("not_applied", evidence.GetProperty("verdict").GetString());

        // Verify via get_plc_online_state cross-check
        var probe = await CallAsync("twincat_get_plc_online_state");
        using var probeDoc = JsonDocument.Parse(probe);
        var probeOnline = probeDoc.RootElement.GetProperty("isOnline").GetBoolean();
        Assert.True(probeOnline, $"get_plc_online_state disagrees: {probe}");

        // Cleanup: leave PLC offline for subsequent tests
        await CallAsync("twincat_logout");
    }

    [Trait("Description", "Bereits online → login(online_change) → no-op-Erfolg, kein Hänger.")]
    [SkippableFact(Timeout = 60_000)]
    public async Task Login_AlreadyOnline_ReturnsNoOpSuccess()
    {
        SkipIfNotReady();
        // Get online first
        SkipIfNoTarget(await CallAsync("twincat_login", new Dictionary<string, object?> { ["mode"] = "online_change" }));
        await AwaitOnlineStateAsync(online: true);

        // Call again — must not hang and must return success
        var text = await CallAsync("twincat_login", new Dictionary<string, object?> { ["mode"] = "online_change" });

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"Tool returned error: {text}");
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean(), $"Expected success=true: {text}");

        await CallAsync("twincat_logout");
    }

    [Trait("Description", "Ergebnis enthält immer success, mode, dialogHandled, message.")]
    [SkippableFact(Timeout = 60_000)]
    public async Task Login_AlwaysReturnsRequiredFields()
    {
        SkipIfNotReady();
        await EnsureOfflineAsync();

        var text = await CallAsync("twincat_login", new Dictionary<string, object?> { ["mode"] = "online_change" });

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"Tool returned error: {text}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("success", out _),       $"Missing 'success' in: {text}");
        Assert.True(root.TryGetProperty("mode", out _),          $"Missing 'mode' in: {text}");
        Assert.True(root.TryGetProperty("dialogHandled", out _), $"Missing 'dialogHandled' in: {text}");
        Assert.True(root.TryGetProperty("isOnlineNow", out _),   $"Missing 'isOnlineNow' in: {text}");
        Assert.True(root.TryGetProperty("message", out _),       $"Missing 'message' in: {text}");
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()),
            $"message was empty in: {text}");

        await CallAsync("twincat_logout");
    }
}
