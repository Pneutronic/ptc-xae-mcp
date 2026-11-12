using System.Text.Json;

namespace PtcXaeMcp.McpTests;

/// <summary>PLC visualization tools (twincat_visu_*): argument contract and refusals, over the wire.</summary>
public partial class McpServerTests
{
    private const string VisuPath = "TIPC^P^P Project^VISUs^VISU_DoesNotExist";

    [SkippableFact]
    public async Task Visu_ListElementTypes_ReturnsTheCatalog()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_list_element_types"));

        using var doc = JsonDocument.Parse(text);
        var types = doc.RootElement.GetProperty("types").EnumerateArray().Select(t => t.GetProperty("type").GetString()).ToList();
        Assert.Contains("Rectangle", types);
        Assert.Contains("Button", types);
        Assert.Contains("Frame", types);
        Assert.True(doc.RootElement.GetProperty("properties").GetArrayLength() > 10);
    }
}
