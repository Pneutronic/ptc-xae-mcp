using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Beckhoff Identification Code (CoE 0x10E2:01), split into its data identifiers.</summary>
internal sealed record EtherCatBic(string Raw, string? OrderNumber, string? Btn, string? Article, string? Batch);

/// <summary>
/// Safety identity of a slave. <see cref="SerialNo"/> is CoE 0xF980:02, which Beckhoff does not document as a serial
/// (<see cref="SerialDocumented"/> is false); the effective identifier follows the precedence in
/// <see cref="EtherCatDeviceDetailReader.EffectiveSafetyIdentifier"/>.
/// </summary>
internal sealed record EtherCatSafety(
    bool IsSafetyDevice, int? FsoeAddress, long? SerialNo, string? SerialSource, bool SerialDocumented,
    string? EffectiveSerial, string? EffectiveSerialSource, bool ViaHostDrive, int? HostDriveAddress,
    string? Detection = null, string? Note = null, string? Role = null, EtherCatSafetyHostDrive? HostDrive = null);

/// <summary>The AX5000 drive that carries an AX5805/AX5806 safety option card (the slave just before it).</summary>
internal sealed record EtherCatSafetyHostDrive(int? Address, string? SerialNo, int? ConfiguredSafetyOption);

/// <summary>
/// P-0-0320 of an AX5000 channel (AX5000 IDN description): firmware, bootloader and option card versions. The option card
/// is a plug-in card of the drive (for example an encoder card), null when none is fitted; it is not the AX5805 safety
/// card.
/// </summary>
internal sealed record SoeSoftwareVersions(
    string? Firmware, int? FirmwareBuild, string? Bootloader, int? BootloaderBuild,
    string? OptionCardFirmware, int? OptionCardFirmwareBuild, int? OptionCardInterface);

/// <summary>Motor name plate and feedback of an OCT servo terminal (CoE 0x9009 / 0x9008).</summary>
internal sealed record EtherCatMotor(string? Vendor, string? ElectricType, string? SerialNo, string? OrderCode, string? Construction);

internal sealed record EtherCatFeedback(string? TypeCode, string? SerialNo, string? Firmware, string? FirmwareDate);

/// <summary>Digital motor name plate of an AX5000 channel (P-0-0098).</summary>
internal sealed record SoeMotor(
    int NameplateType, string? Vendor, int? VendorId, string? OrderCode, string? ElectricType, string? SerialNo, string? FeedbackType);

internal sealed record SoeChannel(
    int DriveNo, string? Firmware, string? Hardware, string? SerialNo, string? SerialRaw, string? ConfiguredType,
    int? SafetyOption, string? SafetyOptionName, SoeMotor? Motor, string? MotorNote, IReadOnlyList<string> Errors,
    SoeSoftwareVersions? SoftwareVersions = null);

internal sealed record EtherCatDeviceDetails(
    EtherCatBic? Bic, EtherCatSafety? Safety, EtherCatMotor? Motor, EtherCatFeedback? Feedback,
    IReadOnlyList<SoeChannel>? Soe, IReadOnlyList<string> Errors);

internal sealed record EtherCatSummary(
    int Slaves, int SafetyDevices, int SafetyWithSerial, int Motors, int MotorsWithSerial,
    int SlavesRead = 0, int SlavesFailed = 0, int SafetyUnknown = 0, int SlavesPartial = 0);

/// <summary>Parsers for the device details of an EtherCAT slave. All pure.</summary>
internal static class EtherCatDeviceParsing
{
    private static readonly string[] BicIdentifiers = ["51S", "30P", "SBTN", "1P", "1K", "2P", "Q"];

    /// <summary>Text up to the first NUL, control characters removed, leading and trailing spaces trimmed.</summary>
    public static string Clean(ReadOnlySpan<byte> data)
    {
        string text = MdpValueDecoder.Text(data);
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    private static string? Value(string s) => s.Length == 0 ? null : s;

    private static bool StartsIdentifier(string s, int pos) =>
        pos < s.Length && BicIdentifiers.Any(id => string.CompareOrdinal(s, pos, id, 0, id.Length) == 0);

    /// <summary>
    /// Splits a BIC such as "1P072222SBTNk4p562d71KEL1809 Q1 51S678294" (Beckhoff documentation example): 1P order number, SBTN the BTN
    /// (8 characters, "SBTN" is not part of it), 1K article, 2P batch; Q, 51S and 30P are recognised and skipped.
    /// </summary>
    public static EtherCatBic ParseBic(string raw)
    {
        string text = raw ?? string.Empty;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        int pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) break;

            string? id = BicIdentifiers.FirstOrDefault(i => string.CompareOrdinal(text, pos, i, 0, i.Length) == 0);
            if (id is null)
            {
                while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
                continue;
            }
            pos += id.Length;

            if (id == "SBTN")
            {
                int take = Math.Min(8, text.Length - pos);
                found.TryAdd(id, text.Substring(pos, take));
                pos += take;
                continue;
            }

            var value = new StringBuilder();
            while (true)
            {
                int end = pos;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                if (id == "1P")
                {
                    int sbtn = text.IndexOf("SBTN", pos, StringComparison.Ordinal);
                    if (sbtn >= 0 && sbtn < end) end = sbtn;
                }
                value.Append(text, pos, end - pos);
                pos = end;
                if (pos < text.Length && string.CompareOrdinal(text, pos, "SBTN", 0, 4) == 0) break;

                int next = pos;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next >= text.Length || StartsIdentifier(text, next)) break;
                value.Append(' ');
                pos = next;
            }
            found.TryAdd(id, value.ToString());
        }

        string? Get(string id) => found.TryGetValue(id, out var v) ? Value(v.Trim()) : null;
        return new EtherCatBic(text.Trim(), Get("1P"), Get("SBTN"), Get("1K"), Get("2P"));
    }

    private static readonly Regex SafetyName = new(
        @"(?<![A-Z0-9])(?:(?:EL|EJ|KL)(?:19|29|69)\d{2}|EP(?:19|29)\d{2}|AX58(?:01|05|06))(?![0-9])",
        RegexOptions.CultureInvariant);

    private static readonly Regex DriveName = new(@"(?<![A-Z0-9])AX5\d{3}", RegexOptions.CultureInvariant);
    private static readonly Regex SafetyDriveOption = new(@"(?<![A-Z0-9])AX58(?:05|06)(?![0-9])", RegexOptions.CultureInvariant);

    private static readonly Regex BlackChannelName = new(@"(?<![A-Z0-9])EL6695", RegexOptions.CultureInvariant);

    /// <summary>EL6695: an EtherCAT bridge that only passes FSoE data through (black channel); it is no safety device.</summary>
    public static bool IsBlackChannelBridge(params string?[] names) => names.Any(n => n is not null && BlackChannelName.IsMatch(n));

    /// <summary>TwinSAFE article pattern, except the EL6695 bridge.</summary>
    public static bool LooksLikeSafetyDevice(params string?[] names) =>
        names.Any(n => n is not null && SafetyName.IsMatch(n) && !BlackChannelName.IsMatch(n));

    /// <summary>"2.16" from 216: a version number with two decimals.</summary>
    public static string TwoDecimals(int value) => string.Create(CultureInfo.InvariantCulture, $"{value / 100}.{value % 100:D2}");

    /// <summary>
    /// P-0-0320 data (after the list header), little endian: UINT16 firmware version @0 (2 decimals), UINT16 firmware
    /// build @2, UINT16 bootloader version @4, UINT16 bootloader build @6, UDINT MainDsp compiler @8, UDINT FeedbackDsp
    /// compiler @12, UINT16 option card firmware version @16 (2 decimals), UINT16 option card firmware build @18, UINT16
    /// option card interface @20. Option card fields that are 0 are null; a short answer yields only what it covers.
    /// </summary>
    public static SoeSoftwareVersions? ParseSoftwareVersions(byte[] data)
    {
        if (data.Length < 8) return null;
        int U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        int? Optional(int offset) => data.Length >= offset + 2 && U16(offset) != 0 ? U16(offset) : null;
        int? card = Optional(16);
        return new SoeSoftwareVersions(TwoDecimals(U16(0)), U16(2), TwoDecimals(U16(4)), U16(6),
            card is null ? null : TwoDecimals(card.Value), Optional(18), Optional(20));
    }

    public static bool LooksLikeDrive(params string?[] names) => names.Any(n => n is not null && DriveName.IsMatch(n));

    /// <summary>AX5805 / AX5806: a safety option card whose traceability is the host drive's serial.</summary>
    public static bool IsDriveSafetyOption(params string?[] names) => names.Any(n => n is not null && SafetyDriveOption.IsMatch(n));

    /// <summary>"Serial#: 000123456" -> "000123456".</summary>
    public static string? SerialFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        int colon = s.IndexOf(':', StringComparison.Ordinal);
        if (s.StartsWith("Serial", StringComparison.OrdinalIgnoreCase) && colon >= 0) s = s[(colon + 1)..].Trim();
        return s.Length == 0 ? null : s;
    }

    public static string? SafetyOptionName(int option) => option switch
    {
        0 => "none", 1 => "AX5801", 2 => "AX5805/AX5806", 3 => "AX5801-0200", _ => null,
    };

    /// <summary>Data of a SoE list: UINT16 actual length, UINT16 maximum length, then the data (actual length bytes).</summary>
    public static bool TryParseSoeList(byte[] raw, out byte[] data)
    {
        data = [];
        if (raw.Length < 4) return false;
        int actual = BinaryPrimitives.ReadUInt16LittleEndian(raw);
        int length = Math.Min(actual, raw.Length - 4);
        data = raw[4..(4 + length)];
        return true;
    }

    private static string Str(byte[] block, int offset, int length)
    {
        if (offset >= block.Length) return string.Empty;
        return Clean(block.AsSpan(offset, Math.Min(length, block.Length - offset)));
    }

    /// <summary>
    /// P-0-0098 data (after the list header): UINT16 name plate type, then the block at byte 4. Type 0 has no motor.
    /// Type 2 (Beckhoff electronic data sheet): vendor STRING(30) @0, order code STRING(30) @30, electric type
    /// STRING(30) @60, serial STRING(10) @90. Type 1 (documented): UDINT id1 @0, UDINT id2 @4, UINT version @8, UINT
    /// vendor @10, UDINT serial @12, STRING(30) motor type @16, STRING(30) feedback type @46.
    /// </summary>
    public static (SoeMotor? Motor, string? Note) ParseNameplate(byte[] data)
    {
        if (data.Length < 2) return (null, "P-0-0098 answer too short");
        int type = BinaryPrimitives.ReadUInt16LittleEndian(data);
        byte[] block = data.Length > 4 ? data[4..] : [];
        switch (type)
        {
            case 0:
                return (null, "no digital motor name plate (type 0)");
            case 2:
                return (new SoeMotor(2, Value(Str(block, 0, 30)), null, Value(Str(block, 30, 30)), Value(Str(block, 60, 30)),
                    Value(Str(block, 90, 10)), null), null);
            case 1:
                {
                    uint serial = block.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12)) : 0;
                    int? vendorId = block.Length >= 12 ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10)) : null;
                    return (new SoeMotor(1, null, vendorId, Value(Str(block, 16, 30)), null,
                        block.Length >= 16 ? serial.ToString(CultureInfo.InvariantCulture) : null, Value(Str(block, 46, 30))), null);
                }
            default:
                return (null, $"unknown digital motor name plate type {type}");
        }
    }
}

/// <summary>
/// Reads the device details of one slave over an open connection to (master NetId, port = fixed address): CoE BIC,
/// safety identity, OCT motor name plate; and for AX5000 drives the SoE parameters. Read-only uploads.
/// </summary>
internal static class EtherCatDeviceDetailReader
{
    internal const uint CoeGroup = 0xF302;
    internal const uint SoeGroup = 0xF420;
    internal const int MaxDriveChannels = 8;

    private static uint CoeOffset(int index, int subIndex) => ((uint)index << 16) | (uint)subIndex;

    private static uint SoeOffset(int driveNo, int idn) => ((uint)driveNo << 24) | (0x40u << 16) | (uint)idn;

    private const int ParameterFlag = 0x8000;

    private static bool IsNotPresent(AdsRawReadResult r) =>
        r.AdsErrorName.Equals("DeviceInvalidOffset", StringComparison.OrdinalIgnoreCase);

    /// <summary>A read that failed because the link or target did not answer (timeout, target not found), not because the object is absent.</summary>
    internal static bool IsTransient(AdsRawReadResult r) => !r.Ok && r.AdsError is 1861 or 7;

    private static readonly Regex TransientText = new(@"\((?:1861|7)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>True for an error text "name (code)" of a transient failure.</summary>
    internal static bool IsTransientText(string? text) => text is not null && TransientText.IsMatch(text.Trim());

    private static string? CoeString(IAdsRawReader r, int index, int sub, List<string> errors, bool recordErrors = true)
    {
        var read = r.Read(CoeGroup, CoeOffset(index, sub), 256);
        if (read.Ok) return EtherCatDeviceParsing.Clean(read.Data);
        if (recordErrors && !IsNotPresent(read)) errors.Add($"0x{index:X4}:{sub:X2}: {read.ErrorText}");
        return null;
    }

    /// <summary>CoE-based details of a slave whose device name was readable.</summary>
    public static EtherCatDeviceDetails ReadCoeDetails(IAdsRawReader coe, string? coeName, string? projectedName)
    {
        var errors = new List<string>();

        EtherCatBic? bic = null;
        string? bicRaw = CoeString(coe, 0x10E2, 1, errors);
        if (!string.IsNullOrEmpty(bicRaw)) bic = EtherCatDeviceParsing.ParseBic(bicRaw);

        EtherCatSafety? safety = null;
        var f1 = coe.Read(CoeGroup, CoeOffset(0xF980, 1), 16);
        var f2 = coe.Read(CoeGroup, CoeOffset(0xF980, 2), 16);
        if (IsTransient(f1)) errors.Add($"0xF980:01: {f1.ErrorText}");
        if (IsTransient(f2)) errors.Add($"0xF980:02: {f2.ErrorText}");
        bool f980Readable = f1.Ok || f2.Ok;
        bool byName = EtherCatDeviceParsing.LooksLikeSafetyDevice(coeName, bic?.Article, projectedName);
        bool bridge = EtherCatDeviceParsing.IsBlackChannelBridge(coeName, bic?.Article, projectedName);
        int? fsoe = f1.Ok && f1.Data.Length == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(f1.Data) : null;
        long? serial = f2.Ok && f2.Data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(f2.Data) : null;
        bool nonZero = fsoe is > 0 || serial is > 0;
        string? serialSource = f2.Ok ? "coe:0xF980:02" : null;
        if (nonZero)
            safety = new EtherCatSafety(true, fsoe, serial, serialSource, false, null, null, false, null, "F980");
        else if (byName)
        {
            // A TwinSAFE article with F980 all zero or unreadable stays a safety device (and is flagged without a serial).
            string? note = f980Readable
                ? "F980 present but FSoE address and serial are 0 — safety function may not be used on this device; verify"
                : null;
            safety = new EtherCatSafety(true, fsoe, serial, serialSource, false, null, null, false, null, "article pattern", note);
        }
        else if (f980Readable || bridge)
            safety = new EtherCatSafety(false, fsoe, serial, serialSource, false, null, null, false, null,
                f980Readable ? "F980" : "article pattern", "passes FSoE data through (black channel); no safety serial required", "blackChannel");

        EtherCatMotor? motor = null;
        EtherCatFeedback? feedback = null;
        string? motorSerial = CoeString(coe, 0x9009, 3, errors);
        if (motorSerial is not null)
        {
            motor = new EtherCatMotor(CoeString(coe, 0x9009, 1, errors), CoeString(coe, 0x9009, 2, errors), motorSerial,
                CoeString(coe, 0x9009, 4, errors), CoeString(coe, 0x9009, 5, errors));
            feedback = new EtherCatFeedback(CoeString(coe, 0x9008, 14, errors), CoeString(coe, 0x9008, 15, errors),
                CoeString(coe, 0x9008, 16, errors), CoeString(coe, 0x9008, 17, errors));
        }

        return new EtherCatDeviceDetails(bic, safety, motor, feedback, null, errors);
    }

    /// <summary>
    /// A plain UINT16 IDN such as P-0-2000 (the drive answers with two bytes, no list header). A list-wrapped answer is
    /// accepted too, taking the first two bytes of its data.
    /// </summary>
    internal static int? SoeUInt16(IAdsRawReader r, int driveNo, int idn, string label, List<string> errors)
    {
        var read = r.Read(SoeGroup, SoeOffset(driveNo, idn), 512);
        if (!read.Ok) { errors.Add($"{label}: {read.ErrorText}"); return null; }
        if (read.Data.Length == 2) return BinaryPrimitives.ReadUInt16LittleEndian(read.Data);
        if (EtherCatDeviceParsing.TryParseSoeList(read.Data, out var data) && data.Length >= 2)
            return BinaryPrimitives.ReadUInt16LittleEndian(data);
        errors.Add($"{label}: unexpected size ({read.Data.Length} bytes)");
        return null;
    }

    private static byte[]? SoeList(IAdsRawReader r, int driveNo, int idn, string label, List<string> errors)
    {
        var read = r.Read(SoeGroup, SoeOffset(driveNo, idn), 512);
        if (!read.Ok) { errors.Add($"{label}: {read.ErrorText}"); return null; }
        if (!EtherCatDeviceParsing.TryParseSoeList(read.Data, out var data)) { errors.Add($"{label}: answer too short"); return null; }
        return data;
    }

    /// <summary>
    /// SoE parameters of an AX5000 drive, one entry per answering channel (drive number 0..7); the first channel whose
    /// S-0-0030 read fails ends the scan. Per-IDN errors are recorded in the channel, not fatal.
    /// </summary>
    public static IReadOnlyList<SoeChannel> ReadSoe(IAdsRawReader soe, List<string>? failures = null)
    {
        var channels = new List<SoeChannel>();
        for (int driveNo = 0; driveNo < MaxDriveChannels; driveNo++)
        {
            var probe = soe.Read(SoeGroup, SoeOffset(driveNo, 30), 512);
            if (!probe.Ok || !EtherCatDeviceParsing.TryParseSoeList(probe.Data, out var firmwareData))
            {
                if (IsTransient(probe)) failures?.Add($"SoE drive {driveNo} S-0-0030: {probe.ErrorText}");
                break;
            }

            var errors = new List<string>();
            string firmware = EtherCatDeviceParsing.Clean(firmwareData);
            byte[]? hw = SoeList(soe, driveNo, 31, "S-0-0031", errors);
            byte[]? serial = SoeList(soe, driveNo, 432, "S-0-0432", errors);
            byte[]? type = SoeList(soe, driveNo, ParameterFlag | 54, "P-0-0054", errors);
            int? safetyOption = SoeUInt16(soe, driveNo, ParameterFlag | 2000, "P-0-2000", errors);
            byte[]? plate = SoeList(soe, driveNo, ParameterFlag | 98, "P-0-0098", errors);
            byte[]? versions = SoeList(soe, driveNo, ParameterFlag | 320, "P-0-0320", errors);
            SoeSoftwareVersions? softwareVersions = versions is null ? null : EtherCatDeviceParsing.ParseSoftwareVersions(versions);
            if (versions is not null && softwareVersions is null) errors.Add("P-0-0320: answer too short");

            string? serialRaw = serial is null ? null : EtherCatDeviceParsing.Clean(serial);
            SoeMotor? motor = null;
            string? note = null;
            if (plate is not null) (motor, note) = EtherCatDeviceParsing.ParseNameplate(plate);

            channels.Add(new SoeChannel(driveNo, Nullable(firmware), hw is null ? null : Nullable(EtherCatDeviceParsing.Clean(hw)),
                EtherCatDeviceParsing.SerialFromText(serialRaw), serialRaw,
                type is null ? null : Nullable(EtherCatDeviceParsing.Clean(type)),
                safetyOption, safetyOption is null ? null : EtherCatDeviceParsing.SafetyOptionName(safetyOption.Value),
                motor, note, errors, softwareVersions));
        }
        return channels;
    }

    private static string? Nullable(string s) => s.Length == 0 ? null : s;

    /// <summary>
    /// The identifier every safety device must end up with, in this order: CoE 0xF980:02 (non-zero), the BTN of the BIC,
    /// the serial of 0x1018:04 (non-zero), and for AX5805/AX5806 the serial of the host drive (S-0-0432). Null when
    /// none exists.
    /// </summary>
    public static (string? Value, string? Source, bool ViaHostDrive, int? HostDriveAddress) EffectiveSafetyIdentifier(
        EtherCatDeviceDetails details, string? identitySerial, string? identitySerialSource, string?[] names,
        EtherCatSlaveRecord? hostDrive)
    {
        var safety = details.Safety;
        if (safety?.SerialNo is { } f980 and not 0)
            return (f980.ToString(CultureInfo.InvariantCulture), "coe:0xF980:02", false, null);
        if (details.Bic?.Btn is { Length: > 0 } btn)
            return (btn, "bic:BTN", false, null);
        if (!string.IsNullOrEmpty(identitySerial) && identitySerial.Any(c => c != '0'))
            return (identitySerial, identitySerialSource switch
            {
                EtherCatSerialSource.Master => "master:0x1018:04",
                EtherCatSerialSource.Coe => "coe:0x1018:04",
                _ => identitySerialSource,
            }, false, null);
        if (EtherCatDeviceParsing.IsDriveSafetyOption(names) && hostDrive?.Details?.Soe is { } channels)
        {
            string? hostSerial = channels.Select(c => c.SerialNo).FirstOrDefault(s => !string.IsNullOrEmpty(s));
            if (hostSerial is not null) return (hostSerial, "hostDrive:S-0-0432", true, hostDrive.Address);
        }
        return (null, null, false, null);
    }
}
