using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_ads_variable", ReadOnly = true)]
    [Description("Read the current RUNTIME value of a PLC symbol via ADS from a running TwinCAT " +
                 "PLC or User Mode Runtime (UMR). This reads LIVE values — not static source " +
                 "declarations (use twincat_plc_read for declarations). " +
                 "Returns JSON { success, symbol, plcType, value, amsNetId, adsPort } or " +
                 "[Error] string. Supported types: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT " +
                 "USINT UINT UDINT ULINT REAL LREAL STRING, plus any ENUM (e.g. E_STATE) -- for " +
                 "an ENUM, value is { name, value }: name is the matching member's declared name " +
                 "(null if the raw value doesn't match any member), value is the raw underlying number. " +
                 "Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only " +
                 "when the router does not answer), default port 851.")]
    public string ReadAdsVariable(
        [Description("Fully-qualified PLC symbol path, e.g. 'GVL_Safety.bSI_DriveReleased' " +
                     "or 'MAIN.rSpeed'.")] string symbolPath,
        [Description("Optional AMS Net ID of the target ADS server (e.g. '192.168.1.1.1.1'). " +
                     "Omit for the local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851 (first PLC task). " +
                     "Use 852 for second PLC task, or the port shown in SYSTEM/Tasks.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_ads_variable", new { symbolPath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .ReadAdsVariable(symbolPath, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_read_ads_variables_batch", ReadOnly = true)]
    [Description("Read the current RUNTIME values of MANY PLC symbols in one call over a single " +
                 "ADS connection (far cheaper than N separate twincat_read_ads_variable calls). " +
                 "Use this to snapshot a whole IO checklist plus a status struct at once. " +
                 "Returns JSON { success, count, results: [{ symbol, plcType, value, ok, message }], " +
                 "amsNetId, adsPort } or an [Error] string on a connection-level failure. " +
                 "For an ENUM symbol, value is { name, value } (see twincat_read_ads_variable). " +
                 "A single unreadable symbol does NOT fail the batch — it appears as an item with " +
                 "ok=false. This tool is READ-ONLY; it never writes or forces any value. " +
                 "Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only " +
                 "when the router does not answer), default port 851.")]
    public string ReadAdsVariablesBatch(
        [Description("PLC symbol paths to read, e.g. ['GVL_IBN.stStatus.nStep', 'GVL_IO.bCyl1Extended']. " +
                     "At least one required.")] string[] symbolPaths,
        [Description("Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime.")]
            string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851 (first PLC task).")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_ads_variables_batch", new { count = symbolPaths?.Length ?? 0, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .ReadAdsVariablesBatch(symbolPaths, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_inspect_fb_instance", ReadOnly = true)]
    [Description("DIAGNOSTIC, READ-ONLY: dump an FB/struct instance's member layout via ADS -- " +
                 "every entry in its MemberInstances (name, instancePath, isProperty, typeName, size, " +
                 "offset if the runtime exposes it) plus any RPC-callable methods (e.g. __getX/__setX for " +
                 "PROPERTYs -- only present when the compiler marked the getter/setter RpcEnable=\"plc\", a " +
                 "PER-PROPERTY compiled flag, not a project-wide switch). Use this to find " +
                 "out empirically whether a given PROPERTY on a live FB instance is reachable as an " +
                 "addressable symbol (appears in members with isProperty=true) or only via an RPC getter " +
                 "(appears in rpcMethods as __get<Name>/__set<Name>) -- many properties are reachable via " +
                 "NEITHER (not RPC-enabled, no backing shadow variable) and only readable through the XAE " +
                 "Automation-Interface/online-value channel, a different transport this tool cannot reach -- " +
                 "do not assume any shape without checking. Returns JSON { success, instancePath, typeName, members:" +
                 "[{ name, instancePath, isProperty, typeName, size, offset }], " +
                 "rpcMethods: [{ name, isVoid, parameterCount }], amsNetId, adsPort } or [Error] string. " +
                 "Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only " +
                 "when the router does not answer), default port 851.")]
    public string InspectFbInstance(
        [Description("Fully-qualified FB/struct instance path, e.g. 'MAIN.fbMachine'.")] string instancePath,
        [Description("Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime.")]
            string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_inspect_fb_instance", new { instancePath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .InspectFbInstance(instancePath, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_write_ads_variable", ReadOnly = false, Destructive = true)]
    [Description("Write a value to a live PLC symbol via ADS on a running TwinCAT PLC or UMR. " +
                 "Refuses writes to input-mapped symbols (AT %I*). " +
                 "Returns JSON { success, symbol, plcType, writtenValue, amsNetId, adsPort } " +
                 "or [Error] string. " +
                 "Value must be the string representation of the target type, e.g. 'true', '42', '3.14'. " +
                 "BOOL accepts only true/false/1/0 (anything else is an [Error], never a silent FALSE). " +
                 "For an ENUM symbol, value may be either a declared member name (e.g. 'E_STATE_RUNNING') " +
                 "or a plain numeric string (e.g. '2'). " +
                 "Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only " +
                 "when the router does not answer), default port 851.")]
    public string WriteAdsVariable(
        [Description("Fully-qualified PLC symbol path, e.g. 'MAIN.bEnable'.")] string symbolPath,
        [Description("String representation of the value to write, e.g. 'true', '42', '3.14', 'Hello', " +
                     "or an ENUM member name like 'E_STATE_RUNNING'.")] string value,
        [Description("Optional AMS Net ID. Omit for local TwinCAT runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_write_ads_variable", new { symbolPath, value, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .WriteAdsVariable(symbolPath, value, amsNetId, adsPort)), cancellationToken);
}
