using System.Text;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Plain UINT16 IDNs of an AX5000 and one top-level serial per safety device. All values invented.</summary>
public class SoeSafetyIdentifierTests
{
    private static EtherCatMasterIdentities Read(Dictionary<int, FakeDeviceSlave> slaves)
    {
        var master = new FakeEtherCatMaster().WithSlaves(slaves.Keys.Order().Select(a => (a, (byte)0x08, (byte)0x00, 2u, (uint)a, 1u, 0u)).ToArray());
        var boxes = slaves.Keys.Order().Select(a => new EtherCatProjectedSlave($"TIID^D^B{a}", $"Box {a}", 2, a, 1, a)).ToArray();
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : slaves[port]);
        return reader.Read(new EtherCatMasterConfig("TIID^D", "D", "10.20.30.40.2.1", 0xFFFF, "test", null, boxes), null, null, false, true, true);
    }

    // ---- P-0-2000 is a plain UINT16 ---------------------------------------------------------------

    private sealed class Raw(AdsRawReadResult result) : IAdsRawReader
    {
        public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength) => result;
    }

    [Fact]
    public void SoeUInt16_ReadsThePlainTwoByteValue()
    {
        var errors = new List<string>();
        Assert.Equal(2, EtherCatDeviceDetailReader.SoeUInt16(new Raw(AdsRawReadResult.Success([0x02, 0x00])), 0, 0x8000 | 2000, "P-0-2000", errors));
        Assert.Empty(errors);
    }

    [Fact]
    public void SoeUInt16_AcceptsAListWrappedAnswer_AndReportsOtherSizes()
    {
        var errors = new List<string>();
        Assert.Equal(3, EtherCatDeviceDetailReader.SoeUInt16(new Raw(AdsRawReadResult.Success(FakeDeviceSlave.SoeList([3, 0]))), 0, 1, "P", errors));
        Assert.Null(EtherCatDeviceDetailReader.SoeUInt16(new Raw(AdsRawReadResult.Success([1])), 0, 1, "P-0-2000", errors));
        Assert.Null(EtherCatDeviceDetailReader.SoeUInt16(new Raw(AdsRawReadResult.Failure(1809, "Nope")), 0, 1, "P-0-2000", errors));
        Assert.Equal(2, errors.Count);
        Assert.StartsWith("P-0-2000", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Channel_DecodesThePlainSafetyOption_AndTheHostDriveShowsIt()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000001111", 2, [0, 0, 0, 0]),
            [1002] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 777),
        };

        var r = Read(slaves);

        var channel = r.Slaves.Single(s => s.Address == 1001).Details!.Soe!.Single();
        Assert.Equal(2, channel.SafetyOption);
        Assert.Equal("AX5805/AX5806", channel.SafetyOptionName);
        Assert.Empty(channel.Errors);
        Assert.Equal(2, r.Slaves.Single(s => s.Address == 1002).Details!.Safety!.HostDrive!.ConfiguredSafetyOption);
    }

    [Fact]
    public void ListStyleIdn_P0054_StillDecodes()
    {
        var drive = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000001111", 0, [0, 0, 0, 0]);
        var channel = Read(new() { [1001] = drive }).Slaves.Single().Details!.Soe!.Single();
        Assert.Equal("AX5000-TEST-####", channel.ConfiguredType);
    }

    // ---- one top-level serial per safety device ---------------------------------------------------------------------

    private static Dictionary<int, FakeDeviceSlave> Machine() => new()
    {
        [1001] = new FakeDeviceSlave().Terminal("EL1918", 0).CoeU16(0xF980, 1, 11).CoeU32(0xF980, 2, 900001),
        [1002] = new FakeDeviceSlave().Terminal("EL2904", 0).CoeU16(0xF980, 1, 12).CoeU32(0xF980, 2, 0)
            .CoeText(0x10E2, 1, "1P222222SBTNbtn22222" + new string(' ', 4) + "2P0001", 141),
        [1003] = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000003333", 2, [0, 0, 0, 0]),
        [1004] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        [1005] = new FakeDeviceSlave().Terminal("EL6910", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        [1006] = new FakeDeviceSlave().Terminal("EL1904", 4711).CoeU16(0xF980, 1, 13).CoeU32(0xF980, 2, 0),
    };

    [Fact]
    public void SafetyDevice_WithoutAnotherSerial_TakesTheEffectiveIdentifierAtTheTopLevel()
    {
        var r = Read(Machine());

        var f980 = r.Slaves.Single(s => s.Address == 1001);
        Assert.Equal("900001", f980.SerialNo);
        Assert.Equal("coe:0xF980:02", f980.SerialSource);
        Assert.True(f980.SerialAvailable);

        var bic = r.Slaves.Single(s => s.Address == 1002);
        Assert.Equal("btn22222", bic.SerialNo);
        Assert.Equal("bic:BTN", bic.SerialSource);

        var host = r.Slaves.Single(s => s.Address == 1004);
        Assert.Equal("000003333", host.SerialNo);
        Assert.Equal("hostDrive:S-0-0432", host.SerialSource);
        Assert.True(host.SerialAvailable);
    }

    [Fact]
    public void ExistingSerials_AreNotReplaced_AndMissingStaysMissing()
    {
        var r = Read(Machine());

        var coe = r.Slaves.Single(s => s.Address == 1006);
        Assert.Equal("4711", coe.SerialNo);
        Assert.Equal(EtherCatSerialSource.Coe, coe.SerialSource);

        var drive = r.Slaves.Single(s => s.Address == 1003);
        Assert.Equal("000003333", drive.SerialNo);
        Assert.Equal(EtherCatSerialSource.Soe, drive.SerialSource);

        var missing = r.Slaves.Single(s => s.Address == 1005);
        Assert.True(missing.SafetySerialMissing);
        Assert.Null(missing.SerialNo);
        Assert.Null(missing.SerialSource);
        Assert.False(missing.SerialAvailable);
    }

    [Fact]
    public void NonSafetyDevice_KeepsItsAbsentSerial()
    {
        var slave = Read(new() { [1001] = new FakeDeviceSlave().Terminal("EL1008", 0) }).Slaves.Single();
        Assert.Null(slave.SerialNo);
        Assert.False(slave.SerialAvailable);
    }

    [Fact]
    public void SerialText_FromTheBtnIsKeptVerbatim()
    {
        string bic = "1P1SBTN" + "0abc0def" + "2P9";
        Assert.Equal("0abc0def", EtherCatDeviceParsing.ParseBic(bic).Btn);
        Assert.Equal(8, Encoding.ASCII.GetBytes(EtherCatDeviceParsing.ParseBic(bic).Btn!).Length);
    }
}
