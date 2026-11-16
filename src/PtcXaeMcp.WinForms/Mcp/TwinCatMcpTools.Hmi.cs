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
        "HMI project name; omit if the solution has only one.";

    private static string HmiResult((string Summary, string Json) result) =>
        McpErrors.IsError(result.Summary) ? result.Summary : result.Summary + Environment.NewLine + result.Json;

    [McpServerTool(Name = "twincat_hmi_list_projects", ReadOnly = true)]
    [Description("List the HMI (TE2000) projects of the open solution with their state. Also reports an open " +
                 "'Activate Engineering Server Trial License' window: a CAPTCHA only a person can answer; the HMI " +
                 "server does not work until then.")]
    public string HmiListProjects(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_projects", new { }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListProjects())), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_get_project_info", ReadOnly = true)]
    [Description("Show config, server extensions and file tree of an HMI project.")]
    public string HmiGetProjectInfo(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Also return the project tree (max 400 nodes).")]
        bool includeFiles = false,
        [Description("Tree depth 1..6.")]
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_get_project_info", new { hmiProject, includeFiles, maxDepth }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).GetProjectInfo(hmiProject, includeFiles, maxDepth))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_mapped_symbols", ReadOnly = true)]
    [Description("List the symbols mapped in an HMI project's server. CAUTION: cached; maps/unmaps after the " +
                 "project was loaded are not shown, even with refresh. For the live state of one symbol use " +
                 "twincat_hmi_read_symbols. While has_more, call again with offset=next_offset.")]
    public string HmiListMappedSymbols(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Only this domain, e.g. 'ADS'.")]
        string? domain = null,
        [Description("Only mapped names starting with this, e.g. 'ADS.PLC1.GVL_HMI.'.")]
        string? namePrefix = null,
        [Description("Include hidden symbols.")]
        bool includeHidden = false,
        [Description("Refresh the server's symbol list first.")]
        bool refresh = false,
        [Description("Page size, default 500, max 5000.")]
        int? limit = null,
        [Description("Symbols to skip (next_offset).")]
        int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_mapped_symbols", new { hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session)
                    .ListMappedSymbols(hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_read_symbols", ReadOnly = true)]
    [Description("Read symbol values through the HMI server (what the HMI shows), max 200. A failing symbol gets " +
                 "ok=false and the server error (SYMBOL_NOT_MAPPED; HMI_ADS_E_SYNC_TIMEOUT = PLC unreachable). " +
                 "Values as the server reports them (a BOOL reads 'False').")]
    public string HmiReadSymbols(
        [Description("HMI symbol names, e.g. ['ADS.PLC1.GVL_HMI.bOpen'].")]
        string[] symbols,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_read_symbols", new { hmiProject, count = symbols?.Length ?? 0 }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ReadSymbols(hmiProject, symbols))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_server_control", ReadOnly = false, Destructive = true)]
    [Description("Start, shut down or refresh symbols of an HMI engineering server. That is the XAE's local " +
                 "TcHmiSrv, not a TF2000 runtime. 'start' after a shutdown/crash, 'refresh_symbols' after PLC " +
                 "symbol changes. Changes no project file.")]
    public string HmiServerControl(
        [Description("start | shutdown | refresh_symbols")] string action,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Max wait for the server to settle, 0..120000 ms.")]
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_server_control", new { action, hmiProject, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ControlServer(hmiProject, action, timeoutMs))), cancellationToken);

    private const string ConfirmNote =
        "A person confirms in a popup (no bypass); rejected = nothing done.";

    private const string ConfirmTimeoutParam = "Seconds to answer the popup, 10..600.";

    [McpServerTool(Name = "twincat_hmi_map_symbol", ReadOnly = false, Destructive = false)]
    [Description("Map a PLC symbol into an HMI project's server so views can bind to it. " + ConfirmNote +
                 " plcPath 'MAIN.x' becomes 'ADS.PLC1.MAIN.x' (internal 'PLC1::MAIN::x'); for arrays or other " +
                 "domains pass mapName + internalName. Refuses an already mapped name. Needs the PLC reachable.")]
    public string HmiMapSymbol(
        [Description("Dotted PLC path, e.g. 'GVL_HMI.bOpen'. Or mapName + internalName.")]
        string? plcPath = null,
        [Description("Mapped name, e.g. 'ADS.PLC1.GVL_HMI.bOpen'.")]
        string? mapName = null,
        [Description("Internal name, e.g. 'PLC1::GVL_HMI::bOpen'.")]
        string? internalName = null,
        [Description("Server domain. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime for plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Sub-symbol.")] string? subSymbolName = null,
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
                 " Not mapped = 'Nothing to do'.")]
    public string HmiUnmapSymbol(
        [Description("Mapped name. Or plcPath.")]
        string? mapName = null,
        [Description("Dotted PLC path (named as in twincat_hmi_map_symbol). Or mapName.")]
        string? plcPath = null,
        [Description("Server domain. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime for plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Also delete its historized data.")] bool removeHistorizedData = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_unmap_symbol",
            new { hmiProject, mapName, plcPath, domain, runtime, removeHistorizedData, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .UnmapSymbol(hmiProject, plcPath, mapName, domain, runtime, removeHistorizedData, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_write_symbol", ReadOnly = false, Destructive = true)]
    [Description("Write a symbol value through an HMI project's server. An ADS symbol ('ADS.PLC1.…') WRITES TO THE " +
                 "LIVE PLC, like twincat_write_ads_variable. " + ConfirmNote + " Refuses unmapped and read-only " +
                 "symbols. Read back; a difference sets verified=false (the PLC may change it at once).")]
    public string HmiWriteSymbol(
        [Description("HMI symbol name, e.g. 'ADS.PLC1.MAIN.bStart'.")]
        string symbol,
        [Description("Value as JSON: true, 42, \"text\", or an object/array.")]
        string valueJson,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_write_symbol", new { hmiProject, symbol, valueJson, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .WriteSymbol(hmiProject, symbol, valueJson, confirmTimeoutSeconds))), cancellationToken);
}
