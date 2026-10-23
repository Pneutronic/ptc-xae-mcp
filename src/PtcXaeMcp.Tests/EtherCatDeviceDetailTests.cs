using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>A slave behind the master: CoE (0xF302) and SoE (0xF420) uploads. All values invented.</summary>
internal sealed class FakeDeviceSlave : IAdsRawReader, IDisposable
{
    private readonly Dictionary<(uint, uint), byte[]> _values = new();
    private readonly string _unreadable;
    public int Reads { get; private set; }
    public List<(uint Group, uint Offset)> Log { get; } = [];

    public FakeDeviceSlave(string unreadable = "DeviceInvalidOffset") => _unreadable = unreadable;

    private static byte[] Fixed(string text, int length)
    {
        var b = new byte[length];
        Encoding.ASCII.GetBytes(text).AsSpan(0, Math.Min(text.Length, length)).CopyTo(b);
        return b;
    }

    public FakeDeviceSlave Coe(int index, int sub, byte[] data)
    {
        _values[(0xF302, ((uint)index << 16) | (uint)sub)] = data;
        return this;
    }

    public FakeDeviceSlave CoeText(int index, int sub, string text, int length = 32) => Coe(index, sub, Fixed(text, length));

    public FakeDeviceSlave CoeU32(int index, int sub, uint value) => Coe(index, sub, BitConverter.GetBytes(value));

    public FakeDeviceSlave CoeU16(int index, int sub, int value) => Coe(index, sub, BitConverter.GetBytes((ushort)value));

    /// <summary>Basic CoE device info of a terminal.</summary>
    public FakeDeviceSlave Terminal(string name, uint serial)
    {
        CoeText(0x1008, 0, name, 24).CoeText(0x1009, 0, "01", 4).CoeText(0x100A, 0, "02", 4).CoeU32(0x1018, 4, serial);
        return this;
    }

    public static byte[] SoeList(byte[] data, int extra = 0)
    {
        var b = new byte[4 + data.Length + extra];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(data.Length + extra));
        data.CopyTo(b, 4);
        return b;
    }

    public FakeDeviceSlave Soe(int driveNo, int idn, byte[] data)
    {
        _values[(0xF420, ((uint)driveNo << 24) | (0x40u << 16) | (uint)idn)] = SoeList(data, 16);
        return this;
    }

    public FakeDeviceSlave SoeText(int driveNo, int idn, string text) => Soe(driveNo, idn, Encoding.ASCII.GetBytes(text + "\0"));

    /// <summary>A whole AX5000 channel with the documented IDNs.</summary>
    public FakeDeviceSlave Channel(int driveNo, string serial, int safetyOption, byte[] nameplate)
    {
        SoeText(driveNo, 30, "Firmware: v9.99 (Build 0001) / Bootloader: v9.01 (Build 0002)");
        Soe(driveNo, 0x8000 | 320, Versions(999, 1, 901, 2, 0, 0, 0));
        SoeText(driveNo, 31, "c:0001  p:0002  d:0003  f:0004  o:----  s:----");
        SoeText(driveNo, 432, "Serial#: " + serial);
        SoeText(driveNo, 0x8000 | 54, "AX5000-TEST-####");
        _values[(0xF420, ((uint)driveNo << 24) | (0x40u << 16) | (uint)(0x8000 | 2000))] = BitConverter.GetBytes((ushort)safetyOption); // plain UINT16, no list header
        Soe(driveNo, 0x8000 | 98, nameplate);
        return this;
    }

    private readonly List<(uint Group, uint Offset, uint Code, string Name)> _failures = [];

    /// <summary>Makes the read of one object fail with an ADS error.</summary>
    public FakeDeviceSlave Fail(uint group, uint offset, uint code, string name) { _failures.Add((group, offset, code, name)); return this; }

    public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
    {
        Reads++;
        Log.Add((indexGroup, indexOffset));
        foreach (var f in _failures)
            if (f.Group == indexGroup && f.Offset == indexOffset) return AdsRawReadResult.Failure(f.Code, f.Name);
        return _values.TryGetValue((indexGroup, indexOffset), out var d)
            ? AdsRawReadResult.Success(d)
            : AdsRawReadResult.Failure(1794, _unreadable);
    }

    public void Dispose() { }

    /// <summary>P-0-0320 data after the list header.</summary>
    public static byte[] Versions(int fw, int fwBuild, int boot, int bootBuild, int card, int cardBuild, int cardInterface)
    {
        var b = new byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)fw);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)fwBuild);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)boot);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)bootBuild);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(16), (ushort)card);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), (ushort)cardBuild);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), (ushort)cardInterface);
        return b;
    }

    public static byte[] Nameplate2(string vendor, string order, string electric, string serial)
    {
        var b = new byte[4 + 100];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 2);
        Fixed(vendor, 30).CopyTo(b, 4);
        Fixed(order, 30).CopyTo(b, 34);
        Fixed(electric, 30).CopyTo(b, 64);
        Fixed(serial, 10).CopyTo(b, 94);
        return b;
    }

    public static byte[] Nameplate1(uint serial, int vendorId, string motorType, string feedbackType)
    {
        var b = new byte[4 + 76];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + 8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + 10), (ushort)vendorId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4 + 12), serial);
        Fixed(motorType, 30).CopyTo(b, 4 + 16);
        Fixed(feedbackType, 30).CopyTo(b, 4 + 46);
        return b;
    }
}

public class EtherCatDeviceDetailTests
{
    private const string NetId = "10.20.30.40.2.1";

    // ---- BIC ------------------------------------------------------------------------

    [Fact]
    public void Bic_FullCode_IsSplitIntoItsIdentifiers()
    {
        var bic = EtherCatDeviceParsing.ParseBic("1P123456SBTNab12cd341KEL1234-0000     Q1    2P000011112222");
        Assert.Equal("123456", bic.OrderNumber);
        Assert.Equal("ab12cd34", bic.Btn);
        Assert.Equal("EL1234-0000", bic.Article);
        Assert.Equal("000011112222", bic.Batch);
        Assert.StartsWith("1P123456SBTN", bic.Raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1P777777", "777777", null, null, null)]
    [InlineData("1P777777SBTNzz99yy88", "777777", "zz99yy88", null, null)]
    [InlineData("1KEL9999-0000   2P555", null, null, "EL9999-0000", "555")]
    [InlineData("SBTNabcd", null, "abcd", null, null)]
    [InlineData("garbage 12345", null, null, null, null)]
    [InlineData("", null, null, null, null)]
    public void Bic_MissingParts_AreNull(string raw, string? order, string? btn, string? article, string? batch)
    {
        var bic = EtherCatDeviceParsing.ParseBic(raw);
        Assert.Equal(order, bic.OrderNumber);
        Assert.Equal(btn, bic.Btn);
        Assert.Equal(article, bic.Article);
        Assert.Equal(batch, bic.Batch);
    }

    [Fact]
    public void Bic_OtherIdentifiers_AreSkippedAndDoNotLeakIntoOthers()
    {
        var bic = EtherCatDeviceParsing.ParseBic("1P111111SBTNaaaabbbb1KEL0000-0000   Q5   51S0000999   30PV1   2P222222");
        Assert.Equal("EL0000-0000", bic.Article);
        Assert.Equal("222222", bic.Batch);
        Assert.Equal("111111", bic.OrderNumber);
    }

    [Fact]
    public void Clean_TrimsNulControlCharactersAndSpaces()
    {
        Assert.Equal("1.0.7.498R      HW_A", EtherCatDeviceParsing.Clean(Encoding.ASCII.GetBytes("  1.0.7.498R      HW_A \0\0junk")));
        Assert.Equal("ab", EtherCatDeviceParsing.Clean(Encoding.ASCII.GetBytes("a\u0001b\0")));
    }

    // ---- SoE parsing ----------------------------------------------------------------

    [Fact]
    public void SoeList_ReadsTheActualLengthAfterTheFourByteHeader()
    {
        Assert.True(EtherCatDeviceParsing.TryParseSoeList(FakeDeviceSlave.SoeList(Encoding.ASCII.GetBytes("abc\0"), extra: 20), out var data));
        Assert.Equal(4, data.Length);
        Assert.False(EtherCatDeviceParsing.TryParseSoeList([1, 0], out _));
        // actual length larger than the answer: clamped
        Assert.True(EtherCatDeviceParsing.TryParseSoeList([200, 0, 200, 0, 1, 2], out var clamped));
        Assert.Equal(2, clamped.Length);
    }

    [Fact]
    public void Serial_FromSoeText_KeepsTheLeadingZeros()
    {
        Assert.Equal("000012345", EtherCatDeviceParsing.SerialFromText("Serial#: 000012345"));
        Assert.Equal("77", EtherCatDeviceParsing.SerialFromText("77"));
        Assert.Null(EtherCatDeviceParsing.SerialFromText(" "));
    }

    [Fact]
    public void Nameplate_Type2_ReadsTheBeckhoffDataSheet()
    {
        var (motor, note) = EtherCatDeviceParsing.ParseNameplate(FakeDeviceSlave.Nameplate2("TestVendor", "AM0000-0AB0-0000", "AM0000-xAx0", "xx0001yy").AsSpan(0).ToArray());
        Assert.Null(note);
        Assert.Equal(2, motor!.NameplateType);
        Assert.Equal("TestVendor", motor.Vendor);
        Assert.Equal("AM0000-0AB0-0000", motor.OrderCode);
        Assert.Equal("AM0000-xAx0", motor.ElectricType);
        Assert.Equal("xx0001yy", motor.SerialNo);
    }

    [Fact]
    public void Nameplate_Type1_ReadsTheDocumentedBlock()
    {
        var (motor, _) = EtherCatDeviceParsing.ParseNameplate(FakeDeviceSlave.Nameplate1(424242, 3, "MOTOR-TEST-1", "FEEDBACK-TEST-1"));
        Assert.Equal(1, motor!.NameplateType);
        Assert.Equal("424242", motor.SerialNo);
        Assert.Equal(3, motor.VendorId);
        Assert.Equal("MOTOR-TEST-1", motor.OrderCode);
        Assert.Equal("FEEDBACK-TEST-1", motor.FeedbackType);
    }

    [Fact]
    public void Nameplate_Type0_HasNoMotorButANote()
    {
        var (motor, note) = EtherCatDeviceParsing.ParseNameplate([0, 0, 0, 0, 0, 0]);
        Assert.Null(motor);
        Assert.Contains("type 0", note, StringComparison.Ordinal);
        Assert.Null(EtherCatDeviceParsing.ParseNameplate([9, 0, 0, 0]).Motor);
    }

    // ---- reader: scenarios -------------------------------------------------------------

    private static EtherCatMasterIdentities Read(
        Dictionary<int, FakeDeviceSlave> slaves, Dictionary<int, string>? names = null, bool coe = true, bool details = true)
    {
        var master = new FakeEtherCatMaster().WithSlaves(slaves.Keys.Order()
            .Select(a => (a, (byte)0x08, (byte)0x00, 2u, (uint)a, 1u, 0u)).ToArray());
        var projected = slaves.Keys.Order()
            .Select(a => new EtherCatProjectedSlave($"TIID^D^B{a}", names?.GetValueOrDefault(a) ?? $"Box {a}", 2, (uint)a, 1, a)).ToArray();
        var config = new EtherCatMasterConfig("TIID^D", "D", NetId, 0xFFFF, "test", null, projected);
        var reader = new EtherCatIdentityReader((_, port) => port == 0xFFFF ? master : slaves[port]);
        return reader.Read(config, null, null, false, coe, details);
    }

    [Fact]
    public void Oct_MotorAndFeedbackBlock_AreReadAndTrimmed()
    {
        var slave = new FakeDeviceSlave().Terminal("EL7000-9014", 0)
            .CoeText(0x9009, 1, "TestVendor").CoeText(0x9009, 2, "AM0000-xJx1").CoeText(0x9009, 3, "aa0000bb\u0002   ")
            .CoeText(0x9009, 4, "AM0000-0JH1-0000").CoeText(0x9009, 5, "sync, rot")
            .CoeText(0x9008, 14, "EDM00-0XX0A0S00").CoeText(0x9008, 15, "C0000000U").CoeText(0x9008, 16, "1.0.0.000R      HW_A").CoeText(0x9008, 17, "01.01.20");

        var rec = Read(new() { [1001] = slave }).Slaves.Single();

        Assert.Equal("TestVendor", rec.Details!.Motor!.Vendor);
        Assert.Equal("AM0000-xJx1", rec.Details.Motor.ElectricType);
        Assert.Equal("aa0000bb", rec.Details.Motor.SerialNo);
        Assert.Equal("AM0000-0JH1-0000", rec.Details.Motor.OrderCode);
        Assert.Equal("sync, rot", rec.Details.Motor.Construction);
        Assert.Equal("EDM00-0XX0A0S00", rec.Details.Feedback!.TypeCode);
        Assert.Equal("C0000000U", rec.Details.Feedback.SerialNo);
        Assert.Equal("1.0.0.000R      HW_A", rec.Details.Feedback.Firmware);
        Assert.Equal("01.01.20", rec.Details.Feedback.FirmwareDate);
    }

    [Fact]
    public void PlainTerminal_HasNoMotorBicOrSafety_AndCostsEightReads()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1008", 0);
        var rec = Read(new() { [1001] = slave }).Slaves.Single();
        Assert.Null(rec.Details!.Bic);
        Assert.Null(rec.Details.Safety);
        Assert.Null(rec.Details.Motor);
        Assert.Empty(rec.Details.Errors);
        Assert.False(rec.SafetySerialMissing);
        // 0x1008, 0x1009, 0x100A, 0x1018:04 + BIC + F980:01/02 + 0x9009:03
        Assert.Equal(8, slave.Reads);
    }

    [Fact]
    public void DetailsOff_ReadsOnlyTheFourBasicValues()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1008", 0);
        var rec = Read(new() { [1001] = slave }, details: false).Slaves.Single();
        Assert.Null(rec.Details);
        Assert.Equal(4, slave.Reads);
    }

    [Fact]
    public void DetailsNeedCoe()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1008", 0);
        var rec = Read(new() { [1001] = slave }, coe: false, details: true).Slaves.Single();
        Assert.Null(rec.Coe);
        Assert.Null(rec.Details);
        Assert.Equal(0, slave.Reads);
    }

    [Fact]
    public void Ax5000_TwoChannels_StopAtTheFirstFailingChannel_AndCountReads()
    {
        var drive = new FakeDeviceSlave("UnknownAmsCommand")
            .Channel(0, "000001111", 2, FakeDeviceSlave.Nameplate2("TestVendor", "AM0000-0AB0-0000", "AM0000-xAx0", "xx0001yy"))
            .Channel(1, "000001111", 2, [0, 0, 0, 0, 0, 0]);
        // the CoE name read of a SoE drive answers UnknownAmsCommand, which is what the fake returns for unknown offsets

        var rec = Read(new() { [1001] = drive }).Slaves.Single();

        Assert.Equal(EtherCatCoeStatus.NotCoe, rec.Coe!.Status);
        var channels = rec.Details!.Soe!;
        Assert.Equal(2, channels.Count);
        Assert.Equal([0, 1], channels.Select(c => c.DriveNo).ToArray());
        Assert.Equal("Firmware: v9.99 (Build 0001) / Bootloader: v9.01 (Build 0002)", channels[0].Firmware);
        Assert.Equal("c:0001  p:0002  d:0003  f:0004  o:----  s:----", channels[0].Hardware);
        Assert.Equal("000001111", channels[0].SerialNo);
        Assert.Equal("Serial#: 000001111", channels[0].SerialRaw);
        Assert.Equal("AX5000-TEST-####", channels[0].ConfiguredType);
        Assert.Equal(2, channels[0].SafetyOption);
        Assert.Equal("AX5805/AX5806", channels[0].SafetyOptionName);
        Assert.Equal("AM0000-0AB0-0000", channels[0].Motor!.OrderCode);
        Assert.Null(channels[1].Motor);
        Assert.Contains("type 0", channels[1].MotorNote, StringComparison.Ordinal);
        Assert.Empty(channels[0].Errors);
        // 1 CoE name + 2 channels x (S-0-0030 + 5 more) + the failing S-0-0030 of channel 2
        Assert.Equal(1 + 14 + 1, drive.Reads);
        Assert.Contains(drive.Log, l => l.Group == 0xF420 && l.Offset == ((2u << 24) | (0x40u << 16) | 30u));
        Assert.DoesNotContain(drive.Log, l => l.Group == 0xF420 && (l.Offset >> 24) == 3);
    }

    [Fact]
    public void Ax5000_PerIdnErrors_AreRecordedAndNotFatal()
    {
        var drive = new FakeDeviceSlave("UnknownAmsCommand");
        drive.SoeText(0, 30, "Firmware: v9.99").SoeText(0, 432, "Serial#: 5");

        var channel = Read(new() { [1001] = drive }).Slaves.Single().Details!.Soe!.Single();

        Assert.Equal("5", channel.SerialNo);
        Assert.Null(channel.Hardware);
        Assert.Equal(["S-0-0031", "P-0-0054", "P-0-2000", "P-0-0098", "P-0-0320"], channel.Errors.Select(e => e.Split(':')[0]).ToArray());
    }

    [Fact]
    public void Ax5Name_TriggersSoeEvenWhenCoeAnswers()
    {
        var drive = new FakeDeviceSlave().Terminal("AX5103-0000", 0).Channel(0, "000002222", 0, [0, 0, 0, 0]);
        var channels = Read(new() { [1001] = drive }).Slaves.Single().Details!.Soe!;
        Assert.Single(channels);
    }

    // ---- safety identity ---------------------------------------------------------------

    private static Dictionary<int, FakeDeviceSlave> SafetyMachine() => new()
    {
        // F980:02 non-zero
        [1001] = new FakeDeviceSlave().Terminal("EL1918", 0).CoeU16(0xF980, 1, 11).CoeU32(0xF980, 2, 900001),
        // F980:02 zero, BTN in the BIC
        [1002] = new FakeDeviceSlave().Terminal("EL2904", 0).CoeU16(0xF980, 1, 12).CoeU32(0xF980, 2, 0)
            .CoeText(0x10E2, 1, "1P222222SBTNbtn22222" + new string(' ', 4) + "2P0001", 141),
        // F980 zero, no BIC, 0x1018:04 non-zero
        [1003] = new FakeDeviceSlave().Terminal("EL1904", 4711).CoeU16(0xF980, 1, 13).CoeU32(0xF980, 2, 0),
        // host drive and its AX5805
        [1004] = new FakeDeviceSlave("UnknownAmsCommand").Channel(0, "000003333", 2, [0, 0, 0, 0]),
        [1005] = new FakeDeviceSlave().Terminal("AX5805-0000", 0).CoeU16(0xF980, 1, 5).CoeU32(0xF980, 2, 0),
        // nothing at all
        [1006] = new FakeDeviceSlave().Terminal("EL6910", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
        // an AX5805 whose predecessor is no drive
        [1007] = new FakeDeviceSlave().Terminal("AX5806-0000", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0),
    };

    [Fact]
    public void Safety_EffectiveIdentifier_FollowsThePrecedence()
    {
        var r = Read(SafetyMachine());
        Safety(r, 1001, "900001", "coe:0xF980:02", false);
        Safety(r, 1002, "btn22222", "bic:BTN", false);
        Safety(r, 1003, "4711", "coe:0x1018:04", false);
        Safety(r, 1005, "000003333", "hostDrive:S-0-0432", true);
        Assert.Equal(1004, r.Slaves.Single(s => s.Address == 1005).Details!.Safety!.HostDriveAddress);

        var f980 = r.Slaves.Single(s => s.Address == 1001).Details!.Safety!;
        Assert.True(f980.IsSafetyDevice);
        Assert.Equal(11, f980.FsoeAddress);
        Assert.Equal(900001, f980.SerialNo);
        Assert.Equal("coe:0xF980:02", f980.SerialSource);
        Assert.False(f980.SerialDocumented);
    }

    private static void Safety(EtherCatMasterIdentities r, int address, string value, string source, bool viaHost)
    {
        var slave = r.Slaves.Single(s => s.Address == address);
        Assert.False(slave.SafetySerialMissing);
        Assert.Equal(value, slave.Details!.Safety!.EffectiveSerial);
        Assert.Equal(source, slave.Details.Safety.EffectiveSerialSource);
        Assert.Equal(viaHost, slave.Details.Safety.ViaHostDrive);
    }

    [Fact]
    public void Safety_WithoutAnyIdentifier_IsFlagged_WarnedAndIncomplete()
    {
        var r = Read(SafetyMachine());

        foreach (int address in new[] { 1006, 1007 })
        {
            var slave = r.Slaves.Single(s => s.Address == address);
            Assert.True(slave.SafetySerialMissing);
            Assert.Null(slave.Details!.Safety!.EffectiveSerial);
        }
        Assert.False(r.SafetyIdentityComplete);
        string warning = Assert.Single(r.Warnings, w => w.StartsWith("Safety devices without a unique identifier", StringComparison.Ordinal));
        Assert.Contains("address 1006", warning, StringComparison.Ordinal);
        Assert.Contains("address 1007", warning, StringComparison.Ordinal);
        Assert.Contains("TIID^D^B1006", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("address 1001", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Safety_UnknownArticleWithZeroF980_IsNoSafetyDevice_ButABlackChannel()
    {
        var slave = new FakeDeviceSlave().Terminal("Interface", 0).CoeU16(0xF980, 1, 0).CoeU32(0xF980, 2, 0);
        var rec = Read(new() { [1001] = slave }).Slaves.Single();
        Assert.False(rec.Details!.Safety!.IsSafetyDevice);
        Assert.Equal("blackChannel", rec.Details.Safety.Role);
        Assert.False(rec.SafetySerialMissing);
    }

    [Theory]
    [InlineData("EL1918", true)]
    [InlineData("EL2904", true)]
    [InlineData("EL6910", true)]
    [InlineData("EJ1914", true)]
    [InlineData("EP1957-0022", true)]
    [InlineData("KL6904", true)]
    [InlineData("AX5805", true)]
    [InlineData("AX5806-0000", true)]
    [InlineData("AX5801", true)]
    [InlineData("EL1008", false)]
    [InlineData("EL2008", false)]
    [InlineData("EL7221-9014", false)]
    [InlineData("AX5206", false)]
    [InlineData("EL19180", false)]
    public void SafetyNamePatterns(string name, bool expected) =>
        Assert.Equal(expected, EtherCatDeviceParsing.LooksLikeSafetyDevice(name));

    [Fact]
    public void Safety_DetectedByNameAlone_WhenF980IsNotReadable()
    {
        var slave = new FakeDeviceSlave().Terminal("EL1904", 0);
        var rec = Read(new() { [1001] = slave }).Slaves.Single();
        Assert.True(rec.Details!.Safety!.IsSafetyDevice);
        Assert.Null(rec.Details.Safety.FsoeAddress);
        Assert.True(rec.SafetySerialMissing);
    }

    // ---- summary and JSON ----------------------------------------------------------------

    [Fact]
    public void Summary_CountsSlavesSafetyAndMotors()
    {
        var machine = SafetyMachine();
        machine[1008] = new FakeDeviceSlave().Terminal("EL7000-9014", 0).CoeText(0x9009, 3, "aa0000bb").CoeText(0x9009, 1, "TestVendor");
        machine[1009] = new FakeDeviceSlave("UnknownAmsCommand")
            .Channel(0, "000004444", 0, FakeDeviceSlave.Nameplate2("TestVendor", "AM0000-0AB0-0000", "AM0000-xAx0", "xx0001yy"))
            .Channel(1, "000004444", 0, FakeDeviceSlave.Nameplate2("TestVendor", "AM0000-0AB0-0000", "AM0000-xAx0", ""));

        var summary = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [Read(machine)]).Summary;

        Assert.Equal(9, summary.Slaves);
        Assert.Equal(6, summary.SafetyDevices);
        Assert.Equal(4, summary.SafetyWithSerial);
        Assert.Equal(3, summary.Motors);
        Assert.Equal(2, summary.MotorsWithSerial);
    }

    [Fact]
    public void Json_CarriesDetailsSummaryAndTheCompletenessFlag()
    {
        var result = new EtherCatIdentitiesResult(DateTimeOffset.UnixEpoch, [Read(SafetyMachine())]);
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, FakeEtherCatIdentitiesResult = result };

        string json = new EtherCatIdentityCoordinator(fake).ReadIdentities(null, null, null, false);

        Assert.Contains("ReadEtherCatIdentities(,,,False,True,True,5000)", fake.CallLog);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("safetyIdentityComplete").GetBoolean());
        Assert.Equal(7, doc.RootElement.GetProperty("summary").GetProperty("slaves").GetInt32());
        Assert.NotEmpty(doc.RootElement.GetProperty("warnings").EnumerateArray());
        var slaves = doc.RootElement.GetProperty("masters")[0].GetProperty("slaves");
        var s1002 = slaves.EnumerateArray().Single(s => s.GetProperty("address").GetInt32() == 1002);
        Assert.Equal("btn22222", s1002.GetProperty("safety").GetProperty("effectiveSerial").GetString());
        Assert.Equal("222222", s1002.GetProperty("bic").GetProperty("orderNumber").GetString());
        var s1004 = slaves.EnumerateArray().Single(s => s.GetProperty("address").GetInt32() == 1004);
        Assert.Equal("000003333", s1004.GetProperty("soe").GetProperty("channels")[0].GetProperty("serialNo").GetString());
        var s1006 = slaves.EnumerateArray().Single(s => s.GetProperty("address").GetInt32() == 1006);
        Assert.True(s1006.GetProperty("safetySerialMissing").GetBoolean());
    }
}
