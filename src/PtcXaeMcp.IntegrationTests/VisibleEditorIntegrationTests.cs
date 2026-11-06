using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — twincat_open_object / twincat_close_object against a live XAE with the neutral
/// fixture open (<see cref="FixtureLayout"/>). This is the only layer that proves the real VISIBLE effect:
/// opening a known POU's editor document and making it the active tab. The OS
/// foreground raise is asserted SOFT (best-effort) so the test does not flake on a
/// CI desktop that denies a focus steal from a background process.
///
/// Preconditions: REQUIRE_LIVE=1, PTC_XAE_MCP_FIXTURE_SLN set, MCP server on 5010.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class VisibleEditorIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string PouEnvVar = "PTC_XAE_MCP_FIXTURE_POU";

    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;
    private string _pouPath = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;

        var pouOverride = Environment.GetEnvironmentVariable(PouEnvVar);
        _pouPath = string.IsNullOrWhiteSpace(pouOverride) ? FixtureLayout.MainPath : pouOverride;

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5),
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?>
            {
                ["slnPath"] = _fixtureSln,
                ["closeCurrentFirst"] = true,
            });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    // ── Scenario 1: open a known POU live → opened/active, no error ────────────

    [Trait("Description", "Bekannten POU per open_object öffnen → kein [Error], als Tab geöffnet/aktiviert, XAE bleibt ohne bringToFront wo er ist.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnKnownPou_OpensAndActivatesTab()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var result = await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });
        var text = GetText(result);

        Assert.False(result.IsError == true, $"open_object error: {text}");
        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
        // The active-tab phrase is present whether it was newly opened or re-activated.
        bool openedOrReactivated =
            text.Contains("activated its tab", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("re-activated tab", StringComparison.OrdinalIgnoreCase);
        Assert.True(openedOrReactivated, $"Expected an active-tab confirmation, got: {text}");
        // Without bringToFront the tab opens behind the user's work and the result says so.
        Assert.Contains("XAE left where it is", text);
    }

    // ── Scenario 2: opening it again still succeeds (idempotent / already-open) ─

    [Trait("Description", "Denselben POU per open_object erneut öffnen → erneut erfolgreich (idempotent, bereits offen).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnPou_Twice_SecondCallStillSucceeds()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });

        var second = await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });
        var text = GetText(second);

        Assert.False(second.IsError == true, $"second open_object error: {text}");
        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
    }

    // ── Scenario 3: a folder (container) is refused with a structured error ────

    [Trait("Description", "PLC-Ordner/Container per open_object öffnen → sauberer [Error], kein Editor.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnPlcFolder_ReturnsCleanError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        // The POUs container folder is not an openable source object.
        var result = await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = FixtureLayout.PousFolder });
        var text = GetText(result);

        Assert.StartsWith("[Error]", text);
        Assert.True(text.Length > 8, $"Error must be structured: '{text}'");
    }

    // ── open-object: foreground opt-in, system-manager nodes ───────────────────

    private const string EtherCatDevicePath = FixtureLayout.EtherCatMasterPath;

    // With bringToFront=true the PLC tab opens the same way and the result reports the
    // foreground request — soft on whether Windows granted it (a CI desktop may deny it).
    [Trait("Description", "open_object auf PLC-POU mit bringToFront=true → eigener Tab, Vordergrund-Anfrage im Ergebnis gemeldet.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnPlcPou_BringToFront_OpensOwnTab()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath, ["bringToFront"] = true }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
        Assert.DoesNotContain("XAE left where it is", text);
        Assert.Contains("foreground", text, StringComparison.OrdinalIgnoreCase);
    }

    // A system-manager node has NO own tab — open_object SELECTS it (UIAutomation) and
    // the single shared sysman editor follows. Assert the select phrasing, no error.
    [Trait("Description", "open_object auf EtherCAT-Gerät → im SysMan selektiert (kein eigener Tab).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnEtherCatDevice_SelectsNodeInSysman()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = EtherCatDevicePath, ["bringToFront"] = true }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("system-manager node", text);
        // The localized SE leaf is what was actually selected in the tree.
        Assert.Contains("Solution Explorer", text);
    }

    // Closing a sysman node is a reported no-op (one shared editor, no per-node tab),
    // never an error.
    [Trait("Description", "close_object auf EtherCAT-Gerät → No tab to close (kein Fehler).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task CloseObject_OnSysmanNode_ReportsNoTab()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_close_object",
            new Dictionary<string, object?> { ["treePath"] = EtherCatDevicePath }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("No tab to close", text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
