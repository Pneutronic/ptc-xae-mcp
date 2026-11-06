using System.Runtime.InteropServices;
using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// hmi-server: TwinCAT HMI (TE2000) projects of the open
/// solution through the HMI Automation Interface. Pure orchestration over
/// <see cref="ITwinCatSession"/> — input validation, HMI-project resolution
/// (never a "first project" fallback when several exist) and result formatting —
/// so every rule here is unit-tested without an XAE.
/// </summary>
internal sealed partial class HmiCoordinator(ITwinCatSession session, IHmiWriteConfirmer? confirmer = null)
{
    internal const int MaxTreeDepth = 6;

    // Primary-constructor parameters are only in scope in this part; HmiCoordinator.Views.cs
    // reaches the session through this property.
    private ITwinCatSession Session => session;

    // Runs in the primary constructor: every HMI tool and GUI action constructs a coordinator
    // first, so a missing TE2000 fails here with a plain message instead of a runtime
    // "could not load file or assembly TcHmiAutomation" from the first typed call.
    private readonly bool _hmiAvailable = Require(session);

    private static bool Require(ITwinCatSession s)
    {
        s.EnsureHmiAutomationAvailable();
        return true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---- read-only -----------------------------------------------------------

    public (string Summary, string Json) ListProjects()
    {
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        HmiProjectList list;
        try { list = session.ListHmiProjects(); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        string summary = list.Projects.Count switch
        {
            0 => "No TwinCAT HMI project (.hmiproj) in the open solution.",
            1 => $"1 HMI project: {Describe(list.Projects[0])}.",
            _ => $"{list.Projects.Count} HMI projects: {string.Join(", ", list.Projects.Select(Describe))}.",
        };
        if (list.BlockingDialog is not null) summary += " " + DialogHint(list.BlockingDialog);
        return (summary, JsonSerializer.Serialize(list, JsonOpts));
    }

    public (string Summary, string Json) GetProjectInfo(string? hmiProject, bool includeFiles, int maxDepth)
    {
        if (maxDepth < 1 || maxDepth > MaxTreeDepth)
            return ($"[Error] maxDepth must be between 1 and {MaxTreeDepth}.", "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiProjectInfo info;
        try { info = session.GetHmiProjectInfo(project.Name, includeFiles, maxDepth); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        var loaded = info.ServerExtensions.Where(e => e.Loaded).Select(e => e.DomainName).ToList();
        string summary =
            $"HMI project '{info.Name}': {loaded.Count}/{info.ServerExtensions.Count} server extension(s) loaded" +
            (loaded.Count > 0 ? $" ({string.Join(", ", loaded)})" : "") +
            $", startup view {Config(info, "StartupView")}, locale {Config(info, "Locale")}.";
        if (info.Files is not null)
            summary += $" {info.Files.Count} top-level item(s){(info.FilesTruncated ? " (tree truncated)" : "")}.";
        if (loaded.Count == 0 && session.FindHmiBlockingDialog() is { } dialog)
            summary += " " + DialogHint(dialog);
        return (summary, JsonSerializer.Serialize(info, JsonOpts));
    }

    /// <summary>Mapped symbols per page: default 500, at most 5000.</summary>
    internal static readonly Mcp.McpLimit MappedSymbolPage = new(500, 5000);

    public (string Summary, string Json) ListMappedSymbols(
        string? hmiProject, string? domain, string? namePrefix, bool includeHidden, bool refresh,
        int? limit = null, int offset = 0)
    {
        if (Mcp.McpPaging.Validate(limit, offset) is { } invalid) return (invalid, "{}");
        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        IReadOnlyList<HmiMappedSymbol> all;
        try { all = session.ListHmiMappedSymbols(project.Name, refresh); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        string? wantedDomain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        string? prefix = string.IsNullOrWhiteSpace(namePrefix) ? null : namePrefix.Trim();
        var symbols = all
            .Where(s => includeHidden || !s.Hidden)
            .Where(s => wantedDomain is null || s.Domain.Equals(wantedDomain, StringComparison.OrdinalIgnoreCase))
            .Where(s => prefix is null || s.MappedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.MappedName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var filters = new List<string>();
        if (wantedDomain is not null) filters.Add($"domain={wantedDomain}");
        if (prefix is not null) filters.Add($"prefix={prefix}");
        if (!includeHidden) filters.Add("hidden excluded");
        var page = Mcp.McpPaging.Apply(symbols, limit, offset, MappedSymbolPage);
        string shown = page.Returned == symbols.Count ? ""
            : page.Returned == 0 ? $" None from offset {page.Offset}."
            : $" Showing {page.Offset + 1}–{page.Offset + page.Returned}{(page.HasMore ? $", next_offset {page.NextOffset}" : "")}.";
        string summary = $"{symbols.Count} mapped symbol(s) in HMI project '{project.Name}' " +
                         $"({all.Count} mapped in all; {string.Join(", ", filters.DefaultIfEmpty("no filter"))}).{shown}";
        string json = page.Serialize(
            new { hmiProject = project.Name, mappedCount = all.Count, symbols = page.Items }, JsonOpts, "symbols");
        return (summary, json);
    }

    internal const int MaxReadSymbols = 200;

    public (string Summary, string Json) ReadSymbols(string? hmiProject, IReadOnlyList<string>? symbols)
    {
        var names = (symbols ?? Array.Empty<string>())
            .Select(s => s?.Trim() ?? "")
            .ToList();
        if (names.Count == 0)
            return ("[Error] symbols is required — at least one HMI symbol name, e.g. 'ADS.PLC1.MAIN.bStart'.", "{}");
        if (names.Any(n => n.Length == 0))
            return ("[Error] symbols must not contain empty names.", "{}");
        names = names.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > MaxReadSymbols)
            return ($"[Error] At most {MaxReadSymbols} symbols per call (got {names.Count}).", "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        IReadOnlyList<HmiSymbolValue> values;
        try { values = session.ReadHmiSymbols(project.Name, names); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        int ok = values.Count(v => v.Ok);
        string summary = $"Read {ok}/{values.Count} symbol(s) through the HMI server of '{project.Name}'.";
        var failed = values.Where(v => !v.Ok).ToList();
        if (failed.Count > 0)
            summary += $" Not readable: {string.Join("; ", failed.Take(5).Select(v => $"{v.Symbol} ({v.Error})"))}" +
                       (failed.Count > 5 ? $" … +{failed.Count - 5} more." : ".");
        if (failed.Any(v => v.Error?.Contains("HMI_ADS_E", StringComparison.Ordinal) == true))
            summary += " " + AdsRuntimeNote;
        string json = JsonSerializer.Serialize(
            new { hmiProject = project.Name, count = values.Count, ok, results = values }, JsonOpts);
        return (summary, json);
    }

    internal static readonly string[] ServerActions = ["start", "shutdown", "refresh_symbols"];
    internal const int MaxServerTimeoutMs = 120_000;

    /// <summary>
    /// Engineering-server control. Not gated by the write confirmation (spec E3): it
    /// changes no project file. Resolution skips the IsReady check so a server can be
    /// started again after a shutdown.
    /// </summary>
    public (string Summary, string Json) ControlServer(string? hmiProject, string? action, int timeoutMs)
    {
        string act = (action ?? "").Trim().ToLowerInvariant();
        if (!ServerActions.Contains(act))
            return ($"[Error] action must be one of: {string.Join(", ", ServerActions)}.", "{}");
        if (timeoutMs < 0 || timeoutMs > MaxServerTimeoutMs)
            return ($"[Error] timeoutMs must be between 0 and {MaxServerTimeoutMs}.", "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");

        var (project, resolveError) = ResolveProject(hmiProject, requireReady: false);
        if (project is null) return (resolveError!, "{}");

        HmiServerControlResult result;
        try { result = session.ControlHmiServer(project.Name, act, timeoutMs); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }

        int loaded = result.ExtensionsAfter.Count(e => e.Loaded);
        string state = $"{loaded}/{result.ExtensionsAfter.Count} server extension(s) loaded";
        if (!result.Ok)
            return (WithDialogHint($"[Error] HMI server {act} failed for '{project.Name}': {result.Error} ({state})."), "{}");
        if (!result.Settled)
            return (WithDialogHint(
                $"[Error] HMI server {act} was issued for '{project.Name}' but had not settled after {timeoutMs} ms ({state})."), "{}");

        string summary = $"HMI engineering server of '{project.Name}': {act} done — {state}.";
        return (summary, JsonSerializer.Serialize(result, JsonOpts));
    }

    // ---- writes (every one confirmed by the engineer, spec "Schreibschutz") --------

    internal const int DefaultConfirmTimeoutSeconds = 120;
    internal const int MinConfirmTimeoutSeconds = 10;
    internal const int MaxConfirmTimeoutSeconds = 600;

    /// <summary>
    /// Map a PLC symbol into the HMI server. Order is fixed and fail-closed: validate →
    /// resolve → refuse if already mapped → popup → MapSymbol → read-back.
    /// </summary>
    public (string Summary, string Json) MapSymbol(
        string? hmiProject, string? plcPath, string? mapName, string? internalName,
        string? domain, string? runtime, string? subSymbolName, int confirmTimeoutSeconds)
    {
        string dom = string.IsNullOrWhiteSpace(domain) ? HmiSymbolNames.AdsDomain : domain.Trim();
        bool hasPath = !string.IsNullOrWhiteSpace(plcPath);
        bool hasExplicit = !string.IsNullOrWhiteSpace(mapName) || !string.IsNullOrWhiteSpace(internalName);

        if (hasPath && hasExplicit)
            return ("[Error] Pass either plcPath or mapName + internalName, not both.", "{}");
        // Issue #160 (live 2026-10-10): MapSymbol(…, "TMC") returns true, the server answers INVALID_DOMAIN
        // ("Domain TMC does not exist or is not initialized") and the engineering model keeps a dangling
        // entry. TMC is an engineering-only domain — refuse before anything is written.
        if (dom.Equals(TmcDomain, StringComparison.OrdinalIgnoreCase))
            return ($"[Error] Domain '{TmcDomain}' cannot be mapped: it exists only in the HMI engineering (attached TMC " +
                    "files); the HMI server rejects it with INVALID_DOMAIN and the engineering would keep a dangling " +
                    "entry. Map against the ADS domain with a reachable PLC, or use an offline server symbol " +
                    "(twincat_hmi_map_offline_symbol).", "{}");
        string map, target;
        if (hasPath)
        {
            if (!dom.Equals(HmiSymbolNames.AdsDomain, StringComparison.OrdinalIgnoreCase))
                return ($"[Error] plcPath derives ADS names only; for domain '{dom}' pass mapName + internalName.", "{}");
            if (!HmiSymbolNames.TryFromPlcPath(plcPath, runtime, out map, out target, out string? nameError))
                return ($"[Error] {nameError}", "{}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(mapName) || string.IsNullOrWhiteSpace(internalName))
                return ("[Error] Pass plcPath (e.g. 'MAIN.fbHmi.bStart') or both mapName and internalName.", "{}");
            map = mapName.Trim();
            target = internalName.Trim();
        }
        string? sub = string.IsNullOrWhiteSpace(subSymbolName) ? null : subSymbolName.Trim();
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        MappingProbe before;
        try { before = ProbeMapping(project.Name, map); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (before.State == MappingState.Mapped)
            return ($"[Error] '{map}' is already mapped in '{project.Name}' ({before.Evidence}). The Automation " +
                    "Interface does not reveal an existing mapping's target, so it is never re-mapped silently — " +
                    "unmap it first to change it.", "{}");
        if (before.State == MappingState.Unknown)
            return ($"[Error] Cannot determine whether '{map}' is mapped ({before.Evidence}) — nothing was written.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.MapSymbol, project.Name, project.ProjectDirectory,
            map, dom, sub is null ? target : $"{target} (subSymbol {sub})", CurrentValue: "(not mapped)", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = session.MapHmiSymbol(project.Name, map, target, dom, sub); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] MapSymbol failed: {ex.Message}"), "{}"); }
        if (!result.Ok)
            return ($"[Error] MapSymbol('{map}' → '{target}', domain {dom}) was refused by the HMI server: " +
                    $"{result.LastError ?? "no error text"}." + MapRefusalNote(dom, result.LastError), "{}");

        MappingProbe after;
        try { after = ProbeMapping(project.Name, map); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return ($"[Error] MapSymbol returned true, but the read-back failed: {ex.Message}", "{}"); }
        if (after.State != MappingState.Mapped)
            return ($"[Error] MapSymbol returned true, but the running server does not know '{map}' on read-back " +
                    $"({after.Evidence}) — not treated as success.", "{}");

        string summary = $"Mapped '{map}' → '{target}' (domain {dom}) in '{project.Name}' — {Describe(outcome)}, " +
                         $"verified by a live read-back ({after.Evidence}).";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            mapName = map,
            internalName = target,
            domain = dom,
            subSymbolName = sub,
            confirmation = ConfirmationWire(outcome),
            readBack = after.Evidence,
        }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// Remove a mapping: validate → resolve → "nothing to do" when not mapped (no popup)
    /// → popup → UnMapSymbol → read-back must no longer find it.
    /// </summary>
    public (string Summary, string Json) UnmapSymbol(
        string? hmiProject, string? plcPath, string? mapName, string? domain, string? runtime,
        bool removeHistorizedData, int confirmTimeoutSeconds)
    {
        string dom = string.IsNullOrWhiteSpace(domain) ? HmiSymbolNames.AdsDomain : domain.Trim();
        bool hasPath = !string.IsNullOrWhiteSpace(plcPath);
        bool hasName = !string.IsNullOrWhiteSpace(mapName);
        if (hasPath == hasName)
            return ("[Error] Pass exactly one of mapName (e.g. 'ADS.PLC1.MAIN.fbHmi.bStart') or plcPath.", "{}");
        string map;
        if (hasPath)
        {
            if (!dom.Equals(HmiSymbolNames.AdsDomain, StringComparison.OrdinalIgnoreCase))
                return ($"[Error] plcPath derives ADS names only; for domain '{dom}' pass mapName.", "{}");
            if (!HmiSymbolNames.TryFromPlcPath(plcPath, runtime, out map, out _, out string? nameError))
                return ($"[Error] {nameError}", "{}");
        }
        else
        {
            map = mapName!.Trim();
        }
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        MappingProbe before;
        try { before = ProbeMapping(project.Name, map); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (before.State == MappingState.NotMapped)
            return ($"Nothing to do: '{map}' is not mapped in '{project.Name}' ({before.Evidence}).",
                JsonSerializer.Serialize(new { hmiProject = project.Name, mapName = map, removed = false }, JsonOpts));
        if (before.State == MappingState.Unknown)
            return ($"[Error] Cannot determine whether '{map}' is mapped ({before.Evidence}) — nothing was written.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.UnmapSymbol, project.Name, project.ProjectDirectory,
            map, dom, NewValue: removeHistorizedData ? "(removed, incl. historized data)" : "(removed)",
            CurrentValue: $"mapped ({before.Evidence})", WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = session.UnmapHmiSymbol(project.Name, map, dom, removeHistorizedData); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] UnMapSymbol failed: {ex.Message}"), "{}"); }
        if (!result.Ok)
            return ($"[Error] UnMapSymbol('{map}') was refused by the HMI server: {result.LastError ?? "no error text"}.", "{}");

        MappingProbe after;
        try { after = ProbeMapping(project.Name, map); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return ($"[Error] UnMapSymbol returned true, but the read-back failed: {ex.Message}", "{}"); }
        if (after.State != MappingState.NotMapped)
            return ($"[Error] UnMapSymbol returned true, but the running server still knows '{map}' on read-back " +
                    $"({after.Evidence}) — not treated as success.", "{}");

        string summary = $"Unmapped '{map}' from '{project.Name}'" +
                         (removeHistorizedData ? " incl. historized data" : "") +
                         $" — {Describe(outcome)}, verified by a live read-back ({after.Evidence}).";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            mapName = map,
            domain = dom,
            removed = true,
            removeHistorizedData,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// Write a value through the HMI server (for ADS: through to the live PLC).
    /// validate JSON → resolve → live pre-read (unmapped → refuse) → read-only → popup →
    /// WriteSymbolJson → read-back. A read-back that differs is reported, not failed:
    /// the PLC may legitimately change the value at once (e.g. a pulse bit).
    /// </summary>
    public (string Summary, string Json) WriteSymbol(
        string? hmiProject, string? symbol, string? valueJson, int confirmTimeoutSeconds)
    {
        string name = symbol?.Trim() ?? "";
        if (name.Length == 0)
            return ("[Error] symbol is required, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'.", "{}");
        if (string.IsNullOrWhiteSpace(valueJson))
            return ("[Error] valueJson is required — a JSON value, e.g. true, 42, 3.5 or \"text\".", "{}");
        string value;
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            value = doc.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            return ($"[Error] valueJson is not valid JSON ({ex.Message}). Strings need quotes: \"text\".", "{}");
        }
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        HmiSymbolValue before;
        try
        {
            IReadOnlyList<HmiSymbolValue> reads = session.ReadHmiSymbols(project.Name, [name]);
            before = reads.Count > 0 ? reads[0] : new HmiSymbolValue(name, false, null, "the server gave no answer");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        if (!before.Ok && before.Error?.Contains("SYMBOL_NOT_MAPPED", StringComparison.Ordinal) == true)
            return ($"[Error] '{name}' is not mapped in '{project.Name}' (the server answers SYMBOL_NOT_MAPPED) — " +
                    "nothing to write to. Map it first (twincat_hmi_map_symbol).", "{}");

        // The Automation-Interface list is a stale cache, but a read-only flag it still
        // carries is honoured: refusing a write wrongly is the safe direction.
        HmiMappedSymbol? listed = null;
        try
        {
            listed = session.ListHmiMappedSymbols(project.Name, false)
                .FirstOrDefault(s => s.MappedName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { /* only a hint — the popup still guards */ }
        if (listed?.ReadOnly == true)
            return ($"[Error] '{name}' is mapped read-only in '{project.Name}' — not written.", "{}");

        bool writesToPlc = name.StartsWith(HmiSymbolNames.AdsDomain + ".", StringComparison.OrdinalIgnoreCase)
                           || listed?.Domain.Equals(HmiSymbolNames.AdsDomain, StringComparison.OrdinalIgnoreCase) == true;
        string current = before.Ok ? before.Value ?? "(null)" : $"(not readable: {before.Error})";
        var request = new HmiWriteRequest(HmiWriteKind.WriteSymbol, project.Name, project.ProjectDirectory,
            name, listed?.Domain ?? (writesToPlc ? HmiSymbolNames.AdsDomain : null), value, current, writesToPlc);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = session.WriteHmiSymbol(project.Name, name, value); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] WriteSymbolJson failed: {ex.Message}"), "{}"); }
        if (!result.Ok)
            return ($"[Error] WriteSymbolJson('{name}', {value}) was refused by the HMI server: " +
                    $"{result.LastError ?? "no error text"}.", "{}");

        HmiSymbolValue after;
        try
        {
            IReadOnlyList<HmiSymbolValue> reads = session.ReadHmiSymbols(project.Name, [name]);
            after = reads.Count > 0 ? reads[0] : new HmiSymbolValue(name, false, null, "the server gave no answer");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { after = new HmiSymbolValue(name, false, null, ex.Message); }

        bool? verified = after.Ok ? ValuesEquivalent(value, after.Value) : null;
        string readBack = verified switch
        {
            true => $"verified by read-back ({after.Value})",
            false => $"WARNING: read-back shows {after.Value} — differs from the written value (the PLC may have " +
                     "changed it already)",
            null => $"read-back not possible ({after.Error})",
        };
        string summary = $"Wrote {value} to '{name}' in '{project.Name}'" +
                         (writesToPlc ? " (through to the PLC)" : "") +
                         $" — {Describe(outcome)}; {readBack}.";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            symbol = name,
            written = value,
            before = before.Ok ? before.Value : null,
            after = after.Ok ? after.Value : null,
            verified,
            writesToPlc,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// Compares a written JSON value with the read-back text of ITcHmiSymbol.Value,
    /// which is NOT JSON for scalars (live: a BOOL reads "False"). null = not comparable.
    /// </summary>
    internal static bool? ValuesEquivalent(string writtenJson, string? readBack)
    {
        if (readBack is null) return null;
        JsonElement written;
        try
        {
            using var doc = JsonDocument.Parse(writtenJson);
            written = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }

        string text = readBack.Trim();
        switch (written.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return bool.TryParse(text, out bool b) ? b == (written.ValueKind == JsonValueKind.True) : false;
            case JsonValueKind.Number:
                return decimal.TryParse(text, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out decimal n)
                       && written.TryGetDecimal(out decimal w)
                    ? n == w
                    : false;
            case JsonValueKind.String:
                return text == written.GetString() || text == written.GetRawText();
            case JsonValueKind.Null:
                return text.Length == 0 || text.Equals("null", StringComparison.OrdinalIgnoreCase);
            default:
                try
                {
                    return System.Text.Json.Nodes.JsonNode.DeepEquals(
                        System.Text.Json.Nodes.JsonNode.Parse(text),
                        System.Text.Json.Nodes.JsonNode.Parse(written.GetRawText()));
                }
                catch (JsonException) { return null; }
        }
    }

    internal enum MappingState { Mapped, NotMapped, Unknown }

    internal readonly record struct MappingProbe(MappingState State, string Evidence);

    /// <summary>
    /// Is <paramref name="mapName"/> mapped RIGHT NOW? Asks the running server, not the
    /// Automation Interface list: verified live 2026-09-25 that GetMappedSymbol(s) is a
    /// stale cache (after UnMapSymbol it still listed the symbol, even with refresh=true),
    /// while a read through the server answered SYMBOL_NOT_MAPPED (code 513) for the
    /// removed name and HMI_ADS_E_* for a still-mapped ADS symbol whose PLC was
    /// unreachable. Anything else is Unknown — callers fail closed on it.
    /// </summary>
    internal MappingProbe ProbeMapping(string hmiProject, string mapName)
    {
        IReadOnlyList<HmiSymbolValue> reads = session.ReadHmiSymbols(hmiProject, [mapName]);
        HmiSymbolValue? read = reads.Count > 0 ? reads[0] : null;
        if (read is null) return new(MappingState.Unknown, "the server gave no answer");
        if (read.Ok) return new(MappingState.Mapped, "the server returns a value");
        string error = read.Error ?? "";
        if (error.Contains("SYMBOL_NOT_MAPPED", StringComparison.Ordinal))
            return new(MappingState.NotMapped, "the server answers SYMBOL_NOT_MAPPED");
        if (error.Contains("HMI_ADS_E", StringComparison.Ordinal))
            return new(MappingState.Mapped, "the server knows the symbol; its ADS read failed");
        return new(MappingState.Unknown, string.IsNullOrWhiteSpace(error) ? "unreadable" : error);
    }

    private static string? TimeoutError(int seconds) =>
        seconds < MinConfirmTimeoutSeconds || seconds > MaxConfirmTimeoutSeconds
            ? $"[Error] confirmTimeoutSeconds must be between {MinConfirmTimeoutSeconds} and {MaxConfirmTimeoutSeconds}."
            : null;

    /// <summary>Fail-closed: anything but Confirmed/AutoConfirmed is a refusal.</summary>
    private (HmiConfirmOutcome Outcome, string? Refusal) AskEngineer(HmiWriteRequest request, int timeoutSeconds)
    {
        HmiConfirmOutcome outcome = confirmer is null
            ? HmiConfirmOutcome.Unavailable
            : confirmer.Confirm(request, TimeSpan.FromSeconds(timeoutSeconds));
        string? refusal = outcome switch
        {
            HmiConfirmOutcome.Confirmed or HmiConfirmOutcome.AutoConfirmed => null,
            HmiConfirmOutcome.Rejected =>
                "[Error] Not confirmed — the engineer rejected the change in the MCP host popup. Nothing was written.",
            HmiConfirmOutcome.TimedOut =>
                $"[Error] Not confirmed within {timeoutSeconds} s — the popup in the MCP host expired. Nothing was written.",
            _ => "[Error] No confirmation channel (the MCP host window is not available), so HMI writes are refused. " +
                 "Nothing was written.",
        };
        return (outcome, refusal);
    }

    private static string Describe(HmiConfirmOutcome outcome) => outcome == HmiConfirmOutcome.AutoConfirmed
        ? $"auto-confirmed (TEMP fixture, {HmiAutoConfirmPolicy.EnvVar}=1)"
        : "confirmed by the engineer";

    private static string ConfirmationWire(HmiConfirmOutcome outcome) =>
        outcome == HmiConfirmOutcome.AutoConfirmed ? "autoConfirmedTempFixture" : "confirmedByEngineer";

    /// <summary>Live, a map against an unreachable PLC runtime failed with LastError
    /// "server request" and nothing else — so the cause is stated as presumed only.</summary>
    private static string MapRefusalNote(string domain, string? lastError) =>
        domain.Equals(HmiSymbolNames.AdsDomain, StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(lastError) || lastError.Trim().Equals("server request", StringComparison.OrdinalIgnoreCase))
            ? " Note: presumably the PLC runtime of the ADS extension is not reachable or the symbol does not exist " +
              "in the PLC — the server gave no reason. Not verified by this tool. " + TmcHint
            : "";

    /// <summary>The offline route out of "no PLC, no symbols".
    /// Deliberately does NOT promise that this makes MapSymbol succeed — mapping goes through
    /// the running server, and that was not shown.</summary>
    internal const string TmcHint =
        "No PLC available? twincat_hmi_list_ads_runtimes shows which NetId/port this runtime points at, and " +
        "twincat_hmi_attach_tmc_file attaches the PLC project's TMC file so the HMI ENGINEERING knows the " +
        "symbols offline (twincat_hmi_list_plc_tmc_candidates lists the .tmc files of the solution's PLC projects). " +
        "That does NOT make a mapping work without a PLC: the HMI server has no 'TMC' domain (verified live, #160). " +
        "The server-side route is twincat_hmi_enable_runtime_tmc: the ADS runtime then serves the PLC symbols " +
        "from the TMC file (#161, not yet verified live).";

    /// <summary>Wording is deliberate: the HMI_ADS_E_* codes say the HMI server could
    /// not talk to the PLC runtime — which runtime/NetId is unreachable is not proven.</summary>
    internal const string AdsRuntimeNote =
        "Note: HMI_ADS_E_* comes from the HMI server's ADS extension — presumably the PLC runtime " +
        "configured under Server → ADS → Runtimes (NetId/port) is not reachable. Not verified by this tool. " +
        TmcHint;

    // ---- project resolution ----------------------------------------------------

    /// <summary>
    /// 0 projects → error; name omitted with exactly 1 → that one; omitted with
    /// several → error listing the names (no fallback); unknown name → error;
    /// still loading → error. Match is case-insensitive on the reported name.
    /// </summary>
    internal (HmiProjectEntry? Project, string? Error) ResolveProject(string? hmiProject, bool requireReady = true)
    {
        HmiProjectList list;
        try { list = session.ListHmiProjects(); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (null, WithDialogHint($"[Error] {ex.Message}")); }

        if (list.Projects.Count == 0)
            return (null, "[Error] The open solution contains no TwinCAT HMI project (.hmiproj).");

        HmiProjectEntry? match;
        if (string.IsNullOrWhiteSpace(hmiProject))
        {
            if (list.Projects.Count > 1)
                return (null, $"[Error] The solution contains {list.Projects.Count} HMI projects — " +
                              $"pass hmiProject: {Names(list)}.");
            match = list.Projects[0];
        }
        else
        {
            string wanted = hmiProject.Trim();
            match = list.Projects.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return (null, $"[Error] HMI project '{wanted}' not found. Available: {Names(list)}.");
        }

        if (requireReady && !match.IsReady)
        {
            string loading = $"[Error] HMI project '{match.Name}' is still loading (IsReady=false) — retry in a moment.";
            return (null, list.BlockingDialog is null ? loading : loading + " " + DialogHint(list.BlockingDialog));
        }
        return (match, null);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>The exception types a failing Automation-Interface call surfaces as:
    /// session errors, the not-built stub, COM/RPC failures and a failed QI.</summary>
    internal static bool IsHmiFailure(Exception ex) =>
        ex is InvalidOperationException or PlatformNotSupportedException
            or COMException or InvalidCastException;

    internal const int MaxServerErrorLength = 300;

    /// <summary>
    /// ITcHmiError.LastError of a server request is the headline plus a full dump of
    /// the request ("Symbol: …", "Complete Command: { … sessionId … }") — seen live.
    /// Keep the headline (code/message/reason) only: it is what an agent can act on,
    /// and the dump leaks the server session id into logs.
    /// </summary>
    internal static string CondenseServerError(string lastError)
    {
        string first = lastError
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";
        return first.Length <= MaxServerErrorLength ? first : first[..MaxServerErrorLength] + "…";
    }

    internal static string DialogHint(string dialogTitle) =>
        Win32DialogAutomation.IsHmiMigrationText(dialogTitle)
            ? $"Note: the XAE asks '{dialogTitle}' — the engineer must decide in the XAE whether to migrate; " +
              "declining leaves the HMI project unloaded. The MCP never answers it."
            : $"Note: the XAE shows '{dialogTitle}' — the engineer must answer it (CAPTCHA) " +
              "before the HMI engineering server works; the MCP never fills it in.";

    private string WithDialogHint(string error) =>
        session.FindHmiBlockingDialog() is { } dialog ? error + " " + DialogHint(dialog) : error;

    private static string Describe(HmiProjectEntry p) =>
        p.IsReady ? p.Name : $"{p.Name} (loading)";

    private static string Names(HmiProjectList list) =>
        string.Join(", ", list.Projects.Select(p => p.Name));

    private static string Config(HmiProjectInfo info, string key) =>
        info.Config.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v : "-";
}
