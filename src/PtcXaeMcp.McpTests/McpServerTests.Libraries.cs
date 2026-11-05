using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Library references: list, check, add/remove/set version.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task LibrarySignatures_IsListedReadOnly_AndRefusesAnOutputPathOutsideTheAllowedRoots()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var tool = tools.Single(t => t.Name == "twincat_library_signatures");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);

        var result = await CallToolAsync("twincat_library_signatures",
            new Dictionary<string, object?> { ["outputPath"] = @"C:\Windows\System32\ptc-libsig.xml" });
        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("outputPath", text);
    }

    [SkippableFact]
    public async Task SaveAsLibrary_UnknownExtension_IsAToolError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_save_as_library",
            new Dictionary<string, object?> { ["outputPath"] = Path.Combine(Path.GetTempPath(), "ptc-save-as-lib.zip") });
        var text = GetText(result);

        Assert.DoesNotContain("An error occurred invoking", text);
        Assert.StartsWith("[Error] outputPath must end in .library", text);
        Assert.Contains(".compiled-library", text);
    }

    // ── Test 13 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T13_ListLibraryReferences_RunsAndReturnsString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_list_library_references");

        var text = GetText(result);
        bool ok = text.StartsWith("Found", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("No library references", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(ok, $"Unexpected response: {text}");
        if (result.IsError == true)
        {
            Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
            Assert.True(text.Length > 10, $"Error response must not be empty: '{text}'");
        }
    }

    // ── Test 14 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T14_ListInstalledLibraries_RunsAndReturnsString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_list_installed_libraries");

        var text = GetText(result);
        bool ok = text.StartsWith("Found", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("No installed libraries", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(ok, $"Unexpected response: {text}");
        if (result.IsError == true)
        {
            Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
            Assert.True(text.Length > 10, $"Error response must not be empty: '{text}'");
        }
    }

    // ── Test 18 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T18_CheckLibrariesInstalled_RunsAndReturnsString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_check_libraries_installed");

        var text = GetText(result);
        bool ok = text.StartsWith("All ", StringComparison.OrdinalIgnoreCase)
            || text.Contains("library reference", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("No library references", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(ok, $"Unexpected response: {text}");
        if (result.IsError == true)
        {
            Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
            Assert.True(text.Length > 10, $"Error response must not be empty: '{text}'");
        }
    }

    // ── twincat_add_library_reference ─────────────────────────────────────────

    [SkippableFact]
    public async Task AddLibraryReference_NoSolution_ReturnsErrorOrNotReferenced()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_add_library_reference",
            new Dictionary<string, object?> { ["name"] = "Tc2_MC2" });
        var text = GetText(result);

        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Added", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already referenced", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }

    // ── twincat_remove_library_reference ──────────────────────────────────────

    [SkippableFact]
    public async Task RemoveLibraryReference_NoSolution_ReturnsErrorOrNotReferenced()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_remove_library_reference",
            new Dictionary<string, object?> { ["name"] = "Tc2_MC2" });
        var text = GetText(result);

        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Removed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not referenced", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }

    // ── twincat_set_library_version ───────────────────────────────────────────

    [SkippableFact]
    public async Task SetLibraryVersion_NoSolution_ReturnsErrorOrNotReferenced()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_set_library_version",
            new Dictionary<string, object?> { ["name"] = "Tc2_MC2", ["version"] = "3.3.73.0" });
        var text = GetText(result);

        bool ok = text.StartsWith("[Error]", StringComparison.Ordinal)
            || text.Contains("Set", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not referenced", StringComparison.OrdinalIgnoreCase);
        Assert.True(ok, $"Unexpected response: {text}");
    }
}
