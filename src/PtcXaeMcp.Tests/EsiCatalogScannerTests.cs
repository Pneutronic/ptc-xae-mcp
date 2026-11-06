using System.Globalization;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #85: offline ESI catalog scan behind twincat_list_ethercat_box_catalog.
/// Uses small synthetic ESI files in a per-test temp directory.
/// </summary>
public sealed class EsiCatalogScannerTests : IDisposable
{
    private readonly string _dir;

    public EsiCatalogScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ptc-esi-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string Device(string type, string productCode, string revision, params (string LcId, string Name)[] names)
    {
        var sb = new StringBuilder();
        sb.Append("<Device Physics=\"KK\">");
        sb.Append(CultureInfo.InvariantCulture, $"<Type ProductCode=\"{productCode}\" RevisionNo=\"{revision}\">{type}</Type>");
        foreach (var (lcId, name) in names)
            sb.Append(CultureInfo.InvariantCulture, $"<Name LcId=\"{lcId}\">{name}</Name>");
        // Bulk the real files carry and the scanner must skip over.
        sb.Append("<Info><Electrical><EBusCurrent>90</EBusCurrent></Electrical></Info>");
        sb.Append("<Profile><Dictionary><DataTypes><DataType><Name>BOOL</Name><Type>X</Type></DataType></DataTypes></Dictionary></Profile>");
        sb.Append("</Device>");
        return sb.ToString();
    }

    private string WriteEsi(string fileName, string vendorId, string vendorName, string devicesXml,
                            string encoding = "ISO-8859-1", string? subDir = null)
    {
        string xml =
            $"<?xml version=\"1.0\" encoding=\"{encoding}\"?>\r\n" +
            "<EtherCATInfo Version=\"1.2\">" +
            $"<Vendor><Id>{vendorId}</Id><Name>{vendorName}</Name></Vendor>" +
            "<Descriptions>" +
            "<Groups><Group><Type>DigIn</Type><Name LcId=\"1033\">Digital In</Name></Group></Groups>" +
            $"<Devices>{devicesXml}</Devices>" +
            "<Modules><Module><Type>NotADevice</Type></Module></Modules>" +
            "</Descriptions></EtherCATInfo>";
        string dir = subDir is null ? _dir : Path.Combine(_dir, subDir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(xml));
        return path;
    }

    private void WriteBeckhoffSample()
    {
        WriteEsi("Beckhoff EL1xxx.xml", "2", "Beckhoff Automation GmbH &amp; Co. KG",
            Device("EL1008", "#x03f03052", "#x00100000", ("1033", "EL1008 8Ch. Dig. Input 24V, 3ms"), ("1031", "EL1008 8K. Dig. Eingang")) +
            Device("EL1008", "#x03f03052", "#x00110000", ("1031", "EL1008 8K. Dig. Eingang"), ("1033", "EL1008 8Ch. Dig. Input 24V, 3ms")) +
            Device("EL1004", "#x03ec3052", "#x00120000", ("1033", "EL1004 4Ch. Dig. Input")));
        WriteEsi("Beckhoff EL31xx.xml", "#x00000002", "Beckhoff Automation GmbH &amp; Co. KG",
            Device("EL3112", "#xc283052", "#x00130000", ("1033", "EL3112")) +
            Device("EL3112", "#x0c283052", "#x00120000", ("1033", "EL3112")) +
            Device("EL3112-0011", "#x0c283052", "#x0010000B", ("1033", "EL3112-0011")));
    }

    private static JsonElement Json(string result)
    {
        int nl = result.IndexOf('\n');
        Assert.True(nl > 0, "expected summary line + JSON: " + result);
        return JsonDocument.Parse(result[(nl + 1)..]).RootElement;
    }

    // ── scan ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_MultipleDevicesPerFile_ExtractsTypeCodesNameVendorAndFile()
    {
        WriteBeckhoffSample();

        EsiCatalogScan scan = EsiCatalogScanner.Scan(_dir);

        Assert.Equal(2, scan.FilesScanned);
        Assert.Equal(0, scan.FilesSkipped);
        Assert.Equal(6, scan.Devices.Count);
        Assert.DoesNotContain(scan.Devices, d => d.Type == "NotADevice" || d.Type == "DigIn" || d.Type == "X");

        EsiCatalogDevice el1004 = Assert.Single(scan.Devices, d => d.Type == "EL1004");
        Assert.Equal(0x03ec3052u, el1004.ProductCode);
        Assert.Equal(0x00120000u, el1004.RevisionNo);
        Assert.Equal("EL1004 4Ch. Dig. Input", el1004.Name);
        Assert.Equal(2u, el1004.VendorId);
        Assert.Equal("Beckhoff Automation GmbH & Co. KG", el1004.VendorName);
        Assert.EndsWith("Beckhoff EL1xxx.xml", el1004.SourceFile);
        Assert.Equal("EL1004-0000-0018", el1004.VInfo);
    }

    [Fact]
    public void Scan_PrefersEnglishName_EvenWhenNotFirst_FallsBackToFirst()
    {
        WriteEsi("a.xml", "2", "Beckhoff",
            Device("EL2008", "#x07d83052", "#x00100000", ("1031", "Deutsch"), ("1033", "English")) +
            Device("EL2004", "#x07d43052", "#x00100000", ("1031", "Nur Deutsch")));

        var scan = EsiCatalogScanner.Scan(_dir);

        Assert.Equal("English", scan.Devices.Single(d => d.Type == "EL2008").Name);
        Assert.Equal("Nur Deutsch", scan.Devices.Single(d => d.Type == "EL2004").Name);
    }

    [Fact]
    public void Scan_MalformedFile_IsSkippedAndCounted_OtherFilesStillScanned()
    {
        WriteBeckhoffSample();
        File.WriteAllText(Path.Combine(_dir, "broken.xml"),
            "<?xml version=\"1.0\"?><EtherCATInfo><Vendor><Id>2</Id></Vendor><Descriptions><Devices><Device><Type>EL9999");

        var scan = EsiCatalogScanner.Scan(_dir);

        Assert.Equal(2, scan.FilesScanned);
        Assert.Equal(1, scan.FilesSkipped);
        string warning = Assert.Single(scan.Warnings);
        Assert.StartsWith("broken.xml:", warning);
        Assert.Contains(scan.Devices, d => d.Type == "EL1008");
    }

    [Fact]
    public void Scan_NonEsiXml_IsCountedSeparately_NotAsSkipped()
    {
        WriteBeckhoffSample();
        File.WriteAllText(Path.Combine(_dir, "modules.xml"), "<?xml version=\"1.0\"?><EtherCATModule><Modules/></EtherCATModule>");

        var scan = EsiCatalogScanner.Scan(_dir);

        Assert.Equal(2, scan.FilesScanned);
        Assert.Equal(0, scan.FilesSkipped);
        Assert.Equal(1, scan.NonEsiFiles);
    }

    [Fact]
    public void Scan_IncludesSubdirectories_AndUtf8Files()
    {
        WriteEsi("vendor.xml", "#x00000123", "ACME GmbH",
            Device("ACME-IO 16", "#x00000001", "#x00000005", ("1033", "ACME IO")),
            encoding: "UTF-8", subDir: "acme");

        var scan = EsiCatalogScanner.Scan(_dir);

        EsiCatalogDevice d = Assert.Single(scan.Devices);
        Assert.Equal(0x123u, d.VendorId);
        Assert.Equal("ACME-IO 16", d.VInfo); // non-Beckhoff: bare Type text, no revision suffix
    }

    [Fact]
    public void Scan_SecondCall_IsCached_UntilAFileChanges()
    {
        WriteBeckhoffSample();

        Assert.False(EsiCatalogScanner.Scan(_dir).FromCache);
        Assert.True(EsiCatalogScanner.Scan(_dir).FromCache);

        WriteEsi("new.xml", "2", "Beckhoff", Device("EK1100", "#x044c2c52", "#x00110000", ("1033", "EK1100")));
        var rescanned = EsiCatalogScanner.Scan(_dir);
        Assert.False(rescanned.FromCache);
        Assert.Contains(rescanned.Devices, d => d.Type == "EK1100");
    }

    [Fact]
    public void Scan_SameDeviceInTwoFiles_IsDeduplicated()
    {
        string dev = Device("EK1100", "#x044c2c52", "#x00110000", ("1033", "EK1100"));
        WriteEsi("a.xml", "2", "Beckhoff", dev);
        WriteEsi("b.xml", "2", "Beckhoff", dev);

        var scan = EsiCatalogScanner.Scan(_dir);

        EsiCatalogDevice d = Assert.Single(scan.Devices);
        Assert.EndsWith("a.xml", d.SourceFile);
    }

    // ── vInfo / numbers ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("EL1008", 0x00110000u, 2u, "EL1008-0000-0017")]
    [InlineData("EL3112-0011", 0x0010000Bu, 2u, "EL3112-0011-0016")]
    [InlineData("AX5206-0000-0216", 0x00D80000u, 2u, "AX5206-0000-0216")]
    [InlineData("EL1008", null, 2u, "EL1008")]
    [InlineData("Red-y", 0x00010000u, 0x1234u, "Red-y")]
    public void BuildVInfo_FollowsBeckhoffConvention(string type, uint? rev, uint? vendor, string expected)
        => Assert.Equal(expected, EsiCatalogScanner.BuildVInfo(type, rev, vendor));

    [Theory]
    [InlineData("#x0c1d3052", 0x0c1d3052u)]
    [InlineData("#xC1D3052", 0x0c1d3052u)]
    [InlineData("0x10", 16u)]
    [InlineData("42", 42u)]
    [InlineData("-1", 0xFFFFFFFFu)]
    [InlineData("", null)]
    [InlineData("#xZZ", null)]
    public void ParseEsiNumber_HandlesHexDecValues(string raw, uint? expected)
        => Assert.Equal(expected, EsiCatalogScanner.ParseEsiNumber(raw));

    // ── query / tool result ─────────────────────────────────────────────────

    [Fact]
    public void ListCatalog_LatestRevisionOnly_KeepsNewestPerVariant()
    {
        WriteBeckhoffSample();

        var root = Json(EsiCatalogScanner.ListCatalog("el3112", esiDirectory: _dir));
        var vInfos = root.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()).ToList();

        // EL3112 (latest 0x13 → 0019) and its variant EL3112-0011 stay separate.
        Assert.Equal(new string?[] { "EL3112-0000-0019", "EL3112-0011-0016" }, vInfos);
        Assert.True(root.GetProperty("latestRevisionOnly").GetBoolean());
    }

    [Fact]
    public void ListCatalog_LatestRevisionOnly_IgnoresBeckhoffPlaceholderRevisions()
    {
        WriteEsi("Beckhoff EK11xx.xml", "2", "Beckhoff",
            Device("EK1100", "#x044c2c52", "#x00120000", ("1033", "EK1100")));
        WriteEsi("Beckhoff EtherCAT Terminals.xml", "2", "Beckhoff",
            Device("EK1100", "#x044c2c52", "#x270b0000", ("1033", "EK1100 generic")) +
            Device("BK1120", "#x04602c22", "#x270b0000", ("1033", "BK1120 generic")));

        var latest = Json(EsiCatalogScanner.ListCatalog("K11", esiDirectory: _dir))
            .GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()).ToList();
        var all = Json(EsiCatalogScanner.ListCatalog("EK1100", latestRevisionOnly: false, esiDirectory: _dir))
            .GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()).ToList();

        // Real revision wins over the 9995 fallback; a placeholder-only product is still listed.
        Assert.Equal(new string?[] { "BK1120-0000-9995", "EK1100-0000-0018" }, latest);
        Assert.Equal(new string?[] { "EK1100-0000-9995", "EK1100-0000-0018" }, all);
    }

    [Fact]
    public void ListCatalog_AllRevisions_ReturnsEveryRevision_NewestFirst()
    {
        WriteBeckhoffSample();

        var root = Json(EsiCatalogScanner.ListCatalog("EL1008", latestRevisionOnly: false, esiDirectory: _dir));
        var vInfos = root.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()).ToList();

        Assert.Equal(new string?[] { "EL1008-0000-0017", "EL1008-0000-0016" }, vInfos);
    }

    [Fact]
    public void ListCatalog_Filter_IsCaseInsensitiveSubstring()
    {
        WriteBeckhoffSample();

        var root = Json(EsiCatalogScanner.ListCatalog("l100", esiDirectory: _dir));
        var types = root.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("type").GetString()).ToList();

        Assert.Equal(new string?[] { "EL1004", "EL1008" }, types);
        var first = root.GetProperty("devices")[0];
        Assert.Equal("#x03EC3052", first.GetProperty("productCode").GetString());
        Assert.Equal("#x00120000", first.GetProperty("revisionNo").GetString());
        Assert.Equal("Beckhoff EL1xxx.xml", first.GetProperty("sourceFile").GetString());
    }

    [Fact]
    public void ListCatalog_Limit_PagesAndReportsTotal()
    {
        WriteBeckhoffSample();

        string result = EsiCatalogScanner.ListCatalog(null, limit: 2, latestRevisionOnly: false, esiDirectory: _dir);
        var root = Json(result);

        Assert.Equal(6, root.GetProperty("total").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.Equal(2, root.GetProperty("returned").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, root.GetProperty("next_offset").GetInt32());
        Assert.Equal(2, root.GetProperty("devices").GetArrayLength());
        Assert.False(root.TryGetProperty("truncated", out _));
        Assert.False(root.TryGetProperty("totalMatches", out _));
        Assert.Contains("returning 1–2 (next_offset 2)", result.Split('\n')[0]);
        Assert.Contains("Pass a filter", result.Split('\n')[0]);
    }

    [Fact]
    public void ListCatalog_PagingThroughAll_VisitsEveryDeviceOnce()
    {
        WriteBeckhoffSample();
        var everything = Json(EsiCatalogScanner.ListCatalog(null, latestRevisionOnly: false, esiDirectory: _dir))
            .GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()).ToList();

        var seen = new List<string?>();
        int? offset = 0;
        while (offset is { } o)
        {
            var root = Json(EsiCatalogScanner.ListCatalog(null, limit: 4, offset: o, latestRevisionOnly: false, esiDirectory: _dir));
            seen.AddRange(root.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("vInfo").GetString()));
            offset = root.GetProperty("next_offset").ValueKind == JsonValueKind.Null ? null : root.GetProperty("next_offset").GetInt32();
        }
        Assert.Equal(6, everything.Count);
        Assert.Equal(everything, seen);
    }

    [Fact]
    public void ListCatalog_LimitAboveMaximum_IsCutToTheMaximum()
    {
        WriteBeckhoffSample();

        var root = Json(EsiCatalogScanner.ListCatalog("EL", limit: 50_000, esiDirectory: _dir));

        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_offset").ValueKind);
        Assert.Equal(1000, EsiCatalogScanner.Page.Resolve(50_000));
        Assert.Equal(1000, EsiCatalogScanner.Page.Resolve(0));
        Assert.Equal(100, EsiCatalogScanner.Page.Resolve(null));
    }

    [Fact]
    public void ListCatalog_NegativeLimit_IsError()
        => Assert.StartsWith("[Error] limit must be 0 or more", EsiCatalogScanner.ListCatalog("EL", limit: -1, esiDirectory: _dir));

    [Fact]
    public void ListCatalog_MissingDirectory_IsError()
    {
        string result = EsiCatalogScanner.ListCatalog("EL", esiDirectory: Path.Combine(_dir, "does-not-exist"));
        Assert.StartsWith("[Error] ESI directory not found", result);
    }

    [Fact]
    public void ListCatalog_ReportsScannedAndSkippedCounts()
    {
        WriteBeckhoffSample();
        File.WriteAllText(Path.Combine(_dir, "junk.xml"), "not xml at all <");

        string result = EsiCatalogScanner.ListCatalog("ZZZ", esiDirectory: _dir);
        var root = Json(result);

        Assert.StartsWith("Found 0 EtherCAT box type(s) matching 'ZZZ'", result);
        Assert.Contains("scanned 2 ESI file(s), skipped 1 malformed", result);
        Assert.Equal(2, root.GetProperty("filesScanned").GetInt32());
        Assert.Equal(1, root.GetProperty("filesSkipped").GetInt32());
        Assert.Equal(1, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(0, root.GetProperty("devices").GetArrayLength());
    }

    // ── real ESI (optional, single file → fast) ─────────────────────────────

    [Fact]
    public void ScanFile_RealBeckhoffEk11xxEsi_ContainsEk1100_WhenInstalled()
    {
        string? dir = EsiCatalogScanner.ResolveEsiDirectory();
        string? file = dir is null ? null : Path.Combine(dir, "Beckhoff EK11xx.xml");
        if (file is null || !File.Exists(file))
            return; // no TwinCAT ESI installed on this machine — nothing to verify

        var devices = EsiCatalogScanner.ScanFile(file);

        Assert.NotNull(devices);
        var ek1100 = devices!.Where(d => d.Type == "EK1100").ToList();
        Assert.NotEmpty(ek1100);
        Assert.All(ek1100, d =>
        {
            Assert.Equal(2u, d.VendorId);
            Assert.StartsWith("EK1100-0000-", d.VInfo);
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
        });
    }
}
