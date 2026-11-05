using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_plc_move, live against the neutral fixture (<see cref="FixtureLayout"/>): a time-stamped FB with a METHOD is
/// moved into a new folder, then the METHOD into a folder inside the FB. Each move must keep the text (revision) of the
/// FB and the METHOD, and the old path must be gone. Everything created is deleted in a finally block.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class PlcMoveIntegrationTests : IAsyncLifetime
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

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private async Task OpenFixtureAsync()
    {
        var open = await _client!.CallToolAsync("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.IsError == true, GetText(open));
        await PlcProjectReadiness.AwaitAsync(_client!);
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private async Task<string> CallAsync(string tool, params (string Key, object? Value)[] args)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in args) dict[key] = value;
        var result = await _client!.CallToolAsync(tool, dict);
        string text = GetText(result);
        return result.IsError == true && !text.StartsWith("[Error]", StringComparison.Ordinal) ? "[Error] (protocol) " + text : text;
    }

    private async Task<string> OkAsync(string tool, params (string Key, object? Value)[] args)
    {
        string text = await CallAsync(tool, args);
        Assert.False(text.StartsWith("[Error]", StringComparison.Ordinal), $"{tool} failed: {text}");
        return text;
    }

    private async Task TryDeleteAsync(string path, string kind, bool recursive = false)
    {
        try { await CallAsync("twincat_plc_delete", ("path", path), ("kind", kind), ("recursive", recursive)); }
        catch { /* best-effort teardown */ }
    }

    private static string Stamp() => DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> RevisionAsync(string path, string kind)
    {
        string read = await OkAsync("twincat_plc_read", ("path", path), ("kind", kind));
        var m = Regex.Match(read, "Revision: ([0-9a-f]{16})");
        Assert.True(m.Success, $"no revision in: {read}");
        return m.Groups[1].Value;
    }

    [Trait("Description", "plc_move: FB mit METHOD in neuen Ordner verschieben, dann die METHOD in einen Ordner im FB; Revisionen gleich, alter Pfad weg; METHOD in fremden POU wird abgelehnt.")]
    [SkippableFact(Timeout = 600_000)]
    public async Task Move_FbIntoFolder_ThenMethodIntoFolderOfTheFb_KeepsTheText()
    {
        SkipIfNotReady();
        await OpenFixtureAsync();

        string stamp = Stamp();
        string fb = $"FB_Move_{stamp}";
        string folder = $"Fld_Move_{stamp}";
        string fbPath = $"{FixtureLayout.PousFolder}^{fb}";
        string folderPath = $"{FixtureLayout.PousFolder}^{folder}";
        string movedFb = $"{folderPath}^{fb}";

        try
        {
            await OkAsync("twincat_plc_create", ("kind", "POU"), ("subtype", "FUNCTION_BLOCK"),
                ("parentPath", FixtureLayout.PousFolder), ("name", fb),
                ("declaration", $"FUNCTION_BLOCK {fb}\nVAR_INPUT\n\tbGo : BOOL;\nEND_VAR\nVAR\n\tnCount : INT;\nEND_VAR"),
                ("implementation", "nCount := nCount + 1;"));
            await OkAsync("twincat_plc_create", ("kind", "MEMBER"), ("subtype", "METHOD"), ("parentPath", fbPath),
                ("name", "Run"), ("returnType", "BOOL"), ("implementation", "Run := bGo;"));
            await OkAsync("twincat_plc_create", ("kind", "FOLDER"), ("parentPath", FixtureLayout.PousFolder), ("name", folder));

            string fbRevision = await RevisionAsync(fbPath, "POU");
            string runRevision = await RevisionAsync($"{fbPath}^Run", "MEMBER");

            // A refusal changes nothing and costs no logout: the target must be a folder.
            string refused = await CallAsync("twincat_plc_move", ("path", fbPath), ("kind", "POU"), ("targetFolderPath", FixtureLayout.MainPath));
            Assert.StartsWith("[Error]", refused);
            Assert.Contains("is not a FOLDER", refused);

            // 1. The FB into the new folder.
            string moved = await OkAsync("twincat_plc_move", ("path", fbPath), ("kind", "POU"), ("targetFolderPath", folderPath));
            Assert.Contains($"Moved POU '{fbPath}' to '{movedFb}'", moved);
            Assert.Contains("verified by read-back", moved);
            Assert.StartsWith("[Error]", await CallAsync("twincat_plc_read", ("path", fbPath), ("kind", "POU")));
            Assert.Equal(fbRevision, await RevisionAsync(movedFb, "POU"));
            Assert.Equal(runRevision, await RevisionAsync($"{movedFb}^Run", "MEMBER"));

            // 2. The METHOD into a folder inside the FB.
            await OkAsync("twincat_plc_create", ("kind", "FOLDER"), ("parentPath", movedFb), ("name", "Methods"));
            string memberMoved = await OkAsync("twincat_plc_move", ("path", $"{movedFb}^Run"), ("kind", "MEMBER"),
                ("targetFolderPath", $"{movedFb}^Methods"));
            Assert.Contains($"Moved MEMBER '{movedFb}^Run' to '{movedFb}^Methods^Run'", memberMoved);
            Assert.StartsWith("[Error]", await CallAsync("twincat_plc_read", ("path", $"{movedFb}^Run"), ("kind", "MEMBER")));
            Assert.Equal(runRevision, await RevisionAsync($"{movedFb}^Methods^Run", "MEMBER"));
            Assert.Equal(fbRevision, await RevisionAsync(movedFb, "POU"));
            Assert.Contains($"{movedFb}^Methods^Run", await OkAsync("twincat_plc_list", ("parentPath", movedFb)));

            // A member does not leave its POU.
            string otherPou = await CallAsync("twincat_plc_move", ("path", $"{movedFb}^Methods^Run"), ("kind", "MEMBER"),
                ("targetFolderPath", folderPath));
            Assert.StartsWith("[Error]", otherPou);
            Assert.Contains("moves only inside its own POU/INTERFACE", otherPou);
        }
        finally
        {
            await TryDeleteAsync(folderPath, "FOLDER", recursive: true);
            await TryDeleteAsync(fbPath, "POU");
        }
    }
}
