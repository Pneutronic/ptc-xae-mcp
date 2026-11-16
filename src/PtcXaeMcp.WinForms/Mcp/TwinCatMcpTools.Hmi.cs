using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-server — TwinCAT HMI (TE2000) projects of the open solution via the HMI
// Automation Interface. The handle is the HMI project
// name from twincat_hmi_list_projects; it may be omitted when the solution holds
// exactly one HMI project.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiProjectParam =
        "HMI project name from twincat_hmi_list_projects. Optional if the solution has exactly one HMI project.";

    private static string HmiResult((string Summary, string Json) result) =>
        McpErrors.IsError(result.Summary) ? result.Summary : result.Summary + Environment.NewLine + result.Json;

    [McpServerTool(Name = "twincat_hmi_list_projects", ReadOnly = true)]
    [Description("List the TwinCAT HMI (TE2000) projects of the open solution. Per project: name, " +
                 "projectDirectory, isReady, isPublishRunning; also automationVersion and whether the XAE window " +
                 "'Activate Engineering Server Trial License' is open (a CAPTCHA only a person can answer; the HMI " +
                 "engineering server does not work until then).")]
    public string HmiListProjects(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_projects", new { }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListProjects())), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_get_project_info", ReadOnly = true)]
    [Description("Show config, server extensions and file tree of one TwinCAT HMI project. Config: ActiveTheme, " +
                 "Locale, ScaleMode, StartupView, LoginPage, websocket timeouts. Engineering-server extensions " +
                 "(e.g. ADS, TcHmiSrv): domainName, state, loaded, version. Tree (max 400 nodes) only with includeFiles.")]
    public string HmiGetProjectInfo(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Also return the project tree. Default false.")]
        bool includeFiles = false,
        [Description("Tree depth 1..6 when includeFiles. Default 2.")]
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_get_project_info", new { hmiProject, includeFiles, maxDepth }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).GetProjectInfo(hmiProject, includeFiles, maxDepth))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_mapped_symbols", ReadOnly = true)]
    [Description("List the symbols mapped in a TwinCAT HMI project's engineering server. Per symbol: mappedName " +
                 "(e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'), domain, type, dataTypeDisplayName, readOnly, hidden. CAUTION: " +
                 "cached view; maps/unmaps after the project was loaded are NOT shown, even with refresh=true. For the " +
                 "live state of one symbol use twincat_hmi_read_symbols (SYMBOL_NOT_MAPPED = not mapped). JSON: " +
                 "mappedCount (all mapped), total (after filters), offset, has_more, next_offset, symbols[]; while " +
                 "has_more, call again with offset=next_offset.")]
    public string HmiListMappedSymbols(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Only this server domain, e.g. 'ADS' (case-insensitive).")]
        string? domain = null,
        [Description("Only mapped names starting with this, e.g. 'ADS.PLC1.GVL_HMI.' (case-insensitive).")]
        string? namePrefix = null,
        [Description("Include hidden symbols. Default false.")]
        bool includeHidden = false,
        [Description("Ask the server to refresh its symbol list first. Default false.")]
        bool refresh = false,
        [Description("Page size: default 500, max 5000 (0 = 5000).")]
        int? limit = null,
        [Description("Symbols to skip (next_offset of the previous page). Default 0.")]
        int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_mapped_symbols", new { hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session)
                    .ListMappedSymbols(hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_read_symbols", ReadOnly = true)]
    [Description("Read symbol values through the HMI engineering server (what the HMI shows). Max 200 names. A " +
                 "failing symbol does not fail the batch: it gets ok=false and the server error (SYMBOL_NOT_MAPPED; " +
                 "HMI_ADS_E_SYNC_TIMEOUT = PLC runtime unreachable). Values come as the server reports them, not " +
                 "guaranteed JSON (a BOOL reads 'False'). Compare with twincat_read_ads_variable to prove a binding " +
                 "end-to-end. JSON: { hmiProject, count, ok, results[] }.")]
    public string HmiReadSymbols(
        [Description("HMI symbol names (at least one), e.g. ['ADS.PLC1.GVL_HMI.HmiOpened', 'UserSelectType'].")]
        string[] symbols,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_read_symbols", new { hmiProject, count = symbols?.Length ?? 0 }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ReadSymbols(hmiProject, symbols))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_server_control", ReadOnly = false, Destructive = true)]
    [Description("Start, shut down or refresh symbols of an HMI project's engineering server. That is the local " +
                 "TcHmiSrv the XAE runs for the project (live view), NOT the TF2000 runtime server on a target. The " +
                 "XAE starts it when the project opens; use 'start' after a shutdown/crash, 'refresh_symbols' after " +
                 "PLC symbol changes. 'start' waits until an extension is loaded, 'shutdown' until none is. Changes " +
                 "no project file. JSON includes extensionsAfter[].")]
    public string HmiServerControl(
        [Description("start | shutdown | refresh_symbols")] string action,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Max wait for the server to settle, 0..120000 ms. Default 30000.")]
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_server_control", new { action, hmiProject, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ControlServer(hmiProject, action, timeoutMs))), cancellationToken);

    private const string ConfirmNote =
        "A person must confirm every call in a popup of the MCP host (no bypass); rejected or timed out = " +
        "nothing written. Meanwhile other calls get 'An operation is already in progress'.";

    private const string ConfirmTimeoutParam =
        "Seconds to answer the popup, 10..600. Default 120.";

    [McpServerTool(Name = "twincat_hmi_map_symbol", ReadOnly = false, Destructive = false)]
    [Description("Map a PLC symbol into an HMI project's server so views can bind to it. " + ConfirmNote +
                 " plcPath 'MAIN.fbHmi.bStart' becomes mapName 'ADS.PLC1.MAIN.fbHmi.bStart' + internalName " +
                 "'PLC1::MAIN::fbHmi::bStart'; for arrays or other domains pass mapName + internalName. Refuses an " +
                 "already mapped name (never re-maps). Success only after read-back finds the mapping. An ADS map " +
                 "needs the ADS extension's PLC runtime reachable.")]
    public string HmiMapSymbol(
        [Description("Dotted PLC path, e.g. 'GVL_HMI.HmiOpened'. Alternative to mapName + internalName.")]
        string? plcPath = null,
        [Description("Explicit mapped name, e.g. 'ADS.PLC1.GVL_HMI.HmiOpened'. Needs internalName.")]
        string? mapName = null,
        [Description("Explicit internal name, e.g. 'PLC1::GVL_HMI::HmiOpened'. Needs mapName.")]
        string? internalName = null,
        [Description("Server domain. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime for names derived from plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Optional sub-symbol.")] string? subSymbolName = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_map_symbol",
            new { hmiProject, plcPath, mapName, internalName, domain, runtime, subSymbolName, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .MapSymbol(hmiProject, plcPath, mapName, internalName, domain, runtime, subSymbolName, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_unmap_symbol", ReadOnly = false, Destructive = true)]
    [Description("Remove a symbol mapping from an HMI project's server. " + ConfirmNote +
                 " Not mapped = 'Nothing to do', no popup. Success only after read-back no longer finds it.")]
    public string HmiUnmapSymbol(
        [Description("Mapped name, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'. Alternative to plcPath.")]
        string? mapName = null,
        [Description("Dotted PLC path, e.g. 'MAIN.fbHmi.bStart' (name derived as in twincat_hmi_map_symbol). Alternative to mapName.")]
        string? plcPath = null,
        [Description("Server domain. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime for names derived from plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Also delete the symbol's historized data. Default false.")] bool removeHistorizedData = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_unmap_symbol",
            new { hmiProject, mapName, plcPath, domain, runtime, removeHistorizedData, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .UnmapSymbol(hmiProject, plcPath, mapName, domain, runtime, removeHistorizedData, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_write_symbol", ReadOnly = false, Destructive = true)]
    [Description("Write a symbol value through a TwinCAT HMI project's server. For an ADS-mapped symbol " +
                 "('ADS.PLC1.…') this WRITES TO THE LIVE PLC, physically effective like twincat_write_ads_variable. " +
                 ConfirmNote + " The popup shows current and new value. Refuses unmapped (SYMBOL_NOT_MAPPED) and " +
                 "read-only symbols before asking. The value is read back; a difference is a warning (verified=false), " +
                 "as the PLC may change it at once. JSON: { written, before, after, verified, writesToPlc }.")]
    public string HmiWriteSymbol(
        [Description("HMI symbol name, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart' or 'UserSelectType'.")]
        string symbol,
        [Description("Value as valid JSON: true, 42, 3.5, \"text\" (quoted), or an object/array for structs.")]
        string valueJson,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_write_symbol", new { hmiProject, symbol, valueJson, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .WriteSymbol(hmiProject, symbol, valueJson, confirmTimeoutSeconds))), cancellationToken);
}
