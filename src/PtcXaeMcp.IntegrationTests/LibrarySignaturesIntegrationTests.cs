using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_library_signatures, live against the neutral fixture (it references Tc2_Standard, Tc2_System, Tc3_Module):
/// the XAE serialises the signatures, TON carries the inputs and outputs every TwinCAT user knows, and nothing in the
/// project changes on disk.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class LibrarySignaturesIntegrationTests : IAsyncLifetime
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

    private static string GetText(CallToolResult result) =>
        string.Join("\n", (result.Content ?? []).OfType<TextContentBlock>().Select(c => c.Text));

    [Trait("Description", "Bibliotheks-Signaturen des Fixtures exportieren → Datei mit Tc2_Standard, TON mit IN/PT und Q/ET, Einzelbibliothek gefiltert, Projektdateien unverändert.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task Signatures_OfTheFixture_DescribeTon_AndChangeNothing()
    {
        LiveXae.GuardReady(_ready);

        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client);

        string fixtureDir = Path.GetDirectoryName(_fixtureSln)!;
        var before = ProjectFiles(fixtureDir);
        string file = Path.Combine(Path.GetTempPath(), $"ptc-libsig-{Guid.NewGuid():N}.xml");
        try
        {
            var all = await _client.CallToolAsync("twincat_library_signatures",
                new Dictionary<string, object?> { ["outputPath"] = file, ["typeName"] = "TON" });
            string text = GetText(all);
            Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), text);
            Console.WriteLine($"[library_signatures] {text.Split('\n')[0]}");

            Assert.Contains("Tc2_Standard", text);
            Assert.Contains("TON (", text);
            Assert.Contains("IN : BOOL", text);
            Assert.Contains("PT : TIME", text);
            Assert.Contains("Q : BOOL", text);
            Assert.Contains("ET : TIME", text);
            Assert.Contains("<LibraryName>Tc2_Standard</LibraryName>", File.ReadAllText(file));

            var one = await _client.CallToolAsync("twincat_library_signatures",
                new Dictionary<string, object?> { ["outputPath"] = file, ["library"] = "Tc2_Standard" });
            string oneText = GetText(one);
            Assert.Contains("Exported the signatures of 1 library", oneText);
            Assert.DoesNotContain("<LibraryName>Tc3_Module</LibraryName>", File.ReadAllText(file));

            Assert.Equal(before, ProjectFiles(fixtureDir));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // Write times of the project files: the export must not save the project.
    private static Dictionary<string, DateTime> ProjectFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tsproj", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, File.GetLastWriteTimeUtc);
}
