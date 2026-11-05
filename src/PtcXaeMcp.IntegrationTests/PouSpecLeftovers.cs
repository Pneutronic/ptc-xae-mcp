using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PtcXaeMcp.IntegrationTests;

/// <summary>
/// Shared cleanup for the throwaway POUs that <see cref="PouMembersIntegrationTests"/>
/// creates — <c>FB_PouSpec_*</c>, <c>PRG_PouSpec_*</c> and their <c>_R</c> / <c>_1</c>
/// variants. Issue #94: these used to pile up in the fixture run after run because
/// per-test teardown swallowed delete failures and never persisted the delete.
///
/// <para>This helper enumerates leftovers <b>by name prefix</b> (so it catches them
/// regardless of which scenario or which previous run created them, including the
/// TwinCAT <c>_1</c> auto-suffix), deletes each through the automation interface, and
/// forces a single <c>Save All</c> so the deletes reach disk.</para>
///
/// <para><b>Never touches</b> <c>PRG_IntegrationScratch</c> or the <c>[INTEGRATION-TEST-HOOK]</c>
/// line in <c>MAIN</c> — those are permanent by design (<c>ScratchPou</c>) and do not match
/// the <c>*_PouSpec_*</c> prefixes, so they are excluded by construction.</para>
/// </summary>
internal static class PouSpecLeftovers
{
    private static readonly string[] LeftoverPrefixes = { "FB_PouSpec_", "PRG_PouSpec_" };

    public sealed record SweepResult(IReadOnlyList<string> Removed, IReadOnlyList<string> Failed)
    {
        public int Total => Removed.Count + Failed.Count;
    }

    /// <summary>True when a leaf node name is one of our throwaway test POUs.</summary>
    public static bool IsLeftover(string leafName) =>
        LeftoverPrefixes.Any(p => leafName.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// Deletes every leftover <c>*_PouSpec_*</c> node currently in the open project and
    /// persists the deletes with one <c>Save All</c>. Best-effort: a node that refuses to
    /// delete is reported in <see cref="SweepResult.Failed"/>, never thrown. Returns an
    /// empty result when the fixture was already clean.
    /// </summary>
    public static async Task<SweepResult> SweepAsync(McpClient client)
    {
        var paths = await ListLeftoverPathsAsync(client);
        var removed = new List<string>();
        var failed = new List<string>();

        foreach (var path in paths)
        {
            var text = GetText(await client.CallToolAsync("twincat_plc_delete",
                new Dictionary<string, object?> { ["path"] = path, ["kind"] = "POU" }));

            // The tool surfaces failures as "[Error] ..." text (not IsError). A
            // "not found" means the node is already gone — that is the goal, so
            // count it as removed, not failed.
            if (!text.StartsWith("[Error]", StringComparison.Ordinal) ||
                text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                removed.Add(Leaf(path));
            else
                failed.Add($"{Leaf(path)} → {text}");
        }

        if (removed.Count > 0)
            await client.CallToolAsync("twincat_save_project", new Dictionary<string, object?>());

        return new SweepResult(removed, failed);
    }

    /// <summary>Tree paths of every leftover <c>*_PouSpec_*</c> node in the open project.</summary>
    public static async Task<IReadOnlyList<string>> ListLeftoverPathsAsync(McpClient client)
    {
        var text = GetText(await client.CallToolAsync("twincat_plc_list",
            new Dictionary<string, object?> { ["kind"] = "POU" }));
        return ParseLeftoverPaths(text);
    }

    /// <summary>
    /// Extracts the tree paths of leftover POUs from <c>twincat_plc_list</c> output. Each
    /// item line is <c>"  {Kind/Subtype,-24} {Path}{ [lang]}"</c>; the path itself can contain
    /// spaces (e.g. "PLC Projekt"), so we strip only the leading kind token and an optional
    /// trailing " [lang]" rather than splitting on whitespace.
    /// </summary>
    internal static IReadOnlyList<string> ParseLeftoverPaths(string listPousText)
    {
        var result = new List<string>();
        foreach (var raw in listPousText.Split('\n'))
        {
            var line = raw.Trim();
            int firstSpace = line.IndexOf(' ');
            if (firstSpace < 0) continue;                       // summary / malformed line

            var path = line[(firstSpace + 1)..].TrimStart();    // drop the Kind token
            int lang = path.LastIndexOf(" [", StringComparison.Ordinal);
            if (lang >= 0 && path.EndsWith("]", StringComparison.Ordinal))
                path = path[..lang];                            // drop the trailing " [lang]"

            if (path.Contains('^') && IsLeftover(Leaf(path)))
                result.Add(path);
        }
        return result;
    }

    private static string Leaf(string path)
    {
        int caret = path.LastIndexOf('^');
        return caret >= 0 ? path[(caret + 1)..] : path;
    }

    private static string GetText(CallToolResult result)
    {
        var content = result.Content;
        if (content is null || content.Count == 0) return string.Empty;
        return string.Join("\n", content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
