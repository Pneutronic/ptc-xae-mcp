using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class LibraryParsingTests
{
    [Fact]
    public void ParseResolution_Pinned_Splits()
    {
        var r = LibraryParsing.ParseResolution("Tc2_Standard, 3.4.5.0 (Beckhoff Automation GmbH)");
        Assert.NotNull(r);
        Assert.Equal("Tc2_Standard", r!.Value.Name);
        Assert.Equal("3.4.5.0", r.Value.Version);
        Assert.Equal("Beckhoff Automation GmbH", r.Value.Publisher);
    }

    [Fact]
    public void ParseResolution_AnyVersion_KeepsStar()
    {
        var r = LibraryParsing.ParseResolution("Tc2_Standard, * (Beckhoff Automation GmbH)");
        Assert.NotNull(r);
        Assert.Equal("*", r!.Value.Version);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("NoComma 1.0 (Pub)")]
    public void ParseResolution_Garbage_ReturnsNull(string input)
    {
        Assert.Null(LibraryParsing.ParseResolution(input));
    }

    // B-6: the publisher parenthesis is optional. Before this fix, a resolution string with
    // no "(Publisher)" suffix failed to match the regex at all and the whole reference was
    // silently dropped from every listing (breaking add/remove/availability checks against it).
    [Fact]
    public void ParseResolution_NoPublisher_StillParses()
    {
        var r = LibraryParsing.ParseResolution("Tc2_Standard, 3.4.5.0");
        Assert.NotNull(r);
        Assert.Equal("Tc2_Standard", r!.Value.Name);
        Assert.Equal("3.4.5.0", r.Value.Version);
        Assert.Equal(string.Empty, r.Value.Publisher);
    }

    // Issue #152 comment / PR #167 live finding: the XAE reports a "*"-added reference
    // resolved to the newest installed version as "Name, * (newest) (Publisher)" — the
    // version group must keep "* (newest)" together, not swallow it as the publisher.
    [Fact]
    public void ParseResolution_NewestAnnotation_KeepsVersionIntact()
    {
        var r = LibraryParsing.ParseResolution("Tc2_MC2, * (newest) (Beckhoff Automation GmbH)");
        Assert.NotNull(r);
        Assert.Equal("Tc2_MC2", r!.Value.Name);
        Assert.Equal("* (newest)", r.Value.Version);
        Assert.Equal("Beckhoff Automation GmbH", r.Value.Publisher);
    }

    [Fact]
    public void BuildReference_NoPublisher_IsAnyVersionStillWorksForStar()
    {
        var refs = LibraryParsing.ListReferencesFromPlcproj(WriteTempPlcprojForDisposal(
            """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PlaceholderReference Include="Tc2_NoPublisher">
                  <DefaultResolution>Tc2_NoPublisher, *</DefaultResolution>
                </PlaceholderReference>
              </ItemGroup>
            </Project>
            """, out var tmp));
        try
        {
            var r = Assert.Single(refs);
            Assert.Equal("*", r.Version);
            Assert.True(r.IsAnyVersion);
            Assert.Equal(string.Empty, r.Publisher);
        }
        finally { File.Delete(tmp); }
    }

    private static string WriteTempPlcprojForDisposal(string content, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".plcproj");
        File.WriteAllText(path, content);
        return path;
    }

    // B-10: with more than one .plcproj under the solution directory and no name matching the
    // solution's own base name, resolving to "whichever the filesystem enumerator returns
    // first" risks reading/writing the wrong PLC project's library references — fail loudly.
    [Fact]
    public void FindPlcprojNear_MultipleAmbiguousCandidates_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sln = Path.Combine(dir, "MySolution.sln");
            File.WriteAllText(sln, "");
            File.WriteAllText(Path.Combine(dir, "ProjectA.plcproj"), "<Project/>");
            File.WriteAllText(Path.Combine(dir, "ProjectB.plcproj"), "<Project/>");

            var ex = Assert.Throws<InvalidOperationException>(() => LibraryParsing.FindPlcprojNear(sln));
            Assert.Contains("Ambiguous", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindPlcprojNear_WithAPlcName_PicksThatPlcsFile_ByFileOrFolderName()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sln = Path.Combine(dir, "MySolution.sln");
            Directory.CreateDirectory(Path.Combine(dir, "Main"));
            Directory.CreateDirectory(Path.Combine(dir, "LibPlc"));
            File.WriteAllText(sln, "");
            string main = Path.Combine(dir, "Main", "Main.plcproj");
            string lib = Path.Combine(dir, "LibPlc", "Renamed.plcproj");
            File.WriteAllText(main, "<Project/>");
            File.WriteAllText(lib, "<Project/>");

            Assert.Equal(main, LibraryParsing.FindPlcprojNear(sln, "main"));
            Assert.Equal(lib, LibraryParsing.FindPlcprojNear(sln, "LibPlc"));
            var ex = Assert.Throws<InvalidOperationException>(() => LibraryParsing.FindPlcprojNear(sln, "Other"));
            Assert.Contains("No .plcproj of PLC 'Other'", ex.Message);
            Assert.Contains("plcProjectName", Assert.Throws<InvalidOperationException>(() => LibraryParsing.FindPlcprojNear(sln)).Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindPlcprojNear_MultipleCandidates_OneMatchesSolutionName_ResolvesToIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sln = Path.Combine(dir, "MySolution.sln");
            File.WriteAllText(sln, "");
            string expected = Path.Combine(dir, "MySolution.plcproj");
            File.WriteAllText(expected, "<Project/>");
            File.WriteAllText(Path.Combine(dir, "OtherPlc.plcproj"), "<Project/>");

            var result = LibraryParsing.FindPlcprojNear(sln);
            Assert.Equal(expected, result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FindPlcprojNear_SingleCandidate_ReturnsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string sln = Path.Combine(dir, "MySolution.sln");
            File.WriteAllText(sln, "");
            string expected = Path.Combine(dir, "SomeOtherName.plcproj");
            File.WriteAllText(expected, "<Project/>");

            var result = LibraryParsing.FindPlcprojNear(sln);
            Assert.Equal(expected, result);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ParseSingleReferenceXml_PinnedResolutionWins()
    {
        const string xml = """
            <root>
              <PlcLibPlaceholder>
                <PlaceholderReference>
                  <PlaceholderName>Tc2_Standard</PlaceholderName>
                  <DefaultResolution>Tc2_Standard, * (Beckhoff Automation GmbH)</DefaultResolution>
                </PlaceholderReference>
              </PlcLibPlaceholder>
              <VSProperties>
                <VSProperty><Name>Resolution</Name><Value>Tc2_Standard, 3.4.5.0 (Beckhoff Automation GmbH)</Value></VSProperty>
              </VSProperties>
            </root>
            """;
        var r = LibraryParsing.ParseSingleReferenceXml(xml);
        Assert.NotNull(r);
        Assert.Equal("Tc2_Standard", r!.Placeholder);
        Assert.Equal("3.4.5.0", r.Version);
        Assert.False(r.IsAnyVersion);
        Assert.Equal(LibrarySource.Xae, r.Source);
    }

    [Fact]
    public void ParseSingleReferenceXml_FallsBackToDefaultResolution()
    {
        const string xml = """
            <root>
              <PlcLibPlaceholder>
                <PlaceholderReference>
                  <PlaceholderName>Tc2_Standard</PlaceholderName>
                  <DefaultResolution>Tc2_Standard, * (Beckhoff Automation GmbH)</DefaultResolution>
                </PlaceholderReference>
              </PlcLibPlaceholder>
            </root>
            """;
        var r = LibraryParsing.ParseSingleReferenceXml(xml);
        Assert.NotNull(r);
        Assert.Equal("*", r!.Version);
        Assert.True(r.IsAnyVersion);
    }

    [Fact]
    public void ParseSingleReferenceXml_MalformedXml_ReturnsNull()
    {
        Assert.Null(LibraryParsing.ParseSingleReferenceXml("<not valid"));
    }

    [Fact]
    public void ListReferencesFromPlcproj_ReadsPinnedResolutions()
    {
        const string plcproj = """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <PlaceholderReference Include="Tc2_Standard">
                  <DefaultResolution>Tc2_Standard, * (Beckhoff Automation GmbH)</DefaultResolution>
                </PlaceholderReference>
                <PlaceholderReference Include="Tc3_Module">
                  <DefaultResolution>Tc3_Module, * (Beckhoff Automation GmbH)</DefaultResolution>
                </PlaceholderReference>
              </ItemGroup>
              <ItemGroup>
                <PlaceholderResolution Include="Tc2_Standard">
                  <Resolution>Tc2_Standard, 3.4.5.0 (Beckhoff Automation GmbH)</Resolution>
                </PlaceholderResolution>
              </ItemGroup>
            </Project>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".plcproj");
        File.WriteAllText(tmp, plcproj);
        try
        {
            var refs = LibraryParsing.ListReferencesFromPlcproj(tmp);
            Assert.Equal(2, refs.Count);

            var std = refs.Single(r => r.Placeholder == "Tc2_Standard");
            Assert.Equal("3.4.5.0", std.Version);
            Assert.False(std.IsAnyVersion);
            Assert.Equal(LibrarySource.Plcproj, std.Source);

            var mod = refs.Single(r => r.Placeholder == "Tc3_Module");
            Assert.Equal("*", mod.Version);
            Assert.True(mod.IsAnyVersion);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void MergeWithFile_AnnotatesMatchAndMismatch()
    {
        var xae = new List<LibraryReference>
        {
            new("Tc2_Standard", "3.4.5.0", "Beckhoff", "Tc2_Standard",
                "Tc2_Standard, 3.4.5.0 (Beckhoff)", LibrarySource.Xae, null, null),
            new("Tc3_Module", "3.5.0.0", "Beckhoff", "Tc3_Module",
                "Tc3_Module, 3.5.0.0 (Beckhoff)", LibrarySource.Xae, null, null),
            new("Tc3_Orphan", "1.0.0.0", "Beckhoff", "Tc3_Orphan",
                "Tc3_Orphan, 1.0.0.0 (Beckhoff)", LibrarySource.Xae, null, null),
        };
        var file = new List<LibraryReference>
        {
            new("Tc2_Standard", "3.4.5.0", "Beckhoff", "Tc2_Standard",
                "Tc2_Standard, 3.4.5.0 (Beckhoff)", LibrarySource.Plcproj, null, null),
            new("Tc3_Module", "3.4.0.0", "Beckhoff", "Tc3_Module",
                "Tc3_Module, 3.4.0.0 (Beckhoff)", LibrarySource.Plcproj, null, null),
        };

        var merged = LibraryParsing.MergeWithFile(xae, file);

        var std = merged.Single(r => r.Placeholder == "Tc2_Standard");
        Assert.Equal("3.4.5.0", std.FilePinnedVersion);
        Assert.True(std.VersionMatchesFile);

        var mod = merged.Single(r => r.Placeholder == "Tc3_Module");
        Assert.Equal("3.4.0.0", mod.FilePinnedVersion);
        Assert.False(mod.VersionMatchesFile);

        var orphan = merged.Single(r => r.Placeholder == "Tc3_Orphan");
        Assert.Null(orphan.FilePinnedVersion);
        Assert.Null(orphan.VersionMatchesFile);
    }

    // Live-observed (100000, 2026-09-23): the XAE itself writes <Parameter>/<Key>/<Value>
    // nodes with an explicit xmlns="" under a <Parameters> wrapper it also writes without a
    // namespace — this cancels the inherited MSBuild default namespace for that subtree.
    // ListParametersFromPlcproj/UpsertParameterInPlcproj must still find and update them
    // instead of reporting "no overrides" and appending an MSBuild-namespaced duplicate.
    private const string PlcprojWithNamespacelessParameter = """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <ItemGroup>
            <PlaceholderReference Include="Sample-Library">
              <DefaultResolution>PTC_Library, * (Pneutronic AG)</DefaultResolution>
              <Parameters xmlns="">
                <Parameter ListName="GVL_PARAMETER">
                  <Key>NUMBER_OF_ITEMS</Key>
                  <Value>102</Value>
                </Parameter>
              </Parameters>
            </PlaceholderReference>
          </ItemGroup>
        </Project>
        """;

    private static string WriteTempPlcproj(string content)
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".plcproj");
        File.WriteAllText(tmp, content);
        return tmp;
    }

    [Fact]
    public void ListParametersFromPlcproj_FindsNamespacelessParameterNode()
    {
        var tmp = WriteTempPlcproj(PlcprojWithNamespacelessParameter);
        try
        {
            var pars = LibraryParsing.ListParametersFromPlcproj(tmp, "Sample-Library");
            Assert.Single(pars);
            Assert.Equal("GVL_PARAMETER", pars[0].ListName);
            Assert.Equal("NUMBER_OF_ITEMS", pars[0].Key);
            Assert.Equal("102", pars[0].Value);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ListAllParametersFromPlcproj_FindsNamespacelessParameterNode()
    {
        var tmp = WriteTempPlcproj(PlcprojWithNamespacelessParameter);
        try
        {
            var pars = LibraryParsing.ListAllParametersFromPlcproj(tmp);
            Assert.Single(pars);
            Assert.Equal("102", pars[0].Value);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UpsertParameterInPlcproj_UpdatesNamespacelessParameter_InPlace_NoDuplicate()
    {
        var tmp = WriteTempPlcproj(PlcprojWithNamespacelessParameter);
        try
        {
            LibraryParsing.UpsertParameterInPlcproj(tmp, "Sample-Library", "GVL_PARAMETER", "NUMBER_OF_ITEMS", "40");

            var pars = LibraryParsing.ListParametersFromPlcproj(tmp, "Sample-Library");
            Assert.Single(pars); // must update in place, not append a second <Parameter>
            Assert.Equal("40", pars[0].Value);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UpsertParameterInPlcproj_AddsSecondKey_IntoSameNamespacelessWrapper()
    {
        var tmp = WriteTempPlcproj(PlcprojWithNamespacelessParameter);
        try
        {
            LibraryParsing.UpsertParameterInPlcproj(tmp, "Sample-Library", "GVL_PARAMETER", "NUMBER_OF_GROUPS", "5");

            var pars = LibraryParsing.ListParametersFromPlcproj(tmp, "Sample-Library");
            Assert.Equal(2, pars.Count);
            Assert.Contains(pars, p => p.Key == "NUMBER_OF_ITEMS" && p.Value == "102");
            Assert.Contains(pars, p => p.Key == "NUMBER_OF_GROUPS" && p.Value == "5");
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData("Tc2_Standard, * (Beckhoff Automation GmbH) [Standard]", "Tc2_Standard", "*", "Beckhoff Automation GmbH")]
    [InlineData("Tc2_MC2, newest (Beckhoff Automation GmbH)", "Tc2_MC2", "newest", "Beckhoff Automation GmbH")]
    [InlineData("Tc2_MC2, * (newest) (Beckhoff Automation GmbH)", "Tc2_MC2", "* (newest)", "Beckhoff Automation GmbH")]
    [InlineData("Tc2_EtherCAT, 3.7.1.0 (Beckhoff Automation GmbH)", "Tc2_EtherCAT", "3.7.1.0", "Beckhoff Automation GmbH")]
    [InlineData("MyLib, 1.0.0.0", "MyLib", "1.0.0.0", "")]
    public void ParseResolution_LiveXaeSpellings(string input, string name, string version, string publisher)
    {
        var r = LibraryParsing.ParseResolution(input);
        Assert.NotNull(r);
        Assert.Equal(name, r!.Value.Name);
        Assert.Equal(version, r.Value.Version);
        Assert.Equal(publisher, r.Value.Publisher);
    }
}
