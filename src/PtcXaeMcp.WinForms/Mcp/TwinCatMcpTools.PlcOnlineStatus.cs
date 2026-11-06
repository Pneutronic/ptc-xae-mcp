using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    private static readonly JsonSerializerOptions PlcOnlineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool(Name = "twincat_get_plc_online_state", ReadOnly = true)]
    [Description(
        "Check whether the TwinCAT XAE IDE is currently Online (logged in to a running " +
        "PLC runtime). When Online, code edits (twincat_plc_write etc.) are blocked " +
        "by the IDE. Use this as a guard before attempting code edits. " +
        "Returns JSON { isOnline: bool|null, message: string }. " +
        "isOnline=true → XAE is Online, edits blocked. " +
        "isOnline=false → XAE is Offline, edits allowed. " +
        "isOnline=null → state indeterminate (COM API not available on this TwinCAT version).")]
    public string GetPlcOnlineState(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_plc_online_state", new { }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var result = bridge.Dispatcher.Session.GetPlcOnlineState();
                return JsonSerializer.Serialize(
                    new { isOnline = result.IsOnline, message = result.Message },
                    PlcOnlineJsonOptions);
            }), cancellationToken);
}
