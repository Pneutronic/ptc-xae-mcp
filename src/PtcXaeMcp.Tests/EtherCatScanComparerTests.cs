using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class EtherCatScanComparerTests
{
    private static EtherCatScanBox Box(int pos, string name, long product, long revision = 1, long vendor = 2) =>
        new(pos, name, vendor, product, revision);

    private static readonly EtherCatScanBox Ek1100 = Box(1, "Term 1 (EK1100)", 0x044C2C52);
    private static readonly EtherCatScanBox El1008 = Box(2, "Term 2 (EL1008)", 0x03F03052);
    private static readonly EtherCatScanBox El2008 = Box(3, "Term 3 (EL2008)", 0x07D83052);

    [Fact]
    public void Identical_HasNoDifferences()
    {
        var r = EtherCatScanComparer.Compare([Ek1100, El1008], [Ek1100, El1008]);
        Assert.True(r.Identical);
        Assert.Empty(r.Differences);
    }

    [Fact]
    public void BothEmpty_IsIdentical() =>
        Assert.True(EtherCatScanComparer.Compare([], []).Identical);

    [Fact]
    public void NamesAreNotCompared()
    {
        var renamed = El1008 with { Name = "Box 9" };
        Assert.True(EtherCatScanComparer.Compare([Ek1100, El1008], [Ek1100, renamed]).Identical);
    }

    [Fact]
    public void MissingAtTheEnd_IsOneMissing()
    {
        var r = EtherCatScanComparer.Compare([Ek1100, El1008, El2008], [Ek1100, El1008]);
        var d = Assert.Single(r.Differences);
        Assert.Equal("missing", d.Kind);
        Assert.Equal(El2008, d.Configured);
        Assert.Null(d.Scanned);
        Assert.Equal(3, d.Position);
    }

    [Fact]
    public void ExtraAtTheEnd_IsOneExtra()
    {
        var r = EtherCatScanComparer.Compare([Ek1100], [Ek1100, El1008]);
        var d = Assert.Single(r.Differences);
        Assert.Equal("extra", d.Kind);
        Assert.Equal(El1008, d.Scanned);
        Assert.Null(d.Configured);
    }

    [Fact]
    public void BoxInsertedInTheMiddle_IsOneExtra_NotManyDifferent()
    {
        var scanned = new[] { Ek1100, Box(2, "new", 0x0BB83052), El1008 with { Position = 3 }, El2008 with { Position = 4 } };
        var r = EtherCatScanComparer.Compare([Ek1100, El1008, El2008], scanned);
        var d = Assert.Single(r.Differences);
        Assert.Equal("extra", d.Kind);
        Assert.Equal(2, d.Position);
    }

    [Fact]
    public void BoxPulledFromTheMiddle_IsOneMissing()
    {
        var scanned = new[] { Ek1100, El2008 with { Position = 2 } };
        var r = EtherCatScanComparer.Compare([Ek1100, El1008, El2008], scanned);
        var d = Assert.Single(r.Differences);
        Assert.Equal("missing", d.Kind);
        Assert.Equal(El1008, d.Configured);
        Assert.Equal(2, d.Position);
    }

    [Fact]
    public void OtherProductAtTheSamePlace_IsDifferentProduct()
    {
        var scanned = new[] { Ek1100, Box(2, "Term 2 (EL1018)", 0x03FA3052), El2008 };
        var r = EtherCatScanComparer.Compare([Ek1100, El1008, El2008], scanned);
        var d = Assert.Single(r.Differences);
        Assert.Equal("different_product", d.Kind);
        Assert.Equal(El1008, d.Configured);
        Assert.Equal(0x03FA3052, d.Scanned!.ProductCode);
    }

    [Fact]
    public void OtherVendorSameProductCode_IsDifferentProduct()
    {
        var scanned = new[] { Ek1100, El1008 with { VendorId = 99 } };
        var r = EtherCatScanComparer.Compare([Ek1100, El1008], scanned);
        Assert.Equal("different_product", Assert.Single(r.Differences).Kind);
    }

    [Fact]
    public void OtherRevision_IsDifferentRevision()
    {
        var scanned = new[] { Ek1100, El1008 with { RevisionNo = 0x00120000 } };
        var r = EtherCatScanComparer.Compare([Ek1100, El1008], scanned);
        var d = Assert.Single(r.Differences);
        Assert.Equal("different_revision", d.Kind);
        Assert.Equal(1, d.Configured!.RevisionNo);
        Assert.Equal(0x00120000, d.Scanned!.RevisionNo);
        Assert.False(r.Identical);
    }

    [Fact]
    public void TwoReplacedAndOneMissing_PairsInOrder()
    {
        var cfg = new[] { Ek1100, El1008, El2008 };
        var scn = new[] { Ek1100, Box(2, "a", 100) };
        var r = EtherCatScanComparer.Compare(cfg, scn);
        Assert.Equal(["different_product", "missing"], r.Differences.Select(d => d.Kind).ToArray());
    }

    [Fact]
    public void BoxWithoutIdentity_IsComparedByName()
    {
        var a = new EtherCatScanBox(1, "Box 1", null, null, null);
        Assert.True(EtherCatScanComparer.Compare([a], [a with { Position = 1 }]).Identical);
        Assert.False(EtherCatScanComparer.Compare([a], [a with { Name = "Box 2" }]).Identical);
    }
}

public class EtherCatScanXmlTests
{
    private const string TargetXml =
        "<TreeItem><ItemName>Device 1 (EtherCAT)</ItemName><DeviceDef><AddressInfo><Pnp><DeviceDesc>Intel(R) Ethernet</DeviceDesc>" +
        "<DeviceName>\\DEVICE\\{ABC}</DeviceName><DevicePath>\\\\?\\pci#abc</DevicePath></Pnp></AddressInfo><AmsNetId>1.2.3.4.5.1</AmsNetId></DeviceDef></TreeItem>";

    [Fact]
    public void ExtractAddressInfo_ReturnsTheAdapterElement()
    {
        var (xml, error) = EtherCatScanXml.ExtractAddressInfo(TargetXml);
        Assert.Null(error);
        Assert.Contains("<DeviceDesc>Intel(R) Ethernet</DeviceDesc>", xml);
        Assert.StartsWith("<AddressInfo", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<TreeItem><DeviceDef/></TreeItem>")]
    [InlineData("<TreeItem><DeviceDef><AddressInfo/></DeviceDef></TreeItem>")]
    [InlineData("<TreeItem><DeviceDef><AddressInfo><Pnp/></AddressInfo></DeviceDef></TreeItem>")]
    // As the XAE writes it for a master without an adapter (captured on the neutral fixture).
    [InlineData("<TreeItem><DeviceDef><AddressInfo><Pnp><DeviceDesc></DeviceDesc><DeviceName></DeviceName><DeviceData>000000000000</DeviceData></Pnp></AddressInfo></DeviceDef></TreeItem>")]
    [InlineData("")]
    public void ExtractAddressInfo_NoAdapter_IsAnErrorNamingIt(string xml)
    {
        var (info, error) = EtherCatScanXml.ExtractAddressInfo(xml);
        Assert.Null(info);
        Assert.Contains("no network adapter", error);
    }

    [Fact]
    public void BuildScanXml_CarriesAdapterAndScanFlag()
    {
        var (info, _) = EtherCatScanXml.ExtractAddressInfo(TargetXml);
        string xml = EtherCatScanXml.BuildScanXml(info!);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Equal("true", doc.Descendants("ScanBoxes").Single().Value);
        Assert.Single(doc.Descendants("AddressInfo"));
        Assert.Equal("TreeItem", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void TemporaryName_IsUniqueAndRecognisable()
    {
        string a = EtherCatScanXml.TemporaryDeviceName();
        string b = EtherCatScanXml.TemporaryDeviceName();
        Assert.NotEqual(a, b);
        Assert.StartsWith("PtcScanTmp_", a, StringComparison.Ordinal);
    }
}
