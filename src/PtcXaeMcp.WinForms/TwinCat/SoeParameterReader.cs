using System.Buffers.Binary;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>The drive (or the route to it) did not answer the first read: not a per-IDN problem.</summary>
internal sealed class SoeTargetUnreachableException(string message) : InvalidOperationException(message);

/// <summary>
/// SoE attribute element (0x04) of an IDN, decoded with the SERCOS attribute layout: conversion factor bits 0-15, data
/// length bits 16-18, function bit 19 (1 = command), display format bits 20-22, decimal places bits 24-27, write
/// protection bits 28-30 (CP2, CP3, CP4). The layout comes from the SERCOS specification, not from Infosys, and has not
/// been checked against a drive yet; <see cref="Raw"/> is always the authoritative value.
/// </summary>
internal sealed record SoeAttribute(
    uint Raw, int ConversionFactor, string DataLength, bool IsList, int? ElementBytes, bool IsCommand,
    string Display, int DecimalPlaces, IReadOnlyList<string> WriteProtectedIn)
{
    public string RawHex => $"0x{Raw:X8}";
}

internal static class SoeAttributeDecoder
{
    private static readonly string[] DisplayNames =
        ["binary", "unsignedDecimal", "signedDecimal", "hex", "text", "idn", "float", "reserved"];

    public static SoeAttribute Decode(uint raw)
    {
        int length = (int)((raw >> 16) & 0x7);
        var protection = new List<string>();
        for (int bit = 0; bit < 3; bit++)
            if ((raw & (1u << (28 + bit))) != 0) protection.Add($"CP{bit + 2}");

        (string dataLength, bool isList, int? elementBytes) = length switch
        {
            1 => ("2 bytes", false, (int?)null),
            2 => ("4 bytes", false, null),
            3 => ("8 bytes", false, null),
            4 => ("list of 1 byte elements", true, 1),
            5 => ("list of 2 byte elements", true, 2),
            6 => ("list of 4 byte elements", true, 4),
            7 => ("list of 8 byte elements", true, 8),
            _ => ("reserved", false, null),
        };
        return new SoeAttribute(raw, (int)(raw & 0xFFFF), dataLength, isList, elementBytes, (raw & 0x80000u) != 0,
            DisplayNames[(raw >> 20) & 0x7], (int)((raw >> 24) & 0xF), protection);
    }
}

/// <summary>One operation data element (value, min, max, default) of an IDN, raw and, where known, interpreted.</summary>
internal sealed record SoeData(
    string Hex, int Length, ulong? Unsigned, long? Signed, decimal? Scaled, SoeListHeader? List,
    IReadOnlyList<string>? IdnList, string? Text);

internal static class SoeDataDecoder
{
    /// <summary>
    /// Without an attribute the data stay raw: hex, plus unsigned for 2/4/8 bytes, plus the list header and the IDN list
    /// of S-0-0016/S-0-0024. With an attribute the display format applies: signed value, scaling by the decimal places,
    /// float, text, IDN list.
    /// </summary>
    public static SoeData Decode(byte[] data, int idnRaw, SoeAttribute? attribute)
    {
        string hex = Convert.ToHexString(data);
        bool scalarLength = data.Length is 2 or 4 or 8;
        bool scalar = scalarLength && attribute is not { IsList: true };
        ulong? unsigned = scalar ? ToUnsigned(data) : null;

        var list = attribute is { IsList: false } ? null : EtherCatStartupParser.ListHeader(data);
        if (attribute is { IsList: true } && list is null && data.Length >= 4)
        {
            // A list that does not fit its header is still a list: report what the header says.
            list = new SoeListHeader(BinaryPrimitives.ReadUInt16LittleEndian(data), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)));
        }

        long? signed = null;
        decimal? scaled = null;
        if (attribute is not null && scalar)
        {
            switch (attribute.Display)
            {
                case "signedDecimal":
                    signed = ToSigned(data);
                    scaled = Scale(signed.Value, attribute.DecimalPlaces);
                    break;
                case "unsignedDecimal":
                    scaled = Scale((decimal)unsigned!.Value, attribute.DecimalPlaces);
                    break;
                case "float" when data.Length == 4:
                    scaled = FloatToDecimal(BitConverter.ToSingle(data));
                    break;
                case "float" when data.Length == 8:
                    scaled = FloatToDecimal(BitConverter.ToDouble(data));
                    break;
            }
        }

        bool idnList = attribute is null ? idnRaw is 16 or 24 : attribute.Display == "idn" && attribute.IsList;
        var idns = idnList && list is not null ? EtherCatStartupParser.IdnList(data, list) : null;

        string? text = null;
        if (attribute is { Display: "text" })
        {
            byte[] chars = list is not null && EtherCatDeviceParsing.TryParseSoeList(data, out var payload) ? payload : data;
            text = EtherCatDeviceParsing.Clean(chars);
        }

        return new SoeData(hex, data.Length, unsigned, signed, scaled, list, idns, text);
    }

    private static ulong? ToUnsigned(byte[] data) => data.Length switch
    {
        2 => BinaryPrimitives.ReadUInt16LittleEndian(data),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
        8 => BinaryPrimitives.ReadUInt64LittleEndian(data),
        _ => null,
    };

    private static long ToSigned(byte[] data) => data.Length switch
    {
        2 => BinaryPrimitives.ReadInt16LittleEndian(data),
        4 => BinaryPrimitives.ReadInt32LittleEndian(data),
        _ => BinaryPrimitives.ReadInt64LittleEndian(data),
    };

    private static decimal? Scale(decimal value, int decimalPlaces)
    {
        decimal result = value;
        for (int i = 0; i < decimalPlaces; i++) result /= 10m;
        return result;
    }

    private static decimal? FloatToDecimal(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 7.9e27 ? (decimal)value : null;
}

internal sealed record SoeReadError(string Element, uint Code, string Name);

/// <summary>
/// One IDN of a drive: what was read (<see cref="Value"/> and the other requested elements) and, when it came from a
/// Startup-list comparison, the projected data and whether the drive holds the same.
/// </summary>
internal sealed record SoeParameterRecord(
    string Idn, int IdnRaw, int DriveNo,
    string? Name = null, string? Unit = null, SoeAttribute? Attribute = null, SoeData? Value = null,
    SoeData? Min = null, SoeData? Max = null, SoeData? Default = null, int? DataStatus = null,
    IReadOnlyList<SoeReadError>? Errors = null,
    bool? Fixed = null, string? Comment = null, string? Projected = null, string? Actual = null, bool? Match = null,
    string? Note = null);

internal sealed record SoeParameterReadResult(
    string TreePath, string NetId, int Port, string Mode, IReadOnlyList<SoeParameterRecord> Parameters);

internal static class SoeParameterMode
{
    public const string Read = "read";
    public const string Compare = "compare";
}

/// <summary>
/// Reads SoE parameters (IDNs) live from a drive over ADS (index group 0xF420, Infosys "EtherCAT ADS interface") and
/// compares them with the Startup list of the project. Read-only uploads. One ADS read per IDN and element; the
/// first read decides whether the drive is reachable at all, every later failure belongs to its IDN.
/// </summary>
internal sealed class SoeParameterReader(IAdsRawReader reader, string target)
{
    internal const int MaxReadLength = 512;
    private bool _answered;

    /// <summary>ADS errors that say the target or the route is not there (port not found, machine not found, timeout, client port).</summary>
    private static bool IsUnreachable(AdsRawReadResult r) => !r.Ok && r.AdsError is 6 or 7 or 1861 or 1864 or 1865;

    private AdsRawReadResult Fetch(int driveNo, int idnRaw, int element)
    {
        var result = reader.Read(EtherCatDeviceDetailReader.SoeGroup, EtherCatDeviceDetailReader.SoeOffset(driveNo, idnRaw, element), MaxReadLength);
        if (!_answered)
        {
            if (IsUnreachable(result))
                throw new SoeTargetUnreachableException(
                    $"The SoE target {target} did not answer: {result.ErrorText}. " +
                    "Is the configuration active on the target (not in config mode), is there a route to it, and is the slave at least in PREOP?");
            _answered = true;
        }
        return result;
    }

    public IReadOnlyList<SoeParameterRecord> ReadAll(IEnumerable<int> idns, int driveNo, IReadOnlyList<int> elements) =>
        idns.Select(idn => Read(driveNo, idn, elements)).ToList();

    /// <summary>The requested elements of one IDN; the attribute is read first so that it can interpret the others.</summary>
    public SoeParameterRecord Read(int driveNo, int idnRaw, IReadOnlyList<int> elements)
    {
        var errors = new List<SoeReadError>();
        SoeAttribute? attribute = null;
        string? name = null, unit = null;
        SoeData? value = null, min = null, max = null, def = null;
        int? status = null;

        byte[]? Get(int element)
        {
            var r = Fetch(driveNo, idnRaw, element);
            if (r.Ok) return r.Data;
            errors.Add(new SoeReadError(SoeElement.Name(element), r.AdsError, r.AdsErrorName));
            return null;
        }

        if (elements.Contains(SoeElement.Attribute) && Get(SoeElement.Attribute) is { } attr)
        {
            if (attr.Length >= 4) attribute = SoeAttributeDecoder.Decode(BinaryPrimitives.ReadUInt32LittleEndian(attr));
            else errors.Add(new SoeReadError("attribute", 0, $"unexpected size ({attr.Length} bytes)"));
        }

        foreach (int element in elements.Where(e => e != SoeElement.Attribute))
        {
            var data = Get(element);
            if (data is null) continue;
            switch (element)
            {
                case SoeElement.DataStatus:
                    status = data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : null;
                    break;
                case SoeElement.ElementName:
                    name = ListText(data);
                    break;
                case SoeElement.Unit:
                    unit = ListText(data);
                    break;
                case SoeElement.Value: value = SoeDataDecoder.Decode(data, idnRaw, attribute); break;
                case SoeElement.Min: min = SoeDataDecoder.Decode(data, idnRaw, attribute); break;
                case SoeElement.Max: max = SoeDataDecoder.Decode(data, idnRaw, attribute); break;
                case SoeElement.Default: def = SoeDataDecoder.Decode(data, idnRaw, attribute); break;
            }
        }

        return new SoeParameterRecord(SoeIdn.Format(idnRaw), idnRaw, driveNo, name, unit, attribute, value, min, max, def,
            status, errors.Count == 0 ? null : errors);
    }

    // Name and unit are SoE lists of characters.
    private static string? ListText(byte[] data)
    {
        byte[] chars = EtherCatDeviceParsing.TryParseSoeList(data, out var payload) ? payload : data;
        string text = EtherCatDeviceParsing.Clean(chars);
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Every SoE entry of a Startup list against the drive: projected = the entry's data, actual = what the drive holds
    /// for that DriveNo and IDN. Lists are compared by their data without the maximum length. Only entries that write the
    /// value element (0x40) are compared; the others are listed with a note. A (DriveNo, IDN) is read once.
    /// </summary>
    public IReadOnlyList<SoeParameterRecord> Compare(IEnumerable<EtherCatStartupEntry> entries, int? driveNoFilter)
    {
        var records = new List<SoeParameterRecord>();
        var reads = new Dictionary<(int DriveNo, int Idn), AdsRawReadResult>();

        foreach (var entry in entries)
        {
            if (entry.Protocol != EtherCatStartupProtocol.SoE || entry.IdnRaw is not { } idn) continue;
            int driveNo = entry.DriveNo ?? 0;
            if (driveNoFilter is { } only && only != driveNo) continue;

            SoeParameterRecord Skeleton(string? note, bool? match = null, string? actual = null, SoeData? value = null,
                IReadOnlyList<SoeReadError>? errors = null) =>
                new(entry.Idn!, idn, driveNo, Value: value, Errors: errors, Fixed: entry.Fixed, Comment: entry.Comment,
                    Projected: entry.DataHex, Actual: actual, Match: match, Note: note);

            if (entry.Elements != SoeElement.Name(SoeElement.Value))
            {
                records.Add(Skeleton($"element '{entry.Elements ?? "none"}' is not compared, only 'value'"));
                continue;
            }

            if (!reads.TryGetValue((driveNo, idn), out var read))
                reads[(driveNo, idn)] = read = Fetch(driveNo, idn, SoeElement.Value);

            if (!read.Ok)
            {
                records.Add(Skeleton(null, errors: [new SoeReadError("value", read.AdsError, read.AdsErrorName)]));
                continue;
            }

            var projected = EtherCatStartupParser.Bytes(entry.DataHex);
            // Only data with a payload count as a list: a 4 byte value such as 00 00 0A 00 looks like an empty list with a
            // maximum length and must not match just because the (empty) payloads are equal.
            bool? match = projected is null ? null : Equal(projected, read.Data, entry.List is not null && projected.Length > 4);
            records.Add(Skeleton(projected is null ? "the projected data are not valid hex" : null, match,
                Convert.ToHexString(read.Data), SoeDataDecoder.Decode(read.Data, idn, null)));
        }
        return records;
    }

    private static bool Equal(byte[] projected, byte[] actual, bool list)
    {
        if (!list) return projected.AsSpan().SequenceEqual(actual);
        return EtherCatDeviceParsing.TryParseSoeList(projected, out var p)
               && EtherCatDeviceParsing.TryParseSoeList(actual, out var a)
               && p.AsSpan().SequenceEqual(a);
    }
}

/// <summary>"S-0-0015" for a record list, used in call logs and messages.</summary>
internal static class SoeParameterText
{
    public static string Idns(IReadOnlyList<int>? idns) =>
        idns is null ? "all" : string.Join('|', idns.Select(SoeIdn.Format));

    public static string Elements(IReadOnlyList<int> elements) =>
        string.Join('|', elements.Select(SoeElement.Name));
}
