using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-ads-tmc — attach a PLC project's TMC file to a TwinCAT HMI project so its symbols are
// known with NO PLC connected. The handle is the HMI project
// name, as in hmi-server; the second key is the virtual PLC name the file is attached under.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiVirtualPlcParam = "Virtual PLC name, as twincat_hmi_list_tmc_files reports it.";

    private const string HmiTmcFileParam =
        @"Absolute path of a PLC project's .tmc from twincat_hmi_list_plc_tmc_candidates (not HMI_Error/Info/Warning.tmc). " +
        @"Allowed under the solution, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or PTC_XAE_MCP_ALLOWED_PATHS.";

    [McpServerTool(Name = "twincat_hmi_list_tmc_files", ReadOnly = true)]
    [Description("List the TMC files attached to an HMI project (offline PLC symbols). Also lists " +
                 "the ADS runtimes that serve symbols from a TMC file (see twincat_hmi_enable_runtime_tmc).")]
    public string HmiListTmcFiles(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_tmc_files", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListTmcFiles(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_ads_runtimes", ReadOnly = true)]
    [Description("List the ADS runtimes (NetId, port, symbols, useTmc) of an HMI project's server. " +
                 "Tells 'PLC unreachable' apart from 'needs no PLC'. Switch one to TMC with twincat_hmi_enable_runtime_tmc.")]
    public string HmiListAdsRuntimes(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_ads_runtimes", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListAdsRuntimes(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_plc_tmc_candidates", ReadOnly = true)]
    [Description("List the TMC file of every PLC project in the open solution. These are the candidates for " +
                 "twincat_hmi_attach_tmc_file. Needs no HMI project. Does not choose the PLC for the HMI: pass one " +
                 "absolutePath as tmcFile.")]
    public string HmiListPlcTmcCandidates(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_plc_tmc_candidates", new { }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListPlcTmcCandidates())), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_project_datatypes", ReadOnly = true)]
    [Description("List the project datatypes of an HMI project (definitions in Properties/tchmi.project.Schema.json): " +
                 "name, kind (userControl/struct/enum/array/alias/other), namespace and where it comes from " +
                 "(attachedTmc/adsRuntime), $ref targets and dangling $refs (a reference to a project type that no " +
                 "longer exists). Reports how tchmi.project.Schema.tpl.json differs. A user-control parameter " +
                 "references a type as tchmi:project#/definitions/<name>.")]
    public string HmiListProjectDatatypes(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_project_datatypes", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListProjectDatatypes(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_attach_tmc_file", ReadOnly = false, Destructive = false)]
    [Description("Attach a PLC project's TMC file to an HMI project for offline symbols. Like TE2000 'Attach TMC " +
                 "file', its symbols become bindable in the engineering without a PLC. " + ConfirmNote + " Refuses a file or virtualPlcName " +
                 "already attached. Undo with twincat_hmi_detach_tmc_file.")]
    public string HmiAttachTmcFile(
        [Description(HmiTmcFileParam)]
        string tmcFile,
        [Description("Letters, digits, '_' or '-', max 64. Default: the .tmc file name.")]
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
    [Description("Detach a TMC file from an HMI project. Its symbols are then no longer known offline. " +
                 ConfirmNote + " Not attached = 'Nothing to do'.")]
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
        "ADS runtime name, e.g. 'PLC1' (case-sensitive; see twincat_hmi_list_ads_runtimes).";

    [McpServerTool(Name = "twincat_hmi_enable_runtime_tmc", ReadOnly = false, Destructive = true)]
    [Description("Serve an HMI ADS runtime's symbols from a TMC file instead of a PLC (USE_TMC). Mapping, " +
                 "reading and binding then work without any PLC (twincat_hmi_attach_tmc_file only feeds the " +
                 "engineering). Converts the .tmc into a .bin in the engineering server's folder and sets USE_TMC=true. " +
                 ConfirmNote + " Whether symbols are served is not checked: read one with twincat_hmi_read_symbols. " +
                 "Undo with twincat_hmi_disable_runtime_tmc. Values need the PLC (HMI_ADS_E_USING_TMC until then).")]
    public string HmiEnableRuntimeTmc(
        [Description(HmiAdsRuntimeParam)] string runtime,
        [Description(HmiTmcFileParam)]
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
    [Description("Switch an HMI ADS runtime from a TMC file back to the PLC (USE_TMC=false). Without a reachable " +
                 "PLC it then has no symbols. " + ConfirmNote + " No TMC in use = 'Nothing to " +
                 "do'. The .bin stays on disk. Mappings made in TMC mode keep their names.")]
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
