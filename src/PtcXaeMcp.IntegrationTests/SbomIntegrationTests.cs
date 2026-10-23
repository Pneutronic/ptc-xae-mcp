using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Stage XAE — the raw-inventory tools (IPC Device Manager, EtherCAT identities, library repositories and
/// resolved library versions) against the neutral fixture. The development VM is not a Beckhoff IPC, so the
/// IPC inventory is expected to answer with a structured status, never an exception-shaped [Error].
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class SbomIntegrationTests : IAsyncLifetime
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

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    // ---- twincat_ipc_read_inventory ------------------------------------------

    [Trait("Description", "IPC-Inventar gegen die lokale NetId → strukturiertes Ergebnis (mdpUnavailable/targetUnreachable/ok/mdpNotReady), nie ein [Error].")]
    [SkippableFact(Timeout = 120_000)]
    public async Task IpcReadInventory_LocalTarget_IsAStructuredResult()
    {
        SkipIfNotReady();
        var result = await _client!.CallToolAsync("twincat_ipc_read_inventory",
            new Dictionary<string, object?> { ["amsNetId"] = "127.0.0.1.1.1", ["includeRaw"] = false });
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        using var doc = JsonDocument.Parse(text);
        string status = doc.RootElement.GetProperty("status").GetString()!;
        Assert.Contains(status, new[] { "ok", "mdpUnavailable", "targetUnreachable", "mdpNotReady" });
        Assert.Equal(10000, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal(status == "ok", doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Trait("Description", "IPC-Inventar mit ungültigem moduleTypes-Filter → [Error] vor jedem ADS-Zugriff.")]
    [SkippableFact(Timeout = 60_000)]
    public async Task IpcReadInventory_BadModuleType_IsAnError()
    {
        SkipIfNotReady();
        var result = await _client!.CallToolAsync("twincat_ipc_read_inventory",
            new Dictionary<string, object?> { ["amsNetId"] = "127.0.0.1.1.1", ["moduleTypes"] = new[] { "not-a-type" } });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }

    // ---- twincat_ethercat_read_identities ------------------------------------

    [Trait("Description", "EtherCAT-Identitaeten auf dem Fixture (deaktivierter Master) -> strukturiert: masterUnreachable/addressNotFound/noEtherCatMaster, nie ein [Error].")]
    [SkippableFact(Timeout = 180_000)]
    public async Task EtherCatReadIdentities_Fixture_IsAStructuredResult()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        var result = await _client!.CallToolAsync("twincat_ethercat_read_identities", new Dictionary<string, object?>());
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        using var doc = JsonDocument.Parse(text);
        string status = doc.RootElement.GetProperty("status").GetString()!;
        Assert.Contains(status, new[] { "ok", "partial", "noEtherCatMaster" });
        foreach (var master in doc.RootElement.GetProperty("masters").EnumerateArray())
        {
            string masterStatus = master.GetProperty("status").GetString()!;
            Assert.Contains(masterStatus, new[] { "ok", "masterUnreachable", "addressNotFound" });
            Assert.True(master.TryGetProperty("slaves", out _), text);
        }
    }

    [Trait("Description", "EtherCAT-Identitaeten: amsNetId ohne masterTreePath -> [Error].")]
    [SkippableFact(Timeout = 60_000)]
    public async Task EtherCatReadIdentities_OverrideWithoutPath_IsAnError()
    {
        SkipIfNotReady();
        var result = await _client!.CallToolAsync("twincat_ethercat_read_identities",
            new Dictionary<string, object?> { ["amsNetId"] = "127.0.0.1.2.1" });
        Assert.StartsWith("[Error]", GetText(result), StringComparison.Ordinal);
    }

    // ---- twincat_list_library_repositories -----------------------------------

    [Trait("Description", "Bibliotheks-Repositories mit includeFiles auf dem Fixture -> jede Referenz aufgeloest (nicht unresolved), Datei gefunden, SHA-256 vorhanden.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task ListLibraryRepositories_Fixture_EveryReferenceResolvedWithHash()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        var result = await _client!.CallToolAsync("twincat_list_library_repositories",
            new Dictionary<string, object?> { ["includeFiles"] = true });
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        using var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("repositories").GetArrayLength() > 0, text);
        var references = doc.RootElement.GetProperty("references");
        Assert.True(references.GetArrayLength() > 0, text);
        foreach (var r in references.EnumerateArray())
        {
            string name = r.GetProperty("name").GetString()!;
            Assert.NotEqual("unresolved", r.GetProperty("resolution").GetString());
            Assert.False(string.IsNullOrEmpty(r.GetProperty("resolvedVersion").GetString()), $"{name}: {text}");
            Assert.True(r.GetProperty("fileFound").GetBoolean(), $"{name}: {text}");
            foreach (var f in r.GetProperty("files").EnumerateArray())
                Assert.Equal(64, f.GetProperty("sha256").GetString()!.Length);
        }
    }

    [Trait("Description", "Bibliotheks-Repositories ohne includeFiles -> nur Repositories, references = null.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task ListLibraryRepositories_WithoutFiles_ListsOnlyRepositories()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        var result = await _client!.CallToolAsync("twincat_list_library_repositories", new Dictionary<string, object?>());
        using var doc = JsonDocument.Parse(GetText(result));
        Assert.True(doc.RootElement.GetProperty("repositories").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("references").ValueKind);
    }

    // ---- twincat_list_library_references (resolved fields) --------------------

    [Trait("Description", "Bibliotheksreferenzen als JSON auf dem Fixture -> jede Referenz mit resolvedName/resolvedVersion und resolution != unresolved.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task ListLibraryReferences_Json_EveryReferenceIsResolved()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        var result = await _client!.CallToolAsync("twincat_list_library_references",
            new Dictionary<string, object?> { ["format"] = "json" });
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        using var doc = JsonDocument.Parse(text);
        var references = doc.RootElement.GetProperty("references");
        Assert.True(references.GetArrayLength() > 0, text);
        foreach (var r in references.EnumerateArray())
        {
            string name = r.GetProperty("name").GetString()!;
            Assert.NotEqual("unresolved", r.GetProperty("resolution").GetString());
            Assert.False(string.IsNullOrEmpty(r.GetProperty("resolvedVersion").GetString()), $"{name}: {text}");
        }
    }

    [Trait("Description", "Bibliotheksreferenzen als Text -> Zeilen tragen den Zusatz [resolved: ..., <resolution>].")]
    [SkippableFact(Timeout = 180_000)]
    public async Task ListLibraryReferences_Text_LinesCarryTheResolvedSuffix()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();
        var result = await _client!.CallToolAsync("twincat_list_library_references");
        var text = GetText(result);

        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
        var lines = text.Split((char)10).Where(l => l.StartsWith("  ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Contains("[resolved:", l, StringComparison.Ordinal));
    }
}
