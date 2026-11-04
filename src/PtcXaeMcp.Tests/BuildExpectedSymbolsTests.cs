using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// A build may pass with 0 errors and a fresh artifact while a just-created object is not in the compiled
/// application (TwinCAT translates only what a task reaches). expectSymbols makes the build look the names
/// up in the PLC project's .tmc and fail loudly when one is missing.
/// </summary>
public sealed class BuildExpectedSymbolsTests : IDisposable
{
    private const string Tmc = """
        <?xml version="1.0" encoding="utf-8"?>
        <TcModuleClass GeneratedBy="TwinCAT XAE Plc">
          <DataTypes>
            <DataType><Name GUID="{1}">ST_Recipe</Name><SubItem><Name>nInner</Name><Type>INT</Type></SubItem></DataType>
            <DataType><Name>MAIN</Name></DataType>
            <DataType><Name>FB_Axis</Name><SubItem><Name>nState</Name><Type>INT</Type></SubItem><ExtendsType>Lib.FB_Base</ExtendsType></DataType>
            <DataType><Name>FB_Base</Name><SubItem><Name>bBusy</Name><Type>BOOL</Type></SubItem></DataType>
          </DataTypes>
          <Modules><Module><DataAreas><DataArea>
            <Symbol><Name>MAIN.fbAxis</Name><BaseType>FB_Axis</BaseType></Symbol>
            <Symbol><Name>GVL_Recipe.nCount</Name><BaseType>INT</BaseType></Symbol>
            <Symbol><Name>GVL_Recipe.stActive</Name><BaseType>ST_Recipe</BaseType><ArrayInfo><LBound>1</LBound><Elements>3</Elements></ArrayInfo></Symbol>
          </DataArea></DataAreas></Module></Modules>
        </TcModuleClass>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptc-expect-" + Guid.NewGuid().ToString("N"));

    public BuildExpectedSymbolsTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Plc"));
        File.WriteAllText(Path.Combine(_dir, "Plc", "Plc.tmc"), Tmc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private FakeTwinCatSession Session(string relativeTmc = @"Plc\Plc.tmc")
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.PlcProjectXmls.Add(new PlcProjectXml("Machine", Path.Combine(_dir, "Machine.tsproj"), "Plc",
            $"<TreeItem><PlcProjectDef><RelativeTmcPath>{relativeTmc}</RelativeTmcPath></PlcProjectDef></TreeItem>", null));
        return fake;
    }

    [Fact]
    public void AllExpectedNamesPresent_ReportsFound()
    {
        string result = new BuildCoordinator(Session()).BuildPlc("Plc", expectSymbols: ["gvl_recipe.ncount", "ST_Recipe", "GVL_Recipe"]);

        Assert.Contains("Build succeeded", result);
        Assert.Contains("Expected symbols: all 3 found in the compiled PLC.", result);
        Assert.Contains("gvl_recipe.ncount (symbol)", result);
        Assert.Contains("ST_Recipe (data type)", result);
        Assert.Contains("GVL_Recipe (2 symbols below it)", result);
        Assert.DoesNotContain("[Error]", result);
    }

    [Fact]
    public void MissingName_IsAnErrorWithTheLinkAlwaysHint()
    {
        string result = new BuildCoordinator(Session()).BuildPlc(null, expectSymbols: ["MAIN.fbAxis, GVL_New.bFlag"]);

        Assert.Contains("[Error] Expected symbols: 1 of 2 missing from the compiled PLC: GVL_New.bFlag.", result);
        Assert.Contains("{attribute 'linkalways'}", result);
        Assert.Contains("MAIN.fbAxis (symbol)", result);
    }

    [Fact]
    public void SubItemNamesOfATypeAreNotTakenForTypes()
    {
        string result = new BuildCoordinator(Session()).BuildPlc("Plc", expectSymbols: ["nInner"]);

        Assert.Contains("1 of 1 missing", result);
    }

    [Fact]
    public void MemberPathsIntoAnInstance_AreFoundThroughTheDataTypes()
    {
        // Seen live: the .tmc lists PRG.fbSuite but not PRG.fbSuite.nMarker; the member is only a SubItem.
        string result = new BuildCoordinator(Session()).BuildPlc("Plc",
            expectSymbols: ["MAIN.fbAxis.nState", "main.fbaxis.bBusy", "GVL_Recipe.stActive[2].nInner"]);

        Assert.Contains("Expected symbols: all 3 found in the compiled PLC.", result);
        Assert.Contains("MAIN.fbAxis.nState (member of MAIN.fbAxis)", result);
        Assert.Contains("main.fbaxis.bBusy (member of MAIN.fbAxis)", result);
        Assert.Contains("GVL_Recipe.stActive[2].nInner (member of GVL_Recipe.stActive)", result);
    }

    [Theory]
    [InlineData("MAIN.fbAxis.nNoSuchMember")]
    [InlineData("MAIN.fbAxis.nState.deeper")]
    [InlineData("GVL_Recipe.nCount.x")]
    public void UnknownMembers_AreMissing(string path)
    {
        string result = new BuildCoordinator(Session()).BuildPlc("Plc", expectSymbols: [path]);

        Assert.Contains($"1 of 1 missing from the compiled PLC: {path}.", result);
    }

    [Fact]
    public void NoTmcOnDisk_IsAnErrorNamingThePath()
    {
        string result = new BuildCoordinator(Session(@"Other\Other.tmc")).BuildPlc(null, expectSymbols: ["MAIN.fbAxis"]);

        Assert.Contains("[Error] Expected symbols not verified", result);
        Assert.Contains("does not exist", result);
    }

    [Fact]
    public void UnknownPlcProjectName_IsAnError()
    {
        string result = new BuildCoordinator(Session()).BuildPlc("Nope", expectSymbols: ["MAIN.fbAxis"]);

        Assert.Contains("[Error] Expected symbols not verified", result);
        Assert.Contains("no PLC project named 'Nope'", result);
    }

    [Fact]
    public void WithoutExpectSymbols_NoReportAndNoTmcLookup()
    {
        var fake = Session();
        string result = new BuildCoordinator(fake).BuildPlc(null);

        Assert.DoesNotContain("Expected symbols", result);
        Assert.DoesNotContain("TMC:", result);
    }

    [Fact]
    public void TmcFreshness_IsStated()
    {
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "Plc", "Plc.tmc"), DateTime.UtcNow.AddHours(-1));

        string result = new BuildCoordinator(Session()).BuildSolution(expectSymbols: ["MAIN.fbAxis"]);

        Assert.Contains("not rewritten by this build", result);
        Assert.Contains("3 symbols, 4 data types", result);
    }

    [Theory]
    [InlineData(new[] { " a ", "b;c", "A" }, new[] { "a", "b", "c" })]
    [InlineData(new[] { "", " , " }, new string[0])]
    public void Normalize_SplitsTrimsAndDeduplicates(string[] input, string[] expected)
    {
        Assert.Equal(expected, CompiledSymbolCheck.Normalize(input));
    }
}
