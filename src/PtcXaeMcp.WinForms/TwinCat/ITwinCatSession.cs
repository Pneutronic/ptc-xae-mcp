namespace PtcXaeMcp.WinForms.TwinCat;

internal interface ITwinCatSession : IDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// Why <see cref="IsConnected"/> is false: no TcXaeShell process, a process whose DTE is not
    /// reachable via COM, or an XAE held by a modal dialog (issue #149). Text without "[Error]".
    /// </summary>
    string DescribeUnreachable() => "No TwinCAT XAE instance is running.";

    bool HasOpenSolution();
    string? ActiveSolutionPath();
    TwinCatEnvironmentStatus Probe();

    /// <param name="hostOverride">Attach to / launch exactly this XAE host instead of the one the project's
    /// TcVersion selects; null keeps the TcVersion-based choice.</param>
    Task ConnectOrLaunchAsync(string? launchWithSlnPath, TimeSpan timeout, CancellationToken ct,
        HostSelection.XaeHostClass? hostOverride = null);
    /// <summary>
    /// Close the solution. With <paramref name="saveFirst"/> everything is saved explicitly first and the
    /// close does not prompt; a "save changes?" prompt the XAE still raises is answered "Speichern"
    /// (saveFirst) or "Nicht speichern" (see <see cref="SaveChangesDialog"/>).
    /// </summary>
    CloseSolutionResult CloseSolution(bool saveFirst);
    void OpenSolution(string slnPath);
    void SaveAll();
    /// <summary>
    /// Saves and then proves it against the FILES on disk (timestamps), not
    /// against the COM model the write went through. Replaces a verification
    /// that was circular by construction.
    /// </summary>
    SaveProjectResult SaveProjectVerified();

    IReadOnlyList<LibraryReference> ListLibraryReferences();
    IReadOnlyList<InstalledLibrary> ListInstalledLibraries();
    IReadOnlyList<LibraryReference> AddLibraryReference(string name, string version, string? company, bool asPlaceholder);
    IReadOnlyList<LibraryReference> RemoveLibraryReference(string name);
    IReadOnlyList<LibraryReference> SetLibraryVersion(string name, string version, string? company);
    IReadOnlyList<InstalledLibrary> InstallLibrary(string libraryPath, bool overwrite);
    /// <summary>
    /// Export the currently open PLC project as a .library file via
    /// ITcPlcIECProject.SaveAsLibrary ("Save as library" in the XAE GUI).
    /// The counterpart to InstallLibrary — previously only available through
    /// a manual GUI step, interrupting every library-iteration cycle.
    /// </summary>
    string SaveAsLibrary(string outputPath, bool install);
    IReadOnlyList<LibraryParameter> ListLibraryParameters(string placeholder);
    IReadOnlyList<LibraryParameter> SetLibraryParameter(string placeholder, string listName, string key, string value);
    string DumpTipcTree();

    // ---- safety-recon (read-only spike) ----
    /// <summary>
    /// Read-only reconnaissance of the TwinCAT Safety project's reachability via
    /// the Automation Interface: enumerates dte.Solution.Projects, probes each
    /// project's COM Object for tree-item-ness and ITcSysManager root shortcuts,
    /// and deep-walks any safety-relevant root (name/ProduceXml). No mutation.
    /// </summary>
    string SafetyRecon();

    /// <summary>
    /// WRITE PROBE (non-destructive, never saves): resolve the safety root
    /// (TISC) + the itemType-800 "Safety Project" node, dump ProduceXml
    /// (false &amp; true), and — only when <paramref name="attemptWrite"/> is
    /// true — attempt a CreateChild subtype sweep + ConsumeXml round-trip,
    /// capturing each attempt's HRESULT and rolling back any created node.
    /// </summary>
    string SafetyProbeCreateAliasDevice(string? targetPath, string? aliasName, string? subTypesCsv, bool attemptWrite);

    // ---- safety (production) ----
    /// <summary>Enumerate the safety project structure from .splcproj + .sds files.</summary>
    string SafetyList();
    /// <summary>Read the safe-I/O assignment joining .sds + .sal aliasDeviceIoUsage + Mappings.xml.</summary>
    string SafetyReadIoMap();
    /// <summary>Dump the FBD logic per group/network from .sal files.</summary>
    string SafetyReadProgram();
    /// <summary>
    /// Author a project-variable alias device (.sds), register it in .splcproj,
    /// and reload the safety project in XAE. NEVER forges or computes the CRC.
    /// </summary>
    string SafetyAddAliasDevice(string signalName, int subType);
    /// <summary>
    /// Issue #153: create a standard digital alias device (SubType 1 = digital input, 2 = digital output)
    /// in <paramref name="group"/> (required when the project has more than one group) — validated,
    /// written through temp files with rollback and read back; then reload the project in XAE.
    /// </summary>
    string SafetyAddAliasDevice(string signalName, int subType, string? group);
    /// <summary>
    /// Link the alias device's connection-image symbol to a standard-PLC symbol
    /// via ITcSysManager.LinkVariables. Fails with a clear precondition error when
    /// the safety project has not been mapped/built yet.
    /// </summary>
    string SafetyLinkAliasDevice(string aliasDevicePath, string plcSymbolPath);

    // ---- nc-axes (Motion) ----
    string DumpTincTree();
    IReadOnlyList<NcTaskEntry> ListNcTasksAxes();
    string CreateNcTask(string name);
    string CreateAxis(string ncTaskPath, string axisName);
    string ConfigureAxis(string axisPath, IReadOnlyDictionary<string, string> fields);
    NcAxisParams ReadAxisParams(string axisPath);

    FieldbusTopology GetFieldbusTopology();
    string GetDeviceXml(string treePath);
    void ConfigureEtherCatSlave(string treePath, string xml);

    // ---- iolink-devices ----
    /// <summary>
    /// Read back the IO-Link configuration of a master terminal (e.g. EL6224)
    /// at <paramref name="treePath"/>: per-port assigned device + named process
    /// data, parsed from the slave's ProduceXml. Read-only.
    /// </summary>
    IoLinkConfig GetIoLinkConfig(string treePath);

    // ---- coe-registers ----
    /// <summary>
    /// Parse the CoE offline dictionary for the EtherCAT slave at
    /// <paramref name="treePath"/> from the matching ESI file.
    /// Optional <paramref name="indexFilter"/> narrows the result (e.g. "800D"
    /// or "8000-80FF"). Read-only; no activated config required.
    /// </summary>
    CoeDictionary ReadCoeDictionary(string treePath, string? indexFilter);
    /// <summary>
    /// Parse the configured Startup SDO-download entries for the EtherCAT slave at
    /// <paramref name="treePath"/> from the node's ProduceXml. Returns an empty
    /// list (not an error) when the slave has no user-configured overrides.
    /// Read-only.
    /// </summary>
    CoeStartupList ReadCoeStartup(string treePath);
    /// <summary>
    /// The complete Startup list (CoE, SoE, EoE, FoE, including the fixed entries) of the EtherCAT box at
    /// <paramref name="treePath"/>, or of every box below every EtherCAT master when it is null (then only boxes with at
    /// least one entry after the filters). <paramref name="protocol"/> is a normalized protocol name or null. Read-only.
    /// </summary>
    EtherCatStartupResult ReadEtherCatStartup(string? treePath, bool includeFixed, string? protocol);
    /// <summary>
    /// Reads SoE parameters live from the drive at <paramref name="treePath"/> over ADS (the box's AMS address from its device
    /// XML): the given <paramref name="idns"/> with the given element bits at <paramref name="driveNo"/> (default 0), or,
    /// with <paramref name="idns"/> null, every SoE entry of the box's Startup list compared with the drive
    /// (<paramref name="driveNo"/> then filters). Throws when the drive does not answer at all; an ADS error of one IDN is part of
    /// the result. Read-only.
    /// </summary>
    SoeParameterReadResult ReadSoeParameters(string treePath, IReadOnlyList<int>? idns, int? driveNo, IReadOnlyList<int> elements, int timeoutMs);

    // ---- ethercat-topology-edit ----
    /// <summary>
    /// Create a top-level EtherCAT device under TIID — a master (ItemSubType
    /// 111) or slave (130) — and save. Returns the new device's tree path.
    /// </summary>
    string AddEtherCatDevice(string name, string deviceKind);
    /// <summary>
    /// Create an EtherCAT box/terminal (optionally a whole subtree) under
    /// <paramref name="parentPath"/>, reusing the proven CreateChild(9099) +
    /// vInfo-candidate engine. Saves the project. Returns a human summary.
    /// </summary>
    string AddEtherCatBox(string parentPath, EtherCatBoxSpec box, string? beforeSibling);
    /// <summary>Delete an EtherCAT box node by tree path and save.</summary>
    string RemoveEtherCatBox(string treePath);
    /// <summary>Set/override a box's upstream PreviousPort (A/B/C/D) and save.</summary>
    string SetEtherCatPreviousPort(string treePath, string port);

    IReadOnlyList<PouEntry> ListPous();
    /// <summary>
    /// As <see cref="ListPous"/>, but with every element of the plc_* catalog (text lists, image pools, visualisations,
    /// recipes, UML diagrams, task references …) and not only POU/GVL/DUT/INTERFACE/FOLDER.
    /// </summary>
    IReadOnlyList<PouEntry> ListPlcElements();
    PouBody ReadPou(string treePath);
    string ReadGvl(string treePath);
    /// <summary>
    /// Every declaration/implementation text the XAE holds for the PLC object stored in
    /// <paramref name="relativeSourcePath"/> (relative to its .plcproj) of the PLC project
    /// <paramref name="plcName"/>, keyed like <see cref="PlcSourceFiles.ParseSections"/>.
    /// Throws InvalidOperationException when the object is not in the tree.
    /// </summary>
    IReadOnlyList<PlcTextSection> ReadPlcObjectSections(string plcName, string relativeSourcePath);
    void WritePouBody(string treePath, string? declaration, string? implementation);
    string CreatePou(string parentPath, string name, string pouType, string returnType, string implLanguage);
    string CreateDut(string parentPath, string name, string dutType, string? baseType);
    /// <summary>
    /// Creates an ordinary GVL (nSubType 615). "Persistent" is not an object type: it is the
    /// <c>VAR_GLOBAL PERSISTENT</c> section the coordinator writes into the declaration (nSubType 629
    /// would create a Parameter List instead — see <see cref="PlcItemType.GvlParamList"/>).
    /// </summary>
    string CreateGvl(string parentPath, string name);
    string CreateInterface(string parentPath, string name);
    /// <summary>
    /// Generic CreateChild (null vInfo) plus settle-wait for elements that take no creation parameters
    /// (FOLDER 601, PARAMLIST 629). <paramref name="label"/> names the element in failure messages.
    /// The caller validates the parent's type first. Returns the new node's tree path.
    /// </summary>
    string CreatePlcChild(string parentPath, string name, int subType, string label);

    // ---- visible-for-video --------------------------------------------------
    /// <summary>
    /// Open a PLC source object (POU/GVL/DUT/INTERFACE) at <paramref name="treePath"/>
    /// in the visible XAE editor, make it the active tab, and best-effort raise the
    /// XAE main window to the OS foreground. Opt-in only — the headless write tools
    /// are NOT changed to auto-open. Refuses non-openable nodes (folders, the PLC
    /// project root, the References node) by throwing.
    /// </summary>
    OpenPlcObjectResult OpenPlcObject(string treePath);

    /// <summary>
    /// Close the visible XAE editor tab for a PLC source object (counterpart to
    /// <see cref="OpenPlcObject"/>). Saves the document first so no prompt appears
    /// and no work is lost. Returns whether a tab was actually closed.
    /// </summary>
    ClosePlcObjectResult ClosePlcObject(string treePath);

    // ---- open-object (generalization of the visible-for-video tools) ---------
    /// <summary>
    /// Surface ANY Solution-Explorer node in the visible XAE. Classifies the target:
    /// a PLC source object opens its own editor tab (the proven file-open fast path);
    /// a system-manager node (EtherCAT/IO, Motion/NC, TcCOM, Scope) is SELECTED via
    /// UIAutomation so the single shared sysman editor re-renders to show it (these
    /// share one editor — there is no per-node tab). Best-effort OS foreground raise.
    /// </summary>
    OpenObjectResult OpenObject(string treePath);

    /// <summary>
    /// Counterpart to <see cref="OpenObject"/>. For a PLC object: save + close its
    /// editor tab. For a sysman node: a reported no-op (one shared editor, no per-node
    /// tab to close) — never an error.
    /// </summary>
    CloseObjectResult CloseObject(string treePath);

    // ---- editor-navigation (feature/editor-navigation) ------------------------
    /// <summary>
    /// Put the editor cursor on <paramref name="line"/> (1-based pane line) of the
    /// requested pane of the POU document at <paramref name="treePath"/>: tab
    /// activation via <see cref="OpenPlcObject"/>, UIAutomation pane focus, then the
    /// VS <c>Edit.GoTo</c> command whose modal Go-To dialog is driven via Win32.
    /// Never logs out (verified Online). Throws when the object cannot be opened or
    /// the pane is absent (e.g. declaration pane while Online = monitoring grid).
    /// </summary>
    GotoLineResult GotoLine(string treePath, int line, EditorSection section);

    // ---- Structured POU members & object lifecycle (feature/pou-handling) ----
    IReadOnlyList<PouMemberEntry> ListPouMembers(string pouPath);
    /// <summary>As <see cref="ListPouMembers"/>, but the folders a POU groups its members in are listed too (Kind FOLDER).</summary>
    IReadOnlyList<PouMemberEntry> ListPouElements(string pouPath);
    string AddPouMember(string pouPath, string kind, string name, string? returnType, string? implLanguage);
    /// <summary>
    /// Same as the 5-arg overload, but for kind=PROPERTY lets the caller create
    /// only Get, only Set, or Both (default) instead of always both — a bare
    /// Set on an INTERFACE forces every implementer to write a setter even for
    /// a property that should be read-only.
    /// </summary>
    string AddPouMember(string pouPath, string kind, string name, string? returnType, string? implLanguage, PropertyAccessors accessors);
    PouBody ReadPouMember(string memberPath);
    void WritePouMember(string memberPath, string? declaration, string? implementation);
    /// <summary>
    /// Resolve <paramref name="treePath"/> and read its ItemType without changing anything
    /// (issue #148): mutating PLC tools validate their target with this BEFORE the automatic
    /// logout, and delete checks the ItemType against <see cref="PlcDeletePolicy"/>.
    /// </summary>
    PlcNodeInfo ProbePlcNode(string treePath);
    void DeletePlcObject(string treePath);
    /// <summary>Delete a FOLDER node (and what it contains). Refuses any node that is not a folder.</summary>
    void DeletePlcFolder(string treePath);
    /// <summary>
    /// Rename a PLC node. The XAE's "rename via refactoring" prompt that the rename may raise is answered
    /// "Ja" when <paramref name="updateReferences"/> is true, else "Nein" (see <see cref="RenameRefactoringDialog"/>).
    /// </summary>
    PlcRenameResult RenamePlcObject(string treePath, string newName, bool updateReferences = false);
    string PlcOpenExport(string filePath, string selector);
    string PlcOpenImport(string filePath, int conflictMode);

    void LinkVariables(string v1, string v2, int offs1 = 0, int offs2 = 0, int size = 0);
    void UnlinkVariables(string v1, string v2 = "");
    string GetMappingInfo();
    void SetDeviceDisabled(string treePath, bool disabled);

    BuildResult BuildPlc(string? plcProjectName);
    BuildResult BuildSolution();
    /// <summary>
    /// ActivateConfiguration(), and with <paramref name="autoConfirmRestart"/> StartRestartTwinCAT(). Issue #195:
    /// with a restart the result is a success only when the target's TwinCAT system (ADS port 10000 of the
    /// project's target NetId, the local NetId for a Local target) reads Run within <paramref name="runTimeout"/>.
    /// An XAE box that the dialog watcher does not answer ends the wait as a failure with its text; an OK-only
    /// box is closed first. See <see cref="ActivationWait"/>.
    /// </summary>
    /// <param name="runTimeout">How long to wait for Run after the restart; null = <see cref="ActivationWait.DefaultDeadline"/>.</param>
    ActivateResult ActivateConfig(
        bool autoConfirmRestart = false,
        bool autoGenerateLicenses = false,
        bool autoConfirmLogin = false,
        LoginMode loginMode = LoginMode.OnlineChange,
        bool updateBootProject = true,
        bool allowDownloadFallback = false,
        TimeSpan? runTimeout = null);

    // ---- build-recovery ----
    LogoutPlcResult LogoutPlc();
    /// <param name="allowDownloadFallback">Issue #150 C-17: when online change is impossible, fall
    /// back to a full download (stops a running PLC) only when this is true; otherwise refuse.</param>
    LoginPlcResult LoginPlc(LoginMode mode, bool allowDownloadFallback = false);
    CleanResult CleanSolution();
    RebuildResult RebuildSolution();
    RebuildResult RebuildPlc(string? plcProjectName);

    // ---- multi-xae-robustness (issue #57) ----
    /// <summary>
    /// Q2: reason to refuse a mutating call because the target XAE is
    /// ambiguous, or null when 0/1 shells run or a unique deterministic
    /// solution-path match exists.
    /// </summary>
    string? MultiXaeBlockReason();
    /// <summary>
    /// Q1: explicit, opt-in recovery of a stale/crashed TcXaeShell. With
    /// <paramref name="pid"/> null, closes EVERY running TcXaeShell (the
    /// original all-or-nothing behaviour); with a PID, closes only that one —
    /// use when several instances are running and only one is confirmed stale,
    /// so the others (possibly holding unsaved work) are left alone.
    /// </summary>
    string ForceCloseXae(int? pid = null);

    // ---- sbom ----
    /// <summary>Reads the Device Manager (MDP) inventory of an IPC over ADS; null NetId = the open solution's target.</summary>
    IpcInventoryResult ReadIpcInventory(string? amsNetId, IReadOnlyList<int>? moduleTypes, bool includeRaw, int timeoutMs);
    /// <summary>Online identity (VendorId, ProductCode, RevisionNo, SerialNo) of the slaves of one or all EtherCAT masters, joined with the projected boxes.</summary>
    EtherCatIdentitiesResult ReadEtherCatIdentities(string? masterTreePath, string? amsNetId, int? adsPort, bool includeCrc, bool includeCoe, bool includeDeviceDetails, int timeoutMs);
    /// <summary>The library repositories of the XAE (ITcPlcLibraryManager.Repositories).</summary>
    LibraryRepositoryListing ListLibraryRepositoryEntries();
    /// <summary>References, placeholder resolutions and installed libraries of the PLC projects (all, or the one named).</summary>
    IReadOnlyList<PlcLibraryContext> ReadPlcLibraryContexts(string? plcProjectPath);

    // ---- runtime-activation ----
    LicenseInfoResult GetLicenseInfo();
    RequiredLicensesResult ListRequiredLicenses();
    AddRequiredLicenseResult AddRequiredLicense(string? licenseId, string? orderNo);
    RemoveRequiredLicenseResult RemoveRequiredLicense(string? licenseId, string? orderNo);
    string DismissSecurityDialog();
    string OpenLicenseManagerAndRequestTrial();
    (string Message, byte[]? ScreenshotPng) GenerateTrialLicenses();
    string SubmitSecurityCode(string code);

    // ---- xae-error-list ----
    ErrorListClearResult ClearErrorList();
    /// <summary>
    /// Read-only snapshot of the Error List window (severity/description/file/line),
    /// the same collection logic BuildPlc/RebuildPlc use to report build errors —
    /// but without requiring a build first. Previously the only way to learn the
    /// current error count was the side effect of ClearErrorList's return value.
    /// </summary>
    IReadOnlyList<BuildError> GetErrorList();

    // ---- ethercat-device-descriptions ----
    /// <summary>Raises the XAE command "Reload Device Descriptions" (by GUID/ID) so newly placed ESI files become known.</summary>
    DeviceDescriptionReloadResult ReloadDeviceDescriptions();

    // ---- xae-event-logger ----
    EventLoggerReadResult ReadEventLogger(uint maxEvents, string? minSeverity, string? amsNetId);
    EventLoggerActionResult ConfirmEventLogger(string mode, string? amsNetId);

    // ---- runtime-control ----
    IReadOnlyList<BroadcastTarget> BroadcastSearch(string? searchTarget);
    string AddRoute(string netId, string? ipAddr, string? hostName, string? userName, string? password, bool projectRoute);
    string SetTarget(string netId, string? platform);
    string RestartRuntime();

    // ---- scope-measurement ----
    string CreateScopeProject(string? projectName);
    string RemoveScopeProject(string? projectName);
    string AddScopeChannel(string symbol, string? chartName, string? axisName, string? channelName, bool stackedAxis,
        string? symbolName, int? targetPort, string? sampleMode, int? sampleTimeMs, string? symbolComment,
        string? projectName);
    string SetScopeAxisMode(string chartName, string mode, string? projectName);
    string SetScopeLabels(string nodeSelector, string? name, string? chartTitle, string? axisTitle, string? projectName);
    IReadOnlyList<ScopeNodeEntry> ListScopeChannels(string? projectName);
    ScopeStatus GetScopeStatus(string? projectName);
    string ExportScopeData(string format, string? projectName);

    // ---- scope-acquisition-symbols ----
    ScopeAcquisitionInfo ReadScopeAcquisition(string acqSelector, string? projectName);
    string SetScopeAcquisitionSymbol(string acqSelector, string? symbolName, int? targetPort,
        string? targetSystem, string? sampleMode, int? sampleTimeMs, double? timeOffsetS,
        string? symbolComment, bool? enabled, string? projectName);

    // ---- scope-recording ----
    string StartScopeRecord(string? projectName);
    string StopScopeRecord(string? projectName);
    ScopeRecordState GetScopeRecordState(string? projectName);
    string SaveScopeData(string? path, string? projectName);
    string SetScopeRecordSettings(double? recordTimeS, bool? ringbuffer, string? fileStore,
        string? recordMode, string? autoSaveMode, string? autoSavePath, string? filenameMask,
        string? syncMode, string? projectName);
    string CreateScopeTriggerGroup(string action, string? projectName);
    string AddScopeTriggerSet(string groupName, string setType, string? symbol, double? threshold,
        string? edge, string? compare, string? projectName);
    IReadOnlyList<ScopeTriggerGroupEntry> ListScopeTriggers(string? projectName);
    /// <summary>
    /// Diagnostics-only research probe (issue #153): dump / create / set / delete on a scope-tree
    /// node so undocumented Scope View property keys and element ids can be observed live.
    /// </summary>
    string ScopeProbe(string action, string? nodePath, string? name, int? elementType,
        string? key, string? value, string? projectName);

    // ---- ads-variable-rw ----
    AdsVariableResult ReadAdsVariable(string symbolPath, string? amsNetId, int adsPort);
    AdsVariableResult WriteAdsVariable(string symbolPath, string value, string? amsNetId, int adsPort);

    // ---- ibn-live ----
    /// <summary>
    /// Read many PLC symbols in one call over a single ADS connection. Each
    /// returned item carries its own success/error so one bad symbol does not
    /// sink the whole batch. Used to snapshot an IBN IO checklist + the test
    /// PRG status struct in a single round trip.
    /// </summary>
    AdsBatchReadResult ReadAdsVariablesBatch(IReadOnlyList<string> symbolPaths, string? amsNetId, int adsPort);

    /// <summary>
    /// Measure the elapsed time between two symbol conditions (edge A → edge B)
    /// by polling a single ADS connection, bounded by <paramref name="timeoutMs"/>.
    /// A read-only cross-check / fallback for the test PRG's internal PLC-timer
    /// measurement (which is more deterministic). Reads only; moves nothing.
    /// </summary>
    SignalTransitionResult MeasureSignalTransition(
        string startSymbol, string startEquals,
        string endSymbol, string endEquals,
        int timeoutMs, string? amsNetId, int adsPort);

    /// <summary>
    /// READ-ONLY diagnostic: dumps an FB/struct instance's MemberInstances
    /// (name, IsProperty, type, size, offset when available) plus any
    /// RPC-callable methods (e.g. __getX/__setX -- only present when the
    /// compiler marked the accessor RpcEnable="plc").
    /// </summary>
    InspectFbInstanceResult InspectFbInstance(string instancePath, string? amsNetId, int adsPort);

    // ---- plc-online-status ----
    /// <summary>
    /// Probe whether the TwinCAT XAE IDE is currently Online (logged in to the
    /// running PLC). When Online, code edits via PLC-code tools are blocked.
    /// Returns <c>IsOnline=null</c> when the state cannot be determined via COM.
    /// </summary>
    PlcOnlineStateResult GetPlcOnlineState();

    // ---- plc-run-control ----
    /// <summary>
    /// Put the PLC runtime into RUN via the ADS state machine (the path that
    /// keeps a freshly downloaded program — unlike RestartRuntime, which reloads
    /// the old boot project). Reads the state back and reports what the runtime
    /// actually is.
    /// </summary>
    PlcRunControlResult PlcStart(string? amsNetId, int adsPort);
    /// <summary>Counterpart to <see cref="PlcStart"/>: ADS state STOP.</summary>
    PlcRunControlResult PlcStop(string? amsNetId, int adsPort);

    // ---- boot-project ----
    /// <summary>
    /// Write the current program as the boot project, so a download survives a
    /// power cycle. With <paramref name="dryRun"/> true (the default at the tool
    /// layer) nothing is changed: it only reports which Automation-Interface
    /// member exists for this, which is safe to run against a live plant.
    /// </summary>
    BootProjectResult ActivateBootProject(bool dryRun);

    // ---- variant-management ----
    VariantStatusResult GetVariantStatus();
    string SetActiveVariant(string variantName);

    // ---- coredump-analysis ----
    /// <summary>
    /// Inventory .core dump files for a UM instance/port. File-based; no XAE required.
    /// </summary>
    string CoreDumpList(string? instanceName, int? port, string? projectFolder);

    /// <summary>
    /// Analyze a core dump: classify kind, compute hang gap, extract embedded strings,
    /// join offline LoggedEvents. File-based; no XAE required.
    /// </summary>
    string CoreDumpAnalyze(string? dumpPath, string? instanceName, int? port, string? projectFolder);

    /// <summary>
    /// Read the symbol-resolved call stack from the dump currently loaded in the IDE.
    /// GUI best-effort via UIAutomation. Returns a precondition error if no dump is loaded.
    /// </summary>
    string CoreDumpReadCallStack();

    // ---- hmi-server (TE2000 HMI Automation Interface) ----
    /// <summary>
    /// Throws with a plain explanation when the HMI tools cannot work on this machine:
    /// the build has no HMI support, or TE2000 (TcHmiAutomation.dll) is not installed.
    /// Called before any HMI operation so that a missing TE2000 does not surface as a
    /// bare "could not load file or assembly" from the runtime.
    /// </summary>
    void EnsureHmiAutomationAvailable();

    /// <summary>
    /// All TwinCAT HMI projects of the open solution, via
    /// <c>dte.GetObject("Beckhoff.TcHmi.1.12")</c> → <c>ITcHmiAutomation.GetHmiProjects()</c>.
    /// An empty list (not an error) when the solution holds no .hmiproj.
    /// </summary>
    HmiProjectList ListHmiProjects();

    /// <summary>
    /// Config values (all <c>ConfigFields</c>), server extensions and — with
    /// <paramref name="includeFiles"/> — the project tree down to <paramref name="maxDepth"/>.
    /// Throws <see cref="InvalidOperationException"/> when the project is unknown.
    /// </summary>
    HmiProjectInfo GetHmiProjectInfo(string hmiProject, bool includeFiles, int maxDepth);

    /// <summary>
    /// Symbols mapped in the project's HMI engineering server
    /// (<c>ITcHmiServer.GetMappedSymbols(refresh)</c>). Unfiltered. CAUTION, verified
    /// live 2026-09-25: this is the Automation Interface's cached view — after an
    /// UnMapSymbol it still listed the symbol even with refresh=true, while the running
    /// server already answered SYMBOL_NOT_MAPPED. Never use it to decide a write.
    /// </summary>
    IReadOnlyList<HmiMappedSymbol> ListHmiMappedSymbols(string hmiProject, bool refresh);

    /// <summary>
    /// Read values through the HMI server. One entry per requested name, in request
    /// order; a symbol that cannot be read carries <c>Ok=false</c> and the server's
    /// <c>LastError</c> instead of failing the batch.
    /// </summary>
    IReadOnlyList<HmiSymbolValue> ReadHmiSymbols(string hmiProject, IReadOnlyList<string> symbols);

    /// <summary>
    /// <c>start</c> / <c>shutdown</c> / <c>refresh_symbols</c> on the project's HMI
    /// ENGINEERING server (<c>ITcHmiServer.Start/Shutdown/RefreshSymbols</c>), then polls
    /// the extensions up to <paramref name="timeoutMs"/> until the action has settled.
    /// </summary>
    HmiServerControlResult ControlHmiServer(string hmiProject, string action, int timeoutMs);

    /// <summary>
    /// <c>ITcHmiServer.MapSymbol(mapName, internalName, domain)</c>, or the
    /// <c>ITcHmiServer3</c> overload when <paramref name="subSymbolName"/> is set. Raw
    /// call only — confirmation and read-back are the coordinator's job.
    /// </summary>
    HmiOperationResult MapHmiSymbol(string hmiProject, string mapName, string internalName, string domain, string? subSymbolName);

    /// <summary><c>ITcHmiServer.UnMapSymbol(mapName, domain, removeHistorizedData)</c>. Raw call only.</summary>
    HmiOperationResult UnmapHmiSymbol(string hmiProject, string mapName, string domain, bool removeHistorizedData);

    /// <summary>
    /// <c>ITcHmiServer.WriteSymbolJson(symbol, valueJson)</c> — for an ADS-mapped symbol
    /// this writes through to the live PLC. Raw call only.
    /// </summary>
    HmiOperationResult WriteHmiSymbol(string hmiProject, string symbol, string valueJson);

    // ---- hmi-views --------------------------------
    /// <summary>Source of a project file via <c>LookupChild(path)</c> → <c>ITcHmiFile.GetSource</c>.
    /// Throws <see cref="InvalidOperationException"/> when missing or not a file.</summary>
    HmiFileContent ReadHmiFile(string hmiProject, string path);

    /// <summary>True when <paramref name="path"/> (file or folder) exists in the project.</summary>
    bool HmiItemExists(string hmiProject, string path);

    /// <summary>
    /// Create an item from a TE2000 template (<c>ITcHmiItem.AddItem(name, HmiTemplates)</c>) in
    /// <paramref name="folder"/>, creating missing folders; saves. Returns the created paths
    /// (main file first, then dependent files). Raw call — confirmation is the coordinator's job.
    /// </summary>
    IReadOnlyList<string> AddHmiItem(string hmiProject, string folder, string name, string templateKey);

    /// <summary><c>ITcHmiFile.SetSource</c> on an existing file, then save. Raw call.</summary>
    HmiOperationResult WriteHmiFile(string hmiProject, string path, string source);

    /// <summary><c>ITcHmiItem.AddExistingItem(name, sourcePath)</c> into <paramref name="folder"/>
    /// (created when missing); saves. Returns the created paths and — when TE2000 raised an error although
    /// the item is in the project afterwards (#247) — that error, so the coordinator can check the copy.
    /// Throws when nothing was added. Raw call.</summary>
    HmiImportResult ImportHmiFile(string hmiProject, string folder, string name, string sourcePath);

    /// <summary><c>ITcHmiProject.Build(configuration, false)</c>.</summary>
    HmiOperationResult BuildHmiProject(string hmiProject, string configuration);

    /// <summary>File or folder at <paramref name="path"/> with the number of items below it;
    /// null when it does not exist. Raw call.</summary>
    HmiItemInfo? DescribeHmiItem(string hmiProject, string path);

    /// <summary><c>ITcHmiFile.IsOpen()</c>: true when the file is open in an editor tab of the XAE.
    /// Writing it then makes the XAE ask modally whether to reload it. Raw call.</summary>
    bool IsHmiFileOpen(string hmiProject, string path);

    /// <summary><c>ITcHmiItem.DeleteChild(path)</c> from the project root, then saves. Removes
    /// the item from the project AND the disk (live 2026-09-25, TEMP copy). Raw call — the
    /// confirmation and the checks live in <c>HmiCoordinator.DeleteItem</c>.</summary>
    HmiOperationResult DeleteHmiItem(string hmiProject, string path);

    /// <summary>
    /// <c>ITcHmiServer.AttachTmcFile(rootNamespace, pathToTmcFile)</c> followed by SaveAllFiles —
    /// registers a PLC project's TMC file with the HMI project under a virtual PLC name, so its
    /// symbols are known WITHOUT a PLC connection. TE2000 persists the
    /// list as the .hmiproj property <c>HmiTmcFiles</c>, hence the save.
    /// <para><b>The returned Ok is NOT evidence.</b> Decompiled TE2000 14.17.22.1: the wrapper
    /// returns true unless it throws and reports a real failure by showing a modal message box in
    /// the XAE. Only the read-back in <c>HmiCoordinator.AttachTmcFile</c> decides.</para>
    /// </summary>
    HmiOperationResult AttachHmiTmcFile(string hmiProject, string rootNamespace, string tmcFilePath);

    /// <summary>
    /// <c>ITcHmiServer.DetachTmcFile(rootNamespace)</c> followed by SaveAllFiles.
    /// <para><b>Ok is always false here</b> — the TE2000 implementation removes the entry and then
    /// returns false unconditionally. Never read that as a failure; the read-back decides.</para>
    /// </summary>
    HmiOperationResult DetachHmiTmcFile(string hmiProject, string rootNamespace);

    /// <summary>
    /// #161, server-side TMC: the two config writes of TE2000's
    /// <c>TcHmiServerCommunication.AttachTmcFile/DetachTmcFile</c>, in that order —
    /// <c>ADS.Config::RUNTIMES::&lt;runtime&gt;::TMC::DATA_SYMBOLUPANDDOWNLOAD</c> =
    /// <paramref name="dataSymbolUpAndDownload"/>, then <c>…::TMC::USE_TMC</c> = <paramref name="useTmc"/>,
    /// each through <c>ITcHmiServer.WriteSymbolJson</c>. Ok=false (with LastError naming the leaf)
    /// when the server refused a write; the first leaf may then already be written. Raw call —
    /// the read-back of <c>ADS.Config::RUNTIMES</c> in <c>HmiCoordinator.EnableRuntimeTmc</c> decides.
    /// </summary>
    HmiOperationResult SetHmiRuntimeTmc(string hmiProject, string runtime, string dataSymbolUpAndDownload, bool useTmc);

    /// <summary>
    /// hmi-internal-symbols (#165): <c>ITcHmiProject.GetInternalSymbolInstance(name, value, type,
    /// persist, isReadonly)</c> + <c>AddInternalSymbol</c> — a CLIENT (framework) internal symbol
    /// <c>%i%name%/i%</c>, persisted by TE2000 in <c>Properties\tchmiconfig.json</c>.
    /// <para><b>Ok is NOT evidence.</b> AddInternalSymbol is void, has no duplicate check and saves
    /// asynchronously (SaveResync); only the read-back of tchmiconfig.json in
    /// <c>HmiCoordinator.AddInternalSymbol</c> decides.</para>
    /// </summary>
    /// <param name="defaultValueJson">Normalised JSON scalar or null.</param>
    HmiOperationResult AddHmiInternalSymbol(string hmiProject, string name, string dataType,
        string? defaultValueJson, bool persist, bool readOnly);

    /// <summary>
    /// <c>ITcHmiProject.RemoveInternalSymbol(name)</c>. <b>Its bool is always false</b>
    /// (decompiled TE2000 14.17.22.1), success or not — the read-back decides.
    /// </summary>
    HmiOperationResult RemoveHmiInternalSymbol(string hmiProject, string name);

    /// <summary>
    /// <c>ITcHmiServer.MapOfflineSymbol(mapName, "", dataType, online, persist, defaultValue)</c> —
    /// a SERVER symbol in the TcHmiSrv domain, persisted by the server in
    /// <c>Server\TcHmiSrv\TcHmiSrv.Config.default.json</c>. The returned Ok is mostly honest, but a
    /// server reply that fails to parse counts as success there, so the read-back still decides.
    /// </summary>
    HmiOperationResult MapHmiOfflineSymbol(string hmiProject, string mapName, string dataType,
        string? defaultValueJson, bool online, bool persist);

    /// <summary>
    /// Title of an open TE2000 "Activate Engineering Server Trial License" window
    /// (a CAPTCHA the engineer must answer), or null. Detection only.
    /// </summary>
    string? FindHmiBlockingDialog();

    /// <summary>
    /// hmi-ads-tmc #166, read-only: for every TwinCAT project of the open solution (a project
    /// whose <c>Object</c> is an ITcSysManager), every child of <c>LookupTreeItem("TIPC")</c> with
    /// its raw <c>ProduceXml</c> — the source of <c>TreeItem/PlcProjectDef/RelativeTmcPath</c>, the
    /// same walk TE2000's "Browse solution for TMC file" dialog does (decompiled,
    /// <c>BrowseSolutionForTmcFileWindow</c>). Needs only the system manager, not the HMI
    /// Automation Interface. A child whose XML cannot be read is returned with an error, not dropped.
    /// </summary>
    IReadOnlyList<PlcProjectXml> ListPlcProjectXml();

    // ---- plc-project-lifecycle ----
    /// <summary>Names of the children of <c>TIPC</c> (one per PLC project / PLC instance), in tree order.</summary>
    IReadOnlyList<string> ListPlcProjectNames();

    /// <summary>
    /// <c>TIPC.CreateChild(name, subType, vInfo, source)</c> — new PLC project from a template name/path,
    /// an existing .plcproj (subType 0/1/2 = copy/move/in place), a .tpzip archive (<paramref name="vInfoNull"/>
    /// = true, Infosys passes null) or a .tmc file. Returns the name of the created child.
    /// </summary>
    string CreatePlcProjectChild(string name, int subType, string source, bool vInfoNull);

    /// <summary><c>TIPC.ExportChild(name, path)</c> — one PLC project as .tpzip.</summary>
    void ExportPlcProject(string plcProjectName, string tpzipPath);

    /// <summary><c>ITcSysManager9.SaveAsArchive(path)</c> — the whole solution as .tszip.</summary>
    void SaveSolutionArchive(string tszipPath);

    /// <summary>
    /// <c>dte.Solution.AddFromTemplate(tszip, destDir, name)</c> — unpacks the archive as a new solution
    /// and opens it (a solution open before is replaced). Returns the full path of the now-open .sln.
    /// </summary>
    string OpenSolutionArchive(string tszipPath, string destDir, string name);

    // ---- plc-instance-settings ----
    /// <summary>Name and ItemType of each child of <paramref name="treePath"/>, in tree order (match by type, not label).</summary>
    IReadOnlyList<TreeChildInfo> ListTreeChildren(string treePath);

    /// <summary><c>ProduceXml(false)</c> on a freshly looked-up <paramref name="treePath"/> (no cached COM node).</summary>
    string ProduceTreeItemXml(string treePath);

    /// <summary><c>ConsumeXml(xml)</c> on <paramref name="treePath"/>. The XAE ignores unknown content silently — callers read back.</summary>
    void ConsumeTreeItemXml(string treePath, string xml);
    // ---- persistent-snapshot ----
    /// <summary>Flatten each requested path into leaves and read them with sum commands over ONE connection / symbol load.
    /// Paths that do not exist come back in <c>Missing</c>, unreadable or unsupported leaves in <c>Skipped</c>.</summary>
    SnapshotReadResult ReadSnapshotLeaves(SnapshotReadRequest request, string? amsNetId, int adsPort);

    /// <summary>Write leaves with sum commands (halve-and-retry on a refused batch), then read EVERY leaf back.</summary>
    SnapshotWriteResult WriteSnapshotLeaves(IReadOnlyList<SnapshotLeafWrite> writes, string? amsNetId, int adsPort);

    /// <summary>Read-only browse of the online symbol table (no values).</summary>
    AdsSymbolListResult ListAdsSymbols(string? root, int maxDepth, bool persistentOnly, int limit, string? amsNetId, int adsPort);

    /// <summary>ADS state of the PLC runtime ("Run", "Stop", …), shown prominently in the restore popup.</summary>
    AdsRunStateResult GetAdsRunState(string? amsNetId, int adsPort);
}
