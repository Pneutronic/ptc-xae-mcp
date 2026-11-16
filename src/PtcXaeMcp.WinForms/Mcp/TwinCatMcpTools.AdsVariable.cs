using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_ads_variable", ReadOnly = true)]
    [Description("Read the live value of a PLC symbol via ADS from a running PLC or UMR. " +
                 "Not the source declaration (use twincat_plc_read). " +
                 "Returns JSON { success, symbol, plcType, value, amsNetId, adsPort } or [Error]. " +
                 "Types: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT USINT UINT UDINT ULINT REAL LREAL " +
                 "STRING and any ENUM; for an ENUM, value is { name, value } (name null if no member matches).")]
    public string ReadAdsVariable(
        [Description("Fully-qualified PLC symbol path, e.g. 'MAIN.rSpeed'.")] string symbolPath,
        [Description("AMS NetId of the target, e.g. '192.168.1.1.1.1'. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851 (first PLC); e.g. 852 for a second PLC.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_ads_variable", new { symbolPath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .ReadAdsVariable(symbolPath, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_read_ads_variables_batch", ReadOnly = true)]
    [Description("Read the live values of many PLC symbols in one ADS call. " +
                 "Much cheaper than repeated twincat_read_ads_variable calls. " +
                 "Returns JSON { success, count, results: [{ symbol, plcType, value, ok, message }], " +
                 "amsNetId, adsPort }, or [Error] on a connection failure. An unreadable symbol does not " +
                 "fail the batch (ok=false). ENUM values as in twincat_read_ads_variable. Never writes.")]
    public string ReadAdsVariablesBatch(
        [Description("Symbol paths, at least one, e.g. ['MAIN.nStep', 'MAIN.fbCyl.LimitSwitchWorkingPosition'].")] string[] symbolPaths,
        [Description("AMS NetId of the target. Omit for the local runtime.")]
            string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_ads_variables_batch", new { count = symbolPaths?.Length ?? 0, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .ReadAdsVariablesBatch(symbolPaths, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_inspect_fb_instance", ReadOnly = true)]
    [Description("Dump an FB/struct instance's member layout and RPC methods via ADS. Read-only diagnostic. " +
                 "Shows whether a PROPERTY is reachable as a symbol (member with isProperty=true) or via an RPC " +
                 "getter (__get<Name>/__set<Name>, only if compiled with RpcEnable=\"plc\" per property); many are " +
                 "neither and cannot be read by this tool. Returns JSON { success, instancePath, typeName, " +
                 "members: [{ name, instancePath, isProperty, typeName, size, offset }], " +
                 "rpcMethods: [{ name, isVoid, parameterCount }], amsNetId, adsPort } or [Error].")]
    public string InspectFbInstance(
        [Description("Fully-qualified FB/struct instance path, e.g. 'MAIN.fbMachine'.")] string instancePath,
        [Description("AMS NetId of the target. Omit for the local runtime.")]
            string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_inspect_fb_instance", new { instancePath, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .InspectFbInstance(instancePath, amsNetId, adsPort)), cancellationToken);

    [McpServerTool(Name = "twincat_write_ads_variable", ReadOnly = false, Destructive = true)]
    [Description("Write a value to a live PLC symbol via ADS on a running PLC or UMR. " +
                 "Takes effect in the running process immediately. Refuses input-mapped symbols (AT %I*). " +
                 "Returns JSON { success, symbol, plcType, writtenValue, amsNetId, adsPort } or [Error].")]
    public string WriteAdsVariable(
        [Description("Fully-qualified PLC symbol path, e.g. 'MAIN.bEnable'.")] string symbolPath,
        [Description("Value as text, e.g. 'true', '42', '3.14', 'Hello'. BOOL accepts only true/false/1/0; " +
                     "an ENUM takes a member name ('E_STATE_RUNNING') or a number.")] string value,
        [Description("AMS NetId of the target. Omit for the local runtime.")] string? amsNetId = null,
        [Description("ADS port of the PLC runtime. Default 851.")] int? adsPort = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_write_ads_variable", new { symbolPath, value, amsNetId, adsPort }, () =>
            bridge.Dispatcher.Run(() =>
                new AdsVariableCoordinator(bridge.Dispatcher.Session)
                    .WriteAdsVariable(symbolPath, value, amsNetId, adsPort)), cancellationToken);
}
