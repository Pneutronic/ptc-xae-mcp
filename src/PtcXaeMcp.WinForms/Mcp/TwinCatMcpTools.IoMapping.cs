using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_link_variables", ReadOnly = false, Destructive = true)]
    [Description("Create a link between two TwinCAT variables identified by their caret-separated tree paths. " +
                 "Requires XAE running with a solution open. Saves the project after linking. " +
                 "Use twincat_get_fieldbus_topology to find I/O paths (TIID^...) and " +
                 "twincat_plc_list to find PLC paths (TIPC^...). " +
                 "When TwinCAT refuses the link, the error names the cause: the type, bit size and direction of both " +
                 "variables, which path does not resolve, or which path is not a variable.")]
    public string LinkVariables(
        [Description("Full tree path of the first variable, e.g. 'TIPC^PLC^PLC Projekt^MAIN.bVar'.")] string variable1TreePath,
        [Description("Full tree path of the second variable, e.g. 'TIID^Device 1 (EtherCAT)^Box 1^Channel 1'.")] string variable2TreePath,
        [Description("Bit offset within variable1TreePath (default 0).")] int bitOffset1 = 0,
        [Description("Bit offset within variable2TreePath (default 0).")] int bitOffset2 = 0,
        [Description("Number of bits to link; 0 means minimum size of both variables (default 0).")] int size = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_link_variables",
            new { variable1TreePath, variable2TreePath, bitOffset1, bitOffset2, size }, () =>
            bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).LinkVariables(
                    variable1TreePath ?? "", variable2TreePath ?? "", bitOffset1, bitOffset2, size)), cancellationToken);

    [McpServerTool(Name = "twincat_unlink_variables", ReadOnly = false, Destructive = true)]
    [Description("Remove a variable link in TwinCAT. " +
                 "If variable2TreePath is omitted or empty, all links from variable1TreePath are removed. " +
                 "Requires XAE running with a solution open. Saves the project after unlinking.")]
    public string UnlinkVariables(
        [Description("Full tree path of the first variable.")] string variable1TreePath,
        [Description("Full tree path of the second variable. Leave empty to remove all links from variable1TreePath.")] string variable2TreePath = "",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_unlink_variables", new { variable1TreePath, variable2TreePath },
            () => bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).UnlinkVariables(variable1TreePath ?? "", variable2TreePath ?? "")), cancellationToken);

    [McpServerTool(Name = "twincat_get_mapping_info", ReadOnly = false, Destructive = true)]
    [Description("Export all current variable mappings as XML (VarLinks format) via ITcSysManager3::ProduceMappingInfo. " +
                 "Writes to outputPath if given, otherwise defaults to Mappings.xml next to the .sln (original " +
                 "behaviour, overwrites any existing file there). Read-only w.r.t. the XAE project itself. " +
                 "Requires XAE running with a solution open.")]
    public string GetMappingInfo(
        [Description("Absolute path to write the XML to. Omit to use the default (Mappings.xml next to the .sln).")] string? outputPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_mapping_info", new { outputPath }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(outputPath, bridge.Dispatcher.Session, "outputPath")
                ?? new IoMappingCoordinator(bridge.Dispatcher.Session).GetMappingInfo(outputPath)), cancellationToken);

    [McpServerTool(Name = "twincat_set_device_disabled", ReadOnly = false, Destructive = true)]
    [Description("Enable or disable a TwinCAT I/O device by its tree path. " +
                 "disabled=true sets SMDS_DISABLED; disabled=false sets SMDS_NOT_DISABLED (re-enables). " +
                 "Requires XAE running with a solution open. Saves the project after changing state.")]
    public string SetDeviceDisabled(
        [Description("Caret-separated tree path of the I/O device, e.g. 'TIID^Device 1 (EtherCAT)'. " +
                     "Get valid paths via twincat_get_fieldbus_topology.")] string treePath,
        [Description("true to disable the device, false to enable it.")] bool disabled,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_device_disabled",
            new { treePath, disabled }, () =>
            bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).SetDeviceDisabled(treePath, disabled)), cancellationToken);
}
