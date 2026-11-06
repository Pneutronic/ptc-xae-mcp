using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class CoeCoordinator(ITwinCatSession session)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>CoE objects per page of twincat_read_coe_dictionary: default 100, at most 1000.</summary>
    internal static readonly Mcp.McpLimit DictionaryPage = new(100, 1000);

    public (string Summary, string Json) ReadDictionary(string treePath, string? indexFilter, int? limit = null, int offset = 0)
    {
        if (Mcp.McpPaging.Validate(limit, offset) is { } invalid) return (invalid, "{}");
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", "{}");
        if (treePath.Equals("TIID", StringComparison.OrdinalIgnoreCase))
            return ("[Error] treePath must be a slave node under TIID (not the root).", "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        CoeDictionary dict;
        try { dict = session.ReadCoeDictionary(treePath, indexFilter); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", "{}"); }

        var page = Mcp.McpPaging.Apply(dict.Objects, limit, offset, DictionaryPage);
        string json = page.Serialize(new
        {
            treePath = dict.TreePath,
            slaveType = dict.SlaveType,
            fallbackNote = dict.FallbackNote,
            objects = page.Items,
        }, JsonOpts, "objects");
        string filter = string.IsNullOrWhiteSpace(indexFilter) ? "" : $", filter={indexFilter}";
        string fallback = dict.FallbackNote is not null ? $" [{dict.FallbackNote}]" : "";
        string shown = page.Returned == dict.Objects.Count ? ""
            : page.Returned == 0 ? $" None from offset {page.Offset}."
            : $" Showing {page.Offset + 1}–{page.Offset + page.Returned}{(page.HasMore ? $", next_offset {page.NextOffset}" : "")}.";
        string summary =
            $"{dict.SlaveType}: {dict.Objects.Count} CoE object(s) in offline dictionary{filter}.{fallback}{shown}";
        return (summary, json);
    }

    public (string Summary, string Json) ReadStartup(string treePath)
    {
        if (string.IsNullOrWhiteSpace(treePath))
            return ("[Error] treePath is required.", "{}");
        if (treePath.Equals("TIID", StringComparison.OrdinalIgnoreCase))
            return ("[Error] treePath must be a slave node under TIID (not the root).", "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        CoeStartupList startup;
        try { startup = session.ReadCoeStartup(treePath); }
        catch (InvalidOperationException ex) { return ($"[Error] {ex.Message}", "{}"); }

        string json = JsonSerializer.Serialize(startup, JsonOpts);
        string entries = startup.EntryCount == 1 ? "1 Startup override" : $"{startup.EntryCount} Startup overrides";
        string summary = $"{startup.SlaveType}: {entries} (SDO-download entries set in project).";
        return (summary, json);
    }
}
