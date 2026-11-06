using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Outcome of a <see cref="GuiComDispatchRunner"/> run: either a successful
/// <see cref="Value"/>, or a message to show the user (dispatcher busy,
/// <see cref="XaeBlockedException"/>, or any other exception the action
/// threw). Deliberately does not throw — every GUI handler that uses this
/// seam gets a value to render, never an exception to catch (issue #154).
/// </summary>
internal readonly struct GuiComOutcome<T>
{
    public bool Ok { get; }
    public T? Value { get; }
    public string? ErrorMessage { get; }

    private GuiComOutcome(bool ok, T? value, string? errorMessage)
    {
        Ok = ok;
        Value = value;
        ErrorMessage = errorMessage;
    }

    public static GuiComOutcome<T> Success(T value) => new(true, value, null);
    public static GuiComOutcome<T> Failure(string message) => new(false, default, message);
}

/// <summary>
/// The seam issue #154 asks for: the acquire/run/release/busy-message logic
/// every GUI handler that touches the TwinCAT session needs, extracted out of
/// <c>Form1</c> so it is unit-testable without a WinForms message loop or a
/// live XAE. <c>Views.ApprovalsView.RunGuiComActionAsync</c> (the IBN Live
/// actions, issue #270) is a thin wrapper around an instance of this class.
/// <para>
/// Behaviour (each covered by a test in <c>GuiComDispatchRunnerTests</c>):
/// </para>
/// <list type="bullet">
/// <item>the dispatcher lock is busy (another GUI action or an MCP call holds
/// it) -&gt; <see cref="GuiComOutcome{T}.Failure"/> with the same "operation
/// already in progress" message the MCP side uses (<see cref="McpDispatcher.Busy"/>);</item>
/// <item>the action throws <see cref="XaeBlockedException"/> -&gt; failure
/// message names the blocking dialog when one was found;</item>
/// <item>the action throws any other exception -&gt; failure message carries
/// <c>ex.Message</c>;</item>
/// <item>the lock is released in every case above, including when the action
/// throws — a caller can immediately run another action afterwards.</item>
/// </list>
/// </summary>
internal sealed class GuiComDispatchRunner
{
    private readonly McpDispatcher _dispatcher;

    public GuiComDispatchRunner(McpDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Runs <paramref name="action"/> off the calling thread (via
    /// <see cref="Task.Run(Func{Task})"/> overload for a synchronous
    /// <see cref="Func{TResult}"/>), guarded by the dispatcher's
    /// single-operation lock. Never throws.
    /// </summary>
    public async Task<GuiComOutcome<T>> RunAsync<T>(Func<T> action)
    {
        if (!_dispatcher.TryAcquire())
            return GuiComOutcome<T>.Failure($"[Error] {_dispatcher.BusyNow().Message}");

        try
        {
            T result = await Task.Run(action).ConfigureAwait(true);
            return GuiComOutcome<T>.Success(result);
        }
        catch (XaeBlockedException ex)
        {
            return GuiComOutcome<T>.Failure(ex.Dialog is null
                ? $"[Error] TwinCAT XAE is blocked: {ex.Message}"
                : $"[Error] TwinCAT XAE is blocked by dialog '{ex.Dialog}' — answer or close it in the XAE. {ex.Message}");
        }
        catch (Exception ex)
        {
            return GuiComOutcome<T>.Failure($"[Error] {ex.Message}");
        }
        finally
        {
            _dispatcher.Release();
        }
    }
}
