using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Reading a scope recording through the export tool's CSV. The CSV below is synthetic, built after the layout
/// TC3ScopeExportTool.exe writes (header with FILETIME, one key/value column pair per channel, then time/value
/// pairs in ms, shorter channels leave cells empty, closed by EOF).
/// </summary>
public sealed class ScopeFileReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptc-scope-file-" + Guid.NewGuid().ToString("N"));

    public ScopeFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // 2026-01-02 03:04:05.006 UTC as FILETIME.
    private static readonly long StartFt = new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc).ToFileTimeUtc();

    private static string Csv(IEnumerable<string> dataRows) => string.Join("\r\n", new[]
    {
        "Name\tTest Recording\t",
        "File\tC:\\temp\\out.csv\t",
        $"Starttime of export\t{StartFt}\tFreitag, 2. Januar 2026\t04:04:05.006",
        $"Endtime of export\t{StartFt + 10_000_000}\tFreitag, 2. Januar 2026\t04:04:06.006",
        "",
        "",
        "Name\tnCount\tName\trSpeed",
        "SymbolComment\t counter\tSymbolComment\t",
        "Data-Type\tUINT16\tData-Type\tREAL64",
        "SampleTime[ms]\t10\tSampleTime[ms]\t2",
        "SymbolName\tMAIN.nCount\tSymbolName\tMAIN.rSpeed",
        "NetID\t10.0.0.1.1.1\tNetID\t10.0.0.1.1.1",
        "Port\t851\tPort\t851",
        "ScaleFactor\t1\tScaleFactor\t0.5",
        "Unit\t(None)\tUnit\tmm/s",
        "",
    }.Concat(dataRows).Append("EOF"));

    private static IEnumerable<string> Rows()
    {
        // nCount: 3 samples (0,10,20 ms); rSpeed: 5 samples every 2 ms with a spike at 4 ms.
        yield return "0\t1\t0\t0.5";
        yield return "10\t2\t2\t1.5";
        yield return "20\t3\t4\t99.25";
        yield return "\t\t6\t1";
        yield return "\t\t8\t2";
    }

    [Fact]
    public void Parse_HeaderMetadataAndStatistics()
    {
        var r = ScopeExportCsv.Parse(new StringReader(Csv(Rows())));

        Assert.Equal("Test Recording", r.Name);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 6, DateTimeKind.Utc), r.StartUtc);
        Assert.Equal(r.StartUtc!.Value.AddSeconds(1), r.EndUtc);
        Assert.Equal(["nCount", "rSpeed"], r.Channels.Select(c => c.Name));

        var n = r.Channels[0];
        Assert.Equal("MAIN.nCount", n.Meta("SymbolName"));
        Assert.Equal("UINT16", n.Meta("Data-Type"));
        Assert.Equal("counter", n.Meta("SymbolComment"));
        Assert.Equal(3, n.SampleCount);
        Assert.Equal((0d, 20d), (n.FirstMs!.Value, n.LastMs!.Value));
        Assert.Equal((1d, 3d, 2d), (n.Min!.Value, n.Max!.Value, n.Mean!.Value));
        Assert.Null(n.Points);

        var s = r.Channels[1];
        Assert.Equal(5, s.SampleCount);
        Assert.Equal(8, s.LastMs);
        Assert.Equal(99.25, s.Max);
        Assert.Equal("mm/s", s.Meta("Unit"));
    }

    [Fact]
    public void Parse_NoChannelBlock_IsAFormatError()
    {
        Assert.Throws<FormatException>(() => ScopeExportCsv.Parse(new StringReader("Name\tx\r\nEOF")));
    }

    [Fact]
    public void Points_SelectedChannelOnly_InWindow()
    {
        var r = ScopeExportCsv.Parse(new StringReader(Csv(Rows())),
            new ScopePointRequest(new HashSet<string>(["MAIN.rSpeed"], StringComparer.OrdinalIgnoreCase), FromMs: 2, ToMs: 6, MaxPoints: 500));

        Assert.Null(r.Channels[0].Points);
        Assert.Equal([2d, 4d, 6d], r.Channels[1].Points!.Select(p => p.TimeMs));
        Assert.False(r.Channels[1].Decimated);
    }

    [Fact]
    public void Points_Decimation_KeepsTheSpike()
    {
        var rows = Enumerable.Range(0, 1000).Select(i => $"\t\t{i * 2}\t{(i == 637 ? 500 : i % 7)}");
        var r = ScopeExportCsv.Parse(new StringReader(Csv(rows)), new ScopePointRequest(null, null, null, MaxPoints: 20));

        var points = r.Channels[1].Points!;
        Assert.True(r.Channels[1].Decimated);
        Assert.True(points.Count <= 20);
        Assert.Contains(points, p => p.Value == 500 && p.TimeMs == 1274);
        Assert.Equal(points.OrderBy(p => p.TimeMs).Select(p => p.TimeMs), points.Select(p => p.TimeMs));
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1,5", 1.5)]
    [InlineData("-3e2", -300)]
    public void TryNumber_InvariantOrDecimalComma(string cell, double expected)
    {
        Assert.True(ScopeExportCsv.TryNumber(cell, out double v));
        Assert.Equal(expected, v);
    }

    // ── reader: tool lookup, process, files ────────────────────────────────────

    private string Svdx()
    {
        string path = Path.Combine(_dir, "rec.svdx");
        File.WriteAllText(path, "not read by the reader itself");
        return path;
    }

    private ScopeFileReader Reader(Func<IReadOnlyList<string>, int?> run, bool toolExists = true)
    {
        string exe = Path.Combine(_dir, "TC3ScopeExportTool.exe");
        if (toolExists) File.WriteAllText(exe, "");
        return new ScopeFileReader
        {
            ToolCandidates = () => [exe],
            Run = (_, args, _) => run(args),
        };
    }

    // Writes the synthetic CSV where the "tool" was told to.
    private static int? WriteCsv(IReadOnlyList<string> args)
    {
        string target = args.Single(a => a.StartsWith("target=", StringComparison.Ordinal))["target=".Length..];
        File.WriteAllText(target, Csv(Rows()));
        return 0;
    }

    [Fact]
    public void Read_ReturnsExperimentalLineSummaryAndJson_AndDeletesTheTempCsv()
    {
        var calls = new List<IReadOnlyList<string>>();
        string svdx = Svdx();

        string result = Reader(a => { calls.Add(a); return WriteCsv(a); })
            .Read(svdx, "nCount", null, null, 0, includeValues: true);

        Assert.StartsWith("[Experimental]", result);
        Assert.Contains("Scope recording 'Test Recording': 2 channels, 2026-01-02 03:04:05.006Z", result);
        Assert.Contains("\"symbolName\": \"MAIN.nCount\"", result);
        Assert.Contains("\"points\"", result);
        var args = Assert.Single(calls);
        Assert.Equal($"svd={svdx}", args[0]);
        Assert.Equal("silent", args[2]);
        Assert.False(File.Exists(args[1]["target=".Length..]));
    }

    [Fact]
    public void Read_ToolMissing_NamesTheProducts()
    {
        string result = Reader(_ => 0, toolExists: false).Read(Svdx(), null, null, null, 0, false);

        Assert.StartsWith("[Error] TC3ScopeExportTool.exe not found", result);
        Assert.Contains("TE130x Scope View or TF3300 Scope Server", result);
    }

    [Fact]
    public void Read_NonZeroExit_And_Timeout_AreErrors()
    {
        Assert.StartsWith("[Error] The export tool exited with code 3", Reader(_ => 3).Read(Svdx(), null, null, null, 0, false));
        Assert.StartsWith("[Error] The export tool did not finish", Reader(_ => null).Read(Svdx(), null, null, null, 0, false));
    }

    [Fact]
    public void Read_MissingFile_And_ReversedWindow()
    {
        Assert.StartsWith("[Error] Scope file not found", Reader(_ => 0).Read(Path.Combine(_dir, "none.svdx"), null, null, null, 0, false));
        Assert.StartsWith("[Error] toMs (1) is before fromMs (5)", Reader(_ => 0).Read(Svdx(), null, 5, 1, 0, true));
    }

    [Fact]
    public void Convert_RefusesAnExistingTarget_UnlessOverwrite()
    {
        string target = Path.Combine(_dir, "out.csv");
        File.WriteAllText(target, "old");
        var reader = Reader(WriteCsv);

        Assert.StartsWith("[Error]", reader.Convert(Svdx(), target, overwrite: false));
        Assert.Equal("old", File.ReadAllText(target));

        string result = reader.Convert(Svdx(), target, overwrite: true);
        Assert.StartsWith("[Experimental]", result);
        Assert.Contains("Converted", result);
        Assert.StartsWith("Name\tTest Recording", File.ReadAllText(target));
    }

    [Fact]
    public void Convert_NeedsAnExtension()
    {
        Assert.StartsWith("[Error] targetPath needs an extension", Reader(WriteCsv).Convert(Svdx(), Path.Combine(_dir, "out"), false));
    }
}
