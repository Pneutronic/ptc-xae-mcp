using System.Globalization;
using System.Text.Json;
using PtcXaeMcp.WinForms.Security;

namespace PtcXaeMcp.WinForms.TwinCat;

/// <summary>
/// #161 — the SERVER-side TMC: an ADS runtime of the HMI
/// server serves its PLC symbols from a binary symbol segment converted from the PLC project's .tmc,
/// so mapping and reading PLC symbols works with NO PLC (the attached engineering-side TMC of
/// <see cref="AttachTmcFile"/> does not do that — the server has no "TMC" domain, #160).
/// <para>What TE2000 does (decompiled 14.17.22.1), and therefore what this does:</para>
/// <list type="number">
/// <item>convert the .tmc with TwinCAT's type system into <c>*.DATA_UPLOADINFO2.bin</c>
///   (TmcLightPipeline → TmcParser + TmcResultExporter; here <see cref="TmcUploadInfoExporter"/>),</item>
/// <item>put it into <c>Tmc\</c> below the engineering server's working directory
///   (<c>.engineering_servers\&lt;HmiProject&gt;</c>),</item>
/// <item>write <c>ADS.Config::RUNTIMES::&lt;rt&gt;::TMC::DATA_SYMBOLUPANDDOWNLOAD</c> = that path relative
///   to the working directory, then <c>…::USE_TMC</c> = true (TcHmiServerCommunication.AttachTmcFile).</item>
/// </list>
/// TE2000 only does this for a NEW project, automatically, through an XAE-internal TMC stream; no
/// dialog offers it. Success is decided by reading <c>ADS.Config::RUNTIMES</c> back — never by the
/// write calls' own answers. Verified live 2026-11-18 (engineering 14.17.22.1, engineering server
/// 24.0.7.0): offline mapping works under the final names (<c>ADS.&lt;rt&gt;.*</c>, schema
/// <c>tchmi:server#/definitions/ADS-&lt;rt&gt;.&lt;Type&gt;</c>), values need the PLC; after
/// <see cref="DisableRuntimeTmc"/> the same mappings read from the real PLC without re-mapping.
/// </summary>
internal sealed partial class HmiCoordinator
{
    /// <summary>The conversion. A seam so tests decide what TwinCAT's type system "produced" — and can
    /// prove that a refused file or runtime never gets this far.</summary>
    internal Func<string, string, (TmcUploadInfo? Info, string? Error)> UploadInfoConverter { get; init; } =
        TmcUploadInfoExporter.Convert;

    /// <summary>
    /// Fixed, fail-closed order: runtime name → tmcFile against <see cref="PathPolicy"/> → identify
    /// the file → resolve the project → the runtime must exist in the live runtime table → find the
    /// engineering server's working directory → convert (before the popup, so the engineer sees the
    /// real result and a file TwinCAT cannot read never reaches the popup) → popup → write the .bin →
    /// the two config writes → read-back of <c>ADS.Config::RUNTIMES</c>.
    /// </summary>
    public (string Summary, string Json) EnableRuntimeTmc(
        string? hmiProject, string? runtime, string? tmcFile, int confirmTimeoutSeconds)
    {
        string rt = runtime?.Trim() ?? "";
        if (!HmiSymbolNames.IsValidRuntime(rt))
            return (RuntimeRequired, "{}");

        // Same file rule as attach (#156 item 7): checked against the allowed roots before it is opened.
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
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (runtimes, readError) = ReadAdsRuntimes(project.Name);
        if (runtimes is null) return (readError!, "{}");
        var (current, missing) = FindRuntime(runtimes, rt, project.Name);
        if (current is null) return (missing!, "{}");

        var (serverFolder, folderError) = EngineeringServerFolder(project);
        if (serverFolder is null) return (folderError!, "{}");
        string binPath = Path.Combine(serverFolder, HmiRuntimeTmc.ExchangeFolder,
            HmiRuntimeTmc.UploadInfoFileName(current.Name, facts.Path));
        string? relative = HmiRuntimeTmc.RelativeToServerFolder(serverFolder, binPath);
        if (relative is null)
            return ($"[Error] '{binPath}' does not lie below the engineering server folder '{serverFolder}' — " +
                    "nothing was written.", "{}");

        var (info, convertError) = UploadInfoConverter(facts.Path, Path.GetFileNameWithoutExtension(facts.Path));
        if (info is null)
            return ($"[Error] '{facts.Path}' could not be converted into the ADS symbol segment the HMI server " +
                    $"reads ({convertError}). Nothing was written.", "{}");

        var request = new HmiWriteRequest(HmiWriteKind.EnableRuntimeTmc, project.Name, project.ProjectDirectory,
            current.Name, HmiSymbolNames.AdsDomain,
            NewValue: $"{HmiRuntimeTmc.UseTmc}=true, {HmiRuntimeTmc.DataSymbolUpAndDownload}={relative} — " +
                      $"converted from {facts.Path} ({facts.SymbolCount} symbols in the TMC, " +
                      $"{info.Payload.Length.ToString(CultureInfo.InvariantCulture)} bytes); the HMI server then " +
                      "serves this runtime's PLC symbols WITHOUT a PLC connection",
            CurrentValue: DescribeTmcState(current),
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        try { HmiRuntimeTmc.WriteAtomically(binPath, info.Payload); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"[Error] Writing '{binPath}' failed ({ex.Message}). The server configuration was not changed.", "{}");
        }

        HmiOperationResult result;
        // Live 2026-11-18: a runtime without a TMC object refuses both leaf writes — write the object whole.
        try { result = Session.SetHmiRuntimeTmc(project.Name, current.Name, relative, useTmc: true,
                  createTmcObject: !current.HasTmcConfig); }
        catch (Exception ex) when (IsHmiFailure(ex))
        {
            return (WithDialogHint($"[Error] Writing {RuntimeTmcSymbol(current.Name)} failed: {ex.Message.TrimEnd('.')}. " +
                                   AfterFailedWrite(project.Name, current.Name)), "{}");
        }
        if (!result.Ok)
            return ($"[Error] The HMI server refused the TMC setting of runtime '{current.Name}': " +
                    $"{result.LastError ?? "no error text"}. {AfterFailedWrite(project.Name, current.Name)}", "{}");

        var (after, afterError) = ReadAdsRuntimes(project.Name);
        if (after is null)
            return ($"[Error] The TMC setting of runtime '{current.Name}' was written, but the read-back failed " +
                    $"({WithoutErrorPrefix(afterError)}) — whether it took effect is unknown. Check with " +
                    "twincat_hmi_list_ads_runtimes.", "{}");
        HmiAdsRuntime? landed = after.FirstOrDefault(r => r.Name.Equals(current.Name, StringComparison.Ordinal));
        if (landed is null || !landed.UseTmc || !HmiRuntimeTmc.SamePath(landed.TmcFile, relative))
            return ($"[Error] The server accepted the writes, but {AdsRuntimesSymbol} reads back " +
                    $"{(landed is null ? "no runtime '" + current.Name + "'" : DescribeTmcState(landed))} — expected " +
                    $"{HmiRuntimeTmc.UseTmc}=true with '{relative}'. Not treated as success.", "{}");

        string summary = $"ADS runtime '{landed.Name}' of '{project.Name}' now takes its PLC symbols from " +
                         $"{facts.Path} ({facts.SymbolCount} symbols) instead of an ADS upload — " +
                         $"{Describe(outcome)}, verified in {AdsRuntimesSymbol} " +
                         $"({HmiRuntimeTmc.UseTmc}=true, {HmiRuntimeTmc.DataSymbolUpAndDownload}={landed.TmcFile}). " +
                         $"Converted file: {binPath} ({info.Payload.Length.ToString(CultureInfo.InvariantCulture)} bytes). " +
                         "Whether the ADS extension already serves the symbols is not checked by this tool " +
                         "(read one, e.g. with twincat_hmi_read_symbols). Reversible with twincat_hmi_disable_runtime_tmc.";
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            runtime = landed.Name,
            useTmc = true,
            dataSymbolUpAndDownload = landed.TmcFile,
            binFile = binPath,
            binSizeBytes = info.Payload.Length,
            engineeringServerFolder = serverFolder,
            tmcFile = facts.Path,
            tmcSymbolCount = facts.SymbolCount,
            module = info.ModuleName,
            nativeLibrary = info.NativeLibrary,
            nativeVersion = info.NativeVersion,
            previous = new { useTmc = current.UseTmc, tmcFile = current.TmcFile, hasTmcConfig = current.HasTmcConfig },
            createdTmcObject = !current.HasTmcConfig,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    /// <summary>
    /// TE2000's DetachTmcFile: <c>DATA_SYMBOLUPANDDOWNLOAD</c> = "" then <c>USE_TMC</c> = false. A runtime
    /// that already has neither → "Nothing to do" without a popup (decision E8). The converted file is
    /// left on disk — it lies in the engineering server's scratch folder, which TE2000 git-ignores.
    /// </summary>
    public (string Summary, string Json) DisableRuntimeTmc(string? hmiProject, string? runtime, int confirmTimeoutSeconds)
    {
        string rt = runtime?.Trim() ?? "";
        if (!HmiSymbolNames.IsValidRuntime(rt))
            return (RuntimeRequired, "{}");
        if (TimeoutError(confirmTimeoutSeconds) is { } timeoutError) return (timeoutError, "{}");

        string? error = SessionGuards.RequireOpenSolution(Session);
        if (error is not null) return (error, "{}");
        var (project, resolveError) = ResolveProject(hmiProject);
        if (project is null) return (resolveError!, "{}");

        var (runtimes, readError) = ReadAdsRuntimes(project.Name);
        if (runtimes is null) return (readError!, "{}");
        var (current, missing) = FindRuntime(runtimes, rt, project.Name);
        if (current is null) return (missing!, "{}");

        if (!current.UseTmc && current.TmcFile is null)
            return ($"Nothing to do: ADS runtime '{current.Name}' of '{project.Name}' does not use a TMC file " +
                    $"({DescribeTmcState(current)}).",
                JsonSerializer.Serialize(
                    new { hmiProject = project.Name, runtime = current.Name, useTmc = false, changed = false }, JsonOpts));

        var request = new HmiWriteRequest(HmiWriteKind.DisableRuntimeTmc, project.Name, project.ProjectDirectory,
            current.Name, HmiSymbolNames.AdsDomain,
            NewValue: $"{HmiRuntimeTmc.UseTmc}=false, {HmiRuntimeTmc.DataSymbolUpAndDownload}=\"\" — the runtime " +
                      $"uploads its symbols from the PLC again ({AdsTarget(current)}); without a reachable PLC it " +
                      "then has no symbols",
            CurrentValue: DescribeTmcState(current),
            WritesToPlc: false);
        var (outcome, refusal) = AskEngineer(request, confirmTimeoutSeconds);
        if (refusal is not null) return (refusal, "{}");

        HmiOperationResult result;
        try { result = Session.SetHmiRuntimeTmc(project.Name, current.Name, "", useTmc: false); }
        catch (Exception ex) when (IsHmiFailure(ex))
        {
            return (WithDialogHint($"[Error] Writing {RuntimeTmcSymbol(current.Name)} failed: {ex.Message.TrimEnd('.')}. " +
                                   AfterFailedWrite(project.Name, current.Name)), "{}");
        }
        if (!result.Ok)
            return ($"[Error] The HMI server refused to reset the TMC setting of runtime '{current.Name}': " +
                    $"{result.LastError ?? "no error text"}. {AfterFailedWrite(project.Name, current.Name)}", "{}");

        var (after, afterError) = ReadAdsRuntimes(project.Name);
        if (after is null)
            return ($"[Error] The TMC setting of runtime '{current.Name}' was reset, but the read-back failed " +
                    $"({WithoutErrorPrefix(afterError)}) — whether it took effect is unknown. Check with " +
                    "twincat_hmi_list_ads_runtimes.", "{}");
        HmiAdsRuntime? landed = after.FirstOrDefault(r => r.Name.Equals(current.Name, StringComparison.Ordinal));
        if (landed is null || landed.UseTmc || landed.TmcFile is not null)
            return ($"[Error] The server accepted the writes, but {AdsRuntimesSymbol} reads back " +
                    $"{(landed is null ? "no runtime '" + current.Name + "'" : DescribeTmcState(landed))} — expected " +
                    $"{HmiRuntimeTmc.UseTmc}=false without a file. Not treated as success.", "{}");

        string summary = $"ADS runtime '{landed.Name}' of '{project.Name}' no longer uses a TMC file — " +
                         $"{Describe(outcome)}, verified in {AdsRuntimesSymbol}. It uploads its symbols from the PLC " +
                         $"again ({AdsTarget(landed)})" +
                         (current.TmcFile is null ? "." : $"; the converted file ({current.TmcFile}) was left on disk.");
        string json = JsonSerializer.Serialize(new
        {
            hmiProject = project.Name,
            runtime = landed.Name,
            useTmc = false,
            changed = true,
            previousFile = current.TmcFile,
            previousUseTmc = current.UseTmc,
            netId = landed.NetId,
            port = landed.Port,
            confirmation = ConfirmationWire(outcome),
        }, JsonOpts);
        return (summary, json);
    }

    // ---- helpers -------------------------------------------------------------

    private const string RuntimeRequired =
        "[Error] runtime is required — the name of an ADS runtime of the HMI server, a key under " +
        "ADS.Config::RUNTIMES such as 'PLC1' (letters, digits and '_'; twincat_hmi_list_ads_runtimes lists them).";

    private static string RuntimeTmcSymbol(string runtime) => $"{AdsRuntimesSymbol}::{runtime}::TMC";

    /// <summary>Exact match only: runtime names are config keys of the server, and the symbol paths
    /// written below are built from the name as the server reports it.</summary>
    private static (HmiAdsRuntime? Runtime, string? Error) FindRuntime(
        IReadOnlyList<HmiAdsRuntime> runtimes, string runtime, string hmiProject)
    {
        HmiAdsRuntime? exact = runtimes.FirstOrDefault(r => r.Name.Equals(runtime, StringComparison.Ordinal));
        if (exact is not null) return (exact, null);
        HmiAdsRuntime? other = runtimes.FirstOrDefault(r => r.Name.Equals(runtime, StringComparison.OrdinalIgnoreCase));
        string configured = runtimes.Count == 0 ? "none" : string.Join(", ", runtimes.Select(r => r.Name));
        return (null, $"[Error] The ADS extension of '{hmiProject}' has no runtime '{runtime}'" +
                      (other is null ? "" : $" — did you mean '{other.Name}'? Runtime names are case-sensitive") +
                      $" (configured: {configured}). This tool switches an existing runtime; it does not create one.");
    }

    /// <summary>The engineering server's working directory, or a ready-to-return error. Only an
    /// EXISTING folder counts — the server creates it on start, and the ADS extension resolves the
    /// written path against it.</summary>
    private (string? Folder, string? Error) EngineeringServerFolder(HmiProjectEntry project)
    {
        string? projectFile = HmiTmcPaths.FindProjectFile(project.ProjectDirectory, project.Name);
        IReadOnlyList<string> candidates = HmiRuntimeTmc.ServerFolderCandidates(project.ProjectDirectory, project.Name,
            HmiRuntimeTmc.ReadServerWorkingDirectory(projectFile), SolutionDirectory());
        string? folder = candidates.FirstOrDefault(Directory.Exists);
        return folder is not null
            ? (folder, null)
            : (null, $"[Error] The engineering server folder of '{project.Name}' was not found (looked for " +
                     (candidates.Count == 0 ? "nothing — no project or solution directory is known" :
                         string.Join(", ", candidates.Select(c => $"'{c}'"))) +
                     "). The HMI engineering server creates it when it starts (twincat_hmi_server_control " +
                     "action=start); the converted file must lie below it. Nothing was written.");
    }

    private static string DescribeTmcState(HmiAdsRuntime r) => r.UseTmc
        ? $"{HmiRuntimeTmc.UseTmc}=true ({r.TmcFile ?? "no file configured"})"
        : $"{HmiRuntimeTmc.UseTmc}=false — symbols from the PLC over {AdsTarget(r)}" +
          (r.TmcFile is null ? "" : $" (a file is still configured: {r.TmcFile})");

    private static string AdsTarget(HmiAdsRuntime r) =>
        $"ADS {r.NetId ?? "?"}:{r.Port?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

    /// <summary>A failed write may have changed one of the two leaves already — say what the server
    /// holds now instead of guessing.</summary>
    private string AfterFailedWrite(string hmiProject, string runtime)
    {
        var (runtimes, _) = ReadAdsRuntimes(hmiProject);
        HmiAdsRuntime? now = runtimes?.FirstOrDefault(r => r.Name.Equals(runtime, StringComparison.Ordinal));
        return now is null
            ? "What the server holds now could not be read — check with twincat_hmi_list_ads_runtimes."
            : $"The server now holds: {DescribeTmcState(now)}.";
    }
}
