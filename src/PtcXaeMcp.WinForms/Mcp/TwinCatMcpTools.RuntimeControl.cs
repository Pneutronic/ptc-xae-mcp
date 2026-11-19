using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_broadcast_search", ReadOnly = true)]
    [Description("Search the local network for TwinCAT targets via ADS broadcast. " +
                 "Returns a JSON array of { name, netId, ipAddr, version, os }. " +
                 "Call it BEFORE twincat_set_target switches the project to a remote target; if the project " +
                 "already targets a remote system, set the target back to local first. Needs an open solution.")]
    public string BroadcastSearch(
        [Description("Hostname or IP for a directed search (TC3 4020.10+), e.g. 'CX-12345' or '192.168.1.5'. " +
                     "Omit to broadcast on all interfaces.")] string? searchTarget = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_broadcast_search", new { searchTarget }, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).BroadcastSearch(searchTarget)), cancellationToken);

    [McpServerTool(Name = "twincat_add_route", ReadOnly = false, Destructive = true)]
    [Description("Add an ADS route to a remote TwinCAT target. " +
                 "Get the target's netId and IP from twincat_broadcast_search first. Needs an open solution.")]
    public string AddRoute(
        [Description("AMS NetId of the remote target, e.g. '192.168.1.5.1.1'.")] string amsNetId,
        [Description("IP address of the target. ipAddr or hostName is required.")] string? ipAddr = null,
        [Description("Hostname of the target. ipAddr or hostName is required.")] string? hostName = null,
        [Description("User for a system route (not for project routes).")] string? userName = null,
        [Description("Password for a system route (not for project routes).")] string? password = null,
        [Description("True (default): project route stored in the .tsproj, no credentials, preferred. " +
                     "False: persistent system route, needs userName/password.")] bool projectRoute = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_route", new { amsNetId, ipAddr, hostName, projectRoute },
            () => bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session)
                    .AddRoute(amsNetId ?? "", ipAddr, hostName, userName, password, projectRoute)), cancellationToken);

    [McpServerTool(Name = "twincat_set_target", ReadOnly = false, Destructive = true)]
    [Description("Set the project's target AMS NetId and optionally the platform. " +
                 "Call it BEFORE twincat_activate_config to avoid the platform-mismatch dialog. Needs an open solution.")]
    public string SetTarget(
        [Description("AMS NetId of the target system, e.g. '192.168.1.5.1.1' or '127.0.0.1.1.1'.")] string amsNetId,
        [Description("'TwinCAT RT (x64)', 'TwinCAT RT (x86)' or 'TwinCAT OS (x64)' (User Mode Runtime). " +
                     "Omit to change only the NetId.")] string? platform = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_target", new { amsNetId, platform }, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).SetTarget(amsNetId ?? "", platform)), cancellationToken);

    [McpServerTool(Name = "twincat_set_system_mode", ReadOnly = false, Destructive = true)]
    [Description("Switch a target's TwinCAT system to Config or Run mode via ADS. " +
                 "Same as the XAE's 'Restart TwinCAT (Config Mode)' / 'Restart TwinCAT System'; waits until the mode is " +
                 "confirmed. A switch stops the PLC on that target. Use it when a running target ignores " +
                 "twincat_activate_config's restart (config, activate, run). Needs no XAE or solution. After a switch to " +
                 "run the PLC on port 851 is checked (up to 15 s); [Warn] if the system runs but the PLC stays stopped.")]
    public string SetSystemMode(
        [Description("AMS NetId of the target system, e.g. '192.168.250.20.1.1'. Required, no implicit local target: " +
                     "pass the local router's NetId to switch the local system.")] string amsNetId,
        [Description("'config' or 'run'.")] string mode,
        [Description("Seconds to wait for the mode to be confirmed. Default 60, clamped to 5..300.")] int? timeoutSeconds = null,
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
    [Description("Start or restart the TwinCAT runtime on the project's target. " +
                 "Use after twincat_activate_config: activate generates the boot project, this starts it. " +
                 "Needs an open solution.")]
    public string RestartRuntime(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_restart_runtime", null, () =>
            bridge.Dispatcher.Run(() =>
                new RuntimeControlCoordinator(bridge.Dispatcher.Session).RestartRuntime()), cancellationToken);

    [McpServerTool(Name = "twincat_umr_list_instances", ReadOnly = true)]
    [Description("List the registered TwinCAT 3 Usermode Runtime instances on this machine. " +
                 "Instances are folders under %ProgramData%\\Beckhoff\\TwinCAT\\3.1\\Runtimes; the names are valid " +
                 "instanceName values for twincat_umr_start. Returns a comma-separated list, or a note if none.")]
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
                 "It starts in Config mode; an init failure returns an error. Does NOT require stopping TcSysSrv. " +
                 "Refused if this session already tracks a UMR process, or if the instance's NetId already answers " +
                 "over ADS (started elsewhere).")]
    public string UmrStart(
        [Description("Registered instance name (see twincat_umr_list_instances). " +
                     "Omit for the single instance, else 'UmRT_Default'.")] string? instanceName = null,
        [Description("Full path to TcSystemServiceUm.exe. Default: the standard installation path.")] string? exePath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_start", new { instanceName }, () =>
        {
            if (McpPathGuard.CheckOptionalRuntimeExe(exePath, "exePath") is { } pathError)
                return pathError;
            try { return host.UmrManager.Start(instanceName, exePath); }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
        }, cancellationToken);

    [McpServerTool(Name = "twincat_umr_set_mode", ReadOnly = false, Destructive = true)]
    [Description("Switch the UMR started by twincat_umr_start between Config and Run mode. " +
                 "Config allows downloading a new configuration; Run executes the loaded PLC program. " +
                 "Succeeds only once the mode is confirmed (Run takes about 20 s). After a Run switch the PLC on " +
                 "port 851 is checked; [Warn] if the system runs but the PLC stays stopped. " +
                 "[Error] when the instance's NetId is unknown.")]
    public string UmrSetMode(
        [Description("'config' or 'run'.")] string mode,
        [Description("Seconds to wait for the mode to be confirmed. Default 60, clamped to 5..300.")] int? timeoutSeconds = null,
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
                 "Waits up to 5 s for it to exit. Safe if the UMR is already stopped.")]
    public string UmrStop(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_umr_stop", null, () =>
        {
            try { return host.UmrManager.Stop(); }
            catch (Exception ex) { return $"[Error] {ex.Message}"; }
        }, cancellationToken);
}
