using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-authoring: see what an HMI project offers, check its references, edit localized texts, place user
// controls and scaffold a page for a symbol. Reads come from the project files on disk (as saved); every
// write goes through the engineer popup, SetSource and a read-back like the hmi-views writes.
internal sealed partial class HmiCoordinator
{
    internal const int MaxScaffoldDepth = 6;

    // ---- list_components ---------------------------------------------------------

    public (string Summary, string Json) ListComponents(string? hmiProject)
    {
        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);

        string summary = $"HMI project '{project!.Name}': {index.Pages.Count(p => p.Kind == "view")} view(s), " +
                         $"{index.Pages.Count(p => p.Kind == "content")} content(s), {index.UserControls.Count} user control(s), " +
                         $"{index.Functions.Count} function(s), languages {(index.Languages.Count == 0 ? "none" : string.Join("/", index.Languages.Select(l => l.Locale)))}" +
                         $", startup view {index.StartupView ?? "(none)"}." +
                         (index.Problems.Count > 0 ? $" {index.Problems.Count} problem(s) reading the files." : "");
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, source = "disk (saved files)", index.StartupView, index.ActiveTheme, index.Themes,
            index.Languages, index.UserControls, index.Functions, index.Pages, index.Problems,
        }, JsonOpts));
    }

    // ---- check_bindings ----------------------------------------------------------

    public (string Summary, string Json) CheckBindings(string? hmiProject, IReadOnlyList<string>? paths, bool includeInfo)
    {
        List<string>? only = null;
        if (paths is { Count: > 0 })
        {
            only = [];
            foreach (string raw in paths)
            {
                string? p = HmiPaths.Normalize(raw);
                if (p is null || HmiComponents.PageKind(p) is null)
                    return ($"[Error] '{raw}' is not a page path (.view, .content or .usercontrol, project-relative with '/').", "{}");
                only.Add(p);
            }
        }

        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);
        if (only is not null && only.FirstOrDefault(p => !index.Pages.Any(x => x.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) is { } unknown)
            return ($"[Error] '{unknown}' is not a page of '{project!.Name}' on disk.", "{}");

        var sources = SymbolSources(project!);
        var report = HmiBindingCheck.Check(snap, index, sources, only);
        var shown = report.Findings.Where(f => includeInfo || f.Severity != "info").ToList();
        int errors = report.Findings.Where(f => f.Severity == "error").Sum(f => f.Count);
        int warnings = report.Findings.Where(f => f.Severity == "warning").Sum(f => f.Count);
        int infos = report.Findings.Where(f => f.Severity == "info").Sum(f => f.Count);

        string summary = $"Checked {report.BindingsChecked} binding(s) in {report.FilesChecked} page(s) of '{project!.Name}': " +
                         (errors + warnings == 0 ? "no errors, no warnings" : $"{errors} error(s), {warnings} warning(s)") +
                         (infos > 0 ? $", {infos} info" : "") + $". Server symbols from {report.ServerSource}.";
        if (errors > 0)
            summary += " First: " + string.Join("; ", report.Findings.Where(f => f.Severity == "error").Take(3)
                .Select(f => $"{f.File}:{(f.Lines is { } l ? string.Join(",", l) : f.Line.ToString(System.Globalization.CultureInfo.InvariantCulture))} {f.Message}"));
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, source = "disk (saved files)", report.FilesChecked, report.BindingsChecked,
            report.BindingsByKind, errors, warnings, infos, report.ServerSource, findings = shown,
        }, JsonOpts));
    }

    private HmiSymbolSources SymbolSources(HmiProjectEntry project)
    {
        HashSet<string>? server = null;
        string source;
        try
        {
            var mapped = Session.ListHmiMappedSymbols(project.Name, false);
            server = new HashSet<string>(mapped.Select(m => m.MappedName), StringComparer.Ordinal);
            source = $"the engineering server ({mapped.Count} mapped)";
        }
        catch (Exception ex) when (IsHmiFailure(ex))
        {
            source = $"nowhere — the engineering server did not answer ({ex.Message})";
        }
        var (serverSymbols, _, _) = HmiInternalSymbols.ReadServerSymbols(project.ProjectDirectory);
        if (server is not null && serverSymbols is { Count: > 0 })
        {
            foreach (var s in serverSymbols) server.Add(s.Name);
            source = source[..^1] + $", {serverSymbols.Count} server-config symbol(s))";
        }

        var domains = new HashSet<string>(StringComparer.Ordinal) { "ADS", "TcHmiSrv" };
        try
        {
            foreach (var e in Session.GetHmiProjectInfo(project.Name, false, 1).ServerExtensions) domains.Add(e.DomainName);
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { /* defaults above */ }

        var (client, _, _) = HmiInternalSymbols.ReadClientSymbols(project.ProjectDirectory);
        var internals = new HashSet<string>((client ?? []).Select(c => c.Name), StringComparer.Ordinal);
        return new HmiSymbolSources(server, source, domains, internals);
    }

    // ---- localization ------------------------------------------------------------

    public (string Summary, string Json) ListLocalization(string? hmiProject)
    {
        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);
        if (index.Languages.Count == 0)
            return ($"HMI project '{project!.Name}' has no languages in {HmiComponents.ConfigPath}.", "{}");
        var report = HmiLocalization.Analyze(snap, index);
        int missing = report.MissingByLanguage.Values.Sum(v => v.Count);
        string summary = $"'{project!.Name}': {index.Languages.Count} language(s) ({string.Join(", ", index.Languages.Select(l => l.Locale))}), " +
                         $"{report.AllKeys.Count} key(s); {missing} missing translation(s), {report.UsedButUndefined.Count} key(s) used but not defined, " +
                         $"{report.DefinedButUnused.Count} defined but unused.";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, report.Languages, report.AllKeys, report.MissingByLanguage, report.UsedButUndefined,
            report.DefinedButUnused,
        }, JsonOpts));
    }

    public (string Summary, string Json) SetLocalizedTexts(string? hmiProject, string? textsJson, int confirmTimeoutSeconds)
    {
        var (changes, parseError) = HmiLocalization.ParseChanges(textsJson);
        if (changes is null) return ("[Error] " + parseError, "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);
        foreach (string locale in changes.Keys)
            if (!index.Languages.Any(l => l.Locale.Equals(locale, StringComparison.Ordinal)))
                return ($"[Error] '{locale}' is not a language of '{project!.Name}' (languages: " +
                        $"{(index.Languages.Count == 0 ? "none" : string.Join(", ", index.Languages.Select(l => l.Locale)))}). " +
                        "Nothing was written.", "{}");

        // plan every file before asking: read the current source through the engineering, apply, diff
        var plan = new List<(HmiLanguageFile Lang, HmiFileContent Current, HmiLocalizationEdit Edit)>();
        try
        {
            foreach (var (locale, keys) in changes)
            {
                var lang = index.Languages.First(l => l.Locale.Equals(locale, StringComparison.Ordinal));
                var current = Session.ReadHmiFile(project!.Name, lang.Path);
                var (edit, editError) = HmiLocalization.Apply(current.Source, keys);
                if (edit is null) return ($"[Error] '{lang.Path}': {editError}. Nothing was written.", "{}");
                if (edit.Added.Count + edit.Changed.Count + edit.Removed.Count == 0) continue;
                if (Session.IsHmiFileOpen(project.Name, lang.Path)) return (OpenInEditorError(lang.Path) + " Nothing was written.", "{}");
                plan.Add((lang, current, edit));
            }
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        if (plan.Count == 0)
            return ("Nothing to do: every text already has this value.",
                JsonSerializer.Serialize(new { hmiProject = project!.Name, changed = false }, JsonOpts));

        string listing = string.Join(Environment.NewLine, plan.Select(p =>
            $"  {p.Lang.Locale}: +{p.Edit.Added.Count} ~{p.Edit.Changed.Count} −{p.Edit.Removed.Count}  ({p.Lang.Path})"));
        var request = new HmiWriteRequest(HmiWriteKind.WriteFile, project!.Name, project.ProjectDirectory,
            $"localized texts ({plan.Count} language file(s))", null, NewValue: Environment.NewLine + listing,
            CurrentValue: string.Join(", ", plan.Select(p => p.Lang.Path)), WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        var written = new List<string>();
        foreach (var (lang, _, edit) in plan)
        {
            string? writeError = WriteAndVerify(project.Name, lang.Path, edit.Text);
            if (writeError is not null)
                return (writeError + (written.Count > 0 ? $" Already written and verified: {string.Join(", ", written)}." : " Nothing was written."), "{}");
            written.Add(lang.Path);
        }
        string summary = $"Updated localized texts of '{project.Name}' in {written.Count} file(s) — {Describe(outcome)}, verified by read-back: " +
                         string.Join("; ", plan.Select(p => $"{p.Lang.Locale} +{p.Edit.Added.Count} ~{p.Edit.Changed.Count} −{p.Edit.Removed.Count}")) + ".";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            files = plan.Select(p => new
            {
                locale = p.Lang.Locale, path = p.Lang.Path, added = p.Edit.Added, changed = p.Edit.Changed,
                removed = p.Edit.Removed, notPresent = p.Edit.AlreadyAbsent,
            }),
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts));
    }

    // ---- place_user_control ------------------------------------------------------

    public (string Summary, string Json) PlaceUserControl(string? hmiProject, string? page, string? userControl, string? id,
        int left, int top, int? width, int? height, string? widthUnit, string? heightUnit, string? parametersJson, bool replace,
        int confirmTimeoutSeconds)
    {
        string? pagePath = HmiPaths.Normalize(page);
        if (pagePath is null || HmiComponents.PageKind(pagePath) is not ("view" or "content" or "usercontrol"))
            return ("[Error] page must be a project-relative .view, .content or .usercontrol path, e.g. 'Desktop.view'.", "{}");
        string? ucPath = HmiPaths.Normalize(userControl);
        if (ucPath is null || HmiComponents.PageKind(ucPath) != "usercontrol")
            return ("[Error] userControl must be the project-relative path of a .usercontrol, e.g. 'UserControls/X/X.usercontrol'.", "{}");
        if (ucPath.Equals(pagePath, StringComparison.OrdinalIgnoreCase))
            return ("[Error] a user control cannot host itself.", "{}");
        string hostId = (id ?? "").Trim();
        if (!HmiPlacement.IsValidId(hostId))
            return ("[Error] id must be a control id: letters, digits, '_' and '-', not starting with a digit.", "{}");
        var (values, paramError) = HmiPlacement.ParseParameters(parametersJson);
        if (values is null) return ("[Error] " + paramError, "{}");
        if (left < 0 || top < 0) return ("[Error] left and top must not be negative.", "{}");
        var layout = new HmiLayout(left, top, width, height, (widthUnit ?? "px").Trim(), (heightUnit ?? "px").Trim());
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        var (project, snap, error) = LoadSnapshot(hmiProject);
        if (snap is null) return (error!, "{}");
        var index = HmiComponents.Build(snap);
        HmiUserControlInfo? uc = index.UserControl(ucPath);
        if (uc is null)
            return ($"[Error] '{ucPath}' is not a user control of '{project!.Name}' on disk" +
                    HmiBindingCheck.Near(ucPath, index.UserControls.Select(u => u.Path)) + ".", "{}");

        HmiFileContent current;
        try
        {
            current = Session.ReadHmiFile(project!.Name, pagePath);
            if (Session.IsHmiFileOpen(project.Name, pagePath)) return (OpenInEditorError(pagePath), "{}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        var (placed, placeError) = HmiPlacement.Place(current.Source, uc, hostId, layout, values, replace);
        if (placed is null) return ($"[Error] {placeError} Nothing was written.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.WriteFile, project.Name, project.ProjectDirectory, pagePath, null,
            NewValue: $"{(placed.Replaced ? "replace" : "add")} user-control host '{hostId}' → {ucPath} " +
                      $"({placed.Attributes.Count} parameter(s){(placed.Attributes.Count > 0 ? ": " + string.Join(", ", placed.Attributes) : "")})",
            CurrentValue: $"{current.Source.Length} characters", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        string? writeError = WriteAndVerify(project.Name, pagePath, placed.Text);
        if (writeError is not null) return (writeError, "{}");
        string summary = $"{(placed.Replaced ? "Replaced" : "Placed")} '{hostId}' ({ucPath}) in '{pagePath}' line {placed.Line} — " +
                         $"{Describe(outcome)}, verified by read-back. Run twincat_hmi_check_bindings, then twincat_hmi_build.";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, page = pagePath, userControl = ucPath, id = hostId, replaced = placed.Replaced,
            line = placed.Line, parameters = placed.Attributes, markup = placed.Markup, confirmation = ConfirmationWire(outcome),
        }, JsonOpts));
    }

    // ---- scaffold_symbol_view ----------------------------------------------------

    public (string Summary, string Json) ScaffoldSymbolView(string? hmiProject, string? symbol, string? path, bool readOnly,
        int maxDepth, string? sampleValueJson, bool dryRun, bool overwrite, int confirmTimeoutSeconds)
    {
        string sym = (symbol ?? "").Trim();
        if (sym.StartsWith("%s%", StringComparison.Ordinal) && sym.EndsWith("%/s%", StringComparison.Ordinal)) sym = sym[3..^4];
        if (sym.Length == 0 || sym.Contains('%') || sym.Contains('|'))
            return ("[Error] symbol must be a server symbol name, e.g. 'PLC1.MAIN.stAxis' (without %s% and options).", "{}");
        string? target = HmiPaths.Normalize(path);
        if (target is null || HmiComponents.PageKind(target) != "content")
            return ("[Error] path must be a project-relative .content path, e.g. 'Contents/Axis.content'.", "{}");
        if (maxDepth < 1 || maxDepth > MaxScaffoldDepth) return ($"[Error] maxDepth must be between 1 and {MaxScaffoldDepth}.", "{}");
        if (!dryRun && TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        JsonElement value;
        string valueSource;
        if (sampleValueJson is not null)
        {
            try { using var doc = JsonDocument.Parse(sampleValueJson); value = doc.RootElement.Clone(); }
            catch (JsonException ex) { return ($"[Error] sampleValue is not valid JSON: {ex.Message}", "{}"); }
            valueSource = "sampleValue";
        }
        else
        {
            string? guard = SessionGuards.RequireOpenSolution(Session);
            if (guard is not null) return (guard, "{}");
            var (p, resolveError) = ResolveProject(hmiProject);
            if (p is null) return (resolveError!, "{}");
            HmiSymbolValue read;
            try { read = Session.ReadHmiSymbols(p.Name, [sym]).Single(); }
            catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
            if (!read.Ok || HmiScaffold.ParseValue(read.Value) is not { } v)
                return ($"[Error] '{sym}' could not be read through the HMI server ({read.Error ?? "no value"}). Map it first, or pass " +
                        "sampleValue (a JSON value of the same shape) to scaffold without a server.", "{}");
            value = v;
            valueSource = "the HMI server";
        }

        var built = HmiScaffold.Build(sym, value, HmiScaffold.ContentId(target), readOnly, maxDepth);
        if (built.Rows == 0)
            return ($"[Error] nothing to scaffold for '{sym}': {string.Join("; ", built.Skipped.DefaultIfEmpty("no scalar fields"))}.", "{}");
        string shape = $"{built.Rows} row(s), {built.Width}×{built.Height} px, value shape from {valueSource}" +
                       (built.Skipped.Count > 0 ? $"; skipped: {string.Join("; ", built.Skipped.Take(5))}" : "");
        if (dryRun)
            return ($"Scaffold for '{sym}' (dry run, nothing written): {shape}.",
                JsonSerializer.Serialize(new { symbol = sym, path = target, built.Rows, built.Width, built.Height, built.Skipped, built.Bindings, source = built.Text }, JsonOpts));

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, projectError) = ResolveProject(hmiProject);
        if (project is null) return (projectError!, "{}");

        bool exists;
        try
        {
            exists = Session.HmiItemExists(project.Name, target);
            if (exists && !overwrite)
                return ($"[Error] '{target}' already exists — pass overwrite=true to replace it, or choose another path. Nothing was written.", "{}");
            if (exists && Session.IsHmiFileOpen(project.Name, target)) return (OpenInEditorError(target), "{}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        var kind = HmiItemKinds.Find("content")!;
        var (folder, fileName) = HmiPaths.Split(target);
        if (!exists && OrphanOnDisk(project, [target]) is { } orphan) return (orphan, "{}");

        var request = new HmiWriteRequest(exists ? HmiWriteKind.WriteFile : HmiWriteKind.AddItem, project.Name, project.ProjectDirectory,
            target, null, NewValue: $"scaffold for {sym}: {shape}", CurrentValue: exists ? "existing file (replaced)" : "(does not exist)",
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        if (!exists)
        {
            try
            {
                if (kind.ViaImport) CreateViaImport(project.Name, folder, fileName, built.Text);
                else Session.AddHmiItem(project.Name, folder, Path.GetFileNameWithoutExtension(fileName), kind.TemplateKey);
            }
            catch (Exception ex) when (IsHmiFailure(ex) || ex is IOException) { return (WithDialogHint($"[Error] creating '{target}' failed: {ex.Message}"), "{}"); }
        }
        string? writeError = WriteAndVerify(project.Name, target, built.Text);
        if (writeError is not null) return (writeError, "{}");
        return ($"Scaffolded '{target}' for '{sym}' in '{project.Name}': {shape} — {Describe(outcome)}, verified by read-back. " +
                "Show it in a region or view, then twincat_hmi_build and twincat_hmi_check_liveview.",
            JsonSerializer.Serialize(new
            {
                hmiProject = project.Name, symbol = sym, path = target, created = !exists, built.Rows, built.Width, built.Height,
                built.Skipped, built.Bindings, confirmation = ConfirmationWire(outcome),
            }, JsonOpts));
    }

    // ---- check_liveview: where the live view is -----------------------------------

    /// <summary>
    /// The live-view URL of an HMI project: the engineering server listens on the .hmiproj property
    /// HmiCommunicationServerPort (live 2026-11-18: TcHmiSrv --endpoint=http://127.0.0.1:3000 for 3000), and
    /// twincat_hmi_build writes liveview_&lt;hash&gt;.html into the project directory, which that server serves.
    /// </summary>
    public (string? Url, string? Error) LiveViewUrl(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (null, error);
        var (project, resolveError) = ResolveProject(hmiProject, requireReady: false);
        if (project is null) return (null, resolveError);
        return LiveViewUrlIn(project.ProjectDirectory);
    }

    internal static (string? Url, string? Error) LiveViewUrlIn(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory)) return (null, $"[Error] project directory not found: {projectDirectory}");
        string? hmiproj = Directory.GetFiles(projectDirectory, "*.hmiproj").FirstOrDefault();
        if (hmiproj is null) return (null, $"[Error] no .hmiproj in {projectDirectory}.");
        int? port = ServerPort(File.ReadAllText(hmiproj));
        if (port is null) return (null, $"[Error] {Path.GetFileName(hmiproj)} has no HmiCommunicationServerPort — pass url.");
        var liveview = new DirectoryInfo(projectDirectory).GetFiles("liveview_*.html")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (liveview is null)
            return (null, "[Error] the project has no live view yet (no liveview_*.html) — run twincat_hmi_build first.");
        return ($"http://127.0.0.1:{port}/{liveview.Name}", null);
    }

    internal static int? ServerPort(string hmiprojXml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(hmiprojXml, @"<HmiCommunicationServerPort>\s*(\d{1,5})\s*</HmiCommunicationServerPort>");
        return m.Success && int.TryParse(m.Groups[1].Value, out int p) && p is > 0 and < 65536 ? p : null;
    }

    // ---- helpers -----------------------------------------------------------------

    private (HmiProjectEntry? Project, HmiProjectSnapshot? Snapshot, string? Error) LoadSnapshot(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (null, null, error);
        var (project, resolveError) = ResolveProject(hmiProject, requireReady: false);
        if (project is null) return (null, null, resolveError);
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory) || !Directory.Exists(project.ProjectDirectory))
            return (project, null, $"[Error] the directory of HMI project '{project.Name}' was not found ({project.ProjectDirectory}).");
        return (project, HmiComponents.Load(project.ProjectDirectory), null);
    }
}
