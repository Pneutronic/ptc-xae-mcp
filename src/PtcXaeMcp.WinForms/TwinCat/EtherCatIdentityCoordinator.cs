using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// sbom: validates the arguments of the EtherCAT identity read and shapes the result as JSON. A master that does not
/// answer is a structured per-master status; "[Error]" is for bad arguments and unexpected failures.
/// </summary>
internal sealed class EtherCatIdentityCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public string ReadIdentities(string? masterTreePath, string? amsNetId, int? adsPort, bool includeCrc, bool includeCoe = true, bool includeDeviceDetails = true, int? timeoutMs = null)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(masterTreePath);
        bool hasOverride = !string.IsNullOrWhiteSpace(amsNetId) || adsPort is not null;
        if (hasOverride && !hasPath)
            return "[Error] amsNetId and adsPort are only valid together with masterTreePath (they override one master's address).";
        if (!string.IsNullOrWhiteSpace(amsNetId) && !AmsNetIdText.IsLiteral(amsNetId))
            return $"[Error] amsNetId '{amsNetId}' is not an AMS NetId (six dot-separated numbers).";
        if (adsPort is < 1 or > 65535)
            return $"[Error] adsPort {adsPort} is out of range (1-65535).";

        string? timeoutError = AdsTimeout.Resolve(timeoutMs, out int timeout);
        if (timeoutError is not null) return timeoutError;

        string? guard = SessionGuards.RequireOpenSolution(session);
        if (guard is not null) return guard;

        EtherCatIdentitiesResult r;
        try
        {
            r = session.ReadEtherCatIdentities(hasPath ? masterTreePath!.Trim() : null,
                string.IsNullOrWhiteSpace(amsNetId) ? null : amsNetId.Trim(), adsPort, includeCrc, includeCoe, includeDeviceDetails, timeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.EnsureError(ex.Message);
        }
        return Format(r);
    }

    internal static string Format(EtherCatIdentitiesResult r) => JsonSerializer.Serialize(new
    {
        success = r.Success,
        safetyIdentityComplete = r.SafetyIdentityComplete,
        summary = new
        {
            slaves = r.Summary.Slaves,
            slavesRead = r.Summary.SlavesRead,
            slavesPartial = r.Summary.SlavesPartial,
            slavesFailed = r.Summary.SlavesFailed,
            safetyUnknown = r.Summary.SafetyUnknown,
            safetyDevices = r.Summary.SafetyDevices,
            safetyWithSerial = r.Summary.SafetyWithSerial,
            motors = r.Summary.Motors,
            motorsWithSerial = r.Summary.MotorsWithSerial,
        },
        warnings = r.Warnings,
        status = r.Status,
        message = r.Masters.Count == 0 ? "The project has no EtherCAT master device under TIID." : null,
        readAt = r.ReadAt,
        masters = r.Masters.Select(m => new
        {
            treePath = m.TreePath,
            name = m.Name,
            status = m.Status,
            message = m.Message,
            netId = m.NetId,
            port = m.Port,
            addressSource = m.AddressSource,
            rawSnippet = m.RawSnippet,
            adsError = m.AdsErrorCode is null ? null : new { code = m.AdsErrorCode, name = m.AdsErrorName },
            masterState = m.MasterState,
            masterStateName = m.MasterStateName,
            slaveCount = m.SlaveCount,
            slaves = m.Slaves.Select(s => new
            {
                address = s.Address,
                treePath = s.TreePath,
                name = s.Name,
                projected = s.Projected is null ? null : new
                {
                    vendorId = s.Projected.VendorId,
                    productCode = s.Projected.ProductCode,
                    revisionNo = s.Projected.RevisionNo,
                },
                online = s.Online is null ? null : new
                {
                    vendorId = s.Online.VendorId,
                    productCode = s.Online.ProductCode,
                    revisionNo = s.Online.RevisionNo,
                    serialNo = s.Online.SerialNo,
                },
                state = s.State,
                stateName = s.StateName,
                link = s.Link,
                linkFlags = s.LinkFlags,
                identityInvalid = s.IdentityInvalid,
                revisionDiffers = s.RevisionDiffers,
                productDiffers = s.ProductDiffers,
                vendorDiffers = s.VendorDiffers,
                serialAvailable = s.SerialAvailable,
                serialNo = s.SerialNo,
                serialSource = s.SerialSource,
                joinedBy = s.JoinedBy,
                readStatus = s.ReadStatus,
                readErrors = s.ReadErrors,
                safetyIdentityUnknown = s.SafetyIdentityUnknown,
                safetyIdentityUnknownReason = s.SafetyIdentityUnknownReason,
                passive = s.Projected?.Passive ?? false,
                safetySerialMissing = s.SafetySerialMissing,
                bic = s.Details?.Bic is null ? null : new
                {
                    raw = s.Details.Bic.Raw,
                    orderNumber = s.Details.Bic.OrderNumber,
                    btn = s.Details.Bic.Btn,
                    article = s.Details.Bic.Article,
                    batch = s.Details.Bic.Batch,
                },
                safety = s.Details?.Safety is null ? null : new
                {
                    isSafetyDevice = s.Details.Safety.IsSafetyDevice,
                    fsoeAddress = s.Details.Safety.FsoeAddress,
                    serialNo = s.Details.Safety.SerialNo,
                    serialSource = s.Details.Safety.SerialSource,
                    serialDocumented = s.Details.Safety.SerialDocumented,
                    effectiveSerial = s.Details.Safety.EffectiveSerial,
                    effectiveSerialSource = s.Details.Safety.EffectiveSerialSource,
                    viaHostDrive = s.Details.Safety.ViaHostDrive,
                    hostDriveAddress = s.Details.Safety.HostDriveAddress,
                    detection = s.Details.Safety.Detection,
                    note = s.Details.Safety.Note,
                    role = s.Details.Safety.Role,
                    hostDrive = s.Details.Safety.HostDrive is null ? null : new
                    {
                        address = s.Details.Safety.HostDrive.Address,
                        serialNo = s.Details.Safety.HostDrive.SerialNo,
                        configuredSafetyOption = s.Details.Safety.HostDrive.ConfiguredSafetyOption,
                    },
                },
                motor = s.Details?.Motor,
                feedback = s.Details?.Feedback,
                soe = s.Details?.Soe is null ? null : new
                {
                    channels = s.Details.Soe.Select(c => new
                    {
                        driveNo = c.DriveNo,
                        firmware = c.Firmware,
                        hardware = c.Hardware,
                        serialNo = c.SerialNo,
                        serialRaw = c.SerialRaw,
                        configuredType = c.ConfiguredType,
                        safetyOption = c.SafetyOption,
                        safetyOptionName = c.SafetyOptionName,
                        motor = c.Motor,
                        motorNote = c.MotorNote,
                        softwareVersions = c.SoftwareVersions,
                        errors = c.Errors,
                    }),
                },
                detailErrors = s.Details?.Errors,
                coe = s.Coe is null ? null : new
                {
                    status = s.Coe.Status,
                    deviceName = s.Coe.DeviceName,
                    hardwareVersion = s.Coe.HardwareVersion,
                    firmwareVersion = s.Coe.FirmwareVersion,
                    serialNo = s.Coe.SerialNo,
                    adsError = s.Coe.AdsError,
                },
                missing = s.Missing,
                extraLink = s.ExtraLink,
                crc = s.Crc,
                adsError = s.AdsError,
                source = new
                {
                    netId = s.NetId,
                    port = s.Port,
                    indexGroup = s.IndexGroup is null ? null : $"0x{s.IndexGroup:X}",
                    indexOffset = s.IndexOffset is null ? null : $"0x{s.IndexOffset:X}",
                },
            }),
            safetyIdentityComplete = m.SafetyIdentityComplete,
            warnings = m.Warnings,
        }),
    }, JsonOpts);
}
