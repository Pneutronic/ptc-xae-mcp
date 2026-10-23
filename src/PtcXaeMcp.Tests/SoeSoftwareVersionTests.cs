using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>AX5000 software versions (P-0-0320) and the host drive of an AX5805/AX5806. All values invented.</summary>
public class SoeSoftwareVersionTests
{
    private static EtherCatMasterIdentities Read(Dictionary<int, FakeDeviceSlave> slaves)
    {
        var master = new FakeEtherCatMaster().WithSlaves(slaves.Keys.Order().Select(a => (a, (byte)0x08, (byte)0x00, 2u, (uint)a, 1u, 0u)).ToArray());
        var boxes = slaves.Keys.Order().Select(a => new EtherCatProjectedSlave($"TIID^D^B{a}", $"Box {a}", 2, a, 1, a)).ToArray();
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : slaves[port]);
        return reader.Read(new EtherCatMasterConfig("TIID^D", "D", "10.20.30.40.2.1", 0xFFFF, "test", null, boxes), null, null, false, true, true);
    }

    // ---- P-0-0320 -----------------------------------------------------------------------------

    [Fact]
    public void SoftwareVersions_DecodeTwoDecimalsAndBuilds()
    {
        // D8 00 01 00 CA 00 02 00 ...: firmware 2.16 build 1, bootloader 2.02 build 2
        var v = EtherCatDeviceParsing.ParseSoftwareVersions(FakeDeviceSlave.Versions(216, 1, 202, 2, 0, 0, 0));

        Assert.Equal("2.16", v!.Firmware);
        Assert.Equal(1, v.FirmwareBuild);
        Assert.Equal("2.02", v.Bootloader);
        Assert.Equal(2, v.BootloaderBuild);
        Assert.Null(v.OptionCardFirmware);
        Assert.Null(v.OptionCardFirmwareBuild);
        Assert.Null(v.OptionCardInterface);
    }

    [Fact]
    public void SoftwareVersions_OptionCardFields_AreDecoded()
    {
        var v = EtherCatDeviceParsing.ParseSoftwareVersions(FakeDeviceSlave.Versions(300, 12, 150, 3, 105, 7, 3));
        Assert.Equal("3.00", v!.Firmware);
        Assert.Equal("1.50", v.Bootloader);
        Assert.Equal("1.05", v.OptionCardFirmware);
        Assert.Equal(7, v.OptionCardFirmwareBuild);
        Assert.Equal(3, v.OptionCardInterface);
    }

    [Fact]
    public void SoftwareVersions_ShortAnswers()
    {
        Assert.Null(EtherCatDeviceParsing.ParseSoftwareVersions([1, 0, 2, 0]));
        var eight = EtherCatDeviceParsing.ParseSoftwareVersions([216, 0, 1, 0, 202, 0, 2, 0]);
        Assert.Equal("2.16", eight!.Firmware);
        Assert.Null(eight.OptionCardFirmware);
    }

    [Theory]
    [InlineData(216, "2.16")]
    [InlineData(100, "1.00")]
    [InlineData(9, "0.09")]
    public void TwoDecimals(int value, string expected) => Assert.Equal(expected, EtherCatDeviceParsing.TwoDecimals(value));

    [Fact]
    public void Channel_CarriesTheSoftwareVersions()
    {
        var drive = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000001111", 0, [0, 0, 0, 0])
            .Soe(0, 0x8000 | 320, FakeDeviceSlave.Versions(216, 1, 202, 2, 0, 0, 0));
        var channel = Read(new() { [1001] = drive }).Slaves.Single().Details!.Soe!.Single();
        Assert.Equal("2.16", channel.SoftwareVersions!.Firmware);
        Assert.Equal("2.02", channel.SoftwareVersions.Bootloader);
        Assert.Empty(channel.Errors);
    }

    // ---- host drive of an AX5805/AX5806 ---------------------------------------------------------------------

    private static FakeDeviceSlave Ax5206() => new FakeDeviceSlave("UnknownAmsCommand")
        .Channel(0, "000001111", 2, [0, 0, 0, 0])
        .Soe(0, 0x8000 | 320, FakeDeviceSlave.Versions(216, 1, 202, 2, 105, 7, 3))
        .Channel(1, "000001111", 2, [0, 0, 0, 0]);

    [Fact]
    public void Ax5805_AlwaysShowsItsHostDrive_EvenWhenItHasItsOwnIdentifier()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = Ax5206(),
            [1002] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 898989),
        };

        var safety = Read(slaves).Slaves.Single(s => s.Address == 1002).Details!.Safety!;

        Assert.Equal("898989", safety.EffectiveSerial);
        Assert.Equal("coe:0xF980:02", safety.EffectiveSerialSource);
        Assert.False(safety.ViaHostDrive);
        Assert.NotNull(safety.HostDrive);
        Assert.Equal(1001, safety.HostDrive.Address);
        Assert.Equal("000001111", safety.HostDrive.SerialNo);
        Assert.Equal(2, safety.HostDrive.ConfiguredSafetyOption);
    }

    [Fact]
    public void Ax5805_WithoutIdentifier_FallsBackToTheHostDriveSerial_AndShowsIt()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = Ax5206(),
            [1002] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        };

        var rec = Read(slaves).Slaves.Single(s => s.Address == 1002);

        Assert.False(rec.SafetySerialMissing);
        Assert.True(rec.Details!.Safety!.ViaHostDrive);
        Assert.Equal("000001111", rec.Details.Safety.EffectiveSerial);
        Assert.Equal(1001, rec.Details.Safety.HostDrive!.Address);
    }

    [Fact]
    public void Ax5805_WithoutADriveBefore_HasNoHostDrive()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = new FakeDeviceSlave().Terminal("EL1008", 0),
            [1002] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 12345),
        };

        var safety = Read(slaves).Slaves.Single(s => s.Address == 1002).Details!.Safety!;

        Assert.Null(safety.HostDrive);
        Assert.Equal("12345", safety.EffectiveSerial);
    }

    [Fact]
    public void Json_CarriesHostDriveRoleAndSoftwareVersions()
    {
        var slaves = new Dictionary<int, FakeDeviceSlave>
        {
            [1001] = Ax5206(),
            [1002] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 898989),
            [1003] = new FakeDeviceSlave().Terminal("EL6695-1001", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        };
        var fake = new FakeTwinCatSession
        {
            IsConnected = true,
            SolutionOpen = true,
            FakeEtherCatIdentitiesResult = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [Read(slaves)]),
        };

        using var doc = JsonDocument.Parse(new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false));
        var list = doc.RootElement.GetProperty("masters")[0].GetProperty("slaves").EnumerateArray().ToList();

        var driveSlave = list.Single(s => s.GetProperty("address").GetInt32() == 1001);
        Assert.Equal(System.Text.Json.JsonValueKind.String, driveSlave.GetProperty("serialNo").ValueKind);
        Assert.Equal("000001111", driveSlave.GetProperty("serialNo").GetString());
        Assert.Equal("soe:S-0-0432", driveSlave.GetProperty("serialSource").GetString());
        var drive = list.Single(s => s.GetProperty("address").GetInt32() == 1001).GetProperty("soe").GetProperty("channels")[0];
        Assert.Equal("2.16", drive.GetProperty("softwareVersions").GetProperty("firmware").GetString());
        Assert.Equal("1.05", drive.GetProperty("softwareVersions").GetProperty("optionCardFirmware").GetString());

        var card = list.Single(s => s.GetProperty("address").GetInt32() == 1002).GetProperty("safety");
        Assert.Equal(1001, card.GetProperty("hostDrive").GetProperty("address").GetInt32());
        Assert.False(card.GetProperty("hostDrive").TryGetProperty("optionCardFirmware", out _));
        Assert.Equal("F980", card.GetProperty("detection").GetString());

        var bridge = list.Single(s => s.GetProperty("address").GetInt32() == 1003).GetProperty("safety");
        Assert.False(bridge.GetProperty("isSafetyDevice").GetBoolean());
        Assert.Equal("blackChannel", bridge.GetProperty("role").GetString());
    }
}
