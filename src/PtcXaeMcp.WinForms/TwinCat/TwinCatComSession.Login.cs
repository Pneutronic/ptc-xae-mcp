namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Login (PLC online) — feature/plc-login --------------------------------
    //
    // Symmetric to LogoutPlc() in TwinCatComSession.Logout.cs.
    // Primary path: the PLC Login DTE command (name contains "Loginto"),
    // discovered dynamically so locale/suffix changes are irrelevant.
    //   Online  ⟺ Logout command IsAvailable (probe via FindPlcLogoutCommand)
    //   Offline ⟺ Login  command IsAvailable (probe via FindPlcLoginCommand)
    //
    // Precheck (issue #148, LIVE-4): the state comes from the LOGOUT command only
    // (PlcOnlineInference). Logout available ⇒ already online (idempotent success);
    // Login AND Logout disabled ⇒ no reachable target — an honest failure, never
    // "already online"; an unreadable flag ⇒ unknown — never success.
    // The Win32 LoginDialogWatcher is started before ExecuteCommand so it is
    // polling when XAE raises the 3-way modal (online-change / download /
    // without-changes). The watcher also handles first-download and
    // overwrite-running confirmations.
    //
    // Legacy fallback for older TC where the DTE command is absent:
    //   ConsumeXml("<LoginCmd>true</LoginCmd>") on the PLC source-project node.

    private static PlcOnlineCommand? FindPlcLoginCommand(dynamic dte) =>
        FindPlcCommandContaining(dte, "Loginto");

    // Each step retries only itself (see LogoutPlc): a retry of the whole login after the Login
    // command had run could execute it a second time — a second, unrequested download.
    public LoginPlcResult LoginPlc(LoginMode mode, bool allowDownloadFallback = false)
    {
        var (dteObj, loginCmd, logoutBefore, mainWindow) = ComBusyRetry.Run<(object, PlcOnlineCommand?, PlcOnlineCommand?, IntPtr)>(() =>
        {
            dynamic d = GetActiveTcDte();
            return ((object)d, FindPlcLoginCommand(d), FindPlcLogoutCommand(d), TryGetXaeMainWindow(d));
        }, OpenTimeout);
        dynamic dte = dteObj;

        // ── Primary: PLC Login DTE command ────────────────────────────────────
        if (loginCmd is { } lc)
        {
            switch (PlcOnlineInference.BeforeLogin(lc.Available, logoutBefore?.Available))
            {
                case LoginPrecheck.AlreadyOnline:
                    return new LoginPlcResult(
                        Success: true,
                        Mode: mode.ToString(),
                        DialogHandled: "none",
                        IsOnlineNow: true,
                        RunState: ComBusyRetry.Run(() => DetermineRunState(dteObj), OpenTimeout),
                        Message: $"PLC already online (Logout command '{logoutBefore!.Value.Name}' is available) — no-op.");
                case LoginPrecheck.NoTarget:
                    return new LoginPlcResult(
                        Success: false,
                        Mode: mode.ToString(),
                        DialogHandled: "none",
                        IsOnlineNow: false,
                        RunState: null,
                        Message: $"[Error] Cannot log in: both the PLC Login command '{lc.Name}' and the Logout " +
                                 "command are disabled — the XAE is Offline and no login is possible (no reachable " +
                                 "target runtime, or the PLC project is not built/activated). Check the target " +
                                 "(twincat_set_target / twincat_activate_config) and retry.");
                case LoginPrecheck.Unknown:
                    return new LoginPlcResult(
                        Success: false,
                        Mode: mode.ToString(),
                        DialogHandled: "none",
                        IsOnlineNow: null,
                        RunState: null,
                        Message: $"[Error] Cannot log in: the PLC online state is unknown (IsAvailable of the " +
                                 $"Login command '{lc.Name}' or of the Logout command could not be read).");
            }

            // The watcher stays up until the state has settled: confirmations after the 3-way modal
            // (first download, replace the running application) can come seconds later.
            var watcher = new Win32DialogAutomation.LoginDialogWatcher(mode, allowDownloadFallback, mainWindow);
            PlcOnlineSettled after;
            watcher.Start();
            try
            {
                // A call the XAE rejects did not run, so retrying the call alone is safe.
                ComBusyRetry.Run(() => { dte.ExecuteCommand(lc.Name); }, OpenTimeout);
                // Verify: Logout command available ⟺ now online.
                after = PlcOnlineSettle.Wait(
                    OnlineFlagsProbe(dteObj, lc.Name, logoutBefore!.Value.Name),   // Proceed: the Logout flag was read
                    expectOnline: true, LoginSettleTimeout, minWaitForOther: TimeSpan.FromSeconds(5), SettleInterval);
            }
            finally
            {
                watcher.Stop();
            }

            bool? nowOnline = after.Online;
            string runState = nowOnline == true ? ComBusyRetry.Run(() => DetermineRunState(dteObj), OpenTimeout) : "unknown";

            return new LoginPlcResult(
                Success: nowOnline == true,
                Mode: mode.ToString(),
                DialogHandled: watcher.DialogHandled,
                IsOnlineNow: nowOnline,
                RunState: runState,
                Message: nowOnline == true
                    ? $"PLC logged in via '{lc.Name}' (mode: {mode}) — now online. Run state: {runState}."
                    : $"[Error] Login command '{lc.Name}' executed but the PLC is not online afterwards " +
                      $"(online state: {(nowOnline is null ? "unknown" : "offline")}; dialog: {watcher.DialogHandled})." +
                      (watcher.DialogHandled.Contains(Win32DialogAutomation.RefusedOnlineChange, StringComparison.Ordinal)
                          ? " Online change was not possible; pass allowDownloadFallback=true to accept a full download (stops the PLC)."
                          : "") +
                      (watcher.DialogHandled.Contains(Win32DialogAutomation.RefusedReplaceRunningApp, StringComparison.Ordinal)
                          ? " The running application must be replaced by a full download: use mode=download or " +
                            "allowDownloadFallback=true (a download replaces and restarts the running application)."
                          : ""));
        }

        return ComBusyRetry.Run(() => LegacyLogin(dteObj, mode), OpenTimeout);
    }

    private LoginPlcResult LegacyLogin(object dteObj, LoginMode mode)
    {
        dynamic dte = dteObj;
        // ── Legacy fallback (older TC without the DTE command) ────────────────
        dynamic? plcNode = FindPlcSourceProjectNode(dte);
        if (plcNode is null)
            return new LoginPlcResult(
                Success: false, Mode: mode.ToString(), DialogHandled: "none",
                IsOnlineNow: null, RunState: null,
                Message: "[Error] No PLC source project node found; cannot log in.");

        try
        {
            plcNode.ConsumeXml("<LoginCmd>true</LoginCmd>");
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new LoginPlcResult(
                Success: false, Mode: mode.ToString(), DialogHandled: "none",
                IsOnlineNow: null, RunState: null,
                Message: $"[Error] ConsumeXml(<LoginCmd>) failed: {ex.Message}");
        }

        Thread.Sleep(800);
        bool? isOnlineNow = null;
        try { var lc2 = FindPlcLogoutCommand(dte); isOnlineNow = lc2?.Available; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        return new LoginPlcResult(
            Success: isOnlineNow == true,
            Mode: mode.ToString(),
            DialogHandled: "ConsumeXml-fallback",
            IsOnlineNow: isOnlineNow,
            RunState: isOnlineNow == true ? DetermineRunState(dte) : null,
            Message: isOnlineNow == true
                ? $"PLC logged in via ConsumeXml fallback (mode: {mode}) — now online."
                : "[Error] ConsumeXml(<LoginCmd>) sent but PLC online state could not be confirmed.");
    }

    // Best-effort: try to read run state from DTE debugger mode.
    // Returns "running" / "stopped" / "unknown" — never throws.
    private static string DetermineRunState(dynamic dte)
    {
        try
        {
            int debugMode = (int)dte.Debugger.CurrentMode;
            // VS Shell: 1=Design, 2=Break, 3=Run; TwinCAT re-uses Run=3 for
            // PLC running. After a Download login the PLC is in STOP → Break(2)
            // or Design(1) while the runtime is online.
            return debugMode == 3 ? "running" : debugMode == 2 ? "stopped" : "unknown";
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return "unknown"; }
    }
}
