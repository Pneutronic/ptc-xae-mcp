using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.Tests;

/// <summary>Test classes that set PTC_XAE_MCP_ALLOWED_PATHS (a process-wide variable) run in
/// this collection, so one never sees the value another one set.</summary>
[CollectionDefinition(Name)]
public sealed class PathPolicyEnvironment
{
    public const string Name = "PathPolicy environment";
}

[Collection(PathPolicyEnvironment.Name)]
public class PathPolicyTests
{
    [Fact]
    public void IsAllowed_EmptyOrNullPath_ReturnsFalse()
    {
        Assert.False(PathPolicy.IsAllowed(null, null, out string reason1));
        Assert.NotEmpty(reason1);
        Assert.False(PathPolicy.IsAllowed("", null, out string reason2));
        Assert.NotEmpty(reason2);
        Assert.False(PathPolicy.IsAllowed("   ", null, out _));
    }

    [Fact]
    public void IsAllowed_PathUnderTemp_ReturnsTrue()
    {
        string candidate = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-test", "foo.xml");
        Assert.True(PathPolicy.IsAllowed(candidate, solutionDirectory: null, out string reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData(true)]   // a repository: .git folder
    [InlineData(false)]  // a worktree: .git file
    public void RepositoryRootOf_FindsTheRepositoryAboveTheSolution(bool gitFolder)
    {
        string repo = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        string solutionDir = Path.Combine(repo, "Source", "Project");
        Directory.CreateDirectory(solutionDir);
        try
        {
            if (gitFolder) Directory.CreateDirectory(Path.Combine(repo, ".git"));
            else File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: elsewhere");

            Assert.Equal(repo, PathPolicy.RepositoryRootOf(solutionDir));
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public void RepositoryRootOf_WithoutARepository_IsNull()
    {
        // Deeper than MaxRepositoryDepth below a fresh folder, so the search never leaves it.
        string root = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "a", "b", "c", "d", "e", "f", "g", "h");
        Directory.CreateDirectory(dir);
        try { Assert.Null(PathPolicy.RepositoryRootOf(dir)); }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void IsAllowed_PathUnderSolutionDirectory_ReturnsTrue()
    {
        // A directory that is neither %TEMP% nor %LOCALAPPDATA%\ptc-xae-mcp nor a
        // TwinCAT install dir, so this only passes because it is the solution dir.
        string solutionDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "ptc-xae-mcp-test-solution-dir");
        string candidate = Path.Combine(solutionDir, "Mappings.xml");

        Assert.True(PathPolicy.IsAllowed(candidate, solutionDir, out string reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void IsAllowed_SolutionDirectoryItself_ReturnsTrue()
    {
        string solutionDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "ptc-xae-mcp-test-solution-dir-exact");
        Assert.True(PathPolicy.IsAllowed(solutionDir, solutionDir, out _));
    }

    [Fact]
    public void IsAllowed_PathOutsideEveryRoot_ReturnsFalse()
    {
        string outside = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "definitely-not-an-allowed-root-" + Guid.NewGuid().ToString("N"), "secret.txt");

        Assert.False(PathPolicy.IsAllowed(outside, solutionDirectory: null, out string reason));
        Assert.Contains(Path.GetFullPath(outside), reason);
    }

    [Fact]
    public void IsAllowed_SiblingDirectoryOfSolutionDirectory_ReturnsFalse()
    {
        // A prefix match on the raw string ("...\Sol" vs "...\SolutionEvil") must not
        // be treated as "under" the solution directory — regression guard for a
        // naive StartsWith(root) check without a trailing separator.
        string solutionDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Sol");
        string sneaky = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "SolEvil", "x.txt");

        Assert.False(PathPolicy.IsAllowed(sneaky, solutionDir, out _));
    }

    [Fact]
    public void IsAllowed_ExtraRootFromEnvVar_ReturnsTrue()
    {
        string extraRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            "ptc-xae-mcp-test-extra-root-" + Guid.NewGuid().ToString("N"));
        string candidate = Path.Combine(extraRoot, "file.xml");

        string? previous = Environment.GetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, extraRoot);
            Assert.True(PathPolicy.IsAllowed(candidate, solutionDirectory: null, out string reason));
            Assert.Empty(reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, previous);
        }
    }

    [Fact]
    public void IsAllowed_MultipleExtraRootsFromEnvVar_EachAllowed()
    {
        string root1 = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "ptc-extra-1-" + Guid.NewGuid().ToString("N"));
        string root2 = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "ptc-extra-2-" + Guid.NewGuid().ToString("N"));

        string? previous = Environment.GetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, $"{root1};{root2}");
            Assert.True(PathPolicy.IsAllowed(Path.Combine(root1, "a.txt"), null, out _));
            Assert.True(PathPolicy.IsAllowed(Path.Combine(root2, "b.txt"), null, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PathPolicy.AllowedPathsEnvVar, previous);
        }
    }

    [Fact]
    public void IsAllowedRuntimeExe_EmptyPath_ReturnsFalse()
    {
        Assert.False(PathPolicy.IsAllowedRuntimeExe(null, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsAllowedRuntimeExe_NotAnExe_ReturnsFalse()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string notExe = Path.Combine(programFilesX86, "Beckhoff", "readme.txt");

        Assert.False(PathPolicy.IsAllowedRuntimeExe(notExe, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsAllowedRuntimeExe_ExeOutsideTwinCatDirs_ReturnsFalse()
    {
        string outsideExe = Path.Combine(Path.GetTempPath(), "not-twincat-" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.False(PathPolicy.IsAllowedRuntimeExe(outsideExe, out string reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsAllowedRuntimeExe_ExeUnderProgramFilesX86Beckhoff_ReturnsTrue()
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string exePath = Path.Combine(programFilesX86, "Beckhoff", "TwinCAT", "3.1", "System", "TcSystemServiceUm.exe");

        Assert.True(PathPolicy.IsAllowedRuntimeExe(exePath, out string reason));
        Assert.Empty(reason);
    }
}
