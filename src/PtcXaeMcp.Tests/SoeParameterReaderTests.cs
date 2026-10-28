using System.Text;
using System.Xml;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>Live SoE parameter reads and the Startup comparison against a synthetic drive. All values invented.</summary>
public class SoeParameterReaderTests
{
    private const uint DeviceInvalidOffset = 1793;

    private sealed class FakeSoeDrive : IAdsRawReader
    {
        private readonly Dictionary<uint, AdsRawReadResult> _answers = [];
        public List<(uint Group, uint Offset)> Calls { get; } = [];

        public FakeSoeDrive Set(int driveNo, int idn, int element, params byte[] data)
        {
            _answers[EtherCatDeviceDetailReader.SoeOffset(driveNo, idn, element)] = AdsRawReadResult.Success(data);
            return this;
        }

        public FakeSoeDrive Fail(int driveNo, int idn, int element, uint code, string name)
        {
            _answers[EtherCatDeviceDetailReader.SoeOffset(driveNo, idn, element)] = AdsRawReadResult.Failure(code, name);
            return this;
        }

        public AdsRawReadResult Read(uint indexGroup, uint indexOffset, int maxLength)
        {
            Calls.Add((indexGroup, indexOffset));
            Assert.Equal(EtherCatDeviceDetailReader.SoeGroup, indexGroup);
            return _answers.TryGetValue(indexOffset, out var r) ? r : AdsRawReadResult.Failure(DeviceInvalidOffset, "DeviceInvalidOffset");
        }
    }

    private static SoeParameterReader Reader(FakeSoeDrive drive) => new(drive, "10.0.0.1.2.1:1002");

    private static byte[] List(params byte[] data) =>
        [(byte)data.Length, 0, (byte)data.Length, 0, .. data];

    private static byte[] Text(string text) => List(Encoding.ASCII.GetBytes(text));

    private static readonly int[] ValueOnly = [SoeElement.Value];

    // ── offsets ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1, 0x40, 0x00400001u)]
    [InlineData(0, 0x8000 | 3231, 0x40, 0x00408C9Fu)]
    [InlineData(1, 30, 0x40, 0x0140001Eu)]
    [InlineData(0, 30, 0x04, 0x0004001Eu)]
    [InlineData(0, 30, 0x02, 0x0002001Eu)]
    public void SoeOffset_PutsElementInTheLowByteOfTheHighWord(int driveNo, int idn, int element, uint expected) =>
        Assert.Equal(expected, EtherCatDeviceDetailReader.SoeOffset(driveNo, idn, element));

    [Fact]
    public void SoeOffset_DefaultElementIsValue() =>
        Assert.Equal(EtherCatDeviceDetailReader.SoeOffset(0, 30, 0x40), EtherCatDeviceDetailReader.SoeOffset(0, 30));

    // ── attribute ───────────────────────────────────────────────────────────

    [Fact]
    public void Attribute_DecodesTheFields()
    {
        // conversion factor 1, 2 byte, unsigned decimal, 4 decimal places, write protected in CP2
        var a = SoeAttributeDecoder.Decode(0x14110001);
        Assert.Equal(0x14110001u, a.Raw);
        Assert.Equal(1, a.ConversionFactor);
        Assert.Equal("2 bytes", a.DataLength);
        Assert.False(a.IsList);
        Assert.Null(a.ElementBytes);
        Assert.False(a.IsCommand);
        Assert.Equal("unsignedDecimal", a.Display);
        Assert.Equal(4, a.DecimalPlaces);
        Assert.Equal(["CP2"], a.WriteProtectedIn);
    }

    [Theory]
    [InlineData(0x00020000u, "4 bytes", false, null)]
    [InlineData(0x00030000u, "8 bytes", false, null)]
    [InlineData(0x00040000u, "list of 1 byte elements", true, 1)]
    [InlineData(0x00050000u, "list of 2 byte elements", true, 2)]
    [InlineData(0x00060000u, "list of 4 byte elements", true, 4)]
    [InlineData(0x00070000u, "list of 8 byte elements", true, 8)]
    [InlineData(0x00000000u, "reserved", false, null)]
    public void Attribute_DataLengthCodes(uint raw, string length, bool isList, int? elementBytes)
    {
        var a = SoeAttributeDecoder.Decode(raw);
        Assert.Equal(length, a.DataLength);
        Assert.Equal(isList, a.IsList);
        Assert.Equal(elementBytes, a.ElementBytes);
    }

    [Theory]
    [InlineData(0x00000000u, "binary")]
    [InlineData(0x00100000u, "unsignedDecimal")]
    [InlineData(0x00200000u, "signedDecimal")]
    [InlineData(0x00300000u, "hex")]
    [InlineData(0x00400000u, "text")]
    [InlineData(0x00500000u, "idn")]
    [InlineData(0x00600000u, "float")]
    public void Attribute_DisplayFormats(uint raw, string display) =>
        Assert.Equal(display, SoeAttributeDecoder.Decode(raw).Display);

    [Fact]
    public void Attribute_CommandBit_AndAllWriteProtections()
    {
        var a = SoeAttributeDecoder.Decode(0x70080000);
        Assert.True(a.IsCommand);
        Assert.Equal(["CP2", "CP3", "CP4"], a.WriteProtectedIn);
    }

    // ── data ────────────────────────────────────────────────────────────────

    [Fact]
    public void Data_WithoutAttribute_IsRawHexAndUnsigned()
    {
        var d = SoeDataDecoder.Decode([0x0A, 0x00], 1, null);
        Assert.Equal("0A00", d.Hex);
        Assert.Equal(2, d.Length);
        Assert.Equal(10UL, d.Unsigned);
        Assert.Null(d.Signed);
        Assert.Null(d.Scaled);
        Assert.Null(d.List);
    }

    [Fact]
    public void Data_SignedDecimalAttribute_GivesSignedAndScaledValue()
    {
        var d = SoeDataDecoder.Decode([0xFE, 0xFF], 1, SoeAttributeDecoder.Decode(0x02210001)); // 2 byte, signed, 2 places
        Assert.Equal(-2L, d.Signed);
        Assert.Equal(-0.02m, d.Scaled);
        Assert.Equal(65534UL, d.Unsigned);
    }

    [Fact]
    public void Data_UnsignedDecimalAttribute_ScalesByDecimalPlaces()
    {
        var d = SoeDataDecoder.Decode([0x64, 0, 0, 0], 1, SoeAttributeDecoder.Decode(0x01120001)); // 4 byte, unsigned, 1 place
        Assert.Equal(10.0m, d.Scaled);
        Assert.Null(d.Signed);
    }

    [Fact]
    public void Data_FloatAttribute_IsReadAsFloat()
    {
        var d = SoeDataDecoder.Decode(BitConverter.GetBytes(1.5f), 1, SoeAttributeDecoder.Decode(0x00620000));
        Assert.Equal(1.5m, d.Scaled);
    }

    [Fact]
    public void Data_IdnListAttribute_GivesIdnList()
    {
        var d = SoeDataDecoder.Decode(List(0x33, 0x00, 0x0A, 0x80), 16, SoeAttributeDecoder.Decode(0x00550000));
        Assert.Equal(new SoeListHeader(4, 4), d.List);
        Assert.Equal(["S-0-0051", "P-0-0010"], d.IdnList);
        Assert.Null(d.Unsigned);
    }

    [Fact]
    public void Data_AtListWithoutAttribute_StillGivesIdnList()
    {
        var d = SoeDataDecoder.Decode(List(0x33, 0x00), 16, null);
        Assert.Equal(["S-0-0051"], d.IdnList);
    }

    [Fact]
    public void Data_TextAttribute_GivesText()
    {
        var d = SoeDataDecoder.Decode(Text("AX5203"), 30, SoeAttributeDecoder.Decode(0x00440000));
        Assert.Equal("AX5203", d.Text);
    }

    [Fact]
    public void Data_FixedLengthAttribute_NeverReadsAListHeader()
    {
        // 0x0000_0000 as a 4 byte scalar would look like an empty list without the attribute
        var d = SoeDataDecoder.Decode([0, 0, 0, 0], 1, SoeAttributeDecoder.Decode(0x00120000));
        Assert.Null(d.List);
        Assert.Equal(0UL, d.Unsigned);
    }

    // ── read ────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_DefaultElement_IsTheValue_OneRead()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 0xA0, 0x0F);
        var r = Reader(drive).Read(0, 1, ValueOnly);

        Assert.Equal("S-0-0001", r.Idn);
        Assert.Equal(1, r.IdnRaw);
        Assert.Equal(0, r.DriveNo);
        Assert.Equal("A00F", r.Value!.Hex);
        Assert.Equal(0x0FA0UL, r.Value.Unsigned);
        Assert.Null(r.Errors);
        Assert.Null(r.Attribute);
        Assert.Equal([EtherCatDeviceDetailReader.SoeOffset(0, 1, 0x40)], drive.Calls.Select(c => c.Offset).ToArray());
    }

    [Fact]
    public void Read_AllElements_AttributeFirst_ThenTheRest()
    {
        var drive = new FakeSoeDrive()
            .Set(0, 80, SoeElement.Attribute, 0x01, 0x00, 0x12, 0x01)       // 0x01120001: 4 byte unsigned, 1 place
            .Set(0, 80, SoeElement.DataStatus, 0x00, 0x00)
            .Set(0, 80, SoeElement.ElementName, Text("Torque limit").ToArray())
            .Set(0, 80, SoeElement.Unit, Text("Nm").ToArray())
            .Set(0, 80, SoeElement.Min, 0, 0, 0, 0)
            .Set(0, 80, SoeElement.Max, 0xE8, 0x03, 0, 0)
            .Set(0, 80, SoeElement.Value, 0x64, 0, 0, 0)
            .Set(0, 80, SoeElement.Default, 0x0A, 0, 0, 0);

        int[] all = [SoeElement.Value, SoeElement.ElementName, SoeElement.Attribute, SoeElement.Unit, SoeElement.Min,
            SoeElement.Max, SoeElement.Default, SoeElement.DataStatus];
        var r = Reader(drive).Read(0, 80, all);

        Assert.Equal(EtherCatDeviceDetailReader.SoeOffset(0, 80, SoeElement.Attribute), drive.Calls[0].Offset);
        Assert.Equal("Torque limit", r.Name);
        Assert.Equal("Nm", r.Unit);
        Assert.Equal(0, r.DataStatus);
        Assert.Equal("unsignedDecimal", r.Attribute!.Display);
        Assert.Equal(10.0m, r.Value!.Scaled);
        Assert.Equal(100.0m, r.Max!.Scaled);
        Assert.Equal(0m, r.Min!.Scaled);
        Assert.Equal(1.0m, r.Default!.Scaled);
        Assert.Null(r.Errors);
    }

    [Fact]
    public void Read_AdsErrorOnOneIdn_IsRecordedOnThatIdn_NotFatal()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 1, 0);
        var reader = Reader(drive);

        var ok = reader.Read(0, 1, ValueOnly);
        var bad = reader.Read(0, 999, ValueOnly);

        Assert.Null(ok.Errors);
        Assert.Null(bad.Value);
        var e = Assert.Single(bad.Errors!);
        Assert.Equal("value", e.Element);
        Assert.Equal(DeviceInvalidOffset, e.Code);
        Assert.Equal("DeviceInvalidOffset", e.Name);
    }

    [Fact]
    public void Read_ErrorOnOneElement_KeepsTheOthers()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 1, 0).Fail(0, 1, SoeElement.Unit, DeviceInvalidOffset, "DeviceInvalidOffset");
        var r = Reader(drive).Read(0, 1, [SoeElement.Value, SoeElement.Unit]);
        Assert.NotNull(r.Value);
        Assert.Null(r.Unit);
        Assert.Equal("unit", Assert.Single(r.Errors!).Element);
    }

    [Fact]
    public void Read_ShortAttribute_IsAnErrorAndValueStaysRaw()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Attribute, 1, 0).Set(0, 1, SoeElement.Value, 1, 0);
        var r = Reader(drive).Read(0, 1, [SoeElement.Attribute, SoeElement.Value]);
        Assert.Null(r.Attribute);
        Assert.Equal("attribute", Assert.Single(r.Errors!).Element);
        Assert.Equal(1UL, r.Value!.Unsigned);
        Assert.Null(r.Value.Scaled);
    }

    [Theory]
    [InlineData(1861u, "ClientSyncTimeout")]
    [InlineData(7u, "TargetMachineNotFound")]
    [InlineData(6u, "TargetPortNotFound")]
    public void Read_FirstReadUnreachable_ThrowsNamingTheTarget(uint code, string name)
    {
        var drive = new FakeSoeDrive().Fail(0, 1, SoeElement.Value, code, name);
        var ex = Assert.Throws<SoeTargetUnreachableException>(() => Reader(drive).Read(0, 1, ValueOnly));
        Assert.Contains("10.0.0.1.2.1:1002", ex.Message);
        Assert.Contains(name, ex.Message);
    }

    [Fact]
    public void Read_UnreachableAfterTheDriveAnswered_IsAPerIdnError()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 1, 0).Fail(0, 2, SoeElement.Value, 1861, "ClientSyncTimeout");
        var reader = Reader(drive);
        reader.Read(0, 1, ValueOnly);
        var r = reader.Read(0, 2, ValueOnly);
        Assert.Equal(1861u, Assert.Single(r.Errors!).Code);
    }

    [Fact]
    public void Read_FirstReadWithAnIdnError_IsNotUnreachable()
    {
        var r = Reader(new FakeSoeDrive()).Read(0, 999, ValueOnly); // DeviceInvalidOffset
        Assert.Equal(DeviceInvalidOffset, Assert.Single(r.Errors!).Code);
    }

    [Fact]
    public void ReadAll_ReadsEveryIdnAtTheDriveNo()
    {
        var drive = new FakeSoeDrive().Set(2, 1, SoeElement.Value, 1, 0).Set(2, 2, SoeElement.Value, 2, 0);
        var rs = Reader(drive).ReadAll([1, 2], 2, ValueOnly);
        Assert.Equal([1, 2], rs.Select(r => r.IdnRaw).ToArray());
        Assert.All(rs, r => Assert.Equal(2, r.DriveNo));
    }

    // ── compare ─────────────────────────────────────────────────────────────

    private static EtherCatStartupEntry SoeEntry(int idn, string dataHex, int driveNo = 0, bool fixedEntry = false,
        string elements = "value", string? comment = null)
    {
        var bytes = EtherCatStartupParser.Bytes(dataHex);
        return new EtherCatStartupEntry(EtherCatStartupProtocol.SoE, fixedEntry, ["PS"], comment, 3000, dataHex,
            Idn: SoeIdn.Format(idn), IdnRaw: idn, ParameterSet: SoeIdn.ParameterSet(idn), DriveNo: driveNo, OpCode: 3,
            Elements: elements, Attribute: 0, List: bytes is null ? null : EtherCatStartupParser.ListHeader(bytes));
    }

    [Fact]
    public void Compare_Match_Mismatch_AndProjectedActualHex()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 0xA0, 0x0F).Set(0, 2, SoeElement.Value, 0x01, 0x00);
        var rs = Reader(drive).Compare([SoeEntry(1, "A00F", comment: "ctrl"), SoeEntry(2, "0300", fixedEntry: true)], null);

        Assert.Equal(2, rs.Count);
        Assert.True(rs[0].Match);
        Assert.Equal("A00F", rs[0].Projected);
        Assert.Equal("A00F", rs[0].Actual);
        Assert.Equal("ctrl", rs[0].Comment);
        Assert.False(rs[0].Fixed);
        Assert.Equal("S-0-0001", rs[0].Idn);

        Assert.False(rs[1].Match);
        Assert.Equal("0300", rs[1].Projected);
        Assert.Equal("0100", rs[1].Actual);
        Assert.True(rs[1].Fixed);
        Assert.Equal(1UL, rs[1].Value!.Unsigned);
    }

    [Fact]
    public void Compare_Lists_IgnoreTheMaximumLength()
    {
        // projected: current 6, max 6; the drive reports a larger maximum with the same data
        var drive = new FakeSoeDrive().Set(0, 24, SoeElement.Value, 0x06, 0, 0x20, 0, 0x86, 0, 0x24, 0, 0x0A, 0x80);
        var rs = Reader(drive).Compare([SoeEntry(24, "06000600860024000A80")], null);
        Assert.True(rs.Single().Match);
    }

    [Fact]
    public void Compare_Lists_DifferingDataDoNotMatch()
    {
        var drive = new FakeSoeDrive().Set(0, 24, SoeElement.Value, 0x06, 0, 0x06, 0, 0x86, 0, 0x24, 0, 0x0B, 0x80);
        var rs = Reader(drive).Compare([SoeEntry(24, "06000600860024000A80")], null);
        Assert.False(rs.Single().Match);
    }

    [Fact]
    public void Compare_FourByteScalarThatLooksLikeAnEmptyList_IsComparedByteExact()
    {
        // projected 0 (4 bytes: header "0 / 0"); the drive holds 0x000A0000 = "0 / 10": equal as empty lists, different as values
        var drive = new FakeSoeDrive().Set(0, 5, SoeElement.Value, 0x00, 0x00, 0x0A, 0x00);
        var r = Reader(drive).Compare([SoeEntry(5, "00000000")], null).Single();
        Assert.False(r.Match);
    }

    [Fact]
    public void Compare_UsesTheEntrysDriveNo_AndReadsEachDriveNoIdnOnce()
    {
        var drive = new FakeSoeDrive().Set(0, 15, SoeElement.Value, 3, 0).Set(4, 15, SoeElement.Value, 3, 0);
        var rs = Reader(drive).Compare(
            [SoeEntry(15, "0300", driveNo: 0), SoeEntry(15, "0300", driveNo: 4), SoeEntry(15, "0300", driveNo: 4)], null);

        Assert.Equal([0, 4, 4], rs.Select(r => r.DriveNo).ToArray());
        Assert.All(rs, r => Assert.True(r.Match));
        Assert.Equal(2, drive.Calls.Count);
        Assert.Equal(
            [EtherCatDeviceDetailReader.SoeOffset(0, 15), EtherCatDeviceDetailReader.SoeOffset(4, 15)],
            drive.Calls.Select(c => c.Offset).ToArray());
    }

    [Fact]
    public void Compare_DriveNoFilter_KeepsOnlyThatDrive()
    {
        var drive = new FakeSoeDrive().Set(4, 15, SoeElement.Value, 3, 0);
        var rs = Reader(drive).Compare([SoeEntry(15, "0300", driveNo: 0), SoeEntry(15, "0300", driveNo: 4)], 4);
        Assert.Equal(4, Assert.Single(rs).DriveNo);
        Assert.Single(drive.Calls);
    }

    [Fact]
    public void Compare_AdsError_IsRecorded_MatchStaysNull()
    {
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 1, 0);
        var rs = Reader(drive).Compare([SoeEntry(1, "0100"), SoeEntry(999, "0100")], null);
        Assert.True(rs[0].Match);
        Assert.Null(rs[1].Match);
        Assert.Equal(DeviceInvalidOffset, Assert.Single(rs[1].Errors!).Code);
        Assert.Null(rs[1].Actual);
    }

    [Fact]
    public void Compare_NonValueElement_IsListedWithANote_WithoutReading()
    {
        var drive = new FakeSoeDrive();
        var rs = Reader(drive).Compare([SoeEntry(1, "0100", elements: "attribute")], null);
        var r = Assert.Single(rs);
        Assert.Null(r.Match);
        Assert.Contains("attribute", r.Note);
        Assert.Empty(drive.Calls);
    }

    [Fact]
    public void Compare_IgnoresNonSoeEntries()
    {
        var eoe = new EtherCatStartupEntry(EtherCatStartupProtocol.EoE, true, ["IP"], null, null, "0102");
        var drive = new FakeSoeDrive().Set(0, 1, SoeElement.Value, 1, 0);
        Assert.Single(Reader(drive).Compare([eoe, SoeEntry(1, "0100")], null));
    }

    [Fact]
    public void Compare_UnreachableDrive_Throws()
    {
        var drive = new FakeSoeDrive().Fail(0, 1, SoeElement.Value, 1861, "ClientSyncTimeout");
        Assert.Throws<SoeTargetUnreachableException>(() => Reader(drive).Compare([SoeEntry(1, "0100")], null));
    }

    // ── slave address ───────────────────────────────────────────────────────

    [Fact]
    public void ParseSlaveAddress_ReadsMasterNetIdAndSlavePort()
    {
        var (netId, port) = EtherCatProjection.ParseSlaveAddress(
            "<TreeItem><EtherCAT><AmsAddress><AmsNetId>10.0.0.1.2.1</AmsNetId><AmsPort>1002</AmsPort></AmsAddress></EtherCAT></TreeItem>");
        Assert.Equal("10.0.0.1.2.1", netId);
        Assert.Equal(1002, port);
    }

    [Theory]
    [InlineData("<TreeItem><EtherCAT/></TreeItem>")]
    [InlineData("<TreeItem><EtherCAT><AmsAddress><AmsNetId>0.0.0.0.0.0</AmsNetId><AmsPort>1002</AmsPort></AmsAddress></EtherCAT></TreeItem>")]
    [InlineData("<TreeItem><EtherCAT><AmsAddress><AmsNetId>10.0.0.1.2.1</AmsNetId><AmsPort>0</AmsPort></AmsAddress></EtherCAT></TreeItem>")]
    [InlineData("<TreeItem><EtherCAT><AmsAddress><AmsNetId>nonsense</AmsNetId><AmsPort>1002</AmsPort></AmsAddress></EtherCAT></TreeItem>")]
    [InlineData("<broken")]
    [InlineData(null)]
    public void ParseSlaveAddress_NoUsableAddress_IsNull(string? xml) =>
        Assert.Equal((null, null), EtherCatProjection.ParseSlaveAddress(xml));

    [Fact]
    public void ParseSlaveAddress_RelativeNetId_IsCompletedByTheTargetPrefix()
    {
        var (netId, _) = EtherCatProjection.ParseSlaveAddress(
            "<TreeItem><EtherCAT><AmsAddress><AmsNetId>0.0.0.0.2.1</AmsNetId><AmsPort>1002</AmsPort></AmsAddress></EtherCAT></TreeItem>");
        Assert.Equal("192.168.1.9.2.1", EtherCatProjection.ApplyTargetPrefix(netId!, "192.168.1.9.1.1", out bool relative));
        Assert.True(relative);
    }

    [Fact]
    public void ParseSlaveAddress_AndStartupParser_ReadTheSameDocument()
    {
        // The Startup entries of the same ProduceXml feed the comparison.
        const string xml = """
            <TreeItem><EtherCAT><AmsAddress><AmsNetId>10.0.0.1.2.1</AmsNetId><AmsPort>1002</AmsPort></AmsAddress>
            <Slave><Info><ProductRevision>SYN-0000-0001</ProductRevision></Info>
            <Mailbox><SoE><InitCmds><InitCmd><Transition>PS</Transition><DriveNo>0</DriveNo><IDN>1</IDN><Elements>64</Elements><Data>0100</Data></InitCmd></InitCmds></SoE></Mailbox>
            </Slave></EtherCAT></TreeItem>
            """;
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        var box = EtherCatStartupParser.Parse("p", "n", doc, true, "SoE")!;
        Assert.Equal(1002, EtherCatProjection.ParseSlaveAddress(xml).Port);
        Assert.Equal("S-0-0001", Assert.Single(box.Entries).Idn);
    }
}
