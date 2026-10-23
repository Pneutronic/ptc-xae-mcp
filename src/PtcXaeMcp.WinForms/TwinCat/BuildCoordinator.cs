using System.Linq;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

// Summary is the build result text (status + errors), shown in the build
// tab's result box. Log is the raw VS Error List logger output, shown in
// the dedicated logger panel. For MCP the two are joined into one string.
internal readonly record struct BuildOutcome(string Summary, string Log);

/// <summary>
/// Outcome of <see cref="BuildCoordinator.BuildSolutionForApply"/>: the session's build result (null
/// when the build did not return), every XAE dialog answered on the way, and the error that ended
/// the wait (blocked by a dialog, timeout, precondition) — null when the build returned.
/// </summary>
internal sealed record ApplyBuildOutcome(
    BuildResult? Result,
    IReadOnlyList<string> DialogsAnswered,
    IReadOnlyList<string> DialogWarnings,
    string? Error);

internal sealed class BuildCoordinator(ITwinCatSession session)
{
    /// <summary>
    /// The solution build apply_schema needs to put its GVL into the process image (issue #155
    /// step 8, R7). The same build path as twincat_build_solution — <see cref="ITwinCatSession.BuildSolution"/>
    /// (save, <c>SolutionBuild.Build(true)</c>, LastBuildInfo, error list via DTE2) — instead of the
    /// runner's second error-list reader. The build runs on its own STA thread while
    /// <paramref name="dialogs"/> answers the XAE modals of <see cref="Apply.ApplyBuildDialogPolicy"/>;
    /// an unknown modal ends the wait with an error naming it (the blocked build then finishes in
    /// the background once someone answers it), as do <paramref name="timeout"/> and cancellation.
    /// The watcher is started here and left running: the caller stops it (dialogs also appear
    /// right after the build returns).
    /// </summary>
    public ApplyBuildOutcome BuildSolutionForApply(Apply.IApplyBuildDialogWatcher dialogs, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return new ApplyBuildOutcome(null, [], [], error);

        dialogs.Start();
        BuildResult? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            using var filter = ComMessageFilter.Register();
            try { result = session.BuildSolution(); }
            catch (Exception ex) { failure = ex; } // com-allow: thread entry — handed to the waiting caller below
        })
        { IsBackground = true, Name = "apply-schema build" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!thread.Join(TimeSpan.FromMilliseconds(200)))
        {
            if (dialogs.Blocker is string blocker)
                return Outcome(null, $"[Error] The build is blocked by an XAE dialog apply_schema does not answer: '{blocker}'. " +
                                     "Answer or close it in the XAE (the build then finishes in the background), check the project, then re-run.");
            if (cancellationToken.IsCancellationRequested)
                return Outcome(null, "[Error] Build cancelled while waiting for SolutionBuild.Build; it may still finish in the XAE.");
            if (sw.Elapsed >= timeout)
                return Outcome(null, $"[Error] The build did not finish within {timeout.TotalSeconds:F0}s" +
                                     (XaeModalProbe.FindBlockingDialog() is string modal ? $"; XAE dialog open: '{modal}'." : "."));
        }

        if (failure is not null) return Outcome(null, McpErrors.EnsureError(ComCall.Describe(failure)));
        return Outcome(result, null);

        ApplyBuildOutcome Outcome(BuildResult? r, string? e) => new(r, dialogs.Answered, dialogs.Warnings, e);
    }

    /// <summary>One-line build verdict for the apply log: status, compiler summary, first errors.</summary>
    internal static string DescribeForApply(BuildResult result)
    {
        var sb = new StringBuilder(result.Success
            ? $"Build succeeded (failedProjects={result.FailedProjects})."
            : $"Build failed (failedProjects={result.FailedProjects})." +
              (result.FailureReason is null ? "" : $" Reason: {result.FailureReason}"));
        if (result.CompilerSummary is not null) sb.Append(CultureInfo.InvariantCulture, $" Compiler: {result.CompilerSummary}.");
        var errors = result.Errors.Where(e => e.Level == "error").Take(5).ToList();
        foreach (var e in errors)
            sb.Append(CultureInfo.InvariantCulture, $" [ERROR]{(e.File is null ? "" : $" ({e.File}{(e.Line.HasValue ? $":{e.Line}" : "")})")} {e.Description}");
        int more = result.Errors.Count(e => e.Level == "error") - errors.Count;
        if (more > 0) sb.Append(CultureInfo.InvariantCulture, $" (+{more} more errors)");
        return sb.ToString();
    }

    // Read-only: unlike BuildPlc/RebuildPlc, does not compile first — reports
    // whatever is currently in the Error List window. Previously the only way
    // to learn the current count was the side effect of ClearErrorList's
    // return value, i.e. a tool that also erases the thing it reports.
    public string GetErrorList()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        IReadOnlyList<BuildError> errors;
        try { errors = session.GetErrorList(); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        if (errors.Count == 0) return "Error list is empty.";

        var sb = new StringBuilder();
        int errorCount = errors.Count(e => e.Level == "error");
        int warningCount = errors.Count(e => e.Level == "warning");
        int messageCount = errors.Count - errorCount - warningCount;
        sb.AppendLine(CultureInfo.InvariantCulture, $"{errors.Count} entries ({errorCount} error, {warningCount} warning, {messageCount} message).");
        foreach (var e in errors)
        {
            string location = e.File is not null
                ? $" ({e.File}{(e.Line.HasValue ? $":{e.Line}" : "")})"
                : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{e.Level.ToUpperInvariant()}]{location} {e.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>The disk-vs-XAE baseline; tests pass their own instance.</summary>
    internal StaleBufferGuard StaleGuard { get; init; } = StaleBufferGuard.Shared;

    public string BuildSolution(bool ignoreStaleFiles = false) => Combine(BuildSolutionDetailed(ignoreStaleFiles));

    public string BuildPlc(string? plcProjectName, bool ignoreStaleFiles = false) => Combine(BuildPlcDetailed(plcProjectName, ignoreStaleFiles));

    public string RebuildSolution(bool ignoreStaleFiles = false) => Combine(RebuildSolutionDetailed(ignoreStaleFiles));

    public string RebuildPlc(string? plcProjectName, bool ignoreStaleFiles = false) => Combine(RebuildPlcDetailed(plcProjectName, ignoreStaleFiles));

    public BuildOutcome BuildSolutionDetailed(bool ignoreStaleFiles = false) =>
        CheckedBuild(null, allProjects: true, ignoreStaleFiles, () =>
        {
            var r = session.BuildSolution();
            return (FormatBuildResult(r), r.Log);
        });

    public BuildOutcome BuildPlcDetailed(string? plcProjectName, bool ignoreStaleFiles = false) =>
        CheckedBuild(plcProjectName, allProjects: false, ignoreStaleFiles, () =>
        {
            var r = session.BuildPlc(plcProjectName);
            return (FormatBuildResult(r), r.Log);
        });

    public BuildOutcome RebuildSolutionDetailed(bool ignoreStaleFiles = false) =>
        CheckedBuild(null, allProjects: true, ignoreStaleFiles, () =>
        {
            var r = session.RebuildSolution();
            return (FormatRebuildResult(r), r.Log);
        });

    public BuildOutcome RebuildPlcDetailed(string? plcProjectName, bool ignoreStaleFiles = false) =>
        CheckedBuild(plcProjectName, allProjects: false, ignoreStaleFiles, () =>
        {
            var r = session.RebuildPlc(plcProjectName);
            return (FormatRebuildResult(r), r.Log);
        });

    /// <summary>
    /// The preflights every build runs before it compiles: stale PLC files (refuses unless ignored — the
    /// build's own File.SaveAll would otherwise write the XAE's old state over the change) and library
    /// references (warns only). <paramref name="allProjects"/> = a solution build, which compiles every PLC project.
    /// </summary>
    private BuildOutcome CheckedBuild(
        string? plcProjectName, bool allProjects, bool ignoreStaleFiles, Func<(string Summary, string Log)> build)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return new BuildOutcome(error, "");

        var (staleLines, refuse, hadStale) = StalePreflight(plcProjectName, allProjects, ignoreStaleFiles);
        if (refuse is not null) return new BuildOutcome(refuse, "");
        string? libraries = LibraryPreflight(plcProjectName, allProjects);

        (string Summary, string Log) result;
        try { result = build(); }
        catch (Exception ex) { return new BuildOutcome($"[Error] {ex.Message}", ""); }

        // The build saved the XAE state (File.SaveAll): disk and XAE agree again. Not after an ignored stale
        // file — the save writes only dirty objects, so the file can still differ and must be reported again.
        if (!hadStale)
        {
            try { StaleGuard.Refresh(session, plcProjectName, allProjects); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var lines = staleLines.ToList();
        if (libraries is not null) lines.Add(libraries);
        lines.Add(result.Summary);
        return new BuildOutcome(string.Join("\n", lines), result.Log);
    }

    private (IReadOnlyList<string> Lines, string? Refuse, bool HadStale) StalePreflight(
        string? plcProjectName, bool allProjects, bool ignoreStaleFiles)
    {
        StaleCheckResult check;
        try { check = StaleGuard.Check(session, plcProjectName, allProjects); }
        catch (Exception ex) { return ([$"[Note] Stale-file check skipped: {ex.Message}"], null, false); }

        var lines = new List<string>();
        if (check.Stale.Count > 0)
        {
            string files = $"{check.Stale.Count} PLC file{(check.Stale.Count == 1 ? "" : "s")} changed on disk outside the XAE " +
                           $"since the project was loaded: {string.Join("; ", check.Stale)}.";
            if (!ignoreStaleFiles)
                return ([], $"[Error] {files} The XAE would build its in-memory state, and the build's save could write " +
                            "that state over the change. Reload with twincat_open_project(closeCurrentFirst=true), or pass " +
                            "ignoreStaleFiles=true to build the in-memory state anyway.", true);
            lines.Add($"[Warn] {files} Built the XAE's in-memory state (ignoreStaleFiles=true); the build's save may have " +
                      "written that state over the files.");
        }
        if (check.Inconclusive.Count > 0)
            lines.Add($"[Warn] Stale-file check inconclusive for {check.Inconclusive.Count} file" +
                      $"{(check.Inconclusive.Count == 1 ? "" : "s")}: {string.Join("; ", check.Inconclusive)}.");
        return (lines, null, check.Stale.Count > 0);
    }

    /// <summary>
    /// A referenced library that is not installed (or only in another version) turns the build's error list into
    /// noise — seen live: 513 errors before, 57 after installing the referenced version. Returns a [Warn] line
    /// naming every unsatisfied reference, a [Note] when the check itself could not run, or null. Never blocks
    /// the build. A PLC build without a project name checks the first PLC project, the one it builds.
    /// </summary>
    private string? LibraryPreflight(string? plcProjectName, bool allProjects)
    {
        IReadOnlyList<PlcLibraryContext> contexts;
        try
        {
            contexts = session.ReadPlcLibraryContexts(allProjects ? null : plcProjectName);
            if (!allProjects && plcProjectName is null) contexts = contexts.Take(1).ToList();
        }
        catch (Exception ex) { return $"[Note] Library check skipped: {ex.Message}"; }

        var problems = new List<string>();
        foreach (var context in contexts)
        {
            foreach (var item in LibraryCoordinator.Classify(context.References, context.Installed))
            {
                if (item.Status == AvailabilityStatus.Installed) continue;
                string what = item.Status == AvailabilityStatus.Missing
                    ? "missing"
                    : $"version mismatch (installed {item.Match?.Version ?? "-"})";
                problems.Add($"{context.PlcProject}: {item.Reference.Name} {item.Reference.Version} {what}");
            }
        }
        if (problems.Count == 0) return null;

        return $"[Warn] Build result not conclusive: {problems.Count} library reference{(problems.Count == 1 ? "" : "s")} " +
               $"not satisfied — {string.Join("; ", problems)}. The error list then reflects the missing libraries, " +
               "not the code. Install the referenced versions (twincat_install_library) and rebuild.";
    }

    private static string FormatRebuildResult(RebuildResult result)
    {
        var sb = new StringBuilder();
        if (result.Success)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build succeeded (failedProjects={result.FailedProjects}).");
        else
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build failed (failedProjects={result.FailedProjects}).");
        if (!result.Success && result.FailureReason is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Reason: {result.FailureReason}");

        AppendBuildTruth(sb, result.CompilerSummary, result.Artifact);

        foreach (var e in result.Errors)
        {
            string location = e.File is not null
                ? $" ({e.File}{(e.Line.HasValue ? $":{e.Line}" : "")})"
                : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{e.Level.ToUpperInvariant()}]{location} {e.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Combine(BuildOutcome o) =>
        string.IsNullOrWhiteSpace(o.Log)
            ? o.Summary
            : $"{o.Summary}\n\n---- Logger output ----\n{o.Log}";

    /// <param name="runTimeoutSeconds">With <paramref name="autoConfirmRestart"/>: how long to wait after the
    /// restart for the target's TwinCAT system to read Run (issue #195); see <see cref="ActivationWait.ClampDeadline"/>.</param>
    public string ActivateConfig(
        bool autoConfirmRestart = false,
        bool autoGenerateLicenses = false,
        bool autoConfirmLogin = false,
        LoginMode loginMode = LoginMode.OnlineChange,
        bool updateBootProject = true,
        bool allowDownloadFallback = false,
        int? runTimeoutSeconds = null)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        ActivateResult result;
        try
        {
            result = session.ActivateConfig(
                autoConfirmRestart, autoGenerateLicenses,
                autoConfirmLogin, loginMode, updateBootProject, allowDownloadFallback,
                ActivationWait.ClampDeadline(runTimeoutSeconds));
        }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        if (!result.Success)
            return $"[Error] Activation failed: {result.Message}";

        return $"Activated: {result.Message}";
    }

    public string GetLicenseInfo()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        LicenseInfoResult info;
        try { info = session.GetLicenseInfo(); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        return FormatLicenseInfo(info);
    }

    public string ListRequiredLicenses()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        RequiredLicensesResult result;
        try { result = session.ListRequiredLicenses(); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        return FormatRequiredLicenses(result);
    }

    public string AddRequiredLicense(string? licenseId, string? orderNo)
    {
        string? inputError = ManualSelectXml.ValidateAddInput(licenseId, orderNo);
        if (inputError is not null) return $"[Error] {inputError}";

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        AddRequiredLicenseResult result;
        try { result = session.AddRequiredLicense(licenseId, orderNo); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        return $"{result.Message}\n{FormatRequiredLicenses(new RequiredLicensesResult(result.RequiredLicenses, string.Empty))}";
    }

    public string RemoveRequiredLicense(string? licenseId, string? orderNo)
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return error;

        RemoveRequiredLicenseResult result;
        try { result = session.RemoveRequiredLicense(licenseId, orderNo); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }

        return $"{result.Message}\n{FormatRequiredLicenses(new RequiredLicensesResult(result.RequiredLicenses, string.Empty))}";
    }

    internal static string FormatRequiredLicenses(RequiredLicensesResult result)
    {
        if (result.Licenses.Count == 0)
            return "Required licenses: (none)";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Required licenses ({result.Licenses.Count}):");
        foreach (var lic in result.Licenses)
        {
            string desc = lic.Description is not null ? $" — {lic.Description}" : string.Empty;
            string order = lic.OrderNo is not null ? $", order={lic.OrderNo}" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {lic.LicenseId}{order}{desc}");
        }
        return sb.ToString().TrimEnd();
    }

    public string DismissSecurityDialog()
    {
        try { return session.DismissSecurityDialog(); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }
    }

    public string OpenLicenseManagerAndRequestTrial()
    {
        try { return session.OpenLicenseManagerAndRequestTrial(); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }
    }

    public (string Message, byte[]? ScreenshotPng) GenerateTrialLicenses()
    {
        try { return session.GenerateTrialLicenses(); }
        catch (Exception ex) { return ($"[Error] {ex.Message}", null); }
    }

    public string SubmitSecurityCode(string code)
    {
        try { return session.SubmitSecurityCode(code); }
        catch (Exception ex) { return $"[Error] {ex.Message}"; }
    }

    internal static string FormatLicenseInfo(LicenseInfoResult info)
    {
        if (info.Licenses.Count == 0)
            return $"No license entries reported. Source: {info.RawXml}";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Licenses ({info.Licenses.Count}):");
        foreach (var lic in info.Licenses)
        {
            string expiry = lic.ExpiryDate.HasValue
                ? lic.ExpiryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "n/a";
            string order = lic.OrderNo is null ? "" : $"{lic.OrderNo} ";
            string usage = lic.Count is null ? "" : $", used {lic.Used ?? 0}/{lic.Count}";
            string code = lic.ResultCode is null or 0 ? "" : $", code {lic.ResultCode}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"  [{lic.Status}] {order}{lic.Name} (id={lic.Id}, expiry={expiry}{usage}{code})");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {info.RawXml}");
        return sb.ToString().TrimEnd();
    }

    // "Build succeeded" on its own has been wrong before — reported while the
    // artifacts stayed old and the log was an echo of an earlier run. So state
    // the two facts that actually settle it: the compiler's own summary line for
    // THIS build, and the age of the newest compile artifact. A missing summary
    // or an artifact that is not seconds old means nothing was translated,
    // whatever the status flag says.
    private static void AppendBuildTruth(StringBuilder sb, string? compilerSummary, BuildArtifactInfo? artifact)
    {
        sb.AppendLine(compilerSummary is not null
            ? $"Compiler: {compilerSummary}"
            : "Compiler: no summary line in this build's output — nothing appears to have been translated.");

        if (artifact is null)
        {
            sb.AppendLine("Artifact: none found.");
            return;
        }

        string age = artifact.AgeSeconds < 90
            ? $"{artifact.AgeSeconds:F0}s old"
            : $"{artifact.AgeSeconds / 60:F0} min old — STALE, this build did not write it";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Artifact: {artifact.Path} ({artifact.LastWriteUtc:yyyy-MM-dd HH:mm:ss}Z, {age})");
    }

    private static string FormatBuildResult(BuildResult result)
    {
        var sb = new StringBuilder();
        if (result.Success)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build succeeded (failedProjects={result.FailedProjects}).");
        else
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build failed (failedProjects={result.FailedProjects}).");
        if (!result.Success && result.FailureReason is not null)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Reason: {result.FailureReason}");

        AppendBuildTruth(sb, result.CompilerSummary, result.Artifact);

        foreach (var e in result.Errors)
        {
            string location = e.File is not null
                ? $" ({e.File}{(e.Line.HasValue ? $":{e.Line}" : "")})"
                : "";
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{e.Level.ToUpperInvariant()}]{location} {e.Description}");
        }

        return sb.ToString().TrimEnd();
    }
}
