using System.Text.Json;
using System.Text.Json.Serialization;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// twincat_read_soe_parameter: validates the arguments and shapes the result as a summary line plus JSON. Bad arguments,
/// an unreachable drive and unexpected failures are "[Error]" strings; an ADS error of one IDN is part of the result.
/// </summary>
internal sealed class SoeParameterCoordinator(ITwinCatSession session)
{
    internal const int MaxIdns = 256;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly int[] DefaultElements = [SoeElement.Value];

    public string Read(string? treePath, IReadOnlyList<string>? idns, int? driveNo, IReadOnlyList<string>? elements, int? timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required (the EtherCAT box of the SoE drive).";
        string path = treePath.Trim();
        if (path.Equals("TIID", StringComparison.OrdinalIgnoreCase))
            return "[Error] treePath must be an EtherCAT box under TIID (not the root).";

        List<int>? idnValues = null;
        if (idns is { Count: > 0 })
        {
            if (idns.Count > MaxIdns)
                return $"[Error] idns has {idns.Count} entries; at most {MaxIdns} per call.";
            idnValues = [];
            foreach (string text in idns)
            {
                if (!SoeIdn.TryParse(text, out int raw))
                    return $"[Error] idns entry '{text}' is not an IDN; use 'S-0-0001', 'P-0-3231' or a number 0-65535.";
                if (!idnValues.Contains(raw)) idnValues.Add(raw);
            }
        }

        IReadOnlyList<int> elementMasks = DefaultElements;
        if (elements is { Count: > 0 })
        {
            if (idnValues is null)
                return "[Error] elements only apply together with idns; without idns the Startup list is compared on the value element.";
            var masks = new List<int>();
            foreach (string text in elements)
            {
                if (!SoeElement.TryParse(text, out int mask))
                    return $"[Error] elements entry '{text}' is not valid; use name, attribute, unit, min, max, default, dataStatus or value.";
                if (!masks.Contains(mask)) masks.Add(mask);
            }
            elementMasks = masks;
        }

        if (driveNo is < 0 or >= EtherCatDeviceDetailReader.MaxDriveChannels)
            return $"[Error] driveNo {driveNo} is out of range (0-{EtherCatDeviceDetailReader.MaxDriveChannels - 1}).";

        string? timeoutError = AdsTimeout.Resolve(timeoutMs, out int timeout);
        if (timeoutError is not null) return timeoutError;

        string? guard = SessionGuards.RequireOpenSolution(session);
        if (guard is not null) return guard;

        SoeParameterReadResult result;
        try { result = session.ReadSoeParameters(path, idnValues, driveNo, elementMasks, timeout); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.EnsureError(ex.Message);
        }

        return Summary(result) + Environment.NewLine + JsonSerializer.Serialize(result, JsonOpts);
    }

    internal static string Summary(SoeParameterReadResult r)
    {
        int total = r.Parameters.Count;
        if (r.Mode == SoeParameterMode.Read)
        {
            int failed = r.Parameters.Count(p => p.Errors is not null);
            return $"{total} SoE parameters read{(failed > 0 ? $" ({failed} with errors)" : string.Empty)}";
        }

        int match = r.Parameters.Count(p => p.Match == true);
        int differ = r.Parameters.Count(p => p.Match == false);
        int errors = r.Parameters.Count(p => p.Errors is not null);
        int skipped = total - match - differ - errors;
        return $"{total} SoE parameters compared with the drive: {match} match, {differ} differ, {errors} errors, {skipped} skipped";
    }
}
