using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// ---- signal testbench: conditions ----------------------------------------------------------
// The conditions twincat_ads_wait_until and twincat_ads_expect evaluate against live values. Everything here is plain
// data and pure logic, free of any ADS type, so it is unit-tested without a PLC.

/// <summary>The hard limits of the signal testbench tools. They are fixed in code, never raised by an argument.</summary>
internal static class SignalLimits
{
    /// <summary>Distinct symbols one wait_until / expect call watches.</summary>
    public const int MaxWatchSymbols = 8;

    /// <summary>Conditions one wait_until / expect call evaluates.</summary>
    public const int MaxConditions = 8;

    public const int DefaultWaitTimeoutMs = 10_000;
    public const int MaxWaitTimeoutMs = 300_000;

    /// <summary>Symbols one record call captures.</summary>
    public const int MaxRecordSymbols = 32;

    public const int DefaultRecordDurationMs = 5_000;
    public const int MaxRecordDurationMs = 120_000;

    /// <summary>Notification cycle in ms: the lowest the tools accept and the highest (a slower cycle only loses edges).</summary>
    public const int MinCycleMs = 1;
    public const int MaxCycleMs = 1_000;

    /// <summary>Value changes one record call keeps; the run stops and says so when it is reached.</summary>
    public const int MaxRecordEvents = 200_000;

    /// <summary>Explicit symbols of one snapshot_compare call, and leaves it reads from a baseline file.</summary>
    public const int MaxSnapshotSymbols = 256;
    public const int MaxSnapshotLeaves = 5_000;

    /// <summary>Entries per difference list in a snapshot_compare answer.</summary>
    public const int SnapshotReportCap = 200;
}

internal enum SignalOp { Eq, Ne, Lt, Le, Gt, Ge, Changed }

/// <summary>One condition: <c>&lt;symbol&gt; &lt;op&gt; &lt;operand&gt;</c> or <c>&lt;symbol&gt; changed</c>.</summary>
internal sealed record SignalCondition(string Text, string Symbol, SignalOp Op, string Operand)
{
    public string OpText => Op switch
    {
        SignalOp.Eq => "==", SignalOp.Ne => "!=", SignalOp.Lt => "<", SignalOp.Le => "<=",
        SignalOp.Gt => ">", SignalOp.Ge => ">=", _ => "changed",
    };
}

/// <summary>A value as the PLC holds it: the JSON-native value, plus the member name for an ENUM.</summary>
internal readonly record struct SignalReading(JsonElement Value, string? EnumName);

/// <summary>A symbol being watched: its type and the value it had when the watch opened.</summary>
/// <param name="PlcType">Normalised PLC type name (an ENUM's own name).</param>
/// <param name="WireType">The primitive the bytes decode as (the base type of an ENUM).</param>
internal sealed record WatchedSymbol(string Symbol, string PlcType, string WireType, bool IsEnum, SignalReading Initial)
{
    public bool IsBool => !IsEnum && WireType is "BOOL" or "BIT";
    public bool IsText => !IsEnum && SnapshotValues.IsStringType(WireType);
    public bool IsNumber => !IsBool && !IsText;
}

internal static class SignalConditionParser
{
    private static readonly (string Text, SignalOp Op)[] Operators =
    [
        ("==", SignalOp.Eq), ("!=", SignalOp.Ne), ("<=", SignalOp.Le), (">=", SignalOp.Ge),
        ("<", SignalOp.Lt), (">", SignalOp.Gt),
    ];

    public const string Grammar =
        "'<symbol> <op> <value>' with op == != < <= > >=, or '<symbol> changed' (e.g. 'MAIN.nCount > 5', 'MAIN.bDone == true')";

    public static bool TryParse(string? text, out SignalCondition condition, out string error)
    {
        condition = null!;
        error = string.Empty;
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) { error = $"A condition is empty. Expected {Grammar}."; return false; }

        int i = 0;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not ('=' or '!' or '<' or '>')) i++;
        string symbol = t[..i];
        if (symbol.Length == 0) { error = $"Condition '{t}' does not start with a symbol path. Expected {Grammar}."; return false; }

        string rest = t[i..].TrimStart();
        if (rest.StartsWith("changed", StringComparison.OrdinalIgnoreCase))
        {
            if (rest.Length != "changed".Length) { error = $"Condition '{t}': 'changed' takes no value."; return false; }
            condition = new SignalCondition(t, symbol, SignalOp.Changed, string.Empty);
            return true;
        }

        foreach (var (opText, op) in Operators)
        {
            if (!rest.StartsWith(opText, StringComparison.Ordinal)) continue;
            string raw = rest[opText.Length..].Trim();
            bool quoted = raw.Length >= 2 && raw[0] is '"' or '\'' && raw[^1] == raw[0];
            string operand = quoted ? raw[1..^1] : raw;
            if (operand.Length == 0 && !quoted) { error = $"Condition '{t}' has no value after '{opText}'."; return false; }
            condition = new SignalCondition(t, symbol, op, operand);
            return true;
        }

        error = $"Condition '{t}' has no operator. Expected {Grammar}.";
        return false;
    }

    /// <summary>Parses a list; the first bad entry ends it with its message.</summary>
    public static bool TryParseAll(IReadOnlyList<string>? texts, out List<SignalCondition> conditions, out string error)
    {
        conditions = [];
        error = string.Empty;
        if (texts is null || texts.Count == 0) { error = "conditions is required (at least one)."; return false; }
        if (texts.Count > SignalLimits.MaxConditions)
        {
            error = $"At most {SignalLimits.MaxConditions} conditions per call (got {texts.Count}).";
            return false;
        }
        foreach (string text in texts)
        {
            if (!TryParse(text, out var c, out error)) return false;
            conditions.Add(c);
        }
        return true;
    }
}

/// <summary>How one condition applies to a symbol of a given type, and whether it holds for a value.</summary>
internal static class SignalCompare
{
    /// <summary>Null when <paramref name="c"/> can be evaluated on <paramref name="s"/>; otherwise why not. Checked before waiting.</summary>
    public static string? Validate(SignalCondition c, WatchedSymbol s)
    {
        if (c.Op == SignalOp.Changed) return null;
        bool ordered = c.Op is SignalOp.Lt or SignalOp.Le or SignalOp.Gt or SignalOp.Ge;

        if (s.IsEnum)
        {
            if (ordered && !TryInteger(c.Operand, out _))
                return $"'{c.Text}': an ENUM compares with == / != by member name or number, and with < > by number.";
            return null;
        }
        if (s.IsBool)
        {
            if (ordered) return $"'{c.Text}': {s.PlcType} supports only == and != (and changed).";
            return AdsBoolValue.TryParse(c.Operand, out _)
                ? null
                : $"'{c.Text}': '{c.Operand}' is not a BOOL value ({AdsBoolValue.Expected}).";
        }
        if (s.IsText)
            return ordered ? $"'{c.Text}': {s.PlcType} supports only == and != (and changed)." : null;
        return TryDecimal(c.Operand, out _)
            ? null
            : $"'{c.Text}': '{c.Operand}' is not a number (time types in their raw unit, e.g. milliseconds for TIME).";
    }

    /// <summary>Whether <paramref name="c"/> holds for <paramref name="value"/>. <c>changed</c> is decided by the caller.</summary>
    public static bool Test(SignalCondition c, WatchedSymbol s, SignalReading value)
    {
        if (s.IsEnum) return TestEnum(c, value);
        if (s.IsBool)
        {
            bool actual = value.Value.ValueKind == JsonValueKind.True;
            if (!AdsBoolValue.TryParse(c.Operand, out bool wanted)) return false;
            return c.Op == SignalOp.Eq ? actual == wanted : actual != wanted;
        }
        if (s.IsText)
        {
            bool same = value.Value.ValueKind == JsonValueKind.String
                        && string.Equals(value.Value.GetString(), c.Operand, StringComparison.Ordinal);
            return c.Op == SignalOp.Eq ? same : !same;
        }
        if (!TryDecimal(c.Operand, out decimal operand)) return false;
        int? cmp = CompareNumber(value.Value, s.WireType, operand);
        if (cmp is null) return c.Op == SignalOp.Ne; // NaN / Infinity: equal to nothing
        return Holds(c.Op, cmp.Value);
    }

    private static bool TestEnum(SignalCondition c, SignalReading value)
    {
        if (TryInteger(c.Operand, out decimal wantedNumber))
        {
            int? cmp = value.Value.ValueKind == JsonValueKind.Number && TryJsonDecimal(value.Value, out decimal have)
                ? have.CompareTo(wantedNumber)
                : null;
            return cmp is null ? c.Op == SignalOp.Ne : Holds(c.Op, cmp.Value);
        }
        bool same = value.EnumName is not null && string.Equals(value.EnumName, c.Operand, StringComparison.OrdinalIgnoreCase);
        return c.Op == SignalOp.Eq ? same : c.Op == SignalOp.Ne && !same;
    }

    private static bool Holds(SignalOp op, int cmp) => op switch
    {
        SignalOp.Eq => cmp == 0,
        SignalOp.Ne => cmp != 0,
        SignalOp.Lt => cmp < 0,
        SignalOp.Le => cmp <= 0,
        SignalOp.Gt => cmp > 0,
        SignalOp.Ge => cmp >= 0,
        _ => false,
    };

    // REAL values are compared as REAL: 0.1 typed in a condition equals the REAL 0.1, not the double 0.10000000149.
    private static int? CompareNumber(JsonElement v, string wireType, decimal operand)
    {
        if (v.ValueKind != JsonValueKind.Number) return null;
        if (wireType == "REAL") return ((float)v.GetDouble()).CompareTo((float)operand);
        if (wireType == "LREAL") return v.GetDouble().CompareTo((double)operand);
        return TryJsonDecimal(v, out decimal have) ? have.CompareTo(operand) : null;
    }

    internal static bool TryJsonDecimal(JsonElement e, out decimal d)
    {
        d = 0;
        if (e.ValueKind != JsonValueKind.Number) return false;
        if (e.TryGetInt64(out long l)) { d = l; return true; }
        if (e.TryGetUInt64(out ulong u)) { d = u; return true; }
        return e.TryGetDecimal(out d);
    }

    private static bool TryDecimal(string text, out decimal d) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    private static bool TryInteger(string text, out decimal d) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d);
}
