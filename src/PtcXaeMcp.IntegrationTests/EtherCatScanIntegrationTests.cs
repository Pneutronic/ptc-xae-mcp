using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live tests of twincat_ethercat_scan_compare. The error paths run on the neutral fixture (its master has no
/// network adapter) and must leave no temporary device behind. The scan itself needs a real EtherCAT adapter:
/// it runs only when PTC_XAE_MCP_ETHERCAT_MASTER names a master of the open solution that has an adapter assigned
/// and a network behind it (target in config mode).
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class EtherCatScanIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string TemporaryPrefix = "PtcScanTmp_";
    private McpClient? _client;
    private bool _ready;
    private string _fixtureSln = string.Empty;

    public async Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return; }
        _fixtureSln = fixture;
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(ServerUrl),
                ConnectionTimeout = TimeSpan.FromSeconds(5)
            });
            _client = await McpClient.CreateAsync(transport,
                cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            _ready = true;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, _fixtureSln);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client!.CallToolAsync(tool, args,
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(180)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private async Task AssertNoTemporaryDeviceAsync()
    {
        string topology = await CallAsync("twincat_get_fieldbus_topology", []);
        Assert.DoesNotContain(TemporaryPrefix, topology, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task UnknownPath_IsAnError_AndLeavesNoTemporaryDevice()
    {
        LiveXae.GuardReady(_ready);
        string result = await CallAsync("twincat_ethercat_scan_compare",
            new() { ["treePath"] = "TIID^Does Not Exist" });
        Assert.StartsWith("[Error]", result, StringComparison.Ordinal);
        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
        await AssertNoTemporaryDeviceAsync();
    }

    [SkippableFact]
    public async Task NotAnEtherCatMaster_IsAnError_AndLeavesNoTemporaryDevice()
    {
        LiveXae.GuardReady(_ready);
        string result = await CallAsync("twincat_ethercat_scan_compare",
            new() { ["treePath"] = FixtureLayout.El1008Path });
        Assert.StartsWith("[Error]", result, StringComparison.Ordinal);
        Assert.Contains("not an EtherCAT master", result, StringComparison.Ordinal);
        await AssertNoTemporaryDeviceAsync();
    }

    [SkippableFact]
    public async Task MasterWithoutAdapter_IsAnError_AndLeavesNoTemporaryDevice()
    {
        LiveXae.GuardReady(_ready);
        string result = await CallAsync("twincat_ethercat_scan_compare",
            new() { ["treePath"] = FixtureLayout.EtherCatMasterPath });
        Assert.StartsWith("[Error]", result, StringComparison.Ordinal);
        Assert.Contains("no network adapter", result, StringComparison.Ordinal);
        await AssertNoTemporaryDeviceAsync();
    }

    [SkippableFact]
    public async Task ScanOnRealHardware_ReturnsAComparison_AndLeavesNoTemporaryDevice()
    {
        LiveXae.GuardReady(_ready);
        string? master = Environment.GetEnvironmentVariable("PTC_XAE_MCP_ETHERCAT_MASTER");
        Skip.If(string.IsNullOrWhiteSpace(master),
            "set PTC_XAE_MCP_ETHERCAT_MASTER to a master of the open solution with an adapter and a network behind it");

        string result = await CallAsync("twincat_ethercat_scan_compare", new() { ["treePath"] = master });
        Assert.DoesNotContain("[Error]", result, StringComparison.Ordinal);
        var doc = JsonDocument.Parse(result).RootElement;
        Assert.True(doc.TryGetProperty("identical", out _));
        Assert.True(doc.GetProperty("scanned").GetArrayLength() > 0, "the scan found no box");
        await AssertNoTemporaryDeviceAsync();
    }
}
