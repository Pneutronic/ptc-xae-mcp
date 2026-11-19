using System.Globalization;
using System.Text.Json;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// hmi-ads-tmc: attach a PLC project's TMC file to an HMI
/// project so its symbols are known with NO PLC connected — the gap `hmi-server` recorded
/// as B10 ("MapSymbol fails without a reachable PLC").
///
/// Two separate mechanisms exist in TE2000, and this part owns the first:
///  1. <b>Engineering-side</b> (here): <c>ITcHmiServer.AttachTmcFile</c> registers the file
///     under a virtual PLC name in the .hmiproj property <c>HmiTmcFiles</c>. That is what
///     the TE2000 dialog "Attach TMC file" does.
///  2. <b>Server-side</b>: <c>ADS.Config::RUNTIMES::&lt;rt&gt;::TMC::{USE_TMC,
///     DATA_SYMBOLUPANDDOWNLOAD}</c>, which points at a converted *_DATA_UPLOADINFO2.bin.
///     READ here (<see cref="ListAdsRuntimes"/>); written by <see cref="EnableRuntimeTmc"/> /
///     <see cref="DisableRuntimeTmc"/> in HmiCoordinator.RuntimeTmc.cs (#161).
///
/// Everything about mechanism 1 was verified by decompiling TE2000 14.17.22.1, because the
/// Automation Interface lies: attach returns true even when it failed, detach always
/// returns false. The read-back is the only evidence, and it reads the saved .hmiproj.
/// </summary>
internal sealed partial class HmiCoordinator
{
    /// <summary>The ADS extension's runtime table as a server symbol. Live 2026-09-26:
    /// readable through ITcHmiServer.ReadSymbols, returns the JSON object of RUNTIMES.</summary>
    internal const string AdsRuntimesSymbol = "ADS.Config::RUNTIMES";

    /// <summary>Pseudo-domain the engineering symbol tree puts an attached TMC under when it
    /// carries no PlcName (decompiled: TcHmiServerSymbolRootLazy.GetTmcDomain, and the
    /// <c>Domain == "TMC"</c> comparisons in the binding resolver).</summary>
    internal const string TmcDomain = "TMC";

    /// <summary>Opens and identifies a .tmc. A seam only so tests can prove that a path the
    /// <see cref="PathPolicy"/> refuses never reaches it.</summary>
    internal Func<string?, (TmcFileFacts? Facts, string? Error)> TmcInspector { get; init; } = TmcFile.Inspect;

    // ---- read-only -----------------------------------------------------------

    public (string Summary, string Json) ListTmcFiles(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (attachments, readError) = ReadAttachments(project);
        if (attachments is null) return (readError!, "{}");

        // The server-side flag is a different mechanism; show it so nobody hunts the wrong one.
        // It is secondary here, so an unreadable runtime table does not fail the call — but it is
        // reported as unknown, never as "none uses one" (review finding E-5).
        var (runtimes, runtimeError) = ReadAdsRuntimes(project.Name);
        List<HmiAdsRuntime>? serverSide = runtimes?.Where(r => r.UseTmc).ToList();

        int missing = attachments.Count(a => !a.FileExists);
        string summary = attachments.Count == 0
            ? $"HMI project '{project.Name}' has no TMC file attached."
            : $"{attachments.Count} TMC file(s) attached to '{project.Name}': " +
              string.Join(", ", attachments.Select(a => $"{a.RootNamespace} → {a.StoredFileName}")) + ".";
        if (missing > 0) summary += $" WARNING: {missing} of them no longer exist on disk.";
        summary += serverSide is null
            ? " Whether an ADS runtime uses a TMC file server-side is UNKNOWN — the runtime table " +
              $"could not be read ({WithoutErrorPrefix(runtimeError)})."
            : serverSide.Count > 0
                ? $" Separately, {serverSide.Count} ADS runtime(s) serve symbols from a TMC file server-side " +
                  $"({string.Join(", ", serverSide.Select(r => r.Name))})."
                : " No ADS runtime uses a TMC file server-side.";

        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            count = attachments.Count,
            attachments,
            adsRuntimesUsingTmc = serverSide?.Select(r => new { r.Name, r.TmcFile }),
            adsRuntimesReadable = serverSide is not null,
        }, JsonOpts);
        return (summary, json);
    }

    public (string Summary, string Json) ListAdsRuntimes(string? hmiProject)
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (runtimes, readError) = ReadAdsRuntimes(project.Name);
        if (runtimes is null) return (readError!, "{}");

        int offline = runtimes.Count(r => r.UseTmc);
        string summary = runtimes.Count == 0
            ? $"HMI project '{project.Name}': the ADS extension has no runtime configured."
            : $"{runtimes.Count} ADS runtime(s) in '{project.Name}': " +
              string.Join(", ", runtimes.Select(Describe)) + $". {offline} of them use a TMC file.";
        string json = JsonSerializer.Serialize(
            new { hmiProject = project.Name, count = runtimes.Count, usingTmc = offline, runtimes }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// #166: the .tmc file of every PLC project in the open solution, as CANDIDATES for
    /// <see cref="AttachTmcFile"/>. Read-only (no logout, no confirmation) and independent of
    /// any HMI project. Deliberately never feeds a default into the attach — which PLC belongs
    /// to which HMI is a project decision; the agent sees the list and names one.
    /// </summary>
    public (string Summary, string Json) ListPlcTmcCandidates()
    {
        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");

        IReadOnlyList<PlcProjectXml> projects;
        try { projects = Session.ListPlcProjectXml(); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return ($"[Error] {ex.Message}", "{}"); }

        string? solutionDirectory = SolutionDirectory();
        IReadOnlyList<HmiTmcCandidate> candidates = HmiTmcCandidates.Build(projects, TmcInspector,
            path => McpPathGuard.CheckOptionalFile(path, solutionDirectory, "tmcFile") is { } refusal
                ? WithoutErrorPrefix(refusal)
                : null);

        int existing = candidates.Count(c => c.Exists);
        int usable = candidates.Count(c => c.SymbolCount > 0);
        string summary = candidates.Count == 0
            ? "No PLC project in the open solution (no TwinCAT project has a child under TIPC), so there is no TMC candidate."
            : $"{candidates.Count} PLC project(s) in the open solution, {existing} with an existing .tmc " +
              $"({usable} with PLC symbols): " +
              string.Join(", ", candidates.Select(DescribeCandidate)) + ". " +
              "Candidates only — pass the absolutePath of the PLC project that belongs to the HMI as tmcFile " +
              "to twincat_hmi_attach_tmc_file.";
        string json = JsonSerializer.Serialize(new
        {
            count = candidates.Count,
            existing,
            candidates,
        }, JsonOpts);
        return (summary, json);
    }

    private static string DescribeCandidate(HmiTmcCandidate c) =>
        $"{c.TcProject}/{c.PlcProject} → " +
        (c.AbsolutePath ?? "(no path)") +
        (c.SymbolCount is int n ? $" ({n.ToString(CultureInfo.InvariantCulture)} symbols)"
            : c.Exists ? "" : " (missing)");

    private static string Describe(HmiAdsRuntime r) =>
        $"{r.Name} ({(r.UseTmc ? "TMC" : $"ADS {r.NetId ?? "?"}:{r.Port?.ToString(CultureInfo.InvariantCulture) ?? "?"}")}" +
        (r.Enabled ? "" : ", disabled") + (r.ReadOnly ? ", read-only" : "") + ")";

    // ---- writes (confirmed by the engineer, hmi-server "Schreibschutz") ------

    /// <summary>
    /// Attach a PLC project's TMC file. Order is fixed and fail-closed: check the path against
    /// <see cref="PathPolicy"/> → identify the file on disk → derive/validate the virtual PLC
    /// name → resolve → read the attachments → refuse the two duplicates TE2000 would answer
    /// with a modal box → popup → AttachTmcFile → read-back against the saved .hmiproj.
    /// </summary>
    public (string Summary, string Json) AttachTmcFile(
        string? hmiProject, string? tmcFile, string? virtualPlcName, int confirmTimeoutSeconds)
    {
        // #156 item 7: a path from an MCP call is checked against the allowed roots BEFORE the file
        // is opened — the same rule as every other file parameter. An empty or relative path is
        // left to Inspect, which refuses it without touching the disk and says what tmcFile has to
        // be (a relative path would otherwise be judged against this host's working directory).
        string candidate = tmcFile?.Trim() ?? "";
        if (IsFullyQualified(candidate)
            && McpPathGuard.CheckOptionalFile(candidate, SolutionDirectory(), "tmcFile") is { } pathError)
            return (pathError, "{}");
        var (facts, fileError) = TmcInspector(tmcFile);
        if (facts is null) return ($"[Error] {fileError}", "{}");
        if (facts.SymbolCount == 0)
            return ($"[Error] '{facts.Path}' contains no <Symbol> entry, so it describes no PLC symbols. " +
                    "The event-class files in a project root (HMI_Error.tmc, HMI_Info.tmc, HMI_Warning.tmc) " +
                    @"look like this — the PLC symbols live in <PlcProject>\<PlcProject>.tmc.", "{}");

        // TE2000's own dialog derives the name from the file, so that is the default here too.
        string name = string.IsNullOrWhiteSpace(virtualPlcName)
            ? Path.GetFileNameWithoutExtension(facts.Path)
            : virtualPlcName.Trim();
        if (!HmiTmcPaths.IsValidVirtualPlcName(name))
            return ($"[Error] virtualPlcName '{name}' cannot be a symbol root — use letters, digits, " +
                    "'_' or '-' only (max 64 characters), e.g. 'PLC' or 'PLC_Main'.", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (before, readError) = ReadAttachments(project);
        if (before is null) return (readError!, "{}");

        string stored = HmiTmcPaths.StoragePath(facts.Path, SolutionDirectory());

        // Exactly the two cases TcHmiServerSymbolRootLazy.AttachTmcFile refuses. Catching them
        // here keeps the XAE's modal warning box — which no agent can dismiss — unreachable.
        HmiTmcAttachment? sameFile = before.FirstOrDefault(a =>
            a.StoredFileName.Equals(stored, StringComparison.OrdinalIgnoreCase)
            || a.StoredFileName.Equals(facts.Path, StringComparison.OrdinalIgnoreCase)
            || a.ResolvedPath.Equals(facts.Path, StringComparison.OrdinalIgnoreCase));
        if (sameFile is not null)
            return ($"[Error] '{facts.Path}' is already attached to '{project.Name}' under the virtual PLC " +
                    $"name '{sameFile.RootNamespace}' — nothing was written. Detach it first " +
                    "(twincat_hmi_detach_tmc_file) to attach it under a different name.", "{}");
        HmiTmcAttachment? sameName = before.FirstOrDefault(a =>
            a.RootNamespace.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
            return ($"[Error] The virtual PLC name '{name}' is already used in '{project.Name}' for " +
                    $"'{sameName.StoredFileName}' — nothing was written. Pass a different virtualPlcName " +
                    "or detach that one first.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.AttachTmcFile, project.Name, project.ProjectDirectory,
            name, TmcDomain,
            NewValue: $"{facts.Path} ({facts.SymbolCount} symbols, {facts.SizeBytes / 1024} KB" +
                      (facts.GeneratedBy is null ? "" : $", GeneratedBy={facts.GeneratedBy}") + ")",
            CurrentValue: before.Count == 0
                ? "(no TMC file attached)"
                : $"{before.Count} attached: {string.Join(", ", before.Select(a => a.RootNamespace))}",
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = Session.AttachHmiTmcFile(project.Name, name, facts.Path); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] AttachTmcFile failed: {ex.Message}"), "{}"); }

        var (after, afterError) = ReadAttachments(project);
        if (after is null)
            return ($"[Error] AttachTmcFile was issued, but the read-back failed ({afterError}) — " +
                    "whether it took effect is unknown. Check with twincat_hmi_list_tmc_files.", "{}");
        HmiTmcAttachment? landed = after.FirstOrDefault(a =>
            a.RootNamespace.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (landed is null)
            return ($"[Error] AttachTmcFile reported no exception, but '{name}' is not in the project's " +
                    $"{HmiTmcPaths.ProjectProperty} on read-back — not treated as success. " +
                    (result.LastError is null
                        ? "The XAE may be showing a warning box with the reason."
                        : $"Last server error: {result.LastError}."), "{}");

        string summary = $"TMC file attached to '{project.Name}' as virtual PLC '{landed.RootNamespace}': " +
                         $"{facts.Path} ({facts.SymbolCount} symbols) — {Describe(outcome)}, verified in the " +
                         $"project's {HmiTmcPaths.ProjectProperty} ({landed.StoredFileName}). " +
                         "Its symbols are now available in the HMI engineering without a PLC connection.";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            virtualPlcName = landed.RootNamespace,
            tmcFile = facts.Path,
            storedAs = landed.StoredFileName,
            symbolCount = facts.SymbolCount,
            sizeBytes = facts.SizeBytes,
            generatedBy = facts.GeneratedBy,
            domain = TmcDomain,
            attachedTotal = after.Count,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// Remove an attached TMC file by its virtual PLC name. Not attached → "Nothing to do"
    /// without a popup (hmi-server decision E8).
    /// </summary>
    public (string Summary, string Json) DetachTmcFile(
        string? hmiProject, string? virtualPlcName, int confirmTimeoutSeconds)
    {
        string name = virtualPlcName?.Trim() ?? "";
        if (!HmiTmcPaths.IsValidVirtualPlcName(name))
            return ("[Error] virtualPlcName is required — the name the TMC file is attached under " +
                    "(twincat_hmi_list_tmc_files shows it).", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (before, readError) = ReadAttachments(project);
        if (before is null) return (readError!, "{}");
        HmiTmcAttachment? current = before.FirstOrDefault(a =>
            a.RootNamespace.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (current is null)
            return ($"Nothing to do: '{project.Name}' has no TMC file attached under the virtual PLC name " +
                    $"'{name}'" + (before.Count == 0 ? " (none at all)." :
                        $" (attached: {string.Join(", ", before.Select(a => a.RootNamespace))})."),
                JsonSerializer.Serialize(
                    new { hmiProject = project.Name, virtualPlcName = name, detached = false }, JsonOpts));

        var request = new HmiWriteRequest(HmiWriteKind.DetachTmcFile, project.Name, project.ProjectDirectory,
            current.RootNamespace, TmcDomain,
            NewValue: "(removed — symbols under this name are no longer known offline)",
            CurrentValue: current.StoredFileName, WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        try { Session.DetachHmiTmcFile(project.Name, current.RootNamespace); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] DetachTmcFile failed: {ex.Message}"), "{}"); }

        var (after, afterError) = ReadAttachments(project);
        if (after is null)
            return ($"[Error] DetachTmcFile was issued, but the read-back failed ({afterError}) — " +
                    "whether it took effect is unknown. Check with twincat_hmi_list_tmc_files.", "{}");
        if (after.Any(a => a.RootNamespace.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return ($"[Error] DetachTmcFile was issued, but '{name}' is still in the project's " +
                    $"{HmiTmcPaths.ProjectProperty} on read-back — not treated as success.", "{}");

        string summary = $"TMC file detached from '{project.Name}': virtual PLC '{current.RootNamespace}' " +
                         $"({current.StoredFileName}) — {Describe(outcome)}, verified in the project's " +
                         $"{HmiTmcPaths.ProjectProperty} ({after.Count} still attached).";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            virtualPlcName = current.RootNamespace,
            detached = true,
            previousFile = current.StoredFileName,
            attachedTotal = after.Count,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    // ---- read-back sources ---------------------------------------------------

    /// <summary>
    /// A ready-to-return error message reused as a REASON inside a successful summary must not
    /// drag its "[Error]" prefix along — a success string carrying it reads like a failure, and
    /// if it ever ended up at the front it would become one to <see cref="McpErrors.IsError"/>.
    /// </summary>
    private static string WithoutErrorPrefix(string? message)
    {
        string text = message?.Trim() ?? "no reason given";
        const string prefix = "[Error]";
        while (text.StartsWith(prefix, StringComparison.Ordinal))
            text = text[prefix.Length..].TrimStart();
        return text.Length == 0 ? "no reason given" : text;
    }

    private static bool IsFullyQualified(string path)
    {
        try { return path.Length > 0 && Path.IsPathFullyQualified(path); }
        catch (ArgumentException) { return false; }
    }

    private string? SolutionDirectory()
    {
        try
        {
            string? solution = Session.ActiveSolutionPath();
            return string.IsNullOrWhiteSpace(solution) ? null : Path.GetDirectoryName(solution);
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return null; }
    }

    /// <summary>
    /// Attached TMC files from the saved .hmiproj, or a ready-to-return error. Fail-closed: an
    /// unreadable project file is never reported as "nothing attached".
    /// </summary>
    private (IReadOnlyList<HmiTmcAttachment>? Attachments, string? Error) ReadAttachments(HmiProjectEntry project)
    {
        string? projectFile = HmiTmcPaths.FindProjectFile(project.ProjectDirectory, project.Name);
        if (projectFile is null)
            return (null, $"[Error] No unique .hmiproj found in '{project.ProjectDirectory}', so the " +
                          $"attached TMC files ({HmiTmcPaths.ProjectProperty}) cannot be read.");

        IReadOnlyList<HmiTmcAttachment>? attachments =
            HmiTmcPaths.ReadAttachments(projectFile, SolutionDirectory());
        return attachments is null
            ? (null, $"[Error] '{projectFile}' could not be read as a project file, so the attached TMC " +
                     "files are unknown — nothing was written.")
            : (attachments, null);
    }

    /// <summary>Live ADS runtime table, or a ready-to-return error. Fail-closed: an unreadable or
    /// unparsable answer is never treated as "no runtimes".</summary>
    private (IReadOnlyList<HmiAdsRuntime>? Runtimes, string? Error) ReadAdsRuntimes(string hmiProject)
    {
        HmiSymbolValue read;
        try
        {
            IReadOnlyList<HmiSymbolValue> values = Session.ReadHmiSymbols(hmiProject, [AdsRuntimesSymbol]);
            if (values.Count == 0)
                return (null, WithDialogHint($"[Error] The HMI server of '{hmiProject}' gave no answer for " +
                                             $"'{AdsRuntimesSymbol}'."));
            read = values[0];
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (null, WithDialogHint($"[Error] {ex.Message}")); }

        if (!read.Ok)
            return (null, WithDialogHint(
                $"[Error] Cannot read '{AdsRuntimesSymbol}' from the HMI server of '{hmiProject}': " +
                $"{read.Error ?? "no error text"}. Is the ADS server extension loaded " +
                "(twincat_hmi_get_project_info)?"));

        IReadOnlyList<HmiAdsRuntime>? parsed = TryParseAdsRuntimes(read.Value);
        return parsed is null
            ? (null, $"[Error] The HMI server answered '{AdsRuntimesSymbol}' with something that is not a " +
                     "runtime object — nothing was read.")
            : (parsed, null);
    }

    /// <summary>
    /// Parses the JSON text of <see cref="AdsRuntimesSymbol"/> into runtimes. Keys are the
    /// upper-case config names of TcHmiAds.Schema.json; the per-runtime TMC object is
    /// optional, because a runtime that never had one carries no TMC key. null = not a
    /// runtime object (callers fail closed).
    /// </summary>
    internal static IReadOnlyList<HmiAdsRuntime>? TryParseAdsRuntimes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
        if (root.ValueKind != JsonValueKind.Object) return null;

        var runtimes = new List<HmiAdsRuntime>();
        foreach (JsonProperty entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) return null;
            JsonElement rt = entry.Value;
            JsonElement tmc = Member(rt, "TMC");
            runtimes.Add(new HmiAdsRuntime(
                entry.Name,
                Bool(rt, "ENABLED") ?? true,
                Text(rt, "NETID"),
                Int(rt, "PORT"),
                Bool(rt, "READ_ONLY") ?? false,
                Bool(rt, "USE_WHITELISTING") ?? false,
                Member(rt, "SYMBOLS") is { ValueKind: JsonValueKind.Object } s ? s.EnumerateObject().Count() : 0,
                Bool(tmc, "USE_TMC") ?? false,
                Text(tmc, "DATA_SYMBOLUPANDDOWNLOAD"),
                tmc.ValueKind == JsonValueKind.Object));
        }
        return runtimes.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static JsonElement Member(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    private static bool? Bool(JsonElement obj, string name) => Member(obj, name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? Text(JsonElement obj, string name) =>
        Member(obj, name) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrEmpty(v.GetString())
            ? v.GetString()
            : null;

    private static int? Int(JsonElement obj, string name) =>
        Member(obj, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int i) ? i : null;
}
