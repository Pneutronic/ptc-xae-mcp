using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-ads-tmc — attach a PLC project's TMC file to a TwinCAT HMI project so its symbols are
// known with NO PLC connected. The handle is the HMI project
// name, as in hmi-server; the second key is the virtual PLC name the file is attached under.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiVirtualPlcParam =
        "Virtual PLC name the TMC file is attached under (root of its symbol paths), as " +
        "twincat_hmi_list_tmc_files reports it.";

    [McpServerTool(Name = "twincat_hmi_list_tmc_files", ReadOnly = true)]
    [Description("List the TMC files attached to a TwinCAT HMI project (offline PLC symbols). They give the HMI " +
                 "engineering PLC symbols WITHOUT a PLC connection; reflects the saved project. Per entry: " +
                 "virtualPlcName, stored path (solution-relative when below it), plcName, absolute path, exists. " +
                 "Also lists, separately, the ADS runtimes that serve symbols from a TMC file server-side (USE_TMC, " +
                 "see twincat_hmi_enable_runtime_tmc). JSON: { hmiProject, count, attachments[], adsRuntimesUsingTmc[] }.")]
    public string HmiListTmcFiles(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_tmc_files", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListTmcFiles(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_ads_runtimes", ReadOnly = true)]
    [Description("List the ADS runtimes of a TwinCAT HMI project's engineering server. Read live from " +
                 "'ADS.Config::RUNTIMES'. Per runtime: name (e.g. PLC1), enabled, netId, port, readOnly, " +
                 "useWhitelisting, symbolCount, useTmc, tmcFile (symbols from a converted TMC file, not an ADS upload). " +
                 "Tells 'PLC unreachable' apart from 'needs no PLC' and shows the NetId/port a failing binding points " +
                 "at. Switch a runtime to TMC with twincat_hmi_enable_runtime_tmc. " +
                 "JSON: { hmiProject, count, usingTmc, runtimes[] }.")]
    public string HmiListAdsRuntimes(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_ads_runtimes", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListAdsRuntimes(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_plc_tmc_candidates", ReadOnly = true)]
    [Description("List the TMC file of every PLC project in the open solution. These are the candidates for " +
                 "twincat_hmi_attach_tmc_file, found like TE2000's 'Browse solution for TMC file' dialog. Per " +
                 "candidate: tcProject, plcProject, relativeTmcPath, absolutePath, exists, lastWriteUtc, symbolCount " +
                 "(null when not opened: missing or outside the allowed roots) and a note why a field is empty. Needs " +
                 "no HMI project. Does NOT choose which PLC belongs to the HMI: pass one absolutePath as tmcFile. " +
                 "JSON: { count, existing, candidates[] }.")]
    public string HmiListPlcTmcCandidates(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_plc_tmc_candidates", new { }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListPlcTmcCandidates())), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_attach_tmc_file", ReadOnly = false, Destructive = false)]
    [Description("Attach a PLC project's TMC file to an HMI project for offline symbols. The TE2000 'Attach TMC " +
                 "file' function: the PLC symbols become browsable and bindable in the engineering WITHOUT ANY PLC " +
                 "CONNECTION. The file must be a <TcModuleClass> with at least one <Symbol>; its symbol count and size " +
                 "are shown in the popup. " + ConfirmNote +
                 " Refuses a file already attached or a virtualPlcName already in use. Success only after read-back " +
                 "finds the new entry. Undo with twincat_hmi_detach_tmc_file.")]
    public string HmiAttachTmcFile(
        [Description(@"Absolute path of the PLC project's '<PlcProject>\<PlcProject>.tmc' (twincat_hmi_list_plc_tmc_candidates lists them), NOT the HMI_Error/HMI_Info/HMI_Warning.tmc event classes. Must lie under the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in the host's PTC_XAE_MCP_ALLOWED_PATHS (';'-separated); other paths get [Error].")]
        string tmcFile,
        [Description("Virtual PLC name: letters, digits, '_' or '-', max 64. Default: the .tmc file name without extension.")]
        string? virtualPlcName = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_attach_tmc_file",
            new { hmiProject, tmcFile, virtualPlcName, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .AttachTmcFile(hmiProject, tmcFile, virtualPlcName, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_detach_tmc_file", ReadOnly = false, Destructive = true)]
    [Description("Detach a TMC file from a TwinCAT HMI project by its virtual PLC name. Its symbols are then no " +
                 "longer known offline. " + ConfirmNote +
                 " Not attached = 'Nothing to do', no popup. Success only after read-back no longer lists it.")]
    public string HmiDetachTmcFile(
        [Description(HmiVirtualPlcParam)] string virtualPlcName,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_detach_tmc_file",
            new { hmiProject, virtualPlcName, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .DetachTmcFile(hmiProject, virtualPlcName, confirmTimeoutSeconds))), cancellationToken);

    // #161 — the SERVER-side mechanism: the ADS extension serves a runtime's symbols from the TMC.

    private const string HmiAdsRuntimeParam =
        "ADS runtime name of the HMI server, e.g. 'PLC1' (case-sensitive, must exist; see " +
        "twincat_hmi_list_ads_runtimes).";

    [McpServerTool(Name = "twincat_hmi_enable_runtime_tmc", ReadOnly = false, Destructive = true)]
    [Description("Serve an HMI ADS runtime's PLC symbols from a TMC file instead of a PLC. Server-side USE_TMC: " +
                 "mapping, reading and binding PLC symbols then work WITHOUT ANY PLC (unlike " +
                 "twincat_hmi_attach_tmc_file, which only feeds the engineering). Converts the .tmc into " +
                 @"'<runtime>_<tmc>.DATA_UPLOADINFO2.bin' under 'Tmc\' in the engineering server's working directory " +
                 @"(<solution>\.engineering_servers\<project>), then sets the runtime's TMC::DATA_SYMBOLUPANDDOWNLOAD " +
                 "to it and USE_TMC=true in ADS.Config::RUNTIMES. The file is checked and converted before the popup. " +
                 ConfirmNote +
                 " Success only after read-back shows USE_TMC=true with the path; whether symbols are served is not " +
                 "checked (read one with twincat_hmi_read_symbols). Re-running regenerates the file. Undo with " +
                 "twincat_hmi_disable_runtime_tmc. Not yet verified against a live HMI server.")]
    public string HmiEnableRuntimeTmc(
        [Description(HmiAdsRuntimeParam)] string runtime,
        [Description(@"Absolute path of the PLC project's '<PlcProject>\<PlcProject>.tmc' (twincat_hmi_list_plc_tmc_candidates lists them), NOT the HMI_Error/Info/Warning.tmc event classes. Must lie under the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in the host's PTC_XAE_MCP_ALLOWED_PATHS (';'-separated); other paths are refused.")]
        string tmcFile,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_enable_runtime_tmc",
            new { hmiProject, runtime, tmcFile, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .EnableRuntimeTmc(hmiProject, runtime, tmcFile, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_disable_runtime_tmc", ReadOnly = false, Destructive = true)]
    [Description("Switch an HMI ADS runtime back from a TMC file to the ADS upload from the PLC. Clears " +
                 "DATA_SYMBOLUPANDDOWNLOAD and sets USE_TMC=false, as TE2000's own detach; without a reachable PLC " +
                 "the runtime then has no symbols. No TMC in use = 'Nothing to do', no popup. " + ConfirmNote +
                 " Success only after read-back shows USE_TMC=false without a file. The converted .bin stays on " +
                 "disk. Not yet verified against a live HMI server.")]
    public string HmiDisableRuntimeTmc(
        [Description(HmiAdsRuntimeParam)] string runtime,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_disable_runtime_tmc",
            new { hmiProject, runtime, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .DisableRuntimeTmc(hmiProject, runtime, confirmTimeoutSeconds))), cancellationToken);
}
