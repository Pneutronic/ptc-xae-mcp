using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_fieldbus_topology", ReadOnly = true)]
    [Description("Return the I/O (TIID) tree as JSON: devices, boxes, slaves, channels. " +
                 "Read-only; needs an open solution. Result: summary line + JSON (deviceCount, devices[]).")]
    public string GetFieldbusTopology(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_fieldbus_topology", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, json) = new FieldbusCoordinator(bridge.Dispatcher.Session).GetTopology();
                if (McpErrors.IsError(summary) ||
                    summary.StartsWith("No fieldbus", StringComparison.OrdinalIgnoreCase))
                    return summary;
                return summary + Environment.NewLine + json;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_get_device_xml", ReadOnly = true)]
    [Description("Return the raw device-configuration XML of one I/O (TIID) tree node. " +
                 "Read-only; needs an open solution. This is the XML twincat_configure_ethercat_slave accepts.")]
    public string GetDeviceXml(
        [Description("Tree path below TIID, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'; the root 'TIID' itself is " +
                     "rejected. From twincat_get_fieldbus_topology.")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_device_xml", new { treePath }, () =>
            bridge.Dispatcher.Run(() => new FieldbusCoordinator(bridge.Dispatcher.Session).GetDeviceXml(treePath)), cancellationToken);

    [McpServerTool(Name = "twincat_configure_ethercat_slave", ReadOnly = false, Destructive = true)]
    [Description("Write device-configuration XML to an EtherCAT slave and verify by read-back. " +
                 "Every written leaf value is checked; values the XAE ignored or changed are returned as [Error]. " +
                 "Side effect: saves ALL dirty documents of the solution (File > Save All), not only this node. " +
                 "Needs an open solution. Get the current XML with twincat_get_device_xml, modify it, pass it here.")]
    public string ConfigureEtherCatSlave(
        [Description("Tree path of the slave below TIID (not the root), e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'.")]
        string treePath,
        [Description("Full device-configuration XML, as returned by twincat_get_device_xml, with your changes.")]
        string xml,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_configure_ethercat_slave",
            new { treePath, xml }, () =>
            bridge.Dispatcher.Run(() =>
                new FieldbusCoordinator(bridge.Dispatcher.Session).ConfigureEtherCatSlave(treePath, xml)), cancellationToken);
}
