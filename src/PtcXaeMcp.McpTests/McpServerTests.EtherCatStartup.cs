using ModelContextProtocol.Client;

namespace PtcXaeMcp.McpTests;

/// <summary>EtherCAT Startup list tools: registration, flags and argument validation (no XAE needed).</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task EtherCatStartup_ToolIsRegisteredReadOnly()
    {
        SkipIfNotReachable();
        var tools = (await _client!.ListToolsAsync()).ToDictionary(t => t.Name);
        Assert.Contains("twincat_read_ethercat_startup", tools.Keys);
        Assert.True(tools["twincat_read_ethercat_startup"].ProtocolTool.Annotations?.ReadOnlyHint);
    }

    [SkippableFact]
    public async Task ReadEtherCatStartup_InvalidProtocol_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_read_ethercat_startup",
            new Dictionary<string, object?> { ["protocol"] = "VoE" });
        string text = GetText(result);
        Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
        Assert.Contains("CoE, SoE, EoE, FoE", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ReadEtherCatStartup_RootPath_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_read_ethercat_startup",
            new Dictionary<string, object?> { ["treePath"] = "TIID" });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }
}
