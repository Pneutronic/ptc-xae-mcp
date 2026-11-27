namespace PtcXaeMcp.WinForms.TwinCat.HostSelection;

/// <summary>One installed TcXaeShell and whether it can open TwinCAT projects (null = could not be checked).</summary>
internal sealed record XaeShellInstall(string Name, string ExePath, bool? HasTwinCat);

/// <summary>
/// Whether a TcXaeShell can open TwinCAT projects. The TwinCAT XAE extension registers the TwinCAT project type
/// in a <c>.pkgdef</c> below the shell's <c>Common7\IDE\Extensions</c>. A shell installed without the TwinCAT
/// integration lacks that entry; it then marks <c>.tsproj</c>/<c>.tcmproj</c> as unsupported and the solution
/// opens without a System Manager. Read from the files only: no shell is started.
/// </summary>
internal static class XaeShellIntegration
{
    /// <summary>Project type GUID of the TwinCAT system project (<c>.tsproj</c>).</summary>
    internal const string TwinCatProjectTypeGuid = "{B1E792BE-AA5F-4E3C-8C82-674BF9C0715B}";

    private const string ProjectKey = @"\Projects\" + TwinCatProjectTypeGuid;
    private const string BeckhoffExtensions = "Beckhoff Automation GmbH";

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    /// <summary>
    /// True when the shell at <paramref name="exePath"/> registers the TwinCAT project type, false when it does not,
    /// null when the shell is not installed or its extensions cannot be read.
    /// </summary>
    internal static bool? HasTwinCatProjectType(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;
            string? ide = Path.GetDirectoryName(exePath);
            if (ide is null) return null;
            string extensions = Path.Combine(ide, "Extensions");
            if (!Directory.Exists(extensions)) return false;

            // The Beckhoff folder holds the entry on every installation seen so far; look there first, then
            // everywhere else so a vendor folder with another name is still found.
            string beckhoff = Path.Combine(extensions, BeckhoffExtensions);
            if (Directory.Exists(beckhoff) && AnyPkgdefRegisters(beckhoff)) return true;
            return AnyPkgdefRegisters(extensions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool AnyPkgdefRegisters(string folder) =>
        Directory.EnumerateFiles(folder, "*.pkgdef", Recursive)
            .Any(file => File.ReadLines(file).Any(line => line.Contains(ProjectKey, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The installed shells (64-bit first) with their TwinCAT integration, for twincat_doctor.</summary>
    internal static IReadOnlyList<XaeShellInstall> Installed(XaeHostResolver resolver, Func<string, bool> exeExists,
        Func<string, bool?> hasTwinCat) =>
        new[] { XaeHostClass.Shell64, XaeHostClass.Shell32 }
            .Select(c => resolver.Resolve(c))
            .Where(s => exeExists(s.ExePath))
            .Select(s => new XaeShellInstall(XaeHostReport.Name(s.ProgId), s.ExePath, hasTwinCat(s.ExePath)))
            .ToList();

    /// <summary>Live probe for twincat_doctor.</summary>
    internal static IReadOnlyList<XaeShellInstall> InstalledLive() =>
        Installed(new XaeHostResolver(), File.Exists, HasTwinCatProjectType);
}
