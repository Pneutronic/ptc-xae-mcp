using System.Xml.Linq;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The event-class XML (TcModuleClass DataType with EventId, as in TcStandardEvents.tmc), ExternalTypes.tmc and the
/// .plcproj entry "Add Global Data Type" writes.
/// </summary>
public sealed class EventClassXmlTests
{
    private static readonly EventClassEvent[] TwoEvents =
    [
        new(1, "Started", "Info", "Spike started"),
        new(2, "Failed", "Error", "Spike failed {0}"),
    ];

    // What ITcTypeInstance.ExportXml(false) returned live for a class created by this server (4026.22).
    private const string Exported =
        "<?xml version=\"1.0\"?><DataTypes><DataType><Name GUID=\"{6873311E-B133-4D19-8C66-3A927E935CEC}\" PersistentType=\"true\">EvSpikeP5</Name>" +
        "<DisplayName TxtId=\"\"><![CDATA[P5 display]]></DisplayName><EventId><Name Id=\"1\">Started</Name><DisplayName TxtId=\"\"><![CDATA[P5 started]]></DisplayName>" +
        "<Severity>Info</Severity></EventId><EventId><Name Id=\"2\">Failed</Name><DisplayName TxtId=\"\"><![CDATA[P5 failed {0}]]></DisplayName>" +
        "<Severity>Error</Severity></EventId></DataType></DataTypes>";

    [Fact]
    public void BuildDataType_IsPersistent_HasNoGuidUnlessGiven_AndKeepsTheTexts()
    {
        string xml = EventClassXml.BuildDataType("MyEvents", null, "My <events> & more", TwoEvents);

        var type = XElement.Parse(xml);
        type.Element("Name")!.Value.Should().Be("MyEvents");
        ((string?)type.Element("Name")!.Attribute("PersistentType")).Should().Be("true");
        type.Element("Name")!.Attribute("GUID").Should().BeNull();
        type.Element("DisplayName")!.Value.Should().Be("My <events> & more");
        xml.Should().Contain("<![CDATA[Spike failed {0}]]>");
        type.Elements("EventId").Select(e => (string?)e.Element("Name")!.Attribute("Id")).Should().Equal("1", "2");
        type.Elements("EventId").Select(e => e.Element("Severity")!.Value).Should().Equal("Info", "Error");

        string withGuid = EventClassXml.BuildDataType("MyEvents", "6873311e-b133-4d19-8c66-3a927e935cec", "x", TwoEvents);
        withGuid.Should().Contain("GUID=\"{6873311E-B133-4D19-8C66-3A927E935CEC}\"");
    }

    [Fact]
    public void Parse_ReadsTheExportedClass()
    {
        var info = EventClassXml.Parse(Exported)!;

        info.Name.Should().Be("EvSpikeP5");
        info.Guid.Should().Be("{6873311E-B133-4D19-8C66-3A927E935CEC}");
        info.DisplayText.Should().Be("P5 display");
        info.Persistent.Should().BeTrue();
        info.BuiltIn.Should().BeFalse();
        info.Events.Should().Equal(new EventClassEvent(1, "Started", "Info", "P5 started"), new EventClassEvent(2, "Failed", "Error", "P5 failed {0}"));
    }

    [Fact]
    public void Parse_BuiltInClass_AndHexIds()
    {
        const string builtIn = "<DataType><Name GUID=\"{bb2a9999-102e-421a-8d3d-b0660e07b1fe}\" TcBaseType=\"true\" CName=\"TcSystemEventClass\">TcSystemEventClass</Name>" +
                               "<DisplayName TxtId=\"\"><![CDATA[TcSystemEventClass]]></DisplayName><EventId><Name Id=\"#x10\">Sixteen</Name>" +
                               "<DisplayName TxtId=\"\"><![CDATA[x]]></DisplayName><Severity>Error</Severity></EventId></DataType>";

        var info = EventClassXml.Parse(builtIn)!;

        info.BuiltIn.Should().BeTrue();
        info.Persistent.Should().BeFalse();
        info.Events.Single().Id.Should().Be(16u);
    }

    [Theory]
    [InlineData("<DataType><Name>ST_Thing</Name><BitSize>8</BitSize></DataType>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void Parse_NoEventClass_IsNull(string xml) => EventClassXml.Parse(xml).Should().BeNull();

    [Theory]
    [InlineData("info", "Info")]
    [InlineData("CRITICAL", "Critical")]
    [InlineData(null, "Info")]
    [InlineData(" ", "Info")]
    [InlineData("Fatal", null)]
    public void NormalizeSeverity(string? given, string? expected) => EventClassXml.NormalizeSeverity(given).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-8\"?>")] // what the XAE leaves behind after dropping the last pin
    public void UpsertIntoTmc_WithoutDocument_CreatesTheTcModuleClass(string? current)
    {
        string dataType = EventClassXml.DataTypeElement(Exported)!;

        string tmc = EventClassXml.UpsertIntoTmc(current, dataType);

        tmc.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        var doc = XDocument.Parse(tmc);
        doc.Root!.Name.LocalName.Should().Be("TcModuleClass");
        EventClassXml.EventClassesInTmc(tmc).Select(c => c.Name).Should().Equal("EvSpikeP5");
    }

    [Fact]
    public void UpsertIntoTmc_ReplacesTheSameClass_AndKeepsOthers()
    {
        const string gui = "﻿<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<TcModuleClass xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance2\" " +
                           "xsi:noNamespaceSchemaLocation=\"http://www.beckhoff.com/schemas/2009/05/TcModuleClass\" GeneratedBy=\"TwinCAT XAE Plc\">\r\n" +
                           "  <DataTypes>\r\n    <DataType><Name GUID=\"{11111111-1111-1111-1111-111111111111}\">Other</Name><EventId><Name Id=\"1\">A</Name></EventId></DataType>\r\n" +
                           "    <DataType><Name GUID=\"{6873311E-B133-4D19-8C66-3A927E935CEC}\" PersistentType=\"true\">EvSpikeP5</Name><EventId><Name Id=\"1\">Old</Name></EventId></DataType>\r\n" +
                           "  </DataTypes>\r\n</TcModuleClass>";

        string tmc = EventClassXml.UpsertIntoTmc(gui.TrimStart('﻿'), EventClassXml.DataTypeElement(Exported)!);

        var classes = EventClassXml.EventClassesInTmc(tmc);
        classes.Select(c => c.Name).Should().Equal("Other", "EvSpikeP5");
        classes[1].Events.Select(e => e.Name).Should().Equal("Started", "Failed");
        tmc.Should().Contain("XMLSchema-instance2"); // the XAE's own root stays as it is
    }

    [Fact]
    public void EnsureExternalTypesItem_AddsTheEntryOnce_AndKeepsLineEnds()
    {
        const string plcproj = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
                               "  <ItemGroup>\r\n    <None Include=\"Plc.tmc\">\r\n      <SubType>Content</SubType>\r\n    </None>\r\n  </ItemGroup>\r\n" +
                               "  <ProjectExtensions>\r\n    <PlcProjectOptions />\r\n  </ProjectExtensions>\r\n</Project>";

        string once = EventClassXml.EnsureExternalTypesItem(plcproj);
        string twice = EventClassXml.EnsureExternalTypesItem(once);

        once.Replace("\r", "\\r").Should().Contain("<None Include=\"ExternalTypes.tmc\">\\r\n      <SubType>Content</SubType>\\r\n    </None>");
        once.IndexOf("ExternalTypes.tmc", StringComparison.Ordinal).Should().BeLessThan(once.IndexOf("<ProjectExtensions>", StringComparison.Ordinal));
        once.Should().NotContain("xmlns=\"\"");
        once.Replace("\r\n", "").Should().NotContain("\n");
        twice.Should().Be(once);
        var item = XDocument.Parse(once).Descendants().Single(e => (string?)e.Attribute("Include") == "ExternalTypes.tmc");
        item.Name.LocalName.Should().Be("None");
        item.Name.NamespaceName.Should().Be("http://schemas.microsoft.com/developer/msbuild/2003");
    }

    [Fact]
    public void ApplyUpdate_ReplacesEventsById_KeepsGuidAndUnmanagedDetails()
    {
        const string existing = "<DataType><Name GUID=\"{6873311E-B133-4D19-8C66-3A927E935CEC}\">MyEvents</Name><DisplayName TxtId=\"\"><![CDATA[old]]></DisplayName>" +
                                "<EventId><Name Id=\"1\">Started</Name><DisplayName TxtId=\"\"><![CDATA[s]]></DisplayName><Severity>Info</Severity>" +
                                "<CauseRemedy><Cause TxtId=\"\"><![CDATA[c]]></Cause></CauseRemedy></EventId>" +
                                "<EventId><Name Id=\"2\">Gone</Name><DisplayName TxtId=\"\"><![CDATA[g]]></DisplayName><Severity>Info</Severity></EventId></DataType>";
        var events = new[] { new EventClassEvent(1, "Begun", "Warning", "b"), new EventClassEvent(3, "New", "Critical", "n") };

        string updated = EventClassXml.ApplyUpdate(existing, "new display", events);

        var info = EventClassXml.Parse(updated)!;
        info.Guid.Should().Be("{6873311E-B133-4D19-8C66-3A927E935CEC}");
        info.Persistent.Should().BeTrue();
        info.DisplayText.Should().Be("new display");
        info.Events.Should().Equal(new EventClassEvent(1, "Begun", "Warning", "b"), new EventClassEvent(3, "New", "Critical", "n"));
        var first = XElement.Parse(updated).Elements("EventId").First();
        first.Elements().Select(e => e.Name.LocalName).Should().Equal("Name", "DisplayName", "Severity", "CauseRemedy");
    }

    [Fact]
    public void ApplyUpdate_OnlyDisplayText_KeepsTheEvents()
    {
        string updated = EventClassXml.ApplyUpdate(Exported, "renamed", null);

        var info = EventClassXml.Parse(updated)!;
        info.DisplayText.Should().Be("renamed");
        info.Events.Select(e => e.Name).Should().Equal("Started", "Failed");
    }

    [Theory]
    [InlineData("Global.ST_MyEvents", true)]
    [InlineData("Global.MyEvents", true)]
    [InlineData("ST_MyEvents", true)]
    [InlineData("Global.ST_MyEventsX", false)]
    public void PinNodeIsClass(string node, bool expected) => EventClassXml.PinNodeIsClass(node, "MyEvents").Should().Be(expected);
}
