using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public class OpenProjectArgsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrWhitespace_ReturnsError(string? path)
    {
        Assert.Equal("slnPath is required.", OpenProjectArgs.Validate(path));
    }

    [Theory]
    [InlineData("relative.sln")]
    [InlineData(@".\relative.sln")]
    [InlineData("subdir\\file.sln")]
    public void Validate_RelativePath_ReturnsError(string path)
    {
        Assert.Equal("slnPath must be an absolute path.", OpenProjectArgs.Validate(path));
    }

    [Theory]
    [InlineData(@"C:\file.txt")]
    [InlineData(@"C:\file")]
    [InlineData(@"C:\foo.csproj")]
    public void Validate_WrongExtension_ReturnsError(string path)
    {
        Assert.Equal("slnPath must end with .sln, .tsproj, .tspproj, .hmiproj or .plcproj.", OpenProjectArgs.Validate(path));
    }

    [Theory]
    [InlineData(".tsproj")]
    [InlineData(".tspproj")]
    [InlineData(".hmiproj")]
    [InlineData(".plcproj")]
    public void Validate_ProjectFile_IsAccepted(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), "p_" + Guid.NewGuid().ToString("N") + ext);
        File.WriteAllText(path, "");
        try { Assert.Null(OpenProjectArgs.Validate(path)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("A.tsproj", "{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}")]
    [InlineData("A.hmiproj", "{FE7A1B72-C5B7-4D7C-BB7D-76384D4DE8E1}")]
    public void SolutionTextFor_UsesTheProjectTypeGuid_AndIsClassifiedAsDeclared(string file, string typeGuid)
    {
        var path = Path.Combine(Path.GetTempPath(), "x", file);
        var text = SolutionFile.SolutionTextFor(path);
        Assert.Contains($"Project(\"{typeGuid}\")", text);
        Assert.Contains(path, SolutionFile.ProjectPaths(text));
        var k = SolutionFile.Classify(text);
        Assert.Equal(file.EndsWith(".tsproj", StringComparison.Ordinal), k.HasTwinCat);
        Assert.Equal(file.EndsWith(".hmiproj", StringComparison.Ordinal), k.HasHmi);
    }

    [Fact]
    public void ResolveSolution_ProjectFile_WritesASolutionThatNamesIt_AndSlnPassesThrough()
    {
        var proj = Path.Combine(Path.GetTempPath(), "r_" + Guid.NewGuid().ToString("N") + ".tsproj");
        File.WriteAllText(proj, "");
        string? sln = null;
        try
        {
            sln = SolutionFile.ResolveSolution(proj);
            Assert.EndsWith(".sln", sln);
            Assert.Contains(proj, SolutionFile.ProjectPaths(File.ReadAllText(sln)));
            Assert.Equal(proj, PtcXaeMcp.WinForms.TwinCat.HostSelection.TsprojLocator.FindForSolution(sln));
            Assert.Equal(@"C:.sln", SolutionFile.ResolveSolution(@"C:.sln"));
        }
        finally
        {
            File.Delete(proj);
            if (sln is not null) File.Delete(sln);
        }
    }

    // A bare .plcproj (a library repository) opens inside a generated stand-alone PLC project (.tspproj) that points
    // at it with an absolute path; the .plcproj itself is never copied or touched.
    [Fact]
    public void ResolveSolution_Plcproj_WrapsItInAStandalonePlcProject()
    {
        string repo = Path.Combine(Path.GetTempPath(), "plcproj_" + Guid.NewGuid().ToString("N"));
        string plcproj = Path.Combine(repo, "MyLib", "MyLib.plcproj");
        Directory.CreateDirectory(Path.GetDirectoryName(plcproj)!);
        File.WriteAllText(plcproj,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <PropertyGroup>\n" +
            "    <ProjectGuid>{688615c6-9d3f-4e9b-80c8-28db620912b2}</ProjectGuid>\n" +
            "    <Name>MyLib</Name>\n" +
            "    <ProgramVersion>3.1.4024.56</ProgramVersion>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        string before = File.ReadAllText(plcproj);
        string? sln = null;
        try
        {
            sln = SolutionFile.ResolveSolution(plcproj);
            string wrapper = Path.ChangeExtension(sln, ".tspproj");
            string slnText = File.ReadAllText(sln);
            string wrapperText = File.ReadAllText(wrapper);

            Assert.Contains("Project(\"{DFBE7525-6864-4E62-8B2E-D530D69D9D96}\")", slnText);
            Assert.Contains(wrapper, SolutionFile.ProjectPaths(slnText));
            Assert.True(SolutionFile.Classify(slnText).HasTwinCat);
            Assert.Contains($"PrjFilePath=\"{plcproj}\"", wrapperText);
            Assert.Contains("GUID=\"{688615C6-9D3F-4E9B-80C8-28DB620912B2}\"", wrapperText);
            Assert.Contains("Name=\"MyLib\"", wrapperText);
            Assert.Contains("TcVersion=\"3.1.4024.56\"", wrapperText);
            System.Xml.Linq.XDocument.Parse(wrapperText); // well-formed

            Assert.Equal(plcproj, SolutionFile.SourceOfGenerated(sln));
            Assert.Equal(plcproj, LibraryParsing.FindPlcprojNear(sln));
            Assert.Equal(before, File.ReadAllText(plcproj));

            // The XAE rewrites the .sln on load; opening again must not overwrite the files it has open.
            File.WriteAllText(sln, slnText + "Global\r\nEndGlobal\r\n");
            Assert.Equal(sln, SolutionFile.ResolveSolution(plcproj));
            Assert.EndsWith("EndGlobal\r\n", File.ReadAllText(sln));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            if (sln is not null) Directory.Delete(Path.GetDirectoryName(sln)!, recursive: true);
        }
    }

    [Fact]
    public void SourceOfGenerated_IsNullForAnOrdinarySolution()
    {
        Assert.Null(SolutionFile.SourceOfGenerated(@"C:\Projects\Machine\Machine.sln"));
        Assert.Null(SolutionFile.SourceOfGenerated(null));
    }

    [Fact]
    public void Validate_NonExistentFile_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".sln");
        var result = OpenProjectArgs.Validate(path);
        Assert.NotNull(result);
        Assert.StartsWith("File not found", result);
    }

    [Fact]
    public void Validate_ExistingSln_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".sln");
        File.WriteAllText(path, "dummy");
        try
        {
            Assert.Null(OpenProjectArgs.Validate(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
