using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.Diagnostics;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// A call to a tool with effect on a target system may carry a <c>requestKey</c>. The same key with the same arguments
/// never runs twice: a repeat gets the stored result, a request still running is reported as running, and a request
/// the host never saw finish is reported as <c>outcome_unknown</c> with the live state of the target. A client that
/// timed out and asks again, or an agent that retries after a restart, cannot activate or restart a second time by
/// mistake. Without a key a call behaves as before.
/// </summary>
internal sealed partial class RequestReceipts(
    ReceiptStore store,
    Func<string?> projectTarget,
    Func<string?, string>? liveState = null,
    McpActivity? activity = null)
{
    /// <summary>The argument that carries the key (listed in the schema of every tool that takes one).</summary>
    public const string KeyArgument = "requestKey";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,128}$")]
    private static partial Regex ValidKey();

    // ---- the call-tool filter -------------------------------------------------------------------------------

    public McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.Params?.Name is not { } name || KeyOf(context.Params.Arguments) is not { } key)
                return await next(context, cancellationToken);
            return await RunAsync(name, key, context.Params.Arguments, () => next(context, cancellationToken));
        };

    /// <summary>The result to answer with: the call's own when the key is new, else the receipt's.</summary>
    internal async ValueTask<CallToolResult> RunAsync(
        string toolName, string key, IDictionary<string, JsonElement>? arguments, Func<ValueTask<CallToolResult>> run)
    {
        if (ToolCatalog.Find(toolName) is not { Receipts: true } tool) return await run();
        if (!ValidKey().IsMatch(key))
            return Error($"[Error] {KeyArgument} must be 8 to 128 characters: letters, digits, '.', '_' or '-' (a UUID is best). Nothing was done.");

        object? args = arguments is null ? null : JsonSerializer.SerializeToElement(arguments);
        string? netId = AccessGate.Target(tool, args, projectTarget);
        string hash = HumanConfirmation.ArgumentHash(arguments);

        var begin = store.Begin(key, toolName, hash, netId);
        if (begin.Failure is not null)
            return Error($"[Error] {toolName} was not started: {begin.Failure}. A request key promises that a request runs once, " +
                         $"so without a receipt it does not run. Leave out {KeyArgument} to run it without that promise.");
        if (!begin.Created) return Existing(begin.Receipt, toolName, hash, netId, TargetIsAnArgument(tool));

        CallToolResult result;
        try
        {
            result = await run();
        }
        catch (OperationCanceledException)
        {
            store.Finish(key, ReceiptOutcome.Unknown, "[Error] The call was cancelled before the host saw it finish.");
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            store.Finish(key, ReceiptOutcome.Unknown, "[Error] " + ex.Message);
            throw;
        }

        string text = TextOf(result);
        var outcome = Classify(text, result.IsError == true);
        if (outcome is null)
            store.Void(key); // a refusal that did nothing: the key is free again
        else
            store.Finish(key, outcome.Value, text);
        return result;
    }

    /// <summary>
    /// What a result text says about the request: null when nothing was done (a refusal or a question to the human; the
    /// key stays free), unknown when the call is held or cancelled, else ok or error.
    /// </summary>
    internal static ReceiptOutcome? Classify(string text, bool isError)
    {
        if (text.StartsWith("[Confirm]", StringComparison.Ordinal)) return null;
        if (text.StartsWith("[Blocked]", StringComparison.Ordinal) || text.StartsWith(McpErrors.CancelledMessage, StringComparison.Ordinal))
            return ReceiptOutcome.Unknown;
        if (isError || McpErrors.IsError(text))
            return NothingWasDone(text) ? null : ReceiptOutcome.Error;
        return ReceiptOutcome.Ok;
    }

    private static readonly string[] Refusals =
    [
        "[Error] Access policy", "[Error] The access policy could not be checked", "[Error] An operation is already in progress",
        "[Error] Declined by the human",
    ];

    private static bool NothingWasDone(string text) =>
        Refusals.Any(p => text.StartsWith(p, StringComparison.Ordinal)) || text.Contains("Nothing was done", StringComparison.Ordinal);

    // ---- a key that is there already ------------------------------------------------------------------------------

    /// <summary>
    /// Whether the target is part of what the caller sent. Only then does another target make another request. A target
    /// read from the open project can change through the request itself: a deploy saves the project, and a target set
    /// with twincat_set_target reaches the .tsproj on disk only then. Its repeat must still find its receipt.
    /// </summary>
    internal static bool TargetIsAnArgument(ToolInfo tool) => tool.Target == TargetRule.AmsNetIdArgument;

    private CallToolResult Existing(Receipt receipt, string toolName, string hash, string? netId, bool compareTarget)
    {
        if (receipt.Tool != toolName || receipt.ArgumentHash != hash
            || (compareTarget && !string.Equals(receipt.TargetNetId, netId, StringComparison.OrdinalIgnoreCase)))
        {
            string text = $"[Error] {KeyArgument} '{receipt.RequestKey}' belongs to another request ({receipt.Tool}, started " +
                          $"{Utc(receipt.StartedUtc)}). A key names one request: use a new {KeyArgument} for this call. Nothing was done.";
            Log(toolName, text, success: false);
            return Error(text);
        }

        switch (receipt.Outcome)
        {
            case ReceiptOutcome.Running:
                {
                    int seconds = (int)Math.Max(0, (DateTime.UtcNow - receipt.StartedUtc).TotalSeconds);
                    string text = $"[Receipt] running: {toolName} with {KeyArgument} '{receipt.RequestKey}' started {Utc(receipt.StartedUtc)} " +
                                  $"({seconds} s ago) and has not finished. It was not started again. Poll twincat_receipt_get with the " +
                                  $"same {KeyArgument} until it finishes.";
                    Log(toolName, text, success: true);
                    return Text(text);
                }
            case ReceiptOutcome.Unknown:
                {
                    string text = $"[Warn] outcome_unknown: {toolName} with {KeyArgument} '{receipt.RequestKey}' started {Utc(receipt.StartedUtc)} " +
                                  "and the host never saw it finish (restart, a cancelled call or a call held by an XAE dialog). It may or " +
                                  $"may not have run, so it was not started again.\nLive state of the target: {LiveState(netId)}\n" +
                                  $"To run it again deliberately, look at that state first, then repeat the call with a NEW {KeyArgument}.";
                    Log(toolName, text, success: false);
                    return Text(text);
                }
            default:
                {
                    string note = $"[Receipt] Repeated request: this is the stored result of the call that started {Utc(receipt.StartedUtc)}" +
                                  (receipt.FinishedUtc is { } done ? $" and finished {Utc(done)}" : "") +
                                  $". Nothing was run again. Use a new {KeyArgument} to run it again deliberately.";
                    Log(toolName, note, success: true);
                    return new CallToolResult
                    {
                        IsError = false,
                        Content = [new TextContentBlock { Text = receipt.ResultText ?? string.Empty }, new TextContentBlock { Text = note }],
                    };
                }
        }
    }

    private string LiveState(string? netId)
    {
        if (liveState is null) return "(not available)";
        try
        {
            return liveState(netId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"(could not be read: {ex.Message})";
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    internal static string? KeyOf(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return null;
        foreach (var (name, value) in arguments)
            if (string.Equals(name, KeyArgument, StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        return null;
    }

    internal static string Utc(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static CallToolResult Text(string text) => new() { IsError = false, Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    private void Log(string tool, string text, bool success)
    {
        try
        {
            activity?.RecordNote(tool + " (receipt)", string.Empty, text, success, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HostLog.Shared.Warn($"Receipt note of {tool} could not be recorded: {ex.Message}");
        }
    }
}
