using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// #161 — server-side TMC: the ADS extension
/// of the HMI server serves a runtime's PLC symbols from a binary symbol segment instead of uploading
/// them from a PLC over ADS. Pure rules only (names, paths, the base-type lookup), so every one is
/// unit-tested without an XAE. Each is mirrored from decompiled TE2000 14.17.22.1:
/// <list type="bullet">
/// <item><c>TcHmiServerCommunication.AttachTmcFile/DetachTmcFile</c> — the two config writes,</item>
/// <item><c>TmcLightPipeline</c> / <c>TmcPipelineOptions</c> — where the .bin lives and how the
///   written path is formed (relative to the engineering server's working directory),</item>
/// <item><c>TcHmiServerInstance.GetWorkingDirectory</c> — that working directory,</item>
/// <item><c>TcHmiTmcParser.TmcParser</c> / <c>TmcResultExporter</c> / <c>Helpers</c> — the base types and
///   the file name.</item>
/// </list>
/// </summary>
internal static class HmiRuntimeTmc
{
    internal const string DataSymbolUpAndDownload = "DATA_SYMBOLUPANDDOWNLOAD";
    internal const string UseTmc = "USE_TMC";

    /// <summary>TcHmiServerInstance: the engineering servers' working directories live in
    /// <c>&lt;dir&gt;\.engineering_servers\&lt;WorkingDirectory&gt;</c>.</summary>
    internal const string EngineeringServersFolder = ".engineering_servers";

    /// <summary>TmcPipelineOptions.ExchangeFolder — TE2000's own sub-folder for converted TMC data.</summary>
    internal const string ExchangeFolder = "Tmc";

    /// <summary>TmcResultExporter.ExportFileSuffix.</summary>
    internal const string UploadInfoSuffix = "DATA_UPLOADINFO2.bin";

    /// <summary>A config leaf below <c>ADS.Config::RUNTIMES::&lt;runtime&gt;::TMC</c>, exactly the symbol
    /// names <c>TcHmiServerCommunication.AttachTmcFile</c> writes.</summary>
    internal static string ConfigSymbol(string runtime, string leaf) =>
        $"{HmiCoordinator.AdsRuntimesSymbol}::{runtime}::TMC::{leaf}";

    /// <summary>The runtime's whole <c>TMC</c> object, written when the runtime has none yet.</summary>
    internal static string TmcObjectSymbol(string runtime) => $"{HmiCoordinator.AdsRuntimesSymbol}::{runtime}::TMC";

    /// <summary>The <c>TMC</c> object holding both leaves — the value accepted live on 2026-11-18.</summary>
    internal static string TmcObjectJson(string dataSymbolUpAndDownload, bool useTmc) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [DataSymbolUpAndDownload] = dataSymbolUpAndDownload,
            [UseTmc] = useTmc,
        });

    // ---- where the .bin goes ------------------------------------------------------

    /// <summary>
    /// The <c>Server</c> folder's <c>WorkingDirectory</c> metadata of a .hmiproj
    /// (<c>&lt;Folder Include="Server"&gt;&lt;WorkingDirectory&gt;X&lt;/WorkingDirectory&gt;&lt;/Folder&gt;</c>),
    /// which TcHmiServerInstance.GetWorkingDirectory uses as the sub-folder name and sets to the
    /// project name when it is missing. Null when absent, unreadable or not a plain folder name — a
    /// value that could climb out of <c>.engineering_servers</c> is never used.
    /// </summary>
    internal static string? ReadServerWorkingDirectory(string? projectFile)
    {
        if (string.IsNullOrEmpty(projectFile)) return null;
        XDocument doc;
        try { doc = XDocument.Load(projectFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }

        string? value = doc.Descendants()
            .Where(e => e.Name.LocalName == "Folder"
                        && string.Equals(((string?)e.Attribute("Include"))?.Trim().TrimEnd('\\', '/'), "Server",
                            StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => e.Elements())
            .Where(e => e.Name.LocalName == "WorkingDirectory")
            .Select(e => e.Value.Trim())
            .FirstOrDefault(v => v.Length > 0);
        return IsPlainFolderName(value) ? value : null;
    }

    private static bool IsPlainFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "." && name != ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Where the engineering server of an HMI project runs, in the order TE2000 decides it
    /// (TcHmiServerInstance.GetWorkingDirectory): the solution-wide base is the parent of the
    /// .hmiproj folder (found by scanning below the solution) or else the solution directory; the
    /// sub-folder is the WorkingDirectory metadata or else the project name. All distinct
    /// combinations, most likely first — the caller takes the first one that EXISTS (the server
    /// creates it on start), so a wrong guess fails as "not found" instead of writing elsewhere.
    /// </summary>
    internal static IReadOnlyList<string> ServerFolderCandidates(
        string? hmiProjectDirectory, string hmiProjectName, string? workingDirectoryName, string? solutionDirectory)
    {
        var bases = new List<string>();
        if (!string.IsNullOrWhiteSpace(hmiProjectDirectory)
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(hmiProjectDirectory)) is { Length: > 0 } parent)
            bases.Add(parent);
        if (!string.IsNullOrWhiteSpace(solutionDirectory)) bases.Add(solutionDirectory);

        var names = new List<string>();
        if (IsPlainFolderName(workingDirectoryName)) names.Add(workingDirectoryName!.Trim());
        if (IsPlainFolderName(hmiProjectName)) names.Add(hmiProjectName.Trim());

        var candidates = new List<string>();
        foreach (string b in bases)
            foreach (string n in names)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(b, EngineeringServersFolder, n)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }
                if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
            }
        return candidates;
    }

    /// <summary>
    /// <c>&lt;runtime&gt;_&lt;tmc name&gt;.DATA_UPLOADINFO2.bin</c>. TmcResultExporter names the file
    /// <c>&lt;PlcName&gt;.DATA_UPLOADINFO2.bin</c>; the runtime is part of the PlcName here so two runtimes
    /// fed from same-named TMC files of different PLC projects never overwrite each other.
    /// </summary>
    internal static string UploadInfoFileName(string runtime, string tmcPath)
    {
        string name = MakeFileSystemSafe($"{runtime}_{Path.GetFileNameWithoutExtension(tmcPath)}");
        return (string.IsNullOrEmpty(name) ? "Module" : name) + "." + UploadInfoSuffix;
    }

    /// <summary>TcHmiTmcParser.Helpers.MakeFileSystemSafe: invalid file-name characters → '_',
    /// trimmed, at most 80 characters.</summary>
    internal static string MakeFileSystemSafe(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length > 80 ? safe[..80] : safe;
    }

    /// <summary>
    /// The value written to <c>DATA_SYMBOLUPANDDOWNLOAD</c>: the file relative to the engineering
    /// server's working directory, backslash-separated — TmcLightPipeline.AttachExportedFiles does
    /// <c>Path.Combine(SolutionDir, ServerFolder).GetRelativePathTo(dir) + file name</c>. Null when the
    /// file does not lie below that folder (it must: the server resolves the value against it).
    /// </summary>
    internal static string? RelativeToServerFolder(string serverFolder, string file)
    {
        string relative;
        try { relative = Path.GetRelativePath(serverFolder, file); }
        catch (ArgumentException) { return null; }
        if (string.IsNullOrEmpty(relative) || relative == "." || Path.IsPathRooted(relative)) return null;
        string normalized = relative.Replace('/', '\\');
        return normalized == ".." || normalized.StartsWith(@"..\", StringComparison.Ordinal) ? null : normalized;
    }

    /// <summary>Read-back comparison of a configured path: separators and case do not matter,
    /// empty and null are the same "no file".</summary>
    internal static bool SamePath(string? left, string? right)
    {
        static string Normalize(string? p) => (p ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Atomic replace, as TcHmiTmcParser.Helpers.AtomicWriteBytes: write a sibling .tmp,
    /// then move it over the target, so a server reading the file never sees half of it.</summary>
    internal static void WriteAtomically(string path, byte[] content)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content, 0, content.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    // ---- what the conversion needs ---------------------------------------------------

    /// <summary>TmcParser.TwinCatSystemLibraryNames — a Build_ folder counts only with one of them.</summary>
    internal static readonly string[] SystemManagerLibraries =
        ["TcSysManCmd.dll", "TcSysManCmd.x64.dll", "TwinCAT System Manager.dll", "TwinCAT System Manager.x64.dll"];

    internal const string BaseTypesFileName = "TcBaseTypes.tmc";

    /// <summary>
    /// The base-type TMC files TwinCAT's type system needs before it can read a PLC TMC —
    /// TmcParser.LoadBaseTypes, transcribed: the newest <c>Build_&lt;version&gt;</c> below
    /// <paramref name="componentsBasePath"/> (<c>…\TwinCAT\3.1\Components\Base</c>) that holds a system
    /// manager library → every <c>*.tmc</c> of its <c>Config\Io</c> and <c>Config\Modules</c>, TcBaseTypes.tmc
    /// first; without such a build, <c>%TWINCAT3DIR%\Config\Io\TcBaseTypes.tmc</c>.
    /// </summary>
    internal static (IReadOnlyList<string>? Files, string? Error) BaseTypeFiles(
        string componentsBasePath, string? twinCat3Dir)
    {
        string? build = null;
        Version? newest = null;
        if (Directory.Exists(componentsBasePath))
            foreach (string directory in Directory.GetDirectories(componentsBasePath))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith("Build_", StringComparison.Ordinal)
                    || !SystemManagerLibraries.Any(l => File.Exists(Path.Combine(directory, l)))
                    || !Version.TryParse(name["Build_".Length..], out Version? version))
                    continue;
                if (newest is null || version > newest) { newest = version; build = directory; }
            }

        if (build is not null)
        {
            string config = Path.Combine(build, "Config");
            string io = Path.Combine(config, "Io");
            if (!Directory.Exists(io))
                return (null, $"the TwinCAT base types folder '{io}' does not exist");
            var files = new List<string>();
            foreach (string folder in new[] { io, Path.Combine(config, "Modules") })
                if (Directory.Exists(folder))
                    files.AddRange(Directory.GetFiles(folder, "*.tmc"));
            // OrderBy is stable: TcBaseTypes.tmc first, the rest in the order the folders listed them.
            return (files.OrderBy(f => Path.GetFileName(f).Equals(BaseTypesFileName, StringComparison.Ordinal) ? 0 : 1)
                .ToList(), null);
        }

        if (string.IsNullOrWhiteSpace(twinCat3Dir))
            return (null, $"no TwinCAT build with a system manager below '{componentsBasePath}' and TWINCAT3DIR is not set");
        string fallback = Path.Combine(twinCat3Dir, "Config", "Io", BaseTypesFileName);
        return File.Exists(fallback)
            ? ([fallback], null)
            : (null, $"no TwinCAT build with a system manager below '{componentsBasePath}' and '{fallback}' does not exist");
    }

    /// <summary>
    /// The x64 <c>TcTypeSystem.dll</c> files that can do the conversion, preferred first:
    /// <list type="number">
    /// <item>TE2000's own copy next to its native TMC parser (<c>bin\TmcParser\win-x64\native</c>) — the
    ///   type system version that ships with the HMI engineering and server;</item>
    /// <item>the CommonComServer 2.8 copy, the folder TE2000's managed TmcParser points its DLL search at.</item>
    /// </list>
    /// Measured on the neutral fixture's FixturePlc.tmc (2026-10-10): the first yields the 32 PLC symbols,
    /// the second additionally one module parameter (<c>&lt;name&gt;.DeviceLog Filter</c>) and is
    /// byte-identical to TE2000's TmcParser + TmcResultExporter.
    /// </summary>
    internal static IReadOnlyList<string> NativeLibraryCandidates(string programFilesX86) =>
    [
        Path.Combine(programFilesX86, "Beckhoff", "TwinCAT", "Functions", "TE2000-HMI-Engineering",
            "bin", "TmcParser", "win-x64", "native", "TcTypeSystem.dll"),
        Path.Combine(programFilesX86, "Beckhoff", "TwinCAT", "CommonComServer", "TcTypeSystem", "2.8", "x64",
            "TcTypeSystem.dll"),
    ];
}

/// <summary>A converted TMC: the AdsSymbolUpAndDownload segment (header version 3) of its one module,
/// plus where it came from. <see cref="Payload"/> is written to disk unchanged.</summary>
internal sealed record TmcUploadInfo(
    byte[] Payload,
    string ModuleName,
    string NativeLibrary,
    string? NativeVersion,
    int BaseTypeFileCount);

/// <summary>
/// #161: converts a PLC project's .tmc into the binary symbol segment the HMI server's ADS extension
/// reads with <c>USE_TMC</c> — what TE2000's TmcLightPipeline does with TcHmiTmcParser.TmcParser and
/// TmcResultExporter, done here in-process through <see cref="TcTypeSystemNative"/> (P/Invoke into
/// TwinCAT's native type system; no .NET-Framework assembly is loaded into this host).
/// </summary>
internal static class TmcUploadInfoExporter
{
    private static readonly object Gate = new();
    private static TcTypeSystemNative? _native;

    /// <summary>
    /// Converts <paramref name="tmcPath"/>; <paramref name="instanceName"/> is the name TE2000 passes to
    /// CreateTypeSystem and SymbolInfo (it only prefixes module-parameter symbols, not the PLC ones).
    /// Never throws for an expected failure: no TwinCAT type system installed, no base types, a TMC
    /// the type system rejects, or not exactly one module with symbols (TmcResultExporter would write
    /// several modules into the same file, the last one winning — that is refused, not guessed).
    /// </summary>
    public static (TmcUploadInfo? Info, string? Error) Convert(string tmcPath, string instanceName)
    {
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrEmpty(programFilesX86))
            programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!Environment.Is64BitProcess)
            return (null, "the conversion needs a 64-bit host process (TwinCAT's x64 type system)");

        string componentsBase = Path.Combine(programFilesX86, "Beckhoff", "TwinCAT", "3.1", "Components", "Base");
        IReadOnlyList<string>? baseTypes;
        string? baseError;
        try
        {
            (baseTypes, baseError) = HmiRuntimeTmc.BaseTypeFiles(componentsBase,
                Environment.GetEnvironmentVariable("TWINCAT3DIR"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"the TwinCAT base types below '{componentsBase}' could not be listed: {ex.Message}");
        }
        if (baseTypes is null) return (null, baseError);

        lock (Gate)
        {
            if (_native is null)
            {
                IReadOnlyList<string> candidates = HmiRuntimeTmc.NativeLibraryCandidates(programFilesX86);
                string? path = candidates.FirstOrDefault(File.Exists);
                if (path is null)
                    return (null, "TwinCAT's native type system is not installed (looked for " +
                                  string.Join(" and ", candidates.Select(c => $"'{c}'")) + ")");
                try { _native = TcTypeSystemNative.Load(path); }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                                               or EntryPointNotFoundException or ArgumentException)
                {
                    return (null, $"'{path}' could not be loaded: {ex.Message}");
                }
            }

            IReadOnlyList<TcTypeSystemNative.ModulePayload> payloads;
            try { payloads = _native.SymbolInfos(tmcPath, baseTypes, instanceName); }
            catch (InvalidOperationException ex) { return (null, ex.Message); }
            // A C++ exception escaping the native type system arrives as SEHException; an access
            // violation is not catchable at all and would end the process (not seen in any run).
            catch (System.Runtime.InteropServices.SEHException ex)
            {
                return (null, $"TwinCAT's type system failed on '{tmcPath}': {ex.Message}");
            }

            if (payloads.Count == 0)
                return (null, $"TwinCAT's type system found no symbols in '{tmcPath}'");
            if (payloads.Count > 1)
                return (null, $"'{tmcPath}' holds {payloads.Count} modules with symbols " +
                              $"({string.Join(", ", payloads.Select(p => p.ModuleName))}); one runtime can serve " +
                              "only one — pass the TMC file of a single PLC project");

            TcTypeSystemNative.ModulePayload module = payloads[0];
            return (new TmcUploadInfo(module.Payload, module.ModuleName, _native.LibraryPath,
                FileVersion(_native.LibraryPath), baseTypes.Count), null);
        }
    }

    private static string? FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileVersion; }
        catch (Exception ex) when (ex is IOException or ArgumentException) { return null; }
    }
}
