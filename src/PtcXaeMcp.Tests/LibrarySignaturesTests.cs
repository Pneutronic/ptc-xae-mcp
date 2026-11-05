using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

// twincat_library_signatures: the parser for the signature XML of ITcPlcLibraryManager2.ProduceAllLibrarySignatures
// (structure per Infosys TE1000 "Exporting PLC library signatures"; the sample below is written for these tests) and
// the coordinator that filters, looks up one type, writes the file and names referenced libraries missing from it.
public class LibrarySignaturesTests
{
    private const string Xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Library>
          <LibraryName>Tc2_Standard</LibraryName>
          <Version>3.4.5.0</Version>
          <Distributor>Beckhoff Automation GmbH</Distributor>
          <TypeSignatures>
            <TypeSignature type="FunctionBlock">
              <Name>TON</Name>
              <Comment>On delay timer</Comment>
              <Inputs>
                <Input><Name>IN</Name><DataType>BOOL</DataType><Comment>starts the timer</Comment></Input>
                <Input><Name>PT</Name><DataType>TIME</DataType></Input>
              </Inputs>
              <Outputs>
                <Output><Name>Q</Name><DataType>BOOL</DataType></Output>
                <Output><Name>ET</Name><DataType>TIME</DataType></Output>
              </Outputs>
            </TypeSignature>
            <TypeSignature type="FunctionBlock"><Name>R_TRIG</Name></TypeSignature>
            <TypeSignature type="Function"><Name>LEN</Name></TypeSignature>
          </TypeSignatures>
        </Library>
        <Library>
          <LibraryName>Tc2_MC2</LibraryName>
          <Version>3.3.62.0</Version>
          <Distributor>Beckhoff Automation GmbH</Distributor>
          <TypeSignatures>
            <TypeSignature type="FunctionBlock">
              <Name>MC_MoveAbsolute</Name>
              <Inputs><Input><Name>Execute</Name><DataType>BOOL</DataType></Input></Inputs>
              <InOuts><InOut><Name>Axis</Name><DataType>AXIS_REF</DataType></InOut></InOuts>
            </TypeSignature>
            <TypeSignature type="VarGlobal">
              <Name>GVL_Const</Name>
              <Constants><Constant><Name>MAX_AXES</Name><DataType>UINT</DataType></Constant></Constants>
            </TypeSignature>
          </TypeSignatures>
        </Library>
        """;

    // ---- parser ----------------------------------------------------------------

    [Fact]
    public void Parse_ReadsEveryLibraryWithItsTypes_FromAFragmentWithSeveralRoots()
    {
        var libs = LibrarySignatureXml.Parse(Xml);

        libs.Select(l => (l.Name, l.Version, l.Distributor)).Should().Equal(
            ("Tc2_Standard", "3.4.5.0", "Beckhoff Automation GmbH"),
            ("Tc2_MC2", "3.3.62.0", "Beckhoff Automation GmbH"));
        libs[0].Types.Select(t => (t.Name, t.Type)).Should().Equal(("TON", "FunctionBlock"), ("R_TRIG", "FunctionBlock"), ("LEN", "Function"));
    }

    [Fact]
    public void Parse_ReadsTheSectionsOfAType_IncludingOnesNotInTheSample()
    {
        var ton = LibrarySignatureXml.Parse(Xml)[0].Types[0];
        ton.Comment.Should().Be("On delay timer");
        ton.Sections.Select(s => s.Name).Should().Equal("Inputs", "Outputs");
        ton.Sections[0].Items.Should().Equal(new SignatureItem("IN", "BOOL", "starts the timer"), new SignatureItem("PT", "TIME", null));

        var move = LibrarySignatureXml.Parse(Xml)[1].Types[0];
        move.Sections.Select(s => s.Name).Should().Equal("Inputs", "InOuts");
        move.Sections[1].Items.Single().Should().Be(new SignatureItem("Axis", "AXIS_REF", null));
    }

    [Fact]
    public void Parse_ASingleLibraryRoot_AndAWrappingRoot_BothWork()
    {
        const string single = "<Library><LibraryName>A</LibraryName><Version>1.0</Version><TypeSignatures/></Library>";
        LibrarySignatureXml.Parse(single).Single().Name.Should().Be("A");
        LibrarySignatureXml.Parse($"<Libraries>{single}</Libraries>").Single().Distributor.Should().BeEmpty();
    }

    [Fact]
    public void Parse_BrokenXml_IsAFormatException()
    {
        var act = () => LibrarySignatureXml.Parse("<Library><LibraryName>A</Library>");
        act.Should().Throw<FormatException>().WithMessage("The library signature XML cannot be read:*");
    }

    // ---- coordinator -----------------------------------------------------------

    private static FakeTwinCatSession OpenFake(string xml = Xml)
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.LibrarySignatures = new LibrarySignatureExport(xml, "References", "TcXaeShell 64-bit");
        fake.LibraryReferences.Add(Ref("Tc2_Standard", "Tc2_Standard"));
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "Tc2_MC2"));
        return fake;
    }

    private static LibraryReference Ref(string name, string placeholder) =>
        new(name, "*", "Beckhoff Automation GmbH", placeholder, "", LibrarySource.Xae, null, null);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"libsig-{Guid.NewGuid():N}.xml");

    [Fact]
    public void Export_WritesTheXml_AndSummarisesEveryLibrary()
    {
        var fake = OpenFake();
        string file = TempFile();
        try
        {
            string result = new LibrarySignatureCoordinator(fake).Export(null, file, null);

            File.ReadAllText(file).Should().Be(Xml);
            result.Should().Be(
                $"Exported the signatures of 2 libraries to {file} (read on the References node, XAE: TcXaeShell 64-bit).\n" +
                "  Tc2_Standard 3.4.5.0 (Beckhoff Automation GmbH): 2 FunctionBlock, 1 Function\n" +
                "  Tc2_MC2 3.3.62.0 (Beckhoff Automation GmbH): 1 FunctionBlock, 1 VarGlobal");
            fake.CallLog.Should().NotContain(c => c.Contains("Save", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_TypeName_ReturnsItsSignature_CaseInsensitive()
    {
        string file = TempFile();
        try
        {
            string result = new LibrarySignatureCoordinator(OpenFake()).Export(null, file, "ton");
            result.Should().EndWith(
                "TON (FunctionBlock) in Tc2_Standard 3.4.5.0 — On delay timer\n" +
                "  Inputs:\n" +
                "    IN : BOOL — starts the timer\n" +
                "    PT : TIME\n" +
                "  Outputs:\n" +
                "    Q : BOOL\n" +
                "    ET : TIME");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_UnknownTypeName_NamesTheLibrariesSearched()
    {
        string file = TempFile();
        try
        {
            new LibrarySignatureCoordinator(OpenFake()).Export(null, file, "FB_Nope").Should().Be(
                "[Error] No type 'FB_Nope' in Tc2_Standard, Tc2_MC2. The signatures were written to " + file + ".");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_LibraryFilter_ByNameOrPlaceholder()
    {
        var fake = OpenFake();
        fake.LibraryReferences[1] = Ref("Tc2_MC2", "MotionLib");
        string file = TempFile();
        try
        {
            var c = new LibrarySignatureCoordinator(fake);
            c.Export("tc2_standard", file, null).Should().Contain("Exported the signatures of 1 library").And.NotContain("Tc2_MC2");
            c.Export("MotionLib", file, null).Should().Contain("  Tc2_MC2 3.3.62.0").And.NotContain("Tc2_Standard");
            File.ReadAllText(file).Should().Contain("<LibraryName>Tc2_MC2</LibraryName>").And.NotContain("Tc2_Standard");
            c.Export("Tc3_Nope", file, null).Should().Be("[Error] Library 'Tc3_Nope' is not in the export. Libraries: Tc2_Standard, Tc2_MC2.");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_AReferencedLibraryMissingFromTheExport_IsNamed()
    {
        var fake = OpenFake();
        fake.LibraryReferences.Add(Ref("Tc3_EventLogger", "Tc3_EventLogger"));
        string file = TempFile();
        try
        {
            new LibrarySignatureCoordinator(fake).Export(null, file, null).Should().EndWith(
                "\n[Warning] Referenced but not in the export (not resolved — installed?): Tc3_EventLogger. Check with twincat_check_libraries_installed.");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Export_DefaultPath_IsUnderTheGivenDirectory_NamedAfterTheSolution()
    {
        var fake = OpenFake();
        fake.ActiveSlnPath = @"C:\work\Plant\Plant.sln";
        string dir = Path.Combine(Path.GetTempPath(), $"libsig-{Guid.NewGuid():N}");
        try
        {
            string result = new LibrarySignatureCoordinator(fake, dir).Export(null, null, null);
            string expected = Path.Combine(dir, "Plant.library-signatures.xml");
            File.Exists(expected).Should().BeTrue();
            result.Should().StartWith($"Exported the signatures of 2 libraries to {expected} ");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Export_Refusals()
    {
        var fake = OpenFake();
        fake.SolutionOpen = false;
        new LibrarySignatureCoordinator(fake).Export(null, TempFile(), null).Should().StartWith("[Error]");

        fake = OpenFake();
        fake.ProduceLibrarySignaturesThrows = new InvalidOperationException("Couldn't resolve libraries!");
        new LibrarySignatureCoordinator(fake).Export(null, TempFile(), null)
            .Should().Be("[Error] The XAE could not serialise the library signatures: Couldn't resolve libraries!");

        fake = OpenFake("<Library><LibraryName>A</Library>");
        new LibrarySignatureCoordinator(fake).Export(null, TempFile(), null)
            .Should().StartWith("[Error] The library signature XML cannot be read:");

        fake = OpenFake("");
        new LibrarySignatureCoordinator(fake).Export(null, TempFile(), null)
            .Should().Be("[Error] The XAE returned no library signatures (no resolved library in the PLC project).");
    }
}
