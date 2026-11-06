using System.Globalization;
using System.Text;

namespace PtcXaeMcp.WinForms.TwinCat;

// twincat_plc_static_analysis: runs the PLC Static Analysis (TE1200) through the Automation Interface and reports the
// findings compactly — rule, level, file, line, message — instead of an Error List dump. The results come from the
// SARIF export (TwinCAT >= 3.1.4026.20); a stale SARIF of an earlier run is deleted first so it can never be read as
// this run's result. When the XAE writes no SARIF, the Error List's SA entries are reported and the reason is named.
internal sealed class StaticAnalysisCoordinator(ITwinCatSession session, string? reportDirectory = null)
{
    /// <summary>Findings per page: default 100, at most 1000.</summary>
    internal static readonly Mcp.McpLimit Page = new(100, 1000);
    private static readonly string[] Levels = ["error", "warning", "note"];

    internal static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ptc-xae-mcp", "static-analysis");

    public string Run(bool checkAll = true, string? settingsFile = null, string minLevel = "warning", int? limit = null, int offset = 0)
    {
        string level = (minLevel ?? "").Trim().ToLowerInvariant();
        if (!Levels.Contains(level)) return $"[Error] Unknown minLevel '{minLevel}'. Expected error, warning or note.";
        if (Mcp.McpPaging.Validate(limit, offset) is { } invalid) return invalid;

        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        string? sln = session.ActiveSolutionPath();
        string? solutionDir = string.IsNullOrWhiteSpace(sln) ? null : Path.GetDirectoryName(sln);
        string name = string.IsNullOrWhiteSpace(sln) ? "project" : Path.GetFileNameWithoutExtension(sln);
        string dir = reportDirectory ?? DefaultDirectory;
        string sarifPath = Path.Combine(dir, $"{name}.static-analysis.sarif.json");
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(sarifPath)) File.Delete(sarifPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[Error] The report file '{sarifPath}' could not be prepared: {ex.Message}";
        }

        StaticAnalysisRun run;
        try { run = session.RunStaticAnalysis(checkAll, settingsFile, sarifPath); }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
        catch (Exception ex) { return $"[Error] The static analysis could not run: {ex.Message}"; }

        IReadOnlyList<StaticAnalysisFinding> findings;
        string source;
        if (run.SarifExported && File.Exists(sarifPath))
        {
            try { findings = SarifReader.Parse(File.ReadAllText(sarifPath), solutionDir); }
            catch (FormatException ex) { return $"[Error] {ex.Message} ({sarifPath})"; }
            source = $"SARIF {sarifPath}";
        }
        else
        {
            try { findings = SarifReader.FromErrorList(session.GetErrorList(), solutionDir); }
            catch (Exception ex) when (ComCall.IsBusy(ex)) { return McpErrors.EnsureError(ComCall.Describe(ex)); }
            catch (Exception ex) { return $"[Error] No SARIF ({run.SarifNote ?? "not written"}) and the Error List could not be read: {ex.Message}"; }
            source = $"the Error List (no SARIF: {run.SarifNote ?? "the XAE wrote no file"})";
        }

        string scope = checkAll ? "check all objects" : "objects in use";
        if (findings.Count == 0) return $"Static analysis ({scope}): no findings — clean. Read from {source}.";

        int Count(string l) => findings.Count(f => f.Level == l);
        string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Static analysis ({scope}): {Plural(Count("error"), "error")}, {Plural(Count("warning"), "warning")}, {Plural(Count("note"), "note")} — read from {source}.");
        var rules = findings.GroupBy(f => f.RuleId.Length == 0 ? "(no rule)" : f.RuleId)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(10)
            .Select(g => $"{g.Key} ×{g.Count()}");
        sb.Append("\nRules: ").Append(string.Join(", ", rules));

        int rank = Array.IndexOf(Levels, level);
        var shown = findings.Where(f => Array.IndexOf(Levels, f.Level) <= rank).ToList();
        var page = Mcp.McpPaging.Apply(shown, limit, offset, Page);
        foreach (var f in page.Items)
            sb.Append(CultureInfo.InvariantCulture, $"\n  [{f.Level}] {f.RuleId}{Location(f)} — {f.Message}");

        if (shown.Count == 0) sb.Append(CultureInfo.InvariantCulture, $"\nNo findings at minLevel {level}.");
        else if (page.Returned == 0) sb.Append(CultureInfo.InvariantCulture, $"\noffset {offset} is past the last of {shown.Count} findings (minLevel {level}).");
        else sb.Append(CultureInfo.InvariantCulture, $"\nShowing {page.Offset + 1}–{page.Offset + page.Returned} of {shown.Count} (minLevel {level}).");
        sb.Append('\n').Append(page.Footer());
        return sb.ToString();
    }

    private static string Location(StaticAnalysisFinding f)
    {
        if (f.File is null && f.Line is null) return "";
        string line = f.Line is int l ? $"L{l}{(f.Column is int c ? $":{c}" : "")}" : "";
        return " " + string.Join(" ", new[] { f.File, line }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
