using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_plc_project_info: reading the <ProjectInfo> section of the PLC project item, setting only the given fields,
// the read-back check and the save.
public class PlcProjectInfoTests
{
    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    [Fact]
    public void Parse_ReadsEveryFieldOfTheProjectInfoSection()
    {
        const string xml = "<TreeItem><IECProjectDef><ProjectInfo><Company /><Title>Lib</Title><Version>1.0.0.0</Version>" +
                           "<Released>true</Released></ProjectInfo><CompilerSettings /></IECProjectDef></TreeItem>";
        PlcProjectInfoXml.Parse(xml).Should().Equal(
            new PlcProjectInfoField("Company", ""), new PlcProjectInfoField("Title", "Lib"),
            new PlcProjectInfoField("Version", "1.0.0.0"), new PlcProjectInfoField("Released", "true"));
    }

    [Fact]
    public void Parse_WithoutProjectInfo_IsAFormatException()
    {
        var act = () => PlcProjectInfoXml.Parse("<TreeItem><IECProjectDef /></TreeItem>");
        act.Should().Throw<FormatException>().WithMessage("*no <ProjectInfo> section*");
    }

    [Fact]
    public void Build_SetsOnlyTheGivenFields_AndEscapes()
    {
        PlcProjectInfoXml.Build([new("Title", "A & B"), new("Author", "")]).Should().Be(
            "<TreeItem><IECProjectDef><ProjectInfo><Title>A &amp; B</Title><Author></Author></ProjectInfo></IECProjectDef></TreeItem>");
    }

    [Theory]
    [InlineData("1.0.0.0", true)]
    [InlineData("3.4.65535.12", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0.0.0", false)]
    [InlineData("1.0.a.0", false)]
    [InlineData("1.0.70000.0", false)]
    public void IsVersion(string version, bool ok) => PlcProjectInfoXml.IsVersion(version).Should().Be(ok);

    [Fact]
    public void Run_WithoutValues_OnlyReads()
    {
        var fake = OpenFake();
        fake.ProjectInfo[1] = new("Title", "Lib");
        string result = new PlcProjectInfoCoordinator(fake).Run(null);
        result.Should().StartWith("Project Information of the PLC project:\n  Title: Lib\n  Version: (not set)");
        fake.CallLog.Should().NotContain(c => c.StartsWith("ConsumePlcProjectXml", StringComparison.Ordinal));
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void Run_SetsTheGivenFields_VerifiesAndSaves()
    {
        var fake = OpenFake();
        fake.ProjectInfo[0] = new("Company", "Keep Me");
        string result = new PlcProjectInfoCoordinator(fake).Run("FixturePlc", title: "My Lib", version: "1.2.3.4", released: true);

        fake.LastConsumedPlcProjectXml.Should().Be(
            "<TreeItem><IECProjectDef><ProjectInfo><Title>My Lib</Title><Version>1.2.3.4</Version><Released>true</Released></ProjectInfo></IECProjectDef></TreeItem>");
        fake.CallLog.Should().Contain("SaveAll()");
        result.Should().StartWith("Set Title, Version, Released (verified by read-back, project saved).\nProject Information of PLC project 'FixturePlc':");
        result.Should().Contain("Title: My Lib").And.Contain("Company: Keep Me").And.Contain("Released: true");
    }

    [Fact]
    public void Run_AnEmptyValue_ClearsTheField()
    {
        var fake = OpenFake();
        fake.ProjectInfo[5] = new("Author", "Someone");
        new PlcProjectInfoCoordinator(fake).Run(null, author: "").Should().StartWith("Set Author");
        fake.ProjectInfo[5].Value.Should().BeEmpty();
    }

    [Fact]
    public void Run_ABadVersion_IsRefusedBeforeAnything()
    {
        var fake = OpenFake();
        new PlcProjectInfoCoordinator(fake).Run(null, version: "1.0").Should().StartWith("[Error] version '1.0' is not a library version");
        fake.CallLog.Should().BeEmpty();
    }

    [Fact]
    public void Run_TheXaeRefuses_NothingSaved()
    {
        var fake = OpenFake();
        fake.ConsumePlcProjectXmlThrows = new InvalidOperationException("XmlAutomationException in path 'TreeItem/IECProjectDef/ProjectInfo/Title'");
        new PlcProjectInfoCoordinator(fake).Run(null, title: "x")
            .Should().Be("[Error] The XAE refused the Project Information: XmlAutomationException in path 'TreeItem/IECProjectDef/ProjectInfo/Title' Nothing was saved.");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void Run_AReadBackThatDiffers_IsAnError_NothingSaved()
    {
        var fake = OpenFake();
        fake.ProjectInfoIgnored.Add("Title");
        new PlcProjectInfoCoordinator(fake).Run(null, title: "x", company: "C")
            .Should().Be("[Error] The read-back differs from what was set: Title is '', not 'x'. Nothing was saved.");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    [Fact]
    public void Run_NoSolution_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        new PlcProjectInfoCoordinator(fake).Run(null, title: "x").Should().StartWith("[Error]");
    }
}
