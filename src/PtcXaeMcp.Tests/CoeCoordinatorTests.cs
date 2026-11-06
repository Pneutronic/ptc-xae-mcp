using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class CoeCoordinatorTests
{
    private const string Path = "TIID^Device 1 (EtherCAT)^Box 1^-1AI5 (EL3174)";

    private static CoeDictionary SampleDict(string path = Path) =>
        new(path, "EL3174", 2,
        [
            new CoeObject("1000", "Device type", "ro", "VAR",
            [
                new CoeSubIndex("00", "Device type", "UDINT", 32, "89132c01", null, "ro", false)
            ]),
            new CoeObject("800D", "AI Advanced Settings Ch.1", "rw", "RECORD",
            [
                new CoeSubIndex("00", "SubIndex 000", "USINT", 8, "18", null, "ro", false),
                new CoeSubIndex("11", "Input Type", "DT0802EN16", 16, "0200", "IIR level 1 (2)", "rw", true),
            ]),
        ]);

    private static CoeStartupList SampleStartup(string path = Path, int count = 0) =>
        new(path, "EL3174", count, count == 0
            ? Array.Empty<CoeStartupEntry>()
            : [new CoeStartupEntry("800D", "17", "0300", 16, "PS", "Input Type override", true)]);

    // ── ReadDictionary guards ─────────────────────────────────────────────────

    [Fact]
    public void ReadDictionary_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var (summary, json) = new CoeCoordinator(fake).ReadDictionary(Path, null);

        Assert.StartsWith("[Error]", summary);
        Assert.Equal("{}", json);
        Assert.DoesNotContain("ReadCoeDictionary", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void ReadDictionary_NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        var (summary, _) = new CoeCoordinator(fake).ReadDictionary(Path, null);
        Assert.StartsWith("[Error]", summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadDictionary_EmptyPath_ReturnsError(string path)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var (summary, json) = new CoeCoordinator(fake).ReadDictionary(path, null);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("required", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{}", json);
    }

    [Theory]
    [InlineData("TIID")]
    [InlineData("tiid")]
    public void ReadDictionary_RootPath_ReturnsError(string path)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var (summary, _) = new CoeCoordinator(fake).ReadDictionary(path, null);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("root", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadDictionary_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ReadCoeDictionaryThrows = new InvalidOperationException("ESI file not found"),
        };

        var (summary, json) = new CoeCoordinator(fake).ReadDictionary(Path, null);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("ESI file not found", summary);
        Assert.Equal("{}", json);
    }

    // ── ReadDictionary happy path ─────────────────────────────────────────────

    [Fact]
    public void ReadDictionary_HappyPath_ReturnsSummaryAndJson()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeDictionaryByPath[Path] = SampleDict();

        var (summary, json) = new CoeCoordinator(fake).ReadDictionary(Path, null);

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
        Assert.Contains("EL3174", summary);
        Assert.Contains("2", summary);
        Assert.Contains("CoE object", summary);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("objectCount", out _));
        Assert.False(doc.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal("EL3174", doc.RootElement.GetProperty("slaveType").GetString());
        var objs = doc.RootElement.GetProperty("objects");
        Assert.Equal(2, objs.GetArrayLength());
        Assert.Equal("800D", objs[1].GetProperty("index").GetString());
        Assert.Equal("Input Type", objs[1].GetProperty("subIndexes")[1].GetProperty("name").GetString());
    }

    [Fact]
    public void ReadDictionary_LimitAndOffset_PageTheObjects()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeDictionaryByPath[Path] = SampleDict();

        var (first, firstJson) = new CoeCoordinator(fake).ReadDictionary(Path, null, limit: 1);
        var (_, secondJson) = new CoeCoordinator(fake).ReadDictionary(Path, null, limit: 1, offset: 1);

        Assert.Contains("2 CoE object(s)", first);
        Assert.Contains("Showing 1–1, next_offset 1.", first);
        using var a = JsonDocument.Parse(firstJson);
        using var b = JsonDocument.Parse(secondJson);
        Assert.Equal("1000", a.RootElement.GetProperty("objects")[0].GetProperty("index").GetString());
        Assert.True(a.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(1, a.RootElement.GetProperty("next_offset").GetInt32());
        Assert.Equal("800D", b.RootElement.GetProperty("objects")[0].GetProperty("index").GetString());
        Assert.False(b.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, b.RootElement.GetProperty("next_offset").ValueKind);
    }

    [Fact]
    public void ReadDictionary_NegativeOffset_IsRefusedBeforeAnythingIsRead()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var (summary, json) = new CoeCoordinator(fake).ReadDictionary(Path, null, offset: -1);

        Assert.StartsWith("[Error] offset must be 0 or more", summary);
        Assert.Equal("{}", json);
        Assert.DoesNotContain("ReadCoeDictionary", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void ReadDictionary_WithIndexFilter_IncludedInSummary()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeDictionaryByPath[Path] = SampleDict();

        var (summary, _) = new CoeCoordinator(fake).ReadDictionary(Path, "800D");

        Assert.Contains("800D", summary);
    }

    [Fact]
    public void ReadDictionary_FallbackNote_AppearsInSummary()
    {
        const string note = "ESI revision 00100000 used as fallback for requested 00110000.";
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeDictionaryByPath[Path] = SampleDict() with { FallbackNote = note };

        var (summary, _) = new CoeCoordinator(fake).ReadDictionary(Path, null);

        Assert.Contains("fallback", summary, StringComparison.OrdinalIgnoreCase);
    }

    // ── ReadStartup guards ────────────────────────────────────────────────────

    [Fact]
    public void ReadStartup_NotConnected_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        var (summary, _) = new CoeCoordinator(fake).ReadStartup(Path);

        Assert.StartsWith("[Error]", summary);
        Assert.DoesNotContain("ReadCoeStartup", string.Join(",", fake.CallLog));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadStartup_EmptyPath_ReturnsError(string path)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        var (summary, _) = new CoeCoordinator(fake).ReadStartup(path);
        Assert.StartsWith("[Error]", summary);
    }

    [Fact]
    public void ReadStartup_SessionThrows_ReturnsError()
    {
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            ReadCoeStartupThrows = new InvalidOperationException("ProduceXml failed: boom"),
        };

        var (summary, json) = new CoeCoordinator(fake).ReadStartup(Path);

        Assert.StartsWith("[Error]", summary);
        Assert.Contains("ProduceXml", summary);
        Assert.Equal("{}", json);
    }

    // ── ReadStartup happy path ────────────────────────────────────────────────

    [Fact]
    public void ReadStartup_EmptyList_SummaryReflectsZero()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeStartupByPath[Path] = SampleStartup(count: 0);

        var (summary, json) = new CoeCoordinator(fake).ReadStartup(Path);

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
        Assert.Contains("0", summary);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("entryCount").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void ReadStartup_OneEntry_SummaryAndJson()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.CoeStartupByPath[Path] = SampleStartup(count: 1);

        var (summary, json) = new CoeCoordinator(fake).ReadStartup(Path);

        Assert.False(summary.StartsWith("[Error]", StringComparison.Ordinal), summary);
        Assert.Contains("EL3174", summary);
        Assert.Contains("1 Startup override", summary);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("entryCount").GetInt32());
        var entry = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("800D", entry.GetProperty("index").GetString());
        Assert.Equal("17", entry.GetProperty("subIndex").GetString());
        Assert.True(entry.GetProperty("complete").GetBoolean());
    }
}
