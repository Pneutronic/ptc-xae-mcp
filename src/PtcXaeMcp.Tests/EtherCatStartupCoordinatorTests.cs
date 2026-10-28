using System.Text.Json;
using System.Xml;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class EtherCatStartupCoordinatorTests
{
    private const string Path = "TIID^Device 1 (EtherCAT)^Drive 1";

    private const string DriveXml = """
        <TreeItem><EtherCAT>
          <LinkedAxis>TINC^NC-Task 1 SAF^Axes^Axis 1</LinkedAxis><SoeChannels>1</SoeChannels>
          <Slave><Info><ProductRevision>SYN-0000-0001</ProductRevision><PhysAddr>1002</PhysAddr></Info>
          <Mailbox>
            <SoE><InitCmds>
              <InitCmd Fixed="true"><Transition>PS</Transition><DriveNo>0</DriveNo><IDN>24</IDN><Elements>64</Elements><Data>06000600 86002400 0A80</Data></InitCmd>
              <InitCmd><Transition>PS</Transition><Comment>Mode</Comment><DriveNo>0</DriveNo><IDN>32778</IDN><Elements>64</Elements><Data>0300</Data></InitCmd>
            </InitCmds></SoE>
            <EoE><InitCmds><InitCmd Fixed="true"><Transition>IP</Transition><Transition>PS</Transition><Data>0102</Data></InitCmd></InitCmds>
              <VirtualPort><MacAddr>02 01 05 10 03 fa</MacAddr><IpPort><IpAddr>192.168.1.5</IpAddr></IpPort></VirtualPort></EoE>
          </Mailbox></Slave></EtherCAT></TreeItem>
        """;

    private static EtherCatStartupBox Box(string xml = DriveXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return EtherCatStartupParser.Parse(Path, "Drive 1", doc)!;
    }

    private static FakeTwinCatSession Fake(params EtherCatStartupBox[] boxes) => new()
    {
        IsConnected = true,
        SolutionOpen = true,
        FakeEtherCatStartupResult = new EtherCatStartupResult(boxes, boxes.Length),
    };

    private static (string Summary, JsonElement Json) Read(FakeTwinCatSession fake, string? path = null,
        bool includeFixed = true, string? protocol = null)
    {
        string text = new EtherCatStartupCoordinator(fake).Read(path, includeFixed, protocol);
        int nl = text.IndexOf('\n');
        string summary = (nl < 0 ? text : text[..nl]).TrimEnd('\r');
        return (summary, nl < 0 ? default : JsonDocument.Parse(text[(nl + 1)..]).RootElement.Clone());
    }

    [Fact]
    public void NotConnected_ReturnsError_WithoutCallingTheSession()
    {
        var fake = new FakeTwinCatSession { IsConnected = false };
        string text = new EtherCatStartupCoordinator(fake).Read(null, true, null);
        Assert.StartsWith("[Error]", text);
        Assert.DoesNotContain("ReadEtherCatStartup", string.Join(",", fake.CallLog));
    }

    [Fact]
    public void NoSolution_ReturnsError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new EtherCatStartupCoordinator(fake).Read(null, true, null));
    }

    [Fact]
    public void UnknownProtocol_ReturnsErrorNamingTheValidOnes_WithoutCallingTheSession()
    {
        var fake = Fake();
        string text = new EtherCatStartupCoordinator(fake).Read(null, true, "VoE");
        Assert.StartsWith("[Error]", text);
        Assert.Contains("VoE", text);
        Assert.Contains("CoE, SoE, EoE, FoE", text);
        Assert.Empty(fake.CallLog);
    }

    [Theory]
    [InlineData("TIID")]
    [InlineData("tiid")]
    public void RootPath_ReturnsError(string path)
    {
        var fake = Fake();
        Assert.StartsWith("[Error]", new EtherCatStartupCoordinator(fake).Read(path, true, null));
        Assert.Empty(fake.CallLog);
    }

    [Fact]
    public void SessionFailure_BecomesError()
    {
        var fake = Fake();
        fake.ReadEtherCatStartupThrows = new InvalidOperationException("Tree path not found: x");
        string text = new EtherCatStartupCoordinator(fake).Read("x", true, null);
        Assert.StartsWith("[Error]", text);
        Assert.Contains("Tree path not found: x", text);
    }

    [Fact]
    public void Arguments_ArePassedNormalized()
    {
        var fake = Fake(Box());
        Read(fake, "  " + Path + " ", includeFixed: false, protocol: "soe");
        Assert.Equal($"ReadEtherCatStartup({Path},False,SoE)", fake.CallLog.Single());
    }

    [Fact]
    public void NoPath_ReadsAll_ProtocolCaseInsensitive()
    {
        var fake = Fake(Box());
        Read(fake, null, protocol: "");
        Assert.Equal("ReadEtherCatStartup(,True,)", fake.CallLog.Single());
    }

    [Fact]
    public void Summary_CountsSlavesEntriesProtocolsAndFixed()
    {
        var (summary, _) = Read(Fake(Box()));
        Assert.Equal("1 slave, 3 startup entries (SoE 2, EoE 1) — 2 fixed", summary);
    }

    [Fact]
    public void Summary_AllMode_ReportsScannedSlaves()
    {
        var fake = Fake(Box());
        fake.FakeEtherCatStartupResult = new EtherCatStartupResult([Box()], 12);
        var (summary, _) = Read(fake);
        Assert.EndsWith("[12 slaves scanned]", summary);
    }

    [Fact]
    public void Summary_NoBoxes()
    {
        var (summary, json) = Read(Fake());
        Assert.Equal("0 slaves, 0 startup entries — 0 fixed", summary);
        Assert.Equal(0, json.GetProperty("boxes").GetArrayLength());
    }

    [Fact]
    public void Json_BoxShape()
    {
        var (_, json) = Read(Fake(Box()));
        Assert.Equal(1, json.GetProperty("slaveCount").GetInt32());
        Assert.Equal(3, json.GetProperty("entryCount").GetInt32());
        Assert.Equal(2, json.GetProperty("fixedCount").GetInt32());
        Assert.Equal(2, json.GetProperty("byProtocol").GetProperty("SoE").GetInt32());
        Assert.Equal(1, json.GetProperty("byProtocol").GetProperty("EoE").GetInt32());
        Assert.False(json.GetProperty("byProtocol").TryGetProperty("CoE", out _));

        var box = json.GetProperty("boxes")[0];
        Assert.Equal(Path, box.GetProperty("treePath").GetString());
        Assert.Equal("Drive 1", box.GetProperty("name").GetString());
        Assert.Equal("SYN", box.GetProperty("slaveType").GetString());
        Assert.Equal(1002, box.GetProperty("physAddr").GetInt32());
        Assert.Equal("TINC^NC-Task 1 SAF^Axes^Axis 1", box.GetProperty("linkedAxis").GetString());
        Assert.Equal(1, box.GetProperty("soeChannels").GetInt32());
        Assert.Equal("02 01 05 10 03 fa", box.GetProperty("virtualPort").GetProperty("macAddr").GetString());
        Assert.Equal(3, box.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Json_BoxWithoutLinkedAxis_HasExplicitNull()
    {
        var box = Box("<TreeItem><EtherCAT><Slave><Info><ProductRevision>EL1008-0000-0017</ProductRevision></Info></Slave></EtherCAT></TreeItem>");
        var (_, json) = Read(Fake(box), Path);
        var b = json.GetProperty("boxes")[0];
        Assert.Equal(JsonValueKind.Null, b.GetProperty("linkedAxis").ValueKind);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("virtualPort").ValueKind);
        Assert.Equal(0, b.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void Json_SoeEntry_HasSoeFieldsOnly()
    {
        var (_, json) = Read(Fake(Box()));
        var entries = json.GetProperty("boxes")[0].GetProperty("entries");
        var mdt = entries.EnumerateArray().Single(e => e.TryGetProperty("idn", out var i) && i.GetString() == "S-0-0024");

        Assert.Equal("SoE", mdt.GetProperty("protocol").GetString());
        Assert.True(mdt.GetProperty("fixed").GetBoolean());
        Assert.Equal("PS", mdt.GetProperty("transitions")[0].GetString());
        Assert.Equal("06000600860024000A80", mdt.GetProperty("dataHex").GetString());
        Assert.Equal(24, mdt.GetProperty("idnRaw").GetInt32());
        Assert.Equal(0, mdt.GetProperty("parameterSet").GetInt32());
        Assert.Equal(0, mdt.GetProperty("driveNo").GetInt32());
        Assert.Equal("value", mdt.GetProperty("elements").GetString());
        Assert.Equal(6, mdt.GetProperty("list").GetProperty("currentLength").GetInt32());
        Assert.Equal(6, mdt.GetProperty("list").GetProperty("maxLength").GetInt32());
        Assert.Equal("P-0-0010", mdt.GetProperty("idnList")[2].GetString());
        Assert.False(mdt.TryGetProperty("index", out _));
        Assert.False(mdt.TryGetProperty("value", out _)); // 10 bytes: no scalar

        var projected = entries.EnumerateArray().Single(e => e.TryGetProperty("idn", out var i) && i.GetString() == "P-0-0010");
        Assert.Equal(3, projected.GetProperty("value").GetInt32());
        Assert.Equal("Mode", projected.GetProperty("comment").GetString());
    }

    [Fact]
    public void Json_UnsignedValueAbove32Bit_IsNotTruncated()
    {
        var box = Box("""
            <TreeItem><EtherCAT><Slave><Info><ProductRevision>SYN-0000-0001</ProductRevision></Info>
            <Mailbox><SoE><InitCmds><InitCmd><Transition>PS</Transition><DriveNo>0</DriveNo><IDN>1</IDN><Elements>64</Elements><Data>FFFFFFFFFFFFFFFF</Data></InitCmd></InitCmds></SoE></Mailbox>
            </Slave></EtherCAT></TreeItem>
            """);
        var (_, json) = Read(Fake(box));
        var value = json.GetProperty("boxes")[0].GetProperty("entries")[0].GetProperty("value");
        Assert.Equal(ulong.MaxValue, value.GetUInt64());
    }
}
