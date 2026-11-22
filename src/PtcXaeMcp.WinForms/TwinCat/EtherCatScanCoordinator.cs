using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// Scans the real EtherCAT network of a configured master and compares it with the configured boxes. The scan runs
/// on a temporary device (see <see cref="ITwinCatSession.ScanEtherCatMaster"/>); the project stays unchanged.
/// </summary>
internal sealed class EtherCatScanCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private const string ConfigModeHint =
        "The scan needs the target in config mode: twincat_set_system_mode config, then try again.";

    public string ScanCompare(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return "[Error] treePath is required (an EtherCAT master under TIID, e.g. from twincat_get_fieldbus_topology).";

        string? guard = SessionGuards.RequireOpenSolution(session);
        if (guard is not null) return guard;

        EtherCatScanData data;
        try { data = session.ScanEtherCatMaster(treePath.Trim()); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.WithNext(McpErrors.EnsureError(ex.Message), ConfigModeHint);
        }

        var comparison = EtherCatScanComparer.Compare(data.Configured, data.Scanned);
        return JsonSerializer.Serialize(new
        {
            treePath = data.MasterTreePath,
            identical = comparison.Identical,
            summary = comparison.Identical
                ? $"Configuration is identical: {data.Configured.Count} box(es) match the scan."
                : $"{comparison.Differences.Count} difference(s): configured {data.Configured.Count} box(es), scanned {data.Scanned.Count}.",
            configured = data.Configured,
            scanned = data.Scanned,
            differences = comparison.Differences.Select(d => new
            {
                position = d.Position,
                kind = d.Kind,
                configured = d.Configured,
                scanned = d.Scanned,
            }),
            note = "The project is unchanged: the scan ran on a temporary device that was removed again, and nothing was saved. " +
                   "Passive boxes (end caps) are not part of the comparison.",
        }, JsonOpts);
    }
}
