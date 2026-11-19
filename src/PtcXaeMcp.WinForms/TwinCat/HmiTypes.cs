namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-server. COM-free records the session hands to
// HmiCoordinator — no TcHmiAutomation type ever crosses ITwinCatSession, so the
// coordinator, the MCP layer and the unit tests build without TE2000.

/// <summary>One TwinCAT HMI project (.hmiproj) of the open solution.</summary>
/// <param name="Name">ITcHmiProjectInformation.ProjectName — the handle every HMI tool takes.</param>
internal sealed record HmiProjectEntry(
    string Name,
    string ProjectDirectory,
    bool IsReady,
    bool IsPublishRunning);

/// <param name="AutomationId">The dte.GetObject id that produced the interface.</param>
/// <param name="AutomationVersion">ITcHmiAutomation.Version — surfaced so a TE2000
/// update that changes the (officially undocumented) API is visible.</param>
/// <param name="BlockingDialog">Title of an open "Activate Engineering Server Trial
/// License" window, or null.</param>
internal sealed record HmiProjectList(
    string AutomationId,
    int AutomationVersion,
    IReadOnlyList<HmiProjectEntry> Projects,
    string? BlockingDialog);

/// <summary>A server extension (domain) of the HMI engineering server, e.g. ADS.</summary>
internal sealed record HmiServerExtension(
    string DomainName,
    string State,
    bool Loaded,
    string? Version);

/// <summary>A node of the HMI project tree (ITcHmiItem).</summary>
internal sealed record HmiFileNode(
    string Name,
    string Path,
    int ChildCount,
    IReadOnlyList<HmiFileNode>? Children);

internal sealed record HmiProjectInfo(
    string Name,
    string ProjectDirectory,
    bool IsReady,
    bool IsPublishRunning,
    IReadOnlyDictionary<string, string?> Config,
    IReadOnlyList<HmiServerExtension> ServerExtensions,
    IReadOnlyList<HmiFileNode>? Files,
    bool FilesTruncated);

/// <summary>A symbol mapped in the HMI server (ITcHmiMappedSymbol).</summary>
internal sealed record HmiMappedSymbol(
    string MappedName,
    string Domain,
    string? Type,
    string? DataTypeDisplayName,
    bool ReadOnly,
    bool Hidden);

/// <param name="Value">The value exactly as ITcHmiSymbol.Value reports it. NOT
/// guaranteed JSON — live the server returns e.g. <c>False</c> for a boolean.</param>
internal sealed record HmiSymbolValue(
    string Symbol,
    bool Ok,
    string? Value,
    string? Error);

/// <param name="Settled">start: at least one extension Loaded; shutdown: none
/// Loaded; refresh_symbols: always true.</param>
internal sealed record HmiServerControlResult(
    string Action,
    bool Ok,
    bool Settled,
    string? Error,
    IReadOnlyList<HmiServerExtension> ExtensionsAfter);

/// <summary>Raw outcome of a mutating Automation-Interface call: its Boolean
/// return plus ITcHmiError.LastError when it returned false.</summary>
internal sealed record HmiOperationResult(bool Ok, string? LastError);

// ---- hmi-ads-tmc --------------------------------

/// <summary>
/// One ADS runtime of the HMI engineering server, as <c>ADS.Config::RUNTIMES</c>
/// reports it live. <paramref name="UseTmc"/>/<paramref name="TmcFile"/> come from the
/// per-runtime <c>TMC</c> object of TcHmiAds.Schema.json (both HideInEngineering, so
/// they never show in the XAE) — when UseTmc is true the extension takes the symbol
/// description from the TMC file INSTEAD of uploading it over ADS.
/// </summary>
/// <param name="SymbolCount">Entries under <c>SYMBOLS</c> (explicitly configured
/// symbols), not the number of PLC symbols.</param>
/// <param name="HasTmcConfig">The runtime carries a <c>TMC</c> object at all. Live 2026-11-18: a runtime
/// that never had one answers <c>INVALID_SUBSYMBOL</c> for <c>…::TMC</c>, and the server refuses a write
/// to a leaf below it — the object then has to be written as a whole.</param>
internal sealed record HmiAdsRuntime(
    string Name,
    bool Enabled,
    string? NetId,
    int? Port,
    bool ReadOnly,
    bool UseWhitelisting,
    int SymbolCount,
    bool UseTmc,
    string? TmcFile,
    bool HasTmcConfig = true);

/// <summary>What a .tmc file on disk turned out to be — read before anything is attached.</summary>
/// <param name="SymbolCount">Number of <c>&lt;Symbol&gt;</c> entries; 0 means the file
/// describes no PLC symbols (e.g. an event-class TMC like HMI_Error.tmc).</param>
internal sealed record TmcFileFacts(
    string Path,
    long SizeBytes,
    int SymbolCount,
    string? GeneratedBy);

/// <summary>
/// One TMC file attached to an HMI project, as the .hmiproj property
/// <c>HmiTmcFiles</c> stores it (verified by decompiling TE2000 14.17.22.1:
/// TcHmiServerSymbolRootLazy.TmcFiles is persisted through
/// <c>SetProjectProperty("HmiTmcFiles", …)</c>).
/// </summary>
/// <param name="RootNamespace">The "virtual PLC name". Symbol root of the attached file
/// in the engineering symbol tree.</param>
/// <param name="StoredFileName">Verbatim as the project file holds it — relative to the
/// SOLUTION directory when the TMC lies below it, absolute otherwise.</param>
/// <param name="PlcName">Set only when the attachment is tied to an existing ADS runtime,
/// which merges its symbols into that runtime's ADS namespace. The Automation Interface
/// passes null here, so an MCP-made attachment never has it.</param>
/// <param name="ResolvedPath">StoredFileName resolved against the solution directory.</param>
internal sealed record HmiTmcAttachment(
    string RootNamespace,
    string StoredFileName,
    string? PlcName,
    string ResolvedPath,
    bool FileExists);

/// <summary>
/// One PLC project of the open solution as the system manager describes it (#166): the
/// <c>TIPC</c> child's raw <c>ProduceXml</c> text, which carries
/// <c>TreeItem/PlcProjectDef/RelativeTmcPath</c>. COM-free; parsed by
/// <see cref="HmiTmcCandidates"/>.
/// </summary>
/// <param name="TcProject">Name of the TwinCAT (.tsproj) project in the solution.</param>
/// <param name="TcProjectFile">Full path of the .tsproj (<c>Project.FullName</c>) — the
/// directory RelativeTmcPath is relative to. Null when the XAE did not report it.</param>
/// <param name="PlcProject">Name of the TIPC child (the PLC project / instance).</param>
/// <param name="Xml">ProduceXml text, or null when it could not be read.</param>
/// <param name="Error">Why <paramref name="Xml"/> is null.</param>
internal sealed record PlcProjectXml(
    string TcProject,
    string? TcProjectFile,
    string PlcProject,
    string? Xml,
    string? Error);

/// <summary>
/// A PLC project's TMC file as a CANDIDATE for twincat_hmi_attach_tmc_file (#166). Never
/// attached automatically — which PLC belongs to which HMI is a project decision.
/// </summary>
/// <param name="RelativeTmcPath">Verbatim from PlcProjectDef, relative to the .tsproj directory.</param>
/// <param name="AbsolutePath">RelativeTmcPath resolved against the .tsproj directory.</param>
/// <param name="LastWriteUtc">Last write time of the file, when it exists.</param>
/// <param name="SymbolCount"><c>&lt;Symbol&gt;</c> entries counted by <see cref="TmcFile.Inspect"/>;
/// null when the file was not (or could not be) inspected.</param>
/// <param name="Note">Why a field is missing (no RelativeTmcPath, file missing, not inspected), else null.</param>
internal sealed record HmiTmcCandidate(
    string TcProject,
    string PlcProject,
    string? RelativeTmcPath,
    string? AbsolutePath,
    bool Exists,
    DateTime? LastWriteUtc,
    int? SymbolCount,
    string? Note);
