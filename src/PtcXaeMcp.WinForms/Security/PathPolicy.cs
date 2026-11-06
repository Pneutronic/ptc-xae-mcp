namespace PtcXaeMcp.WinForms.Security;

/// <summary>
/// Central allow-list for file-path parameters accepted from MCP tool calls
/// (#156 item 7): declarationFile/implementationFile (write_pou_body,
/// write_pou_member, write_gvl), outputPath (get_mapping_info,
/// save_as_library), filePath (plcopen_export/import), sourcePath
/// (import_iodd), libraryPath (install_library), and the path parameter of
/// save_scope_data. A path outside every allowed root is refused before it
/// reaches a COM call or the filesystem — this is a pure decision (no I/O
/// beyond <see cref="Path.GetFullPath(string)"/> and the directory
/// existence checks <see cref="TwinCat.TwinCatPaths"/> already
/// does), so it is unit-testable without a live TwinCAT installation.
/// </summary>
internal static class PathPolicy
{
    /// <summary>Env var name for extra allow-listed roots, ';'-separated.</summary>
    public const string AllowedPathsEnvVar = "PTC_XAE_MCP_ALLOWED_PATHS";

    /// <summary>
    /// True if <paramref name="path"/> resolves under one of: the open
    /// solution's directory (<paramref name="solutionDirectory"/>, may be
    /// null if no solution is open), the git repository holding it, <c>%TEMP%</c>,
    /// <c>%LOCALAPPDATA%\ptc-xae-mcp</c>, the local TwinCAT install
    /// directory, or an extra root from <c>PTC_XAE_MCP_ALLOWED_PATHS</c>.
    /// </summary>
    public static bool IsAllowed(string? path, string? solutionDirectory, out string reason)
    {
        if (!TryGetFullPath(path, out string full, out reason))
            return false;

        // The access policy lives in the allowed %LOCALAPPDATA%\ptc-xae-mcp; no tool may read or write it.
        if (string.Equals(Path.GetFileName(full), Mcp.AccessPolicy.FileName, StringComparison.OrdinalIgnoreCase)
            && TryGetFullPath(Path.GetDirectoryName(Mcp.AccessPolicy.DefaultPath), out string policyDir, out _)
            && IsUnderOrEqual(full, policyDir))
        {
            reason = $"'{full}' is the access policy; it is set in the ptc-xae-mcp window, never by a tool.";
            return false;
        }

        foreach (string root in AllowedRoots(solutionDirectory))
        {
            if (TryGetFullPath(root, out string fullRoot, out _) && IsUnderOrEqual(full, fullRoot))
            {
                reason = string.Empty;
                return true;
            }
        }

        reason = $"'{full}' is outside every allowed directory (the open solution's " +
                 "directory or the git repository holding it, %TEMP%, %LOCALAPPDATA%\\ptc-xae-mcp, the local TwinCAT " +
                 $"installation, or a root listed in {AllowedPathsEnvVar}).";
        return false;
    }

    /// <summary>
    /// Narrower check for <c>umr_start</c>'s <c>exePath</c>: must be an
    /// <c>.exe</c> under a local TwinCAT installation directory — never an
    /// arbitrary executable.
    /// </summary>
    public static bool IsAllowedRuntimeExe(string? exePath, out string reason)
    {
        if (!TryGetFullPath(exePath, out string full, out reason))
            return false;

        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"'{full}' does not point to an .exe.";
            return false;
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidateRoots = new List<string> { Path.Combine(programFilesX86, "Beckhoff") };
        string? installDir = TwinCat.TwinCatPaths.GetInstallDir();
        if (installDir is not null) candidateRoots.Add(installDir);

        foreach (string root in candidateRoots)
        {
            if (TryGetFullPath(root, out string fullRoot, out _) && IsUnderOrEqual(full, fullRoot))
            {
                reason = string.Empty;
                return true;
            }
        }

        reason = $"'{full}' is not under a local TwinCAT installation directory.";
        return false;
    }

    /// <summary>The roots file-path parameters may lie under, for display (twincat_doctor).</summary>
    internal static IReadOnlyList<string> AllowedRootsFor(string? solutionDirectory) =>
        AllowedRoots(solutionDirectory).OfType<string>().Where(r => r.Length > 0).ToList();

    private static IEnumerable<string?> AllowedRoots(string? solutionDirectory)
    {
        yield return solutionDirectory;
        // A solution often sits below the repository root (<repo>\Source\<project>\), while the library goes to
        // <repo>\Library\: the repository holding the solution is the user's own tree too.
        yield return RepositoryRootOf(solutionDirectory);
        yield return Path.GetTempPath();
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp");

        string? installDir = TwinCat.TwinCatPaths.GetInstallDir();
        if (installDir is not null) yield return installDir;

        string extra = Environment.GetEnvironmentVariable(AllowedPathsEnvVar) ?? string.Empty;
        foreach (string root in extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return root;
    }

    /// <summary>Levels above the solution directory searched for the repository root.</summary>
    internal const int MaxRepositoryDepth = 6;

    /// <summary>
    /// The nearest directory at or above <paramref name="solutionDirectory"/> that holds a <c>.git</c> folder or file
    /// (a worktree), at most <see cref="MaxRepositoryDepth"/> levels up; null when there is none.
    /// </summary>
    internal static string? RepositoryRootOf(string? solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory)) return null;
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(solutionDirectory));
            for (int level = 0; dir is not null && level <= MaxRepositoryDepth; level++, dir = dir.Parent)
            {
                string git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static bool TryGetFullPath(string? path, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "path is empty.";
            return false;
        }
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex)
        {
            reason = $"path could not be resolved: {ex.Message}";
            return false;
        }
    }

    private static bool IsUnderOrEqual(string fullPath, string fullRoot)
    {
        string trimmedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, trimmedRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        string rootWithSeparator = trimmedRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
