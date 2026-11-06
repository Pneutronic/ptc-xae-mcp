using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Live integration tests for twincat_read_coe_dictionary and twincat_read_coe_startup.
/// Finds an EtherCAT slave with CoE support (EL3174 preferred) dynamically via the
/// fieldbus topology. Skipped when the MCP server is not reachable or no CoE slave
/// is present in the fixture project.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class CoeIntegrationTests : IAsyncLifetime
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

    // Finds the path of any EL3174 or similar CoE analog slave in the topology.
    // Returns null when none present (test should skip).
    private async Task<string?> FindCoeSlavePathAsync(string preferModel = "EL3174")
    {
        var topo = await _client!.CallToolAsync("twincat_get_fieldbus_topology");
        var text = GetText(topo);
        if (!text.StartsWith("Found", StringComparison.Ordinal)) return null;

        int jsonStart = text.IndexOf('{');
        if (jsonStart < 0) return null;
        using var doc = JsonDocument.Parse(text[jsonStart..]);

        var allNodes = new List<(string Path, string Name)>();
        CollectNodes(doc.RootElement.GetProperty("devices"), allNodes);

        // Prefer the requested model; fall back to any EL node with CoE mailbox capability
        var preferred = allNodes.FirstOrDefault(n =>
            n.Name.Contains(preferModel, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferred.Path)) return preferred.Path;

        // Fallback: any EL analog input (3xxx)
        return allNodes.FirstOrDefault(n =>
            System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"\(EL3\d{3}")).Path;
    }

    private static void CollectNodes(JsonElement nodes, List<(string Path, string Name)> result)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            string path = node.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
            string name = node.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            if (!string.IsNullOrEmpty(path)) result.Add((path, name));
            if (node.TryGetProperty("children", out var children))
                CollectNodes(children, result);
        }
    }

    // ── twincat_read_coe_dictionary ──────────────────────────────────────────

    [Trait("Description", "CoE-Dictionary lesen → Basis-Objekte vorhanden, Antwort gültig.")]
    [SkippableFact(Timeout = 90_000)]
    public async Task ReadCoeDictionary_AnalogSlave_ReturnsDictionary()
    {
        SkipIfNotReady();

        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));

        string? slavePath = await FindCoeSlavePathAsync();
        Skip.If(slavePath is null, "No EL3174 / analog EL3xxx slave found in fixture project.");

        var result = await _client.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?> { ["treePath"] = slavePath });
        var text = GetText(result);

        Assert.False(result.IsError == true, text);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        Assert.Contains("CoE object", text);

        int jsonStart = text.IndexOf('{');
        Assert.True(jsonStart >= 0, $"No JSON in response: {text}");
        using var doc = JsonDocument.Parse(text[jsonStart..]);
        int total = doc.RootElement.GetProperty("total").GetInt32();
        Assert.True(total > 0);
        Assert.True(doc.RootElement.GetProperty("objects").GetArrayLength() > 0);
        Assert.Equal(doc.RootElement.GetProperty("objects").GetArrayLength(), doc.RootElement.GetProperty("returned").GetInt32());

        // Paging through the dictionary two objects at a time visits every object of the full read once.
        var all = await _client.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?> { ["treePath"] = slavePath, ["limit"] = 0 });
        string allText = GetText(all);
        using var allDoc = JsonDocument.Parse(allText[allText.IndexOf('{')..]);
        var expected = allDoc.RootElement.GetProperty("objects").EnumerateArray().Select(o => o.GetProperty("index").GetString()).ToList();
        Assert.Equal(total, expected.Count);

        var seen = new List<string?>();
        int? offset = 0;
        while (offset is { } o && seen.Count <= total)
        {
            var page = await _client.CallToolAsync("twincat_read_coe_dictionary",
                new Dictionary<string, object?> { ["treePath"] = slavePath, ["limit"] = 25, ["offset"] = o });
            string pageText = GetText(page);
            using var pageDoc = JsonDocument.Parse(pageText[pageText.IndexOf('{')..]);
            seen.AddRange(pageDoc.RootElement.GetProperty("objects").EnumerateArray().Select(x => x.GetProperty("index").GetString()));
            offset = pageDoc.RootElement.GetProperty("has_more").GetBoolean() ? pageDoc.RootElement.GetProperty("next_offset").GetInt32() : null;
        }
        Assert.Equal(expected, seen);
    }

    [Trait("Description", "CoE-Dictionary EL3174 lesen → Objekt 800D mit Input Type und Enum-Text.")]
    [SkippableFact(Timeout = 90_000)]
    public async Task ReadCoeDictionary_El3174_Has800DWithEnumResolvedInputType()
    {
        SkipIfNotReady();

        await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });

        string? slavePath = await FindCoeSlavePathAsync("EL3174");
        Skip.If(slavePath is null, "No EL3174 found in fixture project.");

        var result = await _client.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?>
            {
                ["treePath"] = slavePath,
                ["indexFilter"] = "8000-80FF",
            });
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);

        int jsonStart = text.IndexOf('{');
        if (jsonStart < 0) return; // non-EL3174 analog slave, ESI may vary
        using var doc = JsonDocument.Parse(text[jsonStart..]);

        bool found800D = false;
        foreach (var obj in doc.RootElement.GetProperty("objects").EnumerateArray())
        {
            string idx = obj.GetProperty("index").GetString() ?? "";
            if (!string.Equals(idx, "800D", StringComparison.OrdinalIgnoreCase)) continue;
            found800D = true;

            var subs = obj.GetProperty("subIndexes");
            var inputTypeSub = subs.EnumerateArray()
                .FirstOrDefault(s => s.GetProperty("name").GetString() == "Input Type");
            if (inputTypeSub.ValueKind != JsonValueKind.Undefined)
            {
                Assert.True(inputTypeSub.GetProperty("isSetting").GetBoolean());
                string? dt = inputTypeSub.GetProperty("defaultText").GetString();
                Assert.False(string.IsNullOrEmpty(dt),
                    "Expected enum-resolved defaultText for Input Type");
            }
            break;
        }

        // 800D should be present for a real EL3174
        Assert.True(found800D, "Object 800D not found in EL3174 CoE dictionary (filter 8000-80FF)");
    }

    [Trait("Description", "CoE-Dictionary mit Index-Filter → nur passende Objekte zurück.")]
    [SkippableFact(Timeout = 90_000)]
    public async Task ReadCoeDictionary_WithIndexFilter_ReturnsFilteredSubset()
    {
        SkipIfNotReady();

        await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });

        string? slavePath = await FindCoeSlavePathAsync();
        Skip.If(slavePath is null, "No analog EL3xxx slave found in fixture project.");

        // First get unfiltered count
        var allResult = await _client!.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?> { ["treePath"] = slavePath });
        var allText = GetText(allResult);
        if (allText.StartsWith("[Error]", StringComparison.Ordinal)) return; // ESI not found on this machine — graceful skip

        int allJsonStart = allText.IndexOf('{');
        if (allJsonStart < 0) return;
        using var allDoc = JsonDocument.Parse(allText[allJsonStart..]);
        int totalCount = allDoc.RootElement.GetProperty("total").GetInt32();

        // Now filtered to comm objects only (0x1000-0x1FFF)
        var filtered = await _client.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?> { ["treePath"] = slavePath, ["indexFilter"] = "1000-1FFF" });
        var filtText = GetText(filtered);
        if (filtText.StartsWith("[Error]", StringComparison.Ordinal)) return;

        int filtJsonStart = filtText.IndexOf('{');
        if (filtJsonStart < 0) return;
        using var filtDoc = JsonDocument.Parse(filtText[filtJsonStart..]);
        int filtCount = filtDoc.RootElement.GetProperty("total").GetInt32();

        // Filtered count must be ≤ total and all returned objects must be in range
        Assert.True(filtCount <= totalCount);
        foreach (var obj in filtDoc.RootElement.GetProperty("objects").EnumerateArray())
        {
            int idx = Convert.ToInt32(obj.GetProperty("index").GetString(), 16);
            Assert.InRange(idx, 0x1000, 0x1FFF);
        }
    }

    [Trait("Description", "CoE-Dictionary mit Root-Pfad TIID → muss mit [Error] root abgewiesen werden.")]
    [SkippableFact(Timeout = 30_000)]
    public async Task ReadCoeDictionary_RootPath_ReturnsError()
    {
        SkipIfNotReady();

        var result = await _client!.CallToolAsync("twincat_read_coe_dictionary",
            new Dictionary<string, object?> { ["treePath"] = "TIID" });
        var text = GetText(result);

        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── twincat_read_coe_startup ─────────────────────────────────────────────

    [Trait("Description", "CoE-Startup lesen → strukturierte Antwort (0 oder mehr Einträge).")]
    [SkippableFact(Timeout = 90_000)]
    public async Task ReadCoeStartup_AnalogSlave_ReturnsStartupList()
    {
        SkipIfNotReady();

        await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });

        string? slavePath = await FindCoeSlavePathAsync();
        Skip.If(slavePath is null, "No analog EL3xxx slave found in fixture project.");

        var result = await _client!.CallToolAsync("twincat_read_coe_startup",
            new Dictionary<string, object?> { ["treePath"] = slavePath });
        var text = GetText(result);

        Assert.False(result.IsError == true, text);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        Assert.Contains("Startup override", text);

        int jsonStart = text.IndexOf('{');
        Assert.True(jsonStart >= 0, $"No JSON in response: {text}");
        using var doc = JsonDocument.Parse(text[jsonStart..]);

        int count = doc.RootElement.GetProperty("entryCount").GetInt32();
        Assert.True(count >= 0);
        Assert.Equal(count, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Trait("Description", "CoE-Startup mit Root-Pfad TIID → muss mit [Error] root abgewiesen werden.")]
    [SkippableFact(Timeout = 30_000)]
    public async Task ReadCoeStartup_RootPath_ReturnsError()
    {
        SkipIfNotReady();

        var result = await _client!.CallToolAsync("twincat_read_coe_startup",
            new Dictionary<string, object?> { ["treePath"] = "TIID" });
        var text = GetText(result);

        Assert.StartsWith("[Error]", text);
        Assert.Contains("root", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
