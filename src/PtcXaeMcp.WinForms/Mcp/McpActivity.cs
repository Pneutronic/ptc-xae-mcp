using PtcXaeMcp.WinForms.TwinCat;
using System.Diagnostics;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Central observability + test-control surface for MCP tool calls. Bundles
/// the in-memory ring buffer (<see cref="Log"/>), the per-run JSON-lines sink
/// (<see cref="JsonLog"/>) and the cooperative step/pause/stop gate
/// (<see cref="Gate"/>). Owned by <see cref="McpBridge"/> and shared with the
/// GUI, exactly like <see cref="McpSessionRegistry"/>.
/// <para>
/// The <see cref="TraceAsync"/> / <see cref="Trace"/> wrappers are the single
/// point every <c>twincat_*</c> tool body passes through: they gate, time and
/// record the call without changing its result. A logging or gating failure
/// must never alter what a tool returns.
/// </para>
/// </summary>
/// <summary>The access policy's answer for one call: <see cref="Denied"/> refuses it, <see cref="Warning"/> is appended.</summary>
public sealed record AccessDecision(string? Denied, string? Warning)
{
    public static readonly AccessDecision Allowed = new(null, null);
}

public sealed class McpActivity
{
    public McpActivityLog Log { get; }
    public McpActivityJsonLog JsonLog { get; }
    public McpTestGate Gate { get; } = new();

    /// <summary>
    /// Live "which integration test is running" state, driven by the test-only
    /// <c>ptc_test_marker</c> path. Observability only — never recorded as an
    /// XAE operation in <see cref="Log"/>.
    /// </summary>
    public McpCurrentTest CurrentTest { get; } = new();

    private int _runStarted;

    private static readonly AsyncLocal<string?> s_currentTool = new();

    /// <summary>The tool whose traced body runs on this logical call path, or null outside a traced call.</summary>
    internal static string? CurrentToolName => s_currentTool.Value;

    /// <summary>
    /// Records the result of a call that already answered [Blocked], once the XAE call really returned
    /// (<see cref="McpDispatcher.LateResult"/>): one more entry, "&lt;tool&gt; (late result)", so it is never lost.
    /// </summary>
    public void RecordLateResult(string toolName, string result, TimeSpan duration)
    {
        var entry = new McpActivityEntry(DateTimeOffset.UtcNow - duration, toolName + " (late result)", string.Empty,
            McpActivityEntry.Truncate(result), duration, !McpErrors.IsError(result));
        Record(entry);
    }

    /// <summary>
    /// The access policy's decision for a call (tool name, traced arguments): a refusal, or null plus an optional
    /// warning appended to the result. Set by <see cref="McpBridge"/>; null lets every call through (unit tests).
    /// </summary>
    public Func<string, object?, AccessDecision>? AccessGate { get; set; }

    public McpActivity(McpActivityLog? log = null, McpActivityJsonLog? jsonLog = null)
    {
        Log = log ?? new McpActivityLog();
        JsonLog = jsonLog ?? new McpActivityJsonLog();
    }

    /// <summary>
    /// Begins a fresh run: rolls the JSON log to a new per-run file, clears
    /// the in-memory buffer and re-arms the gate. Triggered when a test suite
    /// starts (spec: "cleared first when the test starts") and from
    /// the GUI Clear button.
    /// </summary>
    public void StartNewRun()
    {
        Interlocked.Exchange(ref _runStarted, 1);
        JsonLog.StartNewRun();
        Log.StartNewRun();
        Gate.ResetForNewRun();
        CurrentTest.ResetForNewRun();
    }

    /// <summary>
    /// Ensures a run is active so the JSON log has a file to write to. Called
    /// on the first traced call when the operator hasn't explicitly started
    /// one — the live suite hits the host over HTTP and never clicks "Clear",
    /// yet its traffic must still be captured to disk.
    /// </summary>
    private void EnsureRunStarted()
    {
        if (Interlocked.CompareExchange(ref _runStarted, 1, 0) == 0)
        {
            JsonLog.StartNewRun();
            // Buffer/gate are already in their fresh initial state at startup;
            // only the JSON sink needs an explicit file.
        }
    }

    /// <summary>
    /// Wraps an async tool body: awaits the gate (unless
    /// <paramref name="bypassGate"/>), runs the body, records exactly one
    /// entry (OK or, on a thrown/cancelled call, a failure entry) and returns
    /// the body's result unchanged. Never throws on the recording path.
    /// </summary>
    public async Task<string> TraceAsync(
        string toolName,
        object? arguments,
        Func<Task<string>> body,
        CancellationToken ct = default,
        bool bypassGate = false)
    {
        EnsureRunStarted();

        var startedUtc = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        if (!bypassGate)
        {
            try
            {
                await Gate.WaitAsync(toolName, ct);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                var cancelled = McpErrors.Cancelled("stopped at test gate");
                Record(McpActivityEntry.Failure(
                    toolName, Describe(arguments), cancelled, startedUtc, sw.Elapsed));
                return cancelled;
            }
        }

        var blocked = TwinCat.TwinCatSystemService.BlockReason(toolName, out var serviceBefore);
        if (blocked is not null)
        {
            sw.Stop();
            Record(McpActivityEntry.Failure(
                toolName, Describe(arguments), blocked, startedUtc, sw.Elapsed));
            return blocked;
        }

        var access = Decide(toolName, arguments);
        if (access.Denied is not null)
        {
            sw.Stop();
            Record(McpActivityEntry.Failure(
                toolName, Describe(arguments), access.Denied, startedUtc, sw.Elapsed));
            return access.Denied;
        }

        try
        {
            // The dispatcher names the tool in a [Blocked] answer and in the late result of the call.
            s_currentTool.Value = toolName;
            var result = await body();
            if (serviceBefore.State == TwinCat.SystemServiceState.Running)
                result += TwinCat.TwinCatSystemService.StoppedDuring(serviceBefore);
            if (access.Warning is not null)
                result += "\n" + access.Warning;
            sw.Stop();
            // Tool bodies return "[Error] ..." as a normal string on failure.
            bool ok = !McpErrors.IsError(result);
            Record(new McpActivityEntry(
                startedUtc, toolName, McpActivityEntry.Truncate(Describe(arguments)),
                McpActivityEntry.Truncate(result), sw.Elapsed, ok));
            return result!;
        }
        catch (XaeCallDetachedException ex)
        {
            // An XAE dialog holds the call: the [Blocked] text is the answer, unchanged (no [Error] prefix). The call
            // keeps running; its late result is recorded by RecordLateResult.
            sw.Stop();
            Record(McpActivityEntry.Failure(
                toolName, Describe(arguments), ex.Message, startedUtc, sw.Elapsed));
            return ex.Message;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            var cancelled = McpErrors.Cancelled("call cancelled");
            Record(McpActivityEntry.Failure(
                toolName, Describe(arguments), cancelled, startedUtc, sw.Elapsed));
            return cancelled;
        }
        catch (Exception ex)
        {
            sw.Stop();
            // Busy/blocked COM errors name the XAE dialog holding them (issue #149);
            // EnsureError avoids stacking a second [Error] prefix (issue #151).
            var msg = McpErrors.EnsureError(ComCall.Describe(ex));
            Record(McpActivityEntry.Failure(
                toolName, Describe(arguments), msg, startedUtc, sw.Elapsed));
            return msg;
        }
    }

    /// <summary>
    /// Synchronous-body convenience over <see cref="TraceAsync"/>. Exceptions
    /// from <paramref name="body"/> are caught by <see cref="TraceAsync"/> and
    /// returned as <c>[Error] …</c> strings — the tool always responds with a
    /// success payload and the failure is recorded in the activity log.
    /// </summary>
    public string Trace(
        string toolName,
        object? arguments,
        Func<string> body,
        bool bypassGate = false)
        => Trace(toolName, arguments, body, CancellationToken.None, bypassGate);

    /// <summary>
    /// Overload that accepts the request's <see cref="CancellationToken"/> —
    /// used by synchronous tools whose <c>[McpServerTool]</c> method takes a
    /// <c>CancellationToken cancellationToken = default</c> parameter (the MCP
    /// SDK binds it automatically), so a client disconnect can still cancel a
    /// call parked at the single-step gate instead of surviving it (F-17).
    /// </summary>
    public string Trace(
        string toolName,
        object? arguments,
        Func<string> body,
        CancellationToken cancellationToken,
        bool bypassGate = false)
        => TraceAsync(toolName, arguments, () => Task.FromResult(body()),
                      cancellationToken, bypassGate)
           .GetAwaiter().GetResult();

    // A gate that fails refuses the call: an access policy that cannot be evaluated must not let everything through.
    private AccessDecision Decide(string toolName, object? arguments)
    {
        if (AccessGate is not { } gate) return AccessDecision.Allowed;
        try
        {
            return gate(toolName, arguments);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AccessDecision($"[Error] The access policy could not be checked for {toolName}: {ex.Message}. Nothing was done.", null);
        }
    }

    private void Record(McpActivityEntry entry)
    {
        // Both sinks are individually best-effort; never let recording throw
        // into the tool path.
        try { Log.Add(entry); } catch { /* observability only */ }
        try { JsonLog.Append(entry); } catch { /* observability only */ }
    }

    /// <summary>
    /// Compact, log-safe argument representation. Never throws — a tool call
    /// must not fail because its arguments could not be serialised.
    /// </summary>
    internal static string Describe(object? arguments)
    {
        if (arguments is null)
            return string.Empty;
        if (arguments is string s)
            return s;
        try
        {
            return JsonSerializer.Serialize(arguments);
        }
        catch
        {
            return arguments.ToString() ?? string.Empty;
        }
    }
}
