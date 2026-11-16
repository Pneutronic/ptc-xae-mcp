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
        "Data type: a TwinCAT HMI type reference like 'tchmi:general#/definitions/Boolean' (also INT16, " +
        "String, ...) or a JSON-schema type: boolean, number, integer, string.";

    private const string HmiDefaultValueParam =
        "Optional default as a JSON scalar: true, 42, 3.5, \"text\" or null (no objects/arrays). Must fit " +
        "dataType when that is a known scalar type.";

    [McpServerTool(Name = "twincat_hmi_list_internal_symbols", ReadOnly = true)]
    [Description("List the HMI-owned symbols (no PLC behind them) of a TwinCAT HMI project. Read from the project " +
                 @"files, no HMI server needed. client = framework internal symbols %i%Name%/i% from Properties\tchmiconfig.json " +
                 "(name, type, valueJson, persist, readOnly); server = non-hidden TcHmiSrv-domain symbols from " +
                 @"Server\TcHmiSrv\TcHmiSrv.Config.default.json (name, mapping, useMapping (false = pure offline " +
                 "server variable), dataType, defaultJson, persist). An unreadable server file is reported as " +
                 "unknown, never as none. JSON: { hmiProject, client{file,count,symbols[]}, " +
                 "server{file,readable,count,offline,symbols[]} }.")]
    public string HmiListInternalSymbols(
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_list_internal_symbols", new { hmiProject }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ListInternalSymbols(hmiProject))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_add_internal_symbol", ReadOnly = false, Destructive = false)]
    [Description("Add a client internal symbol %i%name%/i% to a TwinCAT HMI project. Stored in " +
                 @"Properties\tchmiconfig.json; for HMI-side state (selection, page index, dialog flag) without a PLC " +
                 "variable. " + ConfirmNote +
                 " Refuses an existing name. Success only after read-back finds the entry; differing stored " +
                 "type/value/flags are a WARNING. For a symbol the HMI server holds use twincat_hmi_map_offline_symbol. " +
                 "Undo with twincat_hmi_remove_internal_symbol.")]
    public string HmiAddInternalSymbol(
        [Description("Letters, digits, '_'; starts with a letter or '_'; max 128. Referenced as %i%name%/i%.")]
        string name,
        [Description(HmiDataTypeParam)] string dataType,
        [Description(HmiDefaultValueParam)] string? defaultValueJson = null,
        [Description("Persist the value in the client (not the server, not historizing). Default false.")]
        bool persist = false,
        [Description("Read-only for bindings. Default false.")]
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
    [Description("Remove a client internal symbol %i%name%/i% from a TwinCAT HMI project. " + ConfirmNote +
                 " Unknown name (case-sensitive) = 'Nothing to do', no popup. Success only after read-back no longer " +
                 "finds it. Bindings still referencing it stop resolving.")]
    public string HmiRemoveInternalSymbol(
        [Description("Name as twincat_hmi_list_internal_symbols reports it.")]
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
    [Description("Create a server symbol in the TcHmiSrv domain of an HMI engineering server. A variable the HMI " +
                 "SERVER holds for all clients, no PLC behind it, persisted in " +
                 @"Server\TcHmiSrv\TcHmiSrv.Config.default.json; bind to it like any mapped symbol. Needs the HMI " +
                 "engineering server running. " + ConfirmNote +
                 " Refuses a name the server config or running server already knows. Success only after its config " +
                 "reads back with DOMAIN TcHmiSrv and the requested USEMAPPING. Remove with twincat_hmi_unmap_symbol " +
                 "(domain 'TcHmiSrv').")]
    public string HmiMapOfflineSymbol(
        [Description("Dotted identifiers (letters, digits, '_'), e.g. 'Recipe.SelectedIndex', max 256.")]
        string mapName,
        [Description(HmiDataTypeParam)] string dataType,
        [Description(HmiDefaultValueParam)] string? defaultValueJson = null,
        [Description("Sets USEMAPPING. Default false = pure offline server variable starting at the default value; " +
                     "true is unverified live, prefer false.")]
        bool online = false,
        [Description("Server keeps the last value across restarts. Default false.")]
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
