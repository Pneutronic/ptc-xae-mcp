using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — editor-navigation (twincat_locate_step / twincat_goto_line /
/// twincat_show_step) against a live XAE with the fixture open. Proves the real
/// VISIBLE effect the way the spec defines it: the Go-To dialog was seen and closed
/// on OK (structural cursor confirmation — the TwinCAT ST editor has no caret
/// read-back), and — the part that matters for every later tool — no orphaned modal
/// dialog is left behind (the DTE still answers afterwards).
///
/// The step-chain POU and step default to the neutral fixture
/// (<see cref="FixtureLayout.StepChainPouPath"/>, label 1010:); override with
/// PTC_XAE_MCP_FIXTURE_STEP_POU / PTC_XAE_MCP_FIXTURE_STEP for another fixture.
///
/// Preconditions: REQUIRE_LIVE=1 (or PTC_XAE_MCP_FIXTURE_SLN set), MCP server on 5010.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class EditorNavigationIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string StepPouEnvVar = "PTC_XAE_MCP_FIXTURE_STEP_POU";
    private const string StepEnvVar = "PTC_XAE_MCP_FIXTURE_STEP";

    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;
    private string _stepPou = string.Empty;
    private int _step = FixtureLayout.StepChainStep;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;

        var pou = Environment.GetEnvironmentVariable(StepPouEnvVar);
        _stepPou = string.IsNullOrWhiteSpace(pou) ? FixtureLayout.StepChainPouPath : pou;
        if (int.TryParse(Environment.GetEnvironmentVariable(StepEnvVar), out int step)) _step = step;

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
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    // The DTE must answer after every navigation — an orphaned Go-To modal would make
    // this fail with RPC_E_SERVERCALL_RETRYLATER (or "operation in progress").
    private async Task AssertXaeStillResponsiveAsync()
    {
        var text = GetText(await _client!.CallToolAsync("twincat_get_active_project"));
        Assert.Contains("\"solutionOpen\":true", text);
    }

    // ── Scenario 1: show_step on the fixture's step chain → cursor confirmed ─────

    [Trait("Description", "show_step auf Schrittkette → Zeile gefunden, Go-To-Dialog gesehen+geschlossen, XAE danach ansprechbar.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ShowStep_OnStepChain_LocatesAndConfirmsCursor()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_show_step",
            new Dictionary<string, object?> { ["treePath"] = _stepPou, ["step"] = _step, ["bringToFront"] = true }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains($"Located step {_step}", text);
        Assert.Contains("implementation line", text);
        Assert.Contains("Cursor set to implementation line", text);
        Assert.Contains("Go-To dialog seen and closed", text);
        Assert.Contains("--- Branch", text);
        Assert.Contains($"{_step}", text);
        // OS foreground is best-effort: mentioned, not asserted.
        Assert.Contains("foreground", text, StringComparison.OrdinalIgnoreCase);

        await AssertXaeStillResponsiveAsync();
    }

    // ── Scenario 2: goto_line on the declaration pane (Offline fixture) ─────────

    [Trait("Description", "goto_line section=declaration Zeile 3 → Cursor bestätigt, XAE danach ansprechbar.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task GotoLine_DeclarationPane_ConfirmsCursor()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_goto_line",
            new Dictionary<string, object?> { ["treePath"] = _stepPou, ["line"] = 3, ["section"] = "declaration", ["bringToFront"] = true }));

        Assert.DoesNotContain("[Error]", text);
        Assert.Contains("Cursor set to declaration line 3", text);
        Assert.Contains("Go-To dialog seen and closed", text);

        await AssertXaeStillResponsiveAsync();
    }

    // ── Scenario 3: locate_step miss → clean error, editor untouched ────────────

    [Trait("Description", "locate_step mit unbekanntem Schritt → [Error] mit Label-Liste, kein Editor-Zugriff.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task LocateStep_UnknownStep_ReturnsErrorWithLabelsSeen()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var text = GetText(await _client!.CallToolAsync("twincat_locate_step",
            new Dictionary<string, object?> { ["treePath"] = _stepPou, ["step"] = 424242 }));

        Assert.StartsWith("[Error]", text);
        Assert.Contains("424242", text);
        Assert.Contains("labels seen", text);
        Assert.Contains("twincat_goto_line", text);

        await AssertXaeStillResponsiveAsync();
    }

    // ── Scenario 4: goto_line twice in a row → no stale dialog state between calls ─

    [Trait("Description", "goto_line zweimal hintereinander → beide bestätigt (kein Dialog-Rest).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task GotoLine_Twice_BothConfirmed()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        var first = GetText(await _client!.CallToolAsync("twincat_goto_line",
            new Dictionary<string, object?> { ["treePath"] = _stepPou, ["line"] = 5, ["bringToFront"] = true }));
        var second = GetText(await _client!.CallToolAsync("twincat_goto_line",
            new Dictionary<string, object?> { ["treePath"] = _stepPou, ["line"] = 8, ["bringToFront"] = true }));

        Assert.Contains("Cursor set to implementation line 5", first);
        Assert.Contains("Cursor set to implementation line 8", second);

        await AssertXaeStillResponsiveAsync();
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
