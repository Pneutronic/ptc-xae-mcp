using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Build, activate and error-list tools.</summary>
public partial class McpServerTests
{
    // ── Build tools ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T38_BuildTools_AreListed()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("twincat_build_plc", names);
        Assert.Contains("twincat_build_solution", names);
        Assert.Contains("twincat_activate_config", names);
    }

    [SkippableFact]
    public async Task T39_BuildSolution_NoProjectOpen_ReturnsErrorOrSuccess()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_build_solution",
            new Dictionary<string, object?>());

        var text = GetText(result);
        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Build failed", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }

    [SkippableFact]
    public async Task T40_BuildPlc_NoProjectOpen_ReturnsErrorOrSuccess()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_build_plc",
            new Dictionary<string, object?>());

        var text = GetText(result);
        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Build failed", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }

    [SkippableFact]
    public async Task T41_ActivateConfig_NoProjectOpen_ReturnsErrorOrSuccess()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_activate_config",
            new Dictionary<string, object?>());

        var text = GetText(result);
        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Activated", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }

    // ── Test 42 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T42_STA_ToolWindows_ComError_IsStructured()
    {
        SkipIfNotReachable();

        // Call a COM-touching tool with no project open; the error path must not
        // silently swallow the exception and return an empty string.
        var result = await CallToolAsync("twincat_build_solution",
            new Dictionary<string, object?>());

        var text = GetText(result);
        // Any error path must produce a non-empty structured [Error] string.
        // Build tools return [Error] as plain text (IsError==false) when no solution is open,
        // or as an actual MCP error (IsError==true) for protocol-level failures.
        bool isErrorResponse = result.IsError == true || text.StartsWith("[Error]", StringComparison.Ordinal);
        bool isBuildResult = text.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Build failed", StringComparison.OrdinalIgnoreCase);
        Assert.True(isErrorResponse || isBuildResult, $"Unexpected response: {text}");
        if (isErrorResponse)
        {
            Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
            Assert.True(text.Length > 10, $"COM error was swallowed — response is empty or trivial: '{text}'");
        }
    }

    // ── build-integrity: override parameters and the .xti refusal ─────────────

    [SkippableTheory]
    [InlineData("twincat_build_plc", "ignoreStaleFiles")]
    [InlineData("twincat_build_solution", "ignoreStaleFiles")]
    [InlineData("twincat_rebuild_plc", "ignoreStaleFiles")]
    [InlineData("twincat_rebuild_solution", "ignoreStaleFiles")]
    [InlineData("twincat_open_project", "allowMissingReferences")]
    public async Task BuildIntegrity_OverrideParameter_IsInTheSchema(string tool, string parameter)
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var schema = tools.Single(t => t.Name == tool).JsonSchema;

        Assert.True(schema.GetProperty("properties").TryGetProperty(parameter, out _), $"{tool} has no '{parameter}'");
    }

    [SkippableFact]
    public async Task BuildIntegrity_OpenProject_MissingXti_IsRefusedBeforeTheXae()
    {
        SkipIfNotReachable();

        string root = Path.Combine(Path.GetTempPath(), "ptc-mcp-missing-xti-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Proj"));
            File.WriteAllText(Path.Combine(root, "Proj", "Proj.tsproj"),
                "<TcSmProject><Project><Io><Device File=\"Device 1 (EtherCAT).xti\" Id=\"1\"/></Io></Project></TcSmProject>");
            string sln = Path.Combine(root, "Proj.sln");
            File.WriteAllText(sln,
                "Project(\"{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}\") = \"Proj\", \"Proj\\Proj.tsproj\", \"{0}\"\r\nEndProject\r\n");

            var text = GetText(await CallToolAsync("twincat_open_project",
                new Dictionary<string, object?> { ["slnPath"] = sln }));

            Assert.StartsWith("[Error] 1 referenced project file (.xti) missing", text);
            Assert.Contains("Device 1 (EtherCAT).xti", text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── Test 63 — xae-error-list: tool is listed ─────────────────────────────

    [SkippableFact]
    public async Task T63_ClearErrorList_IsListed()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        Assert.Contains("twincat_clear_error_list", tools.Select(t => t.Name).ToHashSet());
    }

    // ── Test 64 — xae-error-list: returns success string (XAE running) ────────
    // Uses [SkippableFact] so the test is reported as SKIP (not FAIL) when XAE
    // is absent. Fixes #90: custom SkipException was invisible to the xUnit runner.

    [SkippableFact]
    public async Task T64_ClearErrorList_WhenXaeRunning_ReturnsSuccessString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_clear_error_list");

        var text = GetText(result);
        SkipIfXaeNotRunning(text);
        // #250: McpTests run mutating tools only without an open solution, and then an XAE that another
        // session used can hold entries the clear command does not remove. The tool says so honestly;
        // that is an environment state, not a contract break — the success path is asserted on a clean XAE.
        Skip.If(text.Contains("entries remain", StringComparison.Ordinal),
            "The XAE holds error-list entries from an earlier solution that cannot be cleared without one (#250): " + text);
        Assert.False(result.IsError == true, $"Tool returned error: {text}");
        Assert.False(string.IsNullOrWhiteSpace(text), "Response was empty");
        Assert.DoesNotContain("[Error]", text, StringComparison.OrdinalIgnoreCase);
    }
}
