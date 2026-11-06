using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_xae_dialogs / twincat_xae_dialog_answer against the live XAE: MAIN is opened in an editor, its file on
/// disk is touched (mtime only, content unchanged), and the reload prompt the XAE raises is listed and answered
/// through the tools. The XAE may check file changes only when its window is activated; when no prompt appears the
/// test is skipped rather than failed. Whatever happens, a reload prompt left open is answered in finally.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class XaeDialogIntegrationTests : IAsyncLifetime
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

    private async Task<string> Call(string tool, Dictionary<string, object?>? args = null) =>
        GetText(await _client!.CallToolAsync(tool, args ?? new Dictionary<string, object?>()));

    private string MainFile =>
        Path.Combine(Path.GetDirectoryName(_fixtureSln)!, FixtureLayout.SolutionName, FixtureLayout.PlcName, "POUs", "MAIN.TcPOU");

    // "- id 0x1A2B: 'Title' — known-safe, blocking" followed by its "buttons: 'Ja', 'Nein'" line.
    private static readonly Regex SafeBlocking = new(@"- id (?<id>0x[0-9A-F]+): '(?<title>[^']*)' — known-safe, blocking\r?\n(?:\s+text: .*\r?\n)*\s+buttons: (?<buttons>.*)");

    private async Task<Match> WaitForReloadPromptAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var match = SafeBlocking.Match(await Call("twincat_xae_dialogs"));
            if (match.Success || DateTime.UtcNow > until) return match;
            await Task.Delay(500);
        }
    }

    private static string? Button(Match prompt, params string[] captions) =>
        Regex.Matches(prompt.Groups["buttons"].Value, "'([^']+)'").Select(m => m.Groups[1].Value)
            .FirstOrDefault(b => captions.Contains(b, StringComparer.OrdinalIgnoreCase));

    [Trait("Description", "MAIN im Editor öffnen, mtime der Datei auf der Platte ändern → Neuladen-Rückfrage erscheint in xae_dialogs (known-safe, blocking) und wird per xae_dialog_answer geschlossen.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ReloadPrompt_IsListed_AndAnsweredThroughTheTools()
    {
        LiveXae.GuardReady(_ready);
        string open = await Call("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
        await FixtureLayout.DiscoverPlcRootAsync(_client!);
        Skip.IfNot(File.Exists(MainFile), $"MAIN's file not found at {MainFile}");

        try
        {
            string opened = await Call("twincat_open_object", new Dictionary<string, object?> { ["treePath"] = FixtureLayout.MainPath });
            Assert.False(opened.StartsWith("[Error]", StringComparison.Ordinal), opened);

            File.SetLastWriteTimeUtc(MainFile, DateTime.UtcNow.AddMinutes(1));
            var prompt = await WaitForReloadPromptAsync(TimeSpan.FromSeconds(20));
            Skip.IfNot(prompt.Success, "The XAE raised no reload prompt while in the background (it checks on activation).");

            string? yes = Button(prompt, "Ja", "Yes");
            Assert.True(yes is not null, $"no Ja/Yes button: {prompt.Value}");
            string answer = await Call("twincat_xae_dialog_answer",
                new Dictionary<string, object?> { ["id"] = prompt.Groups["id"].Value, ["button"] = yes });

            Assert.Contains("the dialog closed", answer);
            Assert.False((await WaitForReloadPromptAsync(TimeSpan.Zero)).Success, "a reload prompt is still open");
            Assert.False((await Call("twincat_plc_read",
                new Dictionary<string, object?> { ["path"] = FixtureLayout.MainPath, ["kind"] = "POU" })).StartsWith("[Error]", StringComparison.Ordinal));
        }
        finally
        {
            // Never leave a reload prompt behind: it would block every later test.
            var left = await WaitForReloadPromptAsync(TimeSpan.Zero);
            if (left.Success && Button(left, "Ja", "Yes", "Nein", "No") is { } any)
                await Call("twincat_xae_dialog_answer", new Dictionary<string, object?> { ["id"] = left.Groups["id"].Value, ["button"] = any });
            await Call("twincat_close_object", new Dictionary<string, object?> { ["treePath"] = FixtureLayout.MainPath });
        }
    }

    private static string GetText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
