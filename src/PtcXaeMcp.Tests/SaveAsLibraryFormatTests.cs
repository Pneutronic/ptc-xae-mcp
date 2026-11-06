using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// twincat_save_as_library: the extension of outputPath selects SaveAsLibrary (.library) or SaveAsLibrary2
/// (.compiled-library); every refusal comes before anything is saved or deleted; an existing file needs overwrite.
/// </summary>
public sealed class SaveAsLibraryFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ptc-save-as-lib-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeTwinCatSession OpenFake() => new() { IsConnected = true, SolutionOpen = true };

    private static bool Saved(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c.StartsWith("SaveAsLibrary", StringComparison.Ordinal));

    // ---- refusals ----------------------------------------------------------------------------

    [Theory]
    [InlineData("MyLib.lib")]
    [InlineData("MyLib.compiled")]
    [InlineData("MyLib")]
    [InlineData("MyLib.library.zip")]
    public void OtherExtension_IsRefusedBeforeAnything(string file)
    {
        var fake = new FakeTwinCatSession(); // not even connected: the extension check comes first

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, file), install: false);

        summary.Should().StartWith("[Error] outputPath must end in .library").And.Contain(".compiled-library");
        Saved(fake).Should().BeFalse();
    }

    [Fact]
    public void EncryptOnASourceLibrary_IsRefused()
    {
        var fake = OpenFake();

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.library"), install: false, encrypt: true);

        summary.Should().StartWith("[Error] encrypt applies only to a .compiled-library");
        Saved(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData("MyLib.library")]
    [InlineData("MyLib.compiled-library")]
    public void ExistingFile_WithoutOverwrite_IsRefusedAndKept(string file)
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, file);
        File.WriteAllText(path, "keep me");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false);

        summary.Should().StartWith("[Error]").And.Contain("already exists").And.Contain("overwrite=true");
        File.ReadAllText(path).Should().Be("keep me");
        Saved(fake).Should().BeFalse();
    }

    [Fact]
    public void NoSolution_IsRefused()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = false };

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.compiled-library"), install: false);

        summary.Should().StartWith("[Error] No solution is currently open.");
        Saved(fake).Should().BeFalse();
    }

    // ---- the two paths -----------------------------------------------------------------------

    [Fact]
    public void SourceLibrary_GoesThroughSaveAsLibrary()
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, "MyLib.library");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false);

        fake.CallLog.Should().Contain($"SaveAsLibrary({path},False)");
        fake.CallLog.Should().NotContain(c => c.StartsWith("SaveAsLibrary2", StringComparison.Ordinal));
        summary.Should().NotStartWith("[Error]").And.Contain("bytes written");
    }

    [Theory]
    [InlineData("MyLib.compiled-library", false)]
    [InlineData("MyLib.COMPILED-LIBRARY", true)]
    public void CompiledLibrary_GoesThroughSaveAsLibrary2(string file, bool encrypt)
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, file);

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false, encrypt: encrypt);

        fake.CallLog.Should().Contain($"SaveAsLibrary2({path},{encrypt},False)");
        fake.CallLog.Should().NotContain(c => c.StartsWith("SaveAsLibrary(", StringComparison.Ordinal));
        summary.Should().StartWith("Saved compiled library").And.Contain($"read-back: {new FileInfo(path).Length} bytes");
    }

    [Fact]
    public void CompiledLibrary_Overwrite_DeletesTheOldFileFirst()
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, "MyLib.compiled-library");
        File.WriteAllText(path, "old");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false, overwrite: true);

        // The fake refuses an existing file like the XAE does: success proves the old one was removed first.
        summary.Should().NotStartWith("[Error]").And.Contain("replaced the previous file");
        File.ReadAllText(path).Should().Be("fake compiled library");
    }

    [Fact]
    public void CompiledLibrary_Install_IsProvenByTheInstalledList()
    {
        var fake = OpenFake();
        string path = Path.Combine(_dir, "MyLib.compiled-library");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: true);

        fake.CallLog.Should().Contain($"SaveAsLibrary2({path},False,True)");
        summary.Should().NotStartWith("[Error]").And.Contain("new in the installed list: MyLib");
    }

    [Fact]
    public void CompiledLibrary_InstallNotLanded_IsError()
    {
        var fake = OpenFake();
        fake.SaveAsLibraryInstallIsNoOp = true;
        var coordinator = new LibraryCoordinator(fake) { FindRepositoryCopy = _ => null };

        string summary = coordinator.SaveAsLibrary(Path.Combine(_dir, "MyLib.compiled-library"), install: true);

        summary.Should().StartWith("[Error] The library file was written").And.Contain("install did not land");
    }

    // ---- failures ----------------------------------------------------------------------------

    [Fact]
    public void SaveAsLibrary2NotOffered_NamesTheXaeRequirement()
    {
        var fake = OpenFake();
        fake.SaveAsLibrary2NotOffered = true;
        string path = Path.Combine(_dir, "MyLib.compiled-library");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(path, install: false);

        summary.Should().StartWith("[Error]").And.Contain("SaveAsLibrary2 is not offered");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void EmptyFile_IsError()
    {
        var fake = OpenFake();
        fake.SaveAsLibrary2Content = string.Empty;

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.compiled-library"), install: false);

        summary.Should().StartWith("[Error] Save as library did not land").And.Contain("empty (0 bytes)");
    }

    [Fact]
    public void CompiledLibrary_NotWritten_IsError()
    {
        var fake = OpenFake();
        fake.NoOpMutations.Add(nameof(FakeTwinCatSession.SaveAsLibrary2));

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.compiled-library"), install: false);

        summary.Should().StartWith("[Error] Save as library did not land").And.Contain("after SaveAsLibrary2 returned");
    }

    [Theory]
    [InlineData(".compiled-library")]
    [InlineData(".library")]
    public void TitleNotSpecified_GetsAHint(string extension)
    {
        var fake = OpenFake();
        var titleMissing = new InvalidOperationException("SaveAsLibrary failed: ArgumentException: 'Title' not specified");
        fake.SaveAsLibrary2Throws = titleMissing;
        fake.SaveAsLibraryThrows = titleMissing;

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib" + extension), install: false);

        summary.Should().StartWith("[Error]").And.Contain("'Title' not specified").And.Contain("Hint: a library needs the Title");
    }

    [Fact]
    public void OtherFailure_HasNoTitleHint()
    {
        var fake = OpenFake();
        fake.SaveAsLibrary2Throws = new InvalidOperationException("SaveAsLibrary2 failed: software protection missing");

        string summary = new LibraryCoordinator(fake).SaveAsLibrary(Path.Combine(_dir, "MyLib.compiled-library"), install: false, encrypt: true);

        summary.Should().StartWith("[Error]").And.Contain("software protection").And.NotContain("Hint:");
    }
}
