using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_link_variables", ReadOnly = false, Destructive = true)]
    [Description("Link two TwinCAT variables given by their tree paths and save the project. " +
                 "Needs an open solution. I/O paths (TIID^...): twincat_get_fieldbus_topology; PLC paths (TIPC^...): " +
                 "twincat_plc_list. A refused link names the cause: type, bit size and direction of both variables, " +
                 "a path that does not resolve, or a path that is not a variable.")]
    public string LinkVariables(
        [Description("Tree path of the first variable, e.g. 'TIPC^PLC^PLC Project^MAIN.bVar'.")] string variable1TreePath,
        [Description("Tree path of the second variable, e.g. 'TIID^Device 1 (EtherCAT)^Box 1^Channel 1'.")] string variable2TreePath,
        [Description("Bit offset in the first variable (default 0).")] int bitOffset1 = 0,
        [Description("Bit offset in the second variable (default 0).")] int bitOffset2 = 0,
        [Description("Bits to link; 0 = smaller size of both variables (default 0).")] int size = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_link_variables",
            new { variable1TreePath, variable2TreePath, bitOffset1, bitOffset2, size }, () =>
            bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).LinkVariables(
                    variable1TreePath ?? "", variable2TreePath ?? "", bitOffset1, bitOffset2, size)), cancellationToken);

    [McpServerTool(Name = "twincat_unlink_variables", ReadOnly = false, Destructive = true)]
    [Description("Remove a variable link, or all links of one variable, and save the project. " +
                 "Needs an open solution.")]
    public string UnlinkVariables(
        [Description("Tree path of the first variable.")] string variable1TreePath,
        [Description("Tree path of the second variable. Empty = remove ALL links of variable1TreePath.")] string variable2TreePath = "",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_unlink_variables", new { variable1TreePath, variable2TreePath },
            () => bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).UnlinkVariables(variable1TreePath ?? "", variable2TreePath ?? "")), cancellationToken);

    [McpServerTool(Name = "twincat_get_mapping_info", ReadOnly = false, Destructive = true)]
    [Description("Export all variable mappings (links) as VarLinks XML to a file. " +
                 "Overwrites an existing file at the target. Does not change the project. Needs an open solution.")]
    public string GetMappingInfo(
        [Description("Absolute path of the XML file (directory must exist). Omit for Mappings.xml next to the .sln.")] string? outputPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_mapping_info", new { outputPath }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(outputPath, bridge.Dispatcher.Session, "outputPath")
                ?? new IoMappingCoordinator(bridge.Dispatcher.Session).GetMappingInfo(outputPath)), cancellationToken);

    [McpServerTool(Name = "twincat_set_device_disabled", ReadOnly = false, Destructive = true)]
    [Description("Enable or disable a TwinCAT I/O device by its tree path and save the project. " +
                 "Needs an open solution.")]
    public string SetDeviceDisabled(
        [Description("Tree path of the I/O device, e.g. 'TIID^Device 1 (EtherCAT)'. " +
                     "From twincat_get_fieldbus_topology.")] string treePath,
        [Description("true to disable the device, false to enable it.")] bool disabled,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_device_disabled",
            new { treePath, disabled }, () =>
            bridge.Dispatcher.Run(() =>
                new IoMappingCoordinator(bridge.Dispatcher.Session).SetDeviceDisabled(treePath, disabled)), cancellationToken);
}
