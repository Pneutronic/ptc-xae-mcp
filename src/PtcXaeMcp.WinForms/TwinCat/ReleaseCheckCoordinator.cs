using System.Globalization;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>A traffic light. <see cref="Unknown"/> is never shown as green: what could not be determined stays visible.</summary>
internal enum Light { Green, Yellow, Red, Unknown }

/// <summary>One line of the release check.</summary>
internal sealed record ReleaseItem(string Id, string Title, Light Light, string Summary, IReadOnlyList<string> Details);

/// <summary>What the files of the solution say (timestamps and the last static-analysis report); every member is null when it could not be read.</summary>
internal sealed record SolutionFileFacts
{
    public DateTime? NewestSourceUtc { get; init; }
    public string? NewestSource { get; init; }
    public DateTime? NewestCompileInfoUtc { get; init; }
    public DateTime? NewestBootAppUtc { get; init; }
    public string? NewestBootApp { get; init; }
    public string? SarifPath { get; init; }
    public DateTime? SarifWriteUtc { get; init; }
    public string? SarifText { get; init; }
    public string? Problem { get; init; }
}

/// <summary>The places the release check reads besides the session; fakes in the tests.</summary>
internal sealed class ReleaseCheckSources
{
    /// <summary>The XAE's open dialogs (Win32 only, no lock).</summary>
    public Func<IReadOnlyList<XaeDialogView>> Dialogs { get; init; } = XaeDialogInspector.List;

    /// <summary>The timestamps in the directory of the solution file and the last static-analysis report of that solution (the argument is the .sln path).</summary>
    public Func<string, SolutionFileFacts> ScanFiles { get; init; } = SolutionFileScan.Scan;

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// <c>twincat_release_check</c>: the technical findings before a hand-over, in one read-only call. It never builds, saves
/// or changes anything: it reads the Error List, the library and license lists, the files of the solution and the open
/// XAE dialogs. What a read cannot show is <see cref="Light.Unknown"/> with the tool that would show it, never green:
/// "Check all objects" and the static analysis compile (they save the project and clear the Error List), so they are
/// read from what they left behind or reported unknown. Stage 19 of the commissioning process judges and documents;
/// this tool only supplies the single findings.
/// </summary>
internal sealed class ReleaseCheckCoordinator(
    ITwinCatSession session, Func<Func<ReleaseCheckCoordinator.ComFacts>, ReleaseCheckCoordinator.ComFacts>? runLocked = null,
    ReleaseCheckSources? sources = null)
{
    private readonly ReleaseCheckSources _sources = sources ?? new ReleaseCheckSources();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>A source file or a compile result within this many seconds of each other counts as the same moment.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    /// <summary>What the locked reads of the XAE and the target returned; a member is null when its read failed (the error is in <c>Errors</c>).</summary>
    internal sealed class ComFacts
    {
        public string? OpenError { get; set; }
        public string? SolutionPath { get; set; }
        public IReadOnlyList<BuildError>? ErrorList { get; set; }
        public IReadOnlyList<PlcLibraryContext>? Libraries { get; set; }
        public RequiredLicensesResult? RequiredLicenses { get; set; }
        public LicenseInfoResult? LicenseInfo { get; set; }
        public AdsRunStateResult? PlcState { get; set; }
        public Dictionary<string, string> Errors { get; } = [];
    }

    public string Check()
    {
        var dialogs = ReadDialogs(out string? dialogError);
        bool blocked = dialogs?.Any(d => d.Blocking) == true;

        ComFacts com;
        if (blocked)
        {
            com = new ComFacts();
            string reason = $"not read: the XAE is blocked by the dialog '{dialogs!.First(d => d.Blocking).Title}'";
            foreach (string key in new[] { "errors", "libraries", "licenses", "required", "runtime" }) com.Errors[key] = reason;
        }
        else
        {
            com = Locked(ReadCom);
            if (com.OpenError is not null) return com.OpenError;
        }

        string? solutionDir = string.IsNullOrWhiteSpace(com.SolutionPath) ? null : Path.GetDirectoryName(com.SolutionPath);
        SolutionFileFacts files = solutionDir is null
            ? new SolutionFileFacts { Problem = blocked ? "the XAE is blocked by a dialog, so the solution directory is not known" : "the solution directory is not known" }
            : SafeScan(com.SolutionPath!);

        DateTime now = _sources.UtcNow();
        var items = new List<ReleaseItem>
        {
            EvaluateBuild(com, files),
            EvaluateCheckAllObjects(),
            EvaluateStaticAnalysis(files, solutionDir, now),
            EvaluateLibraries(com),
            EvaluateLicenses(com),
            EvaluateBootProject(files),
            EvaluateRuntime(com),
            EvaluateDialogs(dialogs, dialogError),
        };
        return Format(items, com.SolutionPath);
    }

    // ---- the reads ---------------------------------------------------------------------------------------------

    private ComFacts Locked(Func<ComFacts> body) => runLocked is null ? body() : runLocked(body);

    private IReadOnlyList<XaeDialogView>? ReadDialogs(out string? error)
    {
        error = null;
        try { return _sources.Dialogs(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; return null; }
    }

    private SolutionFileFacts SafeScan(string solutionPath)
    {
        try { return _sources.ScanFiles(solutionPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SolutionFileFacts { Problem = ex.Message };
        }
    }

    private ComFacts ReadCom()
    {
        var facts = new ComFacts();
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) { facts.OpenError = error; return facts; }

        try { facts.SolutionPath = session.ActiveSolutionPath(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { facts.Errors["solution"] = ex.Message; }

        Read(facts, "errors", () => facts.ErrorList = session.GetErrorList());
        Read(facts, "libraries", () => facts.Libraries = session.ReadPlcLibraryContexts(null));
        Read(facts, "required", () => facts.RequiredLicenses = session.ListRequiredLicenses());
        // The target's license list is an ADS read that can take its time: only when something is required to check against.
        if (facts.RequiredLicenses is { Licenses.Count: > 0 })
            Read(facts, "licenses", () => facts.LicenseInfo = session.GetLicenseInfo());
        Read(facts, "runtime", () => facts.PlcState = session.GetAdsRunState(AdsTargetNetId.ProjectKeyword, SystemModeSwitch.FirstPlcPort));
        return facts;
    }

    // A failed read is one unknown item, not a failed check. A call held by an XAE dialog is answered [Blocked] by the
    // dispatcher for the whole run, which is no exception in here.
    private static void Read(ComFacts facts, string key, Action read)
    {
        try { read(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            facts.Errors[key] = ComCall.Describe(ex);
        }
    }

    // ---- the items ---------------------------------------------------------------------------------------------

    internal static ReleaseItem EvaluateBuild(ComFacts com, SolutionFileFacts files)
    {
        const string id = "build";
        const string title = "Build state and Error List";
        if (com.ErrorList is null)
            return Item(id, title, Light.Unknown, $"the Error List could not be read ({com.Errors.GetValueOrDefault("errors", "no reason given")})");

        int errors = com.ErrorList.Count(e => e.Level == "error");
        int warnings = com.ErrorList.Count(e => e.Level == "warning");
        var details = com.ErrorList.Where(e => e.Level == "error").Distinct().Take(5)
            .Select(e => $"[ERROR]{(e.File is null ? "" : $" ({e.File}{(e.Line.HasValue ? $":{e.Line}" : "")})")} {e.Description}").ToList();
        string counts = $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")} in the Error List";
        if (errors > 0) return Item(id, title, Light.Red, counts, details);

        // An empty Error List does not say that a build ran after the last change: compare the files.
        if (files.NewestCompileInfoUtc is null)
            return Item(id, title, Light.Unknown,
                $"{counts}, but no compile information (*.compileinfo) was found, so it is not known that a build ran. Build with twincat_build_plc.",
                details);
        if (files.NewestSourceUtc is { } src && src > files.NewestCompileInfoUtc.Value + Tolerance)
            return Item(id, title, Light.Yellow,
                $"{counts}; {files.NewestSource} changed {Utc(src)}, after the last build ({Utc(files.NewestCompileInfoUtc.Value)}). Build again.", details);
        if (files.NewestSourceUtc is null)
            return Item(id, title, Light.Unknown, $"{counts}; no PLC source file was found to compare the last build ({Utc(files.NewestCompileInfoUtc.Value)}) with.", details);
        return Item(id, title, Light.Green, $"{counts}; last build {Utc(files.NewestCompileInfoUtc.Value)} is newer than every PLC source file.", details);
    }

    internal static ReleaseItem EvaluateCheckAllObjects() =>
        Item("check_all_objects", "Check all objects", Light.Unknown,
            "not determined: the check compiles every object, saves the project and clears the Error List, which a read-only call must not do. " +
            "Run twincat_plc_check_all_objects (then twincat_get_error_list).");

    internal static ReleaseItem EvaluateStaticAnalysis(SolutionFileFacts files, string? solutionDir, DateTime now)
    {
        const string id = "static_analysis";
        const string title = "Static analysis (last report)";
        if (files.SarifText is null)
            return Item(id, title, Light.Unknown,
                "no report of an earlier run was found" + (files.SarifPath is null ? "" : $" ({files.SarifPath})") +
                "; the analysis compiles and saves, so it is not run here. Run twincat_plc_static_analysis (it needs a TE1200 license for the full rule set).");

        IReadOnlyList<StaticAnalysisFinding> findings;
        try { findings = SarifReader.Parse(files.SarifText, solutionDir); }
        catch (FormatException ex)
        {
            return Item(id, title, Light.Unknown, $"the report {files.SarifPath} cannot be read: {ex.Message}");
        }

        int errors = findings.Count(f => f.Level == "error");
        int warnings = findings.Count(f => f.Level == "warning");
        int notes = findings.Count(f => f.Level == "note");
        string counts = $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}, {notes} note{(notes == 1 ? "" : "s")}";
        string age = files.SarifWriteUtc is { } w ? $"report of {Utc(w)} ({Math.Max(0, (int)(now - w).TotalHours)} h ago)" : "report of unknown age";
        var details = findings.Where(f => f.Level == "error").Take(5)
            .Select(f => $"[error] {f.RuleId} {f.File}{(f.Line is int l ? $":{l}" : "")} {f.Message}").ToList();

        if (errors > 0) return Item(id, title, Light.Red, $"{counts} in the {age}", details);
        if (files.SarifWriteUtc is null) return Item(id, title, Light.Unknown, $"{counts}, but the age of the report is not known", details);
        if (files.NewestSourceUtc is { } src && src > files.SarifWriteUtc.Value + Tolerance)
            return Item(id, title, Light.Yellow, $"{counts} in the {age}; {files.NewestSource} changed {Utc(src)} after it. Run the analysis again.", details);
        return Item(id, title, warnings > 0 ? Light.Yellow : Light.Green, $"{counts} in the {age}", details);
    }

    internal static ReleaseItem EvaluateLibraries(ComFacts com)
    {
        const string id = "libraries";
        const string title = "Libraries installed and pinned";
        if (com.Libraries is null)
            return Item(id, title, Light.Unknown, $"the library references could not be read ({com.Errors.GetValueOrDefault("libraries", "no reason given")})");
        if (com.Libraries.Count == 0)
            return Item(id, title, Light.Unknown, "the library manager reported no PLC project, so no reference was checked");

        var missing = new List<string>();
        var unpinned = new List<string>();
        int total = 0;
        foreach (var context in com.Libraries)
        {
            foreach (var item in LibraryCoordinator.Classify(context.References, context.Installed))
            {
                total++;
                string label = $"{context.PlcProject}: {item.Reference.Name} {item.Reference.Version}".TrimEnd();
                if (item.Status == AvailabilityStatus.Missing) missing.Add($"{label} missing");
                else if (item.Status == AvailabilityStatus.VersionMismatch) missing.Add($"{label} (installed {item.Match?.Version ?? "-"})");
                else if (item.Reference.IsAnyVersion) unpinned.Add(label);
            }
        }
        var details = missing.Concat(unpinned.Select(u => $"{u} is not pinned (resolves to the newest installed version)")).ToList();
        if (missing.Count > 0)
            return Item(id, title, Light.Red, $"{missing.Count} of {total} reference{(total == 1 ? "" : "s")} not satisfied", details);
        if (unpinned.Count > 0)
            return Item(id, title, Light.Yellow, $"all {total} installed, {unpinned.Count} not pinned to a fixed version ('*')", details);
        return Item(id, title, Light.Green, $"all {total} reference{(total == 1 ? "" : "s")} installed with a fixed version");
    }

    internal static ReleaseItem EvaluateLicenses(ComFacts com)
    {
        const string id = "licenses";
        const string title = "Required licenses";
        if (com.RequiredLicenses is null)
            return Item(id, title, Light.Unknown, $"the required licenses could not be read ({com.Errors.GetValueOrDefault("required", "no reason given")})");
        if (com.RequiredLicenses.Licenses.Count == 0)
            return Item(id, title, Light.Unknown,
                "no license is marked as required in the project, so what the project needs is not known (twincat_add_required_license marks one)");
        if (com.LicenseInfo is null)
            return Item(id, title, Light.Unknown,
                $"{com.RequiredLicenses.Licenses.Count} required, but the licenses of the target could not be read ({com.Errors.GetValueOrDefault("licenses", "no reason given")})");

        var worst = Light.Green;
        var details = new List<string>();
        foreach (var required in com.RequiredLicenses.Licenses)
        {
            string name = required.Description ?? required.OrderNo ?? required.LicenseId;
            var entry = com.LicenseInfo.Licenses.FirstOrDefault(l => SameLicense(l.Id, required.LicenseId));
            Light light;
            string text;
            if (entry is null) { light = Light.Unknown; text = "not in the target's license list"; }
            else switch (entry.Status)
                {
                    case LicenseStatus.Present: light = Light.Green; text = "present"; break;
                    case LicenseStatus.Trial:
                        light = Light.Yellow;
                        text = "trial license" + (entry.ExpiryDate is { } d ? $" until {d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : "");
                        break;
                    case LicenseStatus.Expired: light = Light.Red; text = "expired"; break;
                    case LicenseStatus.Missing: light = Light.Red; text = "missing"; break;
                    default: light = Light.Unknown; text = "status unknown"; break;
                }
            details.Add($"{name}: {text}");
            worst = Worse(worst, light);
        }
        return Item(id, title, worst,
            $"{com.RequiredLicenses.Licenses.Count} required license{(com.RequiredLicenses.Licenses.Count == 1 ? "" : "s")} checked against the target", details);
    }

    internal static ReleaseItem EvaluateBootProject(SolutionFileFacts files)
    {
        const string id = "boot_project";
        const string title = "Boot project";
        const string limit = " Whether the running PLC executes exactly this boot application cannot be read here.";
        if (files.NewestBootAppUtc is null)
            return Item(id, title, files.Problem is null ? Light.Yellow : Light.Unknown,
                files.Problem is null
                    ? "no boot application (*.app) was found in the solution: a download with 'update boot project' or twincat_activate_boot_project writes it."
                    : $"the solution files could not be read ({files.Problem})");
        if (files.NewestCompileInfoUtc is null)
            return Item(id, title, Light.Unknown, $"boot application {Utc(files.NewestBootAppUtc.Value)}, but no compile information to compare it with.{limit}");
        if (files.NewestBootAppUtc.Value + Tolerance < files.NewestCompileInfoUtc.Value)
            return Item(id, title, Light.Yellow,
                $"boot application {Utc(files.NewestBootAppUtc.Value)} is older than the last build {Utc(files.NewestCompileInfoUtc.Value)}: activate or download again.{limit}");
        return Item(id, title, Light.Green, $"boot application {Utc(files.NewestBootAppUtc.Value)} is not older than the last build {Utc(files.NewestCompileInfoUtc.Value)}.{limit}");
    }

    internal static ReleaseItem EvaluateRuntime(ComFacts com)
    {
        const string id = "target_runtime";
        const string title = "PLC runtime of the target";
        if (com.PlcState is null)
            return Item(id, title, Light.Unknown, $"the state could not be read ({com.Errors.GetValueOrDefault("runtime", "no reason given")})");
        if (!com.PlcState.Success)
            return Item(id, title, Light.Unknown, $"the PLC runtime on port {com.PlcState.AdsPort} of {com.PlcState.AmsNetId} did not answer: {com.PlcState.Message}");
        return string.Equals(com.PlcState.State, "Run", StringComparison.OrdinalIgnoreCase)
            ? Item(id, title, Light.Green, $"the PLC runtime on port {com.PlcState.AdsPort} of {com.PlcState.AmsNetId} reads Run")
            : Item(id, title, Light.Yellow, $"the PLC runtime on port {com.PlcState.AdsPort} of {com.PlcState.AmsNetId} reads {com.PlcState.State}");
    }

    internal static ReleaseItem EvaluateDialogs(IReadOnlyList<XaeDialogView>? dialogs, string? error)
    {
        const string id = "xae_dialogs";
        const string title = "Open XAE dialogs";
        if (dialogs is null) return Item(id, title, Light.Unknown, $"the dialogs could not be listed ({error ?? "no reason given"})");
        if (dialogs.Count == 0) return Item(id, title, Light.Green, "no dialog is open");
        var details = dialogs.Select(d => $"{d.Id} '{d.Title}'{(d.Blocking ? " (blocks the XAE)" : "")}").ToList();
        return dialogs.Any(d => d.Blocking)
            ? Item(id, title, Light.Red, $"{dialogs.Count} dialog{(dialogs.Count == 1 ? "" : "s")} open, one blocks the XAE (twincat_xae_dialogs shows it)", details)
            : Item(id, title, Light.Yellow, $"{dialogs.Count} dialog{(dialogs.Count == 1 ? "" : "s")} open", details);
    }

    // ---- the answer --------------------------------------------------------------------------------------------

    internal static string Format(IReadOnlyList<ReleaseItem> items, string? solutionPath)
    {
        string overall = Overall(items) switch
        {
            Light.Red => "red",
            Light.Unknown => "unknown",
            Light.Yellow => "yellow",
            _ => "green",
        };
        return JsonSerializer.Serialize(new
        {
            success = true,
            overall,
            green = items.Count(i => i.Light == Light.Green),
            yellow = items.Count(i => i.Light == Light.Yellow),
            red = items.Count(i => i.Light == Light.Red),
            unknown = items.Count(i => i.Light == Light.Unknown),
            solution = solutionPath,
            items = items.Select(i => new
            {
                id = i.Id,
                title = i.Title,
                status = Name(i.Light),
                summary = i.Summary,
                details = i.Details,
            }).ToArray(),
        }, JsonOpts);
    }

    /// <summary>The worst of the lights: red, then unknown (never green), then yellow.</summary>
    internal static Light Overall(IEnumerable<ReleaseItem> items) =>
        items.Aggregate(Light.Green, (worst, i) => Worse(worst, i.Light));

    private static Light Worse(Light a, Light b) => Rank(b) > Rank(a) ? b : a;

    private static int Rank(Light l) => l switch { Light.Red => 3, Light.Unknown => 2, Light.Yellow => 1, _ => 0 };

    internal static string Name(Light l) => l switch
    {
        Light.Green => "green",
        Light.Yellow => "yellow",
        Light.Red => "red",
        _ => "unknown",
    };

    private static ReleaseItem Item(string id, string title, Light light, string summary, IReadOnlyList<string>? details = null) =>
        new(id, title, light, summary, details ?? []);

    private static string Utc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // A license id with or without braces, in any case.
    private static bool SameLicense(string? a, string? b) =>
        string.Equals(a?.Trim('{', '}', ' '), b?.Trim('{', '}', ' '), StringComparison.OrdinalIgnoreCase);
}

/// <summary>The production <see cref="ReleaseCheckSources.ScanFiles"/>: timestamps of the PLC sources and build products of a solution directory.</summary>
internal static class SolutionFileScan
{
    private static readonly string[] SourcePatterns = ["*.TcPOU", "*.TcGVL", "*.TcDUT", "*.TcIO", "*.plcproj"];
    private static readonly string[] SkippedDirectories = ["_Boot", "_CompileInfo", "_CompileInfo_Upload", "_Libraries", ".git", "bin", "obj"];

    public static SolutionFileFacts Scan(string solutionPath)
    {
        string solutionDirectory = Path.GetDirectoryName(solutionPath) ?? solutionPath;
        if (!Directory.Exists(solutionDirectory))
            return new SolutionFileFacts { Problem = $"{solutionDirectory} does not exist" };

        (string? Path, DateTime? Utc) source = (null, null), compileInfo = (null, null), bootApp = (null, null);
        foreach (string pattern in SourcePatterns)
            source = Newest(Walk(solutionDirectory, pattern, includeBuildDirectories: false), source);
        compileInfo = Newest(Walk(solutionDirectory, "*.compileinfo", includeBuildDirectories: true), compileInfo);
        bootApp = Newest(Walk(solutionDirectory, "*.app", includeBuildDirectories: true), bootApp);

        string name = Path.GetFileNameWithoutExtension(solutionPath);
        string sarif = Path.Combine(StaticAnalysisCoordinator.DefaultDirectory, $"{name}.static-analysis.sarif.json");
        string? sarifText = null;
        DateTime? sarifWrite = null;
        if (File.Exists(sarif))
        {
            sarifWrite = File.GetLastWriteTimeUtc(sarif);
            sarifText = File.ReadAllText(sarif);
        }

        return new SolutionFileFacts
        {
            NewestSource = source.Path, NewestSourceUtc = source.Utc,
            NewestCompileInfoUtc = compileInfo.Utc,
            NewestBootApp = bootApp.Path, NewestBootAppUtc = bootApp.Utc,
            SarifPath = sarif, SarifWriteUtc = sarifWrite, SarifText = sarifText,
        };
    }

    private static (string? Path, DateTime? Utc) Newest(IEnumerable<FileInfo> files, (string? Path, DateTime? Utc) best)
    {
        foreach (var f in files)
            if (best.Utc is null || f.LastWriteTimeUtc > best.Utc) best = (f.FullName, f.LastWriteTimeUtc);
        return best;
    }

    // A manual walk so that build directories and libraries are skipped instead of read through.
    private static IEnumerable<FileInfo> Walk(string root, string pattern, bool includeBuildDirectories)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> subdirectories = [];
            try
            {
                files = Directory.EnumerateFiles(dir, pattern).ToList();
                subdirectories = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an unreadable directory is skipped */ }

            foreach (string f in files) yield return new FileInfo(f);
            foreach (string sub in subdirectories)
            {
                string leaf = Path.GetFileName(sub);
                bool build = leaf is "_Boot" or "_CompileInfo" or "_CompileInfo_Upload";
                bool skipped = SkippedDirectories.Contains(leaf, StringComparer.OrdinalIgnoreCase);
                if (skipped && !(includeBuildDirectories && build)) continue;
                pending.Push(sub);
            }
        }
    }
}
