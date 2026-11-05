using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_save_as_library with a .compiled-library target, live against the neutral fixture: the XAE either writes
/// the compiled library (file exists, not empty, an existing file needs overwrite) or refuses because the fixture's
/// PLC project has no Title — then the answer must carry the hint where to set it. Anything else fails.
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

    [Trait("Description", "Fixture als .compiled-library nach %TEMP% speichern → Datei existiert und ist nicht leer, vorhandene Datei nur mit overwrite; fehlt der Title, nennt der Fehler den Hinweis; andere Endung abgelehnt.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task SaveAsCompiledLibrary_WritesTheFile_OrNamesTheMissingTitle()
    {
        LiveXae.GuardReady(_ready);

        string open = await CallAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
        await PlcProjectReadiness.AwaitAsync(_client!);

        string path = Path.Combine(Path.GetTempPath(), $"PtcFixtureLib_{Guid.NewGuid():N}.compiled-library");
        try
        {
            string wrongExtension = await CallAsync("twincat_save_as_library",
                new Dictionary<string, object?> { ["outputPath"] = Path.ChangeExtension(path, ".zip") });
            Assert.StartsWith("[Error] outputPath must end in .library", wrongExtension);

            string text = await CallAsync("twincat_save_as_library", new Dictionary<string, object?> { ["outputPath"] = path });
            if (text.StartsWith("[Error]", StringComparison.Ordinal))
            {
                // The only accepted refusal: the fixture's PLC project has no Title. It must say where to set it.
                Assert.True(text.Contains("Title", StringComparison.OrdinalIgnoreCase) && text.Contains("Hint: a library needs the Title", StringComparison.Ordinal),
                    $"Save as compiled library failed for another reason than a missing Title: {text}");
                Assert.False(File.Exists(path), $"{path} was written although the tool reported an error");
                return;
            }

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
        }
    }
}
