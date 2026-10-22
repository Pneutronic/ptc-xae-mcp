using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// persistent-snapshot: raw ADS bytes ↔ leaf JSON value. The sum commands move raw bytes, so decoding
/// here is independent of the SDK's marshalling. Free of ADS types (unit-testable without a PLC).
/// All integers are little-endian (the PLC's byte order).
/// </summary>
internal static class SnapshotLeafCodec
{
    /// <summary>Byte size of a fixed-size primitive, 0 for STRING / WSTRING (size comes from the symbol).</summary>
    public static int FixedSize(string type) => type switch
    {
        "BOOL" or "BIT" or "BYTE" or "USINT" or "SINT" => 1,
        "WORD" or "UINT" or "INT" => 2,
        "DWORD" or "UDINT" or "DINT" or "REAL" or "TIME" or "DATE" or "TOD" or "TIME_OF_DAY" or "DT" or "DATE_AND_TIME" => 4,
        "LWORD" or "ULINT" or "LINT" or "LREAL" or "LTIME" or "LDT" or "LDATE" or "LTOD" => 8,
        _ => 0,
    };

    public static bool IsSupported(string type) =>
        FixedSize(type) > 0 || SnapshotValues.IsStringType(type);

    public static JsonElement Decode(string type, ReadOnlySpan<byte> raw)
    {
        object value = type switch
        {
            "BOOL" or "BIT" => raw[0] != 0,
            "BYTE" or "USINT" => (ulong)raw[0],
            "SINT" => (long)(sbyte)raw[0],
            "WORD" or "UINT" => (ulong)BinaryPrimitives.ReadUInt16LittleEndian(raw),
            "INT" => (long)BinaryPrimitives.ReadInt16LittleEndian(raw),
            "DWORD" or "UDINT" or "TIME" or "DATE" or "TOD" or "TIME_OF_DAY" or "DT" or "DATE_AND_TIME"
                => (ulong)BinaryPrimitives.ReadUInt32LittleEndian(raw),
            "DINT" => (long)BinaryPrimitives.ReadInt32LittleEndian(raw),
            "LWORD" or "ULINT" or "LTIME" or "LDT" or "LDATE" or "LTOD" => BinaryPrimitives.ReadUInt64LittleEndian(raw),
            "LINT" => BinaryPrimitives.ReadInt64LittleEndian(raw),
            "REAL" => Finite(BinaryPrimitives.ReadSingleLittleEndian(raw)),
            "LREAL" => Finite(BinaryPrimitives.ReadDoubleLittleEndian(raw)),
            "STRING" => PlcStringCodec.Decode(raw.ToArray(), 0, raw.Length),
            "WSTRING" => DecodeWide(raw),
            _ => throw new NotSupportedException($"type '{type}' is not supported"),
        };
        return JsonSerializer.SerializeToElement(value);
    }

    private static object Finite(double d) =>
        double.IsFinite(d) ? d : d.ToString(CultureInfo.InvariantCulture); // "NaN" / "Infinity" / "-Infinity"

    private static string DecodeWide(ReadOnlySpan<byte> raw)
    {
        int end = 0;
        while (end + 1 < raw.Length && (raw[end] != 0 || raw[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(raw[..end]);
    }

    /// <summary>Encodes <paramref name="value"/> for the leaf. <paramref name="size"/> is the symbol size
    /// (needed for strings). Throws <see cref="FormatException"/> for a value the type cannot take.</summary>
    public static byte[] Encode(string type, JsonElement value, int size)
    {
        int fixedSize = FixedSize(type);
        if (fixedSize > 0)
        {
            var buf = new byte[fixedSize];
            switch (type)
            {
                case "BOOL" or "BIT":
                    buf[0] = value.ValueKind switch
                    {
                        JsonValueKind.True => 1,
                        JsonValueKind.False => 0,
                        _ => throw new FormatException("BOOL needs true/false"),
                    };
                    break;
                case "REAL":
                    BinaryPrimitives.WriteSingleLittleEndian(buf, (float)ReadDouble(value));
                    break;
                case "LREAL":
                    BinaryPrimitives.WriteDoubleLittleEndian(buf, ReadDouble(value));
                    break;
                default:
                    WriteInteger(buf, type, value);
                    break;
            }
            return buf;
        }

        if (type == "STRING")
        {
            if (value.ValueKind != JsonValueKind.String) throw new FormatException("STRING needs a text value");
            try { return PlcStringCodec.Encode(value.GetString(), size); }
            catch (InvalidOperationException ex) { throw new FormatException(ex.Message); }
        }
        if (type == "WSTRING")
        {
            if (value.ValueKind != JsonValueKind.String) throw new FormatException("WSTRING needs a text value");
            byte[] text = Encoding.Unicode.GetBytes(value.GetString() ?? "");
            if (text.Length + 2 > size) throw new FormatException($"text does not fit WSTRING of {size / 2 - 1} characters");
            var buf = new byte[size];
            text.CopyTo(buf, 0);
            return buf;
        }
        throw new FormatException($"type '{type}' is not supported");
    }

    private static double ReadDouble(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString() switch
            {
                "NaN" => double.NaN,
                "Infinity" => double.PositiveInfinity,
                "-Infinity" => double.NegativeInfinity,
                _ => throw new FormatException("not a number"),
            };
        }
        throw new FormatException("not a number");
    }

    private static void WriteInteger(byte[] buf, string type, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Number) throw new FormatException($"{type} needs a number");
        bool signed = type is "SINT" or "INT" or "DINT" or "LINT";
        try
        {
            if (signed)
            {
                long l = v.GetInt64();
                switch (buf.Length)
                {
                    case 1: buf[0] = (byte)checked((sbyte)l); break;
                    case 2: BinaryPrimitives.WriteInt16LittleEndian(buf, checked((short)l)); break;
                    case 4: BinaryPrimitives.WriteInt32LittleEndian(buf, checked((int)l)); break;
                    default: BinaryPrimitives.WriteInt64LittleEndian(buf, l); break;
                }
            }
            else
            {
                ulong u = v.GetUInt64();
                switch (buf.Length)
                {
                    case 1: buf[0] = checked((byte)u); break;
                    case 2: BinaryPrimitives.WriteUInt16LittleEndian(buf, checked((ushort)u)); break;
                    case 4: BinaryPrimitives.WriteUInt32LittleEndian(buf, checked((uint)u)); break;
                    default: BinaryPrimitives.WriteUInt64LittleEndian(buf, u); break;
                }
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidOperationException)
        {
            throw new FormatException($"value {v.GetRawText()} does not fit {type}");
        }
    }

    /// <summary>Human-readable form of a time/date raw value (informational, never read back).</summary>
    public static string? Display(string type, JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetUInt64(out ulong v)) return null;
        switch (type)
        {
            case "TIME":
                return "T#" + FormatDuration(TimeSpan.FromMilliseconds(v));
            case "LTIME":
                return "LTIME#" + FormatDuration(TimeSpan.FromTicks((long)Math.Min(v / 100, long.MaxValue)));
            case "DATE" or "DT" or "DATE_AND_TIME":
                return (type == "DATE" ? "D#" : "DT#") + DateTimeOffset.FromUnixTimeSeconds((long)Math.Min(v, 253402300799UL))
                    .UtcDateTime.ToString(type == "DATE" ? "yyyy-MM-dd" : "yyyy-MM-dd-HH:mm:ss", CultureInfo.InvariantCulture);
            case "TOD" or "TIME_OF_DAY":
                return "TOD#" + FormatDuration(TimeSpan.FromMilliseconds(v));
            default:
                return null;
        }
    }

    private static string FormatDuration(TimeSpan t)
    {
        var sb = new StringBuilder();
        if (t.Days > 0) sb.Append(t.Days).Append('d');
        if (t.Hours > 0) sb.Append(t.Hours).Append('h');
        if (t.Minutes > 0) sb.Append(t.Minutes).Append('m');
        if (t.Seconds > 0) sb.Append(t.Seconds).Append('s');
        if (t.Milliseconds > 0) sb.Append(t.Milliseconds).Append("ms");
        return sb.Length == 0 ? "0ms" : sb.ToString();
    }
}
