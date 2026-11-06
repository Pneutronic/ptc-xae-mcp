using PtcXaeMcp.WinForms.Diagnostics;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;
using System.Diagnostics;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

/// <summary>
/// Thrown by <see cref="McpDispatcher.Run"/> / <see cref="McpDispatcher.RunAsync"/> when the call is held by an XAE
/// dialog: its message is the <c>[Blocked] …</c> answer, returned unchanged by <see cref="McpActivity.TraceAsync"/>.
/// A final <see cref="XaeBlockedException"/>, so busy-aware catches let it through and no retry scope retries it.
/// </summary>
internal sealed class XaeCallDetachedException : XaeBlockedException
{
    public XaeCallDetachedException(string message, BlockingXaeDialog dialog)
        : base(message, dialog.Title, null)
    {
        BlockingDialog = dialog;
    }

    public BlockingXaeDialog BlockingDialog { get; }
}

/// <summary>A call whose MCP answer went back as [Blocked] while it still runs in the XAE.</summary>
internal sealed record DetachedCall(string Tool, BlockingXaeDialog Dialog, DateTime SinceUtc);

/// <summary>
/// Serializes concurrent MCP tool invocations via a single SemaphoreSlim(1,1).
/// Owned by ServerHost; tools call Run() or RunAsync() directly.
/// <para>
/// A call that hangs on an XAE dialog does not hang the MCP answer: off the GUI thread the body runs on its own
/// thread while the caller polls an <see cref="XaeCallBlockWatch"/>. Once a dialog with a question has stayed for the
/// grace time the call answers <c>[Blocked] XAE dialog …</c>, but the lock stays held — no other COM call starts —
/// until the body really returns; its late result then goes to <see cref="LateResult"/> (the activity log) and the
/// lock is released. Later calls are refused with the dialog named (<see cref="BusyNow"/>).
/// </para>
/// </summary>
internal sealed class McpDispatcher : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<XaeCallBlockWatch>? _watch;
    private readonly TimeSpan _poll;
    private readonly Func<BlockingXaeDialog?> _currentQuestion;
    private DetachedCall? _detached;

    /// <summary>
    /// Number of bodies currently executing under the lock (0 or 1 by
    /// construction — the lock only ever admits one). Guards
    /// <see cref="Dispose"/> (issue #154, A-12/F-30): McpBridge.Stop() used to
    /// dispose the dispatcher (and its SemaphoreSlim) immediately on
    /// twincat_shutdown/host teardown, with no regard for a tool call still
    /// executing under the lock — that call's own <c>finally</c> block then
    /// throws ObjectDisposedException out of Release(), on whatever thread was
    /// running the tool, instead of the tool simply finishing.
    /// </summary>
    private int _inFlight;
    private volatile bool _disposed;

    public ITwinCatSession Session { get; }

    public McpDispatcher(ITwinCatSession session)
        : this(session, WatchEnabled(Environment.GetEnvironmentVariable(WatchEnvVar)) ? XaeCallBlockWatch.ForXae : null,
               XaeCallBlockWatch.DefaultPoll, XaeCallBlockWatch.CurrentQuestion)
    {
    }

    /// <summary>Set to <c>off</c> (or <c>0</c>/<c>false</c>) to run every body inline as before: no [Blocked] answer.</summary>
    public const string WatchEnvVar = "PTC_XAE_MCP_DIALOG_WATCH";

    /// <summary>The watch is on unless the variable says off. Pure — unit-tested.</summary>
    internal static bool WatchEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is not ("off" or "0" or "false" or "no");

    /// <param name="session">The COM session the tools share.</param>
    /// <param name="watch">A fresh watch per call; null runs every body inline (no [Blocked] answer).</param>
    /// <param name="poll">How often the caller looks at the watch while the body runs.</param>
    /// <param name="currentQuestion">The dialog asking something now, for the refusal of a later call.</param>
    internal McpDispatcher(ITwinCatSession session, Func<XaeCallBlockWatch>? watch, TimeSpan poll,
        Func<BlockingXaeDialog?>? currentQuestion = null)
    {
        Session = session;
        _watch = watch;
        _poll = poll;
        _currentQuestion = currentQuestion ?? (() => null);
    }

    /// <summary>
    /// Receives the result of a call that answered [Blocked] once it really returns: tool name, result text
    /// ([Error] for an exception) and total duration. Set by <see cref="McpBridge"/> to the activity log.
    /// </summary>
    public Action<string, string, TimeSpan>? LateResult { get; set; }

    /// <summary>The call held by a dialog after its [Blocked] answer, or null.</summary>
    public DetachedCall? Detached => Volatile.Read(ref _detached);

    /// <summary>
    /// Acquires the single-operation lock and runs <paramref name="body"/>.
    /// Throws <see cref="InvalidOperationException"/> immediately if another operation is in progress,
    /// and <see cref="XaeCallDetachedException"/> when an XAE dialog holds the body (see the class remarks).
    /// Invokes <paramref name="afterSuccess"/> when body returns a non-[Error] string.
    /// </summary>
    public string Run(Func<string> body, Action? afterSuccess = null)
    {
        if (!TryEnter())
            throw BusyNow();
        bool detached = false;
        try
        {
            string result;
            if (WatchForThisCall() is { } watch)
            {
                var started = Stopwatch.StartNew();
                string tool = McpActivity.CurrentToolName ?? "The running call";
                // A dedicated thread: a body that hangs in the XAE must not hold a pool thread.
                var task = Task.Factory.StartNew(body, CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                while (!Completes(task, _poll))
                {
                    if (watch.Tick(started.Elapsed) is not { } dialog || task.IsCompleted) continue;
                    detached = true;
                    throw Detach(task, tool, dialog, started, afterSuccess);
                }
                result = task.GetAwaiter().GetResult();
            }
            else
            {
                result = body();
            }
            if (afterSuccess is not null && McpErrors.IsOk(result))
                afterSuccess();
            return result;
        }
        finally
        {
            if (!detached) Exit();
        }
    }

    /// <summary>
    /// Async variant for operations that await while holding the lock (e.g. OpenProject).
    /// Throws <see cref="InvalidOperationException"/> immediately if another operation is in progress,
    /// and <see cref="XaeCallDetachedException"/> when an XAE dialog holds the body.
    /// </summary>
    public async Task<string> RunAsync(Func<Task<string>> body, Action? afterSuccess = null)
    {
        if (!TryEnter())
            throw BusyNow();
        bool detached = false;
        try
        {
            string result;
            if (WatchForThisCall() is { } watch)
            {
                var started = Stopwatch.StartNew();
                string tool = McpActivity.CurrentToolName ?? "The running call";
                var task = Task.Run(body);
                while (await Task.WhenAny(task, Task.Delay(_poll)).ConfigureAwait(false) != task)
                {
                    if (watch.Tick(started.Elapsed) is not { } dialog || task.IsCompleted) continue;
                    detached = true;
                    throw Detach(task, tool, dialog, started, afterSuccess);
                }
                result = await task.ConfigureAwait(false);
            }
            else
            {
                result = await body();
            }
            if (afterSuccess is not null && McpErrors.IsOk(result))
                afterSuccess();
            return result;
        }
        finally
        {
            if (!detached) Exit();
        }
    }

    // Only off the GUI thread: a call from the STA GUI thread (or with a SynchronizationContext) keeps running inline,
    // exactly as before — its COM objects and message filter belong to that thread.
    private XaeCallBlockWatch? WatchForThisCall()
    {
        if (_watch is null || SynchronizationContext.Current is not null
            || Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return null;
        return _watch();
    }

    private static bool Completes(Task task, TimeSpan within)
    {
        try { return task.Wait(within); }
        catch (AggregateException) { return true; } // faulted or cancelled: GetResult() rethrows the original
    }

    private XaeCallDetachedException Detach(Task<string> task, string tool, BlockingXaeDialog dialog, Stopwatch started,
        Action? afterSuccess)
    {
        var call = new DetachedCall(tool, dialog, DateTime.UtcNow);
        Volatile.Write(ref _detached, call);
        try { HostLog.Shared.Warn($"{tool} is held by the XAE dialog {dialog.Describe()}; answered [Blocked], the lock stays held until the call returns."); }
        catch (Exception) { /* observability only */ }
        _ = task.ContinueWith(t => FinishLate(t, call, started, afterSuccess),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return new XaeCallDetachedException(BlockedText(tool, dialog), dialog);
    }

    // The detached body returned: record its result (never thrown away silently), then free the lock.
    private void FinishLate(Task<string> task, DetachedCall call, Stopwatch started, Action? afterSuccess)
    {
        try
        {
            string text = LateText(task);
            if (afterSuccess is not null && task.Status == TaskStatus.RanToCompletion && McpErrors.IsOk(text))
            {
                try { afterSuccess(); }
                catch (Exception ex) { text += $"\n(after the call: {ex.Message})"; }
            }
            try { LateResult?.Invoke(call.Tool, text, started.Elapsed); } catch (Exception) { /* observability only */ }
            try { HostLog.Shared.Info($"{call.Tool} returned after its [Blocked] answer ({started.Elapsed.TotalSeconds:F1} s): {McpActivityEntry.Truncate(text)}"); }
            catch (Exception) { /* observability only */ }
        }
        finally
        {
            Volatile.Write(ref _detached, null);
            Exit();
        }
    }

    internal static string LateText(Task<string> task) => task.Status switch
    {
        TaskStatus.RanToCompletion => task.Result,
        TaskStatus.Canceled => McpErrors.Cancelled("call cancelled"),
        _ => McpErrors.EnsureError(ComCall.Describe(
                 task.Exception?.InnerException ?? (Exception?)task.Exception ?? new InvalidOperationException("the call failed"))),
    };

    /// <summary>The [Blocked] answer. Pure — unit-tested.</summary>
    internal static string BlockedText(string tool, BlockingXaeDialog dialog) =>
        $"[Blocked] XAE dialog {dialog.Describe()} — {dialog.HowToAnswer()}. " +
        $"{tool} keeps running in the XAE until the dialog is answered; no other call starts before it returns, " +
        "and its late result goes to the activity log.";

    /// <summary>Wait(0) on the lock, treating an already-disposed dispatcher
    /// (host shutting down) the same as "busy" rather than letting
    /// ObjectDisposedException escape to the tool caller.</summary>
    private bool TryEnter()
    {
        if (_disposed) return false;
        try
        {
            if (!_lock.Wait(0)) return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        Interlocked.Increment(ref _inFlight);
        return true;
    }

    private void Exit()
    {
        Interlocked.Decrement(ref _inFlight);
        // Dispose() may have run while this body was executing (host
        // shutdown mid-tool-call) — Release() on an already-disposed
        // SemaphoreSlim throws ObjectDisposedException; there is nothing
        // meaningful left to release in that case, so swallow it instead of
        // faulting whatever thread ran the tool body.
        try { _lock.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// The refusal for a call that finds the lock taken: names the dialog when a [Blocked] call still holds it,
    /// otherwise <see cref="Busy"/>.
    /// </summary>
    internal InvalidOperationException BusyNow()
    {
        if (Detached is not { } call) return Busy();
        BlockingXaeDialog? now;
        try { now = _currentQuestion(); }
        catch (Exception) { now = null; }
        return new InvalidOperationException(HeldText(call, now, DateTime.UtcNow));
    }

    /// <summary>Why a later call is refused while <paramref name="call"/> is held. Pure — unit-tested.</summary>
    internal static string HeldText(DetachedCall call, BlockingXaeDialog? now, DateTime nowUtc)
    {
        string since = ((int)Math.Max(0, (nowUtc - call.SinceUtc).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return now is not null
            ? $"An operation is already in progress: {call.Tool} waits for the XAE dialog {now.Describe()} " +
              $"({since} s since its [Blocked] answer). No other call starts until it returns — {now.HowToAnswer()}, then retry." +
              $"\n{McpErrors.NextPrefix}twincat_xae_dialogs lists the dialog; twincat_xae_dialog_answer presses one of its buttons."
            : $"An operation is already in progress: {call.Tool} still runs in the XAE ({since} s since its [Blocked] answer for " +
              $"{call.Dialog.Describe()}; that dialog is closed). Its result goes to the activity log; retry in a moment." +
              $"\n{McpErrors.NextPrefix}twincat_xae_dialogs shows whether another dialog waits.";
    }

    /// <summary>
    /// "An operation is already in progress", plus the XAE window the running call waits for when
    /// there is one (a message box, or any window owned by the disabled XAE main window — issue
    /// #149). Detection only.
    /// </summary>
    internal static InvalidOperationException Busy(Func<string?>? findXaeMessageBox = null)
    {
        string? box;
        try { box = (findXaeMessageBox ?? Win32DialogAutomation.TryFindXaeMessageBox)(); }
        catch (Exception) { box = null; }
        return new InvalidOperationException("An operation is already in progress." + (box is null ? "" :
            $" The XAE shows a dialog the running call is waiting for — answer or close it in the XAE: '{box}'."));
    }

    /// <summary>
    /// Low-level acquire for complex async callers (e.g. apply_schema) that cannot use RunAsync.
    /// Must be paired with <see cref="Release"/> in a finally block.
    /// </summary>
    internal bool TryAcquire() => TryEnter();

    /// <summary>Releases the lock acquired via <see cref="TryAcquire"/>.</summary>
    internal void Release() => Exit();

    /// <summary>How long <see cref="Dispose"/> waits for an in-flight tool call
    /// (issue #154, A-12/F-30) to finish before disposing the lock out from
    /// under it anyway. Bounded — host shutdown must not hang forever on a
    /// stuck COM call.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(3);

    public void Dispose()
    {
        _disposed = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) > 0 && sw.Elapsed < DisposeDrainTimeout)
            Thread.Sleep(20);
        _lock.Dispose();
    }
}
