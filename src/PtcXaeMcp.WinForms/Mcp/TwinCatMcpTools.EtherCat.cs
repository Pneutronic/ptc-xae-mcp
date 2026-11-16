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
        [Description("Single box: type, bare 'EL9011' (latest installed revision) or revisioned 'AX5206-0000-0202' " +
                     "(see twincat_list_ethercat_box_catalog). Excludes boxesJson.")]
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
                 "solution or hardware needed. Per device: vInfo (revision-exact, e.g. EL1008-0000-0017), ESI Type, name, " +
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
}
