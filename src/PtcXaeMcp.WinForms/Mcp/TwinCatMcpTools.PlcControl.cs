using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_start", ReadOnly = false, Destructive = true)]
    [Description("Start the PLC runtime (RUN) over ADS. " +
                 "Use after twincat_login mode=download, which leaves the PLC in STOP; unlike twincat_restart_runtime it keeps " +
                 "the downloaded program instead of reloading the old boot project. Reads the state back, so a command " +
                 "without effect is reported as a failure.")]
    public string PlcStart(
        [Description("Target AmsNetId. Default: the local NetId.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851 (first PLC).")] int adsPort = 851,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_start", new { amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcControlCoordinator(bridge.Dispatcher.Session).PlcStart(amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_stop", ReadOnly = false, Destructive = true)]
    [Description("Stop the PLC runtime over ADS; on a real machine this stops the process. " +
                 "Reads the state back and reports the actual runtime state.")]
    public string PlcStop(
        [Description("Target AmsNetId. Default: the local NetId.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851 (first PLC).")] int adsPort = 851,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_stop", new { amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcControlCoordinator(bridge.Dispatcher.Session).PlcStop(amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_activate_boot_project", ReadOnly = false, Destructive = true)]
    [Description("Write the current program as the PLC boot project ('Activate Boot Project'). " +
                 "Without it a download lives in RAM only and the next power cycle loads the previous boot project. Works " +
                 "online or offline. dryRun (default) changes nothing and is safe on a running plant. With dryRun=false the " +
                 "result lists the rewritten _Boot files and fails if no boot application (*.app) was produced. " +
                 "Needs an open solution.")]
    public string ActivateBootProject(
        [Description("true (default) only probes and changes nothing; false writes the boot project.")] bool dryRun = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_activate_boot_project", new { dryRun }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcControlCoordinator(bridge.Dispatcher.Session).ActivateBootProject(dryRun)), cancellationToken);
}
