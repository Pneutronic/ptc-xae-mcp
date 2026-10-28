using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// twincat_read_ethercat_startup: validates the arguments and shapes the Startup lists as a summary line plus JSON.
/// Bad arguments and session failures are "[Error]" strings.
/// </summary>
internal sealed class EtherCatStartupCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly string[] Protocols =
        [EtherCatStartupProtocol.CoE, EtherCatStartupProtocol.SoE, EtherCatStartupProtocol.EoE, EtherCatStartupProtocol.FoE];

    public string Read(string? treePath, bool includeFixed, string? protocol)
    {
        if (!EtherCatStartupProtocol.TryNormalize(protocol, out string? normalized))
            return $"[Error] protocol '{protocol}' is not valid; use one of {EtherCatStartupProtocol.Names} (or omit it for all).";

        string? path = string.IsNullOrWhiteSpace(treePath) ? null : treePath.Trim();
        if (path is not null && path.Equals("TIID", StringComparison.OrdinalIgnoreCase))
            return "[Error] treePath must be an EtherCAT box under TIID (not the root); omit it to read all boxes.";

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        EtherCatStartupResult result;
        try { result = session.ReadEtherCatStartup(path, includeFixed, normalized); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpErrors.EnsureError(ex.Message);
        }

        return Summary(result, path is null) + Environment.NewLine + Json(result);
    }

    internal static string Summary(EtherCatStartupResult r, bool allMode)
    {
        var parts = Protocols.Select(p => (Protocol: p, Count: r.CountByProtocol(p))).Where(x => x.Count > 0)
            .Select(x => $"{x.Protocol} {x.Count}").ToList();
        string byProtocol = parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
        string slaves = r.Boxes.Count == 1 ? "1 slave" : $"{r.Boxes.Count} slaves";
        string scanned = allMode && r.SlavesScanned > r.Boxes.Count ? $" [{r.SlavesScanned} slaves scanned]" : string.Empty;
        return $"{slaves}, {r.EntryCount} startup entries{byProtocol} — {r.FixedCount} fixed{scanned}";
    }

    internal static string Json(EtherCatStartupResult r) => JsonSerializer.Serialize(new
    {
        slaveCount = r.Boxes.Count,
        slavesScanned = r.SlavesScanned,
        entryCount = r.EntryCount,
        fixedCount = r.FixedCount,
        byProtocol = Protocols.Where(p => r.CountByProtocol(p) > 0).ToDictionary(p => p, r.CountByProtocol),
        boxes = r.Boxes.Select(b => new
        {
            treePath = b.TreePath,
            name = b.Name,
            slaveType = b.SlaveType,
            physAddr = b.PhysAddr,
            linkedAxis = b.LinkedAxis,
            soeChannels = b.SoeChannels,
            virtualPort = b.VirtualPort,
            entryCount = b.Entries.Count,
            entries = b.Entries.Select(Entry),
        }),
    }, JsonOpts);

    // Only the fields that exist for the entry's protocol (a null stays out; the common ones always stay).
    private static Dictionary<string, object?> Entry(EtherCatStartupEntry e)
    {
        var d = new Dictionary<string, object?>
        {
            ["protocol"] = e.Protocol,
            ["fixed"] = e.Fixed,
            ["transitions"] = e.Transitions,
        };
        void Add(string key, object? value) { if (value is not null) d[key] = value; }
        Add("comment", e.Comment);
        Add("timeoutMs", e.TimeoutMs);
        d["dataHex"] = e.DataHex;
        Add("index", e.Index);
        Add("subIndex", e.SubIndex);
        Add("completeAccess", e.CompleteAccess);
        Add("source", e.Source);
        Add("idn", e.Idn);
        Add("idnRaw", e.IdnRaw);
        Add("parameterSet", e.ParameterSet);
        Add("driveNo", e.DriveNo);
        Add("opCode", e.OpCode);
        Add("elements", e.Elements);
        Add("attribute", e.Attribute);
        Add("value", e.Value);
        Add("list", e.List);
        Add("idnList", e.IdnList);
        return d;
    }
}
