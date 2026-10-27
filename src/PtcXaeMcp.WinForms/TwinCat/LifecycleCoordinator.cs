using System.Globalization;
using PtcXaeMcp.WinForms.TwinCat.HostSelection;
namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed class LifecycleCoordinator(ITwinCatSession session)
{
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The missing .xti references of the solution last opened with allowMissingReferences=true; save and
    /// close-with-save repeat them. Process-wide like the XAE's open solution.
    /// </summary>
    internal static FileReferenceMemo? OpenedWithMissingReferences { get; private set; }

    internal sealed record FileReferenceMemo(string SolutionPath, string Description);

    public async Task<string> OpenProjectAsync(
        string slnPath, bool closeCurrentFirst, CancellationToken ct, bool allowMissingReferences = false)
    {
        var argError = OpenProjectArgs.Validate(slnPath);
        if (argError is not null)
            return $"[Error] {argError}";

        slnPath = SolutionFile.ResolveSolution(slnPath);

        // Before the XAE sees the project: a broken configuration must not be loaded (a later save
        // damages the .tsproj). Unreadable files only warn — the XAE decides about those itself.
        FileReferenceReport references;
        try { references = FileReferencePreflight.Check(slnPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            references = new FileReferenceReport([], [$"file reference check skipped: {ex.Message}"]);
        }
        string? referenceWarn = null;
        if (references.Missing.Count > 0)
        {
            string description = FileReferencePreflight.Describe(references.Missing);
            if (!allowMissingReferences)
                return $"[Error] {description} Opening would load a broken configuration, and saving it damages the .tsproj. " +
                       "Restore the files (e.g. from version control) or pass allowMissingReferences=true.";
            referenceWarn = $"[Warn] Opened with missing references: {description} Do not save before they are restored.";
        }
        if (references.Warnings.Count > 0)
            referenceWarn = (referenceWarn is null ? "" : referenceWarn + "\n") + $"[Warn] {string.Join("; ", references.Warnings)}.";

        string result = await OpenCheckedAsync(slnPath, closeCurrentFirst, ct);
        if (McpErrors.IsError(result)) return result;
        OpenedWithMissingReferences = references.Missing.Count > 0
            ? new FileReferenceMemo(slnPath, FileReferencePreflight.Describe(references.Missing))
            : null;

        HostReport? hostReport = HostReportFor(slnPath);
        if (hostReport is not null)
        {
            result = $"{result} {hostReport.Info}";
            if (hostReport.Warnings.Count > 0)
                referenceWarn = (referenceWarn is null ? "" : referenceWarn + "\n") + string.Join("\n", hostReport.Warnings);
        }
        return referenceWarn is null ? result : $"{referenceWarn}\n{result}";
    }

    /// <summary>Resolves the TcVersion → XAE host mapping; tests pass their own resolver and probes.</summary>
    internal XaeHostResolver HostResolver { get; init; } = new();
    internal Func<string, bool> ExeExists { get; init; } = File.Exists;
    internal Func<IReadOnlyList<string>> RunningHosts { get; init; } = XaeHostReport.RunningHosts;

    // Best effort: the host note must never turn a successful open into a failure.
    private HostReport? HostReportFor(string slnPath)
    {
        try { return XaeHostReport.ForSolution(slnPath, HostResolver, ExeExists, RunningHosts); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task<string> OpenCheckedAsync(string slnPath, bool closeCurrentFirst, CancellationToken ct)
    {

        await session.ConnectOrLaunchAsync(
            launchWithSlnPath: session.IsConnected ? null : slnPath,
            timeout: LaunchTimeout,
            ct: ct);

        if (!session.IsConnected)
            return "[Error] Unable to connect to TwinCAT XAE after launch attempt.";

        bool launchedWithSln = session.HasOpenSolution()
            && string.Equals(session.ActiveSolutionPath(), slnPath, StringComparison.OrdinalIgnoreCase);

        if (!launchedWithSln)
        {
            if (session.HasOpenSolution())
            {
                if (!closeCurrentFirst)
                    return "[Error] A solution is already open. Use closeCurrentFirst=true or close it manually.";
                session.CloseSolution(saveFirst: false);
            }

            session.OpenSolution(slnPath);
        }

        // hmi-views: an HMI-only solution has no System Manager to wait for.
        if (SolutionFile.IsHmiOnly(slnPath))
            return $"Opened {slnPath}. HMI-only solution (no TwinCAT project)." + HmiNote();

        // The solution's Projects collection can be momentarily empty right
        // after Open() while XAE finishes loading (same settle lag as the
        // system-manager poll in TwinCatComSession). Retry Probe() until the
        // system manager is visible or the deadline passes.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (true)
        {
            try
            {
                var status = session.Probe();
                EstablishStaleBaseline();
                return $"Opened {slnPath}. Active project: {status.ProjectName}";
            }
            catch (InvalidOperationException) when (DateTime.UtcNow < deadline)
            {
                // A modal XAE dialog (TE2000 project migration, engineering-server license)
                // blocks the load; say so instead of waiting out the deadline silently.
                if (session.FindHmiBlockingDialog() is { } dialog)
                    return $"[Error] Opened {slnPath}, but the XAE shows a modal dialog: '{dialog}'. " +
                           "Answer it in the XAE (the MCP never does), then check with twincat_get_active_project.";
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    /// <summary>The disk-vs-XAE baseline the builds compare against; tests pass their own instance.</summary>
    internal StaleBufferGuard StaleGuard { get; init; } = StaleBufferGuard.Shared;

    // The XAE just loaded every PLC file from disk: this is the point where disk and XAE agree.
    // Best effort — a baseline that cannot be recorded is recorded by the first build instead.
    private void EstablishStaleBaseline()
    {
        StaleGuard.Reset();
        try { StaleGuard.Establish(session); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { }
    }

    private string HmiNote()
    {
        try
        {
            if (session.FindHmiBlockingDialog() is { } dialog)
                return $" The XAE shows a modal dialog: '{dialog}' — answer it in the XAE (the MCP never does).";
            var list = session.ListHmiProjects();
            return list.Projects.Count == 0
                ? " No HMI project loaded yet — check with twincat_hmi_list_projects."
                : " HMI projects: " + string.Join(", ", list.Projects.Select(p => p.IsReady ? p.Name : p.Name + " (loading)")) + ".";
        }
        catch (Exception ex) when (HmiCoordinator.IsHmiFailure(ex))
        {
            return " HMI projects not readable yet — check with twincat_hmi_list_projects.";
        }
    }

    public string SaveProject()
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        string? referenceWarn = MissingReferencesWarn();
        var r = session.SaveProjectVerified();
        if (r.Documents.Count == 0) return referenceWarn is null ? r.Message : $"{referenceWarn}\n{r.Message}";

        var sb = new System.Text.StringBuilder();
        if (referenceWarn is not null) sb.AppendLine(referenceWarn);
        sb.AppendLine(r.Message);
        foreach (var d in r.Documents)
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {(d.Flushed ? "ok " : "NOT FLUSHED")} {d.Path}");
        return sb.ToString().TrimEnd();
    }

    public string CloseProject(bool saveFirst)
    {
        var error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        var path = session.ActiveSolutionPath();
        CloseSolutionResult r;
        try { r = session.CloseSolution(saveFirst); }
        catch (Exception ex) { return $"[Error] Close failed: {ex.Message}"; }

        string target = path is null ? "" : $": {path}";
        string note = r.SavePrompt is null
            ? ""
            : $" XAE save prompt: {r.SavePrompt}.";
        if (r.ShellSaveFallback)
            note += " (The explicit save left items dirty, so the XAE's own save-on-close was used.)";

        // Read-back: a cancelled/unanswered prompt leaves the solution open.
        bool stillOpen;
        try { stillOpen = session.HasOpenSolution(); }
        catch (Exception) { stillOpen = false; } // unreadable — the close call itself returned normally
        if (stillOpen)
            return $"[Error] The solution is still open after the close{target}.{note}";
        if (r.SavePromptFailed)
            return $"[Error] Closed{target}, but the save prompt was not answered as asked.{note}";

        string verb = saveFirst ? "Saved and closed" : "Closed (without save)";
        string closed = $"{verb}{(path is null ? "." : target)}{note}";
        string? referenceWarn = saveFirst ? MissingReferencesWarn(path) : null;
        if (string.Equals(OpenedWithMissingReferences?.SolutionPath, path, StringComparison.OrdinalIgnoreCase))
            OpenedWithMissingReferences = null;
        return referenceWarn is null ? closed : $"{referenceWarn}\n{closed}";
    }

    // The solution was opened over missing .xti references (allowMissingReferences=true): a save writes
    // that broken configuration. Warned, not refused — the override was deliberate.
    private string? MissingReferencesWarn(string? solutionPath = null)
    {
        var memo = OpenedWithMissingReferences;
        if (memo is null) return null;
        solutionPath ??= session.ActiveSolutionPath();
        if (!string.Equals(memo.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase)) return null;
        return $"[Warn] The project was opened with missing file references ({memo.Description}) — the saved .tsproj " +
               "may no longer reference those nodes.";
    }
}
