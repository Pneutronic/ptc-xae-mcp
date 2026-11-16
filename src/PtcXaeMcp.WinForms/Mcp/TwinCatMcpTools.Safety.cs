using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // twincat_safety_recon moved to TwinCatMcpToolsDiagnostics (Mcp/TwinCatMcpTools.Diagnostics.cs) —
    // a read-only research spike, registered only with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

    // ── Production safety tools ───────────────────────────────────────────────

    [McpServerTool(Name = "twincat_safety_list", ReadOnly = true)]
    [Description("List the TwinCAT Safety project's alias devices, groups and user FBs. " +
                 "Read-only, from the .splcproj and .sds files on disk. Alias devices (name = .sds file name): SDSID, " +
                 "AliasDeviceType Type/SubType, IO name, LinkingMode, folder; groups (.sal); user FBs (.ufb).")]
    public string SafetyList(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_list", null, () =>
            bridge.Dispatcher.Run(() => new SafetyCoordinator(bridge.Dispatcher.Session).List()), cancellationToken);

    [McpServerTool(Name = "twincat_safety_read_io_map", ReadOnly = true)]
    [Description("Read the safe-I/O assignment as three separate fact lists. Read-only. " +
                 "Alias devices (.sds: SDSID, type, IO); channel usage per group (.sal aliasDeviceIoUsage: variable, " +
                 "source/target, sdsId, channel), joined to a device ONLY by sdsId = SDSID; Mappings.xml rows as found, " +
                 "naming a device only when the row carries its SDSID. Nothing is joined by position or guessed.")]
    public string SafetyReadIoMap(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_read_io_map", null, () =>
            bridge.Dispatcher.Run(() => new SafetyCoordinator(bridge.Dispatcher.Session).ReadIoMap()), cancellationToken);

    [McpServerTool(Name = "twincat_safety_read_program", ReadOnly = true)]
    [Description("Dump the FBD safety logic per group/network from the .sal files. Read-only. " +
                 "FB instances (type + name), input/output port sources/targets (connection monikers), FB parameters " +
                 "(discrepancy, Tof off-delays, EDM, restart). Flags [inferred] wiring.")]
    public string SafetyReadProgram(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_read_program", null, () =>
            bridge.Dispatcher.Run(() => new SafetyCoordinator(bridge.Dispatcher.Session).ReadProgram()), cancellationToken);

    [McpServerTool(Name = "twincat_safety_add_alias_device", ReadOnly = false, Destructive = true)]
    [Description("Create a digital alias device: a BOOL between safety logic and standard PLC. " +
                 "EXPERIMENTAL: the XAE reload step is not yet verified against a live TwinSAFE project. Validates the " +
                 "name, allocates an unused SDSID, writes a standard digital .sds (Type 1, Crc 0) next to the group's alias " +
                 "devices, registers it in .splcproj (the .sds is rolled back if that fails), reads it back and reloads the " +
                 "project in the XAE. Refuses an existing name. The CRC stays 0 and is never computed: run 'Verify Complete " +
                 "Safety Project' in the TwinSAFE editor before download. Never downloads or activates the safety project.")]
    public string SafetyAddAliasDevice(
        [Description("Signal name = .sds file name: an IEC identifier with a configured prefix (default bSI_, bSQ_, SI, " +
                     "SQ; PTC_XAE_MCP_SAFETY_PREFIXES overrides).")]
        string signalName,
        [Description("2 = '1 Digital Output (Standard)': safety → standard PLC (e.g. bSI_ feedback). " +
                     "1 = '1 Digital Input (Standard)': standard PLC → safety (e.g. bSQ_ request). Default 2.")]
        int subType = 2,
        [Description("Group (.sal name without extension). Required only when the project has more than one group.")]
        string? group = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_add_alias_device",
            new { signalName, subType, group }, () =>
            bridge.Dispatcher.Run(() =>
                new SafetyCoordinator(bridge.Dispatcher.Session).AddAliasDevice(signalName, subType, group)), cancellationToken);

    [McpServerTool(Name = "twincat_safety_link_alias_device", ReadOnly = false, Destructive = true)]
    [Description("Link an alias device's EL1918 connection-image symbol to a standard-PLC symbol. " +
                 "Needs the safety project mapped/built so the connection symbol exists; otherwise a precondition error.")]
    public string SafetyLinkAliasDevice(
        [Description("Tree path of the alias device's connection-image symbol, " +
                     "e.g. 'TISC^TwinSAFE_PLC^Alias Devices^bSI_DriveReleased'.")]
        string aliasDevicePath,
        [Description("Dot path of the standard-PLC symbol, e.g. 'GVL_Safety.bSI_DriveReleased'.")]
        string plcSymbolPath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_safety_link_alias_device",
            new { aliasDevicePath, plcSymbolPath }, () =>
            bridge.Dispatcher.Run(() =>
                bridge.Dispatcher.Session.SafetyLinkAliasDevice(aliasDevicePath, plcSymbolPath)), cancellationToken);

    // twincat_safety_probe_create_alias moved to TwinCatMcpToolsDiagnostics
    // (Mcp/TwinCatMcpTools.Diagnostics.cs) — a write-probe research tool, registered only
    // with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.
}
