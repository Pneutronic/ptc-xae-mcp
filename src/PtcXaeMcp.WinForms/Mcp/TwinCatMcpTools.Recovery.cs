using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_login", ReadOnly = false, Destructive = true)]
    [Description("Log the XAE in to the PLC (Offline → Online), applying changes per mode. " +
                 "When the running application differs: 'online_change' applies changes without a stop (keeps values); " +
                 "'download' stops the PLC and transfers the full program (leaves it in STOP; start with twincat_plc_start); " +
                 "'login_without_change' attaches without transferring code (monitoring only). Answers the login modal and " +
                 "follow-up confirmations. Already online = no-op success. No reachable runtime or project not activated: " +
                 "success=false, isOnlineNow=false. online_change is verified on the PLC (online-change counter read over ADS " +
                 "before and after): onlineChange.verdict 'applied', 'not_needed', 'not_applied' (success then false), " +
                 "'download' (a fallback download replaced the application) or 'unknown' (counter unreadable); " +
                 "plcCycling false = application not running. Needs a compiled solution open. " +
                 "Returns { success, mode, dialogHandled, isOnlineNow, runState, message, onlineChange? }.")]
    public string Login(
        [Description("'online_change' (default) | 'download' | 'login_without_change'.")] string mode = "online_change",
        [Description("online_change only: if online change is impossible, download instead (stops the running PLC). Default false refuses with [Error].")] bool allowDownloadFallback = false,
        [Description("online_change only: ADS port of the PLC whose counter is read. Default 851 (first PLC).")] int adsPort = 851,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_login", new { mode, allowDownloadFallback, adsPort }, () =>
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
                var session = bridge.Dispatcher.Session;
                bool verify = loginMode == LoginMode.OnlineChange;
                ulong? countBefore = verify ? OnlineChangeEvidence.ReadCounter(session, adsPort) : null;
                var r = session.LoginPlc(loginMode, allowDownloadFallback);
                OnlineChangeReport? evidence = verify && r.Success
                    ? OnlineChangeEvidence.Capture(session, adsPort, r.DialogHandled, countBefore)
                    : null;
                bool success = r.Success && evidence?.Verdict != "not_applied";
                return JsonSerializer.Serialize(
                    new
                    {
                        success,
                        mode          = r.Mode,
                        dialogHandled = r.DialogHandled,
                        isOnlineNow   = r.IsOnlineNow,
                        runState      = r.RunState,
                        message       = evidence is null ? r.Message : $"{r.Message} {evidence.Message}",
                        onlineChange  = evidence is null ? null : new
                        {
                            verdict     = evidence.Verdict,
                            countBefore = evidence.CountBefore,
                            countAfter  = evidence.CountAfter,
                            plcCycling  = evidence.PlcCycling,
                        },
                    },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }), cancellationToken);

    [McpServerTool(Name = "twincat_logout", ReadOnly = false, Destructive = true)]
    [Description("Log the XAE out of the PLC (Online → Offline). " +
                 "No-op when already offline. Returns { wasOnline, isOnlineNow, message }. Call before " +
                 "twincat_clean_solution / twincat_rebuild_solution, which fail while Online. Needs an open solution.")]
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
    [Description("Clean the whole solution: remove compiler output and boot-project artefacts. " +
                 "The next build starts from scratch. Returns a message or [Error]. Needs an open solution.")]
    public string CleanSolution(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_clean_solution", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var r = bridge.Dispatcher.Session.CleanSolution();
                return r.Success ? r.Message : $"[Error] {r.Message}";
            }), cancellationToken);

    [McpServerTool(Name = "twincat_rebuild_solution", ReadOnly = false, Destructive = false)]
    [Description("Rebuild the whole solution (Clean, then Build), like 'Rebuild All'. " +
                 "Compile only: no activation or runtime restart (use twincat_activate_config). " +
                 "Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong " +
                 "version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the " +
                 "XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could " +
                 "overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. " +
                 "With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only " +
                 "objects a task's program reaches are compiled). Needs an open solution.")]
    public string RebuildSolution(
        [Description("Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip.")] string[]? expectSymbols = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_rebuild_solution", new { ignoreStaleFiles, expectSymbols }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).RebuildSolution(ignoreStaleFiles, expectSymbols)), cancellationToken);

    [McpServerTool(Name = "twincat_rebuild_plc", ReadOnly = false, Destructive = false)]
    [Description("Rebuild one PLC project (Clean, then Build). " +
                 "Compile only: no activation or runtime restart. " +
                 "Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong " +
                 "version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the " +
                 "XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could " +
                 "overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. " +
                 "With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only " +
                 "objects a task's program reaches are compiled). Needs an open solution.")]
    public string RebuildPlc(
        [Description("PLC project name (e.g. 'PLC'). Empty = auto-detect.")] string? plcProjectName = null,
        [Description("Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip.")] string[]? expectSymbols = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_rebuild_plc", new { plcProjectName, ignoreStaleFiles, expectSymbols }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).RebuildPlc(plcProjectName, ignoreStaleFiles, expectSymbols)), cancellationToken);

    [McpServerTool(Name = "twincat_force_close_xae", ReadOnly = false, Destructive = true)]
    [Description("Force-close a stale or crashed XAE (TcXaeShell); its unsaved work is lost. " +
                 "Explicit recovery only; no other tool terminates the XAE. Asks each instance to close and kills those that " +
                 "do not exit in time. Refuses while a dismissable modal (security code / restart / trial license) is open — " +
                 "that XAE is alive; use twincat_dismiss_security_dialog. Use only when twincat_get_status reports a " +
                 "stale/crashed instance or a multi-XAE block you cannot clear otherwise.")]
    public string ForceCloseXae(
        [Description("PID of the one TcXaeShell to close (from the twincat_get_status multi-XAE list), leaving the other instances and their unsaved work alone. Omit to close all.")] int? pid = null,
        CancellationToken cancellationToken = default)
        // Control/recovery command: bypass the operation gate so a wedged
        // in-flight call can never deadlock the very tool meant to recover it
        // (same rationale as twincat_stop / twincat_shutdown). Deliberately not
        // routed through Dispatcher.Run for the same reason.
        => bridge.Activity.Trace("twincat_force_close_xae", new { pid },
            () => new RecoveryCoordinator(bridge.Dispatcher.Session).ForceCloseXae(pid), cancellationToken,
            bypassGate: true);
}
