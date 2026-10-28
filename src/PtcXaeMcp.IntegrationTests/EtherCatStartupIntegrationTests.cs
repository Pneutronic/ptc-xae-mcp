using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live integration test for twincat_read_ethercat_startup. The fixture has no SoE hardware, so the test inserts a
/// Beckhoff AX5203 (ESI "Beckhoff AX5xxx.xml") below the disabled EtherCAT master at run time and removes it again;
/// no fixture file changes. Skipped when the live suite is not enabled or the fixture has no EtherCAT master.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class EtherCatStartupIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
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

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private static (string Summary, JsonElement Json) Split(string text)
    {
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        int nl = text.IndexOf('\n');
        Assert.True(nl > 0, $"expected summary line + JSON: {text}");
        return (text[..nl].TrimEnd('\r'), JsonDocument.Parse(text[(nl + 1)..]).RootElement.Clone());
    }

    [Trait("Description", "AX5203 offline einfügen → Startup-Liste (SoE) lesen, mit und ohne Pfad, Filter; Box wieder entfernen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ReadEtherCatStartup_OfflineAx5203_ListsSoeEntries()
    {
        SkipIfNotReady();

        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));

        var topoText = GetText(await _client.CallToolAsync("twincat_get_fieldbus_topology"));
        Skip.IfNot(topoText.StartsWith("Found", StringComparison.Ordinal), "fixture has no fieldbus devices");

        using var topo = JsonDocument.Parse(topoText[topoText.IndexOf('{')..]);
        string? masterPath = topo.RootElement.GetProperty("devices").EnumerateArray()
            .Where(d => (d.GetProperty("name").GetString() ?? "").Contains("EtherCAT", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.GetProperty("path").GetString()).FirstOrDefault();
        Skip.If(string.IsNullOrWhiteSpace(masterPath), "fixture has no EtherCAT master");

        const string boxName = "ZZ_Startup_Scratch (AX5203)";
        string boxPath = $"{masterPath}^{boxName}";
        bool added = false;
        try
        {
            var add = GetText(await _client.CallToolAsync("twincat_add_ethercat_box",
                new Dictionary<string, object?> { ["parentPath"] = masterPath, ["vInfo"] = "AX5203", ["name"] = boxName }));
            Assert.False(add.StartsWith("[Error]", StringComparison.Ordinal), $"add AX5203 failed: {add}");
            added = true;

            // By path: the drive's SoE entries; the projected ones come from the ESI (S-0-0001/0002/0032).
            var (summary, json) = Split(GetText(await _client.CallToolAsync("twincat_read_ethercat_startup",
                new Dictionary<string, object?> { ["treePath"] = boxPath })));
            Assert.Contains("startup entries", summary, StringComparison.Ordinal);
            var box = json.GetProperty("boxes")[0];
            Assert.Equal(boxPath, box.GetProperty("treePath").GetString());
            Assert.Contains("AX5203", box.GetProperty("slaveType").GetString(), StringComparison.Ordinal);

            var soe = box.GetProperty("entries").EnumerateArray()
                .Where(e => e.GetProperty("protocol").GetString() == "SoE").ToList();
            Assert.NotEmpty(soe);
            var idns = soe.Select(e => e.GetProperty("idn").GetString()).ToHashSet();
            Assert.Contains("S-0-0001", idns);
            Assert.Contains("S-0-0002", idns);
            Assert.Contains("S-0-0032", idns);
            Assert.All(soe, e => Assert.Matches(@"^[SP]-[0-7]-\d{4}$", e.GetProperty("idn").GetString()!));

            // Without path: finds the drive; boxes without any entry (EK1100 coupler) are not listed.
            var (allSummary, all) = Split(GetText(await _client.CallToolAsync("twincat_read_ethercat_startup")));
            Assert.Contains("startup entries", allSummary, StringComparison.Ordinal);
            var allBoxes = all.GetProperty("boxes").EnumerateArray().ToList();
            Assert.Contains(allBoxes, b => b.GetProperty("treePath").GetString() == boxPath);
            Assert.DoesNotContain(allBoxes, b => (b.GetProperty("slaveType").GetString() ?? "").StartsWith("EK1100", StringComparison.Ordinal));
            Assert.All(allBoxes, b => Assert.True(b.GetProperty("entryCount").GetInt32() > 0));

            // Filters: only SoE, no fixed entries.
            var (_, filtered) = Split(GetText(await _client.CallToolAsync("twincat_read_ethercat_startup",
                new Dictionary<string, object?> { ["treePath"] = boxPath, ["protocol"] = "soe", ["includeFixed"] = false })));
            var entries = filtered.GetProperty("boxes")[0].GetProperty("entries").EnumerateArray().ToList();
            Assert.NotEmpty(entries);
            Assert.All(entries, e =>
            {
                Assert.Equal("SoE", e.GetProperty("protocol").GetString());
                Assert.False(e.GetProperty("fixed").GetBoolean());
            });
        }
        finally
        {
            var rm = GetText(await _client.CallToolAsync("twincat_remove_ethercat_box",
                new Dictionary<string, object?> { ["treePath"] = boxPath }));
            if (added)
                Assert.False(rm.StartsWith("[Error]", StringComparison.Ordinal), $"cleanup failed: {rm}");
        }
    }

    [Trait("Description", "Startup-Liste: Wurzelpfad, unbekannter Box-Pfad und ungültiges Protokoll werden mit [Error] abgewiesen.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task ReadEtherCatStartup_BadArguments_ReturnError()
    {
        SkipIfNotReady();

        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));

        string root = GetText(await _client.CallToolAsync("twincat_read_ethercat_startup",
            new Dictionary<string, object?> { ["treePath"] = "TIID" }));
        Assert.StartsWith("[Error]", root, StringComparison.Ordinal);

        string missing = GetText(await _client.CallToolAsync("twincat_read_ethercat_startup",
            new Dictionary<string, object?> { ["treePath"] = "TIID^No Such Device^No Such Box" }));
        Assert.StartsWith("[Error]", missing, StringComparison.Ordinal);
        Assert.Contains("not found", missing, StringComparison.OrdinalIgnoreCase);

        string protocol = GetText(await _client.CallToolAsync("twincat_read_ethercat_startup",
            new Dictionary<string, object?> { ["protocol"] = "VoE" }));
        Assert.StartsWith("[Error]", protocol, StringComparison.Ordinal);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
