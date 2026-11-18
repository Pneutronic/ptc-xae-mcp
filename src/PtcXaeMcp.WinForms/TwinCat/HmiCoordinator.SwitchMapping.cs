using System.Text.Json;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// hmi-offline-types: "Switch Mapping to PLC" for mappings in the engineering-only <c>TMC</c> domain (see
/// <see cref="HmiMappingSwitch"/>). Mappings made over the <c>USE_TMC</c> route are ADS mappings already and need no
/// switch — <see cref="DisableRuntimeTmc"/> moves them to the PLC (verified live 2026-11-18).
/// </summary>
internal sealed partial class HmiCoordinator
{
    internal const int MaxSwitchMappings = 200;

    internal sealed record SwitchOutcome(string MappedName, string From, string? To, string State, string? Detail);

    /// <summary>
    /// Fixed, fail-closed order: validate → resolve → the runtime must exist → collect the TMC-domain mappings
    /// (all, or the named ones) from their server config entries → popup → per mapping: rewrite the entry, read the
    /// mapped name through the server; the PLC (or its TMC file) does not know the symbol → the old entry is written
    /// back and the mapping is reported as refused.
    /// </summary>
    public (string Summary, string Json) SwitchMappingToPlc(
        string? hmiProject, string? runtime, IReadOnlyList<string>? mapNames, int confirmTimeoutSeconds)
    {
        string rt = runtime?.Trim() ?? "";
        if (!HmiSymbolNames.IsValidRuntime(rt)) return (RuntimeRequired, "{}");
        var names = (mapNames ?? []).Select(n => n?.Trim() ?? "").ToList();
        if (names.Any(n => n.Length == 0)) return ("[Error] mapNames must not contain empty names.", "{}");
        names = names.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > MaxSwitchMappings) return ($"[Error] At most {MaxSwitchMappings} mappings per call (got {names.Count}).", "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (runtimes, readError) = ReadAdsRuntimes(project.Name);
        if (runtimes is null) return (readError!, "{}");
        var (target, missing) = FindRuntime(runtimes, rt, project.Name);
        if (target is null) return (missing!, "{}");

        if (names.Count == 0)
        {
            try
            {
                names = Session.ListHmiMappedSymbols(project.Name, true)
                    .Where(s => s.Domain.Equals(TmcDomain, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.MappedName).Distinct(StringComparer.Ordinal).Take(MaxSwitchMappings + 1).ToList();
            }
            catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
            if (names.Count > MaxSwitchMappings)
                return ($"[Error] More than {MaxSwitchMappings} TMC mappings — pass mapNames in batches.", "{}");
        }
        if (names.Count == 0)
            return ($"Nothing to do: '{project.Name}' has no mapping in the {TmcDomain} domain. Mappings made while an ADS " +
                    "runtime used its TMC file (twincat_hmi_enable_runtime_tmc) are ADS mappings already — switch that " +
                    "runtime to the PLC with twincat_hmi_disable_runtime_tmc.",
                JsonSerializer.Serialize(new { hmiProject = project.Name, runtime = target.Name, switched = 0 }, JsonOpts));

        // Every entry is read and checked before the popup: nothing is written for a list with a bad member.
        var entries = new List<(string Name, string Entry, string From, string To)>();
        IReadOnlyList<HmiSymbolValue> reads;
        try { reads = Session.ReadHmiSymbols(project.Name, names.Select(HmiMappingSwitch.ConfigSymbol).ToList()); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return (WithDialogHint($"[Error] {ex.Message}"), "{}"); }
        foreach (string name in names)
        {
            HmiSymbolValue? read = reads.FirstOrDefault(r => r.Symbol == HmiMappingSwitch.ConfigSymbol(name));
            if (read is null || !read.Ok)
                return ($"[Error] The server configuration entry of '{name}' ({HmiMappingSwitch.ConfigSymbol(name)}) is not " +
                        $"readable ({read?.Error ?? "no answer"}) — nothing was written.", "{}");
            var parsed = HmiMappingSwitch.Parse(read.Value);
            if (parsed is null)
                return ($"[Error] The configuration entry of '{name}' is not a symbol entry — nothing was written.", "{}");
            if (!string.Equals(parsed.Value.Domain, TmcDomain, StringComparison.OrdinalIgnoreCase))
                return ($"[Error] '{name}' is mapped in domain '{parsed.Value.Domain ?? "?"}', not {TmcDomain} — only TMC mappings " +
                        "are switched. Nothing was written.", "{}");
            string? to = HmiMappingSwitch.Reroot(parsed.Value.Mapping, target.Name);
            if (to is null)
                return ($"[Error] The internal path of '{name}' ('{parsed.Value.Mapping ?? ""}') has no part below its root, so it " +
                        "cannot be moved to a runtime. Nothing was written.", "{}");
            entries.Add((name, read.Value!, parsed.Value.Mapping!, to));
        }

        var request = new HmiWriteRequest(HmiWriteKind.SwitchMappingToPlc, project.Name, project.ProjectDirectory,
            entries.Count == 1 ? entries[0].Name : $"{entries.Count} mappings",
            HmiSymbolNames.AdsDomain,
            NewValue: $"domain ADS, runtime '{target.Name}' ({AdsTarget(target)}): " +
                      string.Join("; ", entries.Take(5).Select(e => $"{e.Name} → {e.To}")) + (entries.Count > 5 ? $" … +{entries.Count - 5}" : "") +
                      " — mapped names stay, bindings are not touched",
            CurrentValue: $"domain {TmcDomain}: " + string.Join("; ", entries.Take(5).Select(e => e.From)),
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        var results = new List<SwitchOutcome>();
        foreach (var (name, entry, from, to) in entries)
            results.Add(SwitchOne(project.Name, name, entry, from, to, target.Name));

        int switched = results.Count(r => r.State == "switched");
        int offline = results.Count(r => r.State == "switchedUnverified");
        int refused = results.Count(r => r.State is "refused" or "failed" or "restoreFailed");
        string summary = $"Switch Mapping to PLC in '{project.Name}' → runtime '{target.Name}' — {Describe(outcome)}: " +
                         $"{switched + offline}/{results.Count} switched" +
                         (offline > 0 ? $" ({offline} NOT verified: runtime '{target.Name}' uses its TMC file and answers " +
                                        "HMI_ADS_E_USING_TMC for any name — read them after twincat_hmi_disable_runtime_tmc)" : "") +
                         (refused > 0 ? $", {refused} NOT switched: " + string.Join("; ", results.Where(r => r.State is "refused" or "failed" or "restoreFailed")
                             .Take(5).Select(r => $"{r.MappedName} ({r.Detail})")) : "") +
                         ". Mapped names are unchanged, so bindings keep working.";
        if (results.Any(r => r.State == "restoreFailed"))
            summary = "[Error] " + summary + " WARNING: at least one old entry could not be written back — check it with twincat_hmi_read_symbols.";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            runtime = target.Name,
            switched = switched + offline,
            verified = switched,
            notSwitched = refused,
            results,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    private SwitchOutcome SwitchOne(string hmiProject, string name, string entry, string from, string to, string runtime)
    {
        string? next = HmiMappingSwitch.Switched(entry, runtime);
        if (next is null) return new(name, from, to, "failed", "entry could not be rewritten");
        string config = HmiMappingSwitch.ConfigSymbol(name);

        HmiOperationResult write;
        try { write = Session.WriteHmiSymbol(hmiProject, config, next); }
        catch (Exception ex) when (IsHmiFailure(ex)) { return new(name, from, to, "failed", ex.Message); }
        if (!write.Ok) return new(name, from, to, "failed", write.LastError ?? "the server refused the entry");

        HmiSymbolValue? probe;
        try { probe = Session.ReadHmiSymbols(hmiProject, [name]).FirstOrDefault(); }
        catch (Exception ex) when (IsHmiFailure(ex)) { probe = new HmiSymbolValue(name, false, null, ex.Message); }
        string probeError = probe?.Error ?? "";
        if (probe?.Ok == true) return new(name, from, to, "switched", "reads a value from the PLC");
        // Live 2026-11-18: in TMC mode the ADS extension answers HMI_ADS_E_USING_TMC for EVERY name, also one that
        // does not exist — the switch is kept, but its target is not proven.
        if (probeError.Contains(UsingTmcCode, StringComparison.Ordinal))
            return new(name, from, to, "switchedUnverified",
                $"runtime '{runtime}' uses its TMC file (USE_TMC) and answers HMI_ADS_E_USING_TMC for any name — " +
                "whether the symbol exists is checked only with the PLC connected (twincat_hmi_disable_runtime_tmc, then read it)");

        // The PLC does not know the symbol (or the server could not tell): put the old entry back.
        bool unknown = probeError.Contains("HMI_ADS_E_INVALID_SYMBOL", StringComparison.Ordinal)
                       || probeError.Contains("HMI_ADS_E_INVALID_SYMBOL_TYPE", StringComparison.Ordinal);
        string reason = unknown ? $"the symbol does not exist in runtime '{runtime}'" : $"not verifiable: {probeError}";
        try
        {
            HmiOperationResult restore = Session.WriteHmiSymbol(hmiProject, config, entry);
            return restore.Ok
                ? new(name, from, to, "refused", reason + "; old TMC mapping restored")
                : new(name, from, to, "restoreFailed", reason + $"; restoring failed: {restore.LastError ?? "no error text"}");
        }
        catch (Exception ex) when (IsHmiFailure(ex)) { return new(name, from, to, "restoreFailed", reason + $"; restoring failed: {ex.Message}"); }
    }
}
