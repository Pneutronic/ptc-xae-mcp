using System.Collections.Concurrent;
using System.Xml;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>Outcome of <see cref="StaleBufferGuard.Check"/>: files whose disk content the XAE does not hold, and files that could not be compared.</summary>
internal sealed record StaleCheckResult(IReadOnlyList<string> Stale, IReadOnlyList<string> Inconclusive)
{
    public static readonly StaleCheckResult None = new([], []);
}

/// <summary>
/// The XAE compiles the PLC objects it holds in memory and does not reload a source file changed on disk
/// outside it — the build then compiles the old state, and its File.SaveAll can write that old state back
/// over the change. Before a build this compares the files changed since the baseline (mtime) with what the
/// XAE holds (ITcPlcDeclaration.DeclarationText / ITcPlcImplementation.ImplementationText).
///
/// The baseline is the per-file mtime at the last point disk and XAE were known to agree: the open_project
/// that loaded the solution, or the last check without findings. The mtime only selects candidates; the
/// content comparison decides, so a save from the XAE (or by this server) never reports a stale file.
/// </summary>
internal sealed class StaleBufferGuard
{
    /// <summary>The process-wide baseline; the XAE and its open solution outlive every coordinator.</summary>
    public static StaleBufferGuard Shared { get; } = new();

    // .plcproj path → (source file path → mtime UTC).
    private readonly ConcurrentDictionary<string, Dictionary<string, DateTime>> _baseline = new(StringComparer.OrdinalIgnoreCase);

    internal Func<string, DateTime> LastWriteUtc { get; init; } = File.GetLastWriteTimeUtc;

    /// <summary>Records the current mtimes of every PLC source file of the open solution (after open_project).</summary>
    public void Establish(ITwinCatSession session)
    {
        foreach (var project in Projects(session, plcProjectName: null, allProjects: true))
            Record(project.PlcprojPath, PlcSourceFiles.SourceFiles(project.PlcprojPath));
    }

    /// <summary>
    /// Compares the candidates of the PLC project(s) a build compiles. A project seen for the first time only
    /// gets its baseline (a change from before that point is not detectable). Without stale files the
    /// baseline moves to the current mtimes.
    /// </summary>
    public StaleCheckResult Check(ITwinCatSession session, string? plcProjectName, bool allProjects)
    {
        var stale = new List<string>();
        var inconclusive = new List<string>();
        foreach (var project in Projects(session, plcProjectName, allProjects))
        {
            var files = PlcSourceFiles.SourceFiles(project.PlcprojPath);
            if (!_baseline.TryGetValue(project.PlcprojPath, out var known))
            {
                Record(project.PlcprojPath, files);
                continue;
            }

            int staleBefore = stale.Count;
            string plcDir = Path.GetDirectoryName(project.PlcprojPath) ?? string.Empty;
            foreach (string file in files)
            {
                if (!File.Exists(file)) continue;
                DateTime now = LastWriteUtc(file);
                if (known.TryGetValue(file, out var then) && now <= then) continue;

                string relative = Path.GetRelativePath(plcDir, file);
                IReadOnlyList<PlcTextSection> onDisk, inXae;
                try { onDisk = PlcSourceFiles.ParseSections(File.ReadAllText(file)); }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    inconclusive.Add($"{file}: not readable ({ex.Message})");
                    continue;
                }
                try { inXae = session.ReadPlcObjectSections(project.PlcName, relative); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    inconclusive.Add($"{file}: the XAE object could not be read ({ex.Message})");
                    continue;
                }
                if (!inXae.Any(s => s.Key == "decl" || s.Key == "impl"))
                {
                    inconclusive.Add($"{file}: the XAE object exposes no text");
                    continue;
                }
                if (PlcSourceFiles.Differences(onDisk, inXae).Count > 0) stale.Add(file);
            }
            if (stale.Count == staleBefore) Record(project.PlcprojPath, files);
        }
        return new StaleCheckResult(stale, inconclusive);
    }

    /// <summary>Moves the baseline to the current mtimes, e.g. after a build that saved the XAE state to disk.</summary>
    public void Refresh(ITwinCatSession session, string? plcProjectName, bool allProjects)
    {
        foreach (var project in Projects(session, plcProjectName, allProjects))
            if (_baseline.ContainsKey(project.PlcprojPath))
                Record(project.PlcprojPath, PlcSourceFiles.SourceFiles(project.PlcprojPath));
    }

    /// <summary>Forgets every baseline (a new solution was opened).</summary>
    public void Reset() => _baseline.Clear();

    private void Record(string plcproj, IReadOnlyList<string> files)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
            if (File.Exists(f)) map[f] = LastWriteUtc(f);
        _baseline[plcproj] = map;
    }

    // build_plc matches its project by the .plcproj file name (FindProjectUniqueName) and builds the first
    // .plcproj without one; solution builds compile every PLC project.
    private static IReadOnlyList<PlcProjectFile> Projects(ITwinCatSession session, string? plcProjectName, bool allProjects)
    {
        string? sln = session.ActiveSolutionPath();
        if (string.IsNullOrEmpty(sln) || !File.Exists(sln)) return [];
        var all = PlcSourceFiles.FindPlcProjects(sln);
        if (allProjects) return all;
        if (plcProjectName is null) return all.Take(1).ToList();
        return all.Where(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p.PlcprojPath), plcProjectName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PlcName, plcProjectName, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
