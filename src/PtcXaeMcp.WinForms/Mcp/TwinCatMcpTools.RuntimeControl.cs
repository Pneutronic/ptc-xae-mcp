using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_broadcast_search", ReadOnly = true)]
    [Description("Scan for reachable TwinCAT targets on the local network via ADS broadcast. " +
                 "Pass a hostname or IP in 'searchTarget' for a directed search (requires TC3 4020.10+), " +
                 "or omit for a general broadcast across all network interfaces. " +
                 "Returns a JSON array of { name, netId, ipAddr, version, os }. " +
                 "IMPORTANT: Must be called BEFORE twincat_set_target changes the project to a remote target. " +
                 "If the project already targets a remote system, set the target back to local first. " +
                 "Requires XAE running with a solution open.")]
    public string BroadcastSearch(
        [Description("Hostname or IP address to search for directly, e.g. 'CX-12345' or '192.168.1.5'. " +
                     "Omit to broadcast to all devices on the network.")] string? searchTarget = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_broadcast_search", new { searchTarget }, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).BroadcastSearch(searchTarget)), cancellationToken);

    [McpServerTool(Name = "twincat_add_route", ReadOnly = false, Destructive = true)]
    [Description("Add an ADS route to a remote TwinCAT target. " +
                 "Use 'projectRoute=true' for a project-local route (stored in .tsproj, preferred for isolation), " +
                 "or 'false' for a persistent system route (requires credentials). " +
                 "Call twincat_broadcast_search first to discover the target's netId and IP. " +
                 "Requires XAE running with a solution open.")]
    public string AddRoute(
        [Description("AMS NetId of the remote target, e.g. '192.168.1.5.1.1'.")] string? amsNetId = null,
        [Description("IP address of the remote target. Either ipAddr or hostName must be provided.")] string? ipAddr = null,
        [Description("Hostname of the remote target. Either ipAddr or hostName must be provided.")] string? hostName = null,
        [Description("Username for system route authentication (not needed for project routes).")] string? userName = null,
        [Description("Password for system route authentication (not needed for project routes).")] string? password = null,
        [Description("True = project route (stored in .tsproj, no credentials required). " +
                     "False = persistent system route (requires userName/password). Default: true.")] bool projectRoute = true,
        [Description("DEPRECATED alias for amsNetId, kept for one release so existing MCP clients that bind by this name keep working. Prefer amsNetId.")] string? netId = null,
        CancellationToken cancellationToken = default)
    {
        string netIdValue = ParamAliases.Coalesce(amsNetId, netId) ?? "";
        return bridge.Activity.Trace("twincat_add_route", new { netId = netIdValue, ipAddr, hostName, projectRoute },
            () => bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session)
                    .AddRoute(netIdValue, ipAddr, hostName, userName, password, projectRoute)), cancellationToken);
    }

    [McpServerTool(Name = "twincat_set_target", ReadOnly = false, Destructive = true)]
    [Description("Set the project's target NetId and optionally target platform. " +
                 "Call this BEFORE twincat_activate_config to avoid the platform-mismatch dialog. " +
                 "Known platforms: 'TwinCAT RT (x64)', 'TwinCAT RT (x86)', 'TwinCAT OS (x64)' (User Mode Runtime). " +
                 "Requires XAE running with a solution open.")]
    public string SetTarget(
        [Description("AMS NetId of the target system, e.g. '192.168.1.5.1.1' or '127.0.0.1.1.1'.")] string? amsNetId = null,
        [Description("Target platform string. Use 'TwinCAT OS (x64)' for User Mode Runtime. " +
                     "Omit to change only the NetId without touching the platform.")] string? platform = null,
        [Description("DEPRECATED alias for amsNetId, kept for one release. Prefer amsNetId.")] string? netId = null,
        CancellationToken cancellationToken = default)
    {
        string netIdValue = ParamAliases.Coalesce(amsNetId, netId) ?? "";
        return bridge.Activity.Trace("twincat_set_target", new { netId = netIdValue, platform }, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).SetTarget(netIdValue, platform)), cancellationToken);
    }

    [McpServerTool(Name = "twincat_set_system_mode", ReadOnly = false, Destructive = true)]
    [Description("Switch a target's TwinCAT system to Config or Run over ADS — the XAE's 'Restart TwinCAT (Config Mode)' / " +
                 "'Restart TwinCAT System': WriteControl Reconfig (config) or Reset (run) on AMS port 10000, then ReadState " +
                 "until the mode is confirmed. Use it when a running target ignores activate_config's restart (switch to " +
                 "config, activate, switch to run). Needs no XAE and no open solution. A switch stops the PLC on that target. " +
                 "After a switch to run the PLC runtime on port 851 is read as well (up to 15 s): the system can be in Run while the PLC stays stopped, which the result flags with [Warn]. " +
                 "amsNetId is required — there is no implicit local target.")]
    public string SetSystemMode(
        [Description("AMS NetId of the target system, e.g. '192.168.250.20.1.1'. Required; pass the local router's NetId " +
                     "explicitly to switch the local system.")] string amsNetId,
        [Description("'config' or 'run'.")] string mode,
        [Description("Seconds to wait for ReadState to confirm the mode. Default 60, clamped to 5..300.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_system_mode", new { amsNetId, mode, timeoutSeconds }, () =>
        {
            if (!AmsNetIdText.IsLiteral(amsNetId))
                return $"[Error] amsNetId must be an AMS NetId like '192.168.1.5.1.1' (got '{amsNetId}').";
            if (!SystemModeSwitch.TryParseMode(mode, out var m))
                return $"[Error] mode must be 'config' or 'run' (got '{mode}').";
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 60, 5, 300));
            using var control = new AdsSystemControl(amsNetId.Trim());
            return SystemModeSwitch.Switch(control, amsNetId.Trim(), m, timeout);
        }, cancellationToken);

    [McpServerTool(Name = "twincat_restart_runtime", ReadOnly = false, Destructive = true)]
    [Description("Call ITcSysManager.StartRestartTwinCAT() to start or restart the TwinCAT runtime on the target. " +
                 "Use after twincat_activate_config: activate generates the boot project, this command starts it. " +
                 "Requires XAE running with a solution open.")]
    public string RestartRuntime(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_restart_runtime", null, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).RestartRuntime()), cancellationToken);

    [McpServerTool(Name = "twincat_umr_list_instances", ReadOnly = true)]
    [Description("List the registered TwinCAT 3 Usermode Runtime instances on this machine " +
                 "(sub-directories of %ProgramData%\\Beckhoff\\TwinCAT\\3.1\\Runtimes that contain " +
                 "Start.bat + 3.1\\TcRegistry.xml). These names are valid 'instanceName' values for " +
                 "twincat_umr_start. Returns a comma-separated list, or a note if none are installed.")]
    public string UmrListInstances(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_list_instances", null, () =>
        {
            var names = host.UmrManager.EnumerateInstances();
            return names.Count == 0
                ? "(no registered UMR instances found)"
                : string.Join(", ", names);
        }, cancellationToken);

    [McpServerTool(Name = "twincat_umr_start", ReadOnly = false, Destructive = true)]
    [Description("Start a registered TwinCAT 3 Usermode Runtime instance on the local machine. " +
                 "The UMR is a registered instance under %ProgramData%\\Beckhoff\\TwinCAT\\3.1\\Runtimes; " +
                 "it is launched the documented way (its instance Start.bat semantics). " +
                 "Does NOT require stopping TcSysSrv — that is the unrelated router/management service. " +
                 "Initial state after a successful start is Config; an init failure is returned as an error " +
                 "(not a false Config). Fails if a UMR process is already tracked by this session.")]
    public string UmrStart(
        [Description("Registered instance name (see twincat_umr_list_instances). " +
                     "Omit to auto-resolve: the single instance, else 'UmRT_Default'.")] string? instanceName = null,
        [Description("Full path to TcSystemServiceUm.exe. Defaults to the standard installation path.")] string? exePath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_start", new { instanceName }, () =>
        {
            if (McpPathGuard.CheckOptionalRuntimeExe(exePath, "exePath") is { } pathError)
                return pathError;
            try { return host.UmrManager.Start(instanceName, exePath); }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
        }, cancellationToken);

    [McpServerTool(Name = "twincat_umr_set_mode", ReadOnly = false, Destructive = true)]
    [Description("Switch the User Mode Runtime started by twincat_umr_start between Config and Run mode. " +
                 "Config mode allows downloading a new configuration; Run mode executes the loaded PLC program. " +
                 "Switches over ADS on the instance's NetId (WriteControl on port 10000, as twincat_set_system_mode) " +
                 "and succeeds only once ReadState confirms the mode — a Run switch takes about 20 s on a UMR. " +
                 "After a Run switch the PLC runtime on port 851 is read as well: the system can be in Run while the PLC stays stopped, which the result flags with [Warn]. " +
                 "Refused with an [Error] when the instance's NetId is unknown.")]
    public string UmrSetMode(
        [Description("Target mode: 'config' to switch to Config mode, 'run' to switch to Run mode.")] string mode,
        [Description("Seconds to wait for ReadState to confirm the mode. Default 60, clamped to 5..300.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_set_mode", new { mode, timeoutSeconds }, () =>
        {
            if (!SystemModeSwitch.TryParseMode(mode, out _))
                return "[Error] mode must be 'config' or 'run'.";
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? 60, 5, 300));
            try { return host.UmrManager.SetMode(mode, timeout); }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
        }, cancellationToken);

    [McpServerTool(Name = "twincat_umr_stop", ReadOnly = false, Destructive = true)]
    [Description("Stop the running User Mode Runtime process. " +
                 "Sends the exit command to the process and waits up to 5 seconds for it to terminate. " +
                 "Safe to call even if the UMR is already stopped.")]
    public string UmrStop(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_stop", null, () =>
        {
            try { return host.UmrManager.Stop(); }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
        }, cancellationToken);
}
