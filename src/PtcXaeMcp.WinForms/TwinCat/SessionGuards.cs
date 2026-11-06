namespace PtcXaeMcp.WinForms.TwinCat;

internal static class SessionGuards
{
    /// <summary>Pause before the second logout probe when the online state was unknown (issue #190).</summary>
    internal static readonly TimeSpan UnknownStateRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Common precondition for every mutating tool. The multi-XAE check is now
    /// delegated to the session: with &gt;1 TcXaeShell it no longer blocks
    /// purely on the count — it blocks only when it cannot deterministically
    /// bind to the instance with the expected solution open (issue #57 Q2,
    /// <see cref="ITwinCatSession.MultiXaeBlockReason"/>). 0/1 shells, or a
    /// unique deterministic match, pass through.
    /// </summary>
    /// <remarks>
    /// Issue #149 (LIVE-7): the four failure states are reported distinctly — no TcXaeShell
    /// process / process but DTE unreachable (<see cref="ITwinCatSession.DescribeUnreachable"/>),
    /// blocked by a modal (a busy probe that <see cref="ComCall.Run{T}"/> gave up on, naming the
    /// dialog) and no solution. A busy XAE used to answer "No solution is currently open" in 0.1 s.
    /// </remarks>
    public static string? RequireOpenSolution(ITwinCatSession session)
    {
        try
        {
            var multi = session.MultiXaeBlockReason();
            if (multi is not null) return multi;
            if (!session.IsConnected)
                return McpErrors.WithNext(session.DescribeUnreachable(),
                    "twincat_open_project(slnPath) starts or attaches the XAE; twincat_doctor checks the setup.");
            if (!session.HasOpenSolution())
                return McpErrors.WithNext("No solution is currently open.",
                    "twincat_open_project(slnPath) opens one; twincat_get_active_project shows what the XAE has.");
            return null;
        }
        catch (Exception ex) when (ComCall.IsBusy(ex))
        {
            return McpErrors.EnsureError(ComCall.Describe(ex));
        }
    }

    /// <summary>
    /// Precondition for every tool that edits PLC source (write/add/delete/rename
    /// a POU, GVL, DUT, INTERFACE, or member; PLCopen import). While the XAE is
    /// Online (logged in to the running PLC), edits made through the Automation
    /// Interface are not reliably transferred into the offline project source —
    /// a read-back on the same live COM node can still report success even
    /// though the change never lands in the project. So every mutating call logs
    /// out first (LogoutPlc is a cheap no-op when already Offline) and refuses to
    /// proceed if the attempt leaves the XAE confirmed Online.
    /// </summary>
    /// <remarks>
    /// Issue #148 (B-1): fail CLOSED. Only a confirmed <c>IsOnlineNow == false</c> lets the write
    /// proceed; <c>null</c> (state could not be determined) and any exception from the logout
    /// attempt (busy/blocked XAE included) refuse the write. Callers validate their target
    /// (<see cref="RequirePlcNode"/>) BEFORE calling this, because the logout itself is a side
    /// effect on a running plant.
    /// Issue #190: an unknown state is probed once more after <paramref name="unknownStateRetryDelay"/>
    /// (default 1 s) before refusing. Seen live: the PLC Logout command was momentarily not found
    /// right after a rename, the legacy fallback could not read the state either, and a delete was
    /// refused although the calls 2 s before and 1 s after it confirmed Offline. Still fail closed:
    /// only a confirmed Offline from the second probe lets the write proceed.
    /// </remarks>
    public static string? EnsureOfflineForWrite(ITwinCatSession session, TimeSpan? unknownStateRetryDelay = null)
    {
        LogoutPlcResult logout;
        try
        {
            logout = session.LogoutPlc();
            if (logout.IsOnlineNow is null)
            {
                Thread.Sleep(unknownStateRetryDelay ?? UnknownStateRetryDelay);
                logout = session.LogoutPlc();
            }
        }
        catch (Exception ex) when (ComCall.IsBusy(ex))
        {
            return McpErrors.EnsureError(
                $"Could not confirm the XAE is Offline before editing PLC code: {ComCall.Describe(ex)}");
        }
        catch (Exception ex)
        {
            return "[Error] Could not confirm the XAE is Offline before editing PLC code " +
                   $"(logout attempt failed: {ex.Message}). Nothing was changed.";
        }

        if (logout.IsOnlineNow == true)
            return "[Error] XAE is Online and could not be logged out automatically before " +
                   $"editing PLC code ({logout.Message}). Edits made while Online are not " +
                   "reliably transferred into the project — log out manually (twincat_logout) and retry.";
        if (logout.IsOnlineNow is null)
            return "[Error] Could not confirm the XAE is Offline before editing PLC code " +
                   $"({logout.Message}). Refusing to write — check twincat_get_plc_online_state, " +
                   "log out manually (twincat_logout) and retry.";
        return null;
    }

    /// <summary>
    /// Pre-logout target validation (issue #148, B-15): resolves <paramref name="treePath"/> and
    /// returns an "[Error] ..." when it does not exist, so a typo never costs a logout.
    /// </summary>
    public static string? RequirePlcNode(ITwinCatSession session, string treePath, string label)
    {
        PlcNodeInfo node;
        try { node = session.ProbePlcNode(treePath); }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        catch (Exception ex) { return McpErrors.EnsureError(ex.Message); }
        return node.Exists ? null : McpErrors.WithNext($"{label} not found: {treePath}", PlcKindResolver.NotFoundHint);
    }
}
