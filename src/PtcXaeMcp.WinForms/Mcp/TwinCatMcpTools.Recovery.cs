using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_login", ReadOnly = false, Destructive = true)]
    [Description("Log the PLC online (Offline → Online). " +
                 "Parameter mode selects the apply strategy when the running application differs from the project: " +
                 "'online_change' — apply changes to the running PLC without a stop (fast, retains variable values); " +
                 "'download' — stop the PLC, transfer the full compiled program (leaves PLC in STOP — no auto-run); " +
                 "'login_without_change' — attach to the running PLC without transferring code (monitoring only). " +
                 "Primary path: discovers the TwinCAT PLC Login DTE command ('Loginto' substring) and executes it. " +
                 "A Win32 dialog watcher handles the 3-way login modal and follow-up confirmations. " +
                 "Idempotent: if the PLC Logout command is available (already online), returns no-op success. " +
                 "If Login AND Logout are both disabled (no reachable target runtime / project not activated), " +
                 "returns success=false, isOnlineNow=false — never a false 'already online'. " +
                 "Online state is verified after login via the PLC Logout command's IsAvailable flag " +
                 "(same probe used by twincat_get_plc_online_state — correct after activate_config). " +
                 "Requires XAE running with a compiled solution open. " +
                 "Returns { success, mode, dialogHandled, isOnlineNow, runState, message }.")]
    public string Login(
        [Description("Apply mode: 'online_change' | 'download' | 'login_without_change'.")] string mode = "online_change",
        [Description("mode='online_change' only: when online change is not possible, accept a full download instead (stops the running PLC). Default false — the login is refused with an [Error]. Download confirmations are never answered 'Ja' for 'login_without_change'.")] bool allowDownloadFallback = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_login", new { mode, allowDownloadFallback }, () =>
            bridge.Dispatcher.Run(() =>
            {
                LoginMode loginMode = mode switch
                {
                    "online_change"        => LoginMode.OnlineChange,
                    "download"             => LoginMode.Download,
                    "login_without_change" => LoginMode.LoginWithoutChange,
                    _ => throw new ArgumentException(
                        $"Unknown login mode '{mode}'. " +
                        "Valid values: online_change, download, login_without_change."),
                };
                var r = bridge.Dispatcher.Session.LoginPlc(loginMode, allowDownloadFallback);
                return JsonSerializer.Serialize(
                    new
                    {
                        success       = r.Success,
                        mode          = r.Mode,
                        dialogHandled = r.DialogHandled,
                        isOnlineNow   = r.IsOnlineNow,
                        runState      = r.RunState,
                        message       = r.Message,
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }), cancellationToken);

    [McpServerTool(Name = "twincat_logout", ReadOnly = false, Destructive = true)]
    [Description("Log out the PLC project (Online → Offline). " +
                 "Primary path: executes the TwinCAT PLC Logout DTE command (its IsAvailable flag is the " +
                 "reliable online signal — it stays correct after activate_config, unlike DTE.Debugger.CurrentMode). " +
                 "Fallback (older TC): ConsumeXml('<LogoutCmd/>') / DTE.Debugger.Stop(). " +
                 "Idempotent: returns a no-op result when the PLC is already offline. " +
                 "Returns { wasOnline, isOnlineNow, message }. " +
                 "Call before twincat_clean_solution / twincat_rebuild_solution to avoid " +
                 "build failures caused by an active Online connection. " +
                 "Requires XAE running with a solution open.")]
    public string Logout(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_logout", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var r = bridge.Dispatcher.Session.LogoutPlc();
                return JsonSerializer.Serialize(
                    new { wasOnline = r.WasOnline, isOnlineNow = r.IsOnlineNow, message = r.Message },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }), cancellationToken);

    [McpServerTool(Name = "twincat_clean_solution", ReadOnly = false, Destructive = false)]
    [Description("Clean the entire TwinCAT solution via DTE.Solution.SolutionBuild.Clean(). " +
                 "Removes compiler output and boot-project artefacts so the next build starts from scratch. " +
                 "Returns { success, message }. " +
                 "Requires XAE running with a solution open.")]
    public string CleanSolution(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_clean_solution", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var r = bridge.Dispatcher.Session.CleanSolution();
                return r.Success ? r.Message : $"[Error] {r.Message}";
            }), cancellationToken);

    [McpServerTool(Name = "twincat_rebuild_solution", ReadOnly = false, Destructive = false)]
    [Description("Rebuild the entire TwinCAT solution (Clean then Build). " +
                 "Forces a complete recompilation — equivalent to 'Rebuild All' in the IDE. " +
                 "Compile-only: does NOT activate the boot project or restart the runtime " +
                 "(use twincat_activate_config for that). " +
                 "Returns { success, failedProjects, errors[] } identical to twincat_build_solution. " +
                 "Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). Requires XAE running with a solution open.")]
    public string RebuildSolution(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_rebuild_solution", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).RebuildSolution()), cancellationToken);

    [McpServerTool(Name = "twincat_rebuild_plc", ReadOnly = false, Destructive = false)]
    [Description("Rebuild a single PLC project (Clean then BuildProject). " +
                 "Forces a complete recompilation of the named (or auto-detected) PLC project. " +
                 "Compile-only: does NOT activate the boot project or restart the runtime. " +
                 "Returns { success, failedProjects, errors[] } identical to twincat_build_plc. " +
                 "Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). Requires XAE running with a solution open.")]
    public string RebuildPlc(
        [Description("Name of the PLC project to rebuild (e.g. 'PLC'). Leave empty to auto-detect.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_rebuild_plc", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).RebuildPlc(plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_force_close_xae", ReadOnly = false, Destructive = true)]
    [Description("EXPLICIT, opt-in recovery for a stale or crashed TcXaeShell (XAE) instance. " +
                 "Never invoked implicitly — a normal mutating tool will never terminate XAE. " +
                 "Asks each TcXaeShell to close gracefully and only kills the ones that do not " +
                 "exit in time. REFUSES while a dismissable modal dialog (security code / restart / " +
                 "trial license) is open — that instance is alive, not a zombie; use " +
                 "twincat_dismiss_security_dialog instead. Terminating XAE discards any unsaved " +
                 "work in it. Use only when twincat_get_status reports a stale/crashed instance " +
                 "or a multi-XAE block you cannot otherwise clear. " +
                 "pid: close only that one TcXaeShell process (see the PID list in a multi-XAE " +
                 "twincat_get_status block reason) instead of every running instance — use this " +
                 "when several instances are running and only one is confirmed stale, so the " +
                 "others (possibly holding unsaved work) are left alone.")]
    public string ForceCloseXae(
        [Description("PID of one specific TcXaeShell process to close. Omit to close all (original behaviour).")] int? pid = null,
        CancellationToken cancellationToken = default)
        // Control/recovery command: bypass the operation gate so a wedged
        // in-flight call can never deadlock the very tool meant to recover it
        // (same rationale as twincat_stop / twincat_shutdown). Deliberately not
        // routed through Dispatcher.Run for the same reason.
        => bridge.Activity.Trace("twincat_force_close_xae", new { pid },
            () => new RecoveryCoordinator(bridge.Dispatcher.Session).ForceCloseXae(pid), cancellationToken,
            bypassGate: true);
}
