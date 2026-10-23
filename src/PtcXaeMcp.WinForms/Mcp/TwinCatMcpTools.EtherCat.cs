using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_add_ethercat_device", ReadOnly = false, Destructive = false)]
    [Description("Create a top-level EtherCAT device under TIID — a master (default) or slave. " +
                 "Requires XAE running with a solution open; saves the project. " +
                 "Returns the new device tree path; feed that path to twincat_add_ethercat_box " +
                 "(boxesJson) to build the topology under it.")]
    public string AddEtherCatDevice(
        [Description("Device name as it appears under TIID, e.g. '100000 SampleMachine (EtherCAT)'.")]
        string name,
        [Description("'master' (default) or 'slave'.")]
        string deviceKind = "master",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_ethercat_device", new { name, deviceKind }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).AddDevice(name, deviceKind)), cancellationToken);

    [McpServerTool(Name = "twincat_add_ethercat_box", ReadOnly = false, Destructive = false)]
    [Description("Add an EtherCAT box/terminal — or a whole subtree/topology — under a parent " +
                 "TIID node, using the same proven engine as twincat_apply_schema. " +
                 "Requires XAE running with a solution open; saves the project. " +
                 "Provide EITHER 'vInfo' for a single box, OR 'boxesJson' for a nested tree " +
                 "(the apply_schema ethercat node shape: {name,vInfo,previousPort,children[]}; " +
                 "a single object, an array of siblings, a {root:{…}} fragment or a full schema " +
                 "file are all accepted). vInfo may be a bare type (e.g. EL9011 — latest revision " +
                 "auto-selected) or revisioned (e.g. AX5206-0000-0202).")]
    public string AddEtherCatBox(
        [Description("Parent tree path under TIID, e.g. 'TIID^Device 1 (EtherCAT)' or " +
                     "'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. Get a valid path via " +
                     "twincat_get_fieldbus_topology. The bare root 'TIID' is rejected.")]
        string parentPath,
        [Description("Single-box mode: product type/revision (bare 'EL9011' or 'AX5206-0000-0202'). " +
                     "Mutually exclusive with boxesJson.")]
        string? vInfo = null,
        [Description("Optional designation/name for the new box (e.g. 'A1.49 (EL9011)'). " +
                     "Defaults to vInfo when omitted. Single-box mode only.")]
        string? name = null,
        [Description("Optional upstream port letter A/B/C/D for the new box. Single-box mode only.")]
        string? previousPort = null,
        [Description("Optional name of the existing sibling to insert before (ordering). " +
                     "Default appends at the end.")]
        string? beforeSibling = null,
        [Description("Subtree/topology mode: JSON in the apply_schema ethercat node shape. " +
                     "Mutually exclusive with vInfo.")]
        string? boxesJson = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_ethercat_box",
            new { parentPath, vInfo, name, previousPort, beforeSibling, boxesJson }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session)
                    .AddBox(parentPath, vInfo, name, previousPort, beforeSibling, boxesJson)), cancellationToken);

    [McpServerTool(Name = "twincat_list_ethercat_box_catalog", ReadOnly = true)]
    [Description("List installable EtherCAT box/terminal types and their vInfo strings for " +
                 "twincat_add_ethercat_box by scanning the local ESI files " +
                 "(<TwinCAT>\\3.1\\Config\\Io\\EtherCAT\\**\\*.xml) — offline, no XAE, no open solution, " +
                 "no hardware. Per device: vInfo (revision-exact, e.g. EL1008-0000-0017), ESI Type " +
                 "(order code), name (English preferred), productCode, revisionNo, vendor and source file. " +
                 "A bare type such as EL1008 is also accepted by twincat_add_ethercat_box (TwinCAT then " +
                 "picks the latest installed revision). Malformed ESI files are skipped and counted. " +
                 "The first scan of the full directory can take several seconds; results are cached per " +
                 "process until an ESI file changes. Result body is a one-line summary followed by " +
                 "indented JSON.")]
    public string ListEtherCatBoxCatalog(
        [Description("Case-insensitive substring of the ESI Type/vInfo, e.g. 'EL1008', 'EK11' or 'AX52'. " +
                     "Strongly recommended — without it the first maxResults of several thousand types are returned.")]
        string? filter = null,
        [Description("Maximum number of devices returned (default 100, capped at 1000). " +
                     "The summary/JSON report the total match count and whether the list was truncated.")]
        int maxResults = EsiCatalogScanner.DefaultMaxResults,
        [Description("true (default): only the newest revision per product/variant. " +
                     "false: every revision found in the ESI files.")]
        bool latestRevisionOnly = true,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_ethercat_box_catalog",
            new { filter, maxResults, latestRevisionOnly }, () =>
            EsiCatalogScanner.ListCatalog(filter, maxResults, latestRevisionOnly,
                esiDirectory: null, cancellationToken), cancellationToken);

    [McpServerTool(Name = "twincat_remove_ethercat_box", ReadOnly = false, Destructive = true)]
    [Description("Delete an EtherCAT box/terminal node by tree path and save the project. " +
                 "Requires XAE running with a solution open. The bare root 'TIID' and " +
                 "top-level nodes without a parent are rejected.")]
    public string RemoveEtherCatBox(
        [Description("Caret-separated tree path of the box to delete, e.g. " +
                     "'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 4 (EL2004)'. " +
                     "Get a valid path via twincat_get_fieldbus_topology.")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_ethercat_box", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).RemoveBox(treePath)), cancellationToken);

    [McpServerTool(Name = "twincat_reload_device_descriptions", ReadOnly = false, Destructive = false)]
    [Description("Make the XAE re-read the EtherCAT device descriptions (ESI files under <TwinCAT>\\3.1\\Config\\Io\\EtherCAT): " +
                 "the XAE command 'TwinCAT > EtherCAT Devices > Reload Device Descriptions', raised by its command identity, " +
                 "so it works on any XAE language. Run it after placing a vendor ESI there; until then " +
                 "twincat_add_ethercat_box fails for that type with 'Invalid item sub type'. " +
                 "Requires XAE running with a solution open; changes nothing in the project. " +
                 "A dialog the XAE shows is reported, never answered.")]
    public string ReloadDeviceDescriptions(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_reload_device_descriptions", null, () =>
            bridge.Dispatcher.Run(() =>
                new DeviceDescriptionsCoordinator(bridge.Dispatcher.Session).Reload()), cancellationToken);

    [McpServerTool(Name = "twincat_set_previous_port", ReadOnly = false, Destructive = true)]
    [Description("Set/override an EtherCAT box's upstream PreviousPort (A/B/C/D) and save. " +
                 "Requires XAE running with a solution open. Use this to attach a box to a " +
                 "specific port of a multi-port coupler (e.g. EK1122), or to repair a box " +
                 "added without an explicit port.")]
    public string SetEtherCatPreviousPort(
        [Description("Caret-separated tree path of the box, e.g. " +
                     "'TIID^Device 1 (EtherCAT)^Box 2 (EK1122)^Box 3 (EK1100)'.")]
        string treePath,
        [Description("Upstream port letter to select: A, B, C or D.")]
        string port,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_previous_port", new { treePath, port }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatEditCoordinator(bridge.Dispatcher.Session).SetPreviousPort(treePath, port)), cancellationToken);
}
