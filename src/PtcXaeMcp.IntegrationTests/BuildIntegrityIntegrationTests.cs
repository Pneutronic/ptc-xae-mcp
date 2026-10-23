using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// The checks a build runs before it compiles, against the live XAE and the per-run %TEMP% fixture copy:
/// a PLC file changed on disk outside the XAE refuses the build (and its save never touches the file).
/// Only the scratch POU's file is edited on disk; it is restored and the fixture reopened afterwards.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class BuildIntegrityIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string StaleMarker = "// stale-file probe";

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

    private string PlcDir => Path.Combine(Path.GetDirectoryName(_fixtureSln)!, FixtureLayout.SolutionName, FixtureLayout.PlcName);

    private string ScratchFile => Path.Combine(PlcDir, "POUs", ScratchPou.Name + ".TcPOU");

    private async Task<string> Call(string tool, Dictionary<string, object?>? args = null) =>
        GetText(await _client!.CallToolAsync(tool, args ?? new Dictionary<string, object?>()));

    private async Task OpenFixtureAsync()
    {
        string open = await Call("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
    }

    [Trait("Description", "Alle PLC-Dateien mit neuerer mtime, aber gleichem Inhalt: der Build meldet keine veraltete Datei (Abgleich Datei ↔ XAE für jede Fixture-Objektart).")]
    [SkippableFact(Timeout = 300_000)]
    public async Task StaleCheck_TouchedButUnchangedFiles_AreNotReported()
    {
        LiveXae.GuardReady(_ready);
        await OpenFixtureAsync();

        DateTime later = DateTime.UtcNow.AddMinutes(1);
        foreach (string file in Directory.EnumerateFiles(PlcDir, "*.Tc*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, later);

        string result = await Call("twincat_build_plc");

        Assert.DoesNotContain("changed on disk", result);
        Assert.DoesNotContain("inconclusive", result);
    }

    [Trait("Description", "Extern geänderte POU-Datei: Build wird verweigert und die Datei bleibt unverändert; ignoreStaleFiles baut mit [Warn]; nach dem Neuladen baut es ohne Befund.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task StaleCheck_FileChangedOnDisk_RefusesTheBuild_UntilReloaded()
    {
        LiveXae.GuardReady(_ready);
        await OpenFixtureAsync();
        await ScratchPou.EnsureAsync(_client!);
        await Call("twincat_save_project");
        await OpenFixtureAsync(); // the hook and scratch body are on disk and loaded: disk and XAE agree

        string original = await File.ReadAllTextAsync(ScratchFile);
        int body = original.IndexOf("<ST><![CDATA[", StringComparison.Ordinal);
        Assert.True(body >= 0, "scratch POU has no ST body");
        string changed = original.Insert(body + "<ST><![CDATA[".Length, StaleMarker + "\r\n");
        try
        {
            await File.WriteAllTextAsync(ScratchFile, changed);

            string refused = await Call("twincat_build_plc");
            Assert.StartsWith("[Error]", refused);
            Assert.Contains("changed on disk outside the XAE", refused);
            Assert.Contains(ScratchPou.Name + ".TcPOU", refused);
            Assert.Contains(StaleMarker, await File.ReadAllTextAsync(ScratchFile));

            string ignored = await Call("twincat_build_plc", new Dictionary<string, object?> { ["ignoreStaleFiles"] = true });
            Assert.StartsWith("[Warn]", ignored);
            Assert.Contains("changed on disk outside the XAE", ignored);

            await File.WriteAllTextAsync(ScratchFile, changed); // whatever the ignored build's save wrote
            await OpenFixtureAsync();
            string reloaded = await Call("twincat_build_plc");
            Assert.DoesNotContain("changed on disk", reloaded);
            Assert.Contains("Build succeeded", reloaded, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await File.WriteAllTextAsync(ScratchFile, original);
            await OpenFixtureAsync();
        }
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
