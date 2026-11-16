using PtcXaeMcp.WinForms.TwinCat.Apply;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The PDO list of a terminal from its ProduceXml (pure). Fixtures under Fixtures/Pdo are live dumps of an
/// EL3174 (inputs, AlternativeSmMappings) and an EL4004 (outputs, no mapping block), trimmed to ProcessData.
/// </summary>
public class TerminalPdoMapTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pdo", name));

    [Fact]
    public void El3174_ActivePdosAreTheStandardOnes_CompactAreInactive()
    {
        TerminalPdoMap map = TerminalPdoMap.Parse(Fixture("EL3174_ProduceXml.xml"))!;

        map.Pdos.Should().HaveCount(8);
        map.Active("I").Select(p => p.Name).Should().Equal(
            "AI Standard Channel 1", "AI Standard Channel 2", "AI Standard Channel 3", "AI Standard Channel 4");
        map.Active("Q").Should().BeEmpty();
        map.Pdos.Single(p => p.Name == "AI Compact Channel 1").Active.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 0x1a00)]
    [InlineData(2, 0x1a02)]
    [InlineData(3, 0x1a04)]
    [InlineData(4, 0x1a06)]
    public void El3174_ChannelComesFromTheSelectedMapping(int channel, int pdoIndex)
    {
        TerminalPdoMap map = TerminalPdoMap.Parse(Fixture("EL3174_ProduceXml.xml"))!;

        map.ForChannel(channel, "I").Should().ContainSingle().Which.Index.Should().Be(pdoIndex);
        map.ForChannel(channel, "Q").Should().BeEmpty();
    }

    [Fact]
    public void El3174_GapEntriesAreMarked_SubItemsGetATreePath()
    {
        TerminalPdo pdo = TerminalPdoMap.Parse(Fixture("EL3174_ProduceXml.xml"))!.ForChannel(1, "I").Single();

        pdo.Entries.Count(e => e.IsGap).Should().Be(2);
        pdo.Entries.Single(e => e.Name == "Status__Error").TreeName.Should().Be("Status^Error");
        PdoEntry value = pdo.Entries.Single(e => e.Name == "Value");
        value.BitLen.Should().Be(16);
        value.TreeName.Should().Be("Value");
    }

    [Fact]
    public void El4004_OutputsWithoutMapping_FoundByChannelName()
    {
        TerminalPdoMap map = TerminalPdoMap.Parse(Fixture("EL4004_ProduceXml.xml"))!;

        TerminalPdo pdo = map.ForChannel(2, "Q").Should().ContainSingle().Subject;
        pdo.Name.Should().Be("AO Outputs Channel 2");
        pdo.Entries.Should().ContainSingle().Which.Name.Should().Be("Analog output");
        map.ForChannel(2, "I").Should().BeEmpty();
        map.ForChannel(5, "Q").Should().BeEmpty();
    }

    [Fact]
    public void TheMappingBeatsTheName()
    {
        // The mapping assigns channel 1 to a PDO whose name says 2; the name is not consulted.
        const string xml = """
            <TreeItem><EtherCAT><Slave><ProcessData>
              <TxPdo Sm="3"><Index>#x1a00</Index><Name>In Channel 2</Name><Entry><Index>#x6000</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
              <TxPdo Sm="3"><Index>#x1a01</Index><Name>In Channel 1</Name><Entry><Index>#x6010</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
              <AlternativeSmMappings><AlternativeSmMapping Selected="true"><Sm No="3">
                <Pdo ChannelNo="1">#x1a00</Pdo><Pdo ChannelNo="2">#x1a01</Pdo>
              </Sm></AlternativeSmMapping></AlternativeSmMappings>
            </ProcessData></Slave></EtherCAT></TreeItem>
            """;

        TerminalPdoMap.Parse(xml)!.ForChannel(1, "I").Should().ContainSingle().Which.Index.Should().Be(0x1a00);
    }

    [Fact]
    public void ANotSelectedMappingIsIgnored()
    {
        const string xml = """
            <TreeItem><EtherCAT><Slave><ProcessData>
              <TxPdo Sm="3"><Index>#x1a00</Index><Name>In Channel 1</Name><Entry><Index>#x6000</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
              <TxPdo><Index>#x1a01</Index><Name>Other Channel 1</Name><Entry><Index>#x6000</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
              <AlternativeSmMappings><AlternativeSmMapping Selected="false"><Sm No="3"><Pdo ChannelNo="1">#x1a01</Pdo></Sm></AlternativeSmMapping></AlternativeSmMappings>
            </ProcessData></Slave></EtherCAT></TreeItem>
            """;

        TerminalPdoMap.Parse(xml)!.ForChannel(1, "I").Should().ContainSingle().Which.Index.Should().Be(0x1a00);
    }

    [Fact]
    public void ChannelOneNeverMatchesChannelTen()
    {
        const string xml = """
            <TreeItem><EtherCAT><Slave><ProcessData>
              <TxPdo Sm="3"><Index>#x1a09</Index><Name>AI Channel 10</Name><Entry><Index>#x6090</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
              <TxPdo Sm="3"><Index>#x1a00</Index><Name>AI Kanal 1</Name><Entry><Index>#x6000</Index><BitLen>16</BitLen><Name>Value</Name></Entry></TxPdo>
            </ProcessData></Slave></EtherCAT></TreeItem>
            """;

        TerminalPdoMap map = TerminalPdoMap.Parse(xml)!;

        map.ForChannel(1, "I").Should().ContainSingle().Which.Name.Should().Be("AI Kanal 1");
        map.ForChannel(10, "I").Should().ContainSingle().Which.Name.Should().Be("AI Channel 10");
    }

    [Theory]
    [InlineData("<TreeItem><ItemName>K1</ItemName></TreeItem>")]
    [InlineData("<TreeItem><EtherCAT><Slave><ProcessData><Sm2/></ProcessData></Slave></EtherCAT></TreeItem>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void WithoutPdos_ThereIsNoMap(string xml) => TerminalPdoMap.Parse(xml).Should().BeNull();

    [Theory]
    [InlineData(null, 1)]
    [InlineData("BOOL", 1)]
    [InlineData("usint", 8)]
    [InlineData("INT", 16)]
    [InlineData("WORD", 16)]
    [InlineData("REAL", 32)]
    [InlineData("LREAL", 64)]
    [InlineData("STRING", null)]
    [InlineData("ST_Custom", null)]
    public void BitWidthOfTheType(string? type, int? width) => TerminalPdoMap.BitWidth(type).Should().Be(width);
}
