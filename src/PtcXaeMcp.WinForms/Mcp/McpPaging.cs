namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>One page of a list: the items, how many there are in all, and where the next page starts.</summary>
internal sealed record McpPage<T>(IReadOnlyList<T> Items, int Total, int Offset, bool HasMore, int? NextOffset)
{
    public int Returned => Items.Count;

    /// <summary>True when the caller asked for a page; a full list keeps its old output without the paging line.</summary>
    public bool Paged { get; init; }

    /// <summary><c>Page: offset 0, returned 50 of 120; has_more true, next_offset 50</c>.</summary>
    public string Footer() =>
        FormattableString.Invariant($"Page: offset {Offset}, returned {Returned} of {Total}; has_more {(HasMore ? "true" : "false")}") +
        (NextOffset is { } next ? FormattableString.Invariant($", next_offset {next}") : "");
}

/// <summary>
/// The paging convention of the list tools: <c>limit</c> (0 = everything, the default, so a call without paging
/// keeps its full output) and <c>offset</c>; the result names total, returned, has_more and next_offset.
/// </summary>
internal static class McpPaging
{
    public static McpPage<T> Apply<T>(IReadOnlyList<T> all, int limit, int offset)
    {
        int total = all.Count;
        int start = Math.Clamp(offset, 0, total);
        bool paged = limit > 0 || offset > 0;
        int count = limit > 0 ? Math.Min(limit, total - start) : total - start;
        var items = start == 0 && count == total ? all : all.Skip(start).Take(count).ToList();
        int end = start + count;
        bool hasMore = end < total;
        return new McpPage<T>(items, total, start, hasMore, hasMore ? end : null) { Paged = paged };
    }

    /// <summary>A negative limit or offset is refused before anything is read.</summary>
    public static string? Validate(int limit, int offset) =>
        limit < 0 ? $"[Error] limit must be 0 (everything) or more (got {limit}). Nothing was read."
        : offset < 0 ? $"[Error] offset must be 0 or more (got {offset}). Nothing was read."
        : null;
}
