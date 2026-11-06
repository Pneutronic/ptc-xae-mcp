using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Host-level tools: status, log, stop, client info, trace wrapper, test markers, force-close.</summary>
public partial class McpServerTests
{
    // ── multi-xae-robustness (issue #57, fix #88) ─────────────────────────────

    [SkippableFact]
    public async Task ForceCloseXae_NoXae_ReturnsNothingToClose_NotAnError()
    {
        SkipIfNotReachable();

        // Pre-flight: if XAE is already running we must NOT call force_close —
        // it would kill the live XAE and cause cascading failures in later tests.
        // Use a read-only probe instead of the destructive tool (fix for #88).
        var probe = GetText(await CallToolAsync("twincat_get_active_project"));
        if (!ProbeXaeAbsent(probe))
            throw new Xunit.SkipException("XAE is running — force-close test requires no live XAE");

        // No TcXaeShell is running: the explicit recovery tool must return its
        // benign "nothing to close" payload (not an [Error], not a thrown protocol
        // error). Live multi-XAE recovery is verified separately via the live suite.
        var result = await CallToolAsync("twincat_force_close_xae");

        var text = GetText(result);
        Assert.False(result.IsError == true, $"tool returned protocol error: {text}");
        Assert.Contains("nothing to close", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 2 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T2_GetStatus_ReturnsNonEmptyString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_status");

        var text = GetText(result);
        Assert.False(string.IsNullOrWhiteSpace(text), "twincat_get_status returned empty response");
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.Contains("Access policy: access ", text);
    }

    // ── Test 3 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T3_GetLog_ReturnsString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_log");

        Assert.False(result.IsError == true, $"twincat_get_log error: {GetText(result)}");
        // Log may be empty on a fresh start — just verify no crash
    }

    // ── Test 7 ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T7_Stop_WhenIdle_ReturnsSentMessage()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_stop");

        var text = GetText(result);
        Assert.Contains("stop", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 52 — client-detection: tool is listed ─────────────────────────────

    [SkippableFact]
    public async Task T52_GetClientInfo_IsListed()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("twincat_get_client_info", names);
    }

    // ── Test 53 — client-detection: echoes this session's reported identity ────
    // Uses a DEDICATED client with a known Implementation so the assertion is
    // meaningful (the shared fixture client uses the SDK's default name).

    [SkippableFact]
    public async Task T53_GetClientInfo_EchoesKnownClientNameAndVersion()
    {
        SkipIfNotReachable();

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ServerUrl),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        });
        await using var known = await McpClient.CreateAsync(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation
                {
                    Name = "mcptests-harness",
                    Version = "9.9.9"
                }
            },
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        var result = await known.CallToolAsync("twincat_get_client_info");

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.Contains("mcptests-harness", text);
        Assert.Contains("9.9.9", text);
        // Negotiated protocol version must be present and non-empty.
        Assert.Contains("MCP protocol:", text);
        Assert.DoesNotContain("MCP protocol: (unknown)", text);
        // Trust-boundary disclaimer must travel with the response.
        Assert.Contains("self-reported", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tool profile:", text);
        Assert.Contains("Access policy:", text);
    }

    // ── XAE dialogs ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task XaeDialogs_ListsOrSaysNone_AndAnUnknownIdIsRefused()
    {
        SkipIfNotReachable();

        var list = GetText(await CallToolAsync("twincat_xae_dialogs"));
        Assert.False(McpErrorsLike(list), list);
        Assert.True(list.StartsWith("No XAE dialog is open.", StringComparison.Ordinal)
                    || list.Contains("XAE dialog(s) open", StringComparison.Ordinal), list);

        var answer = GetText(await CallToolAsync("twincat_xae_dialog_answer",
            new Dictionary<string, object?> { ["id"] = "0x0", ["button"] = "OK" }));
        Assert.StartsWith("[Error] No open XAE dialog has the id '0x0'", answer);
    }

    private static bool McpErrorsLike(string text) => text.StartsWith("[Error]", StringComparison.Ordinal);

    // ── prompts and resources ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task Prompts_AreListed_AndRenderTheirArguments()
    {
        SkipIfNotReachable();

        var names = (await _client!.ListPromptsAsync()).Select(p => p.Name).ToHashSet();
        Assert.Contains("plc-change", names);
        Assert.Contains("diagnose-runtime", names);
        Assert.Contains("doctor", names);

        var prompt = await _client!.GetPromptAsync("plc-change",
            new Dictionary<string, object?> { ["task"] = "rename nCount to nCounter" });
        string text = string.Join("\n", prompt.Messages.Select(m => (m.Content as TextContentBlock)?.Text));
        Assert.Contains("rename nCount to nCounter", text);
        Assert.Contains("twincat_plc_replace_text", text);
    }

    [SkippableFact]
    public async Task Resources_StatusAndToolsets_AreReadable()
    {
        SkipIfNotReachable();

        var uris = (await _client!.ListResourcesAsync()).Select(r => r.Uri).ToHashSet();
        Assert.Contains("ptc-xae://status", uris);
        Assert.Contains("ptc-xae://toolsets", uris);
        var templates = (await _client!.ListResourceTemplatesAsync()).Select(t => t.UriTemplate).ToHashSet();
        Assert.Contains("ptc-xae://plc/element/{path}", templates);

        string status = string.Join("\n", (await _client!.ReadResourceAsync("ptc-xae://status")).Contents
            .OfType<TextResourceContents>().Select(c => c.Text));
        Assert.Contains("Access policy:", status);
        Assert.Contains("Tool profile:", status);

        string toolsets = string.Join("\n", (await _client!.ReadResourceAsync("ptc-xae://toolsets")).Contents
            .OfType<TextResourceContents>().Select(c => c.Text));
        Assert.Contains("plc (shown", toolsets);
    }

    // ── tool profiles: a client whose name says Cursor sees the core toolsets only ──

    [SkippableFact]
    public async Task ToolProfile_ACursorClient_SeesCoreOnly_AndCannotCallAHiddenTool()
    {
        SkipIfNotReachable();

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(ServerUrl),
            ConnectionTimeout = TimeSpan.FromSeconds(5)
        });
        await using var cursor = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "cursor-mcptests", Version = "1.0" } },
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        string info = GetText(await cursor.CallToolAsync("twincat_get_client_info"));
        Skip.IfNot(info.Contains("Tool profile: core (client 'cursor-mcptests')", StringComparison.Ordinal),
            $"The host overrides the tool profile (window or PTC_XAE_MCP_TOOLSETS): {info}");

        var names = (await cursor.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.Contains("twincat_plc_read", names);
        Assert.Contains("twincat_get_status", names);
        Assert.DoesNotContain(names, n => n.StartsWith("twincat_hmi_", StringComparison.Ordinal));
        Assert.DoesNotContain("twincat_create_scope_project", names);

        var hidden = await cursor.CallToolAsync("twincat_hmi_list_projects");
        Assert.True(hidden.IsError == true);
        Assert.Contains("toolset 'hmi'", GetText(hidden));

        var all = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Assert.True(all.Count > names.Count, $"default client {all.Count} tools, cursor {names.Count}");
    }

    // ── Test 58 — mcp-activity-log: trace wrapper is transparent on success ────
    // Every tool body now passes through McpActivity.Trace. The wrapper must
    // not alter the result: a known-good read-only call still returns its
    // normal payload (proves wrapping introduced no behaviour change).

    [SkippableFact]
    public async Task T58_TraceWrapper_DoesNotAlterSuccessPayload()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_status");

        var text = GetText(result);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.False(string.IsNullOrWhiteSpace(text),
            "get_status payload was swallowed by the trace wrapper");
    }

    // ── Test 59 — mcp-activity-log: trace wrapper is transparent on error ──────

    [SkippableFact]
    public async Task T59_TraceWrapper_PreservesStructuredErrorPayload()
    {
        SkipIfNotReachable();

        // Known validation error path — must still arrive as a structured
        // [Error] string after passing through the trace wrapper.
        var result = await CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = "relative.sln" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("absolute", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 60 — mcp-activity-log: control commands still respond promptly ────
    // twincat_stop bypasses the test gate; it must always return its sentinel
    // even though it is now trace-wrapped (no deadlock / no swallow).

    [SkippableFact]
    public async Task T60_StopControlCommand_StillRespondsThroughWrapper()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_stop");

        var text = GetText(result);
        Assert.Contains("stop", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
    }

    // ── live-test-narration: ptc_test_marker ───────────────────────────────────
    // The marker is the test-only narration path: the live integration suite
    // calls it (via an assembly-wide BeforeAfterTest attribute) so the host's
    // MCP Activity tab can show which test is running. It is deliberately listed
    // in the catalog under a non-twincat_ name that flags it test-only.

    [SkippableFact]
    public async Task TestMarker_IsListed()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        Assert.Contains("ptc_test_marker", tools.Select(t => t.Name).ToHashSet());
    }

    [SkippableFact]
    public async Task TestMarker_StartThenCurrentThenEnd_RoundTrips()
    {
        SkipIfNotReachable();

        var name = "McpLayer_MarkerRoundTrip_" + Guid.NewGuid().ToString("N")[..8];

        // start → host adopts this as the current test; response echoes it.
        var start = GetText(await CallToolAsync("ptc_test_marker",
            new Dictionary<string, object?>
            {
                ["phase"] = "start",
                ["testName"] = name,
                ["description"] = "Mcp-layer round-trip of the narration marker",
            }));
        Assert.DoesNotContain("[Error]", start);
        Assert.Contains("start", start, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(name, start);

        // current → reading back the host state must report the test we started,
        // proving the marker actually updated McpActivity.CurrentTest over the wire.
        var current = GetText(await CallToolAsync("ptc_test_marker",
            new Dictionary<string, object?> { ["phase"] = "current" }));
        Assert.Contains(name, current);

        // end → closes the run; never an error.
        var end = GetText(await CallToolAsync("ptc_test_marker",
            new Dictionary<string, object?> { ["phase"] = "end", ["testName"] = name }));
        Assert.DoesNotContain("[Error]", end);
        Assert.Contains("end", end, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task TestMarker_UnknownPhase_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("ptc_test_marker",
            new Dictionary<string, object?> { ["phase"] = "sideways", ["testName"] = "X" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("phase", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── dialog watcher: the test hook that holds a call on an XAE dialog ──────────

    [SkippableFact]
    public async Task HoldXaeDialog_WithoutConfirm_OpensNothing()
    {
        SkipIfNotReachable();
        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Skip.IfNot(names.Contains("ptc_test_hold_xae_dialog"), "host started without --diagnostics");

        // Without confirm='open' the tool never reaches the XAE.
        var text = GetText(await CallToolAsync("ptc_test_hold_xae_dialog", new Dictionary<string, object?>()));
        Assert.Equal("[Error] confirm must be 'open'; nothing was opened.", text);
    }

    [SkippableFact]
    public async Task XaeDialogs_AnswersAndNamesNoWaitingCall_WhenNoneIsHeld()
    {
        SkipIfNotReachable();
        var text = GetText(await CallToolAsync("twincat_xae_dialogs"));
        Assert.DoesNotContain("[Error]", text);
        Assert.DoesNotContain("Waiting for a dialog", text);
    }
}
