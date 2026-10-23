using System.Buffers.Binary;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Synthetic EtherCAT master ADS interface keyed by (index group, index offset).</summary>
internal sealed class FakeEtherCatMaster : IAdsRawReader, IDisposable
{
    private readonly Dictionary<(uint, uint), byte[]> _values = new();
    public List<(uint Group, uint Offset)> Reads { get; } = [];
    public bool Disposed { get; private set; }

    public FakeEtherCatMaster Set(uint group, uint offset, byte[] data) { _values[(group, offset)] = data; return this; }

    private static byte[] U16(int v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); return b; }

    private static byte[] U32s(params uint[] v)
    {
        var b = new byte[v.Length * 4];
        for (int i = 0; i < v.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    public FakeEtherCatMaster WithSlaves(params (int Address, byte State, byte Link, uint Vendor, uint Product, uint Revision, uint Serial)[] slaves)
    {
        Set(0x6, 0, U16(slaves.Length));
        Set(0x7, 0, slaves.SelectMany(s => U16(s.Address)).ToArray());
        Set(0x9, 0, slaves.SelectMany(s => new[] { s.State, s.Link }).ToArray());
        foreach (var s in slaves)
        {
            Set(0x11, (uint)s.Address, U32s(s.Vendor, s.Product, s.Revision, s.Serial));
            Set(0x12, (uint)s.Address, U32s(1, 2, 3, 4));
        }
        return Set(0x3, 0x100, U16(8));
    }

    private readonly List<(uint Group, uint? Offset, uint Code, string Name)> _failures = [];

    /// <summary>Makes reads of the group (and the offset, if given) fail with an ADS error.</summary>
    public FakeEtherCatMaster Fail(uint group, uint? offset, uint code, string name) { _failures.Add((group, offset, code, name)); return this; }

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        Reads.Add((indexGroup, indexOffset));
        foreach (var f in _failures)
            if (f.Group == indexGroup && (f.Offset is null || f.Offset == indexOffset)) return AdsRawReadResult.Failure(f.Code, f.Name);
        return _values.TryGetValue((indexGroup, indexOffset), out var d)
            ? AdsRawReadResult.Success(d.Length > maxLength ? d[..maxLength] : d)
            : AdsRawReadResult.Failure(1809, "DeviceSymbolNotFound");
    }

    public void Dispose() => Disposed = true;
}

public class EtherCatIdentityTests
{
    private static EtherCatProjectedSlave Proj(string name, int? addr, long vendor = 2, long product = 0x3F03052, long rev = 0x120000) =>
        new($"TIID^Device 1^{name}", name, vendor, product, rev, addr);

    private static EtherCatMasterConfig Master(string name, string? netId, int? port, params EtherCatProjectedSlave[] slaves) =>
        new($"TIID^{name}", name, netId, port, "test", netId is null || port is null ? "<TreeItem/>" : null, slaves);

    private static EtherCatMasterIdentities Read(FakeEtherCatMaster fake, EtherCatMasterConfig master, bool crc = false,
        string? netId = null, int? port = null, List<(string, int)>? opened = null) =>
        new EtherCatIdentityReader((n, p) => { opened?.Add((n, p)); return fake; }).Read(master, netId, port, crc);

    // ---- device XML parsing ---------------------------------------------------

    [Fact]
    public void MasterAddress_FromDeviceDefElements()
    {
        var (netId, port, source) = EtherCatProjection.ParseMasterAddress(
            "<TreeItem><DeviceDef><AmsNetId>10.1.2.3.2.1</AmsNetId><AmsPort>28673</AmsPort></DeviceDef></TreeItem>");
        Assert.Equal("10.1.2.3.2.1", netId);
        Assert.Equal(0xFFFF, port);
        Assert.Contains("DeviceDef/AmsNetId", source);
    }

    [Fact]
    public void MasterAddress_FromAttributes_AndHexPort()
    {
        var (netId, port, _) = EtherCatProjection.ParseMasterAddress("<TreeItem><Device AmsNetId=\"10.1.2.3.2.1\" AmsPort=\"#x7001\"/></TreeItem>");
        Assert.Equal("10.1.2.3.2.1", netId);
        Assert.Equal(0xFFFF, port);
    }

    [Theory]
    [InlineData("<TreeItem><X/></TreeItem>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void MasterAddress_NotFound_IsNull(string xml)
    {
        var (netId, port, _) = EtherCatProjection.ParseMasterAddress(xml);
        Assert.Null(netId);
        Assert.Null(port);
    }

    [Fact]
    public void MasterAddress_RejectsAnEmptyNetId()
    {
        var (netId, _, _) = EtherCatProjection.ParseMasterAddress("<TreeItem><AmsNetId>0.0.0.0.0.0</AmsNetId></TreeItem>");
        Assert.Null(netId);
    }

    [Fact]
    public void Slave_FromInfo_ParsesIdentityAndFixedAddress()
    {
        var s = EtherCatProjection.ParseSlave(
            "<TreeItem><EtherCAT><Slave><Info><VendorId>2</VendorId><ProductCode>#x03f03052</ProductCode>" +
            "<RevisionNo>1179648</RevisionNo><PhysAddr>1002</PhysAddr></Info></Slave></EtherCAT></TreeItem>", "p", "n");
        Assert.NotNull(s);
        Assert.Equal(2, s.VendorId);
        Assert.Equal(0x03f03052, s.ProductCode);
        Assert.Equal(1179648, s.RevisionNo);
        Assert.Equal(1002, s.FixedAddress);
    }

    [Fact]
    public void Slave_WithoutInfo_IsNull_AndMissingPhysAddrIsNull()
    {
        Assert.Null(EtherCatProjection.ParseSlave("<TreeItem><EtherCAT/></TreeItem>", "p", "n"));
        var s = EtherCatProjection.ParseSlave("<TreeItem><EtherCAT><Slave><Info><VendorId>2</VendorId></Info></Slave></EtherCAT></TreeItem>", "p", "n");
        Assert.Null(s!.FixedAddress);
        Assert.Null(s.ProductCode);
    }

    // ---- bit decoding ------------------------------------------------------

    [Fact]
    public void StateAndLink_Decode()
    {
        Assert.Equal("Op", EtherCatIdentityReader.StateName(0x08));
        Assert.Equal("SafeOp", EtherCatIdentityReader.StateName(0x24));
        Assert.Equal("Unknown(0x7)", EtherCatIdentityReader.StateName(0x07));
        Assert.Equal(["linkOk"], EtherCatIdentityReader.LinkFlagNames(0));
        Assert.Equal(["linkMissing", "portB"], EtherCatIdentityReader.LinkFlagNames(0x24));
    }

    // ---- the reader ---------------------------------------------------------

    [Fact]
    public void Read_JoinsByFixedAddress_AndSetsFlags()
    {
        var fake = new FakeEtherCatMaster().WithSlaves(
            (1001, 0x08, 0x00, 2, 0x044C2C52, 0x120000, 0),
            (1002, 0x28, 0x00, 2, 0x03F03052, 0x130000, 77),
            (1003, 0x08, 0x2C, 2, 0x0C663052, 0x110000, 5));
        var master = Master("Device 1", "10.1.2.3.2.1", 28673,
            Proj("EK1100", 1001, product: 0x044C2C52),
            Proj("EL1008", 1002, product: 0x03F03052, rev: 0x120000),
            Proj("EL2008", 1003, product: 0x0C663052, rev: 0x110000));

        var r = Read(fake, master);

        Assert.Equal(EtherCatMasterStatus.Ok, r.Status);
        Assert.Equal(3, r.SlaveCount);
        Assert.Equal("Op", r.MasterStateName);
        Assert.Equal(3, r.Slaves.Count);

        var ek = r.Slaves[0];
        Assert.Equal("TIID^Device 1^EK1100", ek.TreePath);
        Assert.False(ek.SerialAvailable);
        Assert.False(ek.RevisionDiffers);
        Assert.Equal(0, ek.Online!.SerialNo);

        var el = r.Slaves[1];
        Assert.True(el.IdentityInvalid);
        Assert.True(el.RevisionDiffers);
        Assert.False(el.ProductDiffers);
        Assert.True(el.SerialAvailable);
        Assert.Equal(77, el.Online!.SerialNo);
        Assert.Equal("Op", el.StateName);

        var last = r.Slaves[2];
        Assert.True(last.Missing);
        Assert.True(last.ExtraLink);
        Assert.Equal("0x11", $"0x{last.IndexGroup:X}");
        Assert.Equal(1003u, last.IndexOffset);
        Assert.Equal("10.1.2.3.2.1", last.NetId);
        Assert.Equal(28673, last.Port);
    }

    [Fact]
    public void Read_ListsProjectedOnlyAndOnlineOnly()
    {
        var fake = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 1, 1, 9), (1005, 0x08, 0, 2, 7, 7, 9));
        var master = Master("Device 1", "10.1.2.3.2.1", 28673,
            Proj("EK1100", 1001, product: 1, rev: 1),
            Proj("Planned", 1002),
            Proj("NoAddress", null));

        var r = Read(fake, master);

        Assert.Equal(4, r.Slaves.Count);
        var onlineOnly = r.Slaves.Single(s => s.Address == 1005);
        Assert.Null(onlineOnly.Projected);
        Assert.Null(onlineOnly.TreePath);
        Assert.NotNull(onlineOnly.Online);
        Assert.Null(r.Slaves.Single(s => s.Name == "Planned").Online);
        Assert.Null(r.Slaves.Single(s => s.Name == "NoAddress").Online);
    }

    [Fact]
    public void Read_ZeroSlaves_ListsOnlyProjected()
    {
        var fake = new FakeEtherCatMaster().WithSlaves();
        var r = Read(fake, Master("Device 1", "10.1.2.3.2.1", 28673, Proj("EK1100", 1001)));
        Assert.Equal(EtherCatMasterStatus.Ok, r.Status);
        Assert.Equal(0, r.SlaveCount);
        Assert.Single(r.Slaves);
        Assert.Null(r.Slaves[0].Online);
    }

    [Fact]
    public void Read_IncludeCrc_ReadsCountersOnlyWhenAsked()
    {
        var fake = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 1, 1, 9));
        var master = Master("Device 1", "10.1.2.3.2.1", 28673);

        Assert.Null(Read(fake, master).Slaves.Single().Crc);
        Assert.DoesNotContain(fake.Reads, x => x.Group == 0x12);

        var crc = Read(fake, master, crc: true).Slaves.Single().Crc;
        Assert.Equal(new EtherCatCrc(1, 2, 3, 4), crc);
    }

    [Fact]
    public void Read_UsesTheMastersOwnAddress_AndHonoursOverrides()
    {
        var fake = new FakeEtherCatMaster().WithSlaves();
        var opened = new List<(string, int)>();
        Read(fake, Master("Device 1", "10.1.2.3.2.1", 28673), opened: opened);
        Read(fake, Master("Device 1", "10.1.2.3.2.1", 28673), netId: "1.2.3.4.2.1", port: 0xFFFF, opened: opened);
        Assert.Equal([("10.1.2.3.2.1", 28673), ("1.2.3.4.2.1", 0xFFFF)], opened);
    }

    [Fact]
    public void Read_NoAddress_IsAddressNotFound_WithSnippet_AndNoFallbackPort()
    {
        bool opened = false;
        var reader = new EtherCatIdentityReader((_, _) => { opened = true; return new FakeEtherCatMaster(); });
        var r = reader.Read(Master("Device 1", null, null, Proj("EK1100", 1001)), null, null, false);
        Assert.False(opened);
        Assert.Equal(EtherCatMasterStatus.AddressNotFound, r.Status);
        Assert.Equal("<TreeItem/>", r.RawSnippet);
        Assert.Single(r.Slaves);
    }

    [Fact]
    public void Read_OverrideCompletesAMissingAddress()
    {
        var fake = new FakeEtherCatMaster().WithSlaves();
        var r = Read(fake, Master("Device 1", null, null), netId: "1.2.3.4.2.1", port: 0xFFFF);
        Assert.Equal(EtherCatMasterStatus.Ok, r.Status);
        Assert.Null(r.RawSnippet);
    }

    [Fact]
    public void Read_MasterNotAnswering_IsMasterUnreachable_WithAdsError()
    {
        var r = Read(new FakeEtherCatMaster(), Master("Device 1", "10.1.2.3.2.1", 28673, Proj("EK1100", 1001)));
        Assert.Equal(EtherCatMasterStatus.MasterUnreachable, r.Status);
        Assert.Equal(1809u, r.AdsErrorCode);
        Assert.Single(r.Slaves);
    }

    [Fact]
    public void Read_ConnectFailure_IsMasterUnreachable()
    {
        var reader = new EtherCatIdentityReader((_, _) => throw new InvalidOperationException("no route"));
        var r = reader.Read(Master("Device 1", "10.1.2.3.2.1", 28673), null, null, false);
        Assert.Equal(EtherCatMasterStatus.MasterUnreachable, r.Status);
        Assert.Contains("no route", r.Message);
    }

    [Fact]
    public void Read_ReaderIsDisposed()
    {
        var fake = new FakeEtherCatMaster().WithSlaves();
        Read(fake, Master("Device 1", "10.1.2.3.2.1", 28673));
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void ReadAll_OneUnreachableMasterDoesNotStopTheOthers()
    {
        var good = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 1, 1, 9));
        var reader = new EtherCatIdentityReader((netId, _) => netId == "1.1.1.1.2.1" ? new FakeEtherCatMaster() : good);
        var result = reader.ReadAll([
            Master("Device 1", "1.1.1.1.2.1", 28673),
            Master("Device 2", "2.2.2.2.2.1", 28674)], null, null, false);
        Assert.Equal([EtherCatMasterStatus.MasterUnreachable, EtherCatMasterStatus.Ok], result.Masters.Select(m => m.Status).ToArray());
        Assert.False(result.Success);
    }

    // ---- coordinator ------------------------------------------------------------

    [Fact]
    public void Coordinator_OverrideWithoutMasterPath_IsAnError()
    {
        var fake = new FakeTwinCatSession();
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities(null, "1.2.3.4.2.1", null, false), StringComparison.Ordinal);
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, 28673, false), StringComparison.Ordinal);
        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadEtherCat", StringComparison.Ordinal));
    }

    [Fact]
    public void Coordinator_BadNetIdOrPort_IsAnError()
    {
        var fake = new FakeTwinCatSession();
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities("TIID^D", "nonsense", null, false), StringComparison.Ordinal);
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities("TIID^D", null, 70000, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_NoSolution_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_NoMasters_IsAStructuredStatus()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        string json = new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false);
        Assert.False(McpErrors.IsError(json));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("noEtherCatMaster", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Coordinator_ShapesPerSlaveJson()
    {
        var fakeMaster = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 1, 1, 9));
        var master = Read(fakeMaster, Master("Device 1", "10.1.2.3.2.1", 28673, Proj("EK1100", 1001, product: 1, rev: 1)));
        var fake = new FakeTwinCatSession
        {
            IsConnected = true, SolutionOpen = true,
            FakeEtherCatIdentitiesResult = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [master]),
        };

        string json = new EtherCatIdentityCoordinator(fake).ReadIdentities(" TIID^Device 1 ", null, null, true);

        Assert.Equal("ReadEtherCatIdentities(TIID^Device 1,,,True,True,True,5000)", fake.CallLog.Last());
        using var doc = JsonDocument.Parse(json);
        var slave = doc.RootElement.GetProperty("masters")[0].GetProperty("slaves")[0];
        Assert.Equal(1001, slave.GetProperty("address").GetInt32());
        Assert.Equal(9, slave.GetProperty("online").GetProperty("serialNo").GetInt32());
        Assert.True(slave.GetProperty("serialAvailable").GetBoolean());
        Assert.Equal("0x11", slave.GetProperty("source").GetProperty("indexGroup").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Coordinator_SessionException_BecomesError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ReadEtherCatIdentitiesThrows = new InvalidOperationException("Tree path not found: x") };
        Assert.StartsWith("[Error]", new EtherCatIdentityCoordinator(fake).ReadIdentities("x", null, null, false), StringComparison.Ordinal);
    }
}
