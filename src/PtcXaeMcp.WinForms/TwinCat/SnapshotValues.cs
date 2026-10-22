using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// persistent-snapshot: value rules that decide compare and restore — equality of two leaf
/// values and the coercion of a snapshot value to the type the PLC has now. Pure; no ADS types.
/// </summary>
internal static class SnapshotValues
{
    private static readonly Dictionary<string, (decimal Min, decimal Max)> IntRanges = new(StringComparer.Ordinal)
    {
        ["SINT"] = (sbyte.MinValue, sbyte.MaxValue),
        ["INT"] = (short.MinValue, short.MaxValue),
        ["DINT"] = (int.MinValue, int.MaxValue),
        ["LINT"] = (long.MinValue, long.MaxValue),
        ["USINT"] = (0, byte.MaxValue),
        ["BYTE"] = (0, byte.MaxValue),
        ["UINT"] = (0, ushort.MaxValue),
        ["WORD"] = (0, ushort.MaxValue),
        ["UDINT"] = (0, uint.MaxValue),
        ["DWORD"] = (0, uint.MaxValue),
        ["ULINT"] = (0, ulong.MaxValue),
        ["LWORD"] = (0, ulong.MaxValue),
    };

    private static readonly HashSet<string> TimeTypes = new(StringComparer.Ordinal)
    {
        "TIME", "LTIME", "DATE", "DT", "DATE_AND_TIME", "TOD", "TIME_OF_DAY", "LDT", "LDATE", "LTOD",
    };

    public static bool IsIntegerType(string type) => IntRanges.ContainsKey(type);
    public static bool IsFloatType(string type) => type is "REAL" or "LREAL";
    public static bool IsStringType(string type) => type is "STRING" or "WSTRING";
    public static bool IsTimeType(string type) => TimeTypes.Contains(type);

    /// <summary>True for a type the snapshot can carry (primitives, strings, time types);
    /// an ENUM is recognised by the caller (it knows the symbol's category).</summary>
    public static bool IsSupportedPrimitive(string type) =>
        type == "BOOL" || IsIntegerType(type) || IsFloatType(type) || IsStringType(type) || IsTimeType(type);

    /// <summary>Exact comparison of two JSON leaf values (numbers by value, never by text).</summary>
    public static bool ValueEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            if (a.TryGetInt64(out long la) && b.TryGetInt64(out long lb)) return la == lb;
            if (a.TryGetUInt64(out ulong ua) && b.TryGetUInt64(out ulong ub)) return ua == ub;
            if (a.TryGetInt64(out _) || b.TryGetInt64(out _) || a.TryGetUInt64(out _) || b.TryGetUInt64(out _))
            {
                // one integer, one fractional: equal only if the double is that exact integer
                return a.GetDouble() == b.GetDouble() && a.GetDouble() % 1 == 0 && Math.Abs(a.GetDouble()) < 9e15;
            }
            return a.GetDouble().Equals(b.GetDouble());
        }
        if (a.ValueKind != b.ValueKind) return false;
        return a.ValueKind switch
        {
            JsonValueKind.String => string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => a.GetRawText() == b.GetRawText(),
        };
    }

    /// <summary>
    /// Brings <paramref name="from"/> to the type the PLC has now. Compatible families only:
    /// integer ↔ integer (in range), REAL ↔ LREAL (exact), STRING ↔ WSTRING, same-name ENUM
    /// and same time type. Anything else is a type mismatch with a reason.
    /// </summary>
    public static bool TryCoerce(SnapshotLeaf from, string toType, bool toIsEnum, out SnapshotLeaf coerced, out string reason)
    {
        coerced = from;
        reason = string.Empty;
        string ft = from.Type;

        if (string.Equals(ft, toType, StringComparison.Ordinal)) return true;

        if (toIsEnum || IsEnumLike(ft, toType))
        {
            reason = $"type changed: snapshot {ft}, PLC {toType}";
            return false;
        }

        if (IsIntegerType(ft) && IsIntegerType(toType))
        {
            if (!TryGetDecimal(from.Value, out decimal d) || d % 1 != 0)
            { reason = $"value is not an integer for {toType}"; return false; }
            var (min, max) = IntRanges[toType];
            if (d < min || d > max)
            { reason = $"value {d.ToString(CultureInfo.InvariantCulture)} out of range for {toType}"; return false; }
            coerced = new SnapshotLeaf(toType, from.Value, from.Name, from.Display);
            return true;
        }

        if (IsFloatType(ft) && IsFloatType(toType))
        {
            if (from.Value.ValueKind != JsonValueKind.Number)
            { coerced = new SnapshotLeaf(toType, from.Value); return true; } // "NaN"/"Infinity" text
            double dv = from.Value.GetDouble();
            if (toType == "REAL" && (double)(float)dv != dv && float.IsFinite((float)dv))
            { reason = "value not exactly representable as REAL"; return false; }
            coerced = new SnapshotLeaf(toType, from.Value);
            return true;
        }

        if (IsStringType(ft) && IsStringType(toType))
        {
            coerced = new SnapshotLeaf(toType, from.Value);
            return true;
        }

        reason = $"type changed: snapshot {ft}, PLC {toType}";
        return false;
    }

    // An ENUM's own type name never equals a primitive; a primitive snapshot value must not
    // silently land in an enum (or vice versa) unless the names match (handled above).
    private static bool IsEnumLike(string fromType, string toType) =>
        !IsSupportedPrimitive(fromType) || !IsSupportedPrimitive(toType);

    private static bool TryGetDecimal(JsonElement e, out decimal d)
    {
        d = 0;
        if (e.ValueKind != JsonValueKind.Number) return false;
        if (e.TryGetInt64(out long l)) { d = l; return true; }
        if (e.TryGetUInt64(out ulong u)) { d = u; return true; }
        if (!e.TryGetDecimal(out d)) return false;
        return true;
    }

    /// <summary>Compact text of a leaf value for reports ("12.5", "true", "\"abc\"").</summary>
    public static string Show(JsonElement v) => v.ValueKind == JsonValueKind.String ? $"\"{v.GetString()}\"" : v.GetRawText();
}
