using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Joining projected boxes to online slaves (by address, by order), ADS error codes of exceptions, repository listing.</summary>
public class EtherCatJoinAndErrorCodeTests
{
    private const string NetId = "10.20.30.40.2.1";

    private enum FakeAdsCode : uint { ClientSyncTimeOut = 1861, TargetPortNotFound = 6 }

    // Shaped like TwinCAT.Ads.AdsErrorException: the code is an ErrorCode property.
    private sealed class FakeAdsException(FakeAdsCode code) : Exception($"Ads Error: {(uint)code} : '{code}'")
    {
        public FakeAdsCode ErrorCode { get; } = code;
    }

    // ---- EtherCAT join by order ---------------------------------------------------------

    private static EtherCatProjectedSlave Box(string name, int? address, long product, long revision = 1) =>
        new($"TIID^D^{name}", name, 2, product, revision, address);

    private static EtherCatMasterIdentities Read(FakeEtherCatMaster master, params EtherCatProjectedSlave[] boxes) =>
        new EtherCatIdentityReader((_, _) => master).Read(
            new EtherCatMasterConfig("TIID^D", "D", NetId, 0xFFFF, "test", null, boxes), null, null, false);

    [Fact]
    public void Join_ByAddress_SaysSo()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0));
        var r = Read(master, Box("A", 1001, 11));
        Assert.Equal("address", r.Slaves.Single().JoinedBy);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Join_NoProjectedAddresses_FallsBackToOrder_WithWarning()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 5), (1002, 0x08, 0, 2, 12, 1, 6), (1003, 0x08, 0, 2, 13, 1, 7));
        var r = Read(master, Box("A", null, 11), Box("B", null, 12), Box("C", null, 13));

        Assert.Equal(3, r.Slaves.Count);
        Assert.All(r.Slaves, s => Assert.Equal("order", s.JoinedBy));
        Assert.Equal(["A", "B", "C"], r.Slaves.Select(s => s.Name).ToArray());
        Assert.Equal([1001, 1002, 1003], r.Slaves.Select(s => s.Address).ToArray());
        Assert.All(r.Slaves, s => { Assert.False(s.ProductDiffers); Assert.False(s.RevisionDiffers); });
        Assert.Contains(r.Warnings, w => w.Contains("joined by order", StringComparison.Ordinal));
    }

    [Fact]
    public void Join_ByOrder_StillFlagsAWrongOrder()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0), (1002, 0x08, 0, 2, 12, 2, 0));
        var r = Read(master, Box("A", null, 12), Box("B", null, 12, revision: 1));

        Assert.True(r.Slaves[0].ProductDiffers);
        Assert.False(r.Slaves[1].ProductDiffers);
        Assert.True(r.Slaves[1].RevisionDiffers);
    }

    [Fact]
    public void Join_ByOrder_ListsSurplusBoxesAndSurplusSlaves_AndWarnsAboutTheCounts()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0), (1002, 0x08, 0, 2, 12, 1, 0));
        var more = Read(master, Box("A", null, 11), Box("B", null, 12), Box("C", null, 13));
        Assert.Equal(3, more.Slaves.Count);
        Assert.Null(more.Slaves.Single(s => s.Name == "C").Online);
        Assert.Contains(more.Warnings, w => w.Contains("unreliable", StringComparison.Ordinal));

        var fewer = Read(master, Box("A", null, 11));
        Assert.Equal(2, fewer.Slaves.Count);
        Assert.Null(fewer.Slaves.Single(s => s.Address == 1002).Projected);
        Assert.Null(fewer.Slaves.Single(s => s.Address == 1002).JoinedBy);
    }

    [Fact]
    public void Join_AddressesThatMatchNothing_AlsoFallBackToOrder()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0));
        var r = Read(master, Box("A", 7, 11));
        Assert.Equal("order", r.Slaves.Single().JoinedBy);
    }

    [Fact]
    public void Join_OneMatchingAddress_KeepsTheAddressJoin()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0), (1002, 0x08, 0, 2, 12, 1, 0));
        var r = Read(master, Box("A", 1001, 11), Box("B", null, 12));
        Assert.Equal("address", r.Slaves.Single(s => s.Address == 1001).JoinedBy);
        Assert.Null(r.Slaves.Single(s => s.Address == 1002).Projected);
    }

    [Fact]
    public void ProjectedPhysAddrZero_IsNoAddress()
    {
        var s = EtherCatProjection.ParseSlave(
            "<TreeItem><EtherCAT><Slave><Info><VendorId>2</VendorId><PhysAddr>0</PhysAddr></Info></Slave></EtherCAT></TreeItem>", "p", "n");
        Assert.Null(s!.FixedAddress);
    }

    [Fact]
    public void Json_CarriesJoinedBy()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 11, 1, 0));
        var result = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [Read(master, Box("A", null, 11))]);
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, FakeEtherCatIdentitiesResult = result };

        using var doc = JsonDocument.Parse(new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false));

        Assert.Equal("order", doc.RootElement.GetProperty("masters")[0].GetProperty("slaves")[0].GetProperty("joinedBy").GetString());
    }

    // ---- IPC inventory: ADS error of an exception ----------------------------------------

    private static IpcInventoryResult Inventory(Exception thrown) =>
        new MdpInventoryReader(new FakeMdpDevice { ThrowOnRead = thrown }).Read("10.0.0.1.1.1", null, true);

    [Fact]
    public void Exception_WithTimeoutCode_IsTargetUnreachable_WithCodeAndName()
    {
        var r = Inventory(new FakeAdsException(FakeAdsCode.ClientSyncTimeOut));
        Assert.Equal(IpcInventoryStatus.TargetUnreachable, r.Status);
        Assert.Equal(1861u, r.AdsErrorCode);
        Assert.Equal("ClientSyncTimeOut", r.AdsErrorName);
    }

    [Fact]
    public void Exception_WithAnotherCode_IsMdpUnavailable_WithCodeAndName()
    {
        var r = Inventory(new InvalidOperationException("wrapper", new FakeAdsException(FakeAdsCode.TargetPortNotFound)));
        Assert.Equal(IpcInventoryStatus.MdpUnavailable, r.Status);
        Assert.Equal(6u, r.AdsErrorCode);
        Assert.Equal("TargetPortNotFound", r.AdsErrorName);
    }

    [Fact]
    public void Exception_WithoutCode_StaysUnreachable_WithoutCode()
    {
        var r = Inventory(new InvalidOperationException("no route"));
        Assert.Equal(IpcInventoryStatus.TargetUnreachable, r.Status);
        Assert.Null(r.AdsErrorCode);
    }

    [Fact]
    public void FailedReads_AlwaysCarryCodeAndName_InTheJson()
    {
        foreach (uint code in new[] { 7u, 1861u, 1792u })
        {
            var r = new MdpInventoryReader(new FakeMdpDevice { FailAllWith = code }).Read("10.0.0.1.1.1", null, true);
            Assert.False(r.Success);
            Assert.Equal(code, r.AdsErrorCode);
            Assert.False(string.IsNullOrEmpty(r.AdsErrorName));

            using var doc = JsonDocument.Parse(IpcInventoryCoordinator.Format(r));
            Assert.Equal(code, doc.RootElement.GetProperty("adsErrorCode").GetUInt32());
            Assert.Equal(code, doc.RootElement.GetProperty("adsError").GetProperty("code").GetUInt32());
            Assert.Equal(r.AdsErrorName, doc.RootElement.GetProperty("adsErrorName").GetString());
        }
    }

    // ---- library repositories: source and warnings -----------------------------------------

    [Fact]
    public void Repositories_ReportTheirSource_AndPassTheWarningsOn()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeLibraryRepositories.Add(new LibraryRepositoryEntry("System", Path.GetTempPath(), LibraryRepositorySource.FilesystemFallback));
        fake.FakeLibraryRepositoryWarnings.Add("fell back to the file system");

        using var doc = JsonDocument.Parse(new LibraryRepositoryCoordinator(fake).List(false, null));

        Assert.Equal("filesystem-fallback", doc.RootElement.GetProperty("repositories")[0].GetProperty("source").GetString());
        Assert.Equal("fell back to the file system", doc.RootElement.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public void Repositories_DefaultSourceIsCom()
    {
        Assert.Equal("com", new LibraryRepositoryEntry("System", "C:\\x").Source);
    }
}
