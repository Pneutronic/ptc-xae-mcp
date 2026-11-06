namespace PtcXaeMcp.WinForms.TwinCat;

// Pure, COM-free `kind` handling of the generic twincat_plc_* tools. The caller probes the node
// (ProbePlcNode → PlcNodeInfo) and passes the result in; every mismatch is reported here, before any
// logout or write, as "[Error] '<path>' is a GVL, not a DUT".
internal static class PlcKindResolver
{
    /// <summary>Canonicalises a kind argument. False + "[Error] ..." when it is empty or not in the catalog.</summary>
    public static bool TryParseKind(string? raw, out string kind, out string? error)
    {
        kind = "";
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"[Error] kind is required. Expected {PlcKind.Expected}.";
            return false;
        }
        string upper = raw.Trim().ToUpperInvariant();
        string? match = PlcKind.All.FirstOrDefault(k => k == upper);
        if (match is null)
        {
            error = $"[Error] Unknown kind '{raw}'. Expected {PlcKind.Expected}.";
            return false;
        }
        kind = match;
        return true;
    }

    /// <summary>Canonicalises a subtype of <paramref name="kind"/>; null/blank means "not given" and is accepted.</summary>
    public static bool TryParseSubtype(string kind, string? raw, out string? subtype, out string? error)
    {
        subtype = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        string upper = raw.Trim().ToUpperInvariant();
        var known = PlcElementCatalog.SubtypesOf(kind);
        string? match = known.FirstOrDefault(s => s == upper);
        if (match is null)
        {
            error = known.Count == 0
                ? $"[Error] kind {kind} has no subtypes; got subtype '{raw}'."
                : $"[Error] Unknown subtype '{raw}' for kind {kind}. Expected {string.Join(", ", known)}.";
            return false;
        }
        subtype = match;
        return true;
    }

    /// <summary>The kind of an ItemType, or null for system nodes and unknown types.</summary>
    public static string? KindOf(int itemType) => PlcElementCatalog.ForItemType(itemType)?.Kind;

    /// <summary>The next step after a PLC path that does not exist.</summary>
    internal const string NotFoundHint =
        "twincat_plc_list (parentPath = the part before the last '^') or twincat_plc_search shows the real paths; node labels are localized.";

    /// <summary>
    /// Null when the node at <paramref name="path"/> exists and is an element of <paramref name="kind"/>
    /// (and of <paramref name="subtype"/>, when given); otherwise the "[Error] ..." text. <paramref name="kind"/>
    /// and <paramref name="subtype"/> must already be canonical (see <see cref="TryParseKind"/>).
    /// </summary>
    public static string? Check(string path, string kind, PlcNodeInfo node, string? subtype = null)
    {
        if (!node.Exists)
            return McpErrors.WithNext($"PLC tree path not found: {path}", NotFoundHint);
        if (node.ItemType is not int itemType)
            return $"[Error] Cannot verify kind of '{path}': its ItemType could not be read.";

        var entry = PlcElementCatalog.ForItemType(itemType);
        if (entry is null)
            return $"[Error] '{path}' is not a PLC element known to this tool (ItemType {itemType}), not {WithArticle(kind)}.";
        if (entry.IsSystem)
            return $"[Error] '{path}' is a PLC system node (ItemType {itemType}), not {WithArticle(kind)}.";
        if (entry.Kind != kind)
            return $"[Error] '{path}' is {WithArticle(entry.Kind!)}, not {WithArticle(kind)}.";
        if (subtype is not null && entry.Subtype != subtype)
            return $"[Error] '{path}' is {WithArticle(entry.Kind!)} ({entry.Subtype ?? "no subtype"}), not {WithArticle(kind)} ({subtype}).";
        return null;
    }

    /// <summary>"a POU", "an INTERFACE" — for the kind in an error text.</summary>
    public static string WithArticle(string kind) =>
        kind is PlcKind.Interface or PlcKind.ImagePool ? $"an {kind}" : $"a {kind}";
}
