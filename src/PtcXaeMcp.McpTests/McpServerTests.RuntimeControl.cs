using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Runtime control: broadcast search, routes, target, UMR.</summary>
public partial class McpServerTests
{
    // ── Test 73 — runtime-control: all tools listed ───────────────────────────

    [SkippableFact]
    public async Task T73_RuntimeControl_AllToolsListed()
    {
        SkipIfNotReachable();
        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("twincat_broadcast_search", names);
        Assert.Contains("twincat_add_route", names);
        Assert.Contains("twincat_set_target", names);
        Assert.Contains("twincat_restart_runtime", names);
        Assert.Contains("twincat_umr_start", names);
        Assert.Contains("twincat_umr_set_mode", names);
        Assert.Contains("twincat_umr_stop", names);
    }

    // ── Test 74 — runtime-control: broadcast_search returns JSON or error ─────

    [SkippableFact]
    public async Task T74_BroadcastSearch_ReturnsJsonOrError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_broadcast_search",
            new Dictionary<string, object?>());
        var text = GetText(result);
        Assert.False(string.IsNullOrWhiteSpace(text));
        // Either a JSON array (even empty []) or an [Error] message
        bool isJson = text.TrimStart().StartsWith("[", StringComparison.Ordinal);
        bool isError = text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(isJson || isError, $"Unexpected response: {text}");
    }

    // ── Test 74b — runtime-control: broadcast self-scopes off a remote target ──
    // Regression for issue #44: broadcast discovery is a local-network op and
    // must transparently scope itself to the local system even when the project
    // target points at a remote/UMR NetId — no caller set-target dance.
    // Uses [SkippableFact] so the test is reported as SKIP (not FAIL) when XAE
    // is absent. Fixes #90: custom SkipException was invisible to the xUnit runner.
    [SkippableFact]
    public async Task T74b_BroadcastSearch_SelfScopesWhenTargetRemote()
    {
        SkipIfNotReachable();
        const string remoteNetId = "1.2.3.4.1.1"; // unreachable, valid-shape NetId
        try
        {
            var set = GetText(await CallToolAsync("twincat_set_target",
                new Dictionary<string, object?> { ["amsNetId"] =remoteNetId }));
            SkipIfXaeNotRunning(set);
            Assert.DoesNotContain("[Error]", set);

            var text = GetText(await CallToolAsync("twincat_broadcast_search",
                new Dictionary<string, object?>()));
            Assert.False(string.IsNullOrWhiteSpace(text));

            // The pre-fix failure mode: broadcast refused with the
            // "requires the project target to be set to the local system"
            // guidance. Self-scoping must make that message impossible — the
            // result is now a JSON array (possibly empty) or an unrelated error.
            // Self-scope must make the local-target refusal impossible: the
            // result is now a JSON array (possibly empty) or an unrelated error
            // — never the "switch the target to local first" guidance.
            Assert.DoesNotContain("requires the project target to be the local system",
                text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requires the project target to be set to the local system",
                text, StringComparison.OrdinalIgnoreCase);
            bool isJson = text.TrimStart().StartsWith("[", StringComparison.Ordinal) && !text.StartsWith("[Error]", StringComparison.Ordinal);
            bool isError = text.StartsWith("[Error]", StringComparison.Ordinal);
            Assert.True(isJson || isError, $"Unexpected response: {text}");
        }
        finally
        {
            // Leave the session pointed at the local system for the rest of the suite.
            await CallToolAsync("twincat_set_target",
                new Dictionary<string, object?> { ["amsNetId"] ="127.0.0.1.1.1" });
        }
    }

    // ── Test 75 — runtime-control: add_route missing args returns error ────────

    [SkippableFact]
    public async Task T75_AddRoute_MissingArgs_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_add_route",
            new Dictionary<string, object?> { ["amsNetId"] ="" });
        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
    }

    // ── Test 76 — runtime-control: set_target missing netId returns error ──────

    [SkippableFact]
    public async Task T76_SetTarget_EmptyNetId_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_set_target",
            new Dictionary<string, object?> { ["amsNetId"] ="" });
        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
    }

    // ── Test 77 — runtime-control: umr_stop safe when not running ─────────────

    [SkippableFact]
    public async Task T77_UmrStop_WhenNotRunning_ReturnsSafeMessage()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_umr_stop",
            new Dictionary<string, object?>());
        var text = GetText(result);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
    }

    // ── Test 78 — runtime-control: umr_set_mode invalid value returns error ────

    [SkippableFact]
    public async Task T78_UmrSetMode_InvalidMode_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_umr_set_mode",
            new Dictionary<string, object?> { ["mode"] = "restart" });
        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
    }
}
