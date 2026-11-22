using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>When a human is asked before an action with effect on a target system. Ordered from least to most strict.</summary>
internal enum ConfirmMode
{
    /// <summary>Never asked.</summary>
    Off = 0,
    /// <summary>Asked only when the target is not this machine (its AMS router or a User Mode Runtime on it).</summary>
    Remote = 1,
    /// <summary>As <see cref="Remote"/>, and the actions of class <see cref="ConfirmClass.Always"/> on this machine too.</summary>
    All = 2,
}

/// <summary>How a tool takes part in the human confirmation.</summary>
internal enum ConfirmClass
{
    /// <summary>Never asked (reads, project changes, and actions a human already confirms in the host window).</summary>
    Never = 0,
    /// <summary>Asked when the target is not this machine.</summary>
    Runtime = 1,
    /// <summary>Asked for every target when the mode is <see cref="ConfirmMode.All"/>, else when the target is not this machine.</summary>
    Always = 2,
}

/// <summary>What a pending confirmation request of the token path has become.</summary>
internal enum ConfirmationState { Pending, Released, Denied, Used, Expired }

/// <summary>One request of the token path: what the agent was refused until a human releases it in the window.</summary>
internal sealed record ConfirmationEntry(
    string Code, string Token, string Tool, string ArgumentHash, string? NetId, string Summary,
    DateTime CreatedUtc, DateTime ExpiresUtc, ConfirmationState State);

/// <summary>The answer to a redeemed token.</summary>
internal enum RedeemResult { Ok, Unknown, Expired, NotReleased, Denied, Used, Mismatch }

/// <summary>
/// The tokens of the human confirmation for a client that cannot be asked in-band (no MCP elicitation). A token is
/// issued with a refused call, bound to the tool, the arguments and the target, and only a human can release it: the
/// <see cref="Release"/> and <see cref="Deny"/> methods are called by the Approvals view alone, no tool reaches them.
/// A released token is valid until it expires (<see cref="TtlSeconds"/> after it was issued) and works once.
/// </summary>
internal sealed class ConfirmationRegistry(Func<DateTime>? clock = null)
{
    public const int TtlSeconds = 180;
    private const int Capacity = 100;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private readonly List<ConfirmationEntry> _entries = [];

    /// <summary>Something changed (issued, released, denied, used, expired). Raised on the calling thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// A new request for the call, or the live one when the same call asks again before the token ran out (the list
    /// does not fill up with an agent that repeats itself).
    /// </summary>
    public ConfirmationEntry Issue(string tool, string argumentHash, string? netId, string summary)
    {
        ConfirmationEntry entry;
        lock (_gate)
        {
            ExpireDue();
            var live = _entries.FirstOrDefault(e => e.State is ConfirmationState.Pending or ConfirmationState.Released
                && e.Tool == tool && e.ArgumentHash == argumentHash && string.Equals(e.NetId, netId, StringComparison.OrdinalIgnoreCase));
            if (live is not null) return live;

            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            DateTime now = _clock();
            entry = new ConfirmationEntry(token[..8], token, tool, argumentHash, netId, summary, now,
                now.AddSeconds(TtlSeconds), ConfirmationState.Pending);
            _entries.Add(entry);
            while (_entries.Count > Capacity) _entries.RemoveAt(0);
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>HOST WINDOW ONLY. A human releases the request with this code; the agent may redeem its token once.</summary>
    public bool Release(string code) => Change(code, ConfirmationState.Pending, ConfirmationState.Released);

    /// <summary>HOST WINDOW ONLY. A human refuses the request; its token never works.</summary>
    public bool Deny(string code) => Change(code, ConfirmationState.Pending, ConfirmationState.Denied);

    private bool Change(string code, ConfirmationState from, ConfirmationState to)
    {
        bool changed = false;
        lock (_gate)
        {
            ExpireDue();
            int i = _entries.FindIndex(e => e.Code == code && e.State == from);
            if (i >= 0)
            {
                _entries[i] = _entries[i] with { State = to };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Redeems the token for this very call: released, unexpired, unused and bound to the same tool, arguments and target.</summary>
    public RedeemResult Redeem(string token, string tool, string argumentHash, string? netId)
    {
        RedeemResult result;
        lock (_gate)
        {
            ExpireDue();
            int i = _entries.FindIndex(e => string.Equals(e.Token, token, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return RedeemResult.Unknown;
            var e = _entries[i];
            if (e.Tool != tool || e.ArgumentHash != argumentHash || !string.Equals(e.NetId, netId, StringComparison.OrdinalIgnoreCase))
                return RedeemResult.Mismatch;
            switch (e.State)
            {
                case ConfirmationState.Released:
                    _entries[i] = e with { State = ConfirmationState.Used };
                    result = RedeemResult.Ok;
                    break;
                case ConfirmationState.Pending: return RedeemResult.NotReleased;
                case ConfirmationState.Denied: return RedeemResult.Denied;
                case ConfirmationState.Used: return RedeemResult.Used;
                default: return RedeemResult.Expired;
            }
        }
        Changed?.Invoke();
        return result;
    }

    public IReadOnlyList<ConfirmationEntry> Snapshot()
    {
        lock (_gate)
        {
            ExpireDue();
            return [.. _entries];
        }
    }

    /// <summary>Requests that wait for a human.</summary>
    public int PendingCount => Snapshot().Count(e => e.State == ConfirmationState.Pending);

    // Caller holds _gate. A used or denied request stays in the list as the history of the window.
    private void ExpireDue()
    {
        DateTime now = _clock();
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].State is ConfirmationState.Pending or ConfirmationState.Released && now >= _entries[i].ExpiresUtc)
                _entries[i] = _entries[i] with { State = ConfirmationState.Expired };
    }
}

/// <summary>The in-band question of the human confirmation (an MCP elicitation); a fake in the tests.</summary>
internal interface IConfirmAsker
{
    /// <summary>True when the client declared that it can show a form.</summary>
    bool CanAsk { get; }

    /// <summary>Asks the human; <see cref="ConfirmAnswer.Failed"/> when the client did not answer the question.</summary>
    Task<ConfirmAnswer> AskAsync(string message, CancellationToken cancellationToken);
}

internal enum ConfirmAnswer { Yes, No, Failed }

/// <summary>
/// A human confirms an action with effect on a target system before it runs. The call-tool filter
/// (<see cref="Filter"/>) asks through MCP elicitation when the client can answer it, else it refuses the call with a
/// one-time token that a human releases in the window (<see cref="ConfirmationRegistry"/>). Which calls ask follows
/// <see cref="ToolInfo.Confirm"/> and the policy's <see cref="ConfirmMode"/>. A call the access policy refuses anyway is
/// let through to be refused there: nobody confirms an action that is blocked afterwards.
/// </summary>
internal sealed class HumanConfirmation(
    Func<AccessPolicy> policy,
    Func<string?> projectTarget,
    Func<IReadOnlyList<string>> umrInstanceNetIds,
    ConfirmationRegistry registry,
    McpActivity? activity = null,
    Func<IReadOnlyCollection<string>>? localNetIds = null)
{
    /// <summary>The argument that carries the token back (listed in the schema of every tool that can ask).</summary>
    public const string TokenArgument = "confirmationToken";

    /// <summary>How long an elicitation may stay unanswered before the call is refused.</summary>
    public TimeSpan AskTimeout { get; init; } = TimeSpan.FromSeconds(ConfirmationRegistry.TtlSeconds);

    /// <summary>A refusal that comes back faster than this was not given by a human who read the question (#51): a
    /// client declined the elicitation by itself without showing it (seen with the Claude desktop app, Code tab). The
    /// server cannot tell such a refusal from a real "No" by its content, so a too-fast one goes the token path through
    /// the window instead; a human still has to release the action there.</summary>
    public TimeSpan MinHumanAnswer { get; init; } = TimeSpan.FromSeconds(1);

    private static readonly string[] Reserved = [TokenArgument, "requestKey"];

    // ---- the call-tool filter -------------------------------------------------------------------------------

    public McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.Params?.Name is { } name)
            {
                var asker = context.Server is { } server ? new ServerAsker(server) : (IConfirmAsker)NoAsker.Instance;
                var answer = await CheckAsync(name, context.Params.Arguments, asker, cancellationToken);
                if (answer is not null) return answer;
            }
            return await next(context, cancellationToken);
        };

    /// <summary>Null when the call may run, else the result to answer with instead.</summary>
    public async Task<CallToolResult?> CheckAsync(
        string toolName, IDictionary<string, JsonElement>? arguments, IConfirmAsker asker, CancellationToken cancellationToken)
    {
        var current = policy();
        if (current.EffectiveConfirm == ConfirmMode.Off) return null;
        if (ToolCatalog.Find(toolName) is not { Confirm: not ConfirmClass.Never, AlwaysAllowed: false } tool) return null;

        object? args = arguments is null ? null : JsonSerializer.SerializeToElement(arguments);
        string? netId = AccessGate.Target(tool, args, projectTarget);
        bool isLocal = netId is not null && IsLocal(netId);
        if (!ConfirmRules.Needs(tool, current.EffectiveConfirm, isLocal, args)) return null;
        if (activity?.AccessGate is { } gate && Safe(gate, toolName, args)?.Denied is not null) return null;

        string hash = ArgumentHash(arguments);
        string target = Describe(netId, isLocal);                      // wire / log copy: English
        string humanTarget = Describe(netId, isLocal, forHuman: true); // the question: host language
        string summary = Summarise(arguments);
        var started = DateTimeOffset.UtcNow;

        string? failure = null;
        if (asker.CanAsk)
        {
            var asked = System.Diagnostics.Stopwatch.StartNew();
            var answer = await AskWithTimeoutAsync(asker, Question(toolName, humanTarget, summary), cancellationToken);
            asked.Stop();
            if (answer == ConfirmAnswer.No && asked.Elapsed < MinHumanAnswer)
            {
                failure = $"The client refused the confirmation after {asked.ElapsedMilliseconds} ms, too fast for a human " +
                          "who read it: it probably declined the question without showing it.";
                answer = ConfirmAnswer.Failed;
            }
            switch (answer)
            {
                case ConfirmAnswer.Yes:
                    Log(toolName, summary, $"confirmed by the human on {target}", success: true, started);
                    return null;
                case ConfirmAnswer.No:
                    string declined = "[Error] Declined by the human. Nothing was done.";
                    Log(toolName, summary, declined, success: false, started);
                    return Error(declined);
                default:
                    failure ??= "The client did not answer the confirmation request.";
                    break;
            }
        }

        return TokenPath(toolName, arguments, hash, netId, target, summary, failure, started);
    }

    private CallToolResult? TokenPath(string toolName, IDictionary<string, JsonElement>? arguments, string hash, string? netId,
        string target, string summary, string? failure, DateTimeOffset started)
    {
        string? token = TokenOf(arguments);
        string? why = null;
        if (token is not null)
        {
            var redeemed = registry.Redeem(token, toolName, hash, netId);
            if (redeemed == RedeemResult.Ok)
            {
                Log(toolName, summary, $"released by the human in the window, token redeemed on {target}", success: true, started);
                return null;
            }
            if (redeemed == RedeemResult.NotReleased)
            {
                var pending = registry.Snapshot().FirstOrDefault(e => string.Equals(e.Token, token, StringComparison.OrdinalIgnoreCase));
                string wait = $"[Confirm] {toolName} on {target} is not released yet. A human must press Release on request " +
                              $"{pending?.Code} in the Approvals tab of the ptc-xae-mcp window; then repeat this call with the same " +
                              $"{TokenArgument}. Nothing was done.";
                Log(toolName, summary, wait, success: false, started);
                return Text(wait, isError: false);
            }
            if (redeemed == RedeemResult.Denied)
            {
                string denied = "[Error] Declined by the human in the window. Nothing was done.";
                Log(toolName, summary, denied, success: false, started);
                return Error(denied);
            }
            why = redeemed switch
            {
                RedeemResult.Mismatch => $"The {TokenArgument} was issued for a different tool, arguments or target.",
                RedeemResult.Used => $"The {TokenArgument} was already used.",
                RedeemResult.Expired => $"The {TokenArgument} expired.",
                _ => $"The {TokenArgument} is not known.",
            };
        }

        var entry = registry.Issue(toolName, hash, netId, summary);
        string text = $"[Confirm] {toolName} on {target} needs a human's release. Nothing was done yet.\n" +
                      (why is null ? "" : why + " A new request was opened.\n") +
                      (failure is null ? "" : failure + " The request goes through the window instead.\n") +
                      $"1. Ask the human to open the Approvals tab of the ptc-xae-mcp window and press Release on request {entry.Code}" +
                      $" ({summary}).\n" +
                      $"2. Then repeat this exact call with {TokenArgument}=\"{entry.Token}\".\n" +
                      $"The token belongs to this tool, these arguments and this target, is valid for {ConfirmationRegistry.TtlSeconds} s " +
                      "and works once.";
        Log(toolName, summary, $"[Confirm] request {entry.Code} opened for {target}", success: false, started);
        return Text(text, isError: false);
    }

    private async Task<ConfirmAnswer> AskWithTimeoutAsync(IConfirmAsker asker, string message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AskTimeout);
        try
        {
            return await asker.AskAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConfirmAnswer.Failed;
        }
    }

    private bool IsLocal(string netId)
    {
        if (string.Equals(netId, AccessPolicy.LocalKeyword, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var locals = localNetIds is null ? LocalNetIds.Collect(umrInstanceNetIds) : localNetIds();
            return locals.Contains(netId, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static AccessDecision? Safe(Func<string, object?, AccessDecision> gate, string tool, object? args)
    {
        try { return gate(tool, args); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    // ---- texts ----------------------------------------------------------------------------------------------

    /// <summary>One line for twincat_doctor and twincat_get_client_info: the mode and how this client would be asked.</summary>
    internal static string StatusLine(AccessPolicy current, ClientCapabilities? capabilities)
    {
        bool elicitation = ServerAsker.CanElicit(capabilities);
        string how = elicitation
            ? "this client supports MCP elicitation and is asked in its own window"
            : "this client does not support MCP elicitation: a human releases a token in the Approvals tab of the ptc-xae-mcp window";
        return current.EffectiveConfirm == ConfirmMode.Off
            ? "Human confirmation: off (nobody is asked before an action with effect on a target system)."
            : $"Human confirmation: {AccessPolicy.Name(current.EffectiveConfirm)} " +
              $"({(current.EffectiveConfirm == ConfirmMode.All ? "activate, restart, stop and mode switches everywhere, the rest on other systems" : "actions on other systems")}); {how}.";
    }

    /// <summary>The target as the agent reads it (English); <paramref name="forHuman"/> gives the host-language copy for the question.</summary>
    internal static string Describe(string? netId, bool isLocal, bool forHuman = false) => netId switch
    {
        null => forHuman ? Loc.Get("confirm.target.unknown") : "an unknown target system",
        _ when netId == AccessPolicy.LocalKeyword => forHuman ? Loc.Get("confirm.target.local") : "the local system",
        _ => isLocal
            ? (forHuman ? Loc.Format("confirm.target.thismachine", netId) : $"{netId} (this machine)")
            : (forHuman ? Loc.Format("confirm.target.another", netId) : $"{netId} (another system)"),
    };

    /// <summary>The question a human reads in the client's window (host language); <paramref name="summary"/> stays English.</summary>
    internal static string Question(string tool, string target, string summary) =>
        Loc.Format("confirm.question", tool, target, summary.Length == 0 ? Loc.Get("confirm.question.none") : summary);

    /// <summary>The arguments as a short line for the question and the window; secrets are replaced.</summary>
    internal static string Summarise(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return string.Empty;
        var parts = new List<string>();
        foreach (var (key, value) in arguments.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (Reserved.Contains(key, StringComparer.OrdinalIgnoreCase) || value.ValueKind == JsonValueKind.Null) continue;
            string text = IsSecret(key) ? "***" : value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
            parts.Add($"{key}={(text.Length > 80 ? text[..80] + "..." : text)}");
        }
        string line = string.Join(", ", parts);
        return line.Length > 400 ? line[..400] + "..." : line;
    }

    private static bool IsSecret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase);

    /// <summary>A hash of the arguments that does not depend on their order or on null values, without the reserved ones.</summary>
    internal static string ArgumentHash(IDictionary<string, JsonElement>? arguments)
    {
        var root = new JsonObject();
        foreach (var (key, value) in (arguments ?? new Dictionary<string, JsonElement>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (Reserved.Contains(key, StringComparer.OrdinalIgnoreCase) || value.ValueKind == JsonValueKind.Null) continue;
            root[key] = Canonical(JsonNode.Parse(value.GetRawText()));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToJsonString()))).ToLowerInvariant();
    }

    private static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canonical(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };

    private static string? TokenOf(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return null;
        foreach (var (key, value) in arguments)
            if (string.Equals(key, TokenArgument, StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        return null;
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string text) => Text(text, isError: true);

    private void Log(string tool, string summary, string result, bool success, DateTimeOffset started)
    {
        try
        {
            activity?.RecordNote(tool + " (confirmation)", summary, result, success, DateTimeOffset.UtcNow - started);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HostLog.Shared.Warn($"Confirmation of {tool} could not be recorded: {ex.Message}");
        }
    }

    // ---- the MCP client ---------------------------------------------------------------------------------------

    private sealed class NoAsker : IConfirmAsker
    {
        public static readonly NoAsker Instance = new();
        public bool CanAsk => false;
        public Task<ConfirmAnswer> AskAsync(string message, CancellationToken cancellationToken) => Task.FromResult(ConfirmAnswer.Failed);
    }

    /// <summary>Asks through MCP elicitation of the calling session.</summary>
    internal sealed class ServerAsker(McpServer server) : IConfirmAsker
    {
        internal const string Field = "confirmed";

        public bool CanAsk => CanElicit(server.ClientCapabilities);

        /// <summary>The client can show a form (an old client that only says <c>elicitation: {}</c> can).</summary>
        internal static bool CanElicit(ClientCapabilities? capabilities) =>
            capabilities?.Elicitation is { } e && (e.Form is not null || e.Url is null);

        public async Task<ConfirmAnswer> AskAsync(string message, CancellationToken cancellationToken)
        {
            try
            {
                var result = await server.ElicitAsync(new ElicitRequestParams
                {
                    Message = message,
                    RequestedSchema = new ElicitRequestParams.RequestSchema
                    {
                        Properties =
                        {
                            [Field] = new ElicitRequestParams.BooleanSchema
                            {
                                Title = Loc.Get("confirm.field.title"),
                                Description = Loc.Get("confirm.field.description"),
                                Default = false,
                            },
                        },
                        Required = [Field],
                    },
                }, cancellationToken);

                bool yes = result.IsAccepted
                           && result.Content is { } content
                           && content.TryGetValue(Field, out var value)
                           && value.ValueKind == JsonValueKind.True;
                return yes ? ConfirmAnswer.Yes : ConfirmAnswer.No;
            }
            catch (Exception ex) when (ex is McpException or InvalidOperationException or IOException or NotSupportedException)
            {
                HostLog.Shared.Warn($"Confirmation request to the client failed: {ex.Message}");
                return ConfirmAnswer.Failed;
            }
        }
    }
}

/// <summary>Which calls ask a human: the pure rule, apart from the target lookup.</summary>
internal static class ConfirmRules
{
    /// <param name="isLocal">The target is this machine (false for an unknown target: it is asked about).</param>
    public static bool Needs(ToolInfo tool, ConfirmMode mode, bool isLocal, object? arguments)
    {
        if (mode == ConfirmMode.Off || tool.AlwaysAllowed || tool.Confirm == ConfirmClass.Never) return false;

        // A dry run changes nothing; a call that only attaches or only reads is no action.
        if (tool.ConfirmOnlyWithoutDryRun
            && !string.Equals(AccessGate.ArgumentText(arguments, "dryRun"), "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (tool.ConfirmExempt is { } exempt
            && string.Equals(AccessGate.ArgumentText(arguments, exempt.Argument), exempt.Value, StringComparison.OrdinalIgnoreCase))
            return false;

        if (tool.Confirm == ConfirmClass.Always && mode == ConfirmMode.All) return true;
        return !isLocal;
    }
}
