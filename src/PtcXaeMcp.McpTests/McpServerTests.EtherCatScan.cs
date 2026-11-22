using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>EtherCAT scan and comparison with the configured boxes.</summary>
public partial class McpServerTests
{
    [SkippableFact]
    public async Task EtherCatScan_ToolListed_WithSchemaAndHints()
    {
        SkipIfNotReachable();
        var tools = await _client!.ListToolsAsync();
        var scan = tools.Single(t => t.Name == "twincat_ethercat_scan_compare");

        var props = scan.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["treePath"], props);
        Assert.False(scan.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(scan.ProtocolTool.Annotations?.DestructiveHint);
    }

    [SkippableFact]
    public async Task EtherCatScan_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();
        var result = await CallToolAsync("twincat_ethercat_scan_compare",
            new Dictionary<string, object?> { ["treePath"] = "" });
        Assert.StartsWith("[Error]", GetText(result));
    }
}
