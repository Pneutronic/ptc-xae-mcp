using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.Mcp;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>limit/offset paging, the Next line in errors and the filtered Error List.</summary>
public class McpPagingTests
{
    private static readonly IReadOnlyList<int> Ten = Enumerable.Range(1, 10).ToList();

    [Fact]
    public void NoLimit_ReturnsEverything_AndIsNotPaged()
    {
        var page = McpPaging.Apply(Ten, 0, 0);
        Assert.Equal(10, page.Returned);
        Assert.False(page.Paged);
        Assert.False(page.HasMore);
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public void AFirstPage_SaysWhereTheNextStarts()
    {
        var page = McpPaging.Apply(Ten, 4, 0);
        Assert.Equal([1, 2, 3, 4], page.Items);
        Assert.True(page.HasMore);
        Assert.Equal(4, page.NextOffset);
        Assert.Equal("Page: offset 0, returned 4 of 10; has_more true, next_offset 4", page.Footer());
    }

    [Fact]
    public void TheLastPage_HasNoMore()
    {
        var page = McpPaging.Apply(Ten, 4, 8);
        Assert.Equal([9, 10], page.Items);
        Assert.False(page.HasMore);
        Assert.Equal("Page: offset 8, returned 2 of 10; has_more false", page.Footer());
    }

    [Fact]
    public void AnOffsetPastTheEnd_ReturnsNothing()
    {
        var page = McpPaging.Apply(Ten, 5, 50);
        Assert.Empty(page.Items);
        Assert.Equal(10, page.Offset);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void PagingThroughAll_VisitsEveryItemOnce()
    {
        var seen = new List<int>();
        int? offset = 0;
        while (offset is { } o)
        {
            var page = McpPaging.Apply(Ten, 3, o);
            seen.AddRange(page.Items);
            offset = page.NextOffset;
        }
        Assert.Equal(Ten, seen);
    }

    [Theory]
    [InlineData(-1, 0, "limit")]
    [InlineData(0, -2, "offset")]
    public void NegativeValues_AreRefused(int limit, int offset, string name) =>
        Assert.Contains(name, McpPaging.Validate(limit, offset));

    [Fact]
    public void AnOmittedLimit_IsFine() => Assert.Null(McpPaging.Validate(null, 0));

    [Theory]
    [InlineData(null, 100)]   // omitted: the tool's default
    [InlineData(0, 1000)]     // 0: as many as the tool returns
    [InlineData(7, 7)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]  // above the maximum: cut to it
    public void ABoundedLimit_Resolves(int? limit, int expected) =>
        Assert.Equal(expected, new McpLimit(100, 1000).Resolve(limit));

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(5000, 5000)]
    public void AnUnboundedLimit_KeepsEverythingByDefault(int? limit, int expected) =>
        Assert.Equal(expected, McpLimit.Unbounded.Resolve(limit));

    [Fact]
    public void ABoundedApply_PagesEvenWithoutALimit()
    {
        var page = McpPaging.Apply(Ten, null, 0, new McpLimit(4, 6));
        Assert.Equal([1, 2, 3, 4], page.Items);
        Assert.True(page.Paged);
        Assert.Equal(4, page.NextOffset);
        Assert.Equal(6, McpPaging.Apply(Ten, 99, 0, new McpLimit(4, 6)).Returned);
    }

    [Fact]
    public void APartialSource_HasNoTotal_AndLeadsOn()
    {
        var page = McpPaging.ApplyPartial(Ten, 4, 4, complete: false);
        Assert.Equal([5, 6, 7, 8], page.Items);
        Assert.Null(page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(8, page.NextOffset);
        Assert.Equal("Page: offset 4, returned 4 (total not known); has_more true, next_offset 8", page.Footer());

        // Even the last items read lead on: the source holds more than was read.
        var last = McpPaging.ApplyPartial(Ten, 4, 8, complete: false);
        Assert.True(last.HasMore);
        Assert.Equal(10, last.NextOffset);
        // An empty page never points at itself.
        var empty = McpPaging.ApplyPartial(Ten, 4, 10, complete: false);
        Assert.False(empty.HasMore);
        Assert.Null(empty.NextOffset);
    }

    [Fact]
    public void ACompleteSource_IsAnOrdinaryPage()
    {
        var page = McpPaging.ApplyPartial(Ten, 4, 8, complete: true);
        Assert.Equal(10, page.Total);
        Assert.False(page.HasMore);
        Assert.Equal("Page: offset 8, returned 2 of 10; has_more false", page.Footer());
    }

    [Fact]
    public void OneWindow_OverSeveralLists()
    {
        int[] longList = [1, 2, 3, 4, 5], shortList = [10, 20];
        var page = McpPaging.Info(longList.Length, 2, 2);
        Assert.Equal([3, 4], McpPaging.Slice(longList, page));
        Assert.Empty(McpPaging.Slice(shortList, page));
        Assert.True(page.HasMore);
        Assert.Equal(4, page.NextOffset);
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public void Serialize_PutsThePageFieldsBeforeTheList()
    {
        var page = McpPaging.Apply(Ten, 3, 3);
        string json = page.Serialize(new { success = true, items = page.Items, tail = "x" },
            new System.Text.Json.JsonSerializerOptions(), "items");

        Assert.Equal("""{"success":true,"total":10,"offset":3,"returned":3,"has_more":true,"next_offset":6,"items":[4,5,6],"tail":"x"}""", json);
    }

    [Fact]
    public void Serialize_WritesNullForAnUnknownTotalAndTheLastPage()
    {
        var page = McpPaging.ApplyPartial(Ten, 20, 0, complete: false) with { HasMore = false, NextOffset = null };
        string json = page.Serialize(new { items = page.Items }, new System.Text.Json.JsonSerializerOptions());

        Assert.EndsWith("""
            "total":null,"offset":0,"returned":10,"has_more":false,"next_offset":null}
            """, json);
    }

    [Fact]
    public void WithNext_AddsOneNextLine()
    {
        string text = McpErrors.WithNext("Thing not found", "look elsewhere");
        Assert.Equal("[Error] Thing not found\nNext: look elsewhere", text);
        Assert.Equal(text, McpErrors.WithNext(text, "another hint"));
        Assert.Equal("[Error] x\nNext: y", McpErrors.WithNext("[Error] x", "y"));
    }

    private static string ErrorList(string? minLevel, int limit, int offset)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeErrorList.AddRange([
            new BuildError("error", "E1", "A.TcPOU", 1),
            new BuildError("warning", "W1", null, null),
            new BuildError("message", "M1", null, null),
            new BuildError("error", "E2", "B.TcPOU", 2),
        ]);
        return new BuildCoordinator(fake).GetErrorList(minLevel, limit, offset);
    }

    [Fact]
    public void ErrorList_MinLevelError_KeepsOnlyErrors()
    {
        string text = ErrorList("error", 0, 0);
        Assert.Contains("Showing level error and above: 2 entries.", text);
        Assert.Contains("E1", text);
        Assert.Contains("E2", text);
        Assert.DoesNotContain("W1", text);
        Assert.DoesNotContain("M1", text);
    }

    [Fact]
    public void ErrorList_MinLevelWarning_KeepsErrorsAndWarnings()
    {
        string text = ErrorList("warning", 0, 0);
        Assert.Contains("W1", text);
        Assert.DoesNotContain("M1", text);
    }

    [Fact]
    public void ErrorList_Pages()
    {
        string text = ErrorList(null, 2, 2);
        Assert.Contains("M1", text);
        Assert.Contains("E2", text);
        Assert.DoesNotContain("E1", text);
        Assert.EndsWith("Page: offset 2, returned 2 of 4; has_more false", text);
    }

    [Fact]
    public void ErrorList_AnUnknownMinLevel_IsRefused() =>
        Assert.StartsWith("[Error] minLevel must be", ErrorList("fatal", 0, 0));
}
