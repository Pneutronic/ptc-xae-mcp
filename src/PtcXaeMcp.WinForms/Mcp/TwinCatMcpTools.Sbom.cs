using ModelContextProtocol.Server;
using PtcXaeMcp.WinForms.TwinCat;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_ipc_read_inventory", ReadOnly = true)]
    [Description("READ-ONLY raw inventory of a Beckhoff IPC from its Device Manager (MDP) over ADS (AMS port 10000, " +
                 "index group 0xF302): device BTN/BIC, every module (TwinCAT version/build/System ID, installed " +
                 "Beckhoff software, OS, mainboard serial/BIOS, disks, NICs, CPU, memory) with all tables read " +
                 "dynamically (as many disks/NICs/software entries as the device reports). Each value carries its " +
                 "index offset; unknown modules and tables are returned as raw hex, never dropped. Returns JSON " +
                 "{ success, status (ok | mdpUnavailable | targetUnreachable | mdpNotReady), message, netId, port, " +
                 "indexGroup, readAt, adsError, device { btn, bic }, modules [{ moduleId, moduleType, typeName, name, " +
                 "tables [{ tableIndex, name, entries [{ subIndex, name, dataType, value, raw, adsError, indexOffset }] }], " +
                 "decoded }], warnings }. A target without a Device Manager (e.g. a development VM or the user-mode " +
                 "runtime) yields status mdpUnavailable, not an [Error]. [Error] is returned only for bad arguments. " +
                 "The output contains IP/MAC addresses, route names and the TwinCAT System ID.")]
    public string IpcReadInventory(
        [Description("AMS NetId of the target IPC (e.g. '192.168.1.10.1.1'). Omit for the open solution's own target " +
                     "system (activation target); with an explicit NetId no open solution is needed.")] string? amsNetId = null,
        [Description("Optional filter on MDP module types, e.g. ['0x0008','0x000A'] (TwinCAT, Software). " +
                     "Known types: 0x0002 NIC, 0x0008 TwinCAT, 0x000A Software, 0x000B CPU, 0x000C Memory, " +
                     "0x0018 OperatingSystem, 0x001C Mainboard, 0x001D DiskManagement. Default: all modules.")] string[]? moduleTypes = null,
        [Description("Include the raw hex of every decoded value too (default true). Undecodable values always carry it.")] bool includeRaw = true,
        [Description("ADS timeout per read in milliseconds (default 5000, 100-30000). A read that times out is repeated once before the error is recorded.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ipc_read_inventory", new { amsNetId, moduleTypes, includeRaw, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                new IpcInventoryCoordinator(bridge.Dispatcher.Session)
                    .ReadInventory(amsNetId, moduleTypes, includeRaw, timeoutMs)), cancellationToken);

    [McpServerTool(Name = "twincat_ethercat_read_identities", ReadOnly = true)]
    [Description("READ-ONLY online identity of every EtherCAT slave: VendorId, ProductCode, RevisionNo and SerialNo as the " +
                 "EtherCAT master reads them from the devices (ADS index groups 0x7/0x9/0x11), joined by fixed address with " +
                 "the projected identity of the configured boxes (device XML). Enumerates all EtherCAT master devices under " +
                 "TIID (or the one in masterTreePath); the master's AMS NetId and ADS port come from its device XML, and " +
                 "the result states which were read. Per slave: address, treePath, name, projected {vendorId, productCode, " +
                 "revisionNo}, online {vendorId, productCode, revisionNo, serialNo}, state/stateName, link/linkFlags, " +
                 "flags identityInvalid, revisionDiffers, productDiffers, vendorDiffers, serialAvailable (serialNo != 0), " +
                 "missing, extraLink, and source {netId, port, indexGroup, indexOffset}. Projected boxes without an online " +
                 "address and online addresses without a projected box are both listed. With includeCoe the CoE device info is added per slave (coe {status ok|noMailbox|notCoe|error, deviceName, hardwareVersion, firmwareVersion, serialNo, adsError}); serialNo (a string, so that leading zeros survive) and serialSource give the serial from the master (serialSource master), else from CoE 0x1018:04 (coe), else from the SoE S-0-0432 of an AX5000 drive (soe:S-0-0432, digits as read), else for a safety device its effective safety identifier (coe:0xF980:02, bic:BTN, hostDrive:S-0-0432); serialAvailable is false when none exists. The master service port is 0xFFFF. With includeDeviceDetails each slave may also carry bic {raw, orderNumber, btn, article, batch}, safety {isSafetyDevice, fsoeAddress, serialNo (0xF980:02, undocumented), effectiveSerial, effectiveSerialSource, viaHostDrive}, motor/feedback (OCT terminals) and soe.channels[] (AX5000; softwareVersions from P-0-0320, whose optionCard fields describe a plug-in option card of the drive, e.g. an encoder card, null when none is fitted); an AX5805/AX5806 also shows safety.hostDrive {address, serialNo, configuredSafetyOption}; every safety device gets an effective identifier or safetySerialMissing=true plus a warning and safetyIdentityComplete=false. The result has a summary {slaves, slavesRead, slavesPartial, slavesFailed, safetyDevices, safetyWithSerial, safetyUnknown, motors, motorsWithSerial}. Reads that time out are repeated once; every slave has readStatus ok|partial|failed with readErrors, the master (and the result) is status partial when any slave could not be read completely or the fixed addresses/state/link reads failed (the fixed addresses then fall back to the projected ones), and a safety device whose identity could not be read is safetyIdentityUnknown (not safetySerialMissing); safetyIdentityComplete is false in both cases. A master that does not answer " +
                 "(configuration not active on the target / no route) is a per-master status masterUnreachable and the " +
                 "other masters continue; a master whose address cannot be read from the XML is addressNotFound with the " +
                 "raw XML snippet. Never changes state, never switches the project to config mode. Requires XAE running " +
                 "with a solution open and the configuration active on the target.")]
    public string EtherCatReadIdentities(
        [Description("Tree path of ONE EtherCAT master device, e.g. 'TIID^Device 1 (EtherCAT)'. Omit to read all masters.")] string? masterTreePath = null,
        [Description("Override of the master's AMS NetId. Only valid together with masterTreePath.")] string? amsNetId = null,
        [Description("Override of the master's ADS port. Only valid together with masterTreePath.")] int? adsPort = null,
        [Description("Also read the CRC error counters of every slave (per port A-D). Default false.")] bool includeCrc = false,
        [Description("Also read the CoE device info of every slave with a mailbox (read-only SDO upload: 0x1008 device name, 0x1009 hardware version, 0x100A firmware version, 0x1018:04 serial). The master reports SerialNo 0 for most slaves; the CoE serial fills the gap. Default true.")] bool includeCoe = true,
        [Description("Also read the device details per slave (requires includeCoe): the BIC/BTN (CoE 0x10E2:01), the safety identity (0xF980) with an effective unique safety identifier, the OCT motor and feedback name plate (0x9009/0x9008), and for AX5000 drives the SoE parameters per channel (firmware, hardware, serial, configured type, safety option, digital motor name plate). Default true.")] bool includeDeviceDetails = true,
        [Description("ADS timeout per read in milliseconds (default 5000, 100-30000). A read that times out is repeated once before the error is recorded.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_ethercat_read_identities", new { masterTreePath, amsNetId, adsPort, includeCrc, includeCoe, includeDeviceDetails, timeoutMs }, () =>
            bridge.Dispatcher.Run(() =>
                new EtherCatIdentityCoordinator(bridge.Dispatcher.Session)
                    .ReadIdentities(masterTreePath, amsNetId, adsPort, includeCrc, includeCoe, includeDeviceDetails, timeoutMs)), cancellationToken);

    [McpServerTool(Name = "twincat_list_library_repositories", ReadOnly = true)]
    [Description("READ-ONLY list of the PLC library repositories of the XAE (ITcPlcLibraryManager.Repositories): name and " +
                 "folder of each. With includeFiles=true, for every library reference of the PLC project(s) the resolved " +
                 "library (resolvedName, resolvedVersion, resolvedDistributor, resolution = effective | fixed | " +
                 "newest-installed | unresolved), its version folder in the repository and the library file(s) in it " +
                 "(.library / .compiled-library*) with SHA-256 (hashScope 'file': the hash of the file itself, not a " +
                 "Twinpack content hash). If no file is found: fileFound=false plus the searchedPaths. Returns JSON " +
                 "{ success, repositories [{ name, folder, folderExists }], references [{ plcProject, placeholder, name, " +
                 "requestedVersion, resolved*, resolution, fileFound, files [{ path, sha256, hashScope, sizeBytes }], " +
                 "searchedPaths }], warnings } or an [Error] string. Requires XAE running with a solution open.")]
    public string ListLibraryRepositories(
        [Description("Also resolve every library reference to its file(s) in the repositories and hash them. Default false.")] bool includeFiles = false,
        [Description("Only this PLC project (its name or tree path, e.g. 'PLC' or 'TIPC^PLC'). Default: all PLC projects of the solution.")] string? plcProjectPath = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_library_repositories", new { includeFiles, plcProjectPath }, () =>
            bridge.Dispatcher.Run(() =>
                new LibraryRepositoryCoordinator(bridge.Dispatcher.Session)
                    .List(includeFiles, plcProjectPath)), cancellationToken);
}
