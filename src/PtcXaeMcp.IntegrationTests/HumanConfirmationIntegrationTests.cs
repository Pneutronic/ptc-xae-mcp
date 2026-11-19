using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// The human confirmation against the live host: with the default mode <c>remote</c> nothing is asked for this machine
/// or the User Mode Runtime (the live suite runs unasked), a target on another system is asked about through MCP
/// elicitation, and a client without elicitation gets a token. Tests that need the mode <c>all</c> skip unless the host
/// was started with <c>PTC_XAE_MCP_CONFIRM=all</c>. Every question is answered by the test client; nothing waits for a person.
/// </summary>
[Collection("Stage XAE")]
[Trait("Stage", "XAE")]
public class HumanConfirmationIntegrationTests : IAsyncLifetime
{
    private bool _ready;

    public Task InitializeAsync()
    {
        var fixture = LiveXae.FixtureSln();
        if (fixture is null) { LiveXae.Announce(false, string.Empty); return Task.CompletedTask; }
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            _ready = probe.GetAsync(LiveXae.ServerUrl + "health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { _ready = false; }
        LiveXae.Announce(_ready, fixture);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void SkipIfNotReady() => LiveXae.GuardReady(_ready);

    private static async Task<McpClient> ConnectAsync(Func<ElicitRequestParams, ElicitResult>? onQuestion)
    {
        var options = new McpClientOptions();
        if (onQuestion is not null)
            options.Handlers = new McpClientHandlers { ElicitationHandler = (request, _) => ValueTask.FromResult(onQuestion(request!)) };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(LiveXae.ServerUrl),
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        });
        return await McpClient.CreateAsync(transport, options, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    }

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private static ElicitResult Answer(string action, bool confirmed = false) => new()
    {
        Action = action,
        Content = action == "accept" ? new Dictionary<string, JsonElement> { ["confirmed"] = JsonSerializer.SerializeToElement(confirmed) } : null,
    };

    private static async Task<string> ModeAsync(McpClient client)
    {
        string info = await CallAsync(client, "twincat_get_client_info");
        var match = Regex.Match(info, @"Human confirmation: (\w+)");
        Assert.True(match.Success, info);
        return match.Groups[1].Value;
    }

    // An Always-class tool on a system that is not this machine; nothing answers there, so nothing can happen.
    private const string Elsewhere = "10.99.99.99.1.1";

    [Trait("Description", "twincat_doctor und twincat_get_client_info melden den Bestätigungsmodus und ob der Client in-band gefragt werden kann.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task DoctorAndClientInfo_ReportTheModeAndTheClientsAbility()
    {
        SkipIfNotReady();
        await using var capable = await ConnectAsync(_ => Answer("decline"));
        await using var plain = await ConnectAsync(null);

        string withElicitation = await CallAsync(capable, "twincat_get_client_info");
        string without = await CallAsync(plain, "twincat_get_client_info");
        string doctor = await CallAsync(capable, "twincat_doctor");

        Assert.Contains("Human confirmation: ", withElicitation);
        Assert.Contains("is asked in its own window", withElicitation);
        Assert.Contains("does not support MCP elicitation", without);
        Assert.Contains("Human confirmation: ", doctor);
        Assert.Contains("Access policy: access ", await CallAsync(capable, "twincat_get_status"));
        Assert.Contains("human confirmation: ", await CallAsync(capable, "twincat_get_status"));
    }

    [Trait("Description", "Default 'remote': eine Always-Klasse-Aktion auf dem Ziel dieser Maschine (UmRT) wird nicht gefragt; die Live-Suite läuft ohne Rückfrage.")]
    [SkippableFact(Timeout = 120_000)]
    public async Task Remote_TheLocalRuntimeIsNotAsked()
    {
        SkipIfNotReady();
        string netId = LiveXae.RuntimeNetId ?? string.Empty;
        Skip.If(netId.Length == 0, "no runtime NetId set (" + LiveXae.RuntimeNetIdEnvVar + ")");
        Skip.IfNot(string.Equals(AdsStateProbe.TryReadState(netId), "Run", StringComparison.OrdinalIgnoreCase),
            "the runtime is not in Run: twincat_set_system_mode(run) would not be a no-op");

        int asked = 0;
        await using var client = await ConnectAsync(_ => { asked++; return Answer("decline"); });
        Skip.If(await ModeAsync(client) != "remote", "the host does not run in the default mode");

        string text = await CallAsync(client, "twincat_set_system_mode", new() { ["amsNetId"] = netId, ["mode"] = "run" });

        Assert.Equal(0, asked);
        Assert.DoesNotContain("[Confirm]", text);
        Assert.DoesNotContain("Declined by the human", text);
        Assert.Contains("already in Run mode", text);
    }

    [Trait("Description", "Default 'remote': eine Aktion auf einem anderen System wird per MCP-Elicitation gefragt; ein 'decline' nach menschlicher Bedenkzeit tut nichts, ein sofortiges 'decline' (kein Dialog gezeigt, #51) führt zum Freigabe-Token, 'accept' erreicht den Tool-Rumpf.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task Remote_AnotherSystemIsAsked_AndTheAnswerDecides()
    {
        SkipIfNotReady();
        var questions = new List<ElicitRequestParams>();
        string action = "decline";
        // A person needs time to read the question; a "No" faster than the host's MinHumanAnswer (1 s) is taken as a
        // client that declined without showing anything (#51).
        int thinkMs = 1500;
        await using var client = await ConnectAsync(q =>
        {
            questions.Add(q);
            if (thinkMs > 0) Thread.Sleep(thinkMs);
            return Answer(action, confirmed: action == "accept");
        });
        Skip.If(await ModeAsync(client) == "off", "the host runs with the confirmation off");

        string declined = await CallAsync(client, "twincat_set_system_mode", new() { ["amsNetId"] = Elsewhere, ["mode"] = "config" });

        Assert.Equal("[Error] Declined by the human. Nothing was done.", declined);
        var question = Assert.Single(questions);
        Assert.Contains("twincat_set_system_mode", question.Message);
        Assert.Contains(Elsewhere, question.Message);
        Assert.Contains("mode=config", question.Message);

        thinkMs = 0;
        string instant = await CallAsync(client, "twincat_set_system_mode", new() { ["amsNetId"] = Elsewhere, ["mode"] = "config" });

        Assert.Equal(2, questions.Count);
        Assert.StartsWith("[Confirm]", instant);
        Assert.DoesNotContain("Declined by the human", instant);

        action = "accept";
        string accepted = await CallAsync(client, "twincat_set_system_mode", new() { ["amsNetId"] = Elsewhere, ["mode"] = "config" });

        Assert.Equal(3, questions.Count);
        Assert.DoesNotContain("Declined by the human", accepted);
        Assert.DoesNotContain("[Confirm]", accepted);
        Assert.StartsWith("[Error]", accepted); // the body ran and found nothing to talk to
        Assert.Contains("does not answer over ADS", accepted);
    }

    [Trait("Description", "Ein Client ohne Elicitation bekommt [Confirm] samt Token; ohne Freigabe im Fenster läuft nichts, auch nicht mit dem Token.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task Remote_AClientWithoutElicitation_GetsATokenNobodyCanUseWithoutTheWindow()
    {
        SkipIfNotReady();
        await using var client = await ConnectAsync(null);
        Skip.If(await ModeAsync(client) == "off", "the host runs with the confirmation off");
        var args = new Dictionary<string, object?> { ["amsNetId"] = Elsewhere, ["mode"] = "config" };

        string first = await CallAsync(client, "twincat_set_system_mode", args);

        Assert.StartsWith("[Confirm] twincat_set_system_mode", first);
        var token = Regex.Match(first, "confirmationToken=\"([0-9a-f]{32})\"");
        Assert.True(token.Success, first);

        args["confirmationToken"] = token.Groups[1].Value;
        string early = await CallAsync(client, "twincat_set_system_mode", args);
        Assert.Contains("is not released yet", early);
        Assert.DoesNotContain("does not answer over ADS", early);
    }

    [Trait("Description", "Modus 'all' (Host mit PTC_XAE_MCP_CONFIRM=all): auch die Lebenszyklus-Aktion auf dem lokalen UmRT wird gefragt; Antwort entscheidet, die Live-Suite würde hier Rückfragen auslösen.")]
    [SkippableFact(Timeout = 180_000)]
    public async Task All_ALocalLifecycleActionIsAsked()
    {
        SkipIfNotReady();
        var questions = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(q => { questions.Add(q); return Answer("decline"); });
        Skip.IfNot(await ModeAsync(client) == "all", "the host was not started with PTC_XAE_MCP_CONFIRM=all");

        // twincat_umr_set_mode acts on this machine's own User Mode Runtime and answers an error when none was started here.
        string declined = await CallAsync(client, "twincat_umr_set_mode", new() { ["mode"] = "run" });

        Assert.Equal("[Error] Declined by the human. Nothing was done.", declined);
        Assert.Contains("the local system", Assert.Single(questions).Message);

        await using var accepting = await ConnectAsync(_ => Answer("accept", confirmed: true));
        string accepted = await CallAsync(accepting, "twincat_umr_set_mode", new() { ["mode"] = "run" });
        Assert.DoesNotContain("Declined by the human", accepted);
        Assert.DoesNotContain("[Confirm]", accepted);

        // A call that only writes a variable on this machine is not in the lifecycle class: not asked even under 'all'.
        Assert.Single(questions);
    }
}
