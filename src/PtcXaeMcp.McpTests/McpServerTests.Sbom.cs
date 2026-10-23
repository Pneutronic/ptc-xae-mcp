using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Raw inventory tools: IPC Device Manager, EtherCAT identities, library repositories.</summary>
public partial class McpServerTests
{
    private static readonly string[] BadModuleTypes = ["zz"];

    [SkippableFact]
    public async Task Sbom_ToolsListed()
    {
        SkipIfNotReachable();
        var tools = await _client!.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        Assert.Contains("twincat_ipc_read_inventory", names);
        Assert.Contains("twincat_ethercat_read_identities", names);
        Assert.Contains("twincat_list_library_repositories", names);
    }

    [SkippableFact]
    public async Task IpcReadInventory_BadModuleType_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_ipc_read_inventory",
            new Dictionary<string, object?> { ["amsNetId"] = "127.0.0.1.1.1", ["moduleTypes"] = BadModuleTypes });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task EtherCatReadIdentities_OverrideWithoutMasterPath_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_ethercat_read_identities",
            new Dictionary<string, object?> { ["adsPort"] = 28673 });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ListLibraryReferences_BadFormat_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_list_library_references",
            new Dictionary<string, object?> { ["format"] = "xml" });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ListLibraryReferences_JsonFormat_ReturnsJsonOrError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_list_library_references",
            new Dictionary<string, object?> { ["format"] = "json" });
        var text = GetText(result);
        if (text.StartsWith("[Error]", StringComparison.Ordinal)) return; // no solution open
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("references").GetArrayLength() >= 0);
    }
}
