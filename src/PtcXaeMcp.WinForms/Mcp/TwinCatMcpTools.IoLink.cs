using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_iolink_config", ReadOnly = true)]
    [Description("Read the IO-Link configuration of a master terminal (e.g. EL6224). " +
                 "Read-only; needs an open solution. Per port (1..4): the assigned device (name, vendor, product id, " +
                 "IODD file) and its named process data (e.g. 'Valve 1 solenoid 00'). Result: summary line + JSON. " +
                 "The fieldbus topology does not descend into IO-Link ports, so pass the master's own tree path.")]
    public string GetIoLinkConfig(
        [Description("Tree path of the IO-Link master, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1200)^Term 28 (EL6224)'. " +
                     "From twincat_get_fieldbus_topology.")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_iolink_config", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, json) = new IoLinkCoordinator(bridge.Dispatcher.Session).GetConfig(treePath);
                if (McpErrors.IsError(summary) ||
                    summary.StartsWith("No IO-Link", StringComparison.OrdinalIgnoreCase))
                    return summary;
                return summary + Environment.NewLine + json;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_import_iodd", ReadOnly = false, Destructive = true)]
    [Description("Register an IO-Link device description (IODD .xml) in TwinCAT's IODD folder. " +
                 "An IO-Link master (e.g. EL6224) then breaks the device down into named process data. Writes the TwinCAT " +
                 "IODD library, not the project; no open solution needed. Reports the device identity (vendorId/deviceId/" +
                 "productId). Idempotent: an identical registered file is skipped. TwinCAT picks the file up on its next " +
                 "IODD rescan.")]
    public string ImportIodd(
        [Description("Full path of the IODD .xml file, e.g. 'C:\\\\temp\\\\Vendor-Device-20230815-IODD1.1.xml'.")]
        string sourcePath,
        [Description("Override of the IODD folder. Empty = TwinCAT default (…\\Config\\Io\\IOLink).")]
        string? targetFolder = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_import_iodd", new { sourcePath, targetFolder }, () =>
            // No open-solution requirement for this tool (it writes the TwinCAT IODD
            // library, not the project), cancellationToken, so there is no solution directory to allow —
            // sourcePath is checked against the fixed roots only (%TEMP%, %LOCALAPPDATA%
            // \ptc-xae-mcp, the TwinCAT install dir, PTC_XAE_MCP_ALLOWED_PATHS).
            McpPathGuard.CheckRequiredFile(sourcePath, solutionDirectory: null, "sourcePath")
            ?? new IoddImportCoordinator().ImportIodd(sourcePath, targetFolder));

    [McpServerTool(Name = "twincat_check_iodd_present", ReadOnly = true)]
    [Description("Check whether a device's IODD is installed in TwinCAT's IODD folder. " +
                 "Identify the device by productId, ioddFileName or vendorId+deviceId. Returns 'IODD present: …' or a " +
                 "fail-closed '[Error] Missing IODD — STOP…' telling the user to obtain it. Run it before configuring or " +
                 "linking an IO-Link device. No open solution needed.")]
    public string CheckIoddPresent(
        [Description("Product id from the IODD's device variant, e.g. 'VABX-A-P-EL-E12-IOS'.")]
        string? productId = null,
        [Description("Exact IODD file name, e.g. 'Vendor-Device-20230815-IODD1.1.xml'.")]
        string? ioddFileName = null,
        [Description("IO-Link vendor id (with deviceId).")]
        int? vendorId = null,
        [Description("IO-Link device id (with vendorId).")]
        int? deviceId = null,
        [Description("Override of the IODD folder. Empty = TwinCAT default (…\\Config\\Io\\IOLink).")]
        string? targetFolder = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_check_iodd_present",
            new { productId, ioddFileName, vendorId, deviceId, targetFolder }, () =>
            new IoddImportCoordinator().CheckPresent(productId, vendorId, deviceId, ioddFileName, targetFolder), cancellationToken);
}
