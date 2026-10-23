using System.Buffers.Binary;
using System.Text;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>CoE side of a slave as the master exposes it (port = fixed address, index group 0xF302). All values invented.</summary>
internal sealed class FakeCoeSlave : IAdsRawReader, IDisposable
{
    private readonly Dictionary<uint, byte[]> _values = new();
    private readonly (uint Code, string Name)? _failure;
    public int Reads { get; private set; }
    public bool Disposed { get; private set; }

    public FakeCoeSlave(string name, string hw, string sw, uint serial)
    {
        _values[0x10080000] = Pad(name, 24);
        _values[0x10090000] = Pad(hw, 4);
        _values[0x100A0000] = Pad(sw, 4);
        _values[0x10180004] = BitConverter.GetBytes(serial);
    }

    public FakeCoeSlave(uint code, string name) => _failure = (code, name);

    // Strings come back NUL terminated and padded with spaces.
    private static byte[] Pad(string s, int length) => Encoding.ASCII.GetBytes((s + "\0").PadRight(length, ' '));

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        Reads++;
        Assert.Equal(0xF302u, indexGroup);
        if (_failure is { } f) return AdsRawReadResult.Failure(f.Code, f.Name);
        return _values.TryGetValue(indexOffset, out var d) ? AdsRawReadResult.Success(d) : AdsRawReadResult.Failure(1809, "DeviceSymbolNotFound");
    }

    public void Dispose() => Disposed = true;
}

public class EtherCatCoeTests
{
    private const string MasterNetId = "10.20.30.40.2.1";

    // ---- EtherCAT: 28 slaves at 1001..1028 --------------------------------------

    private static bool NoMailbox(int a) => a is 1001 or 1002 or 1003 or 1004;
    private static bool Soe(int a) => a is 1021 or 1022;
    private static uint CoeSerial(int a) => a is >= 1010 and <= 1015 ? (uint)(31000 + a) : 0;

    private static (FakeEtherCatMaster Master, Dictionary<int, FakeCoeSlave> Coe, EtherCatMasterConfig Config) Build()
    {
        var master = new FakeEtherCatMaster();
        var slaves = Enumerable.Range(1001, 28)
            .Select(a => (Address: a, State: (byte)0x08, Link: (byte)0x00, Vendor: 2u, Product: (uint)(0x1000 + a), Revision: 0x00110000u,
                Serial: a == 1028 ? 777u : 0u))
            .ToArray();
        master.WithSlaves(slaves);

        var coe = new Dictionary<int, FakeCoeSlave>();
        foreach (var s in slaves)
        {
            coe[s.Address] = NoMailbox(s.Address) ? new FakeCoeSlave(1870, "PortNotConnected")
                : Soe(s.Address) ? new FakeCoeSlave(1793, "UnknownAmsCommand")
                : new FakeCoeSlave($"EL{7000 + s.Address}-9014", "09", "32", CoeSerial(s.Address));
        }

        var projected = slaves.Select(s => new EtherCatProjectedSlave($"TIID^Device 1^Box {s.Address}", $"Box {s.Address}", 2, s.Product, s.Revision, s.Address)).ToArray();
        var config = new EtherCatMasterConfig("TIID^Device 1", "Device 1", MasterNetId, 0xFFFF, "test", null, projected);
        return (master, coe, config);
    }

    private static EtherCatMasterIdentities Read(
        FakeEtherCatMaster master, Dictionary<int, FakeCoeSlave> coe, EtherCatMasterConfig config, bool includeCoe, List<(string, int)>? opened = null)
    {
        var reader = new EtherCatIdentityReader((netId, port) =>
        {
            opened?.Add((netId, port));
            return port == 0xFFFF ? master : coe[port];
        });
        return reader.Read(config, null, null, false, includeCoe);
    }

    [Fact]
    public void TwentyEightSlaves_CoeStatusesAndSerialPrecedence()
    {
        var (master, coe, config) = Build();

        var r = Read(master, coe, config, includeCoe: true);

        Assert.Equal(EtherCatMasterStatus.Ok, r.Status);
        Assert.Equal(28, r.SlaveCount);
        Assert.Equal(28, r.Slaves.Count);
        Assert.Equal("Op", r.MasterStateName);

        Assert.Equal(4, r.Slaves.Count(s => s.Coe!.Status == EtherCatCoeStatus.NoMailbox));
        Assert.Equal(2, r.Slaves.Count(s => s.Coe!.Status == EtherCatCoeStatus.NotCoe));
        Assert.Equal(22, r.Slaves.Count(s => s.Coe!.Status == EtherCatCoeStatus.Ok));

        var noMailbox = r.Slaves.Single(s => s.Address == 1001);
        Assert.Null(noMailbox.Coe!.DeviceName);
        Assert.Contains("PortNotConnected", noMailbox.Coe.AdsError, StringComparison.Ordinal);
        Assert.False(noMailbox.SerialAvailable);
        Assert.Null(noMailbox.SerialSource);

        var ok = r.Slaves.Single(s => s.Address == 1012);
        Assert.Equal("EL8012-9014", ok.Coe!.DeviceName);
        Assert.Equal("09", ok.Coe.HardwareVersion);
        Assert.Equal("32", ok.Coe.FirmwareVersion);
        Assert.Equal(32012, ok.Coe.SerialNo);
        Assert.Equal("32012", ok.SerialNo);
        Assert.Equal(EtherCatSerialSource.Coe, ok.SerialSource);
        Assert.True(ok.SerialAvailable);
        Assert.Equal(0, ok.Online!.SerialNo);

        var terminalWithoutSerial = r.Slaves.Single(s => s.Address == 1020);
        Assert.Equal(EtherCatCoeStatus.Ok, terminalWithoutSerial.Coe!.Status);
        Assert.Equal(0, terminalWithoutSerial.Coe.SerialNo);
        Assert.False(terminalWithoutSerial.SerialAvailable);
        Assert.Null(terminalWithoutSerial.SerialSource);

        var drive = r.Slaves.Single(s => s.Address == 1021);
        Assert.Equal(EtherCatCoeStatus.NotCoe, drive.Coe!.Status);

        var masterSerial = r.Slaves.Single(s => s.Address == 1028);
        Assert.Equal("777", masterSerial.SerialNo);
        Assert.Equal(EtherCatSerialSource.Master, masterSerial.SerialSource);
        Assert.Equal(coe[1028].Reads > 0, masterSerial.Coe is not null);
    }

    [Fact]
    public void IncludeCoeFalse_OpensOnlyTheMasterAndLeavesCoeNull()
    {
        var (master, coe, config) = Build();
        var opened = new List<(string, int)>();

        var r = Read(master, coe, config, includeCoe: false, opened);

        Assert.Equal([(MasterNetId, 0xFFFF)], opened);
        Assert.All(r.Slaves, s => Assert.Null(s.Coe));
        Assert.All(coe.Values, c => Assert.Equal(0, c.Reads));
    }

    [Fact]
    public void Coe_UsesMasterNetIdAndTheFixedAddressAsPort_AndDisposesEachConnection()
    {
        var (master, coe, config) = Build();
        var opened = new List<(string, int)>();

        Read(master, coe, config, includeCoe: true, opened);

        Assert.Equal((MasterNetId, 0xFFFF), opened[0]);
        Assert.Equal(Enumerable.Range(1001, 28).Select(a => (MasterNetId, a)).ToArray(), opened.Skip(1).ToArray());
        Assert.All(coe.Values, c => Assert.True(c.Disposed));
    }

    [Fact]
    public void Coe_NoMailboxStopsAfterOneRead_OkReadsFour()
    {
        var (master, coe, config) = Build();
        Read(master, coe, config, includeCoe: true);
        Assert.Equal(1, coe[1001].Reads);
        Assert.Equal(1, coe[1021].Reads);
        Assert.Equal(4, coe[1012].Reads);
    }

    [Fact]
    public void Coe_OtherErrorsAreStatusError_AndConnectFailureToo()
    {
        var master = new FakeEtherCatMaster().WithSlaves((1001, 0x08, 0, 2, 1, 1, 0), (1002, 0x08, 0, 2, 2, 1, 0));
        var config = new EtherCatMasterConfig("TIID^D", "D", MasterNetId, 0xFFFF, "test", null, []);
        var reader = new EtherCatIdentityReader((_, port) =>
        {
            if (port == 0xFFFF) return master;
            if (port == 1001) return new FakeCoeSlave(1792, "SomethingElse");
            throw new InvalidOperationException("no route");
        });

        var r = reader.Read(config, null, null, false, includeCoe: true);

        Assert.Equal(EtherCatCoeStatus.Error, r.Slaves.Single(s => s.Address == 1001).Coe!.Status);
        var failed = r.Slaves.Single(s => s.Address == 1002).Coe!;
        Assert.Equal(EtherCatCoeStatus.Error, failed.Status);
        Assert.Contains("no route", failed.AdsError, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectedBoxWithoutOnlineAddress_GetsNoCoeRead()
    {
        var master = new FakeEtherCatMaster().WithSlaves();
        var config = new EtherCatMasterConfig("TIID^D", "D", MasterNetId, 0xFFFF, "test", null,
            [new EtherCatProjectedSlave("TIID^D^B", "B", 2, 1, 1, 1001)]);
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : throw new InvalidOperationException("must not open"));
        var r = reader.Read(config, null, null, false, includeCoe: true);
        Assert.Null(r.Slaves.Single().Coe);
    }

    // ---- MDP: 25 modules ---------------------------------------------------------

    [Fact]
    public void Mdp_TwentyFiveModules_CountsTypesAndUnknowns()
    {
        var dev = new FakeMdpDevice().WithDevice("BTN-0001", "BIC-0001");
        int id = 1;
        dev.AddModule(0x0008, id, "TwinCAT")
            .Table(id, 1, FakeMdpDevice.U16(3), FakeMdpDevice.U16(1), FakeMdpDevice.U16(4026), FakeMdpDevice.Str("10.20.30.40.1.1"),
                FakeMdpDevice.U32(0), FakeMdpDevice.U16(5), FakeMdpDevice.U16(0), FakeMdpDevice.U16(0), FakeMdpDevice.U32(0),
                FakeMdpDevice.Str(""), FakeMdpDevice.Str("0000-AAAA"), FakeMdpDevice.U16(22), FakeMdpDevice.U64(5));
        id++;
        dev.AddModule(0x0018, id, "OS").Table(id, 1, FakeMdpDevice.U32(10), FakeMdpDevice.U32(0), FakeMdpDevice.U32(19045), FakeMdpDevice.Str(""), FakeMdpDevice.Str("10.0.19045"));
        id++;
        dev.AddModule(0x001C, id, "Mainboard").Table(id, 1, FakeMdpDevice.Str("CB0000"), FakeMdpDevice.Str("MB-0001"), FakeMdpDevice.Str("2024-01-01"));
        id++;
        dev.AddModule(0x000B, id, "CPU").Table(id, 1, FakeMdpDevice.U32(1900), FakeMdpDevice.U16(5), FakeMdpDevice.B(0x28, 0x00));
        id++;
        dev.AddModule(0x000C, id, "Memory").Table(id, 1, FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U32(0), FakeMdpDevice.U64(4UL << 30), FakeMdpDevice.U64(2UL << 30));
        id++;
        dev.AddNic(id++, "02:00:00:00:00:01", "192.0.2.10", false, 1);
        dev.AddNic(id++, "02:00:00:00:00:02", "192.0.2.11", true, 0);
        for (int i = 1; i <= 3; i++)
            dev.AddModule(0x001B, id, $"Fan {i}").Table(id++, 1, FakeMdpDevice.U32((uint)(1000 * i)));
        for (int i = 1; i <= 3; i++)
            dev.AddModule(0x0013, id, $"Display {i}").Table(id++, 1, FakeMdpDevice.B((byte)i));
        dev.AddDisks(id++, ("C:", "System", "NTFS", 1, 100, 40));
        dev.AddModule(0x000A, id, "Software")
            .Table(id, 1, FakeMdpDevice.Str("Product A"), FakeMdpDevice.Str("Product B"), FakeMdpDevice.Str("Product C"))
            .Table(id, 2, FakeMdpDevice.Str("Vendor"), FakeMdpDevice.Str("Vendor"), FakeMdpDevice.Str("Vendor"))
            .Table(id, 3, FakeMdpDevice.Str("2026-01-01"), FakeMdpDevice.Str("2026-01-02"), FakeMdpDevice.Str("2026-01-03"))
            .Table(id, 4, FakeMdpDevice.Str("1.0"), FakeMdpDevice.Str("2.0"), FakeMdpDevice.Str("3.0"));
        id++;
        foreach (int type in new[] { 0x03, 0x04, 0x07, 0x10, 0x100, 0x01, 0x1E, 0x1F, 0x20, 0x21 })
            dev.AddModule(type, id, $"Module {type:X}").Table(id++, 1, FakeMdpDevice.B(1, 2));

        var r = new MdpInventoryReader(dev).Read("10.20.30.40.1.1", null, true);

        Assert.Equal(IpcInventoryStatus.Ok, r.Status);
        Assert.Equal(25, r.Modules.Count);
        Assert.Empty(r.Warnings.Where(w => w.Contains("exceeds", StringComparison.Ordinal)));
        Assert.Equal(2, r.Modules.Count(m => m.TypeName == "NIC"));
        Assert.Equal(3, r.Modules.Count(m => m.TypeName == "Fan"));
        Assert.Equal(3, r.Modules.Count(m => m.TypeName == "Display"));
        Assert.Equal(10, r.Modules.Count(m => m.TypeName == "unknown"));
        Assert.All(r.Modules.Where(m => m.TypeName is "Fan" or "Display" or "unknown"), m => Assert.Null(m.Decoded));
        Assert.Contains(r.Modules, m => m.ModuleType == 0x100);

        var raw = r.Modules.Single(m => m.ModuleType == 0x21).Tables.Single(t => t.TableIndex == MdpAddress.ConfigTable(m_id(r, 0x21), 1)).Entries.Single(e => e.SubIndex == 1);
        Assert.Equal("0102", raw.Raw);

        Assert.Equal("3.1.4026.22", r.Modules.Single(m => m.TypeName == "TwinCAT").Decoded!["version"]);
        var disks = (List<Dictionary<string, object?>>)r.Modules.Single(m => m.TypeName == "DiskManagement").Decoded!["disks"]!;
        Assert.Single(disks);
        var software = (List<Dictionary<string, object?>>)r.Modules.Single(m => m.TypeName == "Software").Decoded!["software"]!;
        Assert.Equal(3, software.Count);
        Assert.Equal("Product C", software[2]["name"]);
    }

    private static int m_id(IpcInventoryResult r, int type) => r.Modules.Single(m => m.ModuleType == type).ModuleId;
}
