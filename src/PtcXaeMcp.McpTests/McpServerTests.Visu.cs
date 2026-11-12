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

    [SkippableFact]
    public async Task Visu_Read_WrongKind_IsRefusedBeforeTheXaeIsAsked()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_read",
            new Dictionary<string, object?> { ["path"] = VisuPath, ["kind"] = "POU" }));

        Assert.StartsWith("[Error] The visu tools work on kind VISU", text);
    }

    [SkippableFact]
    public async Task Visu_Read_MissingKind_IsRefused()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_read",
            new Dictionary<string, object?> { ["path"] = VisuPath }));

        Assert.StartsWith("[Error] kind is required", text);
    }

    [SkippableFact]
    public async Task Visu_Read_PathNotFound_IsAnError()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_read",
            new Dictionary<string, object?> { ["path"] = VisuPath, ["kind"] = "VISU" }));
        SkipIfXaeNotRunning(text);

        Assert.StartsWith("[Error]", text);
        Assert.Contains("not found", text);
    }

    [SkippableFact]
    public async Task Visu_Apply_UnknownElementType_IsRefusedBeforeTheXaeIsAsked()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_apply", new Dictionary<string, object?>
        {
            ["path"] = VisuPath,
            ["kind"] = "VISU",
            ["page"] = """{ "elements": [ { "type": "Gauge", "x": 0, "y": 0, "width": 10, "height": 10 } ] }""",
        }));

        Assert.StartsWith("[Error]", text);
        Assert.Contains("unknown element type 'Gauge'", text);
        Assert.EndsWith("Nothing was changed.", text);
    }

    [SkippableFact]
    public async Task Visu_Apply_PageAndPageFile_IsRefused()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_apply", new Dictionary<string, object?>
        {
            ["path"] = VisuPath,
            ["kind"] = "VISU",
            ["page"] = """{ "elements": [] }""",
            ["pageFile"] = Path.Combine(Path.GetTempPath(), "page.json"),
        }));

        Assert.StartsWith("[Error]", text);
    }

    [SkippableFact]
    public async Task Visu_Apply_WrongKind_IsRefused()
    {
        SkipIfNotReachable();

        var text = GetText(await CallToolAsync("twincat_visu_apply", new Dictionary<string, object?>
        {
            ["path"] = VisuPath,
            ["kind"] = "DUT",
            ["page"] = """{ "elements": [] }""",
        }));

        Assert.StartsWith("[Error] The visu tools work on kind VISU", text);
    }
}
