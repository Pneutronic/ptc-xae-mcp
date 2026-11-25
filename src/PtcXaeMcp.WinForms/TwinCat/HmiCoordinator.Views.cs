using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-views: create and change HMI project files. Every write
// goes through the same engineer popup as the hmi-server writes ("frage jeweils"); input
// is validated completely before anything is asked or written.
internal sealed partial class HmiCoordinator
{
    internal const int MaxSourceBytes = 5 * 1024 * 1024;

    public (string Summary, string Json) ReadFile(string? hmiProject, string? path)
    {
        string? p = HmiPaths.Normalize(path);
        if (p is null) return (PathError(path), "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiFileContent file;
        try { file = Session.ReadHmiFile(project.Name, p); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        int lines = file.Source.Length == 0 ? 0 : file.Source.Split('\n').Length;
        string summary = $"'{p}' in '{project.Name}': {file.Source.Length} characters, {lines} lines.";
        return (summary, JsonSerializer.Serialize(
            new { hmiProject = project.Name, path = p, length = file.Source.Length, lines, source = file.Source }, JsonOpts));
    }

    public (string Summary, string Json) AddItem(string? hmiProject, string? folder, string? name, string? kind,
        int confirmTimeoutSeconds)
    {
        var k = HmiItemKinds.Find(kind);
        if (k is null) return ($"[Error] kind must be one of: {HmiItemKinds.Names}.", "{}");
        string? dir = HmiPaths.Normalize(folder, allowEmpty: true);
        if (dir is null) return (PathError(folder), "{}");
        string itemName = (name ?? "").Trim();
        if (itemName.Length == 0 || HmiPaths.Normalize(itemName) is not { } n || n.Contains('/'))
            return ("[Error] name must be a plain file name without folder, e.g. 'PtcCamCurve'.", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (main, dependents) = HmiItemKinds.Paths(k, dir, itemName);
        try
        {
            foreach (string target in dependents.Prepend(main))
                if (Session.HmiItemExists(project.Name, target))
                    return ($"[Error] '{target}' already exists in '{project.Name}' — use twincat_hmi_write_file to change it.", "{}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (OrphanOnDisk(project, dependents.Prepend(main)) is { } orphan) return (orphan, "{}");

        string origin = !k.ViaImport ? $"new from TE2000 template {k.TemplateKey}"
            : k.Name == "view" ? "new from the TE2000 view skeleton (via import)" : "new, empty (via import)";
        var request = new HmiWriteRequest(HmiWriteKind.AddItem, project.Name, project.ProjectDirectory, main, null,
            NewValue: origin + (dependents.Count > 0 ? $" (+ {string.Join(", ", dependents)})" : ""),
            CurrentValue: "(does not exist)", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        IReadOnlyList<string> created;
        string baseName = Path.GetFileNameWithoutExtension(main);
        try
        {
            created = k.ViaImport
                ? CreateViaImport(project.Name, dir, Path.GetFileName(main), HmiItemKinds.Skeleton(k, baseName))
                : Session.AddHmiItem(project.Name, dir, baseName, k.TemplateKey);
        }
        catch (Exception ex) when (IsHmiFailure(ex) || ex is IOException) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        string how = !k.ViaImport ? $"from template {k.TemplateKey}"
            : k.Name == "view" ? "from the vendor view skeleton (imported)" : "as an empty file (imported)";
        string summary = $"Created '{main}' {how} in '{project.Name}'" +
                         (created.Count > 1 ? $" (+ {string.Join(", ", created.Skip(1))})" : "") + $" — {Describe(outcome)}.";
        return (summary, JsonSerializer.Serialize(
            new { hmiProject = project.Name, kind = k.Name, created, confirmation = ConfirmationWire(outcome) }, JsonOpts));
    }

    internal const string TcHmiConfigPath = "Properties/tchmiconfig.json";

    public (string Summary, string Json) WriteFile(string? hmiProject, string? path, string? source, string? sourceFile,
        bool keepSchema, int confirmTimeoutSeconds)
    {
        string? p = HmiPaths.Normalize(path);
        if (p is null) return (PathError(path), "{}");
        // Issue #248: TE2000 rewrites tchmiconfig.json from its in-memory model on every config save
        // (TcHmiConfig.Save → GetJson) and never reloads it after SetSource, so a write here is lost later.
        if (p.Equals(TcHmiConfigPath, StringComparison.OrdinalIgnoreCase))
            return ($"[Error] '{TcHmiConfigPath}' is owned by the HMI engineering's in-memory model: TE2000 rewrites it " +
                    "from that model on its next config save and never reloads it from disk, so a write_file change " +
                    "would be lost. Use the model-aware tools instead (internal symbols: twincat_hmi_add_internal_symbol " +
                    "/ twincat_hmi_remove_internal_symbol).", "{}");
        var (text, sourceError) = LoadSource(source, sourceFile);
        if (text is null) return (sourceError!, "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiFileContent current;
        try { current = Session.ReadHmiFile(project.Name, p); }
        catch (Exception ex) when (IsHmiFailure(ex))
        {
            return (WithDialogHint($"[Error] {ex.Message} Create it first with twincat_hmi_add_item."), "{}");
        }

        // Line endings as the file has them on disk (what git and the editor see); the engine's text otherwise.
        string onDisk = DiskText(project, p) ?? current.Source;
        string target = HmiText.MatchLineEndings(onDisk, keepSchema ? HmiText.KeepSchema(current.Source, text) : text);
        if (HmiText.Unchanged(onDisk, target) && HmiText.Same(current.Source, target))
            return ($"Nothing to do: '{p}' already has this content.",
                JsonSerializer.Serialize(new { hmiProject = project.Name, path = p, changed = false }, JsonOpts));
        try { if (Session.IsHmiFileOpen(project.Name, p)) return (OpenInEditorError(p), "{}"); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        var (added, removed) = HmiText.LineDiff(current.Source, target);
        var request = new HmiWriteRequest(HmiWriteKind.WriteFile, project.Name, project.ProjectDirectory, p, null,
            NewValue: $"{target.Length} characters (+{added} / −{removed} lines)",
            CurrentValue: $"{current.Source.Length} characters", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        string? writeError = WriteAndVerify(project.Name, p, target);
        if (writeError is not null) return (writeError, "{}");

        string summary = $"Wrote '{p}' in '{project.Name}' ({target.Length} characters, +{added}/−{removed} lines) — " +
                         $"{Describe(outcome)}, verified by read-back.";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, path = p, changed = true, length = target.Length, linesAdded = added,
            linesRemoved = removed, schemaKept = keepSchema && !ReferenceEquals(target, text), confirmation = ConfirmationWire(outcome),
        }, JsonOpts));
    }

    public (string Summary, string Json) ImportFile(string? hmiProject, string? sourcePath, string? folder, string? name,
        int confirmTimeoutSeconds)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath.Trim()))
            return ("[Error] sourcePath must be an absolute path to an existing file.", "{}");
        string src = sourcePath.Trim();
        if (!File.Exists(src)) return ($"[Error] sourcePath not found: {src}", "{}");
        string? dir = HmiPaths.Normalize(folder, allowEmpty: true);
        if (dir is null) return (PathError(folder), "{}");
        string fileName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(src) : name.Trim();
        if (HmiPaths.Normalize(fileName) is not { } fn || fn.Contains('/'))
            return ("[Error] name must be a plain file name without folder.", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        string target = dir.Length == 0 ? fileName : dir + "/" + fileName;
        try
        {
            if (Session.HmiItemExists(project.Name, target))
                return ($"[Error] '{target}' already exists in '{project.Name}'.", "{}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (OrphanOnDisk(project, [target]) is { } orphan) return (orphan, "{}");

        long size = new FileInfo(src).Length;
        var request = new HmiWriteRequest(HmiWriteKind.ImportFile, project.Name, project.ProjectDirectory, target, null,
            NewValue: $"copy of {src} ({size} bytes)", CurrentValue: "(does not exist)", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiImportResult import;
        try { import = Session.ImportHmiFile(project.Name, dir, fileName, src); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        string imported = import.Created.Count > 0 ? import.Created[0] : target;

        // #247: TE2000 raised an error although the item is in the project. Believe the file, not the
        // message — but only when the copy on disk is exactly the source.
        bool? identical = SameOnDisk(project, imported, src);
        string engineNote = "";
        if (import.EngineError is { } engineError)
        {
            if (identical != true)
                return ($"[Error] '{imported}' is in the project, but " +
                        (identical is null ? "its file was not found on disk" : "the file on disk differs from the source") +
                        $" — TE2000 reported: {engineError}", "{}");
            engineNote = $" TE2000 reported \"{engineError}\" after adding it (known for .js, #247) — verified: in the project and byte-identical on disk.";
        }

        // A .js/.css reaches the client only when dependencyFiles lists it. Report what tchmiconfig.json
        // on disk says now (the session made TE2000 rewrite it for a .js); the live view bakes the list in
        // at the next twincat_hmi_build.
        string? dependencyType = null;
        string dependencyNote = "";
        if (IsDependencyCandidate(imported))
        {
            // SaveResync may write tchmiconfig.json asynchronously (see ReadBackDelays) — re-read a few times.
            foreach (TimeSpan delay in ReadBackDelays.DefaultIfEmpty(TimeSpan.Zero))
            {
                if (delay > TimeSpan.Zero) Thread.Sleep(delay);
                if ((dependencyType = DependencyFileType(project, imported)) is not null) break;
            }
            dependencyNote = dependencyType is null
                ? $" Not in dependencyFiles of {TcHmiConfigPath} — the client will not load it until TE2000 lists it there."
                : $" Listed in dependencyFiles as {dependencyType}; run twincat_hmi_build so the live view loads it.";
        }

        return ($"Imported {src} as '{imported}' into '{project.Name}' ({size} bytes) — {Describe(outcome)}.{engineNote}{dependencyNote}",
            JsonSerializer.Serialize(new
            {
                hmiProject = project.Name, created = import.Created, bytes = size, engineError = import.EngineError,
                verifiedOnDisk = identical, dependencyType, confirmation = ConfirmationWire(outcome),
            }, JsonOpts));
    }

    /// <summary>True/false when the imported file is below the project directory on disk (byte comparison
    /// with the source); null when it is not there or cannot be read.</summary>
    /// <summary>The file's text as it is on disk (read only, the mtime stays), or null when it cannot be read.</summary>
    private static string? DiskText(HmiProjectEntry project, string path)
    {
        string file = Path.Combine(project.ProjectDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        try { return File.Exists(file) ? File.ReadAllText(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool? SameOnDisk(HmiProjectEntry project, string path, string sourcePath)
    {
        string file = Path.Combine(project.ProjectDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        try { return File.Exists(file) ? File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(sourcePath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsDependencyCandidate(string path) =>
        path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);

    /// <summary>The "type" of <paramref name="path"/> in dependencyFiles of tchmiconfig.json on disk
    /// (JavaScript, EsModule, Stylesheet); null when not listed or unreadable. TE2000 regenerates that
    /// list from the project's .js/.css whenever it writes the file (TcHmiConfig.GetJObject, decompiled).</summary>
    internal static string? DependencyFileType(HmiProjectEntry project, string path)
    {
        string config = Path.Combine(project.ProjectDirectory, TcHmiConfigPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(config)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("dependencyFiles", out var list) || list.ValueKind != JsonValueKind.Array) return null;
            foreach (JsonElement e in list.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String &&
                    string.Equals(HmiPaths.Normalize(n.GetString()), path, StringComparison.OrdinalIgnoreCase))
                    return e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public (string Summary, string Json) Build(string? hmiProject, string? configuration)
    {
        string config = string.IsNullOrWhiteSpace(configuration) ? "Debug" : configuration.Trim();
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiOperationResult result;
        try { result = Session.BuildHmiProject(project.Name, config); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (!result.Ok)
            return (WithDialogHint($"[Error] Build of '{project.Name}' ({config}) failed: {result.LastError ?? "no error text"}. " +
                                   "See the XAE error list (twincat_get_error_list)."), "{}");
        return ($"Built HMI project '{project.Name}' ({config}).",
            JsonSerializer.Serialize(new { hmiProject = project.Name, configuration = config, ok = true }, JsonOpts));
    }

    // ---- delete_item -------------------------------------------------------------

    // The project cannot run without these; deleting them is never a content change (⚑ V8).
    private static readonly string[] ProtectedTopLevel = ["Server", "Properties", "Packages"];

    /// <summary>
    /// Delete a file or folder (DeleteChild removes it from the project AND the disk). Refused
    /// without a popup: infrastructure folders, the startup view and folders containing it.
    /// Success only when the item is gone afterwards.
    /// </summary>
    public (string Summary, string Json) DeleteItem(string? hmiProject, string? path, int confirmTimeoutSeconds)
    {
        string? p = HmiPaths.Normalize(path);
        if (p is null) return (PathError(path), "{}");
        string top = p.Split('/')[0];
        if (ProtectedTopLevel.Contains(top, StringComparer.OrdinalIgnoreCase))
            return ($"[Error] '{p}' is part of the project infrastructure ('{top}') — refused without asking. Nothing was deleted.", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiItemInfo? item;
        string? startup;
        try
        {
            startup = HmiPaths.Normalize(Session.GetHmiProjectInfo(project.Name, false, 1).Config.GetValueOrDefault("StartupView"));
            item = Session.DescribeHmiItem(project.Name, p);
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        if (startup is not null && startup.Equals(p, StringComparison.OrdinalIgnoreCase))
            return ($"[Error] '{p}' is the project's startup view — refused without asking. Nothing was deleted.", "{}");
        if (startup is not null && startup.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
            return ($"[Error] '{p}' is a folder containing the startup view '{startup}' — refused without asking. Nothing was deleted.", "{}");
        if (item is null) return ($"[Error] '{p}' not found in '{project.Name}'. Nothing was deleted.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.DeleteItem, project.Name, project.ProjectDirectory, p, null,
            NewValue: "(deleted — from the project AND from disk, no recycle bin)",
            CurrentValue: item.IsFile ? "file" : $"folder with {item.Descendants} entries", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = Session.DeleteHmiItem(project.Name, p); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] Deleting '{p}' failed: {ex.Message}"), "{}"); }
        if (!result.Ok) return ($"[Error] DeleteChild('{p}') was refused: {result.LastError ?? "no error text"}.", "{}");

        bool still;
        try { still = Session.HmiItemExists(project.Name, p); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return ($"[Error] '{p}' was deleted, but the check afterwards failed: {ex.Message}", "{}"); }
        if (still) return ($"[Error] DeleteChild('{p}') reported success, but the item still exists — not treated as success.", "{}");

        string what = item.IsFile ? "file" : $"folder with {item.Descendants} item(s)";
        IReadOnlyList<string> stale = StaleConfigEntries(project, p);
        string staleNote = stale.Count == 0 ? "" :
            $"\n[Warning] {TcHmiConfigPath} still lists {string.Join(", ", stale.Select(s => $"'{s}'"))} (an entry with this url): " +
            "the HMI engineering keeps it in its in-memory model and DeleteChild does not remove it. No tool of this server " +
            "can remove it yet (write_file refuses that file); remove it in the XAE.";
        return ($"Deleted '{p}' ({what}) from '{project.Name}' — {Describe(outcome)}, verified: no longer in the project." + staleNote,
            JsonSerializer.Serialize(new
            {
                hmiProject = project.Name, path = p, isFile = item.IsFile, descendants = item.Descendants,
                staleConfigEntries = stale, confirmation = ConfirmationWire(outcome),
            }, JsonOpts));
    }

    /// <summary>
    /// The urls in tchmiconfig.json (e.g. the content list) that still point at the deleted item or into the deleted
    /// folder. Empty when the file cannot be read or parsed — this is a hint, not part of the verification.
    /// </summary>
    private IReadOnlyList<string> StaleConfigEntries(HmiProjectEntry project, string deleted)
    {
        // The file on disk (what the build and the live view take) and the engine's text; either may hold it.
        var urls = new List<string>(HmiText.UrlsPointingAt(DiskText(project, TcHmiConfigPath) ?? "", deleted));
        try { urls.AddRange(HmiText.UrlsPointingAt(Session.ReadHmiFile(project.Name, TcHmiConfigPath).Source, deleted)); }
        catch (Exception ex) when (IsHmiFailure(ex) || ex is IOException) { }
        return urls.Distinct(StringComparer.Ordinal).ToList();
    }

    // ---- apply_bundle ------------------------------------------------------------

    /// <summary>
    /// Create/write every file of a manifest with ONE engineer confirmation. The whole plan
    /// (paths, sources, kinds, which files exist, which a template will create) is checked
    /// before the popup; afterwards the entries run in order and stop at the first error.
    /// </summary>
    public (string Summary, string Json) ApplyBundle(string? hmiProject, string? manifestPath, int confirmTimeoutSeconds)
    {
        var (bundleName, entries, manifestError) = LoadManifest(manifestPath);
        if (entries is null) return (manifestError!, "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        // Plan: write existing files, create missing ones from their template, and accept a
        // missing file without kind only when an earlier entry's template creates it.
        var plan = new List<(HmiBundleEntry Entry, string Action)>();
        var willExist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in entries)
            {
                bool exists = willExist.Contains(e.Path) || Session.HmiItemExists(project.Name, e.Path);
                if (exists && !willExist.Contains(e.Path) && Session.IsHmiFileOpen(project.Name, e.Path))
                    return (OpenInEditorError(e.Path) + " Nothing was written.", "{}");
                if (exists) { plan.Add((e, willExist.Contains(e.Path) ? "write (created by template)" : "write")); continue; }
                if (e.Kind is null)
                    return ($"[Error] '{e.Path}' does not exist and has no kind — add \"kind\" or list the entry " +
                            "whose template creates it first. Nothing was written.", "{}");
                var (folder, fileName) = HmiPaths.Split(e.Path);
                var (main, dependents) = HmiItemKinds.Paths(e.Kind, folder, fileName);
                if (!main.Equals(e.Path, StringComparison.OrdinalIgnoreCase))
                    return ($"[Error] '{e.Path}' does not end with '{e.Kind.Extension}' (kind {e.Kind.Name}). Nothing was written.", "{}");
                foreach (string d in dependents)
                    if (Session.HmiItemExists(project.Name, d))
                        return ($"[Error] '{d}' already exists although '{e.Path}' does not. Nothing was written.", "{}");
                if (OrphanOnDisk(project, dependents.Prepend(main)) is { } orphan) return (orphan, "{}");
                willExist.Add(main);
                foreach (string d in dependents) willExist.Add(d);
                plan.Add((e, $"create ({e.Kind.Name}) + write"));
            }
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        string listing = string.Join(Environment.NewLine,
            plan.Select(x => $"  {x.Action,-28} {x.Entry.Path}  ({x.Entry.Source.Length} characters)"));
        var request = new HmiWriteRequest(HmiWriteKind.ApplyBundle, project.Name, project.ProjectDirectory,
            $"bundle '{bundleName}' ({plan.Count} files)", null,
            NewValue: Environment.NewLine + listing, CurrentValue: $"{plan.Count(x => x.Action.StartsWith("create", StringComparison.Ordinal))} new, " +
                $"{plan.Count(x => !x.Action.StartsWith("create", StringComparison.Ordinal))} existing", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        var written = new List<string>();
        var created = new List<string>();
        foreach (var (e, action) in plan)
        {
            if (action.StartsWith("create", StringComparison.Ordinal))
            {
                var (folder, fileName) = HmiPaths.Split(e.Path);
                try
                {
                    created.AddRange(e.Kind!.ViaImport
                        ? CreateViaImport(project.Name, folder, fileName, e.Source)
                        : Session.AddHmiItem(project.Name, folder, Path.GetFileNameWithoutExtension(fileName), e.Kind.TemplateKey));
                }
                catch (Exception ex) when (IsHmiFailure(ex) || ex is IOException)
                {
                    return (PartialError($"creating '{e.Path}' failed: {ex.Message}", written, created), "{}");
                }
            }
            HmiFileContent current;
            try { current = Session.ReadHmiFile(project.Name, e.Path); }
            catch (Exception ex) when (IsHmiFailure(ex))
            {
                return (PartialError($"reading '{e.Path}' failed: {ex.Message}", written, created), "{}");
            }
            string onDisk = DiskText(project, e.Path) ?? current.Source;
            string target = HmiText.MatchLineEndings(onDisk, HmiText.KeepSchema(current.Source, e.Source));
            if (!HmiText.Unchanged(onDisk, target) || !HmiText.Same(current.Source, target))
            {
                string? writeError = WriteAndVerify(project.Name, e.Path, target);
                if (writeError is not null) return (PartialError(writeError, written, created), "{}");
            }
            written.Add(e.Path);
        }

        string summary = $"Applied bundle '{bundleName}' to '{project.Name}': {written.Count} file(s) — {Describe(outcome)}, " +
                         "each verified by read-back. Run twincat_hmi_build before opening the live view.";
        return (summary, JsonSerializer.Serialize(new
        {
            hmiProject = project.Name, bundle = bundleName, files = plan.Select(x => new { path = x.Entry.Path, action = x.Action }),
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts));
    }

    // ---- helpers ---------------------------------------------------------------

    private string? WriteAndVerify(string project, string path, string target)
    {
        HmiOperationResult result;
        try { result = Session.WriteHmiFile(project, path, target); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return WithDialogHint($"[Error] Writing '{path}' failed: {ex.Message}"); }
        if (!result.Ok) return $"[Error] SetSource('{path}') was refused: {result.LastError ?? "no error text"}.";

        HmiFileContent back;
        try { back = Session.ReadHmiFile(project, path); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return $"[Error] '{path}' was written, but the read-back failed: {ex.Message}"; }
        return HmiText.Same(back.Source, target)
            ? null
            : $"[Error] '{path}' was written, but the read-back differs from line {HmiText.FirstDifference(back.Source, target)} — not treated as success.";
    }

    /// <summary>Creates a file whose TE2000 template names it wrongly (html/view/text, see
    /// <see cref="HmiItemKinds"/>): write it under the right name into a private temp folder and
    /// import it with AddExistingItem.</summary>
    private IReadOnlyList<string> CreateViaImport(string project, string folder, string fileName, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-hmi-new", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string temp = Path.Combine(dir, fileName);
            File.WriteAllText(temp, content);
            // An EngineError (#247, seen for .js — not for the html/view/txt files created here) is not checked:
            // ImportHmiFile only returns when the item IS in the project, and apply_bundle reads the content back.
            return Session.ImportHmiFile(project, folder, fileName, temp).Created;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    private static (string? Text, string? Error) LoadSource(string? source, string? sourceFile)
    {
        bool hasSource = source is not null, hasFile = !string.IsNullOrWhiteSpace(sourceFile);
        if (hasSource == hasFile) return (null, "[Error] Pass exactly one of source (the text) or sourceFile (an absolute path).");
        if (hasSource) return (source, null);
        string f = sourceFile!.Trim();
        if (!Path.IsPathFullyQualified(f)) return (null, "[Error] sourceFile must be an absolute path.");
        if (!File.Exists(f)) return (null, $"[Error] sourceFile not found: {f}");
        if (new FileInfo(f).Length > MaxSourceBytes) return (null, $"[Error] sourceFile is larger than {MaxSourceBytes / 1024 / 1024} MB.");
        return (File.ReadAllText(f), null);
    }

    private static (string Name, IReadOnlyList<HmiBundleEntry>? Entries, string? Error) LoadManifest(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !Path.IsPathFullyQualified(manifestPath.Trim()))
            return ("", null, "[Error] manifestPath must be an absolute path to a bundle manifest (JSON).");
        string mp = manifestPath.Trim();
        if (!File.Exists(mp)) return ("", null, $"[Error] manifest not found: {mp}");
        string baseDir = Path.GetDirectoryName(mp)!;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(mp));
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex) { return ("", null, $"[Error] manifest is not valid JSON: {ex.Message}"); }

        string name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()! : Path.GetFileNameWithoutExtension(mp);
        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return (name, null, "[Error] manifest needs a non-empty \"entries\" array.");

        var entries = new List<HmiBundleEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (JsonElement e in list.EnumerateArray())
        {
            i++;
            string? path = e.TryGetProperty("path", out var p) ? HmiPaths.Normalize(p.GetString()) : null;
            if (path is null) return (name, null, $"[Error] entry {i}: invalid or missing \"path\" (project-relative, '/' separators, no '..').");
            if (!seen.Add(path)) return (name, null, $"[Error] entry {i}: '{path}' is listed twice.");
            string? src = e.TryGetProperty("source", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(src)) return (name, null, $"[Error] entry {i} ('{path}'): missing \"source\".");
            string full = Path.GetFullPath(Path.Combine(baseDir, src));
            if (!File.Exists(full)) return (name, null, $"[Error] entry {i} ('{path}'): source not found: {full}");
            if (new FileInfo(full).Length > MaxSourceBytes) return (name, null, $"[Error] entry {i}: source larger than {MaxSourceBytes / 1024 / 1024} MB.");
            HmiItemKinds.Kind? kind = null;
            if (e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String)
            {
                kind = HmiItemKinds.Find(k.GetString());
                if (kind is null) return (name, null, $"[Error] entry {i} ('{path}'): kind must be one of {HmiItemKinds.Names}.");
            }
            entries.Add(new HmiBundleEntry(path, full, File.ReadAllText(full), kind));
        }
        return (name, entries, null);
    }

    /// <summary>
    /// A file on disk that the project does not list makes AddItem/AddExistingItem ask the modal
    /// "A file with the same name … already exists. Do you want to overwrite it?" — the COM call
    /// then waits for a human and the MCP host stays busy (seen live 2026-09-25). Refuse before
    /// asking the engineer instead.
    /// </summary>
    private static string? OrphanOnDisk(HmiProjectEntry project, IEnumerable<string> paths)
    {
        if (string.IsNullOrWhiteSpace(project.ProjectDirectory)) return null;
        foreach (string p in paths)
        {
            string full = Path.Combine(project.ProjectDirectory, p.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
                return $"[Error] '{p}' exists on disk but not in the project '{project.Name}' ({full}). The XAE would ask " +
                       "whether to overwrite it and block the call. Remove or rename the file, or add it with " +
                       "twincat_hmi_import_file from another location. Nothing was written.";
        }
        return null;
    }

    // Live 2026-09-25: SetSource on a file open in an XAE editor tab makes the XAE ask modally
    // "Die Datei wurde extern geändert … Erneut laden?" — every COM call then fails with
    // RPC_E_SERVERCALL_RETRYLATER until a human answers.
    private static string OpenInEditorError(string path) =>
        $"[Error] '{path}' is open in an editor tab of the XAE. Writing it would make the XAE ask modally whether to " +
        "reload it and block every further call. Close the tab (after saving or discarding your own edits) and retry.";

    private static string PathError(string? path) =>
        $"[Error] Invalid project path '{path}': use a project-relative path with '/' (e.g. 'UserControls/X/X.usercontrol'), " +
        "no drive, no leading '/', no '..'.";

    /// <summary>Error text of a bundle that stopped midway: what was written and verified, and
    /// which files exist now without their final content (created from a template or imported,
    /// then the stop came before the read-back).</summary>
    private static string PartialError(string error, List<string> written, List<string> created)
    {
        string head = error.StartsWith("[Error]", StringComparison.Ordinal) ? error : "[Error] " + error;
        var unfinished = created.Where(c => !written.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        return head +
               (written.Count == 0 ? " Nothing of the bundle was written." : $" Written and verified before the stop: {string.Join(", ", written)}.") +
               (unfinished.Count == 0 ? "" : $" Created, final content not verified: {string.Join(", ", unfinished)}.");
    }
}
