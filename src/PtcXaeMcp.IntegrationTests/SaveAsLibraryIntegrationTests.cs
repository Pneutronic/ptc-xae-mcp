using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_plc_project_info and twincat_save_as_library, live against the neutral fixture: the Title and Version are
/// set and read back, then the PLC project is saved as .library and as .compiled-library (files exist and are not
/// empty, an existing file needs overwrite).
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class SaveAsLibraryIntegrationTests : IAsyncLifetime
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
                ConnectionTimeout = TimeSpan.FromSeconds(5),
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
        var result = await _client!.CallToolAsync(tool, args);
        string text = string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));
        Console.WriteLine($"[save_as_library] {tool}: {text}");
        return text;
    }

    [Trait("Description", "Title und Version per plc_project_info setzen und zurücklesen, dann Fixture als .library und .compiled-library nach %TEMP% speichern → Dateien existieren und sind nicht leer, vorhandene Datei nur mit overwrite; andere Endung abgelehnt.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task ProjectInfo_ThenSaveAsLibraryAndCompiledLibrary_WritesTheFiles()
    {
        LiveXae.GuardReady(_ready);

        string open = await CallAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
        await PlcProjectReadiness.AwaitAsync(_client!);

        // A library needs the Title of the PLC project; the fixture has none.
        string info = await CallAsync("twincat_plc_project_info",
            new Dictionary<string, object?> { ["title"] = "PtcXaeMcpFixtureLib", ["version"] = "1.2.3.4", ["company"] = "Fixture" });
        Assert.StartsWith("Set Title, Version, Company (verified by read-back, project saved).", info);
        string read = await CallAsync("twincat_plc_project_info", new Dictionary<string, object?>());
        Assert.Contains("Title: PtcXaeMcpFixtureLib", read);
        Assert.Contains("Version: 1.2.3.4", read);

        string path = Path.Combine(Path.GetTempPath(), $"PtcFixtureLib_{Guid.NewGuid():N}.compiled-library");
        string plain = Path.ChangeExtension(path, ".library");
        try
        {
            string wrongExtension = await CallAsync("twincat_save_as_library",
                new Dictionary<string, object?> { ["outputPath"] = Path.ChangeExtension(path, ".zip") });
            Assert.StartsWith("[Error] outputPath must end in .library", wrongExtension);

            string library = await CallAsync("twincat_save_as_library", new Dictionary<string, object?> { ["outputPath"] = plain });
            Assert.False(library.StartsWith("[Error]", StringComparison.Ordinal), library);
            Assert.True(File.Exists(plain) && new FileInfo(plain).Length > 0, $"{plain} missing or empty after: {library}");

            string text = await CallAsync("twincat_save_as_library", new Dictionary<string, object?> { ["outputPath"] = path });
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);

            Assert.Contains("compiled library", text);
            Assert.Contains("bytes written", text);
            Assert.True(File.Exists(path), $"{path} does not exist after: {text}");
            Assert.True(new FileInfo(path).Length > 0, $"{path} is empty");

            string again = await CallAsync("twincat_save_as_library", new Dictionary<string, object?> { ["outputPath"] = path });
            Assert.StartsWith("[Error]", again);
            Assert.Contains("already exists", again);

            string replaced = await CallAsync("twincat_save_as_library",
                new Dictionary<string, object?> { ["outputPath"] = path, ["overwrite"] = true });
            Assert.False(replaced.StartsWith("[Error]", StringComparison.Ordinal), replaced);
            Assert.Contains("replaced the previous file", replaced);
            Assert.True(new FileInfo(path).Length > 0, $"{path} is empty after the overwrite");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(plain); } catch (IOException) { }
        }
    }
}
