using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.ToolConsole;

/// <summary>One tool as the console lists it: metadata from <c>tools/list</c>.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">Tool description (may be empty).</param>
/// <param name="InputSchema">The tool's <c>inputSchema</c>.</param>
/// <param name="ReadOnly">ReadOnlyHint annotation (null = not declared).</param>
/// <param name="Destructive">DestructiveHint annotation (null = not declared).</param>
internal sealed record ToolSummary(
    string Name,
    string Description,
    System.Text.Json.JsonElement InputSchema,
    bool? ReadOnly,
    bool? Destructive);

/// <summary>A titled group of tool names in the console's tool list.</summary>
internal sealed record ToolGroup(string Title, IReadOnlyList<string> Tools);

/// <summary>Groups tools by area, derived from the tool name.</summary>
internal static class ToolGrouping
{
    // Ordered: the first matching rule wins ("create_scope_project" is Scope, not General).
    private static readonly (string Title, string[] Needles)[] Rules =
    [
        ("Scope", ["scope"]),
        ("HMI", ["_hmi_"]),
        ("Approvals", ["_ibn_"]),
        ("Safety", ["_safety_"]),
        ("PLC", ["_plc_", "_plc", "pou", "_gvl", "_dut", "plcopen", "init_symbol"]),
        ("ADS", ["_ads_"]),
    ];

    /// <summary>Area title for a tool name ("General" when no rule matches).</summary>
    public static string GroupOf(string toolName)
    {
        string n = toolName.ToLowerInvariant();
        if (n.StartsWith("ptc_", StringComparison.Ordinal)) return "Diagnostics";
        foreach (var (title, needles) in Rules)
            foreach (var needle in needles)
                if (n.Contains(needle, StringComparison.Ordinal)) return title;
        return "General";
    }

    /// <summary>
    /// The group title as the tool list shows it: the host-made words (Approvals, Safety, Diagnostics, General) in the
    /// host language, product names (Scope, HMI, PLC, ADS) as they are. <see cref="GroupOf"/> keeps the English key.
    /// </summary>
    public static string DisplayTitle(string title) => title switch
    {
        "Approvals" => Loc.Get("toolconsole.group.approvals"),
        "Safety" => Loc.Get("toolconsole.group.safety"),
        "Diagnostics" => Loc.Get("toolconsole.group.diagnostics"),
        "General" => Loc.Get("toolconsole.group.general"),
        _ => title,
    };

    /// <summary>
    /// Builds the area groups: alphabetical, "General" last; tools sorted by name. A non-empty
    /// <paramref name="filter"/> keeps tools whose name or description contains it (empty groups
    /// are dropped). Favourites are presets, not tools, and are listed by the view above these.
    /// </summary>
    public static IReadOnlyList<ToolGroup> Build(IEnumerable<ToolSummary> tools, string? filter)
    {
        var list = tools
            .Where(t => Matches(t, filter))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var groups = new List<ToolGroup>();

        var byArea = list
            .GroupBy(t => GroupOf(t.Name))
            .OrderBy(g => g.Key == "General" ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in byArea)
            groups.Add(new ToolGroup(g.Key, g.Select(t => t.Name).ToList()));
        return groups;
    }

    private static bool Matches(ToolSummary t, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string f = filter.Trim();
        return t.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
               t.Description.Contains(f, StringComparison.OrdinalIgnoreCase);
    }
}
