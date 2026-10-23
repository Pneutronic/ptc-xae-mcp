using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.McpTests;

/// <summary>Fieldbus/EtherCAT topology, slave configuration and IO-Link tools.</summary>
public partial class McpServerTests
{
    // ── Test 15 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T15_GetFieldbusTopology_RunsAndReturnsString()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_fieldbus_topology");

        var text = GetText(result);
        bool ok = text.StartsWith("Found", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("No fieldbus", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("[Error]", StringComparison.Ordinal);
        Assert.True(ok, $"Unexpected response: {text}");
        if (result.IsError == true)
        {
            Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
            Assert.True(text.Length > 10, $"Error response must not be empty: '{text}'");
        }
    }

    // ── Test 16 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T16_GetDeviceXml_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_device_xml",
            new Dictionary<string, object?> { ["treePath"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Test 17 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task T17_GetDeviceXml_RootPath_RejectsTIID()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_device_xml",
            new Dictionary<string, object?> { ["treePath"] = "TIID" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T29_ConfigureEtherCatSlave_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_configure_ethercat_slave",
            new Dictionary<string, object?> { ["treePath"] = "", ["xml"] = "<Slave/>" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T30_ConfigureEtherCatSlave_EmptyXml_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_configure_ethercat_slave",
            new Dictionary<string, object?> { ["treePath"] = "TIID^Device 1", ["xml"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T31_ConfigureEtherCatSlave_RootPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_configure_ethercat_slave",
            new Dictionary<string, object?> { ["treePath"] = "TIID", ["xml"] = "<Slave/>" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── ethercat-topology-edit tools ───────────────────────────────────────────

    [SkippableFact]
    public async Task T31z_AddEtherCatDevice_EmptyName_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_add_ethercat_device",
            new Dictionary<string, object?> { ["name"] = "", ["deviceKind"] = "master" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("required", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T31a_AddEtherCatBox_RootParent_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_add_ethercat_box",
            new Dictionary<string, object?> { ["parentPath"] = "TIID", ["vInfo"] = "EL9011" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T31b_AddEtherCatBox_NeitherVInfoNorJson_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_add_ethercat_box",
            new Dictionary<string, object?> { ["parentPath"] = "TIID^Device 1 (EtherCAT)" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("exactly one", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T31c_RemoveEtherCatBox_RootPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_remove_ethercat_box",
            new Dictionary<string, object?> { ["treePath"] = "TIID" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task T31d_SetPreviousPort_BadPort_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_set_previous_port",
            new Dictionary<string, object?>
            {
                ["treePath"] = "TIID^Device 1 (EtherCAT)^Box 2 (EK1122)^Box 3 (EK1100)",
                ["port"] = "ZZ",
            });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("port", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── iolink-devices ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Iol_GetIoLinkConfig_EmptyPath_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_get_iolink_config",
            new Dictionary<string, object?> { ["treePath"] = "" });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("treePath", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Iol_ImportIodd_MissingFile_ReturnsError()
    {
        SkipIfNotReachable();

        // sourcePath must resolve under an allowed PathPolicy root (#173) so this test
        // actually reaches the file-not-found check instead of failing on the path itself.
        var sourcePath = Path.Combine(Path.GetTempPath(), "nope-IODD_" + Guid.NewGuid().ToString("N") + ".xml");
        var result = await CallToolAsync("twincat_import_iodd",
            new Dictionary<string, object?> { ["sourcePath"] = sourcePath });

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Iol_CheckIoddPresent_NoSelector_ReturnsError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_check_iodd_present",
            new Dictionary<string, object?>());

        var text = GetText(result);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("specify", text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task ReloadDeviceDescriptions_IsListedAndMentionsTheTrigger()
    {
        SkipIfNotReachable();

        var tools = await _client!.ListToolsAsync();
        var tool = tools.SingleOrDefault(t => t.Name == "twincat_reload_device_descriptions");

        Assert.NotNull(tool);
        Assert.Contains("Reload Device Descriptions", tool!.Description, StringComparison.Ordinal);
        Assert.Contains("twincat_add_ethercat_box", tool.Description, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task ReloadDeviceDescriptions_WithoutSolution_ReturnsStructuredError()
    {
        SkipIfNotReachable();

        var result = await CallToolAsync("twincat_reload_device_descriptions");

        var text = GetText(result);
        Assert.StartsWith("[Error]", text, StringComparison.Ordinal);
        Assert.True(text.Length > 10, $"Error was swallowed — response is empty or trivial: '{text}'");
    }
}
