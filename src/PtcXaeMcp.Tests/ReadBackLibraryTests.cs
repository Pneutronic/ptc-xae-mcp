using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// Issue #152 B-12 / B-14: library add/remove are asserted on the refreshed reference list,
/// remove addresses the placeholder, save-as-library proves a freshly written file and an install
/// proves itself by the installed-list diff or a byte-identical repository copy.
/// </summary>
public sealed class ReadBackLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ptc-readback-lib-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    private static LibraryReference Ref(string name, string placeholder, string version = "*") =>
        new(name, version, "Beckhoff Automation GmbH", placeholder, $"{name}, {version} (Beckhoff Automation GmbH)",
            LibrarySource.Xae, null, null);

    // ---- B-12 add / remove ------------------------------------------------------------------

    [Fact]
    public void Add_Landed_ReportsReadBack()
    {
        var fake = OpenFake();
        var (summary, refs) = new LibraryCoordinator(fake).AddReference("Tc2_Utilities", "*", null, asPlaceholder: false, force: true);

        summary.Should().StartWith("Added 'Tc2_Utilities'").And.Contain("read-back");
        refs.Should().ContainSingle(r => r.Name == "Tc2_Utilities");
    }

    [Fact]
    public void Add_NoOp_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.AddLibraryReference));

        var (summary, _) = new LibraryCoordinator(fake).AddReference("Tc2_Utilities", "*", null, false, force: true);

        summary.Should().StartWith("[Error] Add of library 'Tc2_Utilities' did not land");
    }

    [Fact]
    public void Add_OtherVersionInRefreshedList_IsError()
    {
        var fake = OpenFake();
        fake.AddLibraryResolvesToVersion = "3.3.2.0"; // XAE resolved it to another version

        var (summary, _) = new LibraryCoordinator(fake).AddReference("Tc2_Utilities", "3.3.1.0", null, false, force: true);

        summary.Should().StartWith("[Error] Library 'Tc2_Utilities' was added").And.Contain("'3.3.2.0'");
    }

    [Fact]
    public void Remove_PassesPlaceholderNotLibraryName()
    {
        var fake = OpenFake();
        fake.LibraryReferences.Add(Ref("Tc3_Module", "Tc3_Module_Placeholder"));

        var (summary, refs) = new LibraryCoordinator(fake).RemoveReference("Tc3_Module");

        fake.CallLog.Should().Contain("RemoveLibraryReference(Tc3_Module_Placeholder)");
        summary.Should().StartWith("Removed 'Tc3_Module'");
        refs.Should().BeEmpty();
    }

    [Fact]
    public void Remove_NoOp_IsError()
    {
        var fake = OpenFake();
        fake.LibraryReferences.Add(Ref("Tc2_MC2", "Tc2_MC2"));
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.RemoveLibraryReference));

        var (summary, _) = new LibraryCoordinator(fake).RemoveReference("Tc2_MC2");

        summary.Should().StartWith("[Error] Remove of library 'Tc2_MC2'").And.Contain("still in the refreshed");
    }

    // ---- B-14 save as library ---------------------------------------------------------------

    [Fact]
    public void SaveAsLibrary_FreshFile_ReportsBytesWritten()
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, "MyLib.library");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false);

        summary.Should().NotStartWith("[Error]").And.Contain("read-back");
    }

    [Fact]
    public void SaveAsLibrary_NoFile_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.SaveAsLibrary));

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.library"), install: false);

        summary.Should().StartWith("[Error] Save as library did not land").And.Contain("does not exist");
    }

    [Fact]
    public void SaveAsLibrary_OldFileReplacedButNotRewritten_IsError()
    {
        // overwrite deletes the old file first, so a save that writes nothing cannot pass off the old file as its own.
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.SaveAsLibrary));
        string path = Path.Combine(_dir, "MyLib.library");
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false, overwrite: true);

        summary.Should().StartWith("[Error] Save as library did not land").And.Contain("does not exist");
    }

    [Fact]
    public void SaveAsLibrary_InstallNotLanded_IsError()
    {
        var fake = OpenFake();
        fake.SaveAsLibraryInstallIsNoOp = true; // the file is written, the install part does nothing
        var coordinator = new LibraryCoordinator(fake) { FindRepositoryCopy = _ => null };

        string summary = coordinator.SaveAsLibrary(Path.Combine(_dir, "MyLib.library"), install: true);

        summary.Should().StartWith("[Error] The library file was written").And.Contain("install did not land");
    }

    // ---- B-14 install -----------------------------------------------------------------------

    [Fact]
    public void Install_NewTitle_ReportsDiff()
    {
        var fake = OpenFake();
        string lib = Path.Combine(_dir, "MyLib.library");
        File.WriteAllText(lib, "x");

        var (summary, _) = new LibraryCoordinator(fake).Install(lib, overwrite: false);

        summary.Should().StartWith("Installed 'MyLib.library'").And.Contain("new in the installed list: MyLib");
    }

    [Fact]
    public void Install_NoOp_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.InstallLibrary));
        string lib = Path.Combine(_dir, "MyLib.library");
        File.WriteAllText(lib, "x");
        var coordinator = new LibraryCoordinator(fake) { FindRepositoryCopy = _ => null };

        var (summary, _) = coordinator.Install(lib, overwrite: true);

        summary.Should().StartWith("[Error] Install of 'MyLib.library' did not land");
    }

    [Fact]
    public void Install_OverwriteSameVersion_ProvenByRepositoryCopy()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.InstallLibrary)); // list unchanged, as for an overwrite
        string lib = Path.Combine(_dir, "MyLib.library");
        File.WriteAllText(lib, "x");
        var coordinator = new LibraryCoordinator(fake) { FindRepositoryCopy = _ => @"C:\repo\MyLib\1.0.0.0\MyLib.library" };

        var (summary, _) = coordinator.Install(lib, overwrite: true);

        summary.Should().StartWith("Installed").And.Contain("byte-identical copy");
    }

    [Fact]
    public void Repository_FindCopy_MatchesBytesNotNames()
    {
        string repo = Directory.CreateDirectory(Path.Combine(_dir, "repo", "Company", "MyLib", "1.0.0.0")).FullName;
        string source = Path.Combine(_dir, "MyLib.library");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(repo, "Other.library"), [1, 2, 3, 5]);

        LibraryRepository.FindCopy(source, [Path.Combine(_dir, "repo")]).Should().BeNull();

        File.WriteAllBytes(Path.Combine(repo, "MyLib.library"), [1, 2, 3, 4]);
        LibraryRepository.FindCopy(source, [Path.Combine(_dir, "repo")]).Should().EndWith("MyLib.library");
    }
}
