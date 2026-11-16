using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Mcp;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The human confirmation through a real MCP server and client over HTTP (a loopback port of its own): the question
/// travels as an elicitation request and the answer decides whether the tool body runs. Every call has a deadline,
/// so a broken exchange fails the test instead of hanging it.
/// </summary>
public sealed class HumanConfirmationEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const string Remote = "10.1.1.1.1.1";
    private const string Local = "127.0.0.1.1.1";

    private WebApplication _app = null!;
    private string _url = null!;
    private string? _projectTarget = Remote;
    private ConfirmMode? _mode = ConfirmMode.Remote;
    private readonly ConfirmationRegistry _registry = new();
    private readonly string _receiptsDir = Path.Combine(Path.GetTempPath(), "e2e_receipts_" + Guid.NewGuid().ToString("N"));

    [McpServerToolType]
    internal sealed class E2eTools
    {
        public static int Runs;

        [McpServerTool(Name = "twincat_activate_config", ReadOnly = false, Destructive = true)]
        public string Activate() => "activated " + Interlocked.Increment(ref Runs);

        [McpServerTool(Name = "twincat_get_status", ReadOnly = true)]
        public string Status() => "status";
    }

    public async Task InitializeAsync()
    {
        var confirmation = new HumanConfirmation(
            () => new AccessPolicy(AccessLevel.Runtime, AccessPolicy.ParseList(null), null, "test", Confirm: _mode),
            () => _projectTarget, () => [], _registry, null, () => [Local]);

        var receipts = new RequestReceipts(new ReceiptStore(Path.Combine(_receiptsDir, ReceiptStore.FileName)), () => _projectTarget);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<E2eTools>()
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(receipts.Filter);
                filters.AddCallToolFilter(confirmation.Filter);
            });
        _app = builder.Build();
        _app.MapMcp("/");
        await _app.StartAsync();
        _url = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        try { Directory.Delete(_receiptsDir, recursive: true); } catch (IOException) { }
    }

    private async Task<McpClient> ConnectAsync(Func<ElicitRequestParams, ValueTask<ElicitResult>>? onQuestion)
    {
        var options = new McpClientOptions();
        if (onQuestion is not null)
            options.Handlers = new McpClientHandlers { ElicitationHandler = (request, _) => onQuestion(request!) };
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(_url) });
        using var cts = new CancellationTokenSource(Deadline);
        return await McpClient.CreateAsync(transport, options, cancellationToken: cts.Token);
    }

    private static async Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?>? arguments = null)
    {
        using var cts = new CancellationTokenSource(Deadline);
        return await client.CallToolAsync(tool, arguments, cancellationToken: cts.Token);
    }

    private static string TextOf(CallToolResult result) => ((TextContentBlock)Assert.Single(result.Content)).Text;

    private static ElicitResult Answer(string action, bool? confirmed = null) => new()
    {
        Action = action,
        Content = confirmed is { } c ? new Dictionary<string, JsonElement> { ["confirmed"] = JsonSerializer.SerializeToElement(c) } : null,
    };

    [Fact]
    public async Task AnAcceptedQuestion_RunsTheTool_AndTheQuestionNamesWhatItDoes()
    {
        int before = E2eTools.Runs;
        var questions = new List<ElicitRequestParams>();
        await using var client = await ConnectAsync(q => { questions.Add(q); return ValueTask.FromResult(Answer("accept", true)); });

        var result = await CallAsync(client, "twincat_activate_config");

        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith("activated", TextOf(result));
        Assert.Equal(before + 1, E2eTools.Runs);
        var question = Assert.Single(questions);
        Assert.Contains("twincat_activate_config", question.Message);
        Assert.Contains(Remote, question.Message);
        Assert.IsType<ElicitRequestParams.BooleanSchema>(question.RequestedSchema!.Properties["confirmed"]);
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    public async Task ARefusedQuestion_NeverRunsTheTool(string action, bool? confirmed)
    {
        int before = E2eTools.Runs;
        await using var client = await ConnectAsync(_ => ValueTask.FromResult(Answer(action, confirmed)));

        var result = await CallAsync(client, "twincat_activate_config");

        Assert.True(result.IsError);
        Assert.Equal("[Error] Declined by the human. Nothing was done.", TextOf(result));
        Assert.Equal(before, E2eTools.Runs);
    }

    [Fact]
    public async Task ALocalTarget_RunsWithoutAQuestion_UnderTheDefaultMode()
    {
        _projectTarget = Local;
        int before = E2eTools.Runs;
        int asked = 0;
        await using var client = await ConnectAsync(_ => { asked++; return ValueTask.FromResult(Answer("decline")); });

        var result = await CallAsync(client, "twincat_activate_config");

        Assert.StartsWith("activated", TextOf(result));
        Assert.Equal(0, asked);
        Assert.Equal(before + 1, E2eTools.Runs);
    }

    [Fact]
    public async Task AReadingCall_IsNeverAsked()
    {
        int asked = 0;
        await using var client = await ConnectAsync(_ => { asked++; return ValueTask.FromResult(Answer("decline")); });

        Assert.Equal("status", TextOf(await CallAsync(client, "twincat_get_status")));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AClientWithoutElicitation_GetsAToken_AHumanReleases_AndTheRepeatedCallRunsOnce()
    {
        int before = E2eTools.Runs;
        await using var client = await ConnectAsync(null);

        var first = await CallAsync(client, "twincat_activate_config");

        Assert.StartsWith("[Confirm] twincat_activate_config", TextOf(first));
        Assert.Equal(before, E2eTools.Runs);
        var entry = Assert.Single(_registry.Snapshot());
        Assert.Contains($"confirmationToken=\"{entry.Token}\"", TextOf(first));

        // Not released: still nothing runs.
        var early = await CallAsync(client, "twincat_activate_config", new() { ["confirmationToken"] = entry.Token });
        Assert.Contains("is not released yet", TextOf(early));
        Assert.Equal(before, E2eTools.Runs);

        // Only the window releases it.
        Assert.True(_registry.Release(entry.Code));
        var released = await CallAsync(client, "twincat_activate_config", new() { ["confirmationToken"] = entry.Token });
        Assert.StartsWith("activated", TextOf(released));
        Assert.Equal(before + 1, E2eTools.Runs);

        var reused = await CallAsync(client, "twincat_activate_config", new() { ["confirmationToken"] = entry.Token });
        Assert.StartsWith("[Confirm]", TextOf(reused));
        Assert.Equal(before + 1, E2eTools.Runs);
    }

    [Fact]
    public async Task WithTheModeOff_NothingIsAsked()
    {
        _mode = ConfirmMode.Off;
        int before = E2eTools.Runs;
        int asked = 0;
        await using var client = await ConnectAsync(_ => { asked++; return ValueTask.FromResult(Answer("decline")); });

        Assert.StartsWith("activated", TextOf(await CallAsync(client, "twincat_activate_config")));
        Assert.Equal(0, asked);
        Assert.Equal(before + 1, E2eTools.Runs);
    }

    // ---- request keys through the real filters -------------------------------------------------------------------------

    private static string Key() => Guid.NewGuid().ToString();

    [Fact]
    public async Task TheSameRequestKey_RunsOnce_AndTheRepeatIsNotAskedAgain()
    {
        _mode = ConfirmMode.Off;
        int before = E2eTools.Runs;
        await using var client = await ConnectAsync(null);
        string key = Key();

        var first = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });
        var again = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });

        Assert.Equal(before + 1, E2eTools.Runs);
        var blocks = again.Content.OfType<TextContentBlock>().Select(c => c.Text).ToArray();
        Assert.Equal(TextOf(first), blocks[0]);
        Assert.StartsWith("[Receipt] Repeated request", blocks[1]);
    }

    [Fact]
    public async Task ARequestKeyWithTheTokenPath_IsFreeUntilAHumanReleased_ThenRunsOnce()
    {
        int before = E2eTools.Runs;
        await using var client = await ConnectAsync(null);
        string key = Key();

        var asked = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });
        Assert.StartsWith("[Confirm]", TextOf(asked));
        var entry = Assert.Single(_registry.Snapshot());
        Assert.Equal(before, E2eTools.Runs);

        Assert.True(_registry.Release(entry.Code));
        var ran = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key, ["confirmationToken"] = entry.Token });
        Assert.StartsWith("activated", TextOf(ran));
        Assert.Equal(before + 1, E2eTools.Runs);

        // The token is used up, but the request is answered: the repeat is not asked and does not run.
        var repeat = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });
        Assert.Equal(before + 1, E2eTools.Runs);
        Assert.StartsWith("[Receipt] Repeated request", repeat.Content.OfType<TextContentBlock>().Last().Text);
    }

    [Fact]
    public async Task ADeclinedQuestion_LeavesTheRequestKeyFree()
    {
        int before = E2eTools.Runs;
        bool accept = false;
        await using var client = await ConnectAsync(_ => ValueTask.FromResult(accept ? Answer("accept", true) : Answer("decline")));
        string key = Key();

        var declined = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });
        Assert.True(declined.IsError);
        Assert.Equal(before, E2eTools.Runs);

        accept = true;
        var ran = await CallAsync(client, "twincat_activate_config", new() { ["requestKey"] = key });
        Assert.StartsWith("activated", TextOf(ran));
        Assert.Equal(before + 1, E2eTools.Runs);
    }
}
