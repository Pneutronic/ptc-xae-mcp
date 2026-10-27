<!-- GENERATED FILE — do not hand-edit.
     Regenerate with: python tools/gen_tool_reference.py
     Source: [McpServerTool]/[Description] attributes in
     src/PtcXaeMcp.WinForms/Mcp/TwinCatMcpTools.*.cs (issue #157 item 4). -->

# MCP tool reference

205 tools, generated from the source attributes — one section per
`TwinCatMcpTools.<Group>.cs` partial file, tools in source order within each.

## Contents

- [ActiveProject](#activeproject) (1)
- [AdsVariable](#adsvariable) (4)
- [Build](#build) (13)
- [ClientInfo](#clientinfo) (1)
- [Coe](#coe) (2)
- [CoreDump](#coredump) (3)
- [Diagnostics](#diagnostics) (10)
- [EditorNavigation](#editornavigation) (2)
- [EtherCat](#ethercat) (6)
- [EventLogger](#eventlogger) (2)
- [Fieldbus](#fieldbus) (3)
- [Hmi](#hmi) (8)
- [HmiInternalSymbols](#hmiinternalsymbols) (4)
- [HmiTmc](#hmitmc) (7)
- [HmiViews](#hmiviews) (7)
- [HostSelection](#hostselection) (1)
- [IbnLive](#ibnlive) (7)
- [IoLink](#iolink) (3)
- [IoMapping](#iomapping) (4)
- [Libraries](#libraries) (10)
- [Lifecycle](#lifecycle) (4)
- [NcAxes](#ncaxes) (5)
- [PersistentSnapshot](#persistentsnapshot) (5)
- [PlcCode](#plccode) (9)
- [PlcControl](#plccontrol) (3)
- [PlcEntries](#plcentries) (2)
- [PlcInstanceSettings](#plcinstancesettings) (5)
- [PlcObjects](#plcobjects) (6)
- [PlcOnlineStatus](#plconlinestatus) (1)
- [PlcProjectLifecycle](#plcprojectlifecycle) (7)
- [PouMembers](#poumembers) (8)
- [Recovery](#recovery) (6)
- [RuntimeControl](#runtimecontrol) (9)
- [Safety](#safety) (5)
- [Sbom](#sbom) (3)
- [Schema](#schema) (5)
- [Scope](#scope) (18)
- [ScopeFile](#scopefile) (2)
- [VariantManagement](#variantmanagement) (2)
- [VisibleEditor](#visibleeditor) (2)

## ActiveProject

### `twincat_get_active_project`

Get the currently active TwinCAT project. Returns { solutionOpen, solutionPath, solutionName } so agents can verify the expected project is loaded before mutating operations.

_No parameters._

## AdsVariable

### `twincat_read_ads_variable`

Read the current RUNTIME value of a PLC symbol via ADS from a running TwinCAT PLC or User Mode Runtime (UMR). This reads LIVE values — not static source declarations (use twincat_read_gvl / twincat_read_pou for declarations). Returns JSON { success, symbol, plcType, value, amsNetId, adsPort } or [Error] string. Supported types: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT USINT UINT UDINT ULINT REAL LREAL STRING, plus any ENUM (e.g. E_STATE) -- for an ENUM, value is { name, value }: name is the matching member's declared name (null if the raw value doesn't match any member), value is the raw underlying number. Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only when the router does not answer), default port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPath` | `string` |  | Fully-qualified PLC symbol path, e.g. 'GVL_Safety.bSI_DriveReleased' or 'MAIN.rSpeed'. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID of the target ADS server (e.g. '192.168.1.1.1.1'). Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851 (first PLC task). Use 852 for second PLC task, or the port shown in SYSTEM/Tasks. |

### `twincat_read_ads_variables_batch`

Read the current RUNTIME values of MANY PLC symbols in one call over a single ADS connection (far cheaper than N separate twincat_read_ads_variable calls). Use this to snapshot a whole IO checklist plus a status struct at once. Returns JSON { success, count, results: [{ symbol, plcType, value, ok, message }], amsNetId, adsPort } or an [Error] string on a connection-level failure. For an ENUM symbol, value is { name, value } (see twincat_read_ads_variable). A single unreadable symbol does NOT fail the batch — it appears as an item with ok=false. This tool is READ-ONLY; it never writes or forces any value. Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only when the router does not answer), default port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPaths` | `string[]` |  | PLC symbol paths to read, e.g. ['GVL_IBN.stStatus.nStep', 'GVL_IO.bCyl1Extended']. At least one required. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851 (first PLC task). |

### `twincat_inspect_fb_instance`

DIAGNOSTIC, READ-ONLY: dump an FB/struct instance's member layout via ADS -- every entry in its MemberInstances (name, instancePath, isProperty, typeName, size, offset if the runtime exposes it) plus any RPC-callable methods (e.g. __getX/__setX for PROPERTYs -- only present when the compiler marked the getter/setter RpcEnable="plc", a PER-PROPERTY compiled flag, not a project-wide switch). Use this to find out empirically whether a given PROPERTY on a live FB instance is reachable as an addressable symbol (appears in members with isProperty=true) or only via an RPC getter (appears in rpcMethods as __get<Name>/__set<Name>) -- many properties are reachable via NEITHER (not RPC-enabled, no backing shadow variable) and only readable through the XAE Automation-Interface/online-value channel, a different transport this tool cannot reach -- do not assume any shape without checking. Returns JSON { success, instancePath, typeName, members:[{ name, instancePath, isProperty, typeName, size, offset }], rpcMethods: [{ name, isVoid, parameterCount }], amsNetId, adsPort } or [Error] string. Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only when the router does not answer), default port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instancePath` | `string` |  | Fully-qualified FB/struct instance path, e.g. 'MAIN.fbMachine'. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_write_ads_variable`

Write a value to a live PLC symbol via ADS on a running TwinCAT PLC or UMR. Refuses writes to input-mapped symbols (AT %I*). Returns JSON { success, symbol, plcType, writtenValue, amsNetId, adsPort } or [Error] string. Value must be the string representation of the target type, e.g. 'true', '42', '3.14'. BOOL accepts only true/false/1/0 (anything else is an [Error], never a silent FALSE). For an ENUM symbol, value may be either a declared member name (e.g. 'E_STATE_RUNNING') or a plain numeric string (e.g. '2'). Default amsNetId = the local AMS router's NetId (the registry's, then the loopback, only when the router does not answer), default port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPath` | `string` |  | Fully-qualified PLC symbol path, e.g. 'MAIN.bEnable'. |
| `value` | `string` |  | String representation of the value to write, e.g. 'true', '42', '3.14', 'Hello', or an ENUM member name like 'E_STATE_RUNNING'. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

## Build

### `twincat_get_error_list`

Error List: read-only snapshot of the XAE Error List window (the 'Fehlerliste' tool window) — severity, description, file, line per entry. Does NOT trigger a build and does NOT clear anything — unlike twincat_clear_error_list, whose cleared-count was previously the only way to learn how many entries existed. Identical entries (same level, text, file and line) are listed once with a '(2× in the Error List)' note; the first line gives both the total and the distinct count. The Error List also shows runtime EventLogger events, sometimes twice; for alarm diagnosis use twincat_read_event_logger, which is authoritative. Requires XAE running with a solution open.

_No parameters._

### `twincat_clear_error_list`

Clear all entries in the XAE Error List window (the 'Löschen' / 'Clear' toolbar action). Useful before a new build to remove accumulated stale diagnostics that would otherwise obscure the current build output. Returns the cleared-entry count (best-effort; -1 if unavailable) and a status string. Requires XAE running.

_No parameters._

### `twincat_build_plc`

Compile only the PLC project via DTE.Solution.SolutionBuild.BuildProject. Faster than a full solution build for PLC-code-only changes. Returns { success, failedProjects, errors[] } where success is derived from LastBuildInfo==0. Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). A PLC source file changed on disk outside the XAE since the project was loaded refuses the build with [Error] (the build would compile the old in-memory state and its save could write it over the change); reload with twincat_open_project(closeCurrentFirst=true) or pass ignoreStaleFiles=true. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | Name of the PLC project to build (e.g. 'PLC'). Leave empty to auto-detect the first .plcproj in the solution. |
| `ignoreStaleFiles` | `bool` | `false` | Build even when PLC source files changed on disk outside the XAE (the in-memory state is built; the result starts with [Warn]). Default false refuses. |

### `twincat_build_solution`

Trigger a full solution build via DTE.Solution.SolutionBuild.Build. Compiles PLC code and IO mapping. Returns { success, failedProjects, errors[] } where success is derived from LastBuildInfo==0. Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). A PLC source file changed on disk outside the XAE since the project was loaded refuses the build with [Error] (the build would compile the old in-memory state and its save could write it over the change); reload with twincat_open_project(closeCurrentFirst=true) or pass ignoreStaleFiles=true. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ignoreStaleFiles` | `bool` | `false` | Build even when PLC source files changed on disk outside the XAE (the in-memory state is built; the result starts with [Warn]). Default false refuses. |

### `twincat_activate_config`

Activate the TwinCAT configuration: ITcSysManager::CheckAllObjects() then ActivateConfiguration(). IMPORTANT: ActivateConfiguration() alone is only 'Save To Registry' — it does NOT physically run the new config, so the PLC keeps the OLD code (root cause of issue #136). Set autoConfirmRestart=true to also call StartRestartTwinCAT() so the runtime reboots into the new config + boot project (this IS the deploy for the 'neu aktivieren' path; it forcibly restarts the PLC). Without it, only the registry is written. With autoConfirmRestart the result is 'Activated' ONLY when the target's TwinCAT system reads Run afterwards: an ADS ReadState on port 10000 of the project's target NetId (the local NetId for a Local target) that reads Run once the restart has taken effect (the target was not in Run, or was seen leaving it) and holds it for 1 s, within runTimeoutSeconds (default 60). The restart call returning is not success. An XAE box raised meanwhile (e.g. 'Zielsystem meldet fatalen Fehler' / 'target reports fatal error') ends the wait with '[Error] Activation failed: <box text> …'. An OK-only box is closed with its OK button ('(the XAE box was closed)'), so the XAE is never left modal; a box that asks a question is never answered and stays open. A deadline without Run returns an [Error] with the last state read. The target is the project's: call twincat_set_target first to activate on a specific runtime. Set autoGenerateLicenses=true to answer the trial-license prompt with 'Ja'; the following security-code dialog is typed only when its code is machine-readable — on TC 4026.x it is not, so the dialog is captured once and CANCELLED (activation must never hang) and the result is an [Error] that names the three-step flow (twincat_open_license_manager_and_request_trial → twincat_generate_trial_licenses → twincat_submit_security_code). Without autoGenerateLicenses an unexpected trial-license prompt is declined and reported as an [Error]. Dialog automation only ever touches windows of the attached XAE, never other applications. Set autoConfirmLogin=true to auto-confirm the 'Applikation … seit dem letzten Download geändert' login modal if it appears (loginMode = 'online_change' | 'download' | 'login_without_change'; updateBootProject ticks 'Bootapplikation aktualisieren'). If online change is not possible for the change, the modal is cancelled and the result is an [Error] unless allowDownloadFallback=true (a download stops the running PLC). To deploy to an ALREADY-RUNNING PLC without a full restart, prefer twincat_login (online-change/download). The system reading Run does not prove the PLC application is up on its own port: verify the new code via an ADS symbol probe (twincat_read_ads_variable with amsNetId). Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `autoConfirmRestart` | `bool` | `false` | Also call StartRestartTwinCAT() to physically restart TwinCAT into the new config (forcibly restarts the PLC) and confirm the restart dialog. Success then requires the target's system state to read Run (see runTimeoutSeconds). Default false (registry only — no deploy). |
| `autoGenerateLicenses` | `bool` | `false` | Answer the trial-license prompt with 'Ja'. An unreadable security code (TC 4026.x) is never guessed: the dialog is cancelled and the three-step licence flow is named in the [Error]. Default false. |
| `autoConfirmLogin` | `bool` | `false` | Automatically confirm the 'application changed since last download' login modal if it appears. Default false. |
| `loginMode` | `string` | `"online_change"` | Login strategy when the app-changed modal appears: 'online_change' \| 'download' \| 'login_without_change'. Default 'online_change'. |
| `updateBootProject` | `bool` | `true` | When confirming via download, also tick 'Bootapplikation aktualisieren' so the new code survives a runtime restart. Default true. |
| `allowDownloadFallback` | `bool` | `false` | loginMode='online_change' only: when online change is not possible, confirm a full download instead (stops the running PLC). Default false — the modal is cancelled and an [Error] returned. |
| `runTimeoutSeconds` | `int?` | `null` | autoConfirmRestart only: seconds to wait after the restart for the target's TwinCAT system (ADS port 10000) to read Run. Default 60, clamped to 5..300. |

### `twincat_get_license_info`

Read the licenses of the project's TARGET system from its TwinCAT license server over ADS (AMS port 30) — what the XAE's License editor shows. Returns per license: status (Present / Trial / Expired / Missing / Unknown), order number, name, id, expiry and used/count. The target must answer over ADS (a Local target is this PC). Requires XAE running with a solution open.

_No parameters._

### `twincat_list_required_licenses`

List the licenses currently marked as required in the TIRC^License node (the 'Manage Licenses' / LicenseRequest section). These are the licenses that will be included in the next trial-license generation. Use twincat_add_required_license to add a license before calling twincat_open_license_manager_and_request_trial. Requires XAE running with a solution open.

_No parameters._

### `twincat_add_required_license`

Mark a license as required in the TIRC^License node so it is included in the next trial-license generation. Provide licenseId as a GUID (e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}') — required; an orderNo alone is rejected because the .tsproj <ManualSelect> list only takes GUIDs. A missing <Licenses><Target> section is created; the entry is read back from the .tsproj after the reload. Idempotent: already-present licenses return success without change. Call twincat_open_license_manager_and_request_trial after to generate the trial. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `licenseId` | `string?` | `null` | License GUID, e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}'. Required. |
| `orderNo` | `string?` | `null` | Order number, e.g. 'SAMPLE-001'. Not sufficient on its own (cannot be resolved to a GUID). |

### `twincat_remove_required_license`

Remove a license from the required list in the TIRC^License node. Provide licenseId (GUID) or orderNo — at least one is required. Idempotent: absent licenses return success without change. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `licenseId` | `string?` | `null` | License GUID to remove. Optional if orderNo is supplied. |
| `orderNo` | `string?` | `null` | Order number to remove, e.g. 'SAMPLE-001'. Optional if licenseId is supplied. |

### `twincat_open_license_manager_and_request_trial`

Navigate to SYSTEM > License in TwinCAT XAE via the COM Automation Interface, then click the 'Aktiviere 7 Tage Testlizenz' / '7 Days Trial License...' button. Uses Win32 child-window search — works for Win32/WinForms License Manager controls. If the button is in a WPF view without a native HWND the click will not land and the response explains what to do manually. After a successful click call twincat_generate_trial_licenses to handle the confirmation and security-code dialogs. Requires XAE running with a solution open.

_No parameters._

### `twincat_generate_trial_licenses`

Handle the 'Some required runtime licenses missing — Generate trial licenses?' dialog. Step 1: clicks 'Ja' (Yes) on the trial-license confirmation dialog. Step 2: waits up to 10 s for the 'Sicherheitscode eingeben' security-code dialog and returns a screenshot of it as an embedded PNG image. Read the displayed 5-character code from the image, then call twincat_submit_security_code(code). Does NOT require a COM session — all interaction is Win32-only. Call twincat_open_license_manager_and_request_trial first if the confirmation dialog is not yet visible.

_No parameters._

### `twincat_submit_security_code`

Enter a known security code into the open 'Sicherheitscode eingeben' dialog and click OK. Use this after twincat_generate_trial_licenses returns an image: read the displayed 5-character code from the embedded PNG, then pass it here. Waits up to 2 s for the post-activation success popup and dismisses it automatically. The dialog must already be visible on screen. Does NOT require a COM session — pure Win32.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `code` | `string` |  | The 5-character code shown in the security-code dialog (e.g. 'bW5Rm'). |

### `twincat_dismiss_security_dialog`

Locate the open 'Sicherheitscode eingeben' (security-code entry) dialog, read the displayed 5-character code from child window texts, type it into the edit field, and click OK. NOTE: on TC3 4026.22 the code is custom-drawn and NOT extractable from child texts — use twincat_generate_trial_licenses (which takes a screenshot) followed by twincat_submit_security_code(code) instead. Does NOT require a COM session — pure Win32, and it deliberately does not wait for the operation lock: the dialog it answers is what a running activation is blocked on. Requires the dialog to already be visible on screen.

_No parameters._

## ClientInfo

### `twincat_get_client_info`

Return the calling MCP client's self-reported identity for THIS session: client name, version, negotiated MCP protocol version, and session id. SECURITY: clientInfo is self-reported and unauthenticated — a client may claim any name/version. Diagnostics only; never use for access control. If the per-call session cannot be resolved, falls back to the most-recently-connected client and says so (the value may be stale).

_No parameters._

## Coe

### `twincat_read_coe_dictionary`

Read the CoE offline dictionary for an EtherCAT slave from its ESI file. Returns each object (index, name, access, objectType) and its sub-indexes (subindex, name, dataType, bitSize, defaultValue, defaultText, access, isSetting). Enum-typed sub-indexes include a human-readable defaultText, e.g. 'IIR level 1 (2)'. Read-only; no activated configuration or live controller required. Result: one-line summary + indented JSON (objectCount, objects[]).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the EtherCAT slave under TIID, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^-1AI5 (EL3174)'. Get a valid path via twincat_get_fieldbus_topology first. |
| `indexFilter` | `string?` | `null` | Optional index filter: single index (e.g. '800D') or hex range (e.g. '8000-80FF'). Omit to return all objects. |

### `twincat_read_coe_startup`

Read the configured Startup SDO-download overrides for an EtherCAT slave as stored in the TwinCAT project. These are the CoE register values that will be written to the device on the PS / SafeOp→Op transition — the answer to 'which registers has the project changed away from the ESI default?'. Returns an empty list (not an error) when no overrides are configured. Read-only. Result: one-line summary + indented JSON (entryCount, entries[]).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the EtherCAT slave under TIID, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^-1AI5 (EL3174)'. Get a valid path via twincat_get_fieldbus_topology first. |

## CoreDump

### `twincat_coredump_list`

Inventory all TwinCAT PLC core dumps for a Usermode Runtime instance/port. File-based — no XAE or running runtime required. Returns each dump's full path, write timestamp, size, kind (AutoException = runtime trap; Manual = engineer-generated; CallStackOnly = no variable values), and whether the matching .compileinfo file is present (required to interpret the dump). Locates auto/exception and call-stack dumps under the UMR instance Boot folders; locates manual dumps in the PLC project folder when projectFolder is supplied.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instanceName` | `string?` | `null` | UMR instance name (e.g. 'UmRT_Template'). Omit to search all registered instances. |
| `port` | `int?` | `null` | ADS port number to filter by (e.g. 851). Omit to show all ports. |
| `projectFolder` | `string?` | `null` | Full path to the PLC project folder (the folder containing the .plcproj file). Required to find manual dumps and the _CompileInfo folder. |

### `twincat_coredump_analyze`

Analyze a TwinCAT PLC core dump. File-based — no running runtime required. Classifies the dump kind; computes the hang gap (last embedded app-log timestamp vs. dump write time — a large gap means the PLC stalled, not trapped); extracts embedded human-readable state strings (app-logger ring buffer, last state-machine steps); lists active (unconfirmed) events by joining the offline LoggedEvents.db against CurrentEventMessages.xml. Pass dumpPath= for a specific file, or use instanceName/port to auto-select the newest dump. Pass projectFolder= to help locate manual dumps, the _CompileInfo, LoggedEvents.db, and CurrentEventMessages.xml.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dumpPath` | `string?` | `null` | Full path to the .core file to analyze. Omit to auto-select the newest dump. |
| `instanceName` | `string?` | `null` | UMR instance name — used to locate the newest dump when dumpPath is omitted. |
| `port` | `int?` | `null` | ADS port filter — used to locate the newest dump when dumpPath is omitted. |
| `projectFolder` | `string?` | `null` | Full path to the PLC project folder (contains .plcproj, _CompileInfo, LoggedEvents.db, CurrentEventMessages.xml). |

### `twincat_coredump_read_callstack`

GUI best-effort: read the symbol-resolved call stack (POU, instance path) from the core dump currently loaded in the TwinCAT XAE IDE. Uses UIAutomation to find editor tab captions that carry the '[Online]' suffix — the top-most tab is the code location of the exception. FRAGILE: requires the dump already loaded in the IDE (PLC → Core Dump → Load core dump — engineer loads it, v1 does NOT automate this). Returns a precise precondition error when no dump is loaded or XAE is unreachable; never fabricates a stack. Loading the dump into the IDE is out of scope for v1.

_No parameters._

## Diagnostics

### `ptc_test_marker`

TEST-ONLY narration marker for the live integration suite. Signals which test is currently running so the host UI can show it live. NOT a TwinCAT/XAE operation: it performs no automation, is not counted as a tool call, and is not subject to the step/pause gate. Real MCP clients should ignore this tool. phase='start' marks a test as current (and increments the running index); phase='end' closes it; phase='current' returns the current test as JSON. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `phase` | `string` |  | Lifecycle phase: 'start', 'end', or 'current'. |
| `testName` | `string` | `""` | The test method name (e.g. Read_BOOL_GvlSafetyDriveReleased). Ignored for phase='current'. |
| `description` | `string?` | `null` | Human-readable narration of the test, from its [Trait("Description")]. Optional. |

### `twincat_show_step`

SHOWCASE composite: locate a step's CASE branch (as twincat_locate_step) and move the visible XAE editor cursor to its label line (as twincat_goto_line, implementation pane). Typical flow: read Step / LastSteps via twincat_read_ads_variables_batch → twincat_show_step(treePath, step) → the audience sees the fault branch in XAE, Online values included. Refuses to touch the editor when the step cannot be located (numeric literal labels only — see twincat_locate_step). Returns the located line/label, the branch source, and the cursor confirmation. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU to search, as used by twincat_read_pou. |
| `step` | `int` |  | Numeric step value to locate and show (as read live via ADS). |
| `stepVariable` | `string?` | `null` | Name of the CASE selector variable. Defaults to 'Step' when omitted. |

### `twincat_safety_recon`

Read-only reconnaissance spike: map HOW the TwinCAT Safety project is reachable via the Automation Interface (Beckhoff documents it as NOT addressable — empirically false). Enumerates dte.Solution.Projects (the safety .splcproj is a SEPARATE VS project, not a child of the PLC system manager), probes each project's COM Object for tree-item-ness and ITcSysManager root shortcuts, and deep-walks any safety-relevant root emitting name/ItemType/ItemSubType/childCount/path + ProduceXml(false). No mutation, no save. Requires XAE running with the safety solution open. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

_No parameters._

### `twincat_safety_probe_create_alias`

WRITE PROBE for the TwinCAT Safety project (DEFAULT read-only, NEVER saves). Resolves the safety root (TISC) and the itemType-800 'Safety Project' node and dumps ProduceXml(false) AND ProduceXml(true) — the key question being whether the alias-device list appears in the COM tree at all. Only when attemptWrite=true does it MUTATE in-memory: a CreateChild subType sweep + a ConsumeXml round-trip, each attempt's HRESULT captured and any created node rolled back. File.SaveAll is NEVER called — nothing is persisted. Requires XAE running with the safety solution open. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `targetPath` | `string?` | `null` | Optional caret-path of the safety node to target (e.g. 'TISC^TwinSAFE_PLC'). Omit to auto-discover the safety root and the itemType-800 node. |
| `aliasName` | `string?` | `null` | Name stem for the probe node. Default 'zz_AItest_Probe'. |
| `subTypes` | `string?` | `null` | Optional CSV of candidate ItemSubType ints (e.g. '48,800,505'). Default sweeps a set. |
| `attemptWrite` | `bool` | `false` | When true, attempt the mutating CreateChild/ConsumeXml sweep (still never saves). Default false = read-only. |

### `twincat_scope_probe`

Research probe for the TwinCAT Scope (measurement) project, used to verify the undocumented Scope View property keys and CreateChild element ids live (issue #153). action: 'dump' (default; every node under nodePath with every property key, value and value type), 'create' (IMeasurementScope.CreateChild(name, elementType) on nodePath; reports the HRESULT and the new node), 'set' (assign value to the property whose key equals `key` exactly, then read it back from a freshly resolved node), 'delete' (ProjectItem.Delete/Remove on nodePath). nodePath is a '^'-separated chain of child names from the scope root (empty = root). Never saves. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` | `"dump"` | 'dump' (default), 'create', 'set' or 'delete'. |
| `nodePath` | `string?` | `null` | '^'-separated child names from the scope root, e.g. 'Trigger' or 'Chart1^Axis1'. Empty = root. |
| `name` | `string?` | `null` | create: name of the new child. |
| `elementType` | `int?` | `null` | create: IMeasurementScope element-type id. |
| `key` | `string?` | `null` | set: exact property key. |
| `value` | `string?` | `null` | set: value, converted to the property's current value type. |
| `projectName` | `string?` | `null` | Measurement project name when the solution has more than one. |

### `twincat_dump_nc_tree`

Read-only diagnostic: dump the TwinCAT NC (TINC/Motion) tree — for every node its name, ItemType, ItemSubType, child count, path, and full ProduceXml(false). Requires XAE running with a solution open. Mirrors the PLC TIPC dump; used to discover NC-Task/axes/axis type discriminators and the axis parameter schema. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

_No parameters._

### `twincat_open_plc_object`

DEPRECATED — use twincat_open_object (this is a thin alias kept for back-compat). Opens a PLC source object (POU/GVL/DUT/INTERFACE) in the VISIBLE TwinCAT XAE editor and focuses it. Delegates to twincat_open_object, which also handles system-manager nodes. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the PLC source object to open (POU/GVL/DUT/INTERFACE). |

### `twincat_close_plc_object`

DEPRECATED — use twincat_close_object (this is a thin alias kept for back-compat). Closes the VISIBLE TwinCAT XAE editor tab for a PLC source object (saves first). Delegates to twincat_close_object. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the PLC source object to close, same as passed to twincat_open_plc_object. |

### `twincat_ibn_arm`

TEST SUPPORT ONLY — never registered for released clients; in production IBN Live is armed only by a human in the host window (Approvals view). Open a time-boxed IBN control window (auto-expiry, ≤120 s, renewable). Loads the actuator allowlist and starts the heartbeat that writes the test PRG's bEnable bit — without an active arm window every twincat_ibn_propose is refused, and without the heartbeat the PRG falls back to its home position. Returns JSON { armed, armToken, expiresUtc, ttlSeconds, gvlPath, allowlist }. Arming does NOT move anything. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ttlSeconds` | `int?` | `null` | Control-window lifetime in seconds (default 60, max 120). The window auto-expires; re-arm to renew. |
| `actuatorAllowlist` | `string[]?` | `null` | Actuator ids permitted to be addressed by a proposal during this window. A proposal naming an actuator not on this list is refused. |
| `gvlPath` | `string?` | `null` | Base symbol path of the test PRG control/status struct. Default 'GVL_IBN'. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID of the target ADS server. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_test_confirm`

TEST SUPPORT ONLY - never registered for released clients; in production a restore is confirmed only by a human in the host popup (Approvals view). Confirms the pending restore proposal restoreId as the popup's OK would: the host re-plans, writes and reads back. Returns JSON { restoreId, status, reason }. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `restoreId` | `string` |  | The restoreId returned by twincat_persistent_snapshot_restore. |

## EditorNavigation

### `twincat_locate_step`

HEADLESS: find the CASE branch of a step in an ST POU's step chain (FB_Base style 'CASE Step OF … 1010: …'). treePath is the caret-separated POU path (as twincat_read_pou); step is the numeric step value you read via ADS (e.g. MAIN.fbMachine.Step); stepVariable is the CASE selector (default 'Step'). Only NUMERIC LITERAL labels are matched ('1010:' or a comma list '100, 1110:'); ranges, constants and enum labels are listed in the error but never matched — on a miss, read the POU yourself and call twincat_goto_line with the line you determined. Returns the 1-based IMPLEMENTATION line (= the implementation pane's gutter number, NOT the .TcPOU file line), the label, the branch end line and the branch source so you can quote it. Does not touch the editor.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU to search, as used by twincat_read_pou. |
| `step` | `int` |  | Numeric step value to locate (as read live via ADS, e.g. MAIN.fbMachine.Step). |
| `stepVariable` | `string?` | `null` | Name of the CASE selector variable. Defaults to 'Step' when omitted. |

### `twincat_goto_line`

VISIBLE: put the editor cursor on a line of a POU in the running TwinCAT XAE so a live demo shows exactly the code being discussed. Opens the object's tab (as twincat_open_object), focuses the requested pane via UIAutomation and runs the VS Edit.GoTo command, answering its 'Go To Line' dialog. line is 1-based within the pane: section='implementation' (default) counts lines of the implementation pane (= twincat_read_pou's implementation text split on newlines), section='declaration' counts the declaration pane. Works while Online (no logout; monitoring values stay visible) — but the declaration pane is the monitoring grid while Online, so use the implementation section then. Confirmation is structural (the Go-To dialog was seen and closed on OK); there is no caret read-back. The OS foreground raise is best-effort and reported, never an error.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU to open, as used by twincat_read_pou. |
| `line` | `int` |  | 1-based line number within the pane selected by 'section'. |
| `section` | `string?` | `null` | Which pane 'line' counts within: 'implementation' (default) or 'declaration'. |

## EtherCat

### `twincat_add_ethercat_device`

Create a top-level EtherCAT device under TIID — a master (default) or slave. Requires XAE running with a solution open; saves the project. Returns the new device tree path; feed that path to twincat_add_ethercat_box (boxesJson) to build the topology under it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Device name as it appears under TIID, e.g. '100000 SampleMachine (EtherCAT)'. |
| `deviceKind` | `string` | `"master"` | 'master' (default) or 'slave'. |

### `twincat_add_ethercat_box`

Add an EtherCAT box/terminal — or a whole subtree/topology — under a parent TIID node, using the same proven engine as twincat_apply_schema. Requires XAE running with a solution open; saves the project. Provide EITHER 'vInfo' for a single box, OR 'boxesJson' for a nested tree (the apply_schema ethercat node shape: {name,vInfo,previousPort,children[]}; a single object, an array of siblings, a {root:{…}} fragment or a full schema file are all accepted). vInfo may be a bare type (e.g. EL9011 — latest revision auto-selected) or revisioned (e.g. AX5206-0000-0202).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Parent tree path under TIID, e.g. 'TIID^Device 1 (EtherCAT)' or 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. Get a valid path via twincat_get_fieldbus_topology. The bare root 'TIID' is rejected. |
| `vInfo` | `string?` | `null` | Single-box mode: product type/revision (bare 'EL9011' or 'AX5206-0000-0202'). Mutually exclusive with boxesJson. |
| `name` | `string?` | `null` | Optional designation/name for the new box (e.g. 'A1.49 (EL9011)'). Defaults to vInfo when omitted. Single-box mode only. |
| `previousPort` | `string?` | `null` | Optional upstream port letter A/B/C/D for the new box. Single-box mode only. |
| `beforeSibling` | `string?` | `null` | Optional name of the existing sibling to insert before (ordering). Default appends at the end. |
| `boxesJson` | `string?` | `null` | Subtree/topology mode: JSON in the apply_schema ethercat node shape. Mutually exclusive with vInfo. |

### `twincat_list_ethercat_box_catalog`

List installable EtherCAT box/terminal types and their vInfo strings for twincat_add_ethercat_box by scanning the local ESI files (<TwinCAT>\3.1\Config\Io\EtherCAT\**\*.xml) — offline, no XAE, no open solution, no hardware. Per device: vInfo (revision-exact, e.g. EL1008-0000-0017), ESI Type (order code), name (English preferred), productCode, revisionNo, vendor and source file. A bare type such as EL1008 is also accepted by twincat_add_ethercat_box (TwinCAT then picks the latest installed revision). Malformed ESI files are skipped and counted. The first scan of the full directory can take several seconds; results are cached per process until an ESI file changes. Result body is a one-line summary followed by indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filter` | `string?` | `null` | Case-insensitive substring of the ESI Type/vInfo, e.g. 'EL1008', 'EK11' or 'AX52'. Strongly recommended — without it the first maxResults of several thousand types are returned. |
| `maxResults` | `int` | `EsiCatalogScanner.DefaultMaxResults` | Maximum number of devices returned (default 100, capped at 1000). The summary/JSON report the total match count and whether the list was truncated. |
| `latestRevisionOnly` | `bool` | `true` | true (default): only the newest revision per product/variant. false: every revision found in the ESI files. |

### `twincat_remove_ethercat_box`

Delete an EtherCAT box/terminal node by tree path and save the project. Requires XAE running with a solution open. The bare root 'TIID' and top-level nodes without a parent are rejected.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the box to delete, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 4 (EL2004)'. Get a valid path via twincat_get_fieldbus_topology. |

### `twincat_reload_device_descriptions`

Make the XAE re-read the EtherCAT device descriptions (ESI files under <TwinCAT>\3.1\Config\Io\EtherCAT): the XAE command 'TwinCAT > EtherCAT Devices > Reload Device Descriptions', raised by its command identity, so it works on any XAE language. Run it after placing a vendor ESI there; until then twincat_add_ethercat_box fails for that type with 'Invalid item sub type'. Requires XAE running with a solution open; changes nothing in the project. A dialog the XAE shows is reported, never answered.

_No parameters._

### `twincat_set_previous_port`

Set/override an EtherCAT box's upstream PreviousPort (A/B/C/D) and save. Requires XAE running with a solution open. Use this to attach a box to a specific port of a multi-port coupler (e.g. EK1122), or to repair a box added without an explicit port.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the box, e.g. 'TIID^Device 1 (EtherCAT)^Box 2 (EK1122)^Box 3 (EK1100)'. |
| `port` | `string` |  | Upstream port letter to select: A, B, C or D. |

## EventLogger

### `twincat_read_event_logger`

Read the TwinCAT 3 RUNTIME EventLogger (logged events + currently active alarms) via the EventLogger ADS proxy. This is NOT the Visual Studio Error List (use twincat_clear_error_list for that) — it is the runtime event/alarm log: message text, severity, source, timestamps, alarm/confirmation state. Returns JSON { success, loggedCount, activeAlarmCount, message, events[], activeAlarms[] } or an [Error] string. Requires the TwinCAT runtime reachable on the target.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `maxEvents` | `uint` | `0` | Max number of logged events to return. 0 or omitted = 100. |
| `minSeverity` | `string?` | `null` | Optional minimum severity filter: Verbose \| Info \| Warning \| Error \| Critical. Omit for all severities. |
| `amsNetId` | `string?` | `null` | Optional AMS NetId of the target runtime. Omit/empty = local runtime (single-XAE constraint: local only). |

### `twincat_confirm_event_logger`

Acknowledge/clear the TwinCAT 3 runtime EventLogger. mode: 'confirm' = ConfirmAllAlarms (acknowledge alarms waiting for confirmation); 'clear-alarms' = ClearAllAlarms (clear raised alarms, reset confirmation); 'clear-events' = ClearLoggedEvents (empty the event cache); 'all' = confirm + clear-alarms + clear-events. Self-verifies via a pre/post active-alarm count delta. Returns JSON { success, mode, activeAlarmsBefore, activeAlarmsAfter, message } or an [Error] string. Requires the TwinCAT runtime reachable.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` |  | One of: confirm \| clear-alarms \| clear-events \| all. |
| `amsNetId` | `string?` | `null` | Optional AMS NetId of the target runtime. Omit/empty = local runtime. |

## Fieldbus

### `twincat_get_fieldbus_topology`

Walk the TwinCAT TIID I/O tree and return a JSON topology (devices, boxes, slaves, channels). Read-only. Requires XAE running with a solution open. Result body is a one-line summary followed by indented JSON (deviceCount, devices[]).

_No parameters._

### `twincat_get_device_xml`

Return the raw TwinCAT ProduceXml(false) for a single TIID tree node. Read-only. Requires XAE running with a solution open. treePath must be a non-root caret-separated path under TIID; the root 'TIID' itself is rejected to prevent enormous dumps.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path under TIID, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. Get a valid path via twincat_get_fieldbus_topology first. |

### `twincat_configure_ethercat_slave`

Write device-configuration XML to an EtherCAT slave node via ConsumeXml, then read the node back (ProduceXml) and verify that every written leaf value is there; values XAE ignored or changed are returned as [Error]. Side effect: saves ALL dirty documents of the solution (File > Save All), not only this node. Requires XAE running with a solution open. Obtain current XML first via twincat_get_device_xml, modify it, then pass it back here. treePath must be a non-root caret-separated path under TIID.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the slave to configure, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. Get a valid path via twincat_get_fieldbus_topology first. |
| `xml` | `string` |  | Full device-configuration XML string to write (TwinCAT ConsumeXml format). Obtain the current XML via twincat_get_device_xml and modify as needed. |

## Hmi

### `twincat_hmi_list_projects`

List the TwinCAT HMI (TE2000) projects of the solution open in the XAE, via the HMI Automation Interface (dte.GetObject("Beckhoff.TcHmi.1.12")). Per project: name, projectDirectory, isReady, isPublishRunning. Also reports automationVersion and, if open, the XAE window 'Activate Engineering Server Trial License' (a CAPTCHA only the engineer can answer — the HMI engineering server does not work until then). Read-only. Result: one-line summary + indented JSON.

_No parameters._

### `twincat_hmi_get_project_info`

Details of one TwinCAT HMI project: project config (ActiveTheme, Locale, ScaleMode, StartupView, LoginPage, websocket timeouts), the server extensions of its HMI engineering server (domainName, state, loaded, version — e.g. ADS, TcHmiSrv, TcHmiUserManagement) and, with includeFiles, the project tree (views, content, server config …; max 400 nodes). Read-only. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `includeFiles` | `bool` | `false` | Also return the project tree (ITcHmiItem children). Default false. |
| `maxDepth` | `int` | `2` | Tree depth when includeFiles is true, 1..6. Default 2. |

### `twincat_hmi_list_mapped_symbols`

List the symbols mapped in a TwinCAT HMI project's engineering server, as the HMI Automation Interface reports them (compare with the offline extraction of stage 33). Per symbol: mappedName (e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'), domain (e.g. ADS), type, dataTypeDisplayName, readOnly, hidden. Filterable by domain and name prefix. CAUTION (verified live): this list is a cached view — a map/unmap done after the project was loaded is NOT reflected, even with refresh=true. For the live state of one symbol use twincat_hmi_read_symbols (SYMBOL_NOT_MAPPED = not mapped). Read-only. Result: one-line summary + indented JSON { hmiProject, total, count, symbols[] }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `domain` | `string?` | `null` | Only symbols of this server domain, e.g. 'ADS'. Case-insensitive. Omit for all. |
| `namePrefix` | `string?` | `null` | Only symbols whose mapped name starts with this, e.g. 'ADS.PLC1.GVL_HMI.'. Case-insensitive. |
| `includeHidden` | `bool` | `false` | Include symbols flagged hidden. Default false. |
| `refresh` | `bool` | `false` | Ask the server to refresh its symbol list before answering. Default false. |

### `twincat_hmi_read_symbols`

Read symbol values THROUGH the HMI engineering server (what the HMI would display), e.g. 'ADS.PLC1.MAIN.fbHmi.bStart' or a server symbol like 'UserSelectType'. Batch read; a symbol that cannot be read does NOT fail the batch — it appears with ok=false and the server's error (e.g. SYMBOL_NOT_MAPPED, HMI_ADS_E_SYNC_TIMEOUT when the PLC runtime is unreachable). The value is returned exactly as the server reports it — NOT guaranteed JSON (a BOOL reads as 'False'). Compare with twincat_read_ads_variable to prove a binding end-to-end. Read-only, max 200 names. Result: one-line summary + indented JSON { hmiProject, count, ok, results[] }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbols` | `string[]` |  | HMI symbol names to read, e.g. ['ADS.PLC1.GVL_HMI.HmiOpened', 'UserSelectType']. At least one required. |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_server_control`

Start, shut down or refresh the symbols of a TwinCAT HMI project's ENGINEERING server — the local TcHmiSrv the XAE runs for this project (live view, tests), NOT the TF2000 runtime server on a target. The XAE starts it automatically when the project opens; use 'start' after a shutdown/crash and 'refresh_symbols' after PLC symbol changes. 'start' waits until at least one server extension is loaded, 'shutdown' until none is (bounded by timeoutMs). Changes no project file. Result: one-line summary + indented JSON (extensionsAfter[]).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | start \| shutdown \| refresh_symbols |
| `hmiProject` | `string?` | `null` |  |
| `timeoutMs` | `int` | `30000` | How long to wait for the server to settle, 0..120000 ms. Default 30000. |

### `twincat_hmi_map_symbol`

Map a PLC symbol into a TwinCAT HMI project's server so views can bind to it.  Easiest: plcPath 'MAIN.fbHmi.bStart' becomes mapName 'ADS.PLC1.MAIN.fbHmi.bStart' and internalName 'PLC1::MAIN::fbHmi::bStart' (the convention of all existing mappings, verified on two real projects). Arrays / other domains: pass mapName + internalName explicitly. Refuses when the name is already mapped (the API does not reveal an existing mapping's target, so it is never re-mapped silently). Success only after a read-back finds the mapping. An ADS map needs the PLC runtime of the ADS extension to be reachable. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcPath` | `string?` | `null` | Dotted PLC path, e.g. 'MAIN.fbHmi.bStart' or 'GVL_HMI.HmiOpened'. Alternative to mapName + internalName. |
| `mapName` | `string?` | `null` | Explicit mapped name, e.g. 'ADS.PLC1.GVL_HMI.HmiOpened'. Requires internalName. |
| `internalName` | `string?` | `null` | Explicit internal name inside the domain, e.g. 'PLC1::GVL_HMI::HmiOpened'. Requires mapName. |
| `domain` | `string?` | `null` | Server domain. Default 'ADS'. |
| `runtime` | `string?` | `null` | ADS runtime name used to derive names from plcPath. Default 'PLC1'. |
| `subSymbolName` | `string?` | `null` | Optional sub-symbol (ITcHmiServer3.MapSymbol overload). |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_unmap_symbol`

Remove a symbol mapping from a TwinCAT HMI project's server.  Pass mapName (e.g. 'ADS.PLC1.MAIN.fbHmi.bStart') or plcPath (derived like twincat_hmi_map_symbol). A name that is not mapped returns 'Nothing to do' without a popup. Success only after a read-back no longer finds the mapping. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mapName` | `string?` | `null` | Mapped name to remove, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart'. Alternative to plcPath. |
| `plcPath` | `string?` | `null` | Dotted PLC path, e.g. 'MAIN.fbHmi.bStart' (ADS names derived). Alternative to mapName. |
| `domain` | `string?` | `null` | Server domain of the mapping. Default 'ADS'. |
| `runtime` | `string?` | `null` | ADS runtime name used to derive the name from plcPath. Default 'PLC1'. |
| `removeHistorizedData` | `bool` | `false` | Also delete historized data of the symbol. Default false. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_write_symbol`

Write a value THROUGH the HMI server of a TwinCAT HMI project. For an ADS-mapped symbol ('ADS.PLC1.…') this WRITES THROUGH TO THE LIVE PLC — physically effective like twincat_write_ads_variable.  The popup shows the current and the new value. Refuses unmapped (SYMBOL_NOT_MAPPED) and read-only symbols before asking. valueJson must be valid JSON (true, 42, 3.5, "text", {…}). After the write the value is read back; a differing read-back is reported as a warning (verified=false), because the PLC may change the value at once. Result: one-line summary + indented JSON { written, before, after, verified, writesToPlc }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | HMI symbol name, e.g. 'ADS.PLC1.MAIN.fbHmi.bStart' or a server symbol like 'UserSelectType'. |
| `valueJson` | `string` |  | The value as JSON: true, 42, 3.5, "text" (strings quoted), or an object/array for structs. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiInternalSymbols

### `twincat_hmi_list_internal_symbols`

List the HMI-owned symbols of a TwinCAT HMI project — variables with no PLC behind them, read from the files TE2000 persists them in (works without the HMI server running). Two kinds, kept apart: client = framework internal symbols %i%Name%/i% from Properties\tchmiconfig.json (symbols.internal: name, type, valueJson, persist, readOnly); server = the non-hidden symbols of the TcHmiSrv domain from Server\TcHmiSrv\TcHmiSrv.Config.default.json (name, mapping, useMapping — false means a pure offline server variable —, dataType, defaultJson, persist). An unreadable server file is reported as unknown, never as 'none'. Read-only. Result: one-line summary + indented JSON { hmiProject, client{file,count,symbols[]}, server{file,readable,count,offline,symbols[]} }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_add_internal_symbol`

Add a CLIENT (framework) internal symbol %i%name%/i% to a TwinCAT HMI project (ITcHmiProject.AddInternalSymbol; stored in Properties\tchmiconfig.json) — HMI-side state such as a selection, a page index or a dialog flag that needs no PLC variable.  Refuses a name that already exists (TE2000 adds without a duplicate check and would store it twice). Success only after tchmiconfig.json reads the new entry back — the Automation Interface call returns nothing; differences in the stored type/value/flags are reported as a WARNING. Not a server symbol: for one the HMI server holds, use twincat_hmi_map_offline_symbol. Reversible with twincat_hmi_remove_internal_symbol. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Symbol name — letters, digits and '_', starting with a letter or '_', max 128. Referenced as %i%name%/i%. |
| `dataType` | `string` |  |  |
| `defaultValueJson` | `string?` | `null` |  |
| `persist` | `bool` | `false` | Framework-side persistence of the value in the client (not the server, not historizing). Default false. |
| `readOnly` | `bool` | `false` | Mark the symbol read-only for bindings. Default false. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_remove_internal_symbol`

Remove a CLIENT (framework) internal symbol %i%name%/i% from a TwinCAT HMI project (ITcHmiProject.RemoveInternalSymbol).  A name that does not exist returns 'Nothing to do' without a popup (names are case-sensitive). Success only after the entry is gone from Properties\tchmiconfig.json on read-back — the Automation Interface returns false here even on success, so its return value is ignored. Bindings that still reference the symbol no longer resolve. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name of the internal symbol, as twincat_hmi_list_internal_symbols reports it. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_map_offline_symbol`

Create a SERVER symbol in the TcHmiSrv domain of a TwinCAT HMI project's engineering server (ITcHmiServer.MapOfflineSymbol; the server persists it in Server\TcHmiSrv\TcHmiSrv.Config.default.json) — a variable the HMI SERVER holds, shared by all clients, with no PLC behind it; bind to it like any mapped symbol. online=false (default) makes a pure offline server variable whose value starts at the default; online=true sets USEMAPPING (unverified live — prefer the default). persist keeps the last value across server restarts (OPTIONS.persistedOfflineValue). Needs the HMI engineering server running.  Refuses a name that the server configuration or the running server already knows. Success only after the symbol's config reads back with DOMAIN TcHmiSrv and the requested USEMAPPING — live (TcHmiSrv.Config::SYMBOLS::<mapName>), else from the saved config file. Remove it with twincat_hmi_unmap_symbol (domain 'TcHmiSrv'). Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mapName` | `string` |  | Server symbol name — dotted identifiers (letters, digits, '_'), e.g. 'Recipe.SelectedIndex', max 256. |
| `dataType` | `string` |  |  |
| `defaultValueJson` | `string?` | `null` |  |
| `online` | `bool` | `false` | USEMAPPING of the new symbol. Default false = offline server variable. true is unverified live. |
| `persist` | `bool` | `false` | Server keeps the last value across restarts (OPTIONS.persistedOfflineValue). Default false. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiTmc

### `twincat_hmi_list_tmc_files`

List the TMC files attached to a TwinCAT HMI project — the PLC symbol descriptions the HMI engineering can use WITHOUT a PLC connection. Read from the project's HmiTmcFiles property, so it reflects what was saved. Per entry: virtualPlcName (rootNamespace), the stored path (relative to the solution directory when the file lies below it), plcName, the resolved absolute path and whether that file still exists. Also reports, separately, which ADS runtimes serve symbols from a TMC file server-side (USE_TMC in the ADS extension config) — a different mechanism with the same purpose. Read-only. twincat_hmi_attach_tmc_file only reads a .tmc under an allowed root: the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation, or a root listed in the host's PTC_XAE_MCP_ALLOWED_PATHS environment variable (';'-separated). Result: one-line summary + indented JSON { hmiProject, count, attachments[], adsRuntimesUsingTmc[] }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_list_ads_runtimes`

List the ADS runtimes of a TwinCAT HMI project's engineering server, read live from the server symbol 'ADS.Config::RUNTIMES'. Per runtime: name (e.g. PLC1), enabled, netId, port, readOnly, useWhitelisting, symbolCount and useTmc plus tmcFile (server-side TMC: the extension takes its symbol description from a converted binary instead of uploading it over ADS). Use it to tell 'this runtime's PLC is unreachable' apart from 'this runtime needs no PLC', and to get the NetId/port a failing binding actually points at. Switch a runtime to a TMC file with twincat_hmi_enable_runtime_tmc. Read-only. Result: one-line summary + indented JSON { hmiProject, count, usingTmc, runtimes[] }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_list_plc_tmc_candidates`

List the TMC file of every PLC project in the open solution — the CANDIDATES for twincat_hmi_attach_tmc_file, found the way TE2000's 'Browse solution for TMC file' dialog does: each TwinCAT project's TIPC children, their PlcProjectDef/RelativeTmcPath, resolved against the directory of the .tsproj. Per candidate: tcProject, plcProject, relativeTmcPath, absolutePath, exists, lastWriteUtc and symbolCount (the <Symbol> entries of the file; null when it was not opened — missing, or outside the roots twincat_hmi_attach_tmc_file may read) plus a note saying why a field is empty. Needs no HMI project. Read-only. It does NOT choose: which PLC project belongs to which HMI is a project decision, so pass one absolutePath explicitly as tmcFile. Result: one-line summary + indented JSON { count, existing, candidates[] }.

_No parameters._

### `twincat_hmi_attach_tmc_file`

Attach a PLC project's TMC file to a TwinCAT HMI project, so the HMI engineering knows the PLC symbols WITHOUT ANY PLC CONNECTION (the TE2000 'Attach TMC file' function; the symbols are then browsable and bindable offline). tmcFile is the absolute path of the file the XAE generates for a PLC project, '<PlcProject>\<PlcProject>.tmc' — NOT the HMI_Error/HMI_Info/HMI_Warning.tmc event classes in a project root, which carry no PLC symbols. The file is opened and identified first: it must be a <TcModuleClass> document with at least one <Symbol>; its symbol count and size are shown in the confirmation popup. virtualPlcName defaults to the file name without extension, exactly as the TE2000 dialog derives it. tmcFile must lie under an allowed root — the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation, or a root listed in the host's PTC_XAE_MCP_ALLOWED_PATHS environment variable (';'-separated); any other path is refused with [Error] before the file is opened.  Refuses a file that is already attached and a virtual PLC name already in use, because TE2000 answers those with a modal warning box in the XAE that no agent can dismiss. Success only after the project's HmiTmcFiles property reads the new entry back — the Automation Interface itself reports true even when it failed. Reversible with twincat_hmi_detach_tmc_file. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmcFile` | `string` |  | Absolute path of the PLC project's .tmc file. The XAE writes it next to the PLC project as <PlcProject>\<PlcProject>.tmc. It must lie under the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS. |
| `virtualPlcName` | `string?` | `null` | Virtual PLC name to attach it under — letters, digits, '_' or '-', max 64 characters. Default: the .tmc file name without extension. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_detach_tmc_file`

Remove an attached TMC file from a TwinCAT HMI project by its virtual PLC name — its symbols are then no longer known offline.  A name that is not attached returns 'Nothing to do' without a popup. Success only after the project's HmiTmcFiles property no longer holds the entry — the Automation Interface returns false here even on success, so only the read-back counts. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `virtualPlcName` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_enable_runtime_tmc`

Make an ADS runtime of a TwinCAT HMI project's engineering server serve its PLC symbols from the PLC project's TMC file instead of uploading them from a PLC — server-side USE_TMC, so mapping, reading and binding PLC symbols work WITHOUT ANY PLC (unlike twincat_hmi_attach_tmc_file, which only feeds the engineering: the server has no 'TMC' domain). What TE2000 does for a new project, done on demand: the .tmc is converted with TwinCAT's own type system (TcTypeSystem.dll, in-process) into the binary ADS symbol segment '<runtime>_<tmc>.DATA_UPLOADINFO2.bin', written to 'Tmc\' below the engineering server's working directory (<solution>\.engineering_servers\<project>, created when the server starts), then ADS.Config::RUNTIMES::<runtime>::TMC::DATA_SYMBOLUPANDDOWNLOAD is set to that relative path and ::USE_TMC to true. tmcFile is the PLC project's '<PlcProject>.tmc' (NOT the HMI_Error/Info/Warning.tmc event classes) under an allowed root — the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation, or a root in the host's PTC_XAE_MCP_ALLOWED_PATHS (';'-separated); any other path is refused before the file is opened. The file is identified and converted BEFORE the popup, so a file TwinCAT cannot read never reaches it.  Success only after ADS.Config::RUNTIMES reads back USE_TMC=true with the written path — whether the ADS extension already serves the symbols is not checked (read one with twincat_hmi_read_symbols). Re-running it regenerates the file from the current .tmc. Reversible with twincat_hmi_disable_runtime_tmc. NOT yet verified against a live HMI server (#161). Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `runtime` | `string` |  |  |
| `tmcFile` | `string` |  | Absolute path of the PLC project's .tmc file (the XAE writes it as <PlcProject>\<PlcProject>.tmc; twincat_hmi_list_plc_tmc_candidates lists them). It must lie under the open solution's directory, %TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_disable_runtime_tmc`

Switch an ADS runtime of a TwinCAT HMI project's engineering server back from a TMC file to the ADS upload from the PLC: DATA_SYMBOLUPANDDOWNLOAD = "" then USE_TMC = false, exactly as TE2000's own detach. Without a reachable PLC the runtime then has no symbols again. A runtime that uses no TMC file returns 'Nothing to do' without a popup.  Success only after ADS.Config::RUNTIMES reads back USE_TMC=false without a file. The converted .bin is left on disk (engineering server scratch folder). NOT yet verified against a live HMI server (#161). Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `runtime` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiViews

### `twincat_hmi_read_file`

Read the source text of a file of a TwinCAT HMI project (view, content, user control, .usercontrol.json, function .js/.function.json, CSS …) via ITcHmiFile.GetSource. Read-only. Result: one-line summary + indented JSON { path, length, lines, source }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_add_item`

Create a new file in a TwinCAT HMI project from a TE2000 item template (ITcHmiItem.AddItem), creating missing folders. kind: view, content, usercontrol (creates .usercontrol + .usercontrol.json), function_js / function_ts (creates .js/.ts + .function.json, namespace = project name), codebehind_js, codebehind_ts, css, html, js, json, ts, text (.txt), xml. html, view and text are created by importing a correctly named file (empty; view = the vendor view skeleton) because their TE2000 templates misname the file (x.html.html, x.view.view, x.text.txt). Refuses when the file exists.  Result: one-line summary + indented JSON (created paths).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` |  | Template kind, see description. |
| `folder` | `string?` |  | Target folder, project-relative with '/', e.g. 'UserControls/PtcCamCurve'. The parameter must be passed, but its value may be null or empty — both mean the project root. |
| `name` | `string` |  | File name without folder; the template's extension is added, e.g. 'PtcCamCurve'. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_write_file`

Replace the source text of an EXISTING file of a TwinCAT HMI project (ITcHmiFile.SetSource), then save and verify by read-back. Pass the text as 'source' or an absolute 'sourceFile' (max 5 MB). For JSON files the project's own "$schema" value is kept (keepSchema, default true) because it points into the project's Packages folder. Identical content → 'Nothing to do' without a popup.  Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `source` | `string?` | `null` | The complete new file content. Alternative to sourceFile. |
| `sourceFile` | `string?` | `null` | Absolute path of a local file whose content is written. Alternative to source. |
| `keepSchema` | `bool` | `true` | Keep the existing "$schema" value of a JSON file. Default true. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_import_file`

Copy an external file (e.g. an HTML document, image, PDF, JavaScript library) into a TwinCAT HMI project (ITcHmiItem.AddExistingItem), creating the folder when missing. Refuses when the target exists. When TE2000 raises an error although the file was added (seen for .js), the import counts only if the copy on disk is byte-identical (engineError, verifiedOnDisk). For .js/.css the result says how Properties/tchmiconfig.json lists it in dependencyFiles (dependencyType); run twincat_hmi_build afterwards so the live view loads it.  Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `sourcePath` | `string` |  | Absolute path of the file to import. |
| `folder` | `string?` | `null` | Target folder, project-relative with '/'. Empty = project root. |
| `name` | `string?` | `null` | Target file name. Default: the source file name. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_build`

Build a TwinCAT HMI project (ITcHmiProject.Build) so new user controls, functions and contents reach the engineering server's live view. Changes no source file (no popup). On failure see twincat_get_error_list. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `configuration` | `string?` | `null` | Solution configuration. Default 'Debug'. |

### `twincat_hmi_apply_bundle`

Create/write a whole package of HMI files from a manifest with ONE engineer confirmation — e.g. a user control with its .usercontrol.json and function. Manifest (JSON): { "name": "…", "entries": [ { "path": "UserControls/X/X.usercontrol", "kind": "usercontrol", "source": "X.usercontrol" }, { "path": "UserControls/X/X.usercontrol.json", "source": "X.usercontrol.json" } ] } — 'source' relative to the manifest; existing files are written, missing ones created from 'kind' first; a dependent file (.usercontrol.json, .function.json) needs no kind when an earlier entry's template creates it. JSON files keep the project's "$schema". Everything is validated before the popup; afterwards the entries run in order and stop at the first error (reporting what was already written).  Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `manifestPath` | `string` |  | Absolute path of the bundle manifest (JSON). |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_delete_item`

Delete a file or folder of a TwinCAT HMI project (ITcHmiItem.DeleteChild) — removed from the project AND from the disk, no recycle bin. Refused without a popup: the project infrastructure (Server, Properties, Packages), the startup view and folders containing it; a missing item is reported without a popup. The popup shows file or folder and how many items lie below it.  Success only when the item is gone afterwards. Result: one-line summary + indented JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HostSelection

### `twincat_resolve_host`

Diagnostic: resolve which TwinCAT XAE Shell host a project's declared .tsproj TcVersion selects (>= 3.1.4026 -> 64-bit ProgID TcXaeShell.DTE.17.0, otherwise 32-bit TcXaeShell.DTE.15.0). Reports the parsed TcVersion, host class, chosen ProgID and exe path, whether that exe is installed, and whether the host is currently running. Pure read-only; no project is opened or modified. If no path is given, uses the currently-open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tsprojOrSlnPath` | `string?` | `null` | Absolute path to a .tsproj or .sln. Optional — defaults to the currently-open solution. |

## IbnLive

### `twincat_ibn_disarm`

Close the IBN control window: stop the heartbeat and command the test PRG home (disarm is a watchdog trigger, so it returns the machine to its home position). Always allowed — it reduces risk. Arming happens in the host window (Approvals view); there is no MCP tool to arm. Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

### `twincat_ibn_propose`

Propose a step trigger. Moves NOTHING — it creates a PENDING proposal, opens the host's modal confirm popup, and returns JSON { proposalId, status: "pending" }. The trigger is executed ONLY if a human confirms the popup; there is no AI path to execute it. Requires an active arm window, which only a human opens in the host window (Approvals view) — there is no MCP tool to arm; without it the proposal is refused with an [Error]. An actuator id must be on the allowlist the operator armed. Poll twincat_ibn_get_proposal_status for the outcome.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | Step action: 'start_chain' \| 'next_step' \| 'hold'. |
| `chainId` | `string?` | `null` | Optional id of the test chain this trigger targets. |
| `actuatorId` | `string?` | `null` | Optional actuator id; if given it must be on the armed allowlist. |

### `twincat_ibn_get_proposal_status`

Read-only poll of a proposal: returns JSON { proposalId, status, action, chainId, actuatorId, createdUtc, expiresUtc, result }. Status is one of pending | confirmed | executed | rejected | expired. Reads only; moves nothing.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `proposalId` | `string` |  | The proposalId returned by twincat_ibn_propose. |

### `twincat_list_active_chains`

Read-only snapshot of the running IBN test chains from the test PRG status struct: returns JSON { success, gvlPath, chain: { running, activeChain, currentStep, fault }, raw: [{ symbol, value, ok, message }] }. Reads the gvlPath/target armed in the host window unless overridden. Moves nothing.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `gvlPath` | `string?` | `null` | Base symbol path of the test PRG status struct. Omit to use the armed gvlPath (default 'GVL_IBN'). |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the armed/local target. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_measure_signal_transition`

Measure the elapsed time between two symbol conditions (edge A → edge B) by polling, bounded by a timeout. A READ-ONLY cross-check / fallback for the test PRG's internal PLC-timer measurement (which is more deterministic). Moves nothing. Returns JSON { success, elapsedMs, timedOut, message } or an [Error] string. Conditions compare the symbol's rendered value to the *Equals string (e.g. 'true').

| Parameter | Type | Default | Description |
|---|---|---|---|
| `startSymbol` | `string` |  | Symbol whose condition starts the measurement, e.g. 'GVL_IO.bCyl1Cmd'. |
| `endSymbol` | `string` |  | Symbol whose condition ends the measurement, e.g. 'GVL_IO.bCyl1Extended'. |
| `startEquals` | `string?` | `null` | Value the start symbol must equal to begin timing. Default 'true'. |
| `endEquals` | `string?` | `null` | Value the end symbol must equal to stop timing. Default 'true'. |
| `timeoutMs` | `int?` | `null` | Overall timeout in milliseconds (default 5000, max 60000). |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_ibn_abort`

Panic abort: command the test PRG to home/abort IMMEDIATELY and disarm. Stopping is NOT gated by the arm token — it always works. Re-arming happens only in the host window (Approvals view). Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

### `twincat_ibn_release_all`

Panic abort AND release every pending proposal: homes the test PRG, rejects all pending proposals and clears the registry. Not gated by the arm token. Re-arming happens only in the host window (Approvals view). Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

## IoLink

### `twincat_get_iolink_config`

Read back the IO-Link configuration of a master terminal (e.g. EL6224) by tree path. Read-only. Requires XAE running with a solution open. Returns, per IO-Link port (1..4): the assigned device (name, vendor, product id, IODD file) and its named process data (e.g. 'Valve 1 solenoid 00'). Result body is a one-line summary followed by indented JSON. The fieldbus topology does not descend into IO-Link ports, so pass the EL6224's own tree path here.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the IO-Link master under TIID, e.g. 'TIID^Device 1 (EtherCAT)^+CAB1-A1 (EK1200)^Term 28 (EL6224)'. Find it via twincat_get_fieldbus_topology. |

### `twincat_import_iodd`

Register an IO-Link device description (IODD .xml) into TwinCAT's IODD folder (…\Config\Io\IOLink) so an IO-Link master (e.g. EL6224) can break the device down into named process data. Reads the device identity (vendorId/deviceId/productId) from the IODD and reports it. Idempotent (skips an identical already-registered file). Does NOT require an open solution — it writes the TwinCAT IODD library, not the project. TwinCAT picks the file up on its next IODD rescan.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `sourcePath` | `string` |  | Full path to the IODD .xml file to register, e.g. 'C:\\downloads\\Festo-VABX-A-P-EL-20230815-IODD1.1.xml'. |
| `targetFolder` | `string?` | `null` | Optional override of the target IODD folder. Leave empty to use the TwinCAT default (…\Config\Io\IOLink). |

### `twincat_check_iodd_present`

Check whether the IODD (IO-Link device description) for a device is installed in TwinCAT's IODD folder (…\Config\Io\IOLink) — the IO-Link analogue of checking an EtherCAT ESI is present. Identify the device by productId (e.g. 'VABX-A-P-EL-E12-IOS'), by ioddFileName, or by vendorId+deviceId. Returns 'IODD present: …' or a fail-closed '[Error] Missing IODD — STOP…' report instructing the user to obtain it. Run this before configuring/linking an IO-Link device so the workflow stops instead of configuring blind. Does NOT require an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `productId` | `string?` | `null` | Device product id from the IODD's device-variant, e.g. 'VABX-A-P-EL-E12-IOS'. Optional. |
| `ioddFileName` | `string?` | `null` | Exact IODD filename, e.g. 'Festo-VABX-A-P-EL-20230815-IODD1.1.xml'. Optional. |
| `vendorId` | `int?` | `null` | IO-Link vendor id (use together with deviceId). Optional. |
| `deviceId` | `int?` | `null` | IO-Link device id (use together with vendorId). Optional. |
| `targetFolder` | `string?` | `null` | Optional override of the IODD folder. Leave empty for the TwinCAT default (…\Config\Io\IOLink). |

## IoMapping

### `twincat_link_variables`

Create a link between two TwinCAT variables identified by their caret-separated tree paths. Requires XAE running with a solution open. Saves the project after linking. Use twincat_get_fieldbus_topology to find I/O paths (TIID^...) and twincat_list_pous to find PLC paths (TIPC^...). When TwinCAT refuses the link, the error names the cause: the type, bit size and direction of both variables, which path does not resolve, or which path is not a variable.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variable1TreePath` | `string?` | `null` | Full tree path of the first variable, e.g. 'TIPC^PLC^PLC Projekt^MAIN.bVar'. |
| `variable2TreePath` | `string?` | `null` | Full tree path of the second variable, e.g. 'TIID^Device 1 (EtherCAT)^Box 1^Channel 1'. |
| `bitOffset1` | `int` | `0` | Bit offset within variable1TreePath (default 0). |
| `bitOffset2` | `int` | `0` | Bit offset within variable2TreePath (default 0). |
| `size` | `int` | `0` | Number of bits to link; 0 means minimum size of both variables (default 0). |
| `v1` | `string?` | `null` | DEPRECATED alias for variable1TreePath, kept for one release so existing MCP clients that bind by this name keep working. Prefer variable1TreePath. |
| `v2` | `string?` | `null` | DEPRECATED alias for variable2TreePath, kept for one release. Prefer variable2TreePath. |
| `offs1` | `int?` | `null` | DEPRECATED alias for bitOffset1, kept for one release. Prefer bitOffset1. |
| `offs2` | `int?` | `null` | DEPRECATED alias for bitOffset2, kept for one release. Prefer bitOffset2. |

### `twincat_unlink_variables`

Remove a variable link in TwinCAT. If variable2TreePath is omitted or empty, all links from variable1TreePath are removed. Requires XAE running with a solution open. Saves the project after unlinking.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variable1TreePath` | `string?` | `null` | Full tree path of the first variable. |
| `variable2TreePath` | `string` | `""` | Full tree path of the second variable. Leave empty to remove all links from variable1TreePath. |
| `v1` | `string?` | `null` | DEPRECATED alias for variable1TreePath, kept for one release. Prefer variable1TreePath. |
| `v2` | `string?` | `null` | DEPRECATED alias for variable2TreePath, kept for one release. Prefer variable2TreePath. |

### `twincat_get_mapping_info`

Export all current variable mappings as XML (VarLinks format) via ITcSysManager3::ProduceMappingInfo. Writes to outputPath if given, otherwise defaults to Mappings.xml next to the .sln (original behaviour, overwrites any existing file there). Read-only w.r.t. the XAE project itself. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `outputPath` | `string?` | `null` | Absolute path to write the XML to. Omit to use the default (Mappings.xml next to the .sln). |

### `twincat_set_device_disabled`

Enable or disable a TwinCAT I/O device by its tree path. disabled=true sets SMDS_DISABLED; disabled=false sets SMDS_NOT_DISABLED (re-enables). Requires XAE running with a solution open. Saves the project after changing state.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the I/O device, e.g. 'TIID^Device 1 (EtherCAT)'. Get valid paths via twincat_get_fieldbus_topology. |
| `disabled` | `bool` |  | true to disable the device, false to enable it. |

## Libraries

### `twincat_list_library_references`

List PLC library references for the open project. Reads the live XAE library manager when available and compares against the .plcproj on disk; falls back to the .plcproj alone when XAE is not ready. Each reference also shows the concrete library it resolves to (resolvedName, resolvedVersion, resolvedDistributor) and how that was determined (resolution): 'effective' = the library manager's effective resolution of the placeholder (what the project compiles against), 'fixed' = the reference names a concrete version, 'newest-installed' = '*'/newest resolved to the highest installed version (a computed approximation, see warnings), 'unresolved' = no matching library. format='text' (default): one line per reference with the suffix [resolved: <name> <version> (<distributor>), <resolution>]. format='json': { success, summary, count, references [{ placeholder, name, version, publisher, source, filePinnedVersion, versionMatchesFile, resolvedName, resolvedVersion, resolvedDistributor, resolution }], warnings }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `format` | `string?` | `null` | Output format: 'text' (default) or 'json' (full records). |

### `twincat_list_installed_libraries`

List libraries installed in the local TwinCAT XAE library repository (requires XAE running with a solution open).

_No parameters._

### `twincat_check_libraries_installed`

Check whether every PLC library reference of the open project is installed in the local XAE repository. Reports each reference as Installed, Missing, or VersionMismatch. Reference version '*' is satisfied by any installed version with the same name.

_No parameters._

### `twincat_add_library_reference`

Add a PLC library (or placeholder) reference to the open project's References node. name: library name (required). version: pinned version or '*' for latest (default '*'). company: distributor name (optional). asPlaceholder: add as a placeholder reference (default false). force: bypass the install-guard and add even if the library is not installed (default false). Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library name, e.g. 'Tc2_MC2'. |
| `version` | `string` | `"*"` | Version string or '*' for latest. |
| `company` | `string?` | `null` | Distributor/company, e.g. 'Beckhoff Automation GmbH'. |
| `asPlaceholder` | `bool` | `false` | Add as placeholder reference instead of a direct library reference. |
| `force` | `bool` | `false` | Add even if the library is not installed in the local XAE repository. |

### `twincat_remove_library_reference`

Remove a PLC library or placeholder reference from the open project by name. No-op if the reference is not present. Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library or placeholder name to remove, e.g. 'Tc2_MC2'. |

### `twincat_install_library`

Install a .library/.compiled-library file into the local XAE library repository (repository 'System'). Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `libraryPath` | `string` |  | Absolute path to the .library/.compiled-library file. |
| `overwrite` | `bool` | `false` | Overwrite if the same version is already installed. Default false. |

### `twincat_save_as_library`

Save as library / Save as library and install: export the open PLC project as a .library file via ITcPlcIECProject.SaveAsLibrary — the counterpart to twincat_install_library. install: also register it in the local XAE library repository (default false). Requires XAE running with a solution open; the target directory must already exist.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `outputPath` | `string` |  | Absolute path the .library file should be written to, e.g. 'C:\Libs\PTC_Library.library'. |
| `install` | `bool` | `false` | Also install into the local XAE library repository. Default false. |

### `twincat_get_library_parameters`

List the parameter-list overrides of a referenced PLC library placeholder (e.g. GVL_PARAMETER constants like NUMBER_OF_ITEMS), as persisted in the .plcproj. No overrides means the library defaults apply.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `library` | `string` |  | Library or placeholder name, e.g. 'Sample-Library'. |

### `twincat_set_library_parameter`

Set (or add) one parameter-list override on a referenced PLC library placeholder — e.g. GVL_PARAMETER/NUMBER_OF_ITEMS = 101 on 'Sample-Library'. Writes via the XAE References node (ProduceXml→ConsumeXml), saves, and verifies the value persisted to the .plcproj. Returns the updated override list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `library` | `string` |  | Library or placeholder name, e.g. 'Sample-Library'. |
| `listName` | `string` |  | Parameter list name, e.g. 'GVL_PARAMETER'. |
| `key` | `string` |  | Constant name, e.g. 'NUMBER_OF_ITEMS'. |
| `value` | `string` |  | New value, e.g. '101'. |

### `twincat_set_library_version`

Change the pinned version of an existing PLC library reference. name: library name (required). version: new version string or '*' (required). company: distributor override (optional). Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library name, e.g. 'Tc2_MC2'. |
| `version` | `string` |  | New version string, e.g. '3.3.73.0' or '*'. |
| `company` | `string?` | `null` | Distributor override (optional). |

## Lifecycle

### `twincat_open_project`

Open a TwinCAT solution in the running XAE instance, launching XAE if needed. Takes a .sln or directly a project file (.tsproj / .tspproj / .hmiproj; a temporary .sln is generated for it). Optionally closes the currently-open solution first. Before the XAE loads anything, every independent project file a .tsproj references (File="….xti": devices, boxes, axes, PLC projects) must exist: a missing one (e.g. after IO nodes were renamed outside the XAE) refuses the open with [Error] listing them, because the XAE would load a broken configuration and saving it damages the .tsproj. allowMissingReferences=true opens anyway with [Warn]; save_project and close_project(saveFirst=true) then repeat the warning. The result names the XAE host the project's TcVersion selects (32-bit for builds below 4026, otherwise 64-bit) and warns when the project resolves to the 32-bit XAE although the 64-bit XAE is installed, or when the running XAE is not the selected one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `slnPath` | `string` |  | Absolute path to the .sln, .tsproj, .tspproj or .hmiproj file to open |
| `closeCurrentFirst` | `bool` | `false` | Close the currently-open solution first (no save). Default false. |
| `allowMissingReferences` | `bool` | `false` | Open even when referenced .xti files are missing (the result starts with [Warn]). Default false refuses. |
| `xaeHost` | `string` | `"auto"` | XAE host: auto (default, the one the project's TcVersion selects), 64 (TcXaeShell 64-bit) or 32 (32-bit). 64 opens an older project on purpose in the 64-bit XAE; the TcVersion in the .tsproj is never rewritten. Refused when the requested XAE is not installed or an XAE of the other bitness is already running. |

### `twincat_save_project`

Save all dirty documents and the solution in the running TwinCAT XAE instance (equivalent to File > Save All).

_No parameters._

### `twincat_shutdown`

Shut down the Pneutronic XAE MCP server and exit the host application. Returns immediately; the process terminates ~200 ms later so this response can flush. Side effects: closing the host form kills a running Usermode Runtime (UMR) instance and panic-aborts an armed IbnLive session (writes the abort bit and drops bEnable). Refuses while a UMR instance is running unless force=true is passed, since that stops the runtime out from under whatever is using it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `force` | `bool` | `false` | Shut down even while a UMR instance is running (see side effects above). Default false refuses in that case. |

### `twincat_close_project`

Close the currently open solution in the TwinCAT XAE instance. By default saves first; set saveFirst=false to discard unsaved changes.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `saveFirst` | `bool` | `true` | Save dirty documents before closing. Default true. |

## NcAxes

### `twincat_list_nc_tasks_axes`

Enumerate the NC (Motion) tree: every NC-Task and the axes under it. Read-only. Requires XAE running with a solution open. Returns a summary line followed by one indented line per NC-Task and axis with their tree paths (use those paths with twincat_create_axis / twincat_configure_axis).

_No parameters._

### `twincat_create_nc_task`

Create a new NC-Task under the Motion (TINC) root. Requires XAE running with a solution open. NOTE (TC3 4026.22): the Automation Interface only creates an NC-Task when the project has none yet (green-field). If an NC-Task already exists, this returns guidance to reuse it via twincat_list_nc_tasks_axes + twincat_create_axis. The actual created tree path is read back and returned. NOT saved to disk automatically — call twincat_save_project to persist.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name for the new NC-Task, e.g. 'NC-Task 1'. |

### `twincat_create_axis`

Create an axis under an existing NC-Task. Requires XAE running with a solution open. The NC-Task is resolved by browsing (not by reconstructing a path); get a valid ncTaskPath from twincat_list_nc_tasks_axes. The actual created axis path is read back and returned. NOT saved to disk automatically — call twincat_save_project to persist.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ncTaskPath` | `string` |  | Tree path of the target NC-Task, from twincat_list_nc_tasks_axes. |
| `axisName` | `string` |  | Name for the new axis, e.g. 'Axis 1'. |

### `twincat_configure_axis`

Configure parameters of an existing axis via a ProduceXml→modify→ConsumeXml round-trip (unspecified parameters are preserved). Requires XAE running with a solution open. Resolve axisPath via twincat_list_nc_tasks_axes. Supply only the fields you want to change; all are optional strings (numbers/booleans as text). NOT saved to disk automatically — call twincat_save_project to persist.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `axisPath` | `string` |  | Tree path of the axis to configure, from twincat_list_nc_tasks_axes. |
| `axisType` | `string?` | `null` | Axis type / drive class (Achstyp). 'canopen_ds402' (EL72xx/ELM72xx/AX8xxx, CoE) or 'sercos_soe' (AX5xxx, SoE). Sets AxisIoType + Encoder/EncType + Drive/DrvType together; TwinCAT regenerates the dependent process image. |
| `unit` | `string?` | `null` | Engineering unit, e.g. 'mm' or 'deg'. Maps to AxisParam/Unit. |
| `scaleFactorNumerator` | `string?` | `null` | Encoder scaling numerator. Maps to Encoder/EncParam/ScaleFactorNumerator. |
| `scaleFactorDenominator` | `string?` | `null` | Encoder scaling denominator. Maps to Encoder/EncParam/ScaleFactorDenominator. |
| `velocityMax` | `string?` | `null` | Maximum velocity. Maps to AxisParam/Velo/Maximum. |
| `acceleration` | `string?` | `null` | Acceleration. Maps to AxisParam/Dynamic/Acceleration. |
| `deceleration` | `string?` | `null` | Deceleration. Maps to AxisParam/Dynamic/Deceleration. |
| `jerk` | `string?` | `null` | Jerk. Maps to AxisParam/Dynamic/Jerk. |
| `encoderInverse` | `string?` | `null` | Encoder inversion 'true'/'false'. Maps to Encoder/EncParam/Inverse. |
| `moduloFactor` | `string?` | `null` | Modulo factor. Maps to Encoder/EncParam/ModuloFactor. |
| `softMinEnable` | `string?` | `null` | Enable soft minimum position limit 'true'/'false'. |
| `softMinRange` | `string?` | `null` | Soft minimum position limit value. |
| `softMaxEnable` | `string?` | `null` | Enable soft maximum position limit 'true'/'false'. |
| `softMaxRange` | `string?` | `null` | Soft maximum position limit value. |

### `twincat_read_axis_params`

Read the current parameters of an axis. Read-only. Requires XAE running with a solution open. Resolve axisPath via twincat_list_nc_tasks_axes. Returns a summary, the known field values (the same keys twincat_configure_axis accepts: unit, scaleFactorNumerator, velocityMax, …) and the raw axis ProduceXml.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `axisPath` | `string` |  | Tree path of the axis, from twincat_list_nc_tasks_axes. |

## PersistentSnapshot

### `twincat_list_ads_symbols`

Read-only browse of the ONLINE symbol table of a running PLC / UMR via ADS (no values -- use twincat_read_ads_variables_batch for those). Returns JSON { success, count, truncated, symbols: [{ path, type, size, isPersistent, readOnly, comment }], amsNetId, adsPort } or [Error]. isPersistent is the runtime's IsPersistent flag (VAR_GLOBAL PERSISTENT); RETAIN variables are NOT flagged over ADS. Default amsNetId = the local AMS router's NetId, port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `root` | `string?` | `null` | Symbol to list the children of, e.g. 'GVL_Recipe'. Omit for the top level. |
| `maxDepth` | `int?` | `null` | How many levels below root to list. Default 1. |
| `persistentOnly` | `bool` | `false` | Only symbols the runtime flags as persistent. Default false. |
| `limit` | `int?` | `null` | Maximum entries returned (default 500, max 5000). |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_save`

Capture the remanent data of a running PLC into a snapshot file (format ptc-persistent-snapshot/1: a flat map leaf path -> { type, value }, arrays by declared index). READ-ONLY on the PLC (ADS sum reads); it only writes the local file. The remanent leaves come from the compiled .tmc of the open solution (PERSISTENT and RETAIN, as the compiler resolved them -- selectionSource 'tmc'; every TMC leaf must exist on the PLC with the same type, else [Error] TMC does not match the running PLC), else from the runtime's IsPersistent flags (selectionSource 'ads' -- RETAIN is not detectable that way). roots[] adds explicit subtrees. Leaves that cannot be captured (input-mapped, read-only, POINTER/REFERENCE, PROPERTY, unsupported type, unreadable) are listed in skipped with the reason, never dropped silently. Returns JSON { success, path, leafCount, selectionSource, skipped[], skippedCount, warnings[], amsNetId, adsPort } -- never the value map itself. Default amsNetId = the local AMS router's NetId, port 851.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string?` | `null` | 'persistent', 'retain' or 'both' (default). Which remanent leaves to select. |
| `roots` | `string[]?` | `null` | Extra symbol paths whose whole subtree is added, e.g. ['GVL_Settings.nRetainCounter']. |
| `selection` | `string?` | `null` | 'auto' (default: TMC if found, else ADS), 'tmc', 'ads' or 'roots' (only roots[]). |
| `tmcPath` | `string?` | `null` | Absolute path of the PLC .tmc. Omit to find it in the open solution. |
| `path` | `string?` | `null` | Target .json file. Omit for %LOCALAPPDATA%\ptc-xae-mcp\snapshots\<utc>_<netId>.json. Must be inside the solution directory, TEMP, %LOCALAPPDATA%\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_compare`

Compare a snapshot file leaf by leaf with the running PLC (or with another snapshot via otherPath -- then no PLC is needed). READ-ONLY. Returns JSON { success, equal, against, equalCount, differenceCount, differences[{ path, snapshotPath, type, snapshot, live }], onlyInSnapshot[], onlyOnPlc[], typeMismatches[], conflicts[], warnings[] } (lists capped by limit). equal is true when every leaf of the snapshot matches. onlyInSnapshot = orphans with no counterpart on the PLC after pathMap; onlyOnPlc = remanent leaves the snapshot does not have (information only, it does not make equal false). Types are decided by the PLC as it is now (integer widths, REAL/LREAL, STRING/WSTRING are compatible; anything else is a type mismatch).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `snapshotPath` | `string` |  | Snapshot file written by twincat_persistent_snapshot_save. |
| `otherPath` | `string?` | `null` | Compare against this second snapshot file instead of the PLC. |
| `pathMap` | `string?` | `null` | JSON object of rename rules applied to the snapshot paths, e.g. {"GVL_Old.stRecipe":"GVL_New.stRecipe"} (exact path or prefix at a '.'/'[' boundary). |
| `tmcPath` | `string?` | `null` | Absolute path of the PLC .tmc used to re-select the live remanent set. Omit to find it. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |
| `limit` | `int?` | `null` | Cap per result list (default 200). |

### `twincat_persistent_snapshot_restore`

Restore a snapshot into the running PLC -- DESTRUCTIVE and human-gated. dryRun defaults to TRUE: it only returns the restore plan { planHash, plcState, writes[{ path, type, current, snapshot }], skips[{ path, reason }], unchanged } and writes nothing. With dryRun=false it requires the planHash of a dry run, re-plans, refuses on a different hash, then raises a restore proposal (modal popup in the host window showing target, PLC state RUN/STOP, the differences) and returns { restoreId, status: "pending" }. NOTHING is written until a human confirms; there is no MCP tool that confirms. After the confirm the host re-plans against the PLC (rejecting if it changed), writes with ADS sum commands and reads EVERY written leaf back. Poll twincat_persistent_snapshot_restore_status for the outcome and the per-leaf report. Only leaves whose live value differs are written; type changes, out-of-bounds indices, read-only and input-mapped leaves are skipped with a reason. A running program may overwrite a restored value; the read-back reports it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `snapshotPath` | `string` |  | Snapshot file written by twincat_persistent_snapshot_save. |
| `dryRun` | `bool` | `true` | true (default): return the plan only. false: propose the restore for human confirmation. |
| `planHash` | `string?` | `null` | The planHash returned by the dry run. Required when dryRun=false. |
| `pathMap` | `string?` | `null` | JSON object of rename rules old -> new (see twincat_persistent_snapshot_compare). |
| `include` | `string[]?` | `null` | Restrict the restore to these leaf paths (e.g. picked differences). Omit for all. |
| `amsNetId` | `string?` | `null` | Optional AMS Net ID. Omit for the local TwinCAT runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_restore_status`

Read-only poll of a restore proposal: returns JSON { restoreId, status, reason, target, plcState, snapshot, createdUtc, expiresUtc, writeCount, report }. Status is one of pending | confirmed | executed | rejected | expired. Once executed, report carries { written, failed, skipped, items[{ path, outcome, readBack, message }] } -- the read-back value, not the intended one. Reads only; writes nothing.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `restoreId` | `string` |  | The restoreId returned by twincat_persistent_snapshot_restore. |

## PlcCode

### `twincat_list_pous`

DEPRECATED — use twincat_plc_list. Enumerate POU/GVL/DUT items in the open PLC project. Returns one line per item with kind (POU|GVL|DUT|FOLDER), tree path, and (for POUs) the implementation language.

_No parameters._

### `twincat_read_pou`

DEPRECATED — use twincat_plc_read (kind=POU or INTERFACE). Read a POU's declaration and implementation by its caret-separated tree path (e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN'). Returns the language and both sections. When openInEditor=true, also opens the POU in the visible XAE editor (video mode) — best-effort, never blocks the read.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU to read, e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN'. |
| `openInEditor` | `bool` | `false` | When true, also opens the POU in the visible XAE editor (video mode), best-effort. Default false. |

### `twincat_read_gvl`

DEPRECATED — use twincat_plc_read (kind=GVL or DUT). Read a GVL or DUT declaration by its caret-separated tree path. Returns the declaration block (GVLs and DUTs have no implementation section). When openInEditor=true, also opens the object in the visible XAE editor (video mode) — best-effort, never blocks the read.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the GVL or DUT to read. |
| `openInEditor` | `bool` | `false` | When true, also opens the object in the visible XAE editor (video mode), best-effort. Default false. |

### `twincat_write_pou_body`

DEPRECATED — use twincat_plc_write (kind=POU or INTERFACE). Replace one or both sections of an existing ST POU. Pass null for a section to leave it unchanged. Each section can be given INLINE (declaration/implementation) or READ FROM A FILE (declarationFile/implementationFile) — use the file form for generated content, so it never has to be retyped. Supplying both forms for the same section is an error. Refuses non-ST POUs and GVLs/DUTs with a structured error.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the existing ST POU to write. |
| `declaration` | `string?` | `null` | New declaration ('VAR ... END_VAR' block). Null leaves it unchanged. Alternative to 'declarationFile'. |
| `implementation` | `string?` | `null` | New implementation (ST source). Null leaves it unchanged. Alternative to 'implementationFile'. |
| `declarationFile` | `string?` | `null` | Path to a file whose contents become the declaration. Alternative to 'declaration'. |
| `implementationFile` | `string?` | `null` | Path to a file whose contents become the implementation. Alternative to 'implementation'. |

### `twincat_write_gvl`

DEPRECATED — use twincat_plc_write (kind=GVL or DUT). Replace the declaration of an existing GVL or DUT by its caret-separated tree path. These declaration-only objects have no implementation section, so only the declaration is written (ITcPlcDeclaration); the write is verified by reading back, and any mismatch throws. The declaration can be given inline or READ FROM A FILE via declarationFile — use the file form for generated content. Use twincat_write_pou_body for ST POUs (which also carry an implementation).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the existing GVL or DUT to write. |
| `declaration` | `string?` | `null` | New declaration block. Null leaves it unchanged. Alternative to 'declarationFile'. |
| `declarationFile` | `string?` | `null` | Path to a file whose contents become the declaration. Alternative to 'declaration'. |

### `twincat_create_pou`

DEPRECATED — use twincat_plc_create (kind=POU, subtype=<pouType>). Create a new POU under an existing PLC folder. pouType is one of PROGRAM, FUNCTION_BLOCK, FUNCTION. returnType is required for FUNCTION (defaults to BOOL). implLanguage is ST, IL, SFC, FBD, CFC or LD (defaults to ST when omitted; any other value is an [Error]). parentPath is the caret-separated tree path of the folder (e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application'). Returns the new node's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Caret-separated tree path of the existing folder to create the POU under, e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application'. |
| `name` | `string` |  | Name of the new POU. |
| `pouType` | `string` |  | POU kind: PROGRAM, FUNCTION_BLOCK, or FUNCTION. |
| `returnType` | `string?` | `null` | Return type, required for FUNCTION. Defaults to BOOL when omitted for a FUNCTION. |
| `implLanguage` | `string?` | `null` | Implementation language: ST, IL, SFC, FBD, CFC, or LD. Defaults to ST when omitted; any other value is an [Error]. |

### `twincat_create_dut`

DEPRECATED — use twincat_plc_create (kind=DUT, subtype=<dutType>). Create a new DUT (Data Unit Type) under an existing PLC folder or the PLC project root. dutType is one of STRUCT, ENUM, UNION, ALIAS (defaults to STRUCT). Pass an optional declaration (the full 'TYPE ... END_TYPE' block) to populate it; if omitted, TwinCAT's default skeleton remains. ALIAS is an exception: it REQUIRES a declaration of the form 'TYPE <name> : <baseType>; END_TYPE' (e.g. 'TYPE A_Handle : UDINT; END_TYPE'), because the base type must be supplied when the alias is created. parentPath is the caret-separated tree path of the folder (e.g. 'TIPC^PLC^PLC Projekt^DUTs'). DUTs cannot be created under a POU/GVL/DUT leaf — only a folder or the PLC project node. Returns the new node's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Caret-separated tree path of the existing folder or PLC project node to create the DUT under, e.g. 'TIPC^PLC^PLC Projekt^DUTs'. |
| `name` | `string` |  | Name of the new DUT. |
| `dutType` | `string?` | `null` | DUT kind: STRUCT, ENUM, UNION, or ALIAS. Defaults to STRUCT. ALIAS requires 'declaration'. |
| `declaration` | `string?` | `null` | Full 'TYPE ... END_TYPE' block to populate the DUT. Required for ALIAS (e.g. 'TYPE A_Handle : UDINT; END_TYPE'); optional otherwise (TwinCAT's default skeleton is used when omitted). |

### `twincat_create_gvl`

DEPRECATED — use twincat_plc_create (kind=GVL). Create a new GVL (Global Variable List) under an existing PLC folder or the PLC project root. Set persistent=true to get persistent variables: an ordinary GVL whose declaration has a 'VAR_GLOBAL PERSISTENT' section (persistent is a section, not an object type). Pass an optional declaration (the full 'VAR_GLOBAL ... END_VAR' block; for persistent=true it must contain the PERSISTENT keyword, otherwise [Error]) to populate it; if omitted, TwinCAT's default skeleton remains (for persistent=true an empty 'VAR_GLOBAL PERSISTENT ... END_VAR'). parentPath is the caret-separated tree path of the folder (e.g. 'TIPC^PLC^PLC Projekt^GVLs'). GVLs cannot be created under a POU/GVL/DUT leaf — only a folder or the PLC project node. Returns the new node's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Caret-separated tree path of the existing folder or PLC project node to create the GVL under, e.g. 'TIPC^PLC^PLC Projekt^GVLs'. |
| `name` | `string` |  | Name of the new GVL. |
| `persistent` | `bool` | `false` | True creates an ordinary GVL whose declaration has a 'VAR_GLOBAL PERSISTENT ... END_VAR' section (persistent variables are a section of a GVL, not an object type). A given declaration must contain the PERSISTENT keyword, otherwise [Error]. Default false. |
| `declaration` | `string?` | `null` | Full 'VAR_GLOBAL ... END_VAR' (or PERSISTENT variant) block to populate the GVL. TwinCAT's default skeleton is used when omitted. |

### `twincat_create_interface`

DEPRECATED — use twincat_plc_create (kind=INTERFACE). Create a new INTERFACE under an existing PLC folder or the PLC project root. Pass an optional declaration (the full 'INTERFACE ... END_INTERFACE' block) to populate it; if omitted, TwinCAT's default skeleton remains. Interface methods are added afterwards with twincat_add_pou_member (kind=METHOD). parentPath is the caret-separated tree path of the folder (e.g. 'TIPC^PLC^PLC Projekt^POUs^04_Components'). Interfaces cannot be created under a POU/GVL/DUT leaf — only a folder or the PLC project node. Returns the new node's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Caret-separated tree path of the existing folder or PLC project node to create the interface under, e.g. 'TIPC^PLC^PLC Projekt^POUs^04_Components'. |
| `name` | `string` |  | Name of the new interface. |
| `declaration` | `string?` | `null` | Full 'INTERFACE ... END_INTERFACE' block to populate it. TwinCAT's default skeleton is used when omitted. |

## PlcControl

### `twincat_plc_start`

Start the PLC runtime — the 'Start' / RUN action (F5 in the PLC context), via the ADS state machine. Use this after twincat_login with mode=download, which leaves the PLC in STOP: unlike twincat_restart_runtime, this keeps the freshly downloaded program instead of reloading the old boot project. Reads the state back and reports what the runtime actually is, so a command that is accepted but has no effect is reported as a failure.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | Target AmsNetId. Omit to use the local AMS router's NetId. |
| `adsPort` | `int` | `851` | ADS port of the PLC runtime. Default 851 (first PLC runtime). |

### `twincat_plc_stop`

Stop the PLC runtime — the 'Stop' action, via the ADS state machine. Counterpart to twincat_plc_start. Reads the state back and reports what the runtime actually is. This halts the program on the target: on a real machine that stops the process.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | Target AmsNetId. Omit to use the local AMS router's NetId. |
| `adsPort` | `int` | `851` | ADS port of the PLC runtime. Default 851 (first PLC runtime). |

### `twincat_activate_boot_project`

Write the current program as the boot project — the 'Activate Boot Project' action in the XAE PLC menu. Without it a download lives in RAM only and the target falls back to the previous boot project on the next power cycle. Uses ITcPlcProject.GenerateBootProject on the PLC project node, which works whether or not the XAE is logged in (the Solution-Explorer menu action is greyed out while offline; this is not). dryRun defaults to TRUE and changes nothing — it reports which member this XAE exposes, so it is safe to run against a running plant. With dryRun=false the effect is PROVEN from the _Boot artifacts: the result lists which files were rewritten and fails if no PLC boot application (*.app) was produced. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dryRun` | `bool` | `true` | TRUE (default) only probes and changes nothing. FALSE actually writes the boot project. |

## PlcEntries

### `twincat_plc_list_entries`

List the entries of an element's declaration, structured: STRUCT/UNION fields, ENUM values, GVL variables, and the variables of a POU or MEMBER (METHOD, Get/Set) per section (VAR_INPUT, VAR_OUTPUT, VAR_IN_OUT, VAR, VAR_STAT, VAR_TEMP, VAR_INST, VAR_GLOBAL, each optionally CONSTANT, RETAIN or PERSISTENT). One line per entry: line number, section, name, AT address, type, initial value, trailing comment, attributes ({attribute '…'} pragmas). An entry the editor cannot change safely (several names in one entry, several entries on one line, no ';') is listed with a note. section filters to one section. DUT ALIAS, INTERFACE bodies and ACTION/TRANSITION have no entries. kind is checked against the node's real type.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element (DUT, GVL, POU or MEMBER). |
| `kind` | `string` |  | Element kind the node must have: DUT, GVL, POU or MEMBER. |
| `section` | `string?` | `null` | Optional section filter, e.g. VAR_INPUT or 'VAR_GLOBAL PERSISTENT'. |

### `twincat_plc_edit_entries`

Add, change or remove ONE entry of an element's declaration, leaving the rest of the text byte-identical. op is add, set or remove. Entries: STRUCT/UNION fields, ENUM values (use value; the base type and attributes stay untouched), GVL variables (NORMAL and PARAMLIST), and the variables of a POU or MEMBER per section — a missing section is created by op=add. section (e.g. VAR_INPUT, 'VAR_GLOBAL PERSISTENT', 'VAR RETAIN') is required when it is ambiguous. add: name and type (not for enum values); afterName inserts after that entry instead of at the end of the section. set: pass what changes — type, initialValue ("" removes it), value (enum, "" removes it), address (AT address such as %I*, "" removes it), comment ("" removes it), newName; attributes replaces the entry's attribute pragmas (a name such as hide, "attribute 'hide'" or a full {attribute 'hide'}), an empty list removes them, null leaves them unchanged. remove: name only; the entry's attribute lines go with it. Refused with an [Error], before the automatic logout and without a change: an unknown kind, a duplicate name, several names in one entry or several entries on one line, DUT ALIAS and INTERFACE bodies, a parameter that does not apply. The write is verified by read-back. Renamed or removed entries break code and ADS callers — follow with twincat_rebuild_plc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element (DUT, GVL, POU or MEMBER). |
| `kind` | `string` |  | Element kind the node must have: DUT, GVL, POU or MEMBER. |
| `op` | `string` |  | Operation: add, set or remove. |
| `name` | `string` |  | Name of the entry (for add: the new entry). |
| `section` | `string?` | `null` | Section of a POU/MEMBER/GVL declaration, e.g. VAR_INPUT or 'VAR_GLOBAL PERSISTENT'. Required when ambiguous; add creates a missing one. |
| `type` | `string?` | `null` | Data type, e.g. INT, ARRAY[0..3] OF REAL, POINTER TO BYTE. Required for add (not for enum values). |
| `value` | `string?` | `null` | Enum value (only for ENUM entries). Empty string removes an explicit value. |
| `initialValue` | `string?` | `null` | Initial value (not for enum values). Empty string removes it. |
| `address` | `string?` | `null` | AT address such as %I* or %QX0.0 (not for enum values). Empty string removes it. |
| `comment` | `string?` | `null` | Trailing comment text. Empty string removes it. |
| `afterName` | `string?` | `null` | op=add: insert after this existing entry instead of at the end of the section. |
| `newName` | `string?` | `null` | op=set: new name for the entry. |
| `attributes` | `string[]?` | `null` | Attribute pragmas of the entry. Each is a name (hide), "attribute 'hide'" or {attribute 'hide'}. Null leaves them unchanged, an empty array removes them. |

## PlcInstanceSettings

### `twincat_read_init_symbols`

Read the init values of all {attribute 'TcInitSymbol'} variables from the PLC instance (ProduceXml, TcModuleInstance/Module/InitSymbolValues; TwinCAT >= 4024.17). These values live in the TwinCAT configuration, not in the PLC source. The PLC project must be built. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC, e.g. 'FixturePlc'. Optional when the solution has exactly one. |

### `twincat_write_init_symbol`

Set the init value of one {attribute 'TcInitSymbol'} variable on the PLC instance (ConsumeXml). The symbol must already be listed by twincat_read_init_symbols (the XAE silently ignores others). Proven by a fresh read-back; a value the XAE does not store as given (invalid for the type, out of range — it would wrap) is refused and the old value restored. Logs the XAE out first, saves afterwards. Takes effect with the next Activate Configuration — this tool does not activate.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | Full symbol name as listed by twincat_read_init_symbols, e.g. 'GVL_Params.nSpeed'. |
| `value` | `string` |  | New init value in IEC literal form as the XAE shows it, e.g. '42', '1.5', 'TRUE'. |
| `plcProjectName` | `string?` | `null` | PLC project under TIPC. Optional when the solution has exactly one. |

### `twincat_read_retain_config`

Read the retain configuration of the PLC instance (ProduceXml): every data area with AreaType RetainSrc with its symbols (type, BitSize, BitOffs) and RetainDataMappings if present. Read-only — the configuration cannot be written (Infosys); retain variables are declared in the source (VAR RETAIN). The PLC project must be built.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC. Optional when the solution has exactly one. |

### `twincat_get_type_file_generation`

Read whether the PLC project generates/copies its .tmc and .tpy type files at build (PlcProjectDef TmcFileCopy / TpyFileCopy on the PLC project node under TIPC). Shows the actual state. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC. Optional when the solution has exactly one. |

### `twincat_set_type_file_generation`

Switch .tmc/.tpy type-file generation of the PLC project on or off (ConsumeXml PlcProjectDef TmcFileCopy / TpyFileCopy). The XAE couples them: tpy=true also turns tmc on, tmc=false also turns tpy off (tmc=false with tpy=true is refused). Returns the flags read back. Logs the XAE out first, saves afterwards; effective at the next build.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmc` | `bool?` | `null` | Generate the .tmc file (omit to leave unchanged). |
| `tpy` | `bool?` | `null` | Generate the .tpy file (omit to leave unchanged). |
| `plcProjectName` | `string?` | `null` | PLC project under TIPC. Optional when the solution has exactly one. |

## PlcObjects

### `twincat_plc_list`

List PLC elements. Without arguments: every POU, INTERFACE, DUT, GVL and FOLDER of the open PLC project. kind filters by element kind; parentPath restricts to the subtree below a tree path. Under a POU, an INTERFACE or a PROPERTY, parentPath lists its MEMBERs (METHOD, ACTION, PROPERTY, Get/Set), including members inside folders under a POU; kind=MEMBER requires such a parentPath. recursive=false lists only the direct children. One line per element: kind/subtype, tree path, language.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string?` | `null` | Optional element kind filter, e.g. POU, DUT, GVL, MEMBER. |
| `parentPath` | `string?` | `null` | Optional caret-separated tree path; only elements below it are listed. Required for kind=MEMBER. |
| `recursive` | `bool` | `true` | False lists only the direct children of parentPath. Default true. |

### `twincat_plc_create`

Create a PLC element under parentPath. kind + subtype pick the element: POU/PROGRAM|FUNCTION|FUNCTION_BLOCK, DUT/STRUCT|ENUM|UNION|ALIAS (default STRUCT; ALIAS needs a 'TYPE <name> : <baseType>; END_TYPE' declaration), GVL/NORMAL|PARAMLIST (default NORMAL; persistent variables are a 'VAR_GLOBAL PERSISTENT' section of a NORMAL GVL — put it in the declaration), INTERFACE, MEMBER/METHOD|ACTION|PROPERTY|GET|SET (parentPath is the POU or INTERFACE; GET/SET are named Get/Set; PROPERTY gets its accessors per 'accessors'; TRANSITION under a POU is ST, implementation only), FOLDER (under a folder, the PLC project node, a POU or an INTERFACE), TEXTLIST/LOCAL, IMAGEPOOL/LOCAL, UML/CLASSDIAGRAM, VISU/MANAGER|VISUALIZATION and RECIPE/MANAGER (all under a folder or the PLC project node), VISU/TARGET|WEB (under a visualization manager), RECIPE/DEFINITION (under a recipe manager), TASKREF/PROGRAM (under a PLC task node; name = the PROGRAM to run). The GLOBAL text list / image pool, the text-list enumeration and the trend elements are refused by XAE and not offered. declaration optionally fills the new element (not for FOLDER, ACTION). returnType applies to FUNCTION, METHOD, PROPERTY (default BOOL); implLanguage (ST, IL, SFC, FBD, CFC, LD; default ST) to POUs and members; accessors (GET | SET | BOTH, default BOTH) to PROPERTY only. A parameter that does not apply to the element is an [Error]. Logs the XAE out first. Returns the new element's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` |  | Element kind: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER. |
| `parentPath` | `string` |  | Caret-separated tree path of the parent: a folder or the PLC project node; for kind=MEMBER the POU or INTERFACE. |
| `name` | `string` |  | Name of the new element. |
| `subtype` | `string?` | `null` | Subtype within the kind (required for POU and MEMBER), e.g. FUNCTION_BLOCK, STRUCT, PARAMLIST, METHOD. Optional for DUT (STRUCT), GVL (NORMAL), INTERFACE and FOLDER. |
| `declaration` | `string?` | `null` | Optional declaration text for the new element. TwinCAT's default skeleton remains when omitted. |
| `returnType` | `string?` | `null` | Return type for FUNCTION, METHOD and PROPERTY. Defaults to BOOL. |
| `implLanguage` | `string?` | `null` | Implementation language: ST, IL, SFC, FBD, CFC or LD. Defaults to ST. |
| `accessors` | `string?` | `null` | For subtype=PROPERTY only: GET, SET or BOTH (default). |

### `twincat_plc_read`

Read a PLC element's text by its caret-separated tree path: the declaration and, for elements that have one, the implementation (with its language; for SFC, FBD, CFC and LD the implementation is XAE's native XML). Only the sections the element has are returned (a DUT, GVL or INTERFACE has no implementation, an ACTION or TRANSITION no declaration). kind is checked against the node's real type before anything is read: a mismatch is an [Error] such as "'<path>' is a GVL, not a DUT". When openInEditor=true, also opens the element in the visible XAE editor (video mode), best-effort.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element to read. |
| `kind` | `string` |  | Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER. |
| `openInEditor` | `bool` | `false` | When true, also opens the element in the visible XAE editor (video mode), best-effort. Default false. |

### `twincat_plc_write`

Replace the declaration and/or the implementation of an existing PLC element. A section that is null stays unchanged. Each section can be given inline (declaration/implementation) or read from a file (declarationFile/implementationFile); giving both forms for one section is an error. kind is checked against the node's real type before the automatic logout. A section the element does not have (an implementation for a DUT, GVL or INTERFACE; a declaration for an ACTION) is an [Error] before the logout. The implementation is text for ST and IL; for SFC, FBD, CFC and LD it is XAE's native XML exactly as twincat_plc_read returns it (root <NWL> for LD and FBD, <SFC>, <CFC>) — anything else is refused before the logout. A declaration can be written in every language. A POU/INTERFACE/GVL/DUT declaration that contains a METHOD/PROPERTY/ACTION header line is refused (members are separate elements — create them with twincat_plc_create). The write is verified by an independent read-back.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element to write. |
| `kind` | `string` |  | Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER. |
| `declaration` | `string?` | `null` | New declaration text. Null leaves it unchanged. Alternative to 'declarationFile'. |
| `implementation` | `string?` | `null` | New implementation (ST source). Null leaves it unchanged. Alternative to 'implementationFile'. |
| `declarationFile` | `string?` | `null` | Path to a file whose contents become the declaration. Alternative to 'declaration'. |
| `implementationFile` | `string?` | `null` | Path to a file whose contents become the implementation. Alternative to 'implementation'. |

### `twincat_plc_delete`

Delete a PLC element by its caret-separated tree path. kind is checked against the node's real type before the automatic logout. Whitelist: the PLC project root, the PLC instance, References and any node whose type cannot be read are refused. A FOLDER is deleted only when it is empty, or with recursive=true (then with everything in it). The delete is verified by an independent lookup. Deleting an element that is referenced elsewhere may break the build — follow with twincat_build_plc if that matters.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element to delete. |
| `kind` | `string` |  | Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER. |
| `recursive` | `bool` | `false` | FOLDER only: true deletes a non-empty folder with everything in it. Default false. |

### `twincat_plc_rename`

Rename a PLC element by its caret-separated tree path. kind is checked against the node's real type before the automatic logout. Property accessors (Get/Set) have fixed names and cannot be renamed. Returns the new tree path. Renaming may make TwinCAT ask whether to update all references to the old name (the XAE's rename-via-refactoring prompt); the tool answers that prompt itself — "Nein" by default (only the element is renamed; callers keep the old name), "Ja" with updateReferences=true — and the result says whether it appeared and what was answered. Rebuild afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element to rename. |
| `kind` | `string` |  | Element kind the node must have: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER. |
| `newName` | `string` |  | New name of the element. |
| `updateReferences` | `bool` | `false` |  |

## PlcOnlineStatus

### `twincat_get_plc_online_state`

Check whether the TwinCAT XAE IDE is currently Online (logged in to a running PLC runtime). When Online, code edits (twincat_write_pou_body etc.) are blocked by the IDE. Use this as a guard before attempting code edits. Returns JSON { isOnline: bool|null, message: string }. isOnline=true → XAE is Online, edits blocked. isOnline=false → XAE is Offline, edits allowed. isOnline=null → state indeterminate (COM API not available on this TwinCAT version).

_No parameters._

## PlcProjectLifecycle

### `twincat_plc_project_create`

Create a new PLC project under TIPC in the open solution (TIPC.CreateChild). template: 'Standard PLC Template' (default), 'Empty PLC Template', another Beckhoff template name installed under PlcTemplates, or the absolute path of a custom .plcproj template (a copy is made). The name must not exist under TIPC yet. Logs the XAE out first, saves afterwards. Then build with twincat_build_plc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name of the new PLC project, e.g. 'Plc2'. |
| `template` | `string?` | `null` | Template name or absolute .plcproj path. Default 'Standard PLC Template'. |

### `twincat_plc_project_add_existing`

Take an existing PLC project into the open solution (TIPC.CreateChild; the 'add existing item' dialog's Yes/No/Cancel). path: a .plcproj or a .tpzip. mode: COPY (copy the project folder next to the solution; source unchanged), MOVE (move it there; the source folder is gone afterwards) or IN_PLACE (reference it where it is). The XAE names a .plcproj project after its own <Name> (name, if given, must match it); a .tpzip needs mode=COPY and a name. Refuses a name already under TIPC. Logs the XAE out first.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Absolute path of the .plcproj or .tpzip. |
| `mode` | `string` |  | COPY \| MOVE \| IN_PLACE. |
| `name` | `string?` | `null` | Optional for a .plcproj (must equal the project's own name); required for a .tpzip. |

### `twincat_plc_project_export`

Export one PLC project of the open solution as a .tpzip archive (TIPC.ExportChild). The target directory must exist; an existing file is overwritten. Proves the export by the freshly written file. Counterpart: twincat_plc_project_import.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string` |  | Name of the PLC project under TIPC, e.g. 'FixturePlc'. |
| `tpzipPath` | `string` |  | Absolute path of the .tpzip to write. |

### `twincat_plc_project_import`

Import a .tpzip archive (from twincat_plc_project_export or the XAE) as a new PLC project of the open solution (TIPC.CreateChild(name, 0, null, tpzip)). The name must not exist under TIPC yet. Logs the XAE out first, saves afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tpzipPath` | `string` |  | Absolute path of the .tpzip. |
| `name` | `string` |  | Name of the new PLC project. |

### `twincat_solution_save_archive`

Save the whole open solution as a .tszip archive (ITcSysManager9.SaveAsArchive; 'Save <solution> as Archive' in the XAE). Saves all documents first. The target directory must exist. Counterpart: twincat_solution_open_archive.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tszipPath` | `string` |  | Absolute path of the .tszip to write. |

### `twincat_solution_open_archive`

Unpack a .tszip archive as a new solution and open it (dte.Solution.AddFromTemplate). destDir must be new or empty; the solution is written as <destDir>\<name>.sln. Needs a running XAE. A solution that is already open is only closed (without saving) when closeCurrentFirst=true. Proves the open by the active solution path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tszipPath` | `string` |  | Absolute path of the .tszip. |
| `destDir` | `string` |  | Absolute directory to unpack into (new or empty). |
| `name` | `string` |  | Name of the new solution (file name without .sln). |
| `closeCurrentFirst` | `bool` | `false` | Close the currently open solution first (no save). Default false. |

### `twincat_plc_instance_from_tmc`

Create a PLC instance (I/O image only, no PLC source) from a .tmc module class file under TIPC (TIPC.CreateChild(name, 0, '', tmc)). The XAE names the instance after the module in the .tmc; name, if given, must match it. Refuses a name already under TIPC. Logs the XAE out first, saves afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmcPath` | `string` |  | Absolute path of the .tmc file. |
| `name` | `string?` | `null` | Optional: the expected instance name (must equal the .tmc module name). |

## PouMembers

### `twincat_list_pou_members`

DEPRECATED — use twincat_plc_list (kind=MEMBER, parentPath=<pouPath>). Enumerate the METHOD/ACTION/PROPERTY members of a POU by its caret-separated tree path. PROPERTY nodes also list their Get/Set accessor children. One line per member with kind and tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pouPath` | `string` |  | Caret-separated tree path of the POU to enumerate members of. |

### `twincat_add_pou_member`

DEPRECATED — use twincat_plc_create (kind=MEMBER, subtype=<kind>, parentPath=<pouPath>). Add a structured member to an existing POU. kind is METHOD, ACTION, PROPERTY, GET, or SET. For METHOD/PROPERTY, returnType defaults to BOOL (a METHOD header is verified to read 'METHOD <name> : <returnType>'). implLanguage is ST, IL, SFC, FBD, CFC or LD (defaults to ST when omitted; any other value is an [Error]). GET/SET create nodes named Get/Set; name is ignored for them. PROPERTY is created together with its Get and/or Set accessor(s) (XAE does not auto-generate them) — accessors picks which: GET, SET, or BOTH (default). Use GET-only on an INTERFACE property that should be read-only, so implementers are not forced to write a Set. GET/SET add a single accessor to an existing property node. Returns the new member's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pouPath` | `string` |  | Caret-separated tree path of the existing POU to add the member to. |
| `kind` | `string` |  | Member kind: METHOD, ACTION, PROPERTY, GET, or SET. |
| `name` | `string` |  | Name of the new member. Ignored for kind=GET/SET (nodes are always named Get/Set). |
| `returnType` | `string?` | `null` | Return type for METHOD/PROPERTY. Defaults to BOOL when omitted. |
| `implLanguage` | `string?` | `null` | Implementation language: ST, IL, SFC, FBD, CFC, or LD. Defaults to ST when omitted; any other value is an [Error]. |
| `accessors` | `string?` | `null` | For kind=PROPERTY only: GET, SET, or BOTH (default). Ignored for other kinds. |

### `twincat_read_pou_member`

DEPRECATED — use twincat_plc_read (kind=MEMBER). Read a POU member's declaration and implementation by its caret-separated tree path (e.g. a METHOD, ACTION, or a PROPERTY's Get/Set accessor). A bare PROPERTY node returns only its declaration. When openInEditor=true, also opens the member (or its parent POU file) in the visible XAE editor (video mode) — best-effort, never blocks the read.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `memberPath` | `string` |  | Caret-separated tree path of the member to read. |
| `openInEditor` | `bool` | `false` | When true, also opens the member (or its parent POU file) in the visible XAE editor (video mode), best-effort. Default false. |

### `twincat_write_pou_member`

DEPRECATED — use twincat_plc_write (kind=MEMBER). Replace one or both sections of an existing ST POU member. Pass null for a section to leave it unchanged. Each section can be given INLINE (declaration/implementation) or READ FROM A FILE (declarationFile/implementationFile) — use the file form for generated content such as a large block table, so it never has to be retyped or re-serialised. A transcription slip in an entry like FbTypes[17] is syntactically valid and therefore invisible to the compiler and to st_check. Supplying both forms for the same section is an error. Refuses non-ST members with a structured error.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `memberPath` | `string` |  | Caret-separated tree path of the existing ST POU member to write. |
| `declaration` | `string?` | `null` | New declaration ('VAR ... END_VAR' block). Null leaves it unchanged. Alternative to 'declarationFile'. |
| `implementation` | `string?` | `null` | New implementation (ST source). Null leaves it unchanged. Alternative to 'implementationFile'. |
| `declarationFile` | `string?` | `null` | Path to a file whose contents become the declaration. Alternative to 'declaration'. |
| `implementationFile` | `string?` | `null` | Path to a file whose contents become the implementation. Alternative to 'implementation'. |

### `twincat_delete_plc_object`

DEPRECATED — use twincat_plc_delete (kind=<kind of the element>). Delete a POU/GVL/DUT/INTERFACE/member (METHOD, ACTION, PROPERTY, Get/Set, TRANSITION) node by its caret-separated tree path. Whitelist: the PLC project root, the PLC instance, References, folders and any node whose type cannot be read are refused with an [Error]; the target is validated before the automatic logout. Deleting an object referenced elsewhere may break the build — follow with twincat_build_plc if that matters.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the node to delete. |

### `twincat_rename_plc_object`

DEPRECATED — use twincat_plc_rename (kind=<kind of the element>). Rename a POU/GVL/DUT/member node by its caret-separated tree path. Returns the new tree path. TwinCAT's prompt to update the references to the old name is answered by the tool: "Nein" by default, "Ja" with updateReferences=true; the result says whether it appeared. Rebuild afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the node to rename. |
| `newName` | `string` |  | New name for the node. |
| `updateReferences` | `bool` | `false` |  |

### `twincat_plcopen_export`

Export one or more objects to a PLCopen XML file. selector is a ';'-separated list of dotted object names relative to the PLC project, e.g. "POUs.FB_Foo;POUs.MAIN" (a bare name without its container fails). filePath is the absolute path of the XML file to write.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | `string` |  | Absolute path of the PLCopen XML file to write. |
| `selector` | `string` |  | ';'-separated list of dotted object names relative to the PLC project, e.g. "POUs.FB_Foo;POUs.MAIN" (a bare name without its container fails). |

### `twincat_plcopen_import`

Import objects from a PLCopen XML file. conflictMode is None, Rename, Replace, or Skip and defaults to Skip (non-destructive). filePath is the absolute path of the XML file to read.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | `string` |  | Absolute path of the PLCopen XML file to read. |
| `conflictMode` | `string?` | `null` | Conflict resolution: None, Rename, Replace, or Skip. Defaults to Skip (non-destructive). |

## Recovery

### `twincat_login`

Log the PLC online (Offline → Online). Parameter mode selects the apply strategy when the running application differs from the project: 'online_change' — apply changes to the running PLC without a stop (fast, retains variable values); 'download' — stop the PLC, transfer the full compiled program (leaves PLC in STOP — no auto-run); 'login_without_change' — attach to the running PLC without transferring code (monitoring only). Primary path: discovers the TwinCAT PLC Login DTE command ('Loginto' substring) and executes it. A Win32 dialog watcher handles the 3-way login modal and follow-up confirmations. Idempotent: if the PLC Logout command is available (already online), returns no-op success. If Login AND Logout are both disabled (no reachable target runtime / project not activated), returns success=false, isOnlineNow=false — never a false 'already online'. Online state is verified after login via the PLC Logout command's IsAvailable flag (same probe used by twincat_get_plc_online_state — correct after activate_config). Requires XAE running with a compiled solution open. Returns { success, mode, dialogHandled, isOnlineNow, runState, message }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` | `"online_change"` | Apply mode: 'online_change' \| 'download' \| 'login_without_change'. |
| `allowDownloadFallback` | `bool` | `false` | mode='online_change' only: when online change is not possible, accept a full download instead (stops the running PLC). Default false — the login is refused with an [Error]. Download confirmations are never answered 'Ja' for 'login_without_change'. |

### `twincat_logout`

Log out the PLC project (Online → Offline). Primary path: executes the TwinCAT PLC Logout DTE command (its IsAvailable flag is the reliable online signal — it stays correct after activate_config, unlike DTE.Debugger.CurrentMode). Fallback (older TC): ConsumeXml('<LogoutCmd/>') / DTE.Debugger.Stop(). Idempotent: returns a no-op result when the PLC is already offline. Returns { wasOnline, isOnlineNow, message }. Call before twincat_clean_solution / twincat_rebuild_solution to avoid build failures caused by an active Online connection. Requires XAE running with a solution open.

_No parameters._

### `twincat_clean_solution`

Clean the entire TwinCAT solution via DTE.Solution.SolutionBuild.Clean(). Removes compiler output and boot-project artefacts so the next build starts from scratch. Returns { success, message }. Requires XAE running with a solution open.

_No parameters._

### `twincat_rebuild_solution`

Rebuild the entire TwinCAT solution (Clean then Build). Forces a complete recompilation — equivalent to 'Rebuild All' in the IDE. Compile-only: does NOT activate the boot project or restart the runtime (use twincat_activate_config for that). Returns { success, failedProjects, errors[] } identical to twincat_build_solution. Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). A PLC source file changed on disk outside the XAE since the project was loaded refuses the build with [Error] (the build would compile the old in-memory state and its save could write it over the change); reload with twincat_open_project(closeCurrentFirst=true) or pass ignoreStaleFiles=true. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ignoreStaleFiles` | `bool` | `false` | Build even when PLC source files changed on disk outside the XAE (the in-memory state is built; the result starts with [Warn]). Default false refuses. |

### `twincat_rebuild_plc`

Rebuild a single PLC project (Clean then BuildProject). Forces a complete recompilation of the named (or auto-detected) PLC project. Compile-only: does NOT activate the boot project or restart the runtime. Returns { success, failedProjects, errors[] } identical to twincat_build_plc. Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). A PLC source file changed on disk outside the XAE since the project was loaded refuses the build with [Error] (the build would compile the old in-memory state and its save could write it over the change); reload with twincat_open_project(closeCurrentFirst=true) or pass ignoreStaleFiles=true. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | Name of the PLC project to rebuild (e.g. 'PLC'). Leave empty to auto-detect. |
| `ignoreStaleFiles` | `bool` | `false` | Build even when PLC source files changed on disk outside the XAE (the in-memory state is built; the result starts with [Warn]). Default false refuses. |

### `twincat_force_close_xae`

EXPLICIT, opt-in recovery for a stale or crashed TcXaeShell (XAE) instance. Never invoked implicitly — a normal mutating tool will never terminate XAE. Asks each TcXaeShell to close gracefully and only kills the ones that do not exit in time. REFUSES while a dismissable modal dialog (security code / restart / trial license) is open — that instance is alive, not a zombie; use twincat_dismiss_security_dialog instead. Terminating XAE discards any unsaved work in it. Use only when twincat_get_status reports a stale/crashed instance or a multi-XAE block you cannot otherwise clear. pid: close only that one TcXaeShell process (see the PID list in a multi-XAE twincat_get_status block reason) instead of every running instance — use this when several instances are running and only one is confirmed stale, so the others (possibly holding unsaved work) are left alone.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pid` | `int?` | `null` | PID of one specific TcXaeShell process to close. Omit to close all (original behaviour). |

## RuntimeControl

### `twincat_broadcast_search`

Scan for reachable TwinCAT targets on the local network via ADS broadcast. Pass a hostname or IP in 'searchTarget' for a directed search (requires TC3 4020.10+), or omit for a general broadcast across all network interfaces. Returns a JSON array of { name, netId, ipAddr, version, os }. IMPORTANT: Must be called BEFORE twincat_set_target changes the project to a remote target. If the project already targets a remote system, set the target back to local first. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `searchTarget` | `string?` | `null` | Hostname or IP address to search for directly, e.g. 'CX-12345' or '192.168.1.5'. Omit to broadcast to all devices on the network. |

### `twincat_add_route`

Add an ADS route to a remote TwinCAT target. Use 'projectRoute=true' for a project-local route (stored in .tsproj, preferred for isolation), or 'false' for a persistent system route (requires credentials). Call twincat_broadcast_search first to discover the target's netId and IP. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | AMS NetId of the remote target, e.g. '192.168.1.5.1.1'. |
| `ipAddr` | `string?` | `null` | IP address of the remote target. Either ipAddr or hostName must be provided. |
| `hostName` | `string?` | `null` | Hostname of the remote target. Either ipAddr or hostName must be provided. |
| `userName` | `string?` | `null` | Username for system route authentication (not needed for project routes). |
| `password` | `string?` | `null` | Password for system route authentication (not needed for project routes). |
| `projectRoute` | `bool` | `true` | True = project route (stored in .tsproj, no credentials required). False = persistent system route (requires userName/password). Default: true. |
| `netId` | `string?` | `null` | DEPRECATED alias for amsNetId, kept for one release so existing MCP clients that bind by this name keep working. Prefer amsNetId. |

### `twincat_set_target`

Set the project's target NetId and optionally target platform. Call this BEFORE twincat_activate_config to avoid the platform-mismatch dialog. Known platforms: 'TwinCAT RT (x64)', 'TwinCAT RT (x86)', 'TwinCAT OS (x64)' (User Mode Runtime). Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | AMS NetId of the target system, e.g. '192.168.1.5.1.1' or '127.0.0.1.1.1'. |
| `platform` | `string?` | `null` | Target platform string. Use 'TwinCAT OS (x64)' for User Mode Runtime. Omit to change only the NetId without touching the platform. |
| `netId` | `string?` | `null` | DEPRECATED alias for amsNetId, kept for one release. Prefer amsNetId. |

### `twincat_set_system_mode`

Switch a target's TwinCAT system to Config or Run over ADS — the XAE's 'Restart TwinCAT (Config Mode)' / 'Restart TwinCAT System': WriteControl Reconfig (config) or Reset (run) on AMS port 10000, then ReadState until the mode is confirmed. Use it when a running target ignores activate_config's restart (switch to config, activate, switch to run). Needs no XAE and no open solution. A switch stops the PLC on that target. After a switch to run the PLC runtime on port 851 is read as well (up to 15 s): the system can be in Run while the PLC stays stopped, which the result flags with [Warn]. amsNetId is required — there is no implicit local target.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string` |  | AMS NetId of the target system, e.g. '192.168.250.20.1.1'. Required; pass the local router's NetId explicitly to switch the local system. |
| `mode` | `string` |  | 'config' or 'run'. |
| `timeoutSeconds` | `int?` | `null` | Seconds to wait for ReadState to confirm the mode. Default 60, clamped to 5..300. |

### `twincat_restart_runtime`

Call ITcSysManager.StartRestartTwinCAT() to start or restart the TwinCAT runtime on the target. Use after twincat_activate_config: activate generates the boot project, this command starts it. Requires XAE running with a solution open.

_No parameters._

### `twincat_umr_list_instances`

List the registered TwinCAT 3 Usermode Runtime instances on this machine (sub-directories of %ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes that contain Start.bat + 3.1\TcRegistry.xml). These names are valid 'instanceName' values for twincat_umr_start. Returns a comma-separated list, or a note if none are installed.

_No parameters._

### `twincat_umr_start`

Start a registered TwinCAT 3 Usermode Runtime instance on the local machine. The UMR is a registered instance under %ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes; it is launched the documented way (its instance Start.bat semantics). Does NOT require stopping TcSysSrv — that is the unrelated router/management service. Initial state after a successful start is Config; an init failure is returned as an error (not a false Config). Fails if a UMR process is already tracked by this session.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instanceName` | `string?` | `null` | Registered instance name (see twincat_umr_list_instances). Omit to auto-resolve: the single instance, else 'UmRT_Default'. |
| `exePath` | `string?` | `null` | Full path to TcSystemServiceUm.exe. Defaults to the standard installation path. |

### `twincat_umr_set_mode`

Switch the User Mode Runtime started by twincat_umr_start between Config and Run mode. Config mode allows downloading a new configuration; Run mode executes the loaded PLC program. Switches over ADS on the instance's NetId (WriteControl on port 10000, as twincat_set_system_mode) and succeeds only once ReadState confirms the mode — a Run switch takes about 20 s on a UMR. After a Run switch the PLC runtime on port 851 is read as well: the system can be in Run while the PLC stays stopped, which the result flags with [Warn]. Refused with an [Error] when the instance's NetId is unknown.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` |  | Target mode: 'config' to switch to Config mode, 'run' to switch to Run mode. |
| `timeoutSeconds` | `int?` | `null` | Seconds to wait for ReadState to confirm the mode. Default 60, clamped to 5..300. |

### `twincat_umr_stop`

Stop the running User Mode Runtime process. Sends the exit command to the process and waits up to 5 seconds for it to terminate. Safe to call even if the UMR is already stopped.

_No parameters._

## Safety

### `twincat_safety_list`

Enumerate the TwinCAT Safety project structure: alias devices with SDSID, AliasDeviceType Type/SubType, IO name and LinkingMode as written in each .sds (the device name is the file name), the folder they live in, groups (.sal) and user FBs (.ufb). Read-only — reads .splcproj and .sds files from disk.

_No parameters._

### `twincat_safety_read_io_map`

Read the safe-I/O assignment as three separate fact lists: the alias devices (.sds: SDSID, type, IO); the channel usage of each group (.sal aliasDeviceIoUsage: variable, source/target, sdsId, channel), joined to a device ONLY by sdsId = the device's SDSID; and Mappings.xml rows as found, naming a device only when the row carries its own SDSID. Nothing is joined by position or guessed. Read-only.

_No parameters._

### `twincat_safety_read_program`

Structured dump of the FBD safety logic per group/network: FB instances (type + name), input/output port sources/targets (connection monikers), and FB parameters (discrepancy, Tof off-delays, EDM, restart). Flags [inferred] wiring. Read-only — parses .sal group files.

_No parameters._

### `twincat_safety_add_alias_device`

EXPERIMENTAL (the XAE reload step is not yet verified against a live TwinSAFE project). Create a standard digital alias device (a BOOL exchanged between the safety logic and the standard PLC): validates the name (IEC identifier with a configured signal prefix, see PTC_XAE_MCP_SAFETY_PREFIXES), allocates an SDSID no existing .sds uses, writes the .sds in the TwinSAFE editor's own format (Type 1, SubType 1 = digital input / 2 = digital output, Crc 0) next to the group's existing alias devices, registers it in .splcproj (temp files; the .sds is rolled back if the project file cannot be updated), reads it back, then reloads the project in XAE. Refuses an existing name. The CRC is left at 0 — run 'Verify Complete Safety Project' in the TwinSAFE editor before download. Never forges or computes the CRC. Never downloads/activates the safety project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `signalName` | `string` |  | Signal name — becomes the .sds file name. Must be an IEC identifier starting with a configured prefix (default bSI_, bSQ_, SI, SQ; override with PTC_XAE_MCP_SAFETY_PREFIXES). |
| `subType` | `int` | `2` | 2 = '1 Digital Output (Standard)': safety → standard PLC (e.g. bSI_ feedback). 1 = '1 Digital Input (Standard)': standard PLC → safety (e.g. bSQ_ request). Default 2. |
| `group` | `string?` | `null` | Group (.sal name without extension) the alias device belongs to. Required when the project has more than one group; omit otherwise. |

### `twincat_safety_link_alias_device`

Link an alias device's EL1918 connection-image symbol to a standard-PLC symbol (e.g. GVL_Safety.bSI_DriveReleased) via ITcSysManager.LinkVariables. Requires the safety project to be mapped/built so the connection symbol exists; returns a clear precondition error if not yet mapped.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `aliasDevicePath` | `string` |  | Caret-path of the alias device connection-image symbol in the safety sysmanager (e.g. 'TISC^TwinSAFE_PLC^Alias Devices^bSI_DriveReleased'). |
| `plcSymbolPath` | `string` |  | Dot-path of the standard-PLC symbol to link to (e.g. 'GVL_Safety.bSI_DriveReleased'). |

## Sbom

### `twincat_ipc_read_inventory`

READ-ONLY raw inventory of a Beckhoff IPC from its Device Manager (MDP) over ADS (AMS port 10000, index group 0xF302): device BTN/BIC, every module (TwinCAT version/build/System ID, installed Beckhoff software, OS, mainboard serial/BIOS, disks, NICs, CPU, memory) with all tables read dynamically (as many disks/NICs/software entries as the device reports). Each value carries its index offset; unknown modules and tables are returned as raw hex, never dropped. Returns JSON { success, status (ok | mdpUnavailable | targetUnreachable | mdpNotReady), message, netId, port, indexGroup, readAt, adsError, device { btn, bic }, modules [{ moduleId, moduleType, typeName, name, tables [{ tableIndex, name, entries [{ subIndex, name, dataType, value, raw, adsError, indexOffset }] }], decoded }], warnings }. A target without a Device Manager (e.g. a development VM or the user-mode runtime) yields status mdpUnavailable, not an [Error]. [Error] is returned only for bad arguments. The output contains IP/MAC addresses, route names and the TwinCAT System ID.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | AMS NetId of the target IPC (e.g. '192.168.1.10.1.1'). Omit for the open solution's own target system (activation target); with an explicit NetId no open solution is needed. |
| `moduleTypes` | `string[]?` | `null` | Optional filter on MDP module types, e.g. ['0x0008','0x000A'] (TwinCAT, Software). Known types: 0x0002 NIC, 0x0008 TwinCAT, 0x000A Software, 0x000B CPU, 0x000C Memory, 0x0018 OperatingSystem, 0x001C Mainboard, 0x001D DiskManagement. Default: all modules. |
| `includeRaw` | `bool` | `true` | Include the raw hex of every decoded value too (default true). Undecodable values always carry it. |
| `timeoutMs` | `int?` | `null` | ADS timeout per read in milliseconds (default 5000, 100-30000). A read that times out is repeated once before the error is recorded. |

### `twincat_ethercat_read_identities`

READ-ONLY online identity of every EtherCAT slave: VendorId, ProductCode, RevisionNo and SerialNo as the EtherCAT master reads them from the devices (ADS index groups 0x7/0x9/0x11), joined by fixed address with the projected identity of the configured boxes (device XML). Enumerates all EtherCAT master devices under TIID (or the one in masterTreePath); the master's AMS NetId and ADS port come from its device XML, and the result states which were read. Per slave: address, treePath, name, projected {vendorId, productCode, revisionNo}, online {vendorId, productCode, revisionNo, serialNo}, state/stateName, link/linkFlags, flags identityInvalid, revisionDiffers, productDiffers, vendorDiffers, serialAvailable (serialNo != 0), missing, extraLink, and source {netId, port, indexGroup, indexOffset}. Projected boxes without an online address and online addresses without a projected box are both listed. With includeCoe the CoE device info is added per slave (coe {status ok|noMailbox|notCoe|error, deviceName, hardwareVersion, firmwareVersion, serialNo, adsError}); serialNo (a string, so that leading zeros survive) and serialSource give the serial from the master (serialSource master), else from CoE 0x1018:04 (coe), else from the SoE S-0-0432 of an AX5000 drive (soe:S-0-0432, digits as read), else for a safety device its effective safety identifier (coe:0xF980:02, bic:BTN, hostDrive:S-0-0432); serialAvailable is false when none exists. The master service port is 0xFFFF. With includeDeviceDetails each slave may also carry bic {raw, orderNumber, btn, article, batch}, safety {isSafetyDevice, fsoeAddress, serialNo (0xF980:02, undocumented), effectiveSerial, effectiveSerialSource, viaHostDrive}, motor/feedback (OCT terminals) and soe.channels[] (AX5000; softwareVersions from P-0-0320, whose optionCard fields describe a plug-in option card of the drive, e.g. an encoder card, null when none is fitted); an AX5805/AX5806 also shows safety.hostDrive {address, serialNo, configuredSafetyOption}; every safety device gets an effective identifier or safetySerialMissing=true plus a warning and safetyIdentityComplete=false. The result has a summary {slaves, slavesRead, slavesPartial, slavesFailed, safetyDevices, safetyWithSerial, safetyUnknown, motors, motorsWithSerial}. Reads that time out are repeated once; every slave has readStatus ok|partial|failed with readErrors, the master (and the result) is status partial when any slave could not be read completely or the fixed addresses/state/link reads failed (the fixed addresses then fall back to the projected ones), and a safety device whose identity could not be read is safetyIdentityUnknown (not safetySerialMissing); safetyIdentityComplete is false in both cases. A master that does not answer (configuration not active on the target / no route) is a per-master status masterUnreachable and the other masters continue; a master whose address cannot be read from the XML is addressNotFound with the raw XML snippet. Never changes state, never switches the project to config mode. Requires XAE running with a solution open and the configuration active on the target.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `masterTreePath` | `string?` | `null` | Tree path of ONE EtherCAT master device, e.g. 'TIID^Device 1 (EtherCAT)'. Omit to read all masters. |
| `amsNetId` | `string?` | `null` | Override of the master's AMS NetId. Only valid together with masterTreePath. |
| `adsPort` | `int?` | `null` | Override of the master's ADS port. Only valid together with masterTreePath. |
| `includeCrc` | `bool` | `false` | Also read the CRC error counters of every slave (per port A-D). Default false. |
| `includeCoe` | `bool` | `true` | Also read the CoE device info of every slave with a mailbox (read-only SDO upload: 0x1008 device name, 0x1009 hardware version, 0x100A firmware version, 0x1018:04 serial). The master reports SerialNo 0 for most slaves; the CoE serial fills the gap. Default true. |
| `includeDeviceDetails` | `bool` | `true` | Also read the device details per slave (requires includeCoe): the BIC/BTN (CoE 0x10E2:01), the safety identity (0xF980) with an effective unique safety identifier, the OCT motor and feedback name plate (0x9009/0x9008), and for AX5000 drives the SoE parameters per channel (firmware, hardware, serial, configured type, safety option, digital motor name plate). Default true. |
| `timeoutMs` | `int?` | `null` | ADS timeout per read in milliseconds (default 5000, 100-30000). A read that times out is repeated once before the error is recorded. |

### `twincat_list_library_repositories`

READ-ONLY list of the PLC library repositories of the XAE (ITcPlcLibraryManager.Repositories): name and folder of each. With includeFiles=true, for every library reference of the PLC project(s) the resolved library (resolvedName, resolvedVersion, resolvedDistributor, resolution = effective | fixed | newest-installed | unresolved), its version folder in the repository and the library file(s) in it (.library / .compiled-library*) with SHA-256 (hashScope 'file': the hash of the file itself, not a Twinpack content hash). If no file is found: fileFound=false plus the searchedPaths. Returns JSON { success, repositories [{ name, folder, folderExists }], references [{ plcProject, placeholder, name, requestedVersion, resolved*, resolution, fileFound, files [{ path, sha256, hashScope, sizeBytes }], searchedPaths }], warnings } or an [Error] string. Requires XAE running with a solution open.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `includeFiles` | `bool` | `false` | Also resolve every library reference to its file(s) in the repositories and hash them. Default false. |
| `plcProjectPath` | `string?` | `null` | Only this PLC project (its name or tree path, e.g. 'PLC' or 'TIPC^PLC'). Default: all PLC projects of the solution. |

## Schema

### `twincat_get_status`

Get TwinCAT XAE environment status: IDE connectivity, the active project and PLC, and the TwinCAT system service state.

_No parameters._

### `twincat_get_log`

Get the log output visible in the Pneutronic XAE MCP window. The log grows for the whole host session, so only the newest maxChars characters are returned (default 20000) behind a note of how much was omitted; pass maxChars=0 for the full log.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `maxChars` | `int` | `20000` | Maximum number of log characters returned, newest kept (default 20000). 0 returns the full log. |

### `twincat_stop`

Stop the currently running TwinCAT operation (equivalent to clicking the Stop button).

_No parameters._

### `twincat_validate_schema`

Validate a JSON IO schema file without applying it. Returns validation result with any issues found. Optionally pre-validates a brownfield targetMasterPath.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `schemaPath` | `string` |  | Absolute path to the JSON schema file |
| `targetMasterPath` | `string?` | `null` | brownfield-guard: optional exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'). When supplied, its format is validated. |

### `twincat_apply_schema`

Apply a JSON IO schema to the active TwinCAT XAE project. Builds EtherCAT tree, creates GVL, links variables, and exports. Progress is visible in the UI window. Fail-closed by default (failClosed=true): the PLC project and EtherCAT master must match exactly (no fallback to the first PLC/master), the target master must be provably empty, and an existing GVL of the same name at the target path (TIPC^<plc>^<plc> Project^GVLs^<gvlName>) is an [Error] — nothing is deleted — unless overwriteExistingGvl=true. Links use the exact process-image symbol <gvlName>.<varName>; a same-named variable of another GVL is never linked. During the build the XAE's 'Sync Master' and 'Double use of the same Ethernet interface' boxes are answered with OK and the 'unrestored variable links' question with 'Ja' (keep the link information); every answered dialog is listed in the result, and any other dialog that blocks the build ends the run with an [Error] naming it. The GUI's Apply button keeps the lenient greenfield behaviour.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `schemaPath` | `string` |  | Absolute path to the JSON schema file |
| `plcProject` | `string?` | `null` | PLC project name override. Leave empty to use the schema's twincat.plcProjectName (the GUI selection is never used). |
| `etherCatMaster` | `string?` | `null` | EtherCAT master name override. Leave empty to use the schema's twincat.deviceName (the GUI selection is never used). |
| `targetMasterPath` | `string?` | `null` | brownfield-guard: exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'). When set, the master is resolved exactly (no fuzzy fallback) and fail-closed mode is activated. |
| `failClosed` | `bool` | `true` | Fail-closed mode (default true). The PLC project and EtherCAT master must match exactly, and the empty-check aborts on any COM uncertainty. Pass false for the lenient greenfield resolution (only PLC/first master fallback, StartsWith master match) the GUI uses. |
| `autoBuild` | `bool` | `true` | brownfield-guard: when false, skip the full productive SolutionBuild.Build(true) so a brownfield apply does not re-bind the existing PLC project (new IO is translated by a later build_plc). Default true preserves current behaviour. |
| `overwriteExistingGvl` | `bool` | `false` | Replace an existing GVL of the same name at the exact target path (TIPC^<plc>^<plc> Project^GVLs^<gvlName>); its declarations and links are lost. Default false: such a collision is an [Error] and nothing is deleted. Same as the schema's twincat.overwriteExistingGvl. Never deletes a same-named GVL anywhere else. |

## Scope

### `twincat_create_scope_project`

Add a TwinCAT Scope (measurement) project to the open solution using the Scope View 'YT Scope Project' template. Returns the new project's path. projectName defaults to 'MeasurementProject'. Requires TE130X Scope View to be installed.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name for the new measurement project. Defaults to 'MeasurementProject' when omitted. |

### `twincat_remove_scope_project`

Unregister a TwinCAT Scope (measurement) project from the open solution — the counterpart to twincat_create_scope_project. Removes the project node only; does NOT save the solution and does NOT delete files from disk. projectName must match an existing measurement project; required when the solution has more than one (fails loud otherwise so a customer-shipped scope project is never removed by accident). Idempotent: returns a benign summary if no matching project is present. Use this before closing/deleting a created scope project so a blocked close cannot leave a dangling node (E_ABORT 'Missing Project Nodes' on reopen).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to remove. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_add_scope_channel`

Add a chart → axis-group → channel to the scope project and bind it to a PLC symbol (e.g. 'TIPC^PLC^PLC Projekt^MAIN.rActualValue'). Reuses an existing chart/axis by name if they already exist. chartName, axisName, channelName default to the symbol path. Set stackedAxis=true to place the channel on its own Y-axis. An existing DataPool acquisition with the same Symbol Name and port is reused (not duplicated); otherwise one is created. The acquisition is pointed at the actual PLC symbol so it records data, every field is read back and a field that did not land returns [Error]: symbolName overrides the PLC-side symbol name derived from symbol; targetPort defaults to 851 (TC3 PLC 1 Runtime); sampleMode (e.g. 'Task Sampletime') and sampleTimeMs tune sampling; symbolComment is optional. projectName selects which measurement project to use when the solution has more than one (omit if only one; an ambiguous omission returns a structured [Error] listing the projects). Returns the channel's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | PLC symbol path to bind the channel to, e.g. 'TIPC^PLC^PLC Projekt^MAIN.rActualValue'. |
| `chartName` | `string?` | `null` | Name of the chart to add the channel to; reused if it already exists. Defaults to the symbol path. |
| `axisName` | `string?` | `null` | Name of the axis-group within the chart; reused if it already exists. Defaults to the symbol path. |
| `channelName` | `string?` | `null` | Name of the created channel. Defaults to the symbol path. |
| `stackedAxis` | `bool` | `false` | True to place the channel on its own Y-axis instead of sharing the axis-group's axis. Default false. |
| `symbolName` | `string?` | `null` | PLC-side symbol name for the DataPool acquisition, if it must differ from the name derived from symbol. |
| `targetPort` | `int?` | `null` | ADS target port of the PLC runtime to sample from. Defaults to 851 (TC3 PLC 1 Runtime). |
| `sampleMode` | `string?` | `null` | Sample mode for the acquisition, e.g. 'Task Sampletime'. Uses the scope's default when omitted. |
| `sampleTimeMs` | `int?` | `null` | Sample time in milliseconds. Uses the scope's default when omitted. |
| `symbolComment` | `string?` | `null` | Optional free-text comment stored on the acquisition's symbol binding. |
| `projectName` | `string?` | `null` | Name of the measurement project to use. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_read_scope_acquisition`

Read the full symbol/ADS structure of one DataPool acquisition in the scope project. acqSelector is the acquisition name or 'DataPool^<name>'. Returns every ProjectItem.Properties entry (raw key=value), plus the resolved Symbol Name, Data Type, Sample Mode, Sample Time, and Target Port, and flags acquisitions with no symbol bound (would record nothing). projectName selects the measurement project when the solution has more than one. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `acqSelector` | `string` |  | Acquisition name, or 'DataPool^<name>' tree path. |
| `projectName` | `string?` | `null` | Name of the measurement project to read from. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_set_scope_acquisition_symbol`

Set or repair the PLC symbol and ADS/sample settings of an existing DataPool acquisition (the partial-update analogue of twincat_set_scope_labels). acqSelector is the acquisition name or 'DataPool^<name>'. Supply at least one of: symbolName (PLC-side symbol, e.g. 'GVL_HMI.HmiOpened'), targetPort (e.g. 851), targetSystem, sampleMode, sampleTimeMs, timeOffsetS, symbolComment, enabled. Only supplied fields change. Every field is read back; any field that did not land returns [Error] naming what did and did not apply. projectName selects the measurement project when the solution has more than one. Returns the read-back values.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `acqSelector` | `string` |  | Acquisition name, or 'DataPool^<name>' tree path, of the acquisition to update. |
| `symbolName` | `string?` | `null` | PLC-side symbol name to bind, e.g. 'GVL_HMI.HmiOpened'. Only changed when supplied. |
| `targetPort` | `int?` | `null` | ADS target port of the PLC runtime to sample from, e.g. 851. Only changed when supplied. |
| `targetSystem` | `string?` | `null` | Target system (AMS NetId) to sample from. Only changed when supplied. |
| `sampleMode` | `string?` | `null` | Sample mode for the acquisition, e.g. 'Task Sampletime'. Only changed when supplied. |
| `sampleTimeMs` | `int?` | `null` | Sample time in milliseconds. Only changed when supplied. |
| `timeOffsetS` | `double?` | `null` | Time offset in seconds applied to the acquisition. Only changed when supplied. |
| `symbolComment` | `string?` | `null` | Free-text comment stored on the acquisition's symbol binding. Only changed when supplied. |
| `enabled` | `bool?` | `null` | Whether the acquisition is enabled. Only changed when supplied. |
| `projectName` | `string?` | `null` | Name of the measurement project to update. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_set_scope_axis_mode`

EXPERIMENTAL — the stacked-axis property key is not documented and not yet verified on a live TE130x. Set the Y-axis display mode of a chart in the scope project. mode is 'overlaid' (all channels share one Y-axis) or 'stacked' (each channel gets its own Y-axis). chartName is the chart's tree name. The value is read back; if no stacked-axis property exists or the read-back differs, returns [Error] listing the chart's property keys. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `chartName` | `string` |  | Tree name of the chart to update. |
| `mode` | `string` |  | Y-axis display mode: 'overlaid' (all channels share one Y-axis) or 'stacked' (each channel gets its own Y-axis). |
| `projectName` | `string?` | `null` | Name of the measurement project to update. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_set_scope_labels`

Set or fix the display labels of an existing chart or axis-group node in the scope project. nodeSelector is the node's name. Supply at least one of: name (rename the node), chartTitle (set the chart title text), axisTitle (set the Y-axis title). Flips Show Title to Custom for any supplied title. Every value is read back; if any requested label did not land, returns [Error] naming what did and did not apply. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `nodeSelector` | `string` |  | Name of the chart or axis-group node to update. |
| `name` | `string?` | `null` | New name for the node (rename). Only changed when supplied. |
| `chartTitle` | `string?` | `null` | Title text, only where the node has a title property; TE130x 4026.22 has none on a chart or axis group (a chart shows its name — use `name`), so there it returns [Error]. |
| `axisTitle` | `string?` | `null` | Y-axis title text. Only changed when supplied; flips Show Title to Custom. |
| `projectName` | `string?` | `null` | Name of the measurement project to update. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_list_scope_channels`

Enumerate all charts, axis-groups, channels, and acquisitions in the scope project. Returns one line per node with kind (CHART|AXISGROUP|CHANNEL|ACQUISITION), tree path, bound acquisition (for channels), and whether the display label is still at its template default. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to list. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_get_scope_status`

Report whether a TwinCAT Measurement (scope) project exists in the open solution and summarise its structure (chart/axis/channel/acquisition counts). projectName selects the measurement project when the solution has more than one (ambiguous omission returns a structured [Error] listing them). Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to report on. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_export_scope_data`

Export the recorded scope data to a file next to the open solution. format is one of CSV (default), BINARY, TDMS, DAT, SVD. Returns the written file path. projectName selects the measurement project when the solution has more than one. Returns [Error] if no data has been recorded yet; any other export failure is reported with its message and HRESULT.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `format` | `string` | `"CSV"` | Export file format: CSV (default), BINARY, TDMS, DAT, or SVD. |
| `projectName` | `string?` | `null` | Name of the measurement project to export from. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_start_scope_record`

Start a TwinCAT Scope recording (IMeasurementScope.StartRecord on the scope-config root). The tool does not check the current state itself — call twincat_get_scope_record_state first. Returns [Error] if StartRecord fails or no scope project exists. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to start recording on. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_stop_scope_record`

Stop the active TwinCAT Scope recording (IMeasurementScope.StopRecord). Returns [Error] if StopRecord fails. After stopping, the scope enters 'Reply' state and recorded data is available for twincat_save_scope_data or twincat_export_scope_data. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to stop recording on. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_get_scope_record_state`

Read the current TwinCAT Scope recording state: 'Config' (not recording), 'Record' (recording in progress), or 'Reply' (recording stopped, data available). Polling-friendly — call before start/stop to guard preconditions. Returns [Error] (never 'Config') when the state cannot be read. projectName selects the measurement project when the solution has more than one. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to read. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_save_scope_data`

Save the recorded TwinCAT Scope data as a native .svdx file (SaveSVD). The scope must be in 'Reply' state (recording stopped). path is the output file path; if omitted, defaults to ScopeData.svdx next to the open solution. Returns the written file path. Returns [Error] if no data has been recorded. Use twincat_export_scope_data for CSV/TDMS/DAT/BINARY formats. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string?` | `null` | Output .svdx file path. Defaults to ScopeData.svdx next to the open solution when omitted. |
| `projectName` | `string?` | `null` | Name of the measurement project to save from. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_set_scope_record_settings`

Set TwinCAT Scope record configuration on the scope-config root. Supply at least one of: recordTimeS (total recording duration in seconds, > 0), ringbuffer (true = Restart Record / ring mode), fileStore (file-store path), recordMode ('UserStart' or 'TriggerStart'), autoSaveMode ('None', 'SVDX', or 'Export'), autoSavePath, filenameMask, syncMode ('Soft', 'Medium', or 'Hard'). Only supplied fields are changed. EXPERIMENTAL — these property keys are matched by normalised token and are not yet verified on a live TE130x; every value is read back and any field that did not land returns [Error] listing the scope root's property keys. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `recordTimeS` | `double?` | `null` | Total recording duration in seconds. Must be > 0. Only changed when supplied. |
| `ringbuffer` | `bool?` | `null` | True selects ring-buffer ('Restart Record') mode. Only changed when supplied. |
| `fileStore` | `string?` | `null` | File-store path for recorded data. Only changed when supplied. |
| `recordMode` | `string?` | `null` | NOT writable: 'Record.Start Mode' is read-only in TE130x (it follows from the trigger configuration). Supplying it returns [Error] for this field; omit it. |
| `autoSaveMode` | `string?` | `null` | Auto-save mode: 'None', 'SVDX', or 'Export'. Only changed when supplied. |
| `autoSavePath` | `string?` | `null` | Auto-save output path. Only changed when supplied. |
| `filenameMask` | `string?` | `null` | Filename mask/pattern for auto-saved files. Only changed when supplied. |
| `syncMode` | `string?` | `null` | Synchronisation mode: 'Soft', 'Medium', or 'Hard'. Only changed when supplied. |
| `projectName` | `string?` | `null` | Name of the measurement project to update. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_create_scope_trigger_group`

EXPERIMENTAL — the trigger-group element id and its property keys are not documented and not yet verified on a live TE130x. Create a trigger group under the scope project's existing Trigger node (never creates that node). action is the trigger event that fires when all sets in the group are satisfied: 'StopRecord', 'StopDisplay', 'RestartDisplay', 'StartSubsave', 'StopSubsave', or 'EventMarker'. The action is read back; if it did not land, the group is deleted again and [Error] lists the group's property keys. Use twincat_add_scope_trigger_set to add conditions to the group. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | Trigger event fired when all sets in the group are satisfied: 'StopRecord', 'StopDisplay', 'RestartDisplay', 'StartSubsave', 'StopSubsave', or 'EventMarker'. |
| `projectName` | `string?` | `null` | Name of the measurement project to create the group in. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_add_scope_trigger_set`

EXPERIMENTAL — the condition property keys are not yet verified on a live TE130x. Add a trigger set to an existing trigger group in the TwinCAT Scope project. groupName is the name of the trigger group (from twincat_create_scope_trigger_group). setType is 'ChannelSet' or 'DirectorySet' (TriggerSetType ids 0/1 of the Scope Automation Interface). For ChannelSet: supply symbol (PLC channel symbol), threshold (numeric trigger level), edge ('Rising'|'Falling'|'Both'), compare ('Greater'|'Less'|'Equal'). Each condition is read back; any condition that did not land returns [Error] naming what did and did not apply. projectName selects the measurement project when the solution has more than one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `groupName` | `string` |  | Name of the trigger group to add the set to (from twincat_create_scope_trigger_group). |
| `setType` | `string` | `"ChannelSet"` | Trigger set type: 'ChannelSet' or 'DirectorySet' (TriggerSetType 0/1). Default 'ChannelSet'. |
| `symbol` | `string?` | `null` | NOT supported: the trigger channel is a Guid reference to a DataPool acquisition that the Automation Interface does not expose. Supplying it returns [Error] for this condition; pick the channel in the Scope editor. |
| `threshold` | `double?` | `null` | Numeric trigger level. Only used for ChannelSet. |
| `edge` | `string?` | `null` | Trigger edge: 'Rising', 'Falling', or 'Both'. Only used for ChannelSet. |
| `compare` | `string?` | `null` | Comparison operator: 'Greater', 'Less', or 'Equal'. Only used for ChannelSet. |
| `projectName` | `string?` | `null` | Name of the measurement project to update. Required when the solution has more than one; omit when there is exactly one. |

### `twincat_list_scope_triggers`

EXPERIMENTAL — reads the trigger properties by token match (keys not yet verified on a live TE130x). Enumerate all trigger groups and their trigger sets in the TwinCAT Scope project. Returns group name, action, and for each set: setType, symbol, threshold, edge, and compare condition. projectName selects the measurement project when the solution has more than one. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the measurement project to list. Required when the solution has more than one; omit when there is exactly one. |

## ScopeFile

### `twincat_read_scope_file`

EXPERIMENTAL — read a saved TwinCAT Scope recording (.svdx, e.g. from twincat_save_scope_data) without the XAE. Runs TC3ScopeExportTool.exe (installed with TE130x Scope View or TF3300 Scope Server) headless into a temporary CSV and returns, per channel: name, symbol, comment, data type, sample time, NetId/port, scale/offset/unit, sample count, first/last time, min/max/mean. includeValues=true adds the values of the selected channels in a time window, decimated min/max per bucket (spikes survive) to at most maxPoints per channel. Times are ms since the recording start; start/end are UTC. Values are as the export tool writes them. The path must lie in an allowed directory (%TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS). Result: an [Experimental] line, a summary line, then JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Path of the .svdx file. |
| `channels` | `string?` | `null` | Channels whose values to return (includeValues): channel names or symbol names, comma-separated. Empty = all channels. |
| `fromMs` | `double?` | `null` | Start of the value window in ms since the recording start. Empty = from the start. |
| `toMs` | `double?` | `null` | End of the value window in ms since the recording start. Empty = to the end. |
| `maxPoints` | `int` | `ScopeExportCsv.DefaultMaxPoints` | Maximum points per channel (default 500, at most 5000); more samples are decimated to min/max per bucket. |
| `includeValues` | `bool` | `false` | Return the (decimated) values, not only the per-channel statistics. Default false. |

### `twincat_convert_scope_file`

EXPERIMENTAL — convert a saved TwinCAT Scope recording (.svdx) into an export file without the XAE, using TC3ScopeExportTool.exe (TE130x Scope View / TF3300 Scope Server) headless. The target extension selects the format (.csv, .tdms, … as the export tool supports them; .tdms/.dat may need a full Scope View/Server licence). An existing target is refused unless overwrite=true. Both paths must lie in an allowed directory (%TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or a root in PTC_XAE_MCP_ALLOWED_PATHS).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Path of the .svdx file. |
| `targetPath` | `string` |  | Output file; its extension selects the format, e.g. C:\temp\run1.csv. |
| `overwrite` | `bool` | `false` | Replace an existing target file. Default false. |

## VariantManagement

### `twincat_get_variant_status`

Returns the active variant and all available variants/groups for the open TwinCAT project. Returns JSON { activeVariant, availableVariants, groups } or [Error] string. Returns empty lists (not an error) when the project has no Variant Management configured.

_No parameters._

### `twincat_set_active_variant`

Sets the active project variant by name. Optionally rebuilds the solution immediately after switching. Returns a status message (and build log when rebuildAfter=true) or [Error] string. The name is checked against the project's variants/groups first (unknown -> [Error], nothing changed); the switch is read back and a failed switch restores the previous variant. Pass rebuildAfter=false to switch without building — a rebuild will still be required for changes to take effect.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variantName` | `string` |  | Variant name as defined in the project (e.g. SIMULATION, PRODUCTION). Use [GroupName] syntax to activate a variant group. |
| `rebuildAfter` | `bool` | `true` | Run twincat_build_solution after switching (default true). Pass false to switch without building. |

## VisibleEditor

### `twincat_open_object`

Surface ANY Solution-Explorer node in the VISIBLE TwinCAT XAE so a live MCP-driven demo/video always shows which object is being worked on. treePath is the same caret-separated path as twincat_read_pou (e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN' for a PLC object, or 'TIID^Device 1 (EtherCAT)' for an EtherCAT device). Two behaviours, because XAE treats the node families differently: a PLC source object (POU/GVL/DUT/INTERFACE) opens its OWN editor tab; a system-manager node (EtherCAT/IO, Motion/NC, TcCOM) and a Scope node are ACTIVATED in the Solution-Explorer tree (selected + Enter, like a double-click) — sysman nodes share ONE sysman editor (no per-node tab), and a Scope view opens on activation; when the XAE cannot be brought to the foreground the node is only selected (said so in the result). For a Scope node pass the Solution-Explorer names, e.g. '<scope project>^<scope node>'. The read tools (read_pou, read_gvl, read_pou_member) auto-open. The headless write tools do NOT auto-open — call this explicitly before a write for full video coverage. Returns one line describing what happened (opened tab vs. selected node) and whether the OS foreground raise was confirmed (Windows may block a focus steal — reported, not an error).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the node to surface, e.g. 'TIPC^PLC^PLC Projekt^POUs^01_Application^MAIN' for a PLC object, or 'TIID^Device 1 (EtherCAT)' for an EtherCAT device. |

### `twincat_close_object`

Counterpart to twincat_open_object: tidy up a live demo. For a PLC source object, saves and closes its editor tab. For a system-manager node there is no per-node tab (one shared sysman editor), so this is a reported no-op, never an error. treePath is the same caret-separated path as twincat_open_object. Returns one line describing what happened.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the node to close, same as passed to twincat_open_object. |
