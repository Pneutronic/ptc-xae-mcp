using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_read_coe_dictionary", ReadOnly = true)]
    [Description("Read the CoE offline dictionary of an EtherCAT slave from its ESI file. " +
                 "Per object: index, name, access, objectType and sub-indexes (subindex, name, dataType, bitSize, " +
                 "defaultValue, defaultText for enums e.g. 'IIR level 1 (2)', access, isSetting). " +
                 "Offline: no active configuration or controller needed. " +
                 "Result: summary line + JSON { treePath, slaveType, fallbackNote, total, offset, returned, has_more, " +
                 "next_offset, objects[] }; total counts objects matching indexFilter.")]
    public string ReadCoeDictionary(
        [Description("Tree path of the EtherCAT slave, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 2 (EL3174)'. " +
                     "From twincat_get_fieldbus_topology.")]
        string treePath,
        [Description("Index '800D' or hex range '8000-80FF'. Omit for all objects.")]
        string? indexFilter = null,
        [Description("Objects per page: default 100, max 1000 (0 = 1000).")]
        int? limit = null,
        [Description("Objects to skip: next_offset of the previous call while has_more. Default 0.")]
        int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_coe_dictionary", new { treePath, indexFilter, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
            {
                var (summary, json) = new CoeCoordinator(bridge.Dispatcher.Session)
                    .ReadDictionary(treePath, indexFilter, limit, offset);
                if (McpErrors.IsError(summary))
                    return summary;
                return summary + Environment.NewLine + json;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_read_coe_startup", ReadOnly = true)]
    [Description("Read the project's CoE Startup SDO overrides of an EtherCAT slave. " +
                 "These CoE values are written to the device on the PS / SafeOp→Op transition, i.e. where the project " +
                 "deviates from the ESI default. Empty list (not an error) when none. Read-only, CoE only " +
                 "(SoE/EoE/FoE and fixed entries: twincat_read_ethercat_startup). " +
                 "Result: summary line + JSON (entryCount, entries[]).")]
    public string ReadCoeStartup(
        [Description("Tree path of the EtherCAT slave, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 2 (EL3174)'. " +
                     "From twincat_get_fieldbus_topology.")]
        string treePath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_coe_startup", new { treePath }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, json) = new CoeCoordinator(bridge.Dispatcher.Session)
                    .ReadStartup(treePath);
                if (McpErrors.IsError(summary))
                    return summary;
                return summary + Environment.NewLine + json;
            }), cancellationToken);

    [McpServerTool(Name = "twincat_read_ethercat_startup", ReadOnly = true)]
    [Description("Read the full EtherCAT Startup list of boxes, as the Startup tab shows it. " +
                 "Read-only and offline (no active configuration needed); needs an open solution. Covers CoE, SoE, EoE " +
                 "and FoE mailbox commands, both fixed (generated from the PDO/telegram configuration, in angle brackets) " +
                 "and projected; twincat_read_coe_startup sees only CoE and reports 0 entries for a SoE drive. " +
                 "ESC register commands (set sm ...) are not returned. " +
                 "Result: summary line + JSON { slaveCount, slavesScanned, entryCount, fixedCount, byProtocol, " +
                 "boxes[{ treePath, name, slaveType, physAddr, linkedAxis, soeChannels, virtualPort (EoE), entryCount, " +
                 "entries[] }] }. Entry: protocol, fixed, transitions[], comment, timeoutMs, dataHex; CoE adds index (hex), " +
                 "subIndex, completeAccess, source; SoE adds idn ('S-0-0015'), idnRaw, parameterSet, driveNo (0 = first " +
                 "drive), opCode, elements, attribute, value (unsigned little endian for 2/4/8 bytes), list " +
                 "{currentLength, maxLength} (a plain 4-byte value can look like an empty list), idnList (AT S-0-0016 / " +
                 "MDT S-0-0024).")]
    public string ReadEtherCatStartup(
        [Description("Tree path of ONE EtherCAT box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Drive 1'. " +
                     "From twincat_get_fieldbus_topology. Omit for all boxes of all masters (only boxes with entries are listed).")]
        string? treePath = null,
        [Description("Include the TwinCAT-generated fixed entries. Default true; false = only what the project configures.")]
        bool includeFixed = true,
        [Description("Only CoE, SoE, EoE or FoE (case-insensitive). Omit for all.")]
        string? protocol = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_ethercat_startup", new { treePath, includeFixed, protocol }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatStartupCoordinator(bridge.Dispatcher.Session)
                    .Read(treePath, includeFixed, protocol)), cancellationToken);

    [McpServerTool(Name = "twincat_read_soe_parameter", ReadOnly = true)]
    [Description("Read SoE parameters (IDNs) live from a SoE drive over ADS; never writes. " +
                 "For drives such as Beckhoff AX5000 or Rexroth IndraDrive. Needs the configuration active on the target " +
                 "and the slave at least in PREOP. With idns: reads those IDNs and elements. Without idns: reads every SoE " +
                 "entry of the box's Startup list (twincat_read_ethercat_startup) from the drive and compares (projected, " +
                 "actual, match; AT/MDT lists compared without max length; entries that do not write the value element get " +
                 "a note). Result: summary line + JSON { treePath, netId, port, mode (read | compare), parameters[{ idn, " +
                 "idnRaw, driveNo, name, unit, attribute {rawHex, ...}, value/min/max/default {hex, length, unsigned, " +
                 "signed + scaled (only with attribute), list, idnList, text}, dataStatus, errors[{element, code, name}], " +
                 "fixed, comment, projected, actual, match, note }] }. Scaling from the attribute is not yet verified " +
                 "against a drive: rawHex is authoritative. An ADS error of one IDN (e.g. DeviceInvalidOffset = not " +
                 "supported) goes into its errors; a drive that does not answer is an [Error] with NetId and port.")]
    public string ReadSoeParameter(
        [Description("Tree path of the SoE drive's EtherCAT box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Drive 1'. " +
                     "From twincat_get_fieldbus_topology.")]
        string treePath,
        [Description("IDNs 'S-0-0001', 'P-0-3231' or decimal, at most 256. Omit to compare the box's Startup SoE entries " +
                     "with the drive.")]
        string[]? idns = null,
        [Description("Drive within the box (0 = first; an AX5206 has 0 and 1). With idns default 0; without idns it " +
                     "filters Startup entries by DriveNo (default: each entry at its own DriveNo).")]
        int? driveNo = null,
        [Description("Elements per IDN (only with idns): value (default), name, attribute, unit, min, max, default, " +
                     "dataStatus. Add 'attribute' for decoded, scaled values.")]
        string[]? elements = null,
        [Description("ADS timeout per read in ms (default 5000, 100-30000); a timed-out read is retried once.")]
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_soe_parameter", new { treePath, idns, driveNo, elements, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                new SoeParameterCoordinator(bridge.Dispatcher.Session)
                    .Read(treePath, idns, driveNo, elements, timeoutMs)), cancellationToken);
}
