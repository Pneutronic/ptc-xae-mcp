using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// sbom: validates the arguments of the IPC inventory and shapes the MDP result as JSON.
/// Target problems (unreachable, no Device Manager, not ready) are structured results with success=false,
/// not "[Error]" strings; "[Error]" is reserved for bad arguments and unexpected failures.
/// </summary>
internal sealed class IpcInventoryCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Parses "0x0008" / "0x8" / "8" into module types; null error text on success.</summary>
    internal static string? ParseModuleTypes(IReadOnlyList<string>? raw, out IReadOnlyList<int>? types)
    {
        types = null;
        if (raw is null || raw.Count == 0) return null;
        var list = new List<int>();
        foreach (string item in raw)
        {
            string text = (item ?? string.Empty).Trim();
            bool ok;
            int value;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                ok = int.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            else
                ok = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!ok || value < 0 || value > 0xFFFF)
                return $"[Error] moduleTypes: '{item}' is not a module type (use e.g. \"0x0008\" or \"8\", range 0-0xFFFF).";
            list.Add(value);
        }
        types = list;
        return null;
    }

    public string ReadInventory(string? amsNetId, IReadOnlyList<string>? moduleTypes, bool includeRaw, int? timeoutMs = null)
    {
        string? typeError = ParseModuleTypes(moduleTypes, out var types);
        if (typeError is not null) return typeError;

        string? timeoutError = AdsTimeout.Resolve(timeoutMs, out int timeout);
        if (timeoutError is not null) return timeoutError;

        IpcInventoryResult r;
        try { r = session.ReadIpcInventory(string.IsNullOrWhiteSpace(amsNetId) ? null : amsNetId.Trim(), types, includeRaw, timeout); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.EnsureError(ex.Message);
        }
        return Format(r);
    }

    internal static string Format(IpcInventoryResult r) => JsonSerializer.Serialize(new
    {
        success = r.Success,
        status = r.Status,
        message = r.Message,
        netId = r.NetId,
        port = r.Port,
        indexGroup = Hex(MdpAddress.IndexGroup),
        readAt = r.ReadAt,
        adsError = r.AdsErrorCode is null ? null : new { code = r.AdsErrorCode, name = r.AdsErrorName },
        adsErrorCode = r.AdsErrorCode,
        adsErrorName = r.AdsErrorName,
        device = r.Device is null ? null : new { btn = r.Device.Btn, bic = r.Device.Bic },
        modules = r.Modules.Select(m => new
        {
            moduleId = $"0x{m.ModuleId:X2}",
            moduleType = $"0x{m.ModuleType:X4}",
            typeName = m.TypeName,
            name = m.Name,
            tables = m.Tables.Select(t => new
            {
                tableIndex = $"0x{t.TableIndex:X4}",
                name = t.Name,
                entries = t.Entries.Select(e => new
                {
                    subIndex = e.SubIndex,
                    name = e.Name,
                    dataType = e.DataType,
                    value = e.Value,
                    raw = e.Raw,
                    adsError = e.AdsError,
                    indexOffset = Hex(e.IndexOffset),
                }),
            }),
            decoded = m.Decoded,
        }),
        warnings = r.Warnings,
    }, JsonOpts);

    private static string Hex(uint v) => $"0x{v:X8}";
}
