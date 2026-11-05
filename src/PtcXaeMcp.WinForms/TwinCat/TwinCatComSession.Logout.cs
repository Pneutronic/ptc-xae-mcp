namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Logout / Clean / Rebuild (feature/build-recovery, issue #127) --------
    //
    // At 100000 the XAE was still Online (logged in) when rebuild was attempted,
    // which caused the build to fail with no readable errors. The correct recovery
    // sequence is: logout → clean → rebuild → read errors[].
    //
    // Logout / online-state mechanism (verified live on Tc3 4026.x):
    //   TwinCAT exposes the PLC Login/Logout toolbar actions as DTE commands whose
    //   names carry a dynamic, locale-dependent suffix — e.g. "SPS.Logoutfromnone"
    //   on a German XAE. The Logout command's IsAvailable flag is the RELIABLE
    //   online signal: available ⟺ the IDE is logged into the PLC (Online).
    //
    //   This matters because DTE.Debugger.CurrentMode stays at Design (1) even when
    //   the PLC is online after activate_config — TwinCAT's PLC login does not engage
    //   the VS Shell debugger, so the debugger mode wrongly reads "offline" (#127).
    //   ITcSysManager.IsOnline / plcNode.IsOnline are not exposed on this version
    //   either. ConsumeXml("<LogoutCmd/>") on the PLC node is rejected. Only the
    //   PLC Logout DTE command both reflects (IsAvailable) and performs the logout.
    //
    //   Commands are matched by the "Logoutfrom" / "Loginto" substring so the
    //   locale prefix (SPS./PLC.) and target suffix are irrelevant.
    //
    // Infosys ref (logout → offline action):
    //   https://infosys.beckhoff.com/content/1033/tc3_userinterface/2531428235.html

    // Available=null: IsAvailable could not be read (non-busy COM failure) — "unknown", never
    // "disabled" (issue #148, B-1: an unreadable flag used to become "offline").
    internal readonly record struct PlcOnlineCommand(string Name, bool? Available);

    /// <summary>
    /// Locate the TwinCAT PLC Logout DTE command (dynamic name, e.g.
    /// "SPS.Logoutfromnone"). Its IsAvailable flag is true ⟺ the PLC is Online.
    /// Returns null when no such command exists (non-TwinCAT shell / no PLC project).
    /// </summary>
    private static PlcOnlineCommand? FindPlcLogoutCommand(dynamic dte) =>
        FindPlcCommandContaining(dte, "Logoutfrom");

    // Command name last found per needle. Walking every DTE command takes seconds (several thousand
    // commands) and runs before each PLC write (EnsureOfflineForWrite); a name that no longer
    // resolves falls back to the walk.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PlcCommandNames = new();

    private static PlcOnlineCommand? FindPlcCommandContaining(dynamic dte, string needle)
    {
        if (PlcCommandNames.TryGetValue(needle, out string? known))
        {
            try
            {
                dynamic cmd = dte.Commands.Item(known, 0);
                string name = (string)cmd.Name ?? string.Empty;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return new PlcOnlineCommand(name, ReadAvailable(cmd));
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* gone or renamed — walk */ }
            PlcCommandNames.TryRemove(needle, out _);
        }

        try
        {
            foreach (dynamic cmd in dte.Commands)
            {
                string name;
                try { name = (string)cmd.Name ?? string.Empty; }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;

                PlcCommandNames[needle] = name;
                return new PlcOnlineCommand(name, ReadAvailable(cmd));
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* Commands enumeration unavailable — caller falls back */ }
        return null;
    }

    private static bool? ReadAvailable(dynamic cmd)
    {
        try { return (bool)cmd.IsAvailable; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    private static readonly TimeSpan LogoutSettleTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LoginSettleTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SettleInterval = TimeSpan.FromMilliseconds(250);

    // Reads both flags by command name, without walking every DTE command; null while the XAE
    // rejects calls (it does so for seconds while it logs in or out).
    private static Func<PlcOnlineFlags?> OnlineFlagsProbe(object dteObj, string? loginName, string logoutName) => () =>
    {
        dynamic dte = dteObj;
        try
        {
            bool? logout = ReadCommandAvailable(dte, logoutName);
            bool? login = loginName is null ? (logout is null ? null : !logout) : ReadCommandAvailable(dte, loginName);
            return new PlcOnlineFlags(login, logout);
        }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return null; }
    };

    private static bool? ReadCommandAvailable(dynamic dte, string name)
    {
        try { return (bool)dte.Commands.Item(name, 0).IsAvailable; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Each step retries only itself. A retry of the whole logout after the command had run read
    // the state mid-transition and answered "already offline — no-op" for a logout it had done
    // (live 2026-11-05); for a login it could execute the Login command a second time.
    public LogoutPlcResult LogoutPlc()
    {
        var (dteObj, logoutCmd) = ComBusyRetry.Run<(object, PlcOnlineCommand?)>(() =>
        {
            dynamic d = GetActiveTcDte();
            return ((object)d, FindPlcLogoutCommand(d));
        }, OpenTimeout);
        dynamic dte = dteObj;

        // ── Primary: the PLC Logout DTE command ───────────────────────────────
        // Its availability is the authoritative online state; executing it logs out.
        if (logoutCmd is { } lc)
        {
            if (lc.Available is null)
                return new LogoutPlcResult(false, null,
                    $"Online state unknown: IsAvailable of the PLC Logout command '{lc.Name}' could not be read.");
            if (lc.Available == false)
                return new LogoutPlcResult(false, false,
                    $"PLC already offline (Logout command '{lc.Name}' is disabled) — no-op.");

            bool executed;
            try
            {
                // A call the XAE rejects did not run, so retrying the call alone is safe.
                ComBusyRetry.Run(() => { dte.ExecuteCommand(lc.Name); }, OpenTimeout);
                executed = true;
            }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { executed = false; }   // fall through to the legacy paths

            if (executed)
            {
                PlcOnlineCommand? loginCmd = ComBusyRetry.Run(() => (PlcOnlineCommand?)FindPlcLoginCommand(dte), OpenTimeout);
                PlcOnlineSettled after = PlcOnlineSettle.Wait(
                    OnlineFlagsProbe(dteObj, loginCmd?.Name, lc.Name), expectOnline: false,
                    LogoutSettleTimeout, minWaitForOther: TimeSpan.FromSeconds(3), SettleInterval);
                string unsettled = after.Settled ? "" : $" (the state had not settled after {LogoutSettleTimeout.TotalSeconds:0} s)";
                return new LogoutPlcResult(true, after.Online,
                    after.Online switch
                    {
                        false => $"PLC logged out via '{lc.Name}' — now offline{unsettled}.",
                        true  => $"Logout command '{lc.Name}' executed but PLC still appears online{unsettled}.",
                        null  => $"Logout command '{lc.Name}' executed but the online state could not be read back.",
                    });
            }
        }

        return ComBusyRetry.Run(() => LegacyLogout(dteObj), OpenTimeout);
    }

    private LogoutPlcResult LegacyLogout(object dteObj)
    {
        dynamic dte = dteObj;
        // ── Legacy fallback (older TC versions where the command isn't found) ──
        // ConsumeXml("<LogoutCmd/>") on the PLC source-project node, then
        // DTE.Debugger.Stop(). Guarded by best-effort online detection.
        dynamic? plcNode = FindPlcSourceProjectNode(dte);

        bool? wasOnline = null;
        try
        {
            int mode = (int)dte.Debugger.CurrentMode;
            if (mode != 1) wasOnline = true;   // Run/Break ⟹ definitely online
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        if (wasOnline is null && plcNode is not null)
        {
            try { wasOnline = (bool)plcNode.IsOnline; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        if (wasOnline == false)
            return new LogoutPlcResult(false, false, "PLC was already offline — no-op.");

        string logoutMethod = "none";
        if (plcNode is not null)
        {
            try { plcNode.ConsumeXml("<LogoutCmd/>"); logoutMethod = "ConsumeXml(<LogoutCmd/>)"; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        if (logoutMethod == "none")
        {
            try { dte.Debugger.Stop(); logoutMethod = "Debugger.Stop()"; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }

        Thread.Sleep(500);

        bool? isOnlineNow = null;
        try
        {
            int mode = (int)dte.Debugger.CurrentMode;
            if (mode != 1) isOnlineNow = true;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        if (isOnlineNow is null && plcNode is not null)
        {
            try { isOnlineNow = (bool)plcNode.IsOnline; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }

        string msg = logoutMethod != "none"
            ? (isOnlineNow != true
                ? $"PLC logged out via {logoutMethod} — now offline."
                : $"Logout attempted via {logoutMethod} but PLC is still online.")
            : (wasOnline is null
                ? "No active PLC login found — likely already offline."
                : "Could not log out PLC — all methods failed; PLC may still be online.");

        return new LogoutPlcResult(
            WasOnline: wasOnline ?? (logoutMethod != "none"),
            IsOnlineNow: isOnlineNow,
            Message: msg);
    }

    // CleanSolution and Rebuild* are implemented in TwinCatComSession.Build.cs
    // (partial class — they live there to co-locate with SolutionBuild usage).
}
