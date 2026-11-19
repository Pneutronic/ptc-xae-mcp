using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// TE2000's "Switch Mapping to PLC" as pure rules (decompiled engineering 14.17.22.1,
/// <c>ConfigToolWindowControl.MapToPlcItem_Click</c> → <c>TcHmiServerCommunication.AutoMapSymbol</c>): a mapping in the
/// engineering-only <c>TMC</c> domain is re-added under the SAME mapped name with <c>DOMAIN="ADS"</c>,
/// <c>MAPPING</c> = the target runtime + the internal path without its first segment, <c>AUTOMAP=true</c>,
/// <c>USEMAPPING=true</c>. No unmap and no rename, so bindings, views and project datatypes keep working. Here the
/// server configuration entry <c>TcHmiSrv.Config::SYMBOLS::&lt;name&gt;</c> is rewritten — the entry that request
/// produces. <c>AUTOMAP</c> belongs to the request, not to the stored entry: live 2026-11-18 the server refused an
/// entry carrying it ("server request") and accepted the same entry without it.
/// </summary>
internal static class HmiMappingSwitch
{
    internal const string SymbolsConfig = "TcHmiSrv.Config::SYMBOLS";

    internal static string ConfigSymbol(string mappedName) => $"{SymbolsConfig}::{mappedName}";

    /// <summary>The internal path re-rooted at <paramref name="runtime"/>: <c>PLC_TMC::MAIN::x</c> →
    /// <c>PLC1::MAIN::x</c>. Null when the path has no segment after its root.</summary>
    internal static string? Reroot(string? mapping, string runtime)
    {
        string path = mapping?.Trim() ?? "";
        int sep = path.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0 || sep + 2 >= path.Length) return null;
        return runtime + path[sep..];
    }

    /// <summary>One parsed config entry: its domain and internal path, or null when it is not an entry object.</summary>
    internal static (string? Domain, string? Mapping)? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return null;
            return (Text(o, "DOMAIN"), Text(o, "MAPPING"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The entry after the switch: everything kept except DOMAIN, MAPPING, USEMAPPING (set), AUTOMAP and
    /// SCHEMA (dropped — an automapped ADS symbol takes its type from the runtime, the TMC schema would pin the
    /// offline one). Null when the entry cannot be re-rooted.</summary>
    internal static string? Switched(string entryJson, string runtime)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(entryJson) as JsonObject; }
        catch (JsonException) { return null; }
        if (o is null) return null;
        string? mapping = Reroot(Text(o, "MAPPING"), runtime);
        if (mapping is null) return null;
        o.Remove("SCHEMA");
        o.Remove("AUTOMAP");
        o["DOMAIN"] = HmiSymbolNames.AdsDomain;
        o["MAPPING"] = mapping;
        o["USEMAPPING"] = true;
        return o.ToJsonString();
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
