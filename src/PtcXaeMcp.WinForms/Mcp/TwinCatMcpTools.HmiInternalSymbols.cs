using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-internal-symbols (#165) — HMI-owned variables with no
// PLC behind them: client (framework) internal symbols %i%…%/i% in Properties\tchmiconfig.json,
// and server symbols of the TcHmiSrv domain in Server\TcHmiSrv\TcHmiSrv.Config.default.json.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiDataTypeParam =
        "'tchmi:general#/definitions/Boolean' (or INT16, String, …) or boolean, number, integer, string.";

    private const string HmiDefaultValueParam = "Default as a JSON scalar: true, 42, \"text\" or null; must fit dataType.";

    [McpServerTool(Name = "twincat_hmi_list_internal_symbols", ReadOnly = true)]
    [Description("List the HMI-owned symbols (no PLC behind them) of an HMI project. Read from its files, no " +
                 "server needed: client internal symbols %i%Name%/i% and TcHmiSrv server symbols.")]
    public string HmiListInternalSymbols(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_internal_symbols", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListInternalSymbols(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_add_internal_symbol", ReadOnly = false, Destructive = false)]
    [Description("Add a client internal symbol %i%name%/i% to an HMI project. For HMI-side state (selection, " +
                 "page index) without a PLC variable. " + ConfirmNote + " Refuses an existing name. For a symbol the " +
                 "server holds for all clients use twincat_hmi_map_offline_symbol.")]
    public string HmiAddInternalSymbol(
        [Description("Letters, digits, '_', max 128; not starting with a digit.")]
        string name,
        [Description(HmiDataTypeParam)] string dataType,
        [Description(HmiDefaultValueParam)] string? defaultValueJson = null,
        [Description("Persist the value in the client.")]
        bool persist = false,
        [Description("Read-only for bindings.")]
        bool readOnly = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_add_internal_symbol",
            new { hmiProject, name, dataType, defaultValueJson, persist, readOnly, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .AddInternalSymbol(hmiProject, name, dataType, defaultValueJson, persist, readOnly,
                        confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_remove_internal_symbol", ReadOnly = false, Destructive = true)]
    [Description("Remove a client internal symbol %i%name%/i% from an HMI project. Bindings to it stop resolving. " +
                 ConfirmNote + " Unknown name = 'Nothing to do'.")]
    public string HmiRemoveInternalSymbol(
        [Description("Name as twincat_hmi_list_internal_symbols reports it (case-sensitive).")]
        string name,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_remove_internal_symbol",
            new { hmiProject, name, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .RemoveInternalSymbol(hmiProject, name, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_map_offline_symbol", ReadOnly = false, Destructive = false)]
    [Description("Create a server symbol (TcHmiSrv domain) without a PLC behind it. The HMI server holds it for all " +
                 "clients; bind to it like any mapped symbol. Needs the HMI server running. " + ConfirmNote + " Refuses a " +
                 "known name. Remove with twincat_hmi_unmap_symbol (domain 'TcHmiSrv').")]
    public string HmiMapOfflineSymbol(
        [Description("Dotted identifiers, e.g. 'Recipe.SelectedIndex', max 256.")]
        string mapName,
        [Description(HmiDataTypeParam)] string dataType,
        [Description(HmiDefaultValueParam)] string? defaultValueJson = null,
        [Description("Sets USEMAPPING; false = pure server variable (true is unverified, prefer false).")]
        bool online = false,
        [Description("Server keeps the last value across restarts.")]
        bool persist = false,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_map_offline_symbol",
            new { hmiProject, mapName, dataType, defaultValueJson, online, persist, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .MapOfflineSymbol(hmiProject, mapName, dataType, defaultValueJson, online, persist,
                        confirmTimeoutSeconds))), cancellationToken);
}
