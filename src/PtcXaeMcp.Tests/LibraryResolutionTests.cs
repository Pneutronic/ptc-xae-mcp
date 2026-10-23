using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

public sealed class LibraryResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ptc-lib-res-" + Guid.NewGuid().ToString("N"));

    public LibraryResolutionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static LibraryReference Ref(string name, string version, string publisher = "Beckhoff Automation GmbH", string? placeholder = null) =>
        new(name, version, publisher, placeholder ?? name, $"{name}, {version} ({publisher})", LibrarySource.Xae, null, null);

    private static InstalledLibrary Lib(string name, string version, string publisher = "Beckhoff Automation GmbH") =>
        new(name, version, publisher, name);

    private string Put(string distributor, string name, string version, string file, string content)
    {
        string dir = Path.Combine(_root, distributor, name, version);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- version comparison ----------------------------------------------------

    [Theory]
    [InlineData("3.4.10.0", "3.4.9.0", 1)]
    [InlineData("3.4.9.0", "3.4.10.0", -1)]
    [InlineData("3.4.5.0", "3.4.5.0", 0)]
    [InlineData("3.4", "3.4.0.0", 0)]
    [InlineData("3.10.1.0", "3.9.1.0", 1)]
    [InlineData("3.4.5.0", "3.4.5.1", -1)]
    public void Version_ComparesNumerically(string a, string b, int expected) =>
        Assert.Equal(expected, LibraryVersion.Compare(a, b));

    // ---- resolution order --------------------------------------------------------

    [Fact]
    public void Resolve_PrefersTheManagersEffectiveResolution()
    {
        var com = new PlaceholderResolution("Tc2_Standard", new ResolvedLibrary("Tc2_Standard", "3.4.5.0", "Beckhoff Automation GmbH"), null, null);
        var r = LibraryResolver.Resolve(Ref("Tc2_Standard", "*"), com, [Lib("Tc2_Standard", "3.4.9.0")]);
        Assert.Equal(LibraryResolutionKind.Effective, r.Resolution);
        Assert.Equal("3.4.5.0", r.Version);
        Assert.Equal("Beckhoff Automation GmbH", r.Distributor);
    }

    [Fact]
    public void Resolve_ConcreteVersionIsFixed()
    {
        var r = LibraryResolver.Resolve(Ref("Tc2_Standard", "3.4.5.0"), null, [Lib("Tc2_Standard", "3.4.9.0")]);
        Assert.Equal(LibraryResolutionKind.Fixed, r.Resolution);
        Assert.Equal("3.4.5.0", r.Version);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("* (newest)")]
    [InlineData("newest")]
    public void Resolve_AnyVersion_TakesTheHighestInstalled(string requested)
    {
        var installed = new[] { Lib("Tc2_Standard", "3.4.9.0"), Lib("Tc2_Standard", "3.4.10.0"), Lib("Tc2_Standard", "3.3.0.0"), Lib("Other", "9.9.9.9") };
        var r = LibraryResolver.Resolve(Ref("Tc2_Standard", requested), null, installed);
        Assert.Equal(LibraryResolutionKind.NewestInstalled, r.Resolution);
        Assert.Equal("3.4.10.0", r.Version);
        Assert.Equal("Tc2_Standard", r.Name);
    }

    [Fact]
    public void Resolve_NewestInstalled_HonoursTheDistributor()
    {
        var installed = new[] { Lib("Lib", "2.0.0.0", "Other Vendor"), Lib("Lib", "1.0.0.0", "Beckhoff Automation GmbH") };
        var r = LibraryResolver.Resolve(Ref("Lib", "*"), null, installed);
        Assert.Equal("1.0.0.0", r.Version);
    }

    [Fact]
    public void Resolve_NothingInstalled_IsUnresolved()
    {
        var r = LibraryResolver.Resolve(Ref("Missing", "*"), null, [Lib("Tc2_Standard", "3.4.5.0")]);
        Assert.Equal(LibraryResolutionKind.Unresolved, r.Resolution);
        Assert.Null(r.Version);
        Assert.Null(r.Name);
    }

    [Fact]
    public void Resolve_EffectiveWithoutVersion_FallsThrough()
    {
        var com = new PlaceholderResolution("X", new ResolvedLibrary("X", "", ""), null, null);
        Assert.Equal(LibraryResolutionKind.NewestInstalled, LibraryResolver.Resolve(Ref("X", "*"), com, [Lib("X", "1.0.0.0")]).Resolution);
    }

    [Fact]
    public void FindResolution_MatchesPlaceholderThenName()
    {
        var a = new PlaceholderResolution("Tc2_Standard", null, null, null);
        var b = new PlaceholderResolution("Other", null, null, null);
        Assert.Same(a, LibraryResolver.FindResolution([b, a], Ref("Lib", "*", placeholder: "tc2_standard")));
        Assert.Same(b, LibraryResolver.FindResolution([b, a], Ref("Other", "*", placeholder: "NoMatch")));
        Assert.Null(LibraryResolver.FindResolution([b], Ref("Lib", "*", placeholder: "x")));
    }

    // ---- repository files ----------------------------------------------------------

    [Fact]
    public void Files_AreFoundByDistributorNameVersion_WithSha256OfTheFile()
    {
        string path = Put("Beckhoff Automation GmbH", "Tc2_Standard", "3.4.5.0", "Tc2_Standard.compiled-library-ge33", "library bytes");
        Put("Beckhoff Automation GmbH", "Tc2_Standard", "3.4.5.0", "projectinfo", "not a library file");
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("library bytes"))).ToLowerInvariant();

        var lookup = LibraryRepositoryFiles.Find([new("System", _root)], "Beckhoff Automation GmbH", "Tc2_Standard", "3.4.5.0");

        Assert.True(lookup.FileFound);
        var file = Assert.Single(lookup.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal(expected, file.Sha256);
        Assert.Equal(13, file.SizeBytes);
        Assert.Single(lookup.SearchedPaths);
    }

    [Fact]
    public void Files_BothSourceAndCompiledFilesAreListed()
    {
        Put("V", "L", "1.0.0.0", "L.library", "a");
        Put("V", "L", "1.0.0.0", "L.compiled-library", "b");
        var lookup = LibraryRepositoryFiles.Find([new("System", _root)], "V", "L", "1.0.0.0");
        Assert.Equal(2, lookup.Files.Count);
    }

    [Fact]
    public void Files_NotFound_ReportsTheSearchedPaths()
    {
        var lookup = LibraryRepositoryFiles.Find([new("System", _root), new("User", Path.Combine(_root, "none"))], "V", "L", "9.9.9.9");
        Assert.False(lookup.FileFound);
        Assert.Equal(2, lookup.SearchedPaths.Count);
        Assert.EndsWith(Path.Combine("V", "L", "9.9.9.9"), lookup.SearchedPaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Files_WithoutDistributor_ScanAllDistributorFolders()
    {
        Put("Vendor A", "L", "1.0.0.0", "L.library", "x");
        var lookup = LibraryRepositoryFiles.Find([new("System", _root)], null, "L", "1.0.0.0");
        Assert.True(lookup.FileFound);
    }

    [Fact]
    public void Report_JoinsResolutionAndFiles_PerReference()
    {
        Put("Beckhoff Automation GmbH", "Tc2_Standard", "3.4.10.0", "Tc2_Standard.compiled-library", "v10");
        var ctx = new PlcLibraryContext("PLC", [Ref("Tc2_Standard", "*"), Ref("Gone", "1.0.0.0")], [],
            [Lib("Tc2_Standard", "3.4.9.0"), Lib("Tc2_Standard", "3.4.10.0")]);

        var items = LibraryRepositoryReport.Build([ctx], [new("System", _root)]);

        Assert.Equal(2, items.Count);
        Assert.Equal("3.4.10.0", items[0].Resolved.Version);
        Assert.True(items[0].Lookup.FileFound);
        Assert.Equal(LibraryResolutionKind.Fixed, items[1].Resolved.Resolution);
        Assert.False(items[1].Lookup.FileFound);
        Assert.NotEmpty(items[1].Lookup.SearchedPaths);
    }

    // ---- coordinator -----------------------------------------------------------------

    [Fact]
    public void Coordinator_NoSolution_IsAnError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };
        Assert.StartsWith("[Error]", new LibraryRepositoryCoordinator(fake).List(false, null), StringComparison.Ordinal);
    }

    [Fact]
    public void Coordinator_WithoutIncludeFiles_ListsOnlyRepositories()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeLibraryRepositories.Add(new LibraryRepositoryEntry("System", _root));

        string json = new LibraryRepositoryCoordinator(fake).List(false, null);

        Assert.DoesNotContain(fake.CallLog, c => c.StartsWith("ReadPlcLibraryContexts", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(json);
        var repo = doc.RootElement.GetProperty("repositories")[0];
        Assert.Equal("System", repo.GetProperty("name").GetString());
        Assert.True(repo.GetProperty("folderExists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("references").ValueKind);
    }

    [Fact]
    public void Coordinator_IncludeFiles_ShapesReferencesWithHash()
    {
        Put("Beckhoff Automation GmbH", "Tc2_Standard", "3.4.5.0", "Tc2_Standard.compiled-library", "abc");
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true };
        fake.FakeLibraryRepositories.Add(new LibraryRepositoryEntry("System", _root));
        fake.FakePlcLibraryContexts.Add(new PlcLibraryContext("PLC", [Ref("Tc2_Standard", "3.4.5.0")], [], []));

        string json = new LibraryRepositoryCoordinator(fake).List(true, " PLC ");

        Assert.Contains("ReadPlcLibraryContexts(PLC)", fake.CallLog);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement.GetProperty("references")[0];
        Assert.Equal("fixed", r.GetProperty("resolution").GetString());
        Assert.True(r.GetProperty("fileFound").GetBoolean());
        var file = r.GetProperty("files")[0];
        Assert.Equal("file", file.GetProperty("hashScope").GetString());
        Assert.Equal(64, file.GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void Coordinator_SessionException_BecomesError()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ListLibraryRepositoryEntriesThrows = new InvalidOperationException("Repositories not readable") };
        string r = new LibraryRepositoryCoordinator(fake).List(false, null);
        Assert.StartsWith("[Error]", r, StringComparison.Ordinal);
        Assert.Contains("Repositories not readable", r, StringComparison.Ordinal);
    }
}
