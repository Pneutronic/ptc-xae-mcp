namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A node of a lazily realized tree (the Solution-Explorer tree via UIAutomation, or a fake).</summary>
internal interface IWalkNode
{
    string Name { get; }
    bool IsItem { get; }
    bool IsCollapsed { get; }
    void Expand();
    void Collapse();
    IReadOnlyList<IWalkNode> Children();
}

/// <summary>
/// Issue #150 (C-9): finds a Solution-Explorer node by its PATH, not by its leaf name. The first
/// segment is anchored by a shallow breadth-first search (the Solution Explorer puts grouping
/// levels such as "E/A" above the model's roots); every further segment must be a DIRECT child of
/// the previous match. Only nodes on the way are expanded, and every node this walk expanded is
/// collapsed again unless it is an ancestor of the match — on a miss, all of them.
/// Expanding a Solution-Explorer node pulls the XAE to the foreground (live 2026-11-05: Select and ScrollIntoView
/// do not, Expand does), so a collapsed node is opened only with <c>mayExpand</c>; otherwise the walk stops there
/// and says so.
/// </summary>
internal static class SegmentTreeWalker
{
    /// <summary>Anchor search depth below the tree control: solution → project → group ("E/A") → model root ("Geräte"), plus one spare level.</summary>
    internal const int MaxAnchorDepth = 5;

    internal sealed record WalkResult(IWalkNode? Match, string Note);

    internal const string CollapsedNote =
        "is collapsed in the Solution Explorer; opening it brings the XAE to the foreground, which happens only with bringToFront=true";

    public static WalkResult Find(IWalkNode root, IReadOnlyList<string> segments, bool mayExpand = false)
    {
        if (segments.Count == 0) return new WalkResult(null, "empty path");

        var expanded = new List<IWalkNode>();
        string? collapsed = null;
        var anchors = FindAnchors(root, segments[0], expanded, mayExpand, ref collapsed);
        if (anchors.Count == 0)
        {
            Restore(expanded, keep: null);
            return new WalkResult(null, collapsed is null
                ? $"'{segments[0]}' not found in the Solution Explorer tree"
                : $"'{segments[0]}' not found in the visible Solution Explorer tree: '{collapsed}' {CollapsedNote}");
        }

        string miss = "";
        foreach (var (anchor, anchorPath) in anchors)
        {
            var path = new List<IWalkNode>(anchorPath) { anchor };
            var current = anchor;
            int i = 1;
            for (; i < segments.Count; i++)
            {
                if (current.IsCollapsed)
                {
                    if (!mayExpand) { miss = $"'{current.Name}' {CollapsedNote}"; break; }
                    current.Expand();
                    expanded.Add(current);
                }
                var next = current.Children().FirstOrDefault(c => c.IsItem && NameEquals(c.Name, segments[i]));
                if (next is null) { miss = $"'{segments[i]}' not found under '{current.Name}'"; break; }
                current = next;
                path.Add(current);
            }
            if (i == segments.Count)
            {
                Restore(expanded, keep: new HashSet<IWalkNode>(path.Take(path.Count - 1), ReferenceEqualityComparer.Instance));
                return new WalkResult(current, $"path {string.Join(" > ", segments)}");
            }
        }

        Restore(expanded, keep: null);
        return new WalkResult(null, miss);
    }

    // Shallowest tree items named like the first segment, each with its ancestor chain.
    private static List<(IWalkNode Node, List<IWalkNode> Ancestors)> FindAnchors(
        IWalkNode root, string name, List<IWalkNode> expanded, bool mayExpand, ref string? collapsed)
    {
        var level = new List<(IWalkNode Node, List<IWalkNode> Ancestors)> { (root, new List<IWalkNode>()) };
        for (int depth = 0; depth <= MaxAnchorDepth && level.Count > 0; depth++)
        {
            var hits = level.Where(x => x.Node.IsItem && NameEquals(x.Node.Name, name)).ToList();
            if (hits.Count > 0) return hits;

            var next = new List<(IWalkNode, List<IWalkNode>)>();
            if (depth == MaxAnchorDepth) break;
            foreach (var (node, ancestors) in level)
            {
                if (node.IsCollapsed)
                {
                    if (!mayExpand) { collapsed ??= node.Name; continue; }
                    node.Expand();
                    expanded.Add(node);
                }
                var chain = node.IsItem ? new List<IWalkNode>(ancestors) { node } : ancestors;
                foreach (var c in node.Children()) next.Add((c, chain));
            }
            level = next;
        }
        return new List<(IWalkNode, List<IWalkNode>)>();
    }

    // Collapse what this walk expanded, deepest first, except the ancestors of the match.
    private static void Restore(List<IWalkNode> expanded, HashSet<IWalkNode>? keep)
    {
        for (int i = expanded.Count - 1; i >= 0; i--)
            if (keep is null || !keep.Contains(expanded[i]))
                expanded[i].Collapse();
    }

    private static bool NameEquals(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
