using System.Text.Json;
using System.Text.Json.Nodes;

namespace PtcXaeMcp.WinForms.Mcp;

/// <summary>
/// Where one page sits in a list: how many items there are in all (null when the source holds more than was read
/// and the total is not known), where the page starts, how many it holds and where the next page starts.
/// </summary>
internal record McpPageInfo(int? Total, int Offset, int Returned, bool HasMore, int? NextOffset)
{
    /// <summary>True when the caller asked for a page (or the tool always pages); a text tool then ends with <see cref="Footer"/>.</summary>
    public bool Paged { get; init; }

    /// <summary><c>Page: offset 0, returned 50 of 120; has_more true, next_offset 50</c>.</summary>
    public string Footer() =>
        FormattableString.Invariant($"Page: offset {Offset}, returned {Returned}") +
        (Total is { } total ? FormattableString.Invariant($" of {total}") : " (total not known)") +
        $"; has_more {(HasMore ? "true" : "false")}" +
        (NextOffset is { } next ? FormattableString.Invariant($", next_offset {next}") : "");

    /// <summary>
    /// Serializes <paramref name="payload"/> (a JSON object) and inserts <c>total</c>, <c>offset</c>, <c>returned</c>,
    /// <c>has_more</c> and <c>next_offset</c> right before the property <paramref name="beforeKey"/> (the item list),
    /// at the end when it is absent. The payload must not carry these names itself.
    /// </summary>
    public string Serialize(object payload, JsonSerializerOptions options, string? beforeKey = null)
    {
        if (JsonSerializer.SerializeToNode(payload, options) is not JsonObject node)
            throw new ArgumentException("The payload of a page must serialize to a JSON object.", nameof(payload));
        int at = beforeKey is null ? -1 : node.IndexOf(beforeKey);
        if (at < 0) at = node.Count;
        node.Insert(at++, "total", Total is { } total ? JsonValue.Create(total) : null);
        node.Insert(at++, "offset", JsonValue.Create(Offset));
        node.Insert(at++, "returned", JsonValue.Create(Returned));
        node.Insert(at++, "has_more", JsonValue.Create(HasMore));
        node.Insert(at, "next_offset", NextOffset is { } next ? JsonValue.Create(next) : null);
        return node.ToJsonString(options);
    }
}

/// <summary>One page of a list: the items plus <see cref="McpPageInfo"/>.</summary>
internal sealed record McpPage<T>(IReadOnlyList<T> Items, int? Total, int Offset, bool HasMore, int? NextOffset)
    : McpPageInfo(Total, Offset, Items.Count, HasMore, NextOffset);

/// <summary>
/// A tool's page size: <see cref="Default"/> when <c>limit</c> is omitted, <see cref="Max"/> as the ceiling
/// (0 = no ceiling). <c>limit=0</c> asks for the ceiling, i.e. everything when there is none; a larger limit is
/// cut to the ceiling (has_more/next_offset then lead on).
/// </summary>
internal readonly record struct McpLimit(int Default, int Max)
{
    /// <summary>Everything unless the caller asks for a page (twincat_plc_list, twincat_get_error_list).</summary>
    public static McpLimit Unbounded => new(0, 0);

    public int Resolve(int? limit) => limit switch
    {
        null => Default,
        0 => Max,
        { } l when Max > 0 => Math.Min(l, Max),
        { } l => l,
    };
}

/// <summary>
/// The paging convention of every list tool: parameters <c>limit</c> (default and ceiling per tool, see
/// <see cref="McpLimit"/>) and <c>offset</c> (default 0); the result names total, returned, has_more and
/// next_offset — as JSON fields (<see cref="McpPageInfo.Serialize"/>) or as the last line of a text result
/// (<see cref="McpPageInfo.Footer"/>).
/// </summary>
internal static class McpPaging
{
    /// <summary>Pages <paramref name="all"/>; <paramref name="limit"/> 0 means everything from <paramref name="offset"/> on.</summary>
    public static McpPage<T> Apply<T>(IReadOnlyList<T> all, int limit, int offset)
    {
        var (start, count, hasMore, next) = Window(all.Count, limit, offset);
        var items = start == 0 && count == all.Count ? all : all.Skip(start).Take(count).ToList();
        return new McpPage<T>(items, all.Count, start, hasMore, next) { Paged = limit > 0 || offset > 0 };
    }

    /// <summary>Pages <paramref name="all"/> with the tool's <paramref name="bounds"/> applied to the caller's limit.</summary>
    public static McpPage<T> Apply<T>(IReadOnlyList<T> all, int? limit, int offset, McpLimit bounds) =>
        Apply(all, bounds.Resolve(limit), offset);

    /// <summary>
    /// Pages the items read so far from a source that may hold more (<paramref name="complete"/> false): the total is
    /// then not known (null) and a non-empty page always has more after it.
    /// </summary>
    public static McpPage<T> ApplyPartial<T>(IReadOnlyList<T> seen, int limit, int offset, bool complete)
    {
        var page = Apply(seen, limit, offset);
        if (complete) return page;
        bool more = page.HasMore || page.Returned > 0;
        return page with { Total = null, HasMore = more, NextOffset = more ? page.Offset + page.Returned : null };
    }

    /// <summary>
    /// The same window over several lists at once (twincat_persistent_snapshot_compare): items
    /// [offset, offset+limit) of each list; total is the longest list, has_more holds while any list has more.
    /// </summary>
    public static McpPageInfo Info(int total, int limit, int offset)
    {
        var (start, count, hasMore, next) = Window(total, limit, offset);
        return new McpPageInfo(total, start, count, hasMore, next) { Paged = limit > 0 || offset > 0 };
    }

    /// <summary>The slice of <paramref name="items"/> that <paramref name="page"/> covers.</summary>
    public static IEnumerable<T> Slice<T>(IEnumerable<T> items, McpPageInfo page) =>
        items.Skip(page.Offset).Take(page.Returned);

    private static (int Start, int Count, bool HasMore, int? Next) Window(int total, int limit, int offset)
    {
        int start = Math.Clamp(offset, 0, total);
        int count = limit > 0 ? Math.Min(limit, total - start) : total - start;
        int end = start + count;
        bool hasMore = end < total;
        return (start, count, hasMore, hasMore ? end : null);
    }

    /// <summary>A negative limit or offset is refused before anything is read.</summary>
    public static string? Validate(int? limit, int offset) =>
        limit < 0 ? $"[Error] limit must be 0 or more (got {limit}): omit it for the tool's default, 0 for as many as the tool returns. Nothing was read."
        : offset < 0 ? $"[Error] offset must be 0 or more (got {offset}). Nothing was read."
        : null;
}
