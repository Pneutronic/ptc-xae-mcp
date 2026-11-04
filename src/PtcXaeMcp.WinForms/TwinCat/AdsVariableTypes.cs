namespace PtcXaeMcp.WinForms.TwinCat;

// ---- ads-variable-rw --------------------------------------------------------
// Result shapes for the ADS variable read/write tools.
// Value is a JSON-native type (bool/number/string); raw byte arrays are never
// returned to the MCP caller.

internal sealed record AdsVariableResult(
    bool Success,
    string Symbol,       // original symbolPath
    string? PlcType,     // TwinCAT type name, e.g. "BOOL", "INT", "REAL"
    object? Value,       // typed .NET value (bool/numeric/string) or null on error
    string AmsNetId,     // resolved NetId used for the connection
    int AdsPort,         // ADS port used
    string Message);

// ---- ibn-live: batch read --------------------------------------------------
// twincat_read_ads_variables_batch reads many symbols in one MCP call over a
// single ADS connection. Each item carries its own success/message so a single
// bad symbol never sinks the whole snapshot (used to harvest a whole IO
// checklist + the test-PRG status struct in one shot).

internal sealed record AdsBatchReadItem(
    string Symbol,       // requested symbolPath
    string? PlcType,     // TwinCAT type name, or null if the symbol failed
    object? Value,       // typed .NET value (bool/numeric/string) or null on error
    bool Success,        // per-symbol outcome
    string Message);     // "OK" or "[Error] ..."

internal sealed record AdsBatchReadResult(
    bool Success,        // connection-level outcome (false ⇒ nothing read)
    IReadOnlyList<AdsBatchReadItem> Results,
    string AmsNetId,     // resolved NetId used for the connection
    int AdsPort,         // ADS port used
    string Message);     // "OK" or "[Error] ..."

// ---- typed read by handle ----------------------------------------------------
// A symbol whose PLC type the caller already knows (BOOL, UINT, LREAL, STRING(255), ...). Read by variable handle
// only, so a deep member path into a large FB instance needs no symbol-table search.

internal sealed record AdsTypedRead(string Symbol, string PlcType)
{
    /// <summary>The declared length of a "STRING(n)" type, or null for any other type.</summary>
    public int? StringLength =>
        PlcType.StartsWith("STRING(", StringComparison.OrdinalIgnoreCase) && PlcType.EndsWith(')')
        && int.TryParse(PlcType.AsSpan(7, PlcType.Length - 8), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int n) && n > 0
            ? n
            : null;
}

// ---- diagnostics: fb-instance-inspect --------------------------------------
// twincat_inspect_fb_instance is a READ-ONLY diagnostic: dumps an FB/struct
// instance's MemberInstances (including PROPERTY-flagged entries) plus any
// RPC-callable methods (__getX/__setX -- only present when the compiler
// marked the accessor RpcEnable="plc", a per-property compiled flag), so a
// caller can determine empirically whether a given PROPERTY surfaces as an
// addressable symbol, via RPC getter/setter, or neither (many properties are
// reachable through none of these and only via the XAE Automation-Interface
// channel), instead of guessing.

internal sealed record FbMemberEntry(
    string Name,          // InstanceName
    string InstancePath,  // absolute path, e.g. "MAIN.fbMachine.State"
    bool IsProperty,      // IInstance.IsProperty
    string TypeName,      // declared PLC type name
    long Size,             // IBitSize.Size (bytes, or bits if IsBitType)
    int? Offset);          // IMember.Offset when the runtime type exposes it, else null

internal sealed record FbRpcMethodEntry(
    string Name,          // e.g. "get_State"
    bool IsVoid,
    int ParameterCount);

internal sealed record InspectFbInstanceResult(
    bool Success,
    string InstancePath,
    string? TypeName,
    IReadOnlyList<FbMemberEntry> Members,
    IReadOnlyList<FbRpcMethodEntry> RpcMethods,
    string AmsNetId,     // resolved NetId used for the connection
    int AdsPort,         // ADS port used
    string Message);     // "OK" or "[Error] ..."

// ---- ads-variable-rw: ENUM support ------------------------------------------
// ENUM symbols (DataTypeCategory.Enum) are read/written via their declared
// underlying primitive type (IAliasType.BaseTypeName, e.g. an E_STATE : (...)
// enum backed by INT). The JSON value carries both the symbolic member name
// (when the raw value matches a known member) and the raw number, so callers
// can branch on either without a prior schema lookup. Writes accept either an
// enum member name (e.g. "E_STATE_RUNNING") or a plain numeric string.

internal sealed record EnumAdsValue(
    string? Name,   // matching enum member name, or null for an out-of-range/reserved value
    object Value);  // raw underlying numeric value

// Issue #148 (D-3): BOOL writes used `raw is "true" or "1"` — anything else (e.g. "on",
// "ture", "2") wrote FALSE and reported success. Unknown text is now an error.
internal static class AdsBoolValue
{
    public const string Expected = "true, false, 1 or 0";

    public static bool TryParse(string? raw, out bool value)
    {
        value = false;
        string t = (raw ?? string.Empty).Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"') t = t[1..^1].Trim();
        switch (t.ToLowerInvariant())
        {
            case "true": case "1": value = true; return true;
            case "false": case "0": value = false; return true;
            default: return false;
        }
    }

    public static string UnknownMessage(string? raw) =>
        $"[Error] Unknown BOOL value '{raw}'. Expected {Expected}.";
}
