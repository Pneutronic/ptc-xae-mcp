using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Apply;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #155 step 7: the smaller runner pieces moved onto testable seams — export paths (E-25),
/// the diagnostics folder (E-22), "explicit > schema > GUI default" (a stale GUI selection never
/// overrides the schema) and when the process image is refreshed before linking.
/// </summary>
public class ApplyRunnerPiecesTests
{
    [Fact]
    public void ExportPath_Relative_ResolvesNextToTheSchemaFile_NotTheWorkingDirectory()
    {
        string schemaDir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-test-export");
        string schema = Path.Combine(schemaDir, "schema.json");

        string resolved = ApplyFiles.ResolveExport("out\\mapping.csv", "export.mappingCsvPath", schema, solutionDirectory: null);

        resolved.Should().Be(Path.Combine(schemaDir, "out", "mapping.csv"));
    }

    [Fact]
    public void ExportPath_OutsideEveryAllowedRoot_IsRefused()
    {
        string schema = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-test-export", "schema.json");
        string outside = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Windows", "ptc-export.xti");

        Action act = () => ApplyFiles.ResolveExports(outside, "", schema, solutionDirectory: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*export.xtiPath*not an allowed export location*Nothing was changed*");
    }

    [Fact]
    public void ExportPath_Empty_MeansNotRequested()
    {
        ApplyFiles.ResolveExports("", " ", "C:\\x\\schema.json", null).Should().Be(new ApplyExportPaths("", ""));
    }

    [Fact]
    public void DiagnosticsFolder_IsUnderLocalAppData_NotTheExeDirectory()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ApplyFiles.DiagnosticsFolder.Should().Be(Path.Combine(local, "ptc-xae-mcp", "diagnostics"));
        ApplyFiles.DiagnosticsFolder.Should().NotStartWith(AppContext.BaseDirectory);
    }

    private static SchemaConfig Config(bool linkingEnabled, params string?[] symbols) => new()
    {
        TwinCat = new TwinCatConfig { Linking = new LinkingConfig { Enabled = linkingEnabled } },
        IoPoints = symbols.Select((s, i) => new IoPoint { TerminalName = "T1", Channel = i, Direction = "I", Symbol = s }).ToList(),
    };

    [Fact]
    public void ProcessImageRefresh_OnlyWhenLinkingIsEnabledAndAPointNamesASymbol()
    {
        NativeTwinCatRunner.NeedsProcessImageRefresh(Config(true, "MAIN.fbCyl.bIn")).Should().BeTrue();
        NativeTwinCatRunner.NeedsProcessImageRefresh(Config(true, null, "MAIN.fbCyl.bIn")).Should().BeTrue();
    }

    [Fact]
    public void ProcessImageRefresh_NotWhenNoPointHasASymbol()
    {
        NativeTwinCatRunner.NeedsProcessImageRefresh(Config(true, null, "", "  ")).Should().BeFalse();
    }

    [Fact]
    public void ProcessImageRefresh_NotWhenLinkingIsDisabled()
    {
        NativeTwinCatRunner.NeedsProcessImageRefresh(Config(false, "MAIN.fbCyl.bIn")).Should().BeFalse();
    }

    [Theory]
    [InlineData("Explicit", "Schema", "Gui", "Explicit")]
    [InlineData("", "Schema", "Gui", "Schema")]
    [InlineData(null, " ", "Gui", "Gui")]
    [InlineData(null, null, null, "")]
    public void Effective_ExplicitThenSchemaThenGuiDefault(string? explicitValue, string? schema, string? gui, string expected) =>
        NativeTwinCatRunner.Effective(explicitValue, schema, gui).Should().Be(expected);
}
