using System.Xml;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class EtherCatStartupParserTests
{
    private const string Path = "TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Drive 1 (SoE Drive)";

    private static XmlDocument Doc(string xml)
    {
        var d = new XmlDocument();
        d.LoadXml(xml);
        return d;
    }

    private static EtherCatStartupBox Parse(string xml, bool includeFixed = true, string? protocol = null) =>
        EtherCatStartupParser.Parse(Path, "Drive 1 (SoE Drive)", Doc(xml), includeFixed, protocol)!;

    // Synthetic drive: 3 fixed SoE entries (one twice, DriveNo 0 and 4), 2 projected SoE entries, EoE init fixed with
    // two transitions, virtual port, ESC register InitCmds that must be ignored.
    private const string Drive = """
        <TreeItem>
          <ItemSubTypeName>Synthetic Drive</ItemSubTypeName>
          <EtherCAT>
            <AmsAddress><AmsNetId>10.0.0.1.2.1</AmsNetId><AmsPort>1002</AmsPort></AmsAddress>
            <LinkedAxis>TINC^NC-Task 1 SAF^Axes^Axis 1</LinkedAxis>
            <SoeChannels>1</SoeChannels>
            <Slave>
              <Info>
                <VendorId>2</VendorId><ProductCode>1</ProductCode><RevisionNo>3</RevisionNo>
                <PhysAddr>1002</PhysAddr>
                <ProductRevision>SYN-0000-0001</ProductRevision>
              </Info>
              <InitCmds>
                <InitCmd><Transition>PS</Transition><Comment><![CDATA[set sm 0]]></Comment><Addr>2064</Addr><Data>0010</Data></InitCmd>
              </InitCmds>
              <Mailbox>
                <SoE>
                  <InitCmds>
                    <InitCmd Fixed="true">
                      <Transition>PS</Transition>
                      <Comment><![CDATA[Telegram type]]></Comment>
                      <Timeout>3000</Timeout><OpCode>3</OpCode><DriveNo>0</DriveNo><IDN>15</IDN>
                      <Elements>64</Elements><Attribute>0</Attribute><Data>0300</Data>
                    </InitCmd>
                    <InitCmd Fixed="true">
                      <Transition>PS</Transition>
                      <Timeout>3000</Timeout><OpCode>3</OpCode><DriveNo>4</DriveNo><IDN>15</IDN>
                      <Elements>64</Elements><Attribute>0</Attribute><Data>0300</Data>
                    </InitCmd>
                    <InitCmd Fixed="true">
                      <Transition>PS</Transition>
                      <Comment><![CDATA[MDT list]]></Comment>
                      <Timeout>3000</Timeout><OpCode>3</OpCode><DriveNo>0</DriveNo><IDN>24</IDN>
                      <Elements>64</Elements><Attribute>0</Attribute><Data>06000600 86002400 0A80</Data>
                    </InitCmd>
                    <InitCmd>
                      <Transition>PS</Transition>
                      <Comment><![CDATA[Control word source]]></Comment>
                      <Timeout>3000</Timeout><OpCode>3</OpCode><DriveNo>0</DriveNo><IDN>1</IDN>
                      <Elements>64</Elements><Attribute>0</Attribute><Data>A00F</Data>
                    </InitCmd>
                    <InitCmd>
                      <Transition>PS</Transition><Transition>SO</Transition>
                      <Timeout>5000</Timeout><OpCode>3</OpCode><DriveNo>1</DriveNo><IDN>32778</IDN>
                      <Elements>64</Elements><Attribute>0</Attribute><Data>0100000000000000</Data>
                    </InitCmd>
                  </InitCmds>
                </SoE>
                <EoE>
                  <InitCmds>
                    <InitCmd Fixed="true">
                      <Transition>IP</Transition><Transition>PS</Transition>
                      <Comment><![CDATA[EoE init]]></Comment><Timeout>1000</Timeout>
                      <Data>0102030405</Data>
                    </InitCmd>
                  </InitCmds>
                  <VirtualPort>
                    <MacAddr>02 01 05 10 03 fa</MacAddr>
                    <IpPort>
                      <IpAddr>192.168.1.5</IpAddr><Subnet>255.255.255.0</Subnet><Gateway>192.168.1.1</Gateway>
                      <DnsAddr>0.0.0.0</DnsAddr><DnsName>drive1</DnsName>
                    </IpPort>
                  </VirtualPort>
                </EoE>
                <CoE>
                  <InitCmds>
                    <InitCmd Fixed="true">
                      <Transition>PS</Transition><Comment><![CDATA[clear sm pdos (0x1C12)]]></Comment>
                      <Ccs>1</Ccs><Index>7186</Index><SubIndex>0</SubIndex><Data>00</Data>
                    </InitCmd>
                    <InitCmd>
                      <Transition>PS</Transition><Transition>SO</Transition>
                      <Timeout>2000</Timeout><Index>#x8000</Index><SubIndex>2</SubIndex><Data>0100</Data>
                    </InitCmd>
                  </InitCmds>
                </CoE>
                <FoE>
                  <InitCmds>
                    <InitCmd><Transition>PS</Transition><Comment><![CDATA[load firmware]]></Comment><Timeout>9000</Timeout></InitCmd>
                  </InitCmds>
                </FoE>
              </Mailbox>
              <ProcessData>
                <AlternativeSmMappings>
                  <AlternativeSmMapping Selected="true">
                    <OperationMode>
                      <InitCmds CompleteAccess="1" ChannelNo="1">
                        <Transition>PS</Transition><Index>#x8010</Index><SubIndex>1</SubIndex><Data>00000200</Data>
                      </InitCmds>
                      <InitCmds CompleteAccess="1" DefaultSettings="1" ChannelNo="1">
                        <Transition>PS</Transition><Index>#x8011</Index><SubIndex>1</SubIndex><Data>0000</Data>
                      </InitCmds>
                    </OperationMode>
                  </AlternativeSmMapping>
                </AlternativeSmMappings>
              </ProcessData>
            </Slave>
          </EtherCAT>
        </TreeItem>
        """;

    private static IEnumerable<EtherCatStartupEntry> Of(EtherCatStartupBox b, string protocol) =>
        b.Entries.Where(e => e.Protocol == protocol);

    [Fact]
    public void BoxHeader_CarriesIdentityLinkedAxisAndSoeChannels()
    {
        var box = Parse(Drive);
        Assert.Equal(Path, box.TreePath);
        Assert.Equal("Drive 1 (SoE Drive)", box.Name);
        Assert.Equal("Synthetic Drive", box.SlaveType);
        Assert.Equal(1002, box.PhysAddr);
        Assert.Equal("TINC^NC-Task 1 SAF^Axes^Axis 1", box.LinkedAxis);
        Assert.Equal(1, box.SoeChannels);
    }

    [Fact]
    public void Soe_FixedAndProjected_AreBothReturnedWithFlag()
    {
        var soe = Of(Parse(Drive), "SoE").ToList();
        Assert.Equal(5, soe.Count);
        Assert.Equal(3, soe.Count(e => e.Fixed));
        Assert.Equal(2, soe.Count(e => !e.Fixed));
    }

    [Fact]
    public void Soe_FixedEntry_DuplicatedPerDriveNo_IsKeptTwice()
    {
        var s15 = Of(Parse(Drive), "SoE").Where(e => e.Idn == "S-0-0015").ToList();
        Assert.Equal(2, s15.Count);
        Assert.Equal([0, 4], s15.Select(e => e.DriveNo!.Value).ToArray());
    }

    [Fact]
    public void Soe_Entry_DecodesIdnElementsAndValue()
    {
        var e = Of(Parse(Drive), "SoE").Single(x => x.Idn == "S-0-0001");
        Assert.Equal(1, e.IdnRaw);
        Assert.Equal(0, e.ParameterSet);
        Assert.Equal(0, e.DriveNo);
        Assert.Equal(3, e.OpCode);
        Assert.Equal("value", e.Elements);
        Assert.Equal(0, e.Attribute);
        Assert.Equal("A00F", e.DataHex);
        Assert.Equal(0x0FA0UL, e.Value);
        Assert.Equal(3000, e.TimeoutMs);
        Assert.Equal("Control word source", e.Comment);
        Assert.Equal(["PS"], e.Transitions);
    }

    [Fact]
    public void Soe_ParameterIdn_AndMultipleTransitions_AndDriveNoNotZero()
    {
        var e = Of(Parse(Drive), "SoE").Single(x => x.Idn == "P-0-0010");
        Assert.Equal(32778, e.IdnRaw);
        Assert.Equal(1, e.DriveNo);
        Assert.Equal(["PS", "SO"], e.Transitions);
        Assert.Equal(5000, e.TimeoutMs);
        Assert.Equal(1UL, e.Value); // 8 data bytes, unsigned little endian
        Assert.Null(e.Comment);
    }

    [Fact]
    public void Soe_MdtList_DecodedAsIdnList_WithListHeader()
    {
        var e = Of(Parse(Drive), "SoE").Single(x => x.Idn == "S-0-0024");
        Assert.Equal("06000600860024000A80", e.DataHex);
        Assert.Null(e.Value); // 10 bytes is no scalar
        Assert.Equal(new SoeListHeader(6, 6), e.List);
        Assert.Equal(["S-0-0134", "S-0-0036", "P-0-0010"], e.IdnList);
    }

    [Fact]
    public void Soe_TwoByteScalar_HasNoListAndNoIdnList()
    {
        var e = Of(Parse(Drive), "SoE").First(x => x.Idn == "S-0-0015");
        Assert.Equal(3UL, e.Value);
        Assert.Null(e.List);
        Assert.Null(e.IdnList);
    }

    [Theory]
    [InlineData("0100", 1UL)]
    [InlineData("01000000", 1UL)]
    [InlineData("0100000000000000", 1UL)]
    [InlineData("FFFFFFFF", 0xFFFFFFFFUL)]
    [InlineData("FFFFFFFFFFFFFFFF", ulong.MaxValue)]
    public void Soe_Value_DecodedForTwoFourEightBytes(string hex, ulong expected)
    {
        var box = Parse(SoeOnly(hex, 1));
        Assert.Equal(expected, box.Entries.Single().Value);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("010203")]
    [InlineData("0102030405")]
    [InlineData("")]
    public void Soe_Value_NullForOtherLengths(string hex)
    {
        var e = Parse(SoeOnly(hex, 1)).Entries.Single();
        Assert.Null(e.Value);
        Assert.Equal(hex, e.DataHex);
    }

    [Fact]
    public void Soe_OddIdnListLength_IsNotDecodedAsIdnList()
    {
        // header says 5 bytes of data (odd) for the AT list: not a list of u16 IDNs
        var e = Parse(SoeOnly("0500 0500 3300 2800 01", 16)).Entries.Single();
        Assert.Equal(new SoeListHeader(5, 5), e.List);
        Assert.Null(e.IdnList);
    }

    [Fact]
    public void Soe_ListHeaderThatDoesNotFitTheData_IsNotReported()
    {
        // says 8 bytes but only 2 follow
        var e = Parse(SoeOnly("0800 0800 3300", 16)).Entries.Single();
        Assert.Null(e.List);
        Assert.Null(e.IdnList);
    }

    [Fact]
    public void Soe_MissingComment_IsNull()
    {
        var e = Parse(SoeOnly("0100", 1)).Entries.Single();
        Assert.Null(e.Comment);
    }

    [Fact]
    public void Eoe_Entry_HasTransitionsDataAndNoIdn_VirtualPortOnBox()
    {
        var box = Parse(Drive);
        var e = Of(box, "EoE").Single();
        Assert.True(e.Fixed);
        Assert.Equal(["IP", "PS"], e.Transitions);
        Assert.Equal("EoE init", e.Comment);
        Assert.Equal(1000, e.TimeoutMs);
        Assert.Equal("0102030405", e.DataHex);
        Assert.Null(e.Idn);
        Assert.Equal(new EtherCatEoeVirtualPort("02 01 05 10 03 fa", "192.168.1.5", "255.255.255.0", "192.168.1.1", "0.0.0.0", "drive1"), box.VirtualPort);
    }

    [Fact]
    public void Coe_MailboxEntries_FixedAndProjected_WithAllTransitions()
    {
        var coe = Of(Parse(Drive), "CoE").Where(e => e.Source == "mailbox").ToList();
        Assert.Equal(2, coe.Count);

        var fixedEntry = coe.Single(e => e.Fixed);
        Assert.Equal("1C12", fixedEntry.Index);
        Assert.Equal("0", fixedEntry.SubIndex);
        Assert.True(fixedEntry.CompleteAccess);

        var projected = coe.Single(e => !e.Fixed);
        Assert.Equal("8000", projected.Index);
        Assert.Equal("2", projected.SubIndex);
        Assert.False(projected.CompleteAccess);
        Assert.Equal(["PS", "SO"], projected.Transitions);
        Assert.Equal("0100", projected.DataHex);
    }

    [Fact]
    public void Coe_OperationMode_IgnoresDefaultSettings()
    {
        var op = Of(Parse(Drive), "CoE").Where(e => e.Source == "operationMode").ToList();
        var e = Assert.Single(op);
        Assert.Equal("8010", e.Index);
        Assert.True(e.CompleteAccess);
        Assert.False(e.Fixed);
    }

    [Fact]
    public void Foe_EntryHasOnlyCommonFields()
    {
        var e = Of(Parse(Drive), "FoE").Single();
        Assert.Equal("load firmware", e.Comment);
        Assert.Equal(9000, e.TimeoutMs);
        Assert.Equal(["PS"], e.Transitions);
        Assert.Null(e.Idn);
        Assert.Null(e.Index);
    }

    [Fact]
    public void EscRegisterInitCmds_AreIgnored()
    {
        var box = Parse(Drive);
        Assert.DoesNotContain(box.Entries, e => e.Comment == "set sm 0");
    }

    [Fact]
    public void IncludeFixedFalse_DropsFixedEntriesOfEveryProtocol()
    {
        var box = Parse(Drive, includeFixed: false);
        Assert.All(box.Entries, e => Assert.False(e.Fixed));
        Assert.Equal(2, Of(box, "SoE").Count());
        Assert.Empty(Of(box, "EoE"));
    }

    [Theory]
    [InlineData("CoE", 3)]
    [InlineData("SoE", 5)]
    [InlineData("EoE", 1)]
    [InlineData("FoE", 1)]
    public void ProtocolFilter_KeepsOnlyThatProtocol(string protocol, int count)
    {
        var box = Parse(Drive, protocol: protocol);
        Assert.Equal(count, box.Entries.Count);
        Assert.All(box.Entries, e => Assert.Equal(protocol, e.Protocol));
    }

    [Fact]
    public void ProtocolFilter_WithoutEoE_OmitsVirtualPort()
    {
        Assert.Null(Parse(Drive, protocol: "SoE").VirtualPort);
        Assert.NotNull(Parse(Drive, protocol: "EoE").VirtualPort);
    }

    [Fact]
    public void BoxWithoutMailbox_HasNoEntries()
    {
        var box = Parse("""
            <TreeItem><EtherCAT><Slave><Info><ProductRevision>EL1008-0000-0017</ProductRevision><PhysAddr>1001</PhysAddr></Info>
            <InitCmds><InitCmd><Transition>PS</Transition><Addr>2064</Addr><Data>00</Data></InitCmd></InitCmds>
            </Slave></EtherCAT></TreeItem>
            """);
        Assert.Empty(box.Entries);
        Assert.Equal("EL1008", box.SlaveType);
        Assert.Null(box.VirtualPort);
        Assert.Null(box.LinkedAxis);
        Assert.Null(box.SoeChannels);
    }

    [Fact]
    public void NodeWithoutEtherCatSlave_ReturnsNull() =>
        Assert.Null(EtherCatStartupParser.Parse(Path, "x", Doc("<TreeItem><ItemName>Box</ItemName></TreeItem>")));

    [Fact]
    public void EntryOrder_IsCoeSoeEoeFoe()
    {
        var protocols = Parse(Drive).Entries.Select(e => e.Protocol).Distinct().ToArray();
        Assert.Equal(["CoE", "SoE", "EoE", "FoE"], protocols);
    }

    [Theory]
    [InlineData("CoE", "CoE")]
    [InlineData("coe", "CoE")]
    [InlineData("SOE", "SoE")]
    [InlineData("eoe", "EoE")]
    [InlineData("foe", "FoE")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NormalizeProtocol_IsCaseInsensitive(string? input, string? expected)
    {
        Assert.True(EtherCatStartupProtocol.TryNormalize(input, out string? p));
        Assert.Equal(expected, p);
    }

    [Fact]
    public void NormalizeProtocol_RejectsUnknown() =>
        Assert.False(EtherCatStartupProtocol.TryNormalize("VoE", out _));

    private static string SoeOnly(string data, int idn) => $"""
        <TreeItem><EtherCAT><Slave><Info><ProductRevision>SYN-0000-0001</ProductRevision></Info>
        <Mailbox><SoE><InitCmds>
          <InitCmd><Transition>PS</Transition><DriveNo>0</DriveNo><IDN>{idn}</IDN><Elements>64</Elements><Data>{data}</Data></InitCmd>
        </InitCmds></SoE></Mailbox></Slave></EtherCAT></TreeItem>
        """;

    // ── Tree walk ───────────────────────────────────────────────────────────

    private sealed class Node(string name, string path, string xml, params Node[] children) : EtherCatProjection.IProjectionNode
    {
        public string Name => name;
        public string Path => path;
        public string Xml => xml;
        public IEnumerable<EtherCatProjection.IProjectionNode> Children => children;
    }

    private const string Coupler = "<TreeItem><EtherCAT><Slave><Info><ProductRevision>EK1100-0000-0017</ProductRevision></Info></Slave></EtherCAT></TreeItem>";

    [Fact]
    public void ReadAll_ListsOnlyBoxesWithEntries_AndCountsScanned()
    {
        var master = new Node("Device 1 (EtherCAT)", "TIID^Device 1 (EtherCAT)", "<TreeItem/>",
            new Node("Box 1 (EK1100)", "TIID^Device 1 (EtherCAT)^Box 1 (EK1100)", Coupler,
                new Node("Drive 1", Path, Drive),
                new Node("Term", "TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term", Coupler)),
            new Node("Image", "TIID^Device 1 (EtherCAT)^Image", "<TreeItem/>"));

        var result = EtherCatStartupReader.ReadAll([master], includeFixed: true, protocol: null);

        var box = Assert.Single(result.Boxes);
        Assert.Equal(Path, box.TreePath);
        Assert.Equal("Drive 1", box.Name);
        Assert.Equal(3, result.SlavesScanned);
    }

    [Fact]
    public void ReadAll_BoxReachableTwice_IsListedOnce()
    {
        var drive = new Node("Drive 1", Path, Drive);
        var master = new Node("Device 1", "TIID^Device 1", "<TreeItem/>", drive, drive);
        Assert.Single(EtherCatStartupReader.ReadAll([master], true, null).Boxes);
    }

    [Fact]
    public void ReadAll_FilterThatRemovesAllEntries_DropsTheBox()
    {
        var master = new Node("Device 1", "TIID^Device 1", "<TreeItem/>", new Node("Drive 1", Path, Drive));
        var result = EtherCatStartupReader.ReadAll([master], includeFixed: true, protocol: "FoE");
        Assert.Single(result.Boxes);
        result = EtherCatStartupReader.ReadAll([master], includeFixed: false, protocol: "EoE");
        Assert.Empty(result.Boxes);
        Assert.Equal(1, result.SlavesScanned);
    }

    [Fact]
    public void ReadAll_MalformedXml_IsSkippedNotFatal()
    {
        var master = new Node("Device 1", "TIID^Device 1", "<TreeItem/>",
            new Node("Broken", "TIID^Device 1^Broken", "<TreeItem><oops>"),
            new Node("Drive 1", Path, Drive));
        Assert.Single(EtherCatStartupReader.ReadAll([master], true, null).Boxes);
    }

    [Fact]
    public void Totals_CountEntriesPerProtocolAndFixed()
    {
        var result = EtherCatStartupReader.ReadAll(
            [new Node("Device 1", "TIID^Device 1", "<TreeItem/>", new Node("Drive 1", Path, Drive))], true, null);
        Assert.Equal(10, result.EntryCount);
        Assert.Equal(3, result.CountByProtocol("CoE"));
        Assert.Equal(5, result.CountByProtocol("SoE"));
        Assert.Equal(1, result.CountByProtocol("EoE"));
        Assert.Equal(1, result.CountByProtocol("FoE"));
        Assert.Equal(5, result.FixedCount); // 3 SoE + 1 EoE + 1 CoE
    }
}
