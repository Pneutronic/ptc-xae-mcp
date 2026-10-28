using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Schema;
using PtcXaeMcp.WinForms.TwinCat.Apply;
using PtcXaeMcp.WinForms.TwinCat.Tree;

namespace PtcXaeMcp.WinForms;

internal sealed class RunnerOptions
{
    public bool NoHw { get; init; }
    public bool NoGvl { get; init; }
    public bool NoLink { get; init; }
    public bool NoExport { get; init; }
    public bool AutoConfirmBuild { get; init; }
    // Explicit values (MCP tool parameters): win over the schema.
    public string PlcProjectNameOverride { get; init; } = string.Empty;
    public string MasterNameOverride { get; init; } = string.Empty;
    public string GvlNameOverride { get; init; } = string.Empty;

    // GUI selections: used only where the schema states no value, so a stale selection never
    // overrides the schema (issue #155).
    public string PlcProjectNameDefault { get; init; } = string.Empty;
    public string MasterNameDefault { get; init; } = string.Empty;
    public string GvlNameDefault { get; init; } = string.Empty;

    // brownfield-guard: fail-closed brownfield mode. When active, the empty-check,
    // master/device resolution and GVL-collision guard hard-abort on any uncertainty
    // instead of silently proceeding. Off by default to preserve greenfield onboarding.
    public bool FailClosed { get; init; }

    // brownfield-guard H2: exact target master, absolute tree path (e.g.
    // "TIID^Device 1 (EtherCAT)"). When set it resolves the master authoritatively and
    // implicitly activates fail-closed mode — no fuzzy/first-master fallback.
    public string TargetMasterPath { get; init; } = string.Empty;

    // brownfield-guard H4: when false, the full productive SolutionBuild.Build(true) in
    // RefreshPlcProcessImage is skipped so a brownfield apply does not re-bind the entire
    // existing PLC project. Default true preserves greenfield onboarding (Stufe 6).
    public bool AutoBuild { get; init; } = true;

    // issue #155 E-2: an existing GVL at the target path is replaced only when this is set (or
    // the schema's twincat.overwriteExistingGvl is true). Otherwise the apply stops with an
    // error naming the existing GVL, before any tree change.
    public bool OverwriteExistingGvl { get; init; }
}

internal sealed class LinkResult
{
    public string VarName { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Terminal { get; init; } = string.Empty;
    public int Channel { get; init; }
    public string Group { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string PlcPath { get; init; } = string.Empty;
    public string IoPath { get; init; } = string.Empty;
}

internal sealed class SchemaConfig
{
    [JsonPropertyName("twincat")]
    public TwinCatConfig TwinCat { get; init; } = new();

    [JsonPropertyName("ethercat")]
    public EtherCatConfig EtherCat { get; init; } = new();

    [JsonPropertyName("ioPoints")]
    public List<IoPoint> IoPoints { get; init; } = [];

    // Schema-validator R1 (UnmappedMemberHandling.Disallow): catches typos in fields that
    // matter (e.g. plcProjectNam) by throwing on any unmodeled JSON property. Free-form
    // top-level metadata some producers attach (e.g. "meta": { sourcePdf, generatedAt }) is
    // legitimate and not itself validated, so it is captured here rather than rejected.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

internal sealed class TwinCatConfig
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("plcProjectName")]
    public string PlcProjectName { get; set; } = string.Empty;

    [JsonPropertyName("gvlName")]
    public string GvlName { get; set; } = string.Empty;

    /// <summary>
    /// issue #155 E-2: replace an existing GVL of the same name at the target path (its
    /// declarations and links are lost). Default false: a collision is an error.
    /// </summary>
    [JsonPropertyName("overwriteExistingGvl")]
    public bool OverwriteExistingGvl { get; init; }

    [JsonPropertyName("linking")]
    public LinkingConfig Linking { get; init; } = new();

    [JsonPropertyName("export")]
    public ExportConfig Export { get; init; } = new();
}

internal sealed class LinkingConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("failIfUnresolved")]
    public bool FailIfUnresolved { get; init; } = true;
}

internal sealed class ExportConfig
{
    [JsonPropertyName("xtiPath")]
    public string XtiPath { get; init; } = string.Empty;

    [JsonPropertyName("mappingCsvPath")]
    public string MappingCsvPath { get; init; } = string.Empty;
}

internal sealed class EtherCatConfig
{
    [JsonPropertyName("root")]
    public EthercatNode Root { get; init; } = new();
}

internal sealed class EthercatNode
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("vInfo")]
    public string VInfo { get; init; } = string.Empty;

    [JsonPropertyName("previousPort")]
    public string? PreviousPort { get; init; }

    [JsonPropertyName("children")]
    public List<EthercatNode> Children { get; init; } = [];
}

internal sealed class IoPoint
{
    [JsonPropertyName("terminalName")]
    public string TerminalName { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    public int Channel { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("varName")]
    public string VarName { get; init; } = string.Empty;

    [JsonPropertyName("group")]
    public string? Group { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("ioLeaf")]
    public string? IoLeaf { get; init; }

    // ---- iolink-devices: link a GVL var to a named IO-Link PDO leaf ----
    /// <summary>IO-Link port number (1..4) on the master named by <see cref="TerminalName"/>.</summary>
    [JsonPropertyName("ioLinkPort")]
    public int? IoLinkPort { get; init; }

    /// <summary>
    /// Named IO-Link process-data entry to link to, e.g. <c>Valve 1 solenoid 00</c>.
    /// When set, the leaf is resolved under the master's IO-Link port instead of a
    /// numbered terminal <see cref="Channel"/>.
    /// </summary>
    [JsonPropertyName("ioLinkChannel")]
    public string? IoLinkChannel { get; init; }

    /// <summary>True when this point addresses a named IO-Link PDO entry rather than a terminal channel.</summary>
    [JsonIgnore]
    public bool IsIoLink => !string.IsNullOrWhiteSpace(IoLinkChannel);
}

internal static class StaTask
{
    public static Task Run(Action action, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // The runner's COM calls run on this STA thread: register the busy-retry message
            // filter here too (issue #149 A-5) — CoRegisterMessageFilter is per thread, and the
            // one in Program.Main only covers the GUI thread.
            using var comFilter = TwinCat.ComMessageFilter.Register();
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                action();
                tcs.TrySetResult(null);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}

internal static class ComInterop
{
    public static object GetActiveObject(string progId)
    {
        Guid clsid;
        try
        {
            CLSIDFromProgIDEx(progId, out clsid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CLSIDFromProgID(progId, out clsid);
        }

        GetActiveObject(ref clsid, IntPtr.Zero, out object obj);
        return obj;
    }

    /// <summary>
    /// True if a COM object for <paramref name="progId"/> is in the Running
    /// Object Table (i.e. that XAE host is up). Diagnostic only — never throws.
    /// </summary>
    public static bool IsProgIdRunning(string progId)
    {
        try { return GetActiveObject(progId) is not null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CLSIDFromProgIDEx(string progId, out Guid clsid);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = false)]
    private static extern void GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppunk);
}

internal sealed class NativeTwinCatRunner
{
    private readonly ITwinCatSession _session;
    private readonly Action<string> _log;
    private readonly CancellationToken _cancellationToken;

    // DTE of the attached XAE (issue #155 step 7: attached through the session's attach path).
    private object? _dte;

    // Step 8: answers the XAE modals raised by the apply build (started with the first build,
    // stopped at the end of the run: some appear only after Build(true) has returned).
    private ApplyBuildDialogWatcher? _dialogs;

    // issue #155 steps 3-6: the tree seam and the components built on it.
    private ComSysManTree? _tree;
    private ApplyTargetResolver? _targetResolver;
    private GvlWriter? _gvlWriter;
    // EtherCAT topology of this run (null when -NoHw skipped it).
    private EtherCatTopologyBuilder? _topology;

    // Exact GVL target of this run (null when the GVL folder is not resolved, e.g. -NoGvl).
    private GvlTarget? _gvlTarget;
    // Exact tree path of the resolved EtherCAT master.
    private string _masterPath = string.Empty;
    // Every Warn() call also lands here so the end-of-run ApplyReport can carry them (step 2).
    private readonly List<string> _warnings = [];

    // brownfield-guard: effective fail-closed mode (strict empty-check, exact master/PLC,
    // exact linking), the pinned master path and whether the productive build runs.
    private bool _failClosed;
    private string _targetMasterPath = string.Empty;
    private bool _autoBuild = true;

    private NativeTwinCatRunner(ITwinCatSession session, Action<string> log, CancellationToken cancellationToken)
    {
        _session = session;
        _log = log;
        _cancellationToken = cancellationToken;
    }

    public static Task RunAsync(ITwinCatSession session, string schemaPath, RunnerOptions options, Action<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return StaTask.Run(() => new NativeTwinCatRunner(session, log, cancellationToken).Run(schemaPath, options), cancellationToken);
    }

    /// <summary>
    /// Explicit tool parameter, then the schema's value, then the GUI's default. A stale GUI
    /// selection never overrides a value the schema states.
    /// </summary>
    internal static string Effective(string? explicitValue, string? schemaValue, string? guiDefault)
    {
        if (!string.IsNullOrWhiteSpace(explicitValue)) return explicitValue.Trim();
        if (!string.IsNullOrWhiteSpace(schemaValue)) return schemaValue.Trim();
        return guiDefault?.Trim() ?? string.Empty;
    }

    private void Run(string schemaPath, RunnerOptions options)
    {
        _warnings.Clear();
        _targetMasterPath = options.TargetMasterPath ?? string.Empty;
        _failClosed = BrownfieldGuard.IsStrict(options.FailClosed, _targetMasterPath);
        _autoBuild = options.AutoBuild;

        // Declared outside the try so the ApplyReport built after it can see the outcome of a
        // run that completed (possibly with warnings) rather than only a run that threw.
        LinkOutcome linked = new([], [], false);
        IReadOnlyList<string> dialogsAnswered = [];

        try
        {
            Info("Apply-PtcXaeMcp C# native runner");
            if (!File.Exists(schemaPath))
                throw new InvalidOperationException($"Schema JSON not found: {schemaPath}");

            var cfg = LoadAndValidateSchema(schemaPath);
            cfg.TwinCat.GvlName = Effective(options.GvlNameOverride, cfg.TwinCat.GvlName, options.GvlNameDefault);
            string requestedPlcName = Effective(options.PlcProjectNameOverride, cfg.TwinCat.PlcProjectName, options.PlcProjectNameDefault);
            string requestedMasterName = Effective(options.MasterNameOverride, cfg.TwinCat.DeviceName, options.MasterNameDefault);
            bool overwriteExistingGvl = options.OverwriteExistingGvl || cfg.TwinCat.OverwriteExistingGvl;
            ShowRunConfiguration(schemaPath, options, cfg, requestedPlcName, requestedMasterName, overwriteExistingGvl);

            Step("TwinCAT/XAE");
            Info("Connecting to TwinCAT XAE...");
            // E-10: the same precondition every mutating tool checks (one deterministic XAE,
            // reachable, a solution open), before anything is touched.
            string? blocked = SessionGuards.RequireOpenSolution(_session);
            if (blocked is not null)
                throw new InvalidOperationException(StripErrorPrefix(blocked));
            _dte = TwinCatComSession.AttachForExternalCaller(GetCandidateProgIds(), TimeSpan.FromSeconds(30));
            _tree = new ComSysManTree(TwinCatComSession.RequireSystemManagerOf(_dte), _cancellationToken);
            _targetResolver = new ApplyTargetResolver(_tree, Warn);
            _gvlWriter = new GvlWriter(_tree, Info, Warn);

            // E-25: export targets resolved against the schema file and allowed by PathPolicy
            // before the run changes anything.
            string solutionPath = _session.ActiveSolutionPath() ?? string.Empty;
            ApplyExportPaths exports = ApplyFiles.ResolveExports(cfg.TwinCat.Export.XtiPath, cfg.TwinCat.Export.MappingCsvPath,
                schemaPath, string.IsNullOrWhiteSpace(solutionPath) ? null : Path.GetDirectoryName(solutionPath));

            // E-15: exact PLC resolution; no fallback to "the first PLC" in fail-closed mode,
            // and never a guess between several PLCs.
            PlcTarget plcTarget = _targetResolver.ResolvePlc(requestedPlcName, _failClosed);
            cfg.TwinCat.PlcProjectName = plcTarget.PlcName;
            Info(string.Equals(plcTarget.PlcName, requestedPlcName, StringComparison.OrdinalIgnoreCase)
                ? $"PLC project: '{plcTarget.PlcName}'"
                : $"PLC project resolved: '{requestedPlcName}' -> '{plcTarget.PlcName}'");

            // Exact master (pinned path / exact name; lenient fallbacks only outside fail-closed).
            if (!string.IsNullOrWhiteSpace(_targetMasterPath))
                Info($"Target master path pinned (fail-closed): {_targetMasterPath}");
            ITreeNode master = _targetResolver.ResolveMaster(requestedMasterName, _targetMasterPath, _failClosed);
            _masterPath = master.Path.Length > 0 ? master.Path : $"TIID^{master.Name}";
            cfg.TwinCat.DeviceName = master.Name;
            Info(string.Equals(master.Name, requestedMasterName, StringComparison.OrdinalIgnoreCase)
                ? $"EtherCAT master: '{master.Name}'"
                : $"EtherCAT master resolved: '{requestedMasterName}' -> '{master.Name}'");

            // E-2 / E-15: resolve the exact GVL target and refuse a collision BEFORE anything in
            // the tree changes.
            _gvlTarget = ResolveGvlTarget(plcTarget, cfg.TwinCat.GvlName, required: !options.NoGvl);
            if (!options.NoGvl)
            {
                Info($"GVL target: {_gvlTarget!.GvlPath} (overwriteExistingGvl={overwriteExistingGvl})");
                _gvlWriter.CheckWritable(_gvlTarget, overwriteExistingGvl);
            }

            if (!options.NoHw)
            {
                Step("EtherCAT Tree");
                ApplyEthercatTree(master, cfg);
            }
            else
            {
                Info("Step skipped: EtherCAT Tree (-NoHw)");
            }

            if (!options.NoGvl)
            {
                Step($"GVL '{cfg.TwinCat.GvlName}'");
                EnsureGvl(_gvlTarget!, GvlDeclarationRenderer.Render(cfg.IoPoints), overwriteExistingGvl);
                if (cfg.TwinCat.Linking.Enabled)
                    RefreshPlcProcessImage(options.AutoConfirmBuild, cfg.TwinCat.PlcProjectName);
            }
            else
            {
                Info("Step skipped: GVL (-NoGvl)");
            }

            if (!options.NoLink && cfg.TwinCat.Linking.Enabled)
            {
                if (options.NoGvl)
                    RefreshPlcProcessImage(options.AutoConfirmBuild, cfg.TwinCat.PlcProjectName);

                Step($"Linking (failIfUnresolved={cfg.TwinCat.Linking.FailIfUnresolved})");
                // Step 6: exact symbols only (E-3), one LinkVariables per point (E-17), per-run caches (E-16).
                var linker = new Linker(_tree, new LinkTarget(cfg.TwinCat.PlcProjectName, cfg.TwinCat.GvlName, _masterPath,
                        _failClosed, _gvlTarget?.GvlPath ?? string.Empty),
                    cfg.EtherCat.Root, _topology?.CreatedPaths, Info, Warn, _cancellationToken);
                linked = linker.LinkAll(cfg.IoPoints, cfg.TwinCat.Linking.FailIfUnresolved);
            }
            else
            {
                Info(options.NoLink ? "Step skipped: Linking (-NoLink)" : "Step skipped: Linking (disabled)");
            }

            if (!options.NoExport)
            {
                Step("Export");
                ExportResults(cfg.TwinCat.DeviceName, exports, linked.Links);
            }
            else
            {
                Info("Step skipped: Export (-NoExport)");
            }
        }
        finally
        {
            _topology?.LogMissingSummary(Step);
            dialogsAnswered = StopDialogWatcher();
        }

        // E-4 / E-11: the run's overall pass/fail is decided once here, from the accumulated
        // ApplyReport, instead of individual call sites deciding for themselves.
        ApplyReport report = ApplyReport.Build(_topology?.MissingNodes ?? [], linked.Links, _warnings,
            linked.HadUnresolvedFailure, linked.Unresolved, dialogsAnswered);
        Step("Result");
        Info(report.Describe());
        if (!report.Ok)
            throw new InvalidOperationException("Apply failed: " + report.Describe());
    }

    // Stops the dialog watcher after a short tail (a box can follow the end of Build(true) by a
    // second or two) and logs what it answered; the warnings among them join the ApplyReport.
    private IReadOnlyList<string> StopDialogWatcher()
    {
        if (_dialogs is null) return [];
        _dialogs.Stop(TimeSpan.FromSeconds(3));
        foreach (string warning in _dialogs.Warnings) Warn($"XAE dialog answered: {warning}");
        foreach (string answered in _dialogs.Answered.Except(_dialogs.Warnings)) Info($"XAE dialog answered: {answered}");
        if (_dialogs.Blocker is string blocker) Warn($"XAE dialog left open (not answered by apply_schema): '{blocker}'");
        return _dialogs.Answered;
    }

    private static string StripErrorPrefix(string message) =>
        message.StartsWith("[Error] ", StringComparison.Ordinal) ? message["[Error] ".Length..] : message;

    // R1: the schema JSON is validated by one shared validator (SchemaModelValidator), also used
    // by SchemaValidation.Validate for the UI / twincat_validate_schema (was E-23).
    private static SchemaConfig LoadAndValidateSchema(string schemaPath)
    {
        string rawJson = File.ReadAllText(schemaPath, Encoding.UTF8);
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        List<SchemaCheckIssue> issues = SchemaModelValidator.Validate(doc.RootElement, rawJson);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid schema configuration:" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", issues.Select(i => i.Message)));
        }

        var cfg = JsonSerializer.Deserialize<SchemaConfig>(rawJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return cfg ?? throw new InvalidOperationException("Schema is empty.");
    }

    private void ShowRunConfiguration(string schemaPath, RunnerOptions options, SchemaConfig cfg,
        string plcName, string masterName, bool overwriteExistingGvl)
    {
        Step("Configuration");
        Info($"Schema JSON         : {schemaPath}");
        Info($"Build hardware      : {(options.NoHw ? "no (skip)" : "yes")}");
        Info($"Write GVL           : {(options.NoGvl ? "no (skip)" : "yes")}");
        Info($"Create links        : {(options.NoLink ? "no (skip)" : "yes")}");
        Info($"Write export        : {(options.NoExport ? "no (skip)" : "yes")}");
        Info($"Auto-confirm build  : {(options.AutoConfirmBuild ? "yes" : "no")}");
        Info($"Replace existing GVL: {(overwriteExistingGvl ? "yes (overwriteExistingGvl)" : "no (collision = error)")}");
        Info($"PLC target          : {(plcName.Length == 0 ? "<none>" : plcName)}");
        Info($"Master target       : {(masterName.Length == 0 ? "<none>" : masterName)}");
        Info($"GVL name            : {cfg.TwinCat.GvlName}");
    }

    private static IEnumerable<string> GetCandidateProgIds()
    {
        // Base order defined once in XaeHostResolver; the registry sweep only widens the list
        // with other installed XAE classes. The attach itself is the session's
        // (TwinCatComSession.AttachForExternalCaller: multi-XAE binding, Q2 guard, busy retry).
        var result = new List<string>(TwinCat.HostSelection.XaeHostResolver.LegacyAttachProbeOrder);

        using var classesRoot = Microsoft.Win32.Registry.ClassesRoot;
        foreach (string name in classesRoot.GetSubKeyNames())
        {
            if (name.StartsWith("TcXaeShell.DTE.", StringComparison.OrdinalIgnoreCase))
                result.Add(name);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void ApplyEthercatTree(ITreeNode master, SchemaConfig cfg)
    {
        Info($"Using existing device: {master.Name}");

        // brownfield-guard H1: in fail-closed mode read the count strictly so a COM error
        // surfaces instead of masquerading as an empty master.
        int childCount = BrownfieldGuard.ReadChildCount(() => master.ChildCount, _failClosed);
        BrownfieldGuard.AssertMasterEmpty(childCount, master.Name);

        Info("Creating EtherCAT topology...");
        _topology = new EtherCatTopologyBuilder(_tree!, Info, Warn, _cancellationToken);
        _topology.Build(master, cfg.EtherCat.Root);
        Info("EtherCAT topology created.");
    }

    // E-2: GvlWriter creates the GVL at the exact target path. An existing GVL there is a
    // collision error unless overwriteExistingGvl is set (or this run created it); a failed
    // CreateChild is reported, never answered with a delete.
    private void EnsureGvl(GvlTarget target, string declarationText, bool overwriteExistingGvl)
    {
        ITreeNode created = _gvlWriter!.Write(target, declarationText, overwriteExistingGvl);
        string gvlPath = created.Path.Length > 0 ? created.Path : target.GvlPath;

        AlwaysBindOutcome alwaysBind = _gvlWriter.SetAlwaysBind(gvlPath);
        if (alwaysBind.Verified)
        {
            Info(alwaysBind.Message);
            return;
        }

        // E-22: diagnostics under %LOCALAPPDATA%\ptc-xae-mcp\diagnostics, never next to the exe.
        string dump;
        try { dump = ApplyFiles.WriteDiagnostic($"{target.GvlName}.xml", alwaysBind.Xml); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { dump = $"<not written: {ex.Message}>"; }
        Warn($"{alwaysBind.Message} XML diagnostics: {dump}");
    }

    // E-15: the GVL folder is resolved exactly (ApplyTargetResolver). When the run does not write
    // a GVL (-NoGvl) the target is only used to find the existing GVL for linking, so a missing
    // folder is logged instead of failing the run.
    private GvlTarget? ResolveGvlTarget(PlcTarget plc, string gvlName, bool required)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gvlName))
                throw new InvalidOperationException("twincat.gvlName is empty.");
            return new GvlTarget(plc, _targetResolver!.ResolveGvlFolder(plc), gvlName.Trim());
        }
        // XaeBlockedException is an InvalidOperationException too: busy is never "no folder".
        catch (InvalidOperationException ex) when (!required && TwinCat.ComCall.Recoverable(ex))
        {
            Info($"GVL target not resolved (the GVL is not written): {ex.Message}");
            return null;
        }
    }

    /// <summary>Deadline for the apply build (the session's own build budget).</summary>
    private static readonly TimeSpan ApplyBuildTimeout = TimeSpan.FromMinutes(10);

    // Step 8 / R7: the productive build goes through the session's build path
    // (BuildCoordinator.BuildSolutionForApply: SolutionBuild.Build(true), LastBuildInfo, the
    // error list via DTE2) — the runner no longer has its own error-list reader. The XAE modals
    // that block Build(true) during apply are answered by _dialogs (ApplyBuildDialogPolicy) and
    // reported; an unknown one fails the run naming it instead of hanging.
    private void RefreshPlcProcessImage(bool autoConfirmBuild, string plcName)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        Step("PLC Build");
        if (!autoConfirmBuild)
            throw new InvalidOperationException("Interactive build confirmation is not supported by the native runner; enable AutoConfirmBuild.");

        // brownfield-guard H4: in a brownfield apply (autoBuild=false) the full productive
        // rebuild is deliberately skipped so the running PLC project is not re-bound.
        bool built = BrownfieldGuard.RunBuildIfEnabled(_autoBuild, () =>
        {
            Info("Refreshing the PLC process image (solution build started)...");
            _dialogs ??= new ApplyBuildDialogWatcher(XaeWindowScope.For(TwinCatComSession.XaeMainWindowOf(_dte!)));
            ApplyBuildOutcome outcome = new BuildCoordinator(_session).BuildSolutionForApply(_dialogs, ApplyBuildTimeout, _cancellationToken);
            _cancellationToken.ThrowIfCancellationRequested();
            string dialogs = ApplyReport.DescribeDialogs(outcome.DialogsAnswered).Trim();
            if (outcome.Error is not null)
                throw new InvalidOperationException($"{StripErrorPrefix(outcome.Error)} {dialogs}");
            BuildResult result = outcome.Result
                ?? throw new InvalidOperationException($"The build returned no result. {dialogs}");
            string verdict = BuildCoordinator.DescribeForApply(result);
            if (!result.Success)
                throw new InvalidOperationException($"Compile errors: {verdict} {dialogs}");
            Info($"{verdict} {dialogs}");
            ReloadReferencedPlcTmc(plcName);
        });

        if (!built)
        {
            Info("Full productive rebuild skipped on purpose (autoBuild=false): the existing PLC project is not " +
                 "re-bound. New IO reaches the process image only after a separate 'build_plc'.");
        }
    }

    // A referenced stand-alone PLC's instance keeps the process image of the TMC it was loaded from; after the
    // build regenerated that TMC it is reloaded here, before the linker looks for the new GVL (#14).
    private void ReloadReferencedPlcTmc(string plcName)
    {
        if (_tree is not ComSysManTree com || string.IsNullOrWhiteSpace(plcName)) return;
        IReadOnlyList<string> reloaded = com.Call(() => TwinCatComSession.ReloadReferencedPlcTmc(com.SysMan, plcName),
            "ReloadTmc of the referenced PLC instance");
        foreach (string instance in reloaded)
            Info($"Referenced stand-alone PLC project: reloaded the TMC into '{instance}' so the new GVL is in its process image.");
    }

    private void ExportResults(string deviceName, ApplyExportPaths exports, IReadOnlyList<LinkResult> results)
    {
        if (exports.XtiPath.Length > 0)
        {
            EnsureOutputFolder(exports.XtiPath);
            Info($"Exporting XTI: {exports.XtiPath}");
            ITreeNode ioRoot = _tree!.Lookup("TIID")
                ?? throw new InvalidOperationException("TwinCAT tree item 'TIID' not found; XTI not exported.");
            ioRoot.ExportChild(deviceName, exports.XtiPath);
        }

        if (exports.MappingCsvPath.Length > 0)
        {
            EnsureOutputFolder(exports.MappingCsvPath);
            Info($"Writing mapping CSV: {exports.MappingCsvPath}");
            File.WriteAllText(exports.MappingCsvPath, CsvExport.BuildMappingCsv(results), new UTF8Encoding(true));
        }
    }

    private static void EnsureOutputFolder(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);
    }

    private void Info(string message) => _log($"[{DateTime.Now:HH:mm:ss}] {message}");
    private void Warn(string message)
    {
        _warnings.Add(message);
        _log($"[{DateTime.Now:HH:mm:ss}] WARNING: {message}");
    }
    private void Step(string message) => _log($"[{DateTime.Now:HH:mm:ss}] --- {message} ---");
}
