namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed record BuildError(
    string Level,
    string Description,
    string? File,
    int? Line);

// Evidence that a translation actually ran: the newest compile artifact and how
// old it is. AgeSeconds is what makes a stale "Build succeeded" visible — an
// artifact that is minutes old did not come from the build that just returned.
internal sealed record BuildArtifactInfo(
    string Path,
    DateTime LastWriteUtc,
    double AgeSeconds);

internal sealed record BuildResult(
    bool Success,
    int FailedProjects,
    IReadOnlyList<BuildError> Errors,
    string Log = "",
    // The compiler's own summary line from THIS build ("N errors, M warnings"),
    // or null when the build produced no such line — which is itself the signal
    // that nothing was translated.
    string? CompilerSummary = null,
    BuildArtifactInfo? Artifact = null,
    // Issue #152 C-10: why the build does not count as succeeded (null when it does).
    string? FailureReason = null,
    // How the result was produced, when that matters to the caller (e.g. which way "Check all objects" ran).
    string? Note = null);

internal sealed record ActivateResult(
    bool Success,
    string Message,
    // Which radio the "Applikation … seit dem letzten Download geändert" login
    // modal was confirmed with ("none" when no modal appeared / autoConfirmLogin
    // was off, "online_change", "download", or "login_without_change").
    string LoginModalHandled = "none",
    // Post-activation PLC online state (null when not probed, i.e. autoConfirmLogin off).
    bool? PlcOnline = null,
    string? RunState = null);

internal sealed record ErrorListClearResult(
    bool Success,
    int ClearedCount,
    string Message,
    int RemainingCount = -1);

// ---- save-project verification ----
// One document that was dirty before the save, with the evidence that decides
// whether it really reached the disk: the file's timestamp before and after.
internal sealed record SavedDocument(
    string Path,
    bool SavedFlagAfter,
    DateTime? MtimeBefore,
    DateTime? MtimeAfter)
{
    public bool FileChanged => MtimeAfter is not null && MtimeAfter != MtimeBefore;
    public bool Flushed => SavedFlagAfter && FileChanged;
}

internal sealed record SaveProjectResult(
    bool Success,
    string? SolutionPath,
    IReadOnlyList<SavedDocument> Documents,
    string Message);

// ---- build-recovery ----

internal sealed record LogoutPlcResult(bool WasOnline, bool? IsOnlineNow, string Message);

internal sealed record CleanResult(bool Success, string Message);

internal sealed record RebuildResult(
    bool Success,
    int FailedProjects,
    IReadOnlyList<BuildError> Errors,
    string Log = "",
    string? CompilerSummary = null,
    BuildArtifactInfo? Artifact = null,
    // Issue #152 C-10: why the build does not count as succeeded (null when it does).
    string? FailureReason = null);

// ---- plc-login ----

internal enum LoginMode { OnlineChange, Download, LoginWithoutChange }

internal sealed record LoginPlcResult(
    bool Success,
    string Mode,
    string DialogHandled,
    bool? IsOnlineNow,
    string? RunState,
    string Message);

// ---- runtime-activation ----

internal enum LicenseStatus { Present, Trial, Expired, Missing, Unknown }

internal sealed record LicenseEntry(
    string Id,
    string Name,
    LicenseStatus Status,
    DateTime? ExpiryDate,
    string? OrderNo = null,
    int? Count = null,
    int? Used = null,
    int? ResultCode = null);

internal sealed record LicenseInfoResult(
    IReadOnlyList<LicenseEntry> Licenses,
    string RawXml);

internal sealed record RequiredLicense(
    string LicenseId,
    string? OrderNo,
    string? Description);

internal sealed record RequiredLicensesResult(
    IReadOnlyList<RequiredLicense> Licenses,
    string RawXml);

internal sealed record AddRequiredLicenseResult(
    bool Added,
    bool WasAlreadyPresent,
    IReadOnlyList<RequiredLicense> RequiredLicenses,
    string Message);

internal sealed record RemoveRequiredLicenseResult(
    bool Removed,
    bool WasAbsent,
    IReadOnlyList<RequiredLicense> RequiredLicenses,
    string Message);
