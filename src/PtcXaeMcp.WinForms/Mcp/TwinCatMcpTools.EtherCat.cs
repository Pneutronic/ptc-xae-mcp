using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_add_ethercat_device", ReadOnly = false, Destructive = false)]
    [Description("Create a top-level EtherCAT device (master or slave) under TIID. " +
                 "Needs an open solution; saves the project. Returns the new device tree path, the parent for " +
                 "twincat_add_ethercat_box.")]
    public string AddEtherCatDevice(
        [Description("Device name under TIID, e.g. 'Machine (EtherCAT)'.")]
        string name,
        [Description("'master' (default) or 'slave'.")]
        string deviceKind = "master",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_ethercat_device", new { name, deviceKind }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).AddDevice(name, deviceKind)), cancellationToken);

    [McpServerTool(Name = "twincat_add_ethercat_box", ReadOnly = false, Destructive = false)]
    [Description("Add an EtherCAT box/terminal, or a whole subtree, under a TIID node. " +
                 "Same engine as twincat_apply_schema. Needs an open solution; saves the project. " +
                 "Pass EITHER vInfo (one box) OR boxesJson (a tree).")]
    public string AddEtherCatBox(
        [Description("Parent tree path, e.g. 'TIID^Device 1 (EtherCAT)' or 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. " +
                     "From twincat_get_fieldbus_topology; the bare root 'TIID' is rejected.")]
        string parentPath,
        [Description("Single box: type, bare 'EL9011' (latest installed revision) or revisioned 'AX5206-0000-0202'; other " +
                     "vendors as 'V<vendorId>_P<productCode>_R<revision>' (8 hex digits each; a bare third-party ESI Type is " +
                     "translated). See twincat_list_ethercat_box_catalog. Excludes boxesJson.")]
        string? vInfo = null,
        [Description("Single box: designation, e.g. 'A1.49 (EL9011)'. Default: vInfo.")]
        string? name = null,
        [Description("Single box: upstream port letter A/B/C/D.")]
        string? previousPort = null,
        [Description("Name of an existing sibling to insert before. Default: append at the end.")]
        string? beforeSibling = null,
        [Description("Subtree mode: apply_schema ethercat node JSON {name, vInfo, previousPort, children[]} as one object, " +
                     "an array of siblings, a {root:{…}} fragment or a full schema file. Excludes vInfo.")]
        string? boxesJson = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_ethercat_box",
            new { parentPath, vInfo, name, previousPort, beforeSibling, boxesJson }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session)
                    .AddBox(parentPath, vInfo, name, previousPort, beforeSibling, boxesJson)), cancellationToken);

    [McpServerTool(Name = "twincat_list_ethercat_box_catalog", ReadOnly = true)]
    [Description("List installable EtherCAT box types and their vInfo from the local ESI files. " +
                 "vInfo is the input of twincat_add_ethercat_box. Scans <TwinCAT>\\3.1\\Config\\Io\\EtherCAT offline: no XAE, " +
                 "solution or hardware needed. Per device: vInfo (revision-exact, e.g. EL1008-0000-0017; other vendors " +
                 "V<vendorId>_P<productCode>_R<revision>, e.g. V00000059_P00000013_R00000002), ESI Type, name, " +
                 "productCode, revisionNo, vendor, source file. Malformed ESI files are skipped and counted. The first scan " +
                 "can take seconds; cached until an ESI file changes. Result: summary line + JSON { esiDirectory, filter, " +
                 "latestRevisionOnly, filesScanned, filesSkipped, nonEsiFiles, devicesIndexed, cached, warnings, total, " +
                 "offset, returned, has_more, next_offset, devices[] }; total counts every match.")]
    public string ListEtherCatBoxCatalog(
        [Description("Case-insensitive substring of ESI Type/vInfo, e.g. 'EL1008' or 'AX52'. Strongly recommended: " +
                     "without it several thousand types are paged.")]
        string? filter = null,
        [Description("Devices per page: default 100, max 1000 (0 = 1000).")]
        int? limit = null,
        [Description("Devices to skip: next_offset of the previous call while has_more. Default 0.")]
        int offset = 0,
        [Description("true (default): newest revision per product only; false: every revision.")]
        bool latestRevisionOnly = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_ethercat_box_catalog",
            new { filter, limit, offset, latestRevisionOnly }, () =>
            EsiCatalogScanner.ListCatalog(filter, limit, offset, latestRevisionOnly,
                esiDirectory: null, cancellationToken), cancellationToken);

    [McpServerTool(Name = "twincat_remove_ethercat_box", ReadOnly = false, Destructive = true)]
    [Description("Delete an EtherCAT box/terminal by tree path and save the project. " +
                 "Needs an open solution. The root 'TIID' and top-level nodes without a parent are rejected.")]
    public string RemoveEtherCatBox(
        [Description("Tree path of the box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 4 (EL2004)'. " +
                     "From twincat_get_fieldbus_topology.")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_ethercat_box", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).RemoveBox(treePath)), cancellationToken);

    [McpServerTool(Name = "twincat_reload_device_descriptions", ReadOnly = false, Destructive = false)]
    [Description("Make the XAE re-read the EtherCAT device description (ESI) files. " +
                 "Runs 'TwinCAT > EtherCAT Devices > Reload Device Descriptions' (any XAE language). Run it after placing " +
                 "a vendor ESI under <TwinCAT>\\3.1\\Config\\Io\\EtherCAT; until then twincat_add_ethercat_box fails for " +
                 "that type with 'Invalid item sub type'. Needs an open solution; changes nothing in the project. " +
                 "A dialog the XAE shows is reported, never answered.")]
    public string ReloadDeviceDescriptions(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_reload_device_descriptions", null, () =>
            bridge.Dispatcher.Run(() =>
                new DeviceDescriptionsCoordinator(bridge.Dispatcher.Session).Reload()), cancellationToken);

    [McpServerTool(Name = "twincat_set_previous_port", ReadOnly = false, Destructive = true)]
    [Description("Set an EtherCAT box's upstream PreviousPort (A/B/C/D) and save the project. " +
                 "Needs an open solution. Attaches a box to a specific port of a multi-port coupler (e.g. EK1122) " +
                 "or repairs a box added without a port.")]
    public string SetEtherCatPreviousPort(
        [Description("Tree path of the box, e.g. 'TIID^Device 1 (EtherCAT)^Box 2 (EK1122)^Box 3 (EK1100)'.")]
        string treePath,
        [Description("Upstream port: A, B, C or D.")]
        string port,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_previous_port", new { treePath, port }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).SetPreviousPort(treePath, port)), cancellationToken);

    [McpServerTool(Name = "twincat_ethercat_scan_compare", ReadOnly = false, Destructive = false)]
    [Description("Scan an EtherCAT master's network and compare it with the configured boxes. " +
                 "The XAE Scan in config mode, without activating a configuration (twincat_ethercat_read_identities needs one): " +
                 "a temporary master on the same adapter scans the real network and is removed again; the project is left " +
                 "unchanged and nothing is saved. Needs config mode (twincat_set_system_mode config) and a network adapter " +
                 "assigned to the master. Compares by position and identity (vendor, product code, revision), not by box name. " +
                 "Returns JSON { treePath, identical, summary, configured[], scanned[], differences[] } where a difference is " +
                 "{ position, kind: missing | extra | different_product | different_revision, configured, scanned }, or [Error]; " +
                 "an [Error] that names a temporary device means it could not be removed and must be deleted by hand.")]
    public string EtherCatScanCompare(
        [Description("Tree path of the EtherCAT master under TIID, e.g. 'TIID^Device 1 (EtherCAT)' (twincat_get_fieldbus_topology).")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ethercat_scan_compare", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatScanCoordinator(bridge.Dispatcher.Session).ScanCompare(treePath)), cancellationToken);
}
