using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// The XAE could not serve a COM call: it rejected every attempt (RPC_E_CALL_REJECTED /
/// RPC_E_SERVERCALL_RETRYLATER) until the deadline, or it is held by a modal window.
/// A <b>final</b> verdict: <see cref="ComCall.Run{T}"/> never retries it (so nested
/// retry scopes do not multiply their budgets), and <see cref="ComCall.IsBusy"/> treats it
/// as busy, so every busy-aware <c>catch … when (ComCall.NotBusy(ex))</c> lets it through.
/// </summary>
internal sealed class XaeBlockedException : InvalidOperationException
{
    /// <summary>Title (plus text for message boxes) of the blocking XAE window, when one was found.</summary>
    public string? Dialog { get; }

    public XaeBlockedException(string message, string? dialog, Exception? inner)
        : base(message, inner)
    {
        Dialog = dialog;
        HResult = inner is not null && ComCall.IsBusyHResult(inner.HResult)
            ? inner.HResult
            : ComCall.RPC_E_SERVERCALL_RETRYLATER;
    }
}

/// <summary>
/// Central COM access helpers (issue #149). Every <c>TwinCatComSession.*</c> partial goes
/// through these instead of bare <c>catch { }</c> blocks:
/// <list type="bullet">
/// <item><see cref="IsBusy"/> — recognises "XAE busy" in every shape it reaches managed code:
/// <see cref="COMException"/> with the HRESULT, the C# runtime binder's
/// <c>MissingMemberException("Could not get dispatch ID for X (error: 0x8001010A)")</c> raised when
/// <c>IDispatch::GetIDsOfNames</c> is rejected, <c>TargetInvocationException</c>/wrapper inner
/// exceptions, and <see cref="XaeBlockedException"/>.</item>
/// <item><see cref="NotBusy"/> — the exception filter for every best-effort catch: genuine
/// "member not present / wrong node" failures still take the fallback, busy never does.</item>
/// <item><see cref="ReadProp{T}(Func{T}, T)"/> — per-node property read with a default only for
/// genuine failures.</item>
/// <item><see cref="Run{T}"/> — Stopwatch deadline for busy retries; a visible window owned by the
/// XAE (any class) that persists for <see cref="ModalGrace"/> ends the wait early with
/// <c>XAE is blocked by dialog '…'</c>.</item>
/// </list>
/// </summary>
internal static class ComCall
{
    internal const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
    internal const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);

    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a blocking XAE window must stay up while calls are rejected before a call gives
    /// up early. Short-lived progress windows come and go within this; an orphaned modal
    /// (the Go-To dialog of #150) does not.
    /// </summary>
    internal static readonly TimeSpan ModalGrace = TimeSpan.FromMilliseconds(1500);

    // Markers the runtime binder / wrappers leave in messages when the HRESULT itself is lost
    // (live 2026-09-26: "Could not get dispatch ID for Build (error: 0x8001010A)").
    private static readonly string[] BusyMarkers =
    [
        "0x8001010A", "0x80010001", "RPC_E_SERVERCALL_RETRYLATER", "RPC_E_CALL_REJECTED",
    ];

    public static bool IsBusyHResult(int hResult) =>
        hResult == RPC_E_CALL_REJECTED || hResult == RPC_E_SERVERCALL_RETRYLATER;

    /// <summary>True when <paramref name="ex"/> (or any exception it wraps) means "XAE busy/blocked".</summary>
    public static bool IsBusy(Exception? ex)
    {
        for (int depth = 0; ex is not null && depth < 10; depth++)
        {
            if (ex is XaeBlockedException) return true;
            if (IsBusyHResult(ex.HResult)) return true;
            string msg = ex.Message ?? string.Empty;
            foreach (var marker in BusyMarkers)
                if (msg.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (ex is AggregateException agg)
                foreach (var inner in agg.InnerExceptions)
                    if (IsBusy(inner)) return true;
            ex = ex.InnerException;
        }
        return false;
    }

    /// <summary>Exception filter for best-effort catches: <c>catch (Exception ex) when (ComCall.NotBusy(ex))</c>.</summary>
    public static bool NotBusy(Exception ex) => !IsBusy(ex);

    /// <summary>
    /// Exception filter for best-effort catches that must also let cancellation through
    /// (issue #155 E-12): <c>catch (Exception ex) when (ComCall.Recoverable(ex))</c>.
    /// False for a busy/blocked XAE and for <see cref="OperationCanceledException"/>, so neither
    /// is turned into "not found" / a fallback value.
    /// </summary>
    public static bool Recoverable(Exception ex) => ex is not OperationCanceledException && NotBusy(ex);

    /// <summary>True when the chain already carries a final <see cref="XaeBlockedException"/>.</summary>
    internal static bool IsFinal(Exception? ex)
    {
        for (int depth = 0; ex is not null && depth < 10; depth++, ex = ex.InnerException)
            if (ex is XaeBlockedException) return true;
        return false;
    }

    /// <summary>
    /// Per-node property/method read: returns <paramref name="fallback"/> only when the read fails
    /// for a genuine reason (member not present on this node, wrong node kind, E_FAIL …);
    /// a busy/blocked XAE is rethrown so the caller's retry scope — not a silently truncated
    /// result — handles it.
    /// </summary>
    public static T ReadProp<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (NotBusy(ex)) { return fallback; }
    }

    /// <summary><see cref="ReadProp{T}(Func{T}, T)"/> with <c>default</c> as the fallback.</summary>
    public static T? ReadProp<T>(Func<T> read) => ReadProp<T?>(() => read(), default);

    /// <summary>
    /// Best-effort action (a probe or cleanup whose failure does not change the result): false on a
    /// genuine failure, busy is rethrown.
    /// </summary>
    public static bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception ex) when (NotBusy(ex)) { return false; }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, retrying while the XAE rejects calls, bounded by a Stopwatch
    /// deadline. Gives up early with <see cref="XaeBlockedException"/> when a blocking XAE window
    /// stays up for <see cref="ModalGrace"/>, and at the deadline otherwise. Non-busy exceptions
    /// and final <see cref="XaeBlockedException"/>s propagate unchanged on the first attempt.
    /// </summary>
    public static T Run<T>(
        Func<T> action,
        TimeSpan timeout,
        TimeSpan? delay = null,
        Func<string?>? findBlockingDialog = null,
        string? operation = null,
        Func<TimeSpan>? elapsedOverride = null)
    {
        var step = delay ?? DefaultDelay;
        var probe = findBlockingDialog ?? XaeModalProbe.FindBlockingDialog;
        var sw = Stopwatch.StartNew();
        // Test-only seam (issue #157 item 7): a real Stopwatch makes the
        // "exhausts timeout" scenario depend on wall-clock/JIT/scheduling
        // jitter (flaky under load with a tight timeout). Production callers
        // never pass elapsedOverride, so behaviour there is unchanged.
        Func<TimeSpan> elapsed = elapsedOverride ?? (() => sw.Elapsed);
        string? modal = null;
        TimeSpan modalSince = TimeSpan.Zero;

        while (true)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (IsBusy(ex) && !IsFinal(ex))
            {
                string? seen = SafeProbe(probe);
                if (seen is null) modal = null;
                else if (!string.Equals(seen, modal, StringComparison.Ordinal))
                {
                    modal = seen;
                    modalSince = elapsed();
                }

                if (modal is not null && elapsed() - modalSince >= ModalGrace)
                    throw Blocked(modal, elapsed(), ex, operation);
                if (elapsed() >= timeout)
                    throw Blocked(modal, elapsed(), ex, operation);
                Thread.Sleep(step);
            }
        }
    }

    public static void Run(
        Action action,
        TimeSpan timeout,
        TimeSpan? delay = null,
        Func<string?>? findBlockingDialog = null,
        string? operation = null)
        => Run<object?>(() => { action(); return null; }, timeout, delay, findBlockingDialog, operation);

    /// <summary>
    /// Error text for an exception that reached a tool boundary. Busy exceptions that never
    /// went through <see cref="Run{T}"/> still get the blocking dialog named.
    /// </summary>
    public static string Describe(
        Exception ex, Func<string?>? findBlockingDialog = null, Func<string?>? bindingContext = null)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is XaeBlockedException blocked) return blocked.Message;
        if (!IsBusy(ex))
        {
            // A binding failure on the DTE ("… does not contain a definition for …") reads like a broken build;
            // name the XAE host the session is attached to, the usual cause being a 32-/64-bit mismatch.
            string? context = IsBindingFailure(ex) ? SafeProbe(bindingContext ?? HostSelection.XaeHostReport.CurrentBindingContext) : null;
            return context is null ? ex.Message : $"{ex.Message} {context}";
        }
        string? dialog = SafeProbe(findBlockingDialog ?? XaeModalProbe.FindBlockingDialog);
        return dialog is not null
            ? BlockedByDialogMessage(dialog, null, null) + $" ({ex.Message})"
            : $"XAE is busy and rejected the call ({BusyCode(ex)}): {ex.Message}";
    }

    internal static XaeBlockedException Blocked(string? dialog, TimeSpan waited, Exception inner, string? operation = null)
    {
        string message = dialog is not null
            ? BlockedByDialogMessage(dialog, waited, operation)
            : $"XAE is busy — every call{For(operation)} was rejected ({BusyCode(inner)}) for " +
              $"{waited.TotalSeconds:F1}s and no blocking XAE window was found. Retry when the XAE is idle.";
        return new XaeBlockedException(message, dialog, inner);
    }

    internal static string BlockedByDialogMessage(string dialog, TimeSpan? waited, string? operation) =>
        $"XAE is blocked by dialog '{dialog}' — calls{For(operation)} are rejected" +
        (waited is null ? "" : $" (waited {waited.Value.TotalSeconds:F1}s)") +
        ". Close or answer the dialog in the XAE, then retry.";

    private static string For(string? operation) =>
        string.IsNullOrWhiteSpace(operation) ? "" : $" for {operation}";

    private static string BusyCode(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e.HResult == RPC_E_CALL_REJECTED) return "RPC_E_CALL_REJECTED";
            if (e.HResult == RPC_E_SERVERCALL_RETRYLATER) return "RPC_E_SERVERCALL_RETRYLATER";
        }
        return ex.Message.Contains("0x80010001", StringComparison.OrdinalIgnoreCase)
            ? "RPC_E_CALL_REJECTED"
            : "RPC_E_SERVERCALL_RETRYLATER";
    }

    private static bool IsBindingFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) return true;
        return false;
    }

    private static string? SafeProbe(Func<string?> probe)
    {
        // The probe is Win32 window enumeration (no COM). A failure there must never mask
        // the COM error being reported — it just means "no dialog known".
        try { return probe(); }
        catch (Exception) { return null; }
    }
}
