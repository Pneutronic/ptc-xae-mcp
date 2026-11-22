using PtcXaeMcp.WinForms.Localization;

namespace PtcXaeMcp.WinForms.Dialogs;

/// <summary>
/// The sample project shipped with the app (issue #273): the neutral fixture
/// <c>tests/Fixtures/PtcXaeMcpFixture</c> (Beckhoff standard libraries only), copied next to the
/// executable under <c>Sample\PtcXaeMcpFixture\</c>. "Open sample project" never opens the shipped
/// copy in place (the install directory may be read-only and the XAE writes into the project):
/// it copies it to a fresh timestamped folder under the user's Documents and opens that.
/// </summary>
internal static class SampleProject
{
    public const string FolderName = "PtcXaeMcpFixture";
    public const string SolutionFileName = "PtcXaeMcpFixture.sln";

    /// <summary>Documents subfolder that collects all sample copies.</summary>
    public const string DocumentsFolder = "Pneutronic XAE MCP";

    /// <summary>The shipped (read-only source) copy below the application directory.</summary>
    public static string ShippedDirectory(string appBaseDirectory) =>
        Path.Combine(appBaseDirectory, "Sample", FolderName);

    /// <summary>Target folder for one copy: <c>&lt;Documents&gt;\Pneutronic XAE MCP\Sample-yyyyMMdd-HHmmss</c>.</summary>
    public static string TargetDirectory(string documentsFolder, DateTime now) =>
        Path.Combine(documentsFolder, DocumentsFolder, "Sample-" + now.ToString("yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Like <see cref="TargetDirectory"/>, but never an existing folder: a second copy in
    /// the same second gets <c>-2</c>, <c>-3</c>, …</summary>
    public static string UniqueTargetDirectory(string documentsFolder, DateTime now, Func<string, bool>? exists = null)
    {
        exists ??= Directory.Exists;
        string baseDir = TargetDirectory(documentsFolder, now);
        string candidate = baseDir;
        for (int i = 2; exists(candidate); i++)
            candidate = baseDir + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>Recursively copies <paramref name="source"/> to <paramref name="target"/> (created) and
    /// clears the read-only attribute on the copies. Returns the path of the copied solution.</summary>
    public static string CopyTo(string source, string target)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(Loc.Format("sample.missing", source));
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, dest, overwrite: false);
            File.SetAttributes(dest, FileAttributes.Normal);
        }
        string sln = Path.Combine(target, SolutionFileName);
        if (!File.Exists(sln))
            throw new FileNotFoundException(Loc.Format("sample.noSolution", SolutionFileName), sln);
        return sln;
    }
}
