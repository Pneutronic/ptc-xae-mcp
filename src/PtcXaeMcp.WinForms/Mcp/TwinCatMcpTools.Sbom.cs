using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_ipc_read_inventory", ReadOnly = true)]
    [Description("Read a Beckhoff IPC's inventory from its Device Manager (MDP) over ADS. " +
                 "Read-only: device BTN/BIC and every module (TwinCAT version/build/System ID, installed Beckhoff software, " +
                 "OS, mainboard serial/BIOS, disks, NICs, CPU, memory); unknown modules and tables come as raw hex, never " +
                 "dropped. Returns JSON { success, status (ok | mdpUnavailable | targetUnreachable | mdpNotReady), message, " +
                 "netId, device { btn, bic }, modules [{ moduleType, typeName, name, tables [{ name, entries [{ name, value, " +
                 "raw, indexOffset, adsError }] }], decoded }], warnings }. A target without Device Manager (e.g. a VM or the " +
                 "user-mode runtime) gives status mdpUnavailable, not [Error]; [Error] only for bad arguments. The output " +
                 "contains IP/MAC addresses, route names and the TwinCAT System ID.")]
    public string IpcReadInventory(
        [Description("Target IPC AMS NetId, e.g. '192.168.1.10.1.1'. Omit for the open solution's target; with a NetId no open solution is needed.")] string? amsNetId = null,
        [Description("Filter on MDP module types, e.g. ['0x0008','0x000A']. Known: 0x0002 NIC, 0x0008 TwinCAT, " +
                     "0x000A Software, 0x000B CPU, 0x000C Memory, 0x0018 OS, 0x001C Mainboard, 0x001D Disks. Default: all.")] string[]? moduleTypes = null,
        [Description("Also give the raw hex of decoded values (undecodable ones always have it). Default true.")] bool includeRaw = true,
        [Description("ADS timeout per read in ms, 100-30000. Default 5000; a timed-out read is retried once.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ipc_read_inventory", new { amsNetId, moduleTypes, includeRaw, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                new IpcInventoryCoordinator(bridge.Dispatcher.Session)
                    .ReadInventory(amsNetId, moduleTypes, includeRaw, timeoutMs)), cancellationToken);

    [McpServerTool(Name = "twincat_ethercat_read_identities", ReadOnly = true)]
    [Description("Read vendor, product, revision and serial of every EtherCAT slave online. " +
                 "Read-only; never changes state or switches to config mode. Joins the master's online data with the configured " +
                 "boxes by fixed address, for all masters (or masterTreePath). Per slave: address, treePath, name, projected " +
                 "{vendorId, productCode, revisionNo}, online {… serialNo}, state/stateName, link/linkFlags, flags " +
                 "identityInvalid, revisionDiffers, productDiffers, vendorDiffers, serialAvailable, missing, extraLink, source. " +
                 "Boxes without online address and online addresses without box are both listed. serialNo is a string " +
                 "(leading zeros kept); serialSource: master, else coe (0x1018:04), else soe:S-0-0432 (AX5000), else for a " +
                 "safety device its effective identifier (coe:0xF980:02, bic:BTN, hostDrive:S-0-0432). Every safety device " +
                 "gets an effective identifier or safetySerialMissing=true with a warning; unreadable identity = " +
                 "safetyIdentityUnknown; safetyIdentityComplete is false in both cases. Each slave has readStatus " +
                 "ok|partial|failed with readErrors; any incomplete read (or failed address/state/link read, then projected " +
                 "addresses are used) makes master and result status partial. A master that does not answer is " +
                 "masterUnreachable (others continue); one without address in its XML is addressNotFound. Result has summary " +
                 "{slaves, slavesRead, slavesPartial, slavesFailed, safetyDevices, safetyWithSerial, safetyUnknown, motors, " +
                 "motorsWithSerial}. Needs an open solution and the configuration active on the target.")]
    public string EtherCatReadIdentities(
        [Description("Tree path of ONE EtherCAT master, e.g. 'TIID^Device 1 (EtherCAT)'. Omit for all masters.")] string? masterTreePath = null,
        [Description("Override of the master's AMS NetId (default from its device XML). Only with masterTreePath.")] string? amsNetId = null,
        [Description("Override of the master's ADS port (master service port 0xFFFF). Only with masterTreePath.")] int? adsPort = null,
        [Description("Also read each slave's CRC error counters (ports A-D). Default false.")] bool includeCrc = false,
        [Description("Also read CoE device info of slaves with a mailbox (read-only SDO: 0x1008 name, 0x1009 hardware, 0x100A firmware, 0x1018:04 serial) into coe {status ok|noMailbox|notCoe|error, …}; fills the serial the master reports as 0. Default true.")] bool includeCoe = true,
        [Description("Needs includeCoe. Also read per slave: bic (BTN, CoE 0x10E2:01), safety identity (0xF980) with an effective unique identifier (AX5805/AX5806 also safety.hostDrive), OCT motor/feedback name plate (0x9009/0x9008), and for AX5000 the SoE parameters in soe.channels[] (firmware, hardware, serial, type, safety option, motor name plate; optionCard = a plug-in card such as an encoder card, null if none). Default true.")] bool includeDeviceDetails = true,
        [Description("ADS timeout per read in ms, 100-30000. Default 5000; a timed-out read is retried once.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ethercat_read_identities", new { masterTreePath, amsNetId, adsPort, includeCrc, includeCoe, includeDeviceDetails, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatIdentityCoordinator(bridge.Dispatcher.Session)
                    .ReadIdentities(masterTreePath, amsNetId, adsPort, includeCrc, includeCoe, includeDeviceDetails, timeoutMs)), cancellationToken);

    [McpServerTool(Name = "twincat_list_library_repositories", ReadOnly = true)]
    [Description("List the XAE's PLC library repositories, optionally with hashed library files. " +
                 "Read-only. With includeFiles each library reference gets its resolved library (resolution effective | fixed " +
                 "| newest-installed | unresolved) and the file(s) in its repository folder with SHA-256 of the file itself " +
                 "(not a Twinpack content hash); none found = fileFound=false plus searchedPaths. Returns JSON { success, " +
                 "repositories [{ name, folder, folderExists }], references [{ plcProject, placeholder, name, requestedVersion, " +
                 "resolved*, resolution, fileFound, files [{ path, sha256, hashScope, sizeBytes }], searchedPaths }], warnings } " +
                 "or [Error]. Needs an open solution.")]
    public string ListLibraryRepositories(
        [Description("Also resolve every library reference to its file(s) and hash them. Default false.")] bool includeFiles = false,
        [Description("Only this PLC project, by name or tree path (e.g. 'PLC' or 'TIPC^PLC'). Default: all.")] string? plcProjectPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_library_repositories", new { includeFiles, plcProjectPath }, () =>
            bridge.Dispatcher.Run(() =>
                new LibraryRepositoryCoordinator(bridge.Dispatcher.Session)
                    .List(includeFiles, plcProjectPath)), cancellationToken);
}
