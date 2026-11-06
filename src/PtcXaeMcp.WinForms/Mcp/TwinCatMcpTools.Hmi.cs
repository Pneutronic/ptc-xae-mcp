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
        "HMI project name as reported by twincat_hmi_list_projects. Optional when the " +
        "open solution contains exactly one HMI project; required (no fallback) when it contains several.";

    private static string HmiResult((string Summary, string Json) result) =>
        McpErrors.IsError(result.Summary) ? result.Summary : result.Summary + Environment.NewLine + result.Json;

    [McpServerTool(Name = "twincat_hmi_list_projects", ReadOnly = true)]
    [Description("List the TwinCAT HMI (TE2000) projects of the solution open in the XAE, via the HMI " +
                 "Automation Interface (dte.GetObject(\"Beckhoff.TcHmi.1.12\")). Per project: name, " +
                 "projectDirectory, isReady, isPublishRunning. Also reports automationVersion and, if open, " +
                 "the XAE window 'Activate Engineering Server Trial License' (a CAPTCHA only the engineer can " +
                 "answer — the HMI engineering server does not work until then). Read-only. " +
                 "Result: one-line summary + indented JSON.")]
    public string HmiListProjects(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_projects", new { }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListProjects())), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_get_project_info", ReadOnly = true)]
    [Description("Details of one TwinCAT HMI project: project config (ActiveTheme, Locale, ScaleMode, " +
                 "StartupView, LoginPage, websocket timeouts), the server extensions of its HMI engineering " +
                 "server (domainName, state, loaded, version — e.g. ADS, TcHmiSrv, TcHmiUserManagement) and, " +
                 "with includeFiles, the project tree (views, content, server config …; max 400 nodes). " +
                 "Read-only. Result: one-line summary + indented JSON.")]
    public string HmiGetProjectInfo(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Also return the project tree (ITcHmiItem children). Default false.")]
        bool includeFiles = false,
        [Description("Tree depth when includeFiles is true, 1..6. Default 2.")]
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_get_project_info", new { hmiProject, includeFiles, maxDepth }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).GetProjectInfo(hmiProject, includeFiles, maxDepth))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_list_mapped_symbols", ReadOnly = true)]
    [Description("List the symbols mapped in a TwinCAT HMI project's engineering server, as the HMI Automation " +
                 "Interface reports them (compare with the offline extraction of stage 33). Per symbol: " +
                 "mappedName (e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'), domain (e.g. ADS), type, " +
                 "dataTypeDisplayName, readOnly, hidden. Filterable by domain and name prefix. CAUTION (verified " +
                 "live): this list is a cached view — a map/unmap done after the project was loaded is NOT reflected, " +
                 "even with refresh=true. For the live state of one symbol use twincat_hmi_read_symbols " +
                 "(SYMBOL_NOT_MAPPED = not mapped). Read-only. " +
                 "Result: one-line summary + indented JSON { hmiProject, mappedCount, total, offset, returned, has_more, " +
                 "next_offset, symbols[] }: mappedCount = every mapped symbol, total = those that pass the filters, " +
                 "paged with limit/offset (call again with offset=next_offset while has_more is true).")]
    public string HmiListMappedSymbols(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Only symbols of this server domain, e.g. 'ADS'. Case-insensitive. Omit for all.")]
        string? domain = null,
        [Description("Only symbols whose mapped name starts with this, e.g. 'ADS.PLC1.GVL_HMI.'. Case-insensitive.")]
        string? namePrefix = null,
        [Description("Include symbols flagged hidden. Default false.")]
        bool includeHidden = false,
        [Description("Ask the server to refresh its symbol list before answering. Default false.")]
        bool refresh = false,
        [Description("Symbols per page: default 500, at most 5000 (0 = 5000).")]
        int? limit = null,
        [Description("Symbols to skip, for the next page (next_offset of the previous call). Default 0.")]
        int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_mapped_symbols", new { hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session)
                    .ListMappedSymbols(hmiProject, domain, namePrefix, includeHidden, refresh, limit, offset))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_read_symbols", ReadOnly = true)]
    [Description("Read symbol values THROUGH the HMI engineering server (what the HMI would display), e.g. " +
                 "'ADS.PLC1.MAIN.fbHmi.bStart' or a server symbol like 'UserSelectType'. Batch read; a symbol " +
                 "that cannot be read does NOT fail the batch — it appears with ok=false and the server's error " +
                 "(e.g. SYMBOL_NOT_MAPPED, HMI_ADS_E_SYNC_TIMEOUT when the PLC runtime is unreachable). The " +
                 "value is returned exactly as the server reports it — NOT guaranteed JSON (a BOOL reads as " +
                 "'False'). Compare with twincat_read_ads_variable to prove a binding end-to-end. Read-only, " +
                 "max 200 names. Result: one-line summary + indented JSON { hmiProject, count, ok, results[] }.")]
    public string HmiReadSymbols(
        [Description("HMI symbol names to read, e.g. ['ADS.PLC1.GVL_HMI.HmiOpened', 'UserSelectType']. " +
                     "At least one required.")] string[] symbols,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_read_symbols", new { hmiProject, count = symbols?.Length ?? 0 }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ReadSymbols(hmiProject, symbols))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_server_control", ReadOnly = false, Destructive = true)]
    [Description("Start, shut down or refresh the symbols of a TwinCAT HMI project's ENGINEERING server — the " +
                 "local TcHmiSrv the XAE runs for this project (live view, tests), NOT the TF2000 runtime server " +
                 "on a target. The XAE starts it automatically when the project opens; use 'start' after a " +
                 "shutdown/crash and 'refresh_symbols' after PLC symbol changes. 'start' waits until at least one " +
                 "server extension is loaded, 'shutdown' until none is (bounded by timeoutMs). Changes no project " +
                 "file. Result: one-line summary + indented JSON (extensionsAfter[]).")]
    public string HmiServerControl(
        [Description("start | shutdown | refresh_symbols")] string action,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("How long to wait for the server to settle, 0..120000 ms. Default 30000.")]
        int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_server_control", new { action, hmiProject, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ControlServer(hmiProject, action, timeoutMs))), cancellationToken);

    private const string ConfirmNote =
        "EVERY call must be confirmed by the engineer in a modal popup of the MCP host — no parameter bypasses " +
        "it; rejected or expired (confirmTimeoutSeconds) means nothing is written. While the popup is open, " +
        "other MCP calls get 'An operation is already in progress'.";

    private const string ConfirmTimeoutParam =
        "Seconds the engineer has to answer the popup, 10..600. Default 120.";

    [McpServerTool(Name = "twincat_hmi_map_symbol", ReadOnly = false, Destructive = false)]
    [Description("Map a PLC symbol into a TwinCAT HMI project's server so views can bind to it. " + ConfirmNote +
                 " Easiest: plcPath 'MAIN.fbHmi.bStart' becomes mapName 'ADS.PLC1.MAIN.fbHmi.bStart' and " +
                 "internalName 'PLC1::MAIN::fbHmi::bStart' (the convention of all existing mappings, verified on two " +
                 "real projects). Arrays / other domains: pass mapName + internalName explicitly. Refuses when the " +
                 "name is already mapped (the API does not reveal an existing mapping's target, so it is never " +
                 "re-mapped silently). Success only after a read-back finds the mapping. An ADS map needs the PLC " +
                 "runtime of the ADS extension to be reachable. Result: one-line summary + indented JSON.")]
    public string HmiMapSymbol(
        [Description("Dotted PLC path, e.g. 'MAIN.fbHmi.bStart' or 'GVL_HMI.HmiOpened'. Alternative to mapName + internalName.")]
        string? plcPath = null,
        [Description("Explicit mapped name, e.g. 'ADS.PLC1.GVL_HMI.HmiOpened'. Requires internalName.")]
        string? mapName = null,
        [Description("Explicit internal name inside the domain, e.g. 'PLC1::GVL_HMI::HmiOpened'. Requires mapName.")]
        string? internalName = null,
        [Description("Server domain. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime name used to derive names from plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Optional sub-symbol (ITcHmiServer3.MapSymbol overload).")] string? subSymbolName = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_map_symbol",
            new { hmiProject, plcPath, mapName, internalName, domain, runtime, subSymbolName, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .MapSymbol(hmiProject, plcPath, mapName, internalName, domain, runtime, subSymbolName, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_unmap_symbol", ReadOnly = false, Destructive = true)]
    [Description("Remove a symbol mapping from a TwinCAT HMI project's server. " + ConfirmNote +
                 " Pass mapName (e.g. 'ADS.PLC1.MAIN.fbHmi.bStart') or plcPath (derived like twincat_hmi_map_symbol). " +
                 "A name that is not mapped returns 'Nothing to do' without a popup. Success only after a read-back " +
                 "no longer finds the mapping. Result: one-line summary + indented JSON.")]
    public string HmiUnmapSymbol(
        [Description("Mapped name to remove, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'. Alternative to plcPath.")]
        string? mapName = null,
        [Description("Dotted PLC path, e.g. 'MAIN.fbHmi.bStart' (ADS names derived). Alternative to mapName.")]
        string? plcPath = null,
        [Description("Server domain of the mapping. Default 'ADS'.")] string? domain = null,
        [Description("ADS runtime name used to derive the name from plcPath. Default 'PLC1'.")] string? runtime = null,
        [Description("Also delete historized data of the symbol. Default false.")] bool removeHistorizedData = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_unmap_symbol",
            new { hmiProject, mapName, plcPath, domain, runtime, removeHistorizedData, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .UnmapSymbol(hmiProject, plcPath, mapName, domain, runtime, removeHistorizedData, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_write_symbol", ReadOnly = false, Destructive = true)]
    [Description("Write a value THROUGH the HMI server of a TwinCAT HMI project. For an ADS-mapped symbol " +
                 "('ADS.PLC1.…') this WRITES THROUGH TO THE LIVE PLC — physically effective like " +
                 "twincat_write_ads_variable. " + ConfirmNote + " The popup shows the current and the new value. " +
                 "Refuses unmapped (SYMBOL_NOT_MAPPED) and read-only symbols before asking. valueJson must be valid " +
                 "JSON (true, 42, 3.5, \"text\", {…}). After the write the value is read back; a differing read-back " +
                 "is reported as a warning (verified=false), because the PLC may change the value at once. " +
                 "Result: one-line summary + indented JSON { written, before, after, verified, writesToPlc }.")]
    public string HmiWriteSymbol(
        [Description("HMI symbol name, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart' or a server symbol like 'UserSelectType'.")]
        string symbol,
        [Description("The value as JSON: true, 42, 3.5, \"text\" (strings quoted), or an object/array for structs.")]
        string valueJson,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_write_symbol", new { hmiProject, symbol, valueJson, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .WriteSymbol(hmiProject, symbol, valueJson, confirmTimeoutSeconds))), cancellationToken);
}
