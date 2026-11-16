using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>plc-instance-settings — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_init_symbols", ReadOnly = true)]
    [Description("Read init values of {attribute 'TcInitSymbol'} variables on the PLC instance. They live in the TwinCAT " +
                 "configuration, not in the PLC source. Needs a built PLC project and TwinCAT >= 4024.17.")]
    public string ReadInitSymbols(
        [Description("PLC project under TIPC; optional when there is exactly one.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_init_symbols", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcInstanceSettingsCoordinator(bridge.Dispatcher.Session).ReadInitSymbols(plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_write_init_symbol", ReadOnly = false, Destructive = true)]
    [Description("Set one {attribute 'TcInitSymbol'} init value on the PLC instance. The symbol must be listed by " +
                 "twincat_read_init_symbols (the XAE silently ignores others). Verified by read-back; a value the XAE does not store as " +
                 "given (invalid or out of range) is refused and the old value restored. Logs the XAE out first, saves afterwards. Does " +
                 "not activate: effective at the next Activate Configuration.")]
    public string WriteInitSymbol(
        [Description("Full symbol name as listed by twincat_read_init_symbols, e.g. 'GVL_Params.nSpeed'.")] string symbol,
        [Description("New init value as IEC literal, e.g. '42', '1.5', 'TRUE'.")] string value,
        [Description("PLC project under TIPC; optional when there is exactly one.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_write_init_symbol", new { symbol, value, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcInstanceSettingsCoordinator(bridge.Dispatcher.Session).WriteInitSymbol(symbol, value, plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_read_retain_config", ReadOnly = true)]
    [Description("Read the retain configuration (RetainSrc data areas) of the PLC instance. Lists each area's symbols (type, " +
                 "BitSize, BitOffs) and RetainDataMappings if present. Cannot be written; declare retain variables in the source " +
                 "(VAR RETAIN). Needs a built PLC project.")]
    public string ReadRetainConfig(
        [Description("PLC project under TIPC; optional when there is exactly one.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_retain_config", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcInstanceSettingsCoordinator(bridge.Dispatcher.Session).ReadRetainConfig(plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_get_type_file_generation", ReadOnly = true)]
    [Description("Read whether the PLC project generates its .tmc and .tpy type files at build.")]
    public string GetTypeFileGeneration(
        [Description("PLC project under TIPC; optional when there is exactly one.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_type_file_generation", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcInstanceSettingsCoordinator(bridge.Dispatcher.Session).GetTypeFileGeneration(plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_set_type_file_generation", ReadOnly = false, Destructive = true)]
    [Description("Switch .tmc/.tpy type-file generation of the PLC project on or off. Coupled: tpy=true also turns tmc on, " +
                 "tmc=false also turns tpy off (tmc=false with tpy=true is refused). Returns the flags read back. Logs the XAE out " +
                 "first, saves afterwards; effective at the next build.")]
    public string SetTypeFileGeneration(
        [Description("Generate the .tmc file; omit = unchanged.")] bool? tmc = null,
        [Description("Generate the .tpy file; omit = unchanged.")] bool? tpy = null,
        [Description("PLC project under TIPC; optional when there is exactly one.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_type_file_generation", new { tmc, tpy, plcProjectName }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcInstanceSettingsCoordinator(bridge.Dispatcher.Session).SetTypeFileGeneration(tmc, tpy, plcProjectName)), cancellationToken);
}
