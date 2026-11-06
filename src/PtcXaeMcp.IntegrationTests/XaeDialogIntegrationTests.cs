using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// twincat_xae_dialogs / twincat_xae_dialog_answer against the live XAE: MAIN is opened in an editor, its file on
/// disk is touched (mtime only, content unchanged), and the reload prompt the XAE raises is listed and answered
/// through the tools. The XAE may check file changes only when its window is activated; when no prompt appears the
/// test is skipped rather than failed. Whatever happens, a reload prompt left open is answered in finally.
/// <para>
/// A call held by an XAE dialog: the reload prompt never holds a running call (verified live 2026-11-06 — it appears
/// a second or two after the touch, and calls made then are rejected at once with "XAE is blocked by dialog"). The
/// held call is made with the diagnostics hook ptc_test_hold_xae_dialog instead, which opens Tools > Options inside
/// a COM call that returns only once the dialog is closed.
/// </para>
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class XaeDialogIntegrationTests : IAsyncLifetime
{
    private const string ServerUrl = "http://localhost:5010/";
    private const string HoldTool = "ptc_test_hold_xae_dialog";

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

            // While the prompt is open a call is rejected at once and names it — it does not hang.
            var sw = Stopwatch.StartNew();
            string rejected = await Call("twincat_plc_read",
                new Dictionary<string, object?> { ["path"] = FixtureLayout.MainPath, ["kind"] = "POU" });
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed.TotalSeconds:F1} s: {rejected}");
            Assert.StartsWith("[Error] XAE is blocked by dialog", rejected);

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

    private static readonly Regex BlockedId = new(@"^\[Blocked\] XAE dialog '(?<title>[^']*)' \(id (?<id>0x[0-9A-F]+), (?<class>[a-z-]+); buttons: (?<buttons>.*?)\) — ");

    [Trait("Description", "Ein COM-Aufruf, den ein XAE-Dialog festhält (Extras > Optionen über den Diagnose-Hook), antwortet nach wenigen Sekunden [Blocked] mit Dialog-Id und Buttons statt zu hängen; der nächste Aufruf wird mit dem Dialog benannt abgewiesen; nach Abbrechen per xae_dialog_answer läuft der nächste Aufruf wieder, das späte Ergebnis steht im Aktivitätslog.")]
    [SkippableFact(Timeout = 300_000)]
    public async Task ACallHeldByAnXaeDialog_AnswersBlocked_AndTheNextCallRunsAfterTheAnswer()
    {
        LiveXae.GuardReady(_ready);
        var tools = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();
        Skip.IfNot(tools.Contains(HoldTool), "the host runs without --diagnostics (no test hook)");
        string open = await Call("twincat_open_project",
            new Dictionary<string, object?> { ["slnPath"] = _fixtureSln, ["closeCurrentFirst"] = true });
        Assert.False(open.StartsWith("[Error]", StringComparison.Ordinal), open);
        await FixtureLayout.DiscoverPlcRootAsync(_client!);
        var read = new Dictionary<string, object?> { ["path"] = FixtureLayout.MainPath, ["kind"] = "POU" };
        var started = DateTimeOffset.UtcNow;

        string? dialogId = null;
        try
        {
            var sw = Stopwatch.StartNew();
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string held = GetText(await _client!.CallToolAsync(HoldTool,
                new Dictionary<string, object?> { ["confirm"] = "open" }, cancellationToken: guard.Token));
            var took = sw.Elapsed;

            var m = BlockedId.Match(held);
            Assert.True(m.Success, held);
            dialogId = m.Groups["id"].Value;
            Assert.True(took < TimeSpan.FromSeconds(15), $"[Blocked] only after {took.TotalSeconds:F1} s");
            Assert.True(took >= TimeSpan.FromSeconds(2), $"[Blocked] before the grace time: {took.TotalSeconds:F1} s");
            Assert.Contains($"{HoldTool} keeps running in the XAE", held);
            string cancel = Regex.Matches(m.Groups["buttons"].Value, "'([^']+)'").Select(b => b.Groups[1].Value)
                .FirstOrDefault(b => b is "Abbrechen" or "Cancel") ?? throw new Xunit.Sdk.XunitException($"no cancel button: {held}");

            // The lock stays held: the next call is refused at once, naming the dialog and the next step.
            sw.Restart();
            string refused = await Call("twincat_plc_read", read);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"refusal took {sw.Elapsed.TotalSeconds:F1} s");
            Assert.StartsWith($"[Error] An operation is already in progress: {HoldTool} waits for the XAE dialog", refused);
            Assert.Contains(dialogId, refused);
            Assert.Contains("twincat_xae_dialog_answer", refused);

            string dialogs = await Call("twincat_xae_dialogs");
            Assert.Contains($"- id {dialogId}:", dialogs);
            Assert.Contains($"Waiting for a dialog: {HoldTool}", dialogs);

            string answer = await Call("twincat_xae_dialog_answer",
                new Dictionary<string, object?> { ["id"] = dialogId, ["button"] = cancel });
            Assert.Contains("the dialog closed", answer);
            dialogId = null;

            // The held call returns, its late result is logged and the lock is released: the next call runs.
            string next = await Poll.UntilAsync(() => Call("twincat_plc_read", read),
                r => !r.StartsWith("[Error] An operation is already in progress", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            Assert.False(next.StartsWith("[Error]", StringComparison.Ordinal), next);
            Assert.True(LateResultLogged(started), "no '(late result)' entry in the activity log");
        }
        finally
        {
            // Never leave the Options dialog open: it would block every later test.
            if (dialogId is not null)
            {
                foreach (string caption in new[] { "Abbrechen", "Cancel" })
                {
                    string r = await Call("twincat_xae_dialog_answer",
                        new Dictionary<string, object?> { ["id"] = dialogId, ["button"] = caption });
                    if (r.Contains("the dialog closed", StringComparison.Ordinal)) break;
                }
            }
        }
    }

    // The host writes every entry to %LOCALAPPDATA%\ptc-xae-mcp\activity-*.log (one JSON object per line).
    private static bool LateResultLogged(DateTimeOffset since)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp");
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        do
        {
            if (Directory.Exists(dir))
                foreach (var file in Directory.EnumerateFiles(dir, "activity-*.log"))
                {
                    if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;
                    string text;
                    try { text = File.ReadAllText(file); }
                    catch (IOException) { continue; } // the host may be mid-append
                    if (text.Contains($"{HoldTool} (late result)", StringComparison.Ordinal)) return true;
                }
            Thread.Sleep(500);
        } while (DateTime.UtcNow < until);
        return false;
    }

    private static string GetText(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
