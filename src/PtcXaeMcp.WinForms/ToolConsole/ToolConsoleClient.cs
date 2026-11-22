using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PtcXaeMcp.WinForms.Localization;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.ToolConsole;

/// <summary>Outcome of one console tool call.</summary>
/// <param name="IsError">The tool reported an error, or the call failed (see <paramref name="Text"/>).</param>
/// <param name="Text">Concatenated text content, or the error message.</param>
/// <param name="RawResponseJson">The full <c>CallToolResult</c> as JSON (empty when the call never got a response).</param>
/// <param name="Duration">Wall-clock time of the call.</param>
/// <param name="NotReachable">True when the server could not be reached; the request was never sent.</param>
/// <param name="OutcomeUnknown">True when the request was sent but no answer arrived; the tool may have run.</param>
internal sealed record ToolCallOutcome(
    bool IsError, string Text, string RawResponseJson, TimeSpan Duration,
    bool NotReachable = false, bool OutcomeUnknown = false);

/// <summary>
/// In-process MCP client for the tool console (issue #271). Connects lazily to this app's own
/// server over HTTP, so a manual call takes the same path as a call from an AI client: token
/// check, dispatcher lock, activity log. All members are async; awaiting from the UI thread is fine.
/// </summary>
internal sealed class ToolConsoleClient : IAsyncDisposable
{
    /// <summary>Client name; the session tracker does not count a session with this name as a client.</summary>
    public const string ClientName = McpSessionRegistry.ConsoleClientName;

    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private McpClient? _client;
    private volatile bool _disposed;

    public ToolConsoleClient(int port) => _endpoint = new Uri($"http://localhost:{port}/");

    /// <summary>Reads <c>tools/list</c>; reconnects once when the cached connection is stale.</summary>
    public async Task<IReadOnlyList<ToolSummary>> ListToolsAsync(CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            var client = await ConnectAsync(ct).ConfigureAwait(false);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                return tools
                    .Select(t => new ToolSummary(
                        t.Name,
                        t.Description ?? string.Empty,
                        t.ProtocolTool.InputSchema.Clone(),
                        t.ProtocolTool.Annotations?.ReadOnlyHint,
                        t.ProtocolTool.Annotations?.DestructiveHint))
                    .ToList();
            }
            catch (Exception ex) when (attempt == 0 && IsConnectionProblem(ex, ct))
            {
                await ResetAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Calls a tool. Never throws for server problems: they come back as an error outcome.
    /// A failure before the request was sent is "not reachable"; a failure after it was sent is
    /// "outcome unknown" (the tool may have run). Never retried: a write must not run twice.
    /// </summary>
    public async Task<ToolCallOutcome> CallAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        McpClient client;
        try
        {
            client = await ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new ToolCallOutcome(true, Loc.Get("toolconsole.client.closed"), string.Empty, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ToolCallOutcome(true, Loc.Get("toolconsole.client.cancelledbeforesend"), string.Empty, sw.Elapsed);
        }
        catch (Exception ex)
        {
            // Nothing was sent yet: "try again" is safe.
            return new ToolCallOutcome(true, NotReachableMessage(ex), string.Empty, sw.Elapsed, NotReachable: true);
        }

        try
        {
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in arguments)
                args[p.Key] = p.Value is null
                    ? JsonSerializer.SerializeToElement<object?>(null)
                    : JsonSerializer.SerializeToElement(p.Value);

            var result = await client.CallToolAsync(
                new CallToolRequestParams { Name = tool, Arguments = args }, ct).ConfigureAwait(false);

            string raw = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
            return new ToolCallOutcome(result.IsError == true, ContentText(result), raw, sw.Elapsed);
        }
        catch (McpProtocolException ex)
        {
            // A JSON-RPC error (unknown tool, invalid params): the server answered, the connection is fine.
            return new ToolCallOutcome(true, ex.Message, string.Empty, sw.Elapsed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            await ResetAsync().ConfigureAwait(false);
            return new ToolCallOutcome(true,
                Loc.Get("toolconsole.client.cancelledaftersend"),
                string.Empty, sw.Elapsed, OutcomeUnknown: true);
        }
        catch (Exception ex)
        {
            await ResetAsync().ConfigureAwait(false);
            return new ToolCallOutcome(true,
                Loc.Format("toolconsole.client.outcomeunknown", ex.Message),
                string.Empty, sw.Elapsed, OutcomeUnknown: true);
        }
    }

    /// <summary>User-facing text for a connect/list failure.</summary>
    public string NotReachableMessage(Exception ex) =>
        Loc.Format("toolconsole.client.notreachable", _endpoint, ex.Message);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ResetAsync().ConfigureAwait(false);
        // The semaphore is deliberately not disposed: a call still in flight may release it.
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A client whose session already ended (server restarted) is replaced.
            if (_client is { Completion.IsCompleted: true })
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            if (_client is not null) return _client;

            var options = new HttpClientTransportOptions { Endpoint = _endpoint, Name = ClientName };
            string? token = Environment.GetEnvironmentVariable(LocalhostGuard.TokenEnvVar);
            if (!string.IsNullOrEmpty(token))
                options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };

            // HttpClient's default timeout of 100 s would cut long tool calls (builds); the
            // console's own CancellationToken bounds every call instead.
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var transport = new HttpClientTransport(options, http, loggerFactory: null, ownsHttpClient: true);
            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = ClientName, Version = McpBridge.ThisAssemblyVersion() } },
                cancellationToken: ct).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ResetAsync()
    {
        try { await _connectLock.WaitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; }
        try
        {
            var c = _client;
            _client = null;
            if (c is not null)
            {
                try { await c.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* already broken: nothing left to clean up */ }
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private static bool IsConnectionProblem(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is not McpProtocolException && ex is not ObjectDisposedException;

    private static string ContentText(CallToolResult result)
    {
        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(block is TextContentBlock t ? t.Text : $"[{block.Type} content]");
        }
        return sb.ToString();
    }
}
