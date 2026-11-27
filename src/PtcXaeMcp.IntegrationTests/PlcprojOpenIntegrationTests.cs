using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_open_project on a bare .plcproj, as in a library repository without a TwinCAT project around it. The
/// server wraps it in a generated stand-alone PLC project under %TEMP%; the .plcproj and its sources stay where they
/// are and are saved in place. Works on a copy of the fixture's PLC folder only, never on the fixture itself; the next
/// test that needs the fixture reopens it.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcprojOpenIntegrationTests : IAsyncLifetime
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

    [Trait("Description", "Einzelne .plcproj (Library-Repo ohne .tsproj) per open_project öffnen → PLC-Baum da, edit_entries mit '// Kommentar' landet einfach kommentiert in der Quelldatei, Build grün.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task OpenBarePlcproj_TreeLoads_EditsAreSavedInPlace()
    {
        LiveXae.GuardReady(_ready);

        // A "library repository": only the PLC folder of a fresh fixture copy, no .tsproj, no .sln.
        string sourcePlc = Path.Combine(Path.GetDirectoryName(_fixtureSln)!, "PtcXaeMcpFixture", "FixturePlc");
        string repo = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-plcproj-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
        string libDir = Path.Combine(repo, "FixturePlc");
        CopyDirectory(sourcePlc, libDir);
        string plcproj = Path.Combine(libDir, "FixturePlc.plcproj");
        Assert.True(File.Exists(plcproj), $"fixture PLC project missing: {plcproj}");
        string dut = Path.Combine(libDir, "DUTs", "ST_FixtureRecord.TcDUT");

        var open = GetText(await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = plcproj, ["closeCurrentFirst"] = true }));
        Assert.DoesNotContain("[Error]", open);
        Assert.Contains("Opened", open);

        var pous = GetText(await _client!.CallToolAsync("twincat_plc_list",
            new Dictionary<string, object?> { ["kind"] = "POU" }));
        Assert.Contains("^MAIN", pous);
        // The tree path has spaces ("FixturePlc Projekt"): cut the PLC root out of the MAIN line.
        string mainLine = pous.Split('\n').First(l => l.Contains("^POUs^MAIN", StringComparison.Ordinal));
        int start = mainLine.IndexOf("TIPC^", StringComparison.Ordinal);
        string plcRoot = mainLine[start..mainLine.IndexOf("^POUs^MAIN", StringComparison.Ordinal)];
        string dutPath = plcRoot + "^DUTs^ST_FixtureRecord";

        // A comment passed with its own // is written once.
        var edit = GetText(await _client!.CallToolAsync("twincat_plc_edit_entries",
            new Dictionary<string, object?>
            {
                ["path"] = dutPath, ["kind"] = "DUT", ["op"] = "add", ["name"] = "nLibProbe", ["type"] = "INT",
                ["comment"] = "// library probe",
            }));
        Assert.DoesNotContain("[Error]", edit);

        var save = GetText(await _client!.CallToolAsync("twincat_save_project", new Dictionary<string, object?>()));
        Assert.DoesNotContain("[Error]", save);

        string saved = File.ReadAllText(dut);
        Assert.Contains("nLibProbe : INT; // library probe", saved);
        Assert.DoesNotContain("// //", saved);
        // Nothing of the generated wrapper lands in the library repository.
        Assert.Empty(Directory.GetFiles(repo, "*.tspproj", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(repo, "*.sln", SearchOption.AllDirectories));

        var build = GetText(await _client!.CallToolAsync("twincat_build_plc", new Dictionary<string, object?>()));
        Assert.Contains("Build succeeded", build);

        await _client!.CallToolAsync("twincat_close_project", new Dictionary<string, object?> { ["saveFirst"] = false });
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
