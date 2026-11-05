using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — twincat_open_plc_object against a live XAE with the neutral
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

    [Trait("Description", "Bekannten POU sichtbar öffnen → kein [Error], als Tab geöffnet/aktiviert.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenPlcObject_OnKnownPou_OpensAndActivatesTab()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var result = await _client!.CallToolAsync("twincat_open_plc_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });
        var text = GetText(result);

        Assert.False(result.IsError == true, $"open_plc_object error: {text}");
        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
        // The active-tab phrase is present whether it was newly opened or re-activated.
        bool openedOrReactivated =
            text.Contains("activated its tab", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("re-activated tab", StringComparison.OrdinalIgnoreCase);
        Assert.True(openedOrReactivated, $"Expected an active-tab confirmation, got: {text}");
        // OS foreground is best-effort: the result must MENTION it, but we do NOT
        // assert it was confirmed (a CI desktop may deny the focus steal).
        Assert.Contains("foreground", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Scenario 2: opening it again still succeeds (idempotent / already-open) ─

    [Trait("Description", "Denselben POU erneut öffnen → erneut erfolgreich (idempotent, bereits offen).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenPlcObject_Twice_SecondCallStillSucceeds()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        await _client!.CallToolAsync("twincat_open_plc_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });

        var second = await _client!.CallToolAsync("twincat_open_plc_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath });
        var text = GetText(second);

        Assert.False(second.IsError == true, $"second open_plc_object error: {text}");
        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
    }

    // ── Scenario 3: a folder (container) is refused with a structured error ────

    [Trait("Description", "Ordner/Container öffnen → sauberer [Error], kein Editor.")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenPlcObject_OnFolder_ReturnsCleanError()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        // The POUs container folder is not an openable source object.
        var result = await _client!.CallToolAsync("twincat_open_plc_object",
            new Dictionary<string, object?> { ["treePath"] = FixtureLayout.PousFolder });
        var text = GetText(result);

        Assert.StartsWith("[Error]", text);
        Assert.True(text.Length > 8, $"Error must be structured: '{text}'");
    }

    // ── open-object (generalization) ────────────────────────────────────────────

    private const string EtherCatDevicePath = FixtureLayout.EtherCatMasterPath;

    // A PLC object via the generalized tool opens its OWN tab (same fast path as the
    // deprecated alias) — assert the PLC phrasing, soft on OS foreground.
    [Trait("Description", "open_object auf PLC-POU → eigener Tab (Opened PLC object).")]
    [SkippableFact(Timeout = 240_000)]
    public async Task OpenObject_OnPlcPou_OpensOwnTab()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_open_object",
            new Dictionary<string, object?> { ["treePath"] = _pouPath }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Opened PLC object", text);
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
