using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>io-tasks — the Profinet device (RT) on an EL6631-0010, MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_profinet_device_add", ReadOnly = false, Destructive = false)]
    [Description("Create a Profinet device (RT) on an EL6631-0010 terminal. TwinCAT acts as Profinet IO device for an external " +
                 "Profinet controller. " +
                 "Creates the I/O device under I/O > Devices, binds it to the EL6631-0010 in the EtherCAT network and adds its Profinet " +
                 "box from the newest EL6631 GSDML file of the TwinCAT installation (Config\\Io\\Profinet); the XAE names that box itself. Refused with [Error] before " +
                 "the logout, nothing changed: the box is not an EL6631-0010 or already used by a device, the name is taken, no GSDML " +
                 "file, unknown dap, unknown sync task or one without image. Logs the XAE out first and reads the binding back. If a " +
                 "later step fails, the [Error] names what was created. Saves on success; effective at the next Activate " +
                 "Configuration. Add modules with twincat_profinet_module_add.")]
    public string ProfinetDeviceAdd(
        [Description("Device name under I/O > Devices, e.g. 'PN Device'.")] string name,
        [Description("Tree path of the EL6631-0010 EtherCAT box, e.g. 'TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 5 (EL6631-0010)'.")] string boxPath,
        [Description("Device access point number of the GSDML (1 = oldest firmware version). It must match the terminal's firmware. Default: the newest.")] int? dap = null,
        [Description("Optional sync task (name or path of a task with image, e.g. 'IoTask'); see twincat_io_device_sync_task.")] string? syncTask = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_profinet_device_add", new { name, boxPath, dap, syncTask }, () =>
            bridge.Dispatcher.Run(() =>
                new ProfinetCoordinator(bridge.Dispatcher.Session).AddDevice(name, boxPath, dap, syncTask)), cancellationToken);

    [McpServerTool(Name = "twincat_profinet_list_modules", ReadOnly = true)]
    [Description("List the placed and the offered modules of a Profinet device (RT). Placed: per slot; offered: the module types " +
                 "its DAP allows (from the device's GSDML file). Read-only; needs an open solution. Returns a summary line and JSON { device, " +
                 "box, api, gsdml, dap { number, name, maxSlot }, placed: [{ slot, name, module, identNumber, path }] (slot 0 = the DAP), catalog: " +
                 "[{ module, name, category, identNumber, inputBytes, outputBytes }] }.")]
    public string ProfinetListModules(
        [Description("Tree path of the Profinet device, e.g. 'TIID^PN Device'.")] string device,
        [Description("Optional case-insensitive filter on module ID, name or category, e.g. 'Byte' or 'Float32'.")] string? filter = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_profinet_list_modules", new { device, filter }, () =>
            bridge.Dispatcher.Run(() =>
                new ProfinetCoordinator(bridge.Dispatcher.Session).ListModules(device, filter)), cancellationToken);

    [McpServerTool(Name = "twincat_profinet_module_add", ReadOnly = false, Destructive = false)]
    [Description("Place one module from the GSDML catalog of a Profinet device (RT) in a slot. Refused with [Error] before the logout, " +
                 "nothing changed: unknown module, module not allowed by the DAP or modular, slot 0 (the DAP) or beyond the last slot, " +
                 "slot occupied. Logs the XAE out first, reads slot and module back and saves; effective at the next Activate " +
                 "Configuration. The remote Profinet controller must be configured with the same modules in the same slots.")]
    public string ProfinetModuleAdd(
        [Description("Tree path of the Profinet device, e.g. 'TIID^PN Device'.")] string device,
        [Description("The module: GSDML ID (e.g. 'BYTE_I4'), name (e.g. '4 Byte Input') or ident number (e.g. '0x706'), from twincat_profinet_list_modules.")] string module,
        [Description("Slot number, 1 or higher (slot 0 holds the DAP).")] int slot,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_profinet_module_add", new { device, module, slot }, () =>
            bridge.Dispatcher.Run(() =>
                new ProfinetCoordinator(bridge.Dispatcher.Session).AddModule(device, module, slot)), cancellationToken);
}
