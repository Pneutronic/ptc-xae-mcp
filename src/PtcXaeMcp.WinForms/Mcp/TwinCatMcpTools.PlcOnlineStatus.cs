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
        "Check whether the XAE is Online (logged in to a running PLC runtime). " +
        "The PLC write tools (twincat_plc_write etc.) log out by themselves before writing. Returns JSON { isOnline, message }: " +
        "true = Online, false = Offline, null = unknown on this TwinCAT version.")]
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
