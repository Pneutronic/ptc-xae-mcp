<!-- GENERATED FILE — do not hand-edit.
     Regenerate with: python tools/gen_tool_reference.py
     Source: [McpServerTool]/[Description] attributes in
     src/PtcXaeMcp.WinForms/Mcp/TwinCatMcpTools.*.cs (issue #157 item 4). -->

# MCP tool reference

236 tools, generated from the source attributes — one section per
`TwinCatMcpTools.<Group>.cs` partial file, tools in source order within each.

## Contents

- [ActiveProject](#activeproject) (1)
- [AdsVariable](#adsvariable) (4)
- [Build](#build) (14)
- [ClientInfo](#clientinfo) (1)
- [Coe](#coe) (4)
- [CoreDump](#coredump) (3)
- [Diagnostics](#diagnostics) (9)
- [Doctor](#doctor) (1)
- [EditorNavigation](#editornavigation) (2)
- [EtherCat](#ethercat) (6)
- [EventClasses](#eventclasses) (4)
- [EventLogger](#eventlogger) (2)
- [Fieldbus](#fieldbus) (3)
- [Hmi](#hmi) (8)
- [HmiAuthoring](#hmiauthoring) (12)
- [HmiInternalSymbols](#hmiinternalsymbols) (4)
- [HmiTmc](#hmitmc) (10)
- [HmiViews](#hmiviews) (7)
- [HostSelection](#hostselection) (1)
- [IbnLive](#ibnlive) (7)
- [IoLink](#iolink) (3)
- [IoMapping](#iomapping) (4)
- [Libraries](#libraries) (11)
- [Lifecycle](#lifecycle) (4)
- [NcAxes](#ncaxes) (5)
- [PersistentSnapshot](#persistentsnapshot) (5)
- [PlcControl](#plccontrol) (3)
- [PlcEntries](#plcentries) (2)
- [PlcInstanceSettings](#plcinstancesettings) (5)
- [PlcObjects](#plcobjects) (7)
- [PlcOnlineStatus](#plconlinestatus) (1)
- [PlcProjectInfo](#plcprojectinfo) (1)
- [PlcProjectLifecycle](#plcprojectlifecycle) (7)
- [PlcSearch](#plcsearch) (2)
- [PlcTasks](#plctasks) (1)
- [PouMembers](#poumembers) (2)
- [Receipts](#receipts) (1)
- [Recovery](#recovery) (6)
- [RuntimeControl](#runtimecontrol) (9)
- [Safety](#safety) (5)
- [Sbom](#sbom) (3)
- [Schema](#schema) (5)
- [Scope](#scope) (18)
- [ScopeFile](#scopefile) (2)
- [SignalTestbench](#signaltestbench) (4)
- [StaticAnalysis](#staticanalysis) (1)
- [TcUnit](#tcunit) (1)
- [VariantManagement](#variantmanagement) (5)
- [VisibleEditor](#visibleeditor) (2)
- [Visu](#visu) (4)
- [Workflow](#workflow) (2)
- [XaeDialogs](#xaedialogs) (2)

## ActiveProject

### `twincat_get_active_project`

Get the open TwinCAT solution: { solutionOpen, solutionPath, solutionName }. Check it before changing anything.

_No parameters._

## AdsVariable

### `twincat_read_ads_variable`

Read the live value of a PLC symbol via ADS from a running PLC or UMR. Not the source declaration (use twincat_plc_read). Returns JSON { success, symbol, plcType, value, amsNetId, adsPort } or [Error]. Types: BOOL BIT BYTE WORD DWORD SINT INT DINT LINT USINT UINT UDINT ULINT REAL LREAL STRING and any ENUM; for an ENUM, value is { name, value } (name null if no member matches).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPath` | `string` |  | Fully-qualified PLC symbol path, e.g. 'MAIN.rSpeed'. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target, e.g. '192.168.1.1.1.1'. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851 (first PLC); e.g. 852 for a second PLC. |

### `twincat_read_ads_variables_batch`

Read the live values of many PLC symbols in one ADS call. Much cheaper than repeated twincat_read_ads_variable calls. Returns JSON { success, count, results: [{ symbol, plcType, value, ok, message }], amsNetId, adsPort }, or [Error] on a connection failure. An unreadable symbol does not fail the batch (ok=false). ENUM values as in twincat_read_ads_variable. Never writes.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPaths` | `string[]` |  | Symbol paths, at least one, e.g. ['MAIN.nStep', 'MAIN.fbCyl.LimitSwitchWorkingPosition']. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_inspect_fb_instance`

Dump an FB/struct instance's member layout and RPC methods via ADS. Read-only diagnostic. Shows whether a PROPERTY is reachable as a symbol (member with isProperty=true) or via an RPC getter (__get<Name>/__set<Name>, only if compiled with RpcEnable="plc" per property); many are neither and cannot be read by this tool. Returns JSON { success, instancePath, typeName, members: [{ name, instancePath, isProperty, typeName, size, offset }], rpcMethods: [{ name, isVoid, parameterCount }], amsNetId, adsPort } or [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instancePath` | `string` |  | Fully-qualified FB/struct instance path, e.g. 'MAIN.fbMachine'. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_write_ads_variable`

Write a value to a live PLC symbol via ADS on a running PLC or UMR. Takes effect in the running process immediately. Refuses input-mapped symbols (AT %I*). Returns JSON { success, symbol, plcType, writtenValue, amsNetId, adsPort } or [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbolPath` | `string` |  | Fully-qualified PLC symbol path, e.g. 'MAIN.bEnable'. |
| `value` | `string` |  | Value as text, e.g. 'true', '42', '3.14', 'Hello'. BOOL accepts only true/false/1/0; an ENUM takes a member name ('E_STATE_RUNNING') or a number. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

## Build

### `twincat_get_error_list`

Read the XAE Error List: severity, text, file and line per entry. Does not build or clear (that is twincat_clear_error_list). Identical entries are listed once with a '(2× in the Error List)' note; the first line gives total and distinct count. The list also shows runtime EventLogger events, sometimes twice; for alarm diagnosis use twincat_read_event_logger. When paging, the last line gives total, has_more and next_offset. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `minLevel` | `string?` | `null` | Lowest severity kept: 'error', 'warning' or 'message'. Default: all. |
| `limit` | `int` | `0` | Max distinct entries; 0 (default) = all. |
| `offset` | `int` | `0` | Entries to skip (next_offset of the previous page). Default 0. |

### `twincat_clear_error_list`

Clear all entries of the XAE Error List, for instance before a build. Returns the cleared count (-1 if unknown). Needs a running XAE.

_No parameters._

### `twincat_build_plc`

Compile only the PLC project, faster than a full solution build. Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only objects a task's program reaches are compiled). Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project name (e.g. 'PLC'). Empty = first .plcproj. |
| `ignoreStaleFiles` | `bool` | `false` | Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses. |
| `expectSymbols` | `string[]?` | `null` | Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip. |

### `twincat_plc_check_all_objects`

Run 'Check all objects': compile every PLC object, also ones no task reaches. twincat_build_plc compiles only what a task reaches, so errors in uncalled code (or a whole library project) go unnoticed; use this to verify a library or code not called yet. Clears the Error List first. Same preflights as twincat_build_plc (stale files refuse, library references warn). An XAE lacking the automation call needs the menu command, which brings the XAE to the front: only with bringToFront=true, else [Error]; the result says which way ran. Result: 'Check all objects: no errors' or '[Error] Check all objects failed.' with the errors and file/line. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC name (e.g. 'PLC') or its project name. Empty = first PLC project. |
| `ignoreStaleFiles` | `bool` | `false` | Check despite PLC files changed on disk outside the XAE (checks the in-memory state; result starts with [Warn]). Default false refuses. |
| `bringToFront` | `bool` | `false` | Allow the XAE to come to the front if the menu-command fallback is needed. Default false. |

### `twincat_build_solution`

Build the whole solution: PLC code and IO mapping. Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only objects a task's program reaches are compiled). Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ignoreStaleFiles` | `bool` | `false` | Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses. |
| `expectSymbols` | `string[]?` | `null` | Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip. |

### `twincat_activate_config`

Activate the TwinCAT configuration on the project's target. Call twincat_set_target first to choose another runtime. Without autoConfirmRestart only the registry is written: nothing is deployed and the PLC keeps its OLD code. With it TwinCAT restarts into the new config and boot project (forcibly restarting the PLC); the result is 'Activated' only when the target's system state (ADS port 10000) reads Run after the restart and holds it 1 s within runTimeoutSeconds, else an [Error] with the last state. An XAE error box meanwhile (e.g. 'Zielsystem meldet fatalen Fehler') ends the wait with '[Error] Activation failed: <box text>'; OK-only boxes are closed with OK, boxes that ask a question are never answered. A trial-license prompt is declined with [Error] unless autoGenerateLicenses. Only windows of the attached XAE are touched. To deploy to an already running PLC without a restart prefer twincat_login. Run does not prove the PLC application is up: verify with twincat_read_ads_variable (amsNetId). Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `autoConfirmRestart` | `bool` | `false` | Restart TwinCAT into the new config (forcibly restarts the PLC; confirms the restart dialog). Default false = registry only, no deploy. |
| `autoGenerateLicenses` | `bool` | `false` | Answer the trial-license prompt with 'Ja'. An unreadable security code (TC 4026.x) is never guessed: the dialog is cancelled and the [Error] names the flow twincat_open_license_manager_and_request_trial → twincat_generate_trial_licenses → twincat_submit_security_code. Default false. |
| `autoConfirmLogin` | `bool` | `false` | Confirm the 'Applikation … seit dem letzten Download geändert' login modal if it appears. Default false. |
| `loginMode` | `string` | `"online_change"` | Answer for that modal: 'online_change' (default) \| 'download' \| 'login_without_change'. |
| `updateBootProject` | `bool` | `true` | On download, tick 'Bootapplikation aktualisieren' so the code survives a runtime restart. Default true. |
| `allowDownloadFallback` | `bool` | `false` | loginMode='online_change' only: if online change is impossible, download instead (stops the running PLC). Default false cancels the modal with [Error]. |
| `runTimeoutSeconds` | `int?` | `null` | autoConfirmRestart only: seconds to wait for Run. Default 60, clamped to 5..300. |

### `twincat_get_license_info`

Read the licenses of the project's target system over ADS (AMS port 30). As the License editor shows them: status (Present / Trial / Expired / Missing / Unknown), order number, name, id, expiry, used/count. The target must answer over ADS (Local = this PC). Needs an open solution.

_No parameters._

### `twincat_list_required_licenses`

List the licenses marked as required for the next trial-license generation. Add one with twincat_add_required_license before twincat_open_license_manager_and_request_trial. Needs an open solution.

_No parameters._

### `twincat_add_required_license`

Mark a license as required so the next trial-license generation includes it. licenseId is required; an orderNo alone is rejected. Writes the .tsproj license list (created if missing) and reads it back. Idempotent. Then call twincat_open_license_manager_and_request_trial. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `licenseId` | `string?` | `null` | License GUID, e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}'. Required. |
| `orderNo` | `string?` | `null` | Order number, e.g. 'SAMPLE-001'. Not sufficient on its own. |

### `twincat_remove_required_license`

Remove a license from the required-license list. Give licenseId or orderNo. Idempotent. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `licenseId` | `string?` | `null` | License GUID. Optional if orderNo is given. |
| `orderNo` | `string?` | `null` | Order number, e.g. 'SAMPLE-001'. Optional if licenseId is given. |

### `twincat_open_license_manager_and_request_trial`

Open SYSTEM > License in the XAE and click the 7-day trial license button. If the click cannot land (WPF view), the result says what to do by hand. Then call twincat_generate_trial_licenses for the confirmation and security-code dialogs. Needs an open solution.

_No parameters._

### `twincat_generate_trial_licenses`

Answer the trial-license prompt with 'Ja' and screenshot the security code. Waits up to 10 s for the 'Sicherheitscode eingeben' dialog and returns it as a PNG image; read the 5-character code and call twincat_submit_security_code(code). Needs no open solution. If the prompt is not visible yet, call twincat_open_license_manager_and_request_trial first.

_No parameters._

### `twincat_submit_security_code`

Enter the trial-license security code into its open dialog and click OK. The 'Sicherheitscode eingeben' dialog must be visible; take the code from the twincat_generate_trial_licenses image. Closes the success popup that follows (waits up to 2 s). Needs no open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `code` | `string` |  | The 5-character code, e.g. 'bW5Rm'. |

### `twincat_dismiss_security_dialog`

Read, type and confirm the code of the open security-code dialog. Works only when the 'Sicherheitscode eingeben' dialog shows the code as window text; on TC3 4026.22 it is drawn and cannot be read — use twincat_generate_trial_licenses, then twincat_submit_security_code. Does not wait for other calls, so it works while an activation is blocked on the dialog. The dialog must be visible.

_No parameters._

## ClientInfo

### `twincat_get_client_info`

Return this session's self-reported MCP client identity and tool profile. Client name, version, MCP protocol version, session id, tool profile and access policy. The identity is unauthenticated: diagnostics only, never for access control. If the session cannot be resolved, reports the most recently connected client and says so (may be stale).

_No parameters._

## Coe

### `twincat_read_coe_dictionary`

Read the CoE offline dictionary of an EtherCAT slave from its ESI file. Per object: index, name, access, objectType and sub-indexes (subindex, name, dataType, bitSize, defaultValue, defaultText for enums e.g. 'IIR level 1 (2)', access, isSetting). Offline: no active configuration or controller needed. Result: summary line + JSON { treePath, slaveType, fallbackNote, total, offset, returned, has_more, next_offset, objects[] }; total counts objects matching indexFilter.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the EtherCAT slave, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 2 (EL3174)'. From twincat_get_fieldbus_topology. |
| `indexFilter` | `string?` | `null` | Index '800D' or hex range '8000-80FF'. Omit for all objects. |
| `limit` | `int?` | `null` | Objects per page: default 100, max 1000 (0 = 1000). |
| `offset` | `int` | `0` | Objects to skip: next_offset of the previous call while has_more. Default 0. |

### `twincat_read_coe_startup`

Read the project's CoE Startup SDO overrides of an EtherCAT slave. These CoE values are written to the device on the PS / SafeOp→Op transition, i.e. where the project deviates from the ESI default. Empty list (not an error) when none. Read-only, CoE only (SoE/EoE/FoE and fixed entries: twincat_read_ethercat_startup). Result: summary line + JSON (entryCount, entries[]).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the EtherCAT slave, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 2 (EL3174)'. From twincat_get_fieldbus_topology. |

### `twincat_read_ethercat_startup`

Read the full EtherCAT Startup list of boxes, as the Startup tab shows it. Read-only and offline (no active configuration needed); needs an open solution. Covers CoE, SoE, EoE and FoE mailbox commands, both fixed (generated from the PDO/telegram configuration, in angle brackets) and projected; twincat_read_coe_startup sees only CoE and reports 0 entries for a SoE drive. ESC register commands (set sm ...) are not returned. Result: summary line + JSON { slaveCount, slavesScanned, entryCount, fixedCount, byProtocol, boxes[{ treePath, name, slaveType, physAddr, linkedAxis, soeChannels, virtualPort (EoE), entryCount, entries[] }] }. Entry: protocol, fixed, transitions[], comment, timeoutMs, dataHex; CoE adds index (hex), subIndex, completeAccess, source; SoE adds idn ('S-0-0015'), idnRaw, parameterSet, driveNo (0 = first drive), opCode, elements, attribute, value (unsigned little endian for 2/4/8 bytes), list {currentLength, maxLength} (a plain 4-byte value can look like an empty list), idnList (AT S-0-0016 / MDT S-0-0024).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string?` | `null` | Tree path of ONE EtherCAT box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Drive 1'. From twincat_get_fieldbus_topology. Omit for all boxes of all masters (only boxes with entries are listed). |
| `includeFixed` | `bool` | `true` | Include the TwinCAT-generated fixed entries. Default true; false = only what the project configures. |
| `protocol` | `string?` | `null` | Only CoE, SoE, EoE or FoE (case-insensitive). Omit for all. |

### `twincat_read_soe_parameter`

Read SoE parameters (IDNs) live from a SoE drive over ADS; never writes. For drives such as Beckhoff AX5000 or Rexroth IndraDrive. Needs the configuration active on the target and the slave at least in PREOP. With idns: reads those IDNs and elements. Without idns: reads every SoE entry of the box's Startup list (twincat_read_ethercat_startup) from the drive and compares (projected, actual, match; AT/MDT lists compared without max length; entries that do not write the value element get a note). Result: summary line + JSON { treePath, netId, port, mode (read | compare), parameters[{ idn, idnRaw, driveNo, name, unit, attribute {rawHex, ...}, value/min/max/default {hex, length, unsigned, signed + scaled (only with attribute), list, idnList, text}, dataStatus, errors[{element, code, name}], fixed, comment, projected, actual, match, note }] }. Scaling from the attribute is not yet verified against a drive: rawHex is authoritative. An ADS error of one IDN (e.g. DeviceInvalidOffset = not supported) goes into its errors; a drive that does not answer is an [Error] with NetId and port.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the SoE drive's EtherCAT box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Drive 1'. From twincat_get_fieldbus_topology. |
| `idns` | `string[]?` | `null` | IDNs 'S-0-0001', 'P-0-3231' or decimal, at most 256. Omit to compare the box's Startup SoE entries with the drive. |
| `driveNo` | `int?` | `null` | Drive within the box (0 = first; an AX5206 has 0 and 1). With idns default 0; without idns it filters Startup entries by DriveNo (default: each entry at its own DriveNo). |
| `elements` | `string[]?` | `null` | Elements per IDN (only with idns): value (default), name, attribute, unit, min, max, default, dataStatus. Add 'attribute' for decoded, scaled values. |
| `timeoutMs` | `int?` | `null` | ADS timeout per read in ms (default 5000, 100-30000); a timed-out read is retried once. |

## CoreDump

### `twincat_coredump_list`

List the TwinCAT PLC core dumps of a Usermode Runtime instance. File-based; needs no XAE or running runtime. Per dump: full path, write time, size, kind (AutoException = runtime trap, Manual = engineer-made, CallStackOnly = no variable values) and whether the .compileinfo needed to interpret it is present.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instanceName` | `string?` | `null` | UMR instance name, e.g. 'UmRT_Template'. Omit for all registered instances. |
| `port` | `int?` | `null` | ADS port to filter by, e.g. 851. Omit for all ports. |
| `projectFolder` | `string?` | `null` | Full path of the PLC project folder (containing the .plcproj). Required to find manual dumps and the _CompileInfo folder. |

### `twincat_coredump_analyze`

Analyze a TwinCAT PLC core dump file. Needs no running runtime. Classifies the dump kind; computes the hang gap (last app-log timestamp vs dump write time: a large gap means the PLC stalled, not trapped); extracts embedded state strings (app-log ring buffer, last state-machine steps); lists active unconfirmed events from LoggedEvents.db and CurrentEventMessages.xml.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dumpPath` | `string?` | `null` | Full path of the .core file. Omit to pick the newest dump (by instanceName/port). |
| `instanceName` | `string?` | `null` | UMR instance name for picking the newest dump. |
| `port` | `int?` | `null` | ADS port filter for picking the newest dump. |
| `projectFolder` | `string?` | `null` | Full path of the PLC project folder (with .plcproj, _CompileInfo, LoggedEvents.db, CurrentEventMessages.xml); needed for manual dumps and event data. |

### `twincat_coredump_read_callstack`

Read the call stack of the core dump loaded in the XAE (best effort). Returns POU and instance path per frame; the top-most is the exception location. FRAGILE: the dump must already be loaded by hand (PLC > Core Dump > Load core dump); this tool cannot load it. Returns an error when no dump is loaded or the XAE is unreachable; never invents a stack.

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
| `treePath` | `string` |  | Caret-separated tree path of the POU to search, as used by twincat_plc_read. |
| `step` | `int` |  | Numeric step value to locate and show (as read live via ADS). |
| `stepVariable` | `string?` | `null` | Name of the CASE selector variable. Defaults to 'Step' when omitted. |
| `bringToFront` | `bool` | `false` |  |

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

### `ptc_test_hold_xae_dialog`

TEST-ONLY hook for the live suite: opens the XAE's Options dialog (Tools > Options) inside a COM call that returns only once the dialog is closed, so the call is held by an XAE dialog — it answers [Blocked] after a few seconds while the dialog stays open. Close the dialog with twincat_xae_dialog_answer ('Abbrechen'/'Cancel'). confirm must be 'open'. Only registered when the host is started with --diagnostics / PTC_XAE_MCP_DIAGNOSTICS=1.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `confirm` | `string` | `""` | Must be 'open': guards against an accidental call. |

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

## Doctor

### `twincat_doctor`

Diagnose all tool prerequisites in one call: ok / warn / fail plus next step. Covers server version, TwinCAT System Service, local AMS NetId, TcXaeShell instances, a dialog blocking the XAE, the open solution and the roots file-path parameters may use. Never starts an XAE or runtime; an XAE silent for 5 s is reported, not awaited. Use it first when tools fail for unclear reasons.

_No parameters._

## EditorNavigation

### `twincat_locate_step`

HEADLESS: find the CASE branch of a step in an ST POU's step chain. Looks for 'CASE <stepVariable> OF … 1010: …'; only numeric literal labels match ('1010:' or '100, 1110:'). Ranges, constants and enum labels are listed in the error but never matched — then read the POU and call twincat_goto_line yourself. Returns the 1-based implementation line (the pane's gutter number, not the .TcPOU file line), the label, the branch end line and the branch source. Does not touch the editor.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU, as for twincat_plc_read. |
| `step` | `int` |  | Numeric step value, e.g. read via ADS from MAIN.fbMachine.Step. |
| `stepVariable` | `string?` | `null` | CASE selector variable. Default 'Step'. |

### `twincat_goto_line`

VISIBLE: put the XAE editor cursor on a line of a POU. Opens the object's tab (as twincat_open_object) and answers the 'Go To Line' dialog. Lines count as in the twincat_plc_read text of that section. Works while Online (no logout), but Online the declaration pane is the monitoring grid — use the implementation then. Confirmed structurally (dialog seen and closed), no caret read-back. Needs the XAE in the foreground: without bringToFront=true it is an [Error] unless the XAE already is in front.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path of the POU, as for twincat_plc_read. |
| `line` | `int` |  | 1-based line within the section's pane. |
| `section` | `string?` | `null` | 'implementation' (default) or 'declaration'. |
| `bringToFront` | `bool` | `false` |  |

## EtherCat

### `twincat_add_ethercat_device`

Create a top-level EtherCAT device (master or slave) under TIID. Needs an open solution; saves the project. Returns the new device tree path, the parent for twincat_add_ethercat_box.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Device name under TIID, e.g. 'Machine (EtherCAT)'. |
| `deviceKind` | `string` | `"master"` | 'master' (default) or 'slave'. |

### `twincat_add_ethercat_box`

Add an EtherCAT box/terminal, or a whole subtree, under a TIID node. Same engine as twincat_apply_schema. Needs an open solution; saves the project. Pass EITHER vInfo (one box) OR boxesJson (a tree).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `parentPath` | `string` |  | Parent tree path, e.g. 'TIID^Device 1 (EtherCAT)' or 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. From twincat_get_fieldbus_topology; the bare root 'TIID' is rejected. |
| `vInfo` | `string?` | `null` | Single box: type, bare 'EL9011' (latest installed revision) or revisioned 'AX5206-0000-0202'; other vendors as 'V<vendorId>_P<productCode>_R<revision>' (8 hex digits each; a bare third-party ESI Type is translated). See twincat_list_ethercat_box_catalog. Excludes boxesJson. |
| `name` | `string?` | `null` | Single box: designation, e.g. 'A1.49 (EL9011)'. Default: vInfo. |
| `previousPort` | `string?` | `null` | Single box: upstream port letter A/B/C/D. |
| `beforeSibling` | `string?` | `null` | Name of an existing sibling to insert before. Default: append at the end. |
| `boxesJson` | `string?` | `null` | Subtree mode: apply_schema ethercat node JSON {name, vInfo, previousPort, children[]} as one object, an array of siblings, a {root:{…}} fragment or a full schema file. Excludes vInfo. |

### `twincat_list_ethercat_box_catalog`

List installable EtherCAT box types and their vInfo from the local ESI files. vInfo is the input of twincat_add_ethercat_box. Scans <TwinCAT>\3.1\Config\Io\EtherCAT offline: no XAE, solution or hardware needed. Per device: vInfo (revision-exact, e.g. EL1008-0000-0017; other vendors V<vendorId>_P<productCode>_R<revision>, e.g. V00000059_P00000013_R00000002), ESI Type, name, productCode, revisionNo, vendor, source file. Malformed ESI files are skipped and counted. The first scan can take seconds; cached until an ESI file changes. Result: summary line + JSON { esiDirectory, filter, latestRevisionOnly, filesScanned, filesSkipped, nonEsiFiles, devicesIndexed, cached, warnings, total, offset, returned, has_more, next_offset, devices[] }; total counts every match.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filter` | `string?` | `null` | Case-insensitive substring of ESI Type/vInfo, e.g. 'EL1008' or 'AX52'. Strongly recommended: without it several thousand types are paged. |
| `limit` | `int?` | `null` | Devices per page: default 100, max 1000 (0 = 1000). |
| `offset` | `int` | `0` | Devices to skip: next_offset of the previous call while has_more. Default 0. |
| `latestRevisionOnly` | `bool` | `true` | true (default): newest revision per product only; false: every revision. |

### `twincat_remove_ethercat_box`

Delete an EtherCAT box/terminal by tree path and save the project. Needs an open solution. The root 'TIID' and top-level nodes without a parent are rejected.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the box, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)^Term 4 (EL2004)'. From twincat_get_fieldbus_topology. |

### `twincat_reload_device_descriptions`

Make the XAE re-read the EtherCAT device description (ESI) files. Runs 'TwinCAT > EtherCAT Devices > Reload Device Descriptions' (any XAE language). Run it after placing a vendor ESI under <TwinCAT>\3.1\Config\Io\EtherCAT; until then twincat_add_ethercat_box fails for that type with 'Invalid item sub type'. Needs an open solution; changes nothing in the project. A dialog the XAE shows is reported, never answered.

_No parameters._

### `twincat_set_previous_port`

Set an EtherCAT box's upstream PreviousPort (A/B/C/D) and save the project. Needs an open solution. Attaches a box to a specific port of a multi-port coupler (e.g. EK1122) or repairs a box added without a port.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the box, e.g. 'TIID^Device 1 (EtherCAT)^Box 2 (EK1122)^Box 3 (EK1100)'. |
| `port` | `string` |  | Upstream port: A, B, C or D. |

## EventClasses

### `twincat_event_class_create`

Create an EventLogger event class in the project (.tsproj) type system. Saved as persistent (kept although nothing references it); the PLC reaches each event as TC_EVENTS.<class>.<event> at once. The GUID is generated. Refused with [Error] before anything changes: a non-IEC class or event name, no events, an id outside 0..4294967295, a duplicate id or name, an unknown severity, an existing class of that name. The result shows the class as read back. A PLC library carries the class only after twincat_plc_add_global_data_type.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name of the event class (IEC identifier), e.g. 'MyLibEvents'. |
| `events` | `EventClassEventInput[]` |  | The events [{id, name, severity, text}], at least one. |
| `displayText` | `string?` | `null` | Display text of the class. Default: its name. |

### `twincat_event_class_read`

Read the EventLogger event classes of the type system as JSON. Without name: every project class (name, GUID, display text, origin .tsproj or TMC file, event count, PLC projects pinning it via ExternalTypes.tmc). With name: that class with all events (id, name, severity, text) and, per pinning PLC project, whether its ExternalTypes.tmc copy is up to date.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string?` | `null` | Name of one event class. Omit for the list. |
| `includeBuiltIn` | `bool` | `false` | Also list built-in TwinCAT classes (TcSystemEventClass …). Default false. |

### `twincat_event_class_update`

Change an event class of the project: display text and/or event list. Only classes kept in the .tsproj. Logs out of the PLC first. The GUID stays, the PLC's TC_EVENTS follows at once and the project is saved. Events are matched by id: unmanaged details (cause/remedy, description) stay with a kept id; ids missing from the new list are removed. Pins in PLC projects (ExternalTypes.tmc) are rewritten with the new content while the solution is saved, closed and reopened. Refused with [Error] before anything changes: invalid arguments as in twincat_event_class_create, an unknown or built-in class, a class from a TMC file (change it in that file).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name of the event class to change. |
| `events` | `EventClassEventInput[]?` | `null` | The complete new event list [{id, name, severity, text}], replacing the old one. Omit to keep the events. |
| `displayText` | `string?` | `null` | New display text of the class. Omit to keep it. |

### `twincat_plc_add_global_data_type`

Pin a project data type or event class into a PLC project. Same as the XAE command "Add Global Data Type" for STRUCT, ENUM, ALIAS or event classes kept in the .tsproj: a library saved from the PLC then carries the type (for an event class, its users get TC_EVENTS.<class>). Project types it uses (members, base types, nested) are taken along; built-in types and types from a library, TMC file or PLC are not (the result warns). Logs out of the PLC first, then saves and closes the solution, writes ExternalTypes.tmc next to the .plcproj plus its .plcproj entry, and reopens it. Result: each pinned type's ExternalTypes.tmc node read back. Out-of-date pins are refreshed; if all are current nothing changes. Refused with [Error] before anything changes: an unknown, built-in or non-project type, an unknown PLC project, several PLC projects without plcProjectName, or a name collision (a DUT, POU or interface of that name in the PLC, or another type of that name or GUID in its ExternalTypes.tmc).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `typeName` | `string` |  | Name of the data type or event class in the type system, e.g. 'ST_AxisData' or 'MyLibEvents'. |
| `plcProjectName` | `string?` | `null` | The PLC by its name under PLC or its .plcproj name. Optional when the solution has exactly one PLC project. |

## EventLogger

### `twincat_read_event_logger`

Read the TwinCAT runtime EventLogger: logged events and active alarms. Not the Visual Studio Error List (twincat_get_error_list / twincat_clear_error_list). Needs the runtime reachable on the target. Per entry: message text, severity, source, timestamps, alarm/confirmation state. Returns JSON { success, loggedCount, activeAlarmCount, message, total, offset, returned, has_more, next_offset, events[], activeAlarms[] } or an [Error]. events: newest first, paged, at most the newest 10000 searched; total is null while the logger holds more than were read; loggedCount = events read for this page. activeAlarms is always complete.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `limit` | `int?` | `null` | Logged events per page: default 100, max 1000 (0 = 1000). |
| `offset` | `int` | `0` | Logged events to skip, newest first: next_offset of the previous call while has_more. Default 0. |
| `minSeverity` | `string?` | `null` | Minimum severity: Verbose \| Info \| Warning \| Error \| Critical. Omit for all. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target runtime. Omit/empty = local runtime (single-XAE constraint: local only). |

### `twincat_confirm_event_logger`

Acknowledge or clear alarms/events of the TwinCAT runtime EventLogger. Needs the runtime reachable. Checked by the active-alarm count before/after. Returns JSON { success, mode, activeAlarmsBefore, activeAlarmsAfter, message } or an [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` |  | 'confirm' acknowledges alarms awaiting confirmation; 'clear-alarms' clears raised alarms and resets confirmation; 'clear-events' empties the logged-event cache; 'all' does all three. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target runtime. Omit/empty = local runtime. |

## Fieldbus

### `twincat_get_fieldbus_topology`

Return the I/O (TIID) tree as JSON: devices, boxes, slaves, channels. Read-only; needs an open solution. Result: summary line + JSON (deviceCount, devices[]).

_No parameters._

### `twincat_get_device_xml`

Return the raw device-configuration XML of one I/O (TIID) tree node. Read-only; needs an open solution. This is the XML twincat_configure_ethercat_slave accepts.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path below TIID, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'; the root 'TIID' itself is rejected. From twincat_get_fieldbus_topology. |

### `twincat_configure_ethercat_slave`

Write device-configuration XML to an EtherCAT slave and verify by read-back. Every written leaf value is checked; values the XAE ignored or changed are returned as [Error]. Side effect: saves ALL dirty documents of the solution (File > Save All), not only this node. Needs an open solution. Get the current XML with twincat_get_device_xml, modify it, pass it here.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the slave below TIID (not the root), e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1100)'. |
| `xml` | `string` |  | Full device-configuration XML, as returned by twincat_get_device_xml, with your changes. |

## Hmi

### `twincat_hmi_list_projects`

List the HMI (TE2000) projects of the open solution with their state. Also reports an open 'Activate Engineering Server Trial License' window: a CAPTCHA only a person can answer; the HMI server does not work until then.

_No parameters._

### `twincat_hmi_get_project_info`

Show config, server extensions and file tree of an HMI project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `includeFiles` | `bool` | `false` | Also return the project tree (max 400 nodes). |
| `maxDepth` | `int` | `2` | Tree depth 1..6. |

### `twincat_hmi_list_mapped_symbols`

List the symbols mapped in an HMI project's server. CAUTION: cached; maps/unmaps after the project was loaded are not shown, even with refresh. For the live state of one symbol use twincat_hmi_read_symbols. While has_more, call again with offset=next_offset.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `domain` | `string?` | `null` | Only this domain, e.g. 'ADS'. |
| `namePrefix` | `string?` | `null` | Only mapped names starting with this, e.g. 'ADS.PLC1.GVL_HMI.'. |
| `includeHidden` | `bool` | `false` | Include hidden symbols. |
| `refresh` | `bool` | `false` | Refresh the server's symbol list first. |
| `limit` | `int?` | `null` | Page size, default 500, max 5000. |
| `offset` | `int` | `0` | Symbols to skip (next_offset). |

### `twincat_hmi_read_symbols`

Read symbol values through the HMI server (what the HMI shows), max 200. A failing symbol gets ok=false and the server error (SYMBOL_NOT_MAPPED; HMI_ADS_E_SYNC_TIMEOUT = PLC unreachable). Values as the server reports them (a BOOL reads 'False').

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbols` | `string[]` |  | HMI symbol names, e.g. ['ADS.PLC1.GVL_HMI.bOpen']. |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_server_control`

Start, shut down or refresh symbols of an HMI engineering server. That is the XAE's local TcHmiSrv, not a TF2000 runtime. 'start' after a shutdown/crash, 'refresh_symbols' after PLC symbol changes. Changes no project file.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | start \| shutdown \| refresh_symbols |
| `hmiProject` | `string?` | `null` |  |
| `timeoutMs` | `int` | `30000` | Max wait for the server to settle, 0..120000 ms. |

### `twincat_hmi_map_symbol`

Map a PLC symbol into an HMI project's server so views can bind to it.  plcPath 'MAIN.x' becomes 'ADS.PLC1.MAIN.x' (internal 'PLC1::MAIN::x'); for arrays or other domains pass mapName + internalName. Refuses an already mapped name. Needs the PLC reachable.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcPath` | `string?` | `null` | Dotted PLC path, e.g. 'GVL_HMI.bOpen'. Or mapName + internalName. |
| `mapName` | `string?` | `null` | Mapped name, e.g. 'ADS.PLC1.GVL_HMI.bOpen'. |
| `internalName` | `string?` | `null` | Internal name, e.g. 'PLC1::GVL_HMI::bOpen'. |
| `domain` | `string?` | `null` | Server domain. Default 'ADS'. |
| `runtime` | `string?` | `null` | ADS runtime for plcPath. Default 'PLC1'. |
| `subSymbolName` | `string?` | `null` | Sub-symbol. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_unmap_symbol`

Remove a symbol mapping from an HMI project's server.  Not mapped = 'Nothing to do'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mapName` | `string?` | `null` | Mapped name. Or plcPath. |
| `plcPath` | `string?` | `null` | Dotted PLC path (named as in twincat_hmi_map_symbol). Or mapName. |
| `domain` | `string?` | `null` | Server domain. Default 'ADS'. |
| `runtime` | `string?` | `null` | ADS runtime for plcPath. Default 'PLC1'. |
| `removeHistorizedData` | `bool` | `false` | Also delete its historized data. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_write_symbol`

Write a symbol value through an HMI project's server. An ADS symbol ('ADS.PLC1.…') WRITES TO THE LIVE PLC, like twincat_write_ads_variable.  Refuses unmapped and read-only symbols. Read back; a difference sets verified=false (the PLC may change it at once).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | HMI symbol name, e.g. 'ADS.PLC1.MAIN.bStart'. |
| `valueJson` | `string` |  | Value as JSON: true, 42, "text", or an object/array. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiAuthoring

### `twincat_hmi_list_components`

List the pages, user controls, functions and languages of an HMI project. Read from the saved files: views, contents, user controls WITH their parameters (propertyName, attribute, type, required), functions with arguments, languages, themes, and which page uses which user control/content/function. Use it before twincat_hmi_place_user_control.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_check_bindings`

Check that every binding and reference in HMI pages resolves. Saved files: %s% server symbols against the mapped symbols, %i% internal symbols, %pp% user-control parameters, %ctrl% control ids, %f%/trigger functions, %l% texts in every language, user-control hosts (target exists, required parameters), region targets, duplicate ids. Findings with file, line, severity (error/warning/info) and a 'did you mean'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `paths` | `string[]?` | `null` | Pages to check, project-relative (.view/.content/.usercontrol). Default: all. |
| `includeInfo` | `bool` | `false` | Also list info findings (framework functions and domain symbols that are not checked). |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_list_localization`

List the localized texts of an HMI project and what is missing. Saved files: languages with their texts, keys missing per language, keys used (%l%Key%/l%) but not defined, keys defined but unused.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_set_localized_texts`

Set or remove localized texts in existing languages. One confirmation for all: { "de": { "Start": "Start", "Old": null }, "en": { "Start": "Start" } } (null removes). Existing keys keep their place, new keys are appended. Use in a page as %l%Key%/l%.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `texts` | `string` |  | JSON object: locale → { key → text \| null }. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_place_user_control`

Place a project user control on a view or content. Adds a TcHmiUserControlHost as last child of the page's root (or replaces the host with the same id when replace=true). parameters by propertyName from twincat_hmi_list_components, e.g. { "Safety": "%s%PLC1.GVL.Safety%/s%", "Window": 40 }; objects/arrays are written as JSON. Unknown or missing required parameters are refused. Size defaults to the control's design size.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `page` | `string` |  | Target page, project-relative, e.g. 'Desktop.view' or 'Contents/Main.content'. |
| `userControl` | `string` |  | The user control, e.g. 'UserControls/X/X.usercontrol'. |
| `id` | `string` |  | Control id of the host, unique in the page. |
| `left` | `int` | `0` | Left in px. |
| `top` | `int` | `0` | Top in px. |
| `width` | `int?` | `null` | Width; default: design width of the user control. |
| `height` | `int?` | `null` | Height; default: design height of the user control. |
| `widthUnit` | `string?` | `null` | 'px' (default) or '%'. |
| `heightUnit` | `string?` | `null` | 'px' (default) or '%'. |
| `parameters` | `string?` | `null` | JSON object propertyName → value (string, number, bool, object, array; a binding as string). |
| `replace` | `bool` | `false` | Replace the existing host with this id instead of refusing. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_check_liveview`

Load the HMI live view in a headless browser and report what broke. Reports JS exceptions, console errors/warnings, failed requests, whether the framework shows a view, and the rendered controls (count, controls without size). Optional: switch to another view, take a PNG screenshot, evaluate a JavaScript expression after load (result as JSON; may write symbols through the server, so not read-only). Run twincat_hmi_build first. Needs Edge, Chrome or a Playwright Chromium.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `url` | `string?` | `null` | Live-view URL. Default: http://127.0.0.1:<HmiCommunicationServerPort>/<newest liveview_*.html> of the project. |
| `view` | `string?` | `null` | View to load after start, project-relative, e.g. 'Views/Test.view'. Default: the startup view. |
| `waitMs` | `int` | `HmiLiveViewCheck.DefaultWaitMs` | Max wait for the framework and view in ms, 1000-120000. Default 30000. |
| `width` | `int` | `1280` | Viewport width in px. Default 1280. |
| `height` | `int` | `800` | Viewport height in px. Default 800. |
| `screenshotPath` | `string?` | `null` | Absolute path of a PNG screenshot to write. |
| `expression` | `string?` | `null` | JavaScript evaluated after load (promises awaited); its value is returned as JSON. |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_describe_controls`

List the controls of the project's HMI packages, or one control's attributes. Read from each control's Description.json in the restored NuGet packages (framework and Beckhoff controls): type, container or not, design size; with type all attributes incl. the base chain (attribute name, propertyName, type, bindable, read-only, declaring control). Use it before twincat_hmi_place_control.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `type` | `string?` | `null` | Control type, full (TcHmi.Controls.Beckhoff.TcHmiButton) or short (TcHmiButton). Default: list all controls. |
| `filter` | `string?` | `null` | Substring filter: on control names (list) or attribute name/propertyName/category (with type). |
| `includeHidden` | `bool` | `false` | Also list controls/attributes the designer hides. |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_place_control`

Place any control of the project's packages on a view, content or user control. Adds it as last child of the page root or of parentId (must be a container: TcHmiContainer, TcHmiGrid, …), or replaces the control with the same id and type. attributes by propertyName, displayName or attribute name, checked against the control's description: unknown, read-only, layout ones and bindings on non-bindable ones are refused; objects/arrays as JSON. E.g. a region: type TcHmiRegion, { "TargetContent": "Contents/A.content" }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `page` | `string` |  | Target page, project-relative, e.g. 'Desktop.view' or 'Contents/Main.content'. |
| `type` | `string` |  | Control type, full or short, e.g. 'TcHmiButton', 'TcHmi.Controls.System.TcHmiRegion'. |
| `id` | `string` |  | Control id, unique in the page. |
| `parentId` | `string?` | `null` | Id of the container control to place into. Default: the page's root control. |
| `left` | `int` | `0` | Left in px. |
| `top` | `int` | `0` | Top in px. |
| `width` | `int?` | `null` | Width; default: the control's design width. |
| `height` | `int?` | `null` | Height; default: the control's design height. |
| `widthUnit` | `string?` | `null` | 'px' (default) or '%'. |
| `heightUnit` | `string?` | `null` | 'px' (default) or '%'. |
| `attributes` | `string?` | `null` | JSON object attribute → value, e.g. { "Text": "Start", "StateSymbol": "%s%PLC1.MAIN.bStart%/s%" }. |
| `replace` | `bool` | `false` | Replace the existing control with this id (same type) instead of refusing. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_check_access`

Check that the user groups may read and write the symbols the HMI pages bind. Saved files: every %s% use against the group rights of the server configuration (SYMBOLACCESS default, explicit symbol entries). A symbol a page writes (TwoWay binding, state symbol, WriteToSymbol action) needs write access. In the engineering server everything runs as administrator; on a TF2000 runtime a missing right leaves the control empty or drops the write. Default groups: enabled groups with users, except administrators and guests. With endpoint the rights come live from that TF2000 server (login: user + NAME of an environment variable holding the password).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `groups` | `string[]?` | `null` | User groups to check, e.g. ['Operators']. Default: every enabled group with users except __SystemAdministrators/__SystemGuests. |
| `paths` | `string[]?` | `null` | Pages to check, project-relative (.view/.content/.usercontrol). Default: all. |
| `hmiProject` | `string?` | `null` |  |
| `endpoint` | `string?` | `null` | TF2000 server URL to take the rights from live, e.g. https://192.168.1.10:2020. Default: the project's saved configuration. |
| `user` | `string?` | `null` | User who may read the server configuration (e.g. an administrator); with endpoint. |
| `passwordVariable` | `string?` | `null` | Name of an environment variable of the MCP host that holds the password (the password itself is never a parameter). |
| `trustServerCertificate` | `bool` | `false` | Accept a self-signed server certificate (https). Default false. |

### `twincat_hmi_server_diagnostics`

Diagnose a TF2000 or engineering HMI server through its WebSocket API. Reports version, license state with used clients/targets, extensions and their state, outdated symbols, sessions, event-log counts per severity, active alarms and the newest events. Default target: the engineering server of the HMI project. For a runtime pass endpoint (e.g. https://192.168.1.10:2020) and, if it requires login, user plus the NAME of an environment variable of the MCP host holding the password.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `endpoint` | `string?` | `null` | Server URL, e.g. https://192.168.1.10:2020. Default: http://127.0.0.1:<HmiCommunicationServerPort> of the HMI project. |
| `user` | `string?` | `null` | User to log in as. Default: no login (local engineering server). |
| `passwordVariable` | `string?` | `null` | Name of an environment variable of the MCP host that holds the password (the password itself is never a parameter). |
| `trustServerCertificate` | `bool` | `false` | Accept a self-signed server certificate (https/wss). Default false. |
| `minSeverity` | `string?` | `null` | Lowest event severity to list: verbose, info, warning (default), error, critical. |
| `maxEvents` | `int` | `20` | Max events to list, 0-200. Default 20. |
| `timeoutMs` | `int` | `15000` | Timeout in ms, 1000-60000. Default 15000. |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_publish`

Publish the HMI project to a TF2000 server with a saved publish profile. It replaces the project there. The profile, created once in the XAE, holds target, user and the password (the XAE keeps it encrypted; the MCP never passes one). Reads the target before and after: success only when it then reports this project. user + passwordVariable (NAME of an environment variable of the MCP host) are only for these reads.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `profile` | `string` |  | Name of a publish profile saved in the project (create it once in the XAE's Publish dialog, password saved). |
| `targetUrl` | `string?` | `null` | Optional check: the TF2000 server URL the profile must point at, e.g. https://192.168.1.10:2020. |
| `user` | `string?` | `null` | User for reading the target before and after (default: the profile's user). |
| `passwordVariable` | `string?` | `null` | Name of an environment variable of the MCP host holding that user's password (the password itself is never a parameter). |
| `trustServerCertificate` | `bool` | `false` | Accept a self-signed certificate for the reads before and after (https). Default false. |
| `timeoutSeconds` | `int` | `300` | Max seconds to wait for the publish, 30-1800. Default 300. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `120` |  |

### `twincat_hmi_scaffold_symbol_view`

Generate a .content with one row per field of a server symbol. Label + checkbox (BOOL), numeric input (number), textbox (string), or read-only textblocks; nested structs get a header. The shape comes from the symbol's current value read through the HMI server, or from sampleValue. An array of structs becomes a datagrid (one column per simple field, edits written back); other arrays are listed as skipped. dryRun returns the markup without writing.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | Server symbol, e.g. 'PLC1.MAIN.stAxis' (mapped name, without %s%). |
| `path` | `string` |  | Target .content, project-relative, e.g. 'Contents/Axis.content'. |
| `readOnly` | `bool` | `false` | Only display values (textblocks, disabled checkboxes). |
| `maxDepth` | `int` | `3` | Struct nesting depth, 1-6. Default 3. |
| `sampleValue` | `string?` | `null` | JSON value of the symbol's shape, used instead of reading the server. |
| `dryRun` | `bool` | `false` | Return the markup only, write nothing. |
| `overwrite` | `bool` | `false` | Replace an existing file. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiInternalSymbols

### `twincat_hmi_list_internal_symbols`

List the HMI-owned symbols (no PLC behind them) of an HMI project. Read from its files, no server needed: client internal symbols %i%Name%/i% and TcHmiSrv server symbols.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_add_internal_symbol`

Add a client internal symbol %i%name%/i% to an HMI project. For HMI-side state (selection, page index) without a PLC variable.  Refuses an existing name. For a symbol the server holds for all clients use twincat_hmi_map_offline_symbol.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Letters, digits, '_', max 128; not starting with a digit. |
| `dataType` | `string` |  |  |
| `defaultValueJson` | `string?` | `null` |  |
| `persist` | `bool` | `false` | Persist the value in the client. |
| `readOnly` | `bool` | `false` | Read-only for bindings. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_remove_internal_symbol`

Remove a client internal symbol %i%name%/i% from an HMI project. Bindings to it stop resolving.  Unknown name = 'Nothing to do'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name as twincat_hmi_list_internal_symbols reports it (case-sensitive). |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_map_offline_symbol`

Create a server symbol (TcHmiSrv domain) without a PLC behind it. The HMI server holds it for all clients; bind to it like any mapped symbol. Needs the HMI server running.  Refuses a known name. Remove with twincat_hmi_unmap_symbol (domain 'TcHmiSrv').

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mapName` | `string` |  | Dotted identifiers, e.g. 'Recipe.SelectedIndex', max 256. |
| `dataType` | `string` |  |  |
| `defaultValueJson` | `string?` | `null` |  |
| `online` | `bool` | `false` | Sets USEMAPPING; false = pure server variable (true is unverified, prefer false). |
| `persist` | `bool` | `false` | Server keeps the last value across restarts. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiTmc

### `twincat_hmi_list_tmc_files`

List the TMC files attached to an HMI project (offline PLC symbols). Also lists the ADS runtimes that serve symbols from a TMC file (see twincat_hmi_enable_runtime_tmc).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_list_ads_runtimes`

List the ADS runtimes (NetId, port, symbols, useTmc) of an HMI project's server. Tells 'PLC unreachable' apart from 'needs no PLC'. Switch one to TMC with twincat_hmi_enable_runtime_tmc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_list_plc_tmc_candidates`

List the TMC file of every PLC project in the open solution. These are the candidates for twincat_hmi_attach_tmc_file. Needs no HMI project. Does not choose the PLC for the HMI: pass one absolutePath as tmcFile.

_No parameters._

### `twincat_hmi_list_project_datatypes`

List the project datatypes of an HMI project (tchmi.project.Schema.json). Per definition: name, kind (userControl/struct/enum/array/alias/other), namespace and where it comes from (attachedTmc/adsRuntime), $ref targets and dangling $refs (a reference to a project type that no longer exists). Reports how tchmi.project.Schema.tpl.json differs. A user-control parameter references a type as tchmi:project#/definitions/<name>.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_create_project_datatype`

Create an HMI project datatype from a PLC STRUCT or ENUM of an attached TMC. Like TE2000 'Create new project datatype → based on a remote type': '<virtualPlc>.<Type>' plus its sub-types (enum fields, nested structs, arrays as '<virtualPlc>.ARRAY_1..n_OF-<Type>' with startOffset) in Properties/tchmi.project.Schema.json (and .tpl.json if present), then asks the HMI server to refresh its project definitions.  Refuses a type that already exists (also under another name) and a sub-type that exists with different content; identical sub-types are reused. Use the result as user-control parameter type 'tchmi:project#/definitions/<name>'. Check with twincat_hmi_list_project_datatypes.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `remoteType` | `string` |  | '<virtualPlc>.<Type>', e.g. 'PLC_TMC.ST_Recipe': the virtual PLC name of an attached TMC file (twincat_hmi_list_tmc_files) and a STRUCT or ENUM declared in that PLC. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_switch_mapping_to_plc`

Move HMI mappings from the engineering-only TMC domain to a real ADS runtime. Like TE2000 'Switch Mapping to PLC': same mapped name, DOMAIN=ADS, internal path re-rooted at the runtime, so bindings keep working. Default: all TMC mappings.  Each mapping is read through the server afterwards; when the PLC does not know the symbol the old entry is written back and the mapping is reported as not switched. A runtime still in TMC mode cannot tell (state switchedUnverified). Mappings made with twincat_hmi_enable_runtime_tmc are ADS already — use twincat_hmi_disable_runtime_tmc for those.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `runtime` | `string` |  |  |
| `mapNames` | `string[]?` | `null` | Mapped names to switch (default: every mapping in the TMC domain, at most 200). |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_attach_tmc_file`

Attach a PLC project's TMC file to an HMI project for offline symbols. Like TE2000 'Attach TMC file', its symbols become bindable in the engineering without a PLC.  Refuses a file or virtualPlcName already attached. Undo with twincat_hmi_detach_tmc_file.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmcFile` | `string` |  |  |
| `virtualPlcName` | `string?` | `null` | Letters, digits, '_' or '-', max 64. Default: the .tmc file name. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_detach_tmc_file`

Detach a TMC file from an HMI project. Its symbols are then no longer known offline.  Not attached = 'Nothing to do'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `virtualPlcName` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_enable_runtime_tmc`

Serve an HMI ADS runtime's symbols from a TMC file instead of a PLC (USE_TMC). Mapping, reading and binding then work without any PLC (twincat_hmi_attach_tmc_file only feeds the engineering). Converts the .tmc into a .bin in the engineering server's folder and sets USE_TMC=true.  Whether symbols are served is not checked: read one with twincat_hmi_read_symbols. Undo with twincat_hmi_disable_runtime_tmc. Values need the PLC (HMI_ADS_E_USING_TMC until then).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `runtime` | `string` |  |  |
| `tmcFile` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_disable_runtime_tmc`

Switch an HMI ADS runtime from a TMC file back to the PLC (USE_TMC=false). Without a reachable PLC it then has no symbols.  No TMC in use = 'Nothing to do'. The .bin stays on disk. Mappings made in TMC mode keep their names.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `runtime` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HmiViews

### `twincat_hmi_read_file`

Read the source text of a file in an HMI project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |

### `twincat_hmi_add_item`

Create a file in an HMI project from a TE2000 item template. Refuses an existing file. kind: view, content, usercontrol, function_js, function_ts, codebehind_js, codebehind_ts, css, html, js, json, ts, text, xml (usercontrol and function_* also create their .json).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` |  | Template kind (see the tool description). |
| `folder` | `string?` |  | Target folder, project-relative, e.g. 'UserControls/Foo'; empty = project root. |
| `name` | `string` |  | File name without extension. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_write_file`

Replace the source text of an existing file in an HMI project. Identical content = 'Nothing to do'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `source` | `string?` | `null` | Complete new content. Or sourceFile. |
| `sourceFile` | `string?` | `null` | Absolute path of a local file (max 5 MB) to write. Or source. |
| `keepSchema` | `bool` | `true` | Keep a JSON file's existing "$schema". |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_import_file`

Import an external file (HTML, image, PDF, JS library) into an HMI project. Refuses an existing target. For .js/.css run twincat_hmi_build afterwards so the live view loads it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `sourcePath` | `string` |  | Absolute path of the file to import. |
| `folder` | `string?` | `null` | Target folder, project-relative; empty = project root. |
| `name` | `string?` | `null` | Target file name. Default: the source name. |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_build`

Build an HMI project so changes reach the server's live view. Needed for new user controls, functions, contents. On failure see twincat_get_error_list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `hmiProject` | `string?` | `null` |  |
| `configuration` | `string?` | `null` | Solution configuration. Default 'Debug'. |

### `twincat_hmi_apply_bundle`

Write a package of HMI files from a manifest with one confirmation. Manifest: { "name": "…", "entries": [ { "path": "UserControls/X/X.usercontrol", "kind": "usercontrol", "source": "X.usercontrol" } ] }; 'source' is relative to the manifest. Existing files are replaced, missing ones created from 'kind' (not needed for a file an earlier entry's template creates). Validated before the popup; stops at the first error.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `manifestPath` | `string` |  | Absolute path of the bundle manifest (JSON). |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

### `twincat_hmi_delete_item`

Delete a file or folder of an HMI project from project AND disk. No recycle bin. Refuses project infrastructure (Server, Properties, Packages) and the startup view.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `hmiProject` | `string?` | `null` |  |
| `confirmTimeoutSeconds` | `int` | `HmiCoordinator.DefaultConfirmTimeoutSeconds` |  |

## HostSelection

### `twincat_resolve_host`

Resolve which XAE host (32- or 64-bit) a project's TcVersion selects. 3.1.4026 and later select the 64-bit XAE, older ones the 32-bit. Reports TcVersion, host class, ProgID, exe path, whether it is installed and running. Opens and changes nothing.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tsprojOrSlnPath` | `string?` | `null` | Absolute path to a .tsproj or .sln. Default: the open solution. |

## IbnLive

### `twincat_ibn_disarm`

Close the IBN control window and command the test PRG home. Stops the heartbeat; disarm is a watchdog trigger, so the machine returns to its home position. Always allowed. Arming happens only in the host window (Approvals view); no MCP tool arms. Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

### `twincat_ibn_propose`

Propose a step trigger for a human to confirm; moves NOTHING by itself. Creates a PENDING proposal, opens the host's modal confirm popup and returns JSON { proposalId, status: "pending" }. Executed ONLY if a human confirms the popup; no AI path executes it. Needs an active arm window, opened only by a human in the host window (Approvals view; no MCP tool arms), else [Error]. Poll twincat_ibn_get_proposal_status for the outcome.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | Step action: 'start_chain' \| 'next_step' \| 'hold'. |
| `chainId` | `string?` | `null` | Id of the test chain this trigger targets. |
| `actuatorId` | `string?` | `null` | Actuator id; must be on the allowlist the operator armed. |

### `twincat_ibn_get_proposal_status`

Poll the status of an IBN proposal; read-only, moves nothing. Returns JSON { proposalId, status, action, chainId, actuatorId, createdUtc, expiresUtc, result }; status: pending | confirmed | executed | rejected | expired.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `proposalId` | `string` |  | The proposalId returned by twincat_ibn_propose. |

### `twincat_list_active_chains`

Read a snapshot of the running IBN test chains; moves nothing. Reads the test PRG status struct at the gvlPath/target armed in the host window unless overridden. Returns JSON { success, gvlPath, chain: { running, activeChain, currentStep, fault }, raw: [{ symbol, value, ok, message }] }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `gvlPath` | `string?` | `null` | Base symbol path of the test PRG status struct. Omit for the armed gvlPath (default 'GVL_IBN'). |
| `amsNetId` | `string?` | `null` | AMS Net ID. Omit for the armed/local target. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_measure_signal_transition`

Measure the time between two symbol conditions (edge A → edge B) by polling. Read-only, moves nothing; a cross-check for the test PRG's own PLC-timer measurement, which is more deterministic. A condition compares the symbol's rendered value to the *Equals string. Returns JSON { success, elapsedMs, timedOut, message } or an [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `startSymbol` | `string` |  | Symbol whose condition starts the measurement, e.g. 'MAIN.fbCyl.ValveWorkingPosition'. |
| `endSymbol` | `string` |  | Symbol whose condition ends the measurement, e.g. 'MAIN.fbCyl.LimitSwitchWorkingPosition'. |
| `startEquals` | `string?` | `null` | Value the start symbol must equal to begin timing. Default 'true'. |
| `endEquals` | `string?` | `null` | Value the end symbol must equal to stop timing. Default 'true'. |
| `timeoutMs` | `int?` | `null` | Overall timeout in ms (default 5000, max 60000). |
| `amsNetId` | `string?` | `null` | AMS Net ID. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_ibn_abort`

Panic abort: command the test PRG to home/abort IMMEDIATELY and disarm. Not gated by the arm token; always works. Re-arming only in the host window (Approvals view). Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

### `twincat_ibn_release_all`

Panic abort AND reject every pending IBN proposal. Homes the test PRG, rejects all pending proposals and clears the registry. Not gated by the arm token. Re-arming only in the host window (Approvals view). Returns JSON { kind, armed:false, abortSymbol, result }.

_No parameters._

## IoLink

### `twincat_get_iolink_config`

Read the IO-Link configuration of a master terminal (e.g. EL6224). Read-only; needs an open solution. Per port (1..4): the assigned device (name, vendor, product id, IODD file) and its named process data (e.g. 'Valve 1 solenoid 00'). Result: summary line + JSON. The fieldbus topology does not descend into IO-Link ports, so pass the master's own tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the IO-Link master, e.g. 'TIID^Device 1 (EtherCAT)^Box 1 (EK1200)^Term 28 (EL6224)'. From twincat_get_fieldbus_topology. |

### `twincat_import_iodd`

Register an IO-Link device description (IODD .xml) in TwinCAT's IODD folder. An IO-Link master (e.g. EL6224) then breaks the device down into named process data. Writes the TwinCAT IODD library, not the project; no open solution needed. Reports the device identity (vendorId/deviceId/productId). Idempotent: an identical registered file is skipped. TwinCAT picks the file up on its next IODD rescan.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `sourcePath` | `string` |  | Full path of the IODD .xml file, e.g. 'C:\\temp\\Vendor-Device-20230815-IODD1.1.xml'. |
| `targetFolder` | `string?` | `null` | Override of the IODD folder. Empty = TwinCAT default (…\Config\Io\IOLink). |

### `twincat_check_iodd_present`

Check whether a device's IODD is installed in TwinCAT's IODD folder. Identify the device by productId, ioddFileName or vendorId+deviceId. Returns 'IODD present: …' or a fail-closed '[Error] Missing IODD — STOP…' telling the user to obtain it. Run it before configuring or linking an IO-Link device. No open solution needed.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `productId` | `string?` | `null` | Product id from the IODD's device variant, e.g. 'VABX-A-P-EL-E12-IOS'. |
| `ioddFileName` | `string?` | `null` | Exact IODD file name, e.g. 'Vendor-Device-20230815-IODD1.1.xml'. |
| `vendorId` | `int?` | `null` | IO-Link vendor id (with deviceId). |
| `deviceId` | `int?` | `null` | IO-Link device id (with vendorId). |
| `targetFolder` | `string?` | `null` | Override of the IODD folder. Empty = TwinCAT default (…\Config\Io\IOLink). |

## IoMapping

### `twincat_link_variables`

Link two TwinCAT variables given by their tree paths and save the project. Needs an open solution. I/O paths (TIID^...): twincat_get_fieldbus_topology; PLC paths (TIPC^...): twincat_plc_list. A refused link names the cause: type, bit size and direction of both variables, a path that does not resolve, or a path that is not a variable.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variable1TreePath` | `string` |  | Tree path of the first variable, e.g. 'TIPC^PLC^PLC Project^MAIN.bVar'. |
| `variable2TreePath` | `string` |  | Tree path of the second variable, e.g. 'TIID^Device 1 (EtherCAT)^Box 1^Channel 1'. |
| `bitOffset1` | `int` | `0` | Bit offset in the first variable (default 0). |
| `bitOffset2` | `int` | `0` | Bit offset in the second variable (default 0). |
| `size` | `int` | `0` | Bits to link; 0 = smaller size of both variables (default 0). |

### `twincat_unlink_variables`

Remove a variable link, or all links of one variable, and save the project. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variable1TreePath` | `string` |  | Tree path of the first variable. |
| `variable2TreePath` | `string` | `""` | Tree path of the second variable. Empty = remove ALL links of variable1TreePath. |

### `twincat_get_mapping_info`

Export all variable mappings (links) as VarLinks XML to a file. Overwrites an existing file at the target. Does not change the project. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `outputPath` | `string?` | `null` | Absolute path of the XML file (directory must exist). Omit for Mappings.xml next to the .sln. |

### `twincat_set_device_disabled`

Enable or disable a TwinCAT I/O device by its tree path and save the project. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the I/O device, e.g. 'TIID^Device 1 (EtherCAT)'. From twincat_get_fieldbus_topology. |
| `disabled` | `bool` |  | true to disable the device, false to enable it. |

## Libraries

### `twincat_list_library_references`

List the open project's PLC library references and what each resolves to. Reads the XAE library manager and compares with the .plcproj on disk (the .plcproj alone if the XAE is not ready). resolution: 'effective' (what the project compiles against), 'fixed' (concrete version referenced), 'newest-installed' ('*' taken as the highest installed version, an approximation) or 'unresolved'. JSON: { success, summary, count, references [{ placeholder, name, version, publisher, source, filePinnedVersion, versionMatchesFile, resolvedName, resolvedVersion, resolvedDistributor, resolution }], warnings }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `format` | `string?` | `null` | 'text' (default, one line per reference) or 'json' (full records). |
| `plcProjectName` | `string?` | `null` |  |

### `twincat_list_installed_libraries`

List the libraries installed in the local XAE library repository. Needs an open solution.

_No parameters._

### `twincat_check_libraries_installed`

Check that every PLC library reference is installed in the local repository. Each one is Installed, Missing or VersionMismatch; version '*' matches any installed version of that name.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` |  |

### `twincat_add_library_reference`

Add a PLC library or placeholder reference to the open project. A library that is not installed is refused with [Error] unless force=true. Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library name, e.g. 'Tc2_MC2'. |
| `version` | `string` | `"*"` | Version or '*' for latest (default). |
| `company` | `string?` | `null` | Distributor, e.g. 'Beckhoff Automation GmbH'. Optional. |
| `asPlaceholder` | `bool` | `false` | Add as placeholder instead of a direct library reference. Default false. |
| `force` | `bool` | `false` | Add even if the library is not installed locally. Default false. |
| `plcProjectName` | `string?` | `null` |  |

### `twincat_remove_library_reference`

Remove a PLC library or placeholder reference by name. No-op if absent. Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library or placeholder name to remove, e.g. 'Tc2_MC2'. |
| `plcProjectName` | `string?` | `null` |  |

### `twincat_install_library`

Install a .library/.compiled-library file into the local 'System' repository. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `libraryPath` | `string` |  | Absolute path to the .library/.compiled-library file. |
| `overwrite` | `bool` | `false` | Overwrite if the same version is already installed. Default false. |

### `twincat_save_as_library`

Export the open PLC project as a library file, optionally installing it. The outputPath extension selects '.library' (source) or '.compiled-library' (compiled); others are refused. The PLC project's Title (Project Information) must be set and the target directory must exist. Verified by the file existing and not being empty (size reported) and, with install, by the installed-library list. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `outputPath` | `string` |  | Absolute path ending in .library or .compiled-library, e.g. 'C:\Libs\PTC_Library.compiled-library'. |
| `install` | `bool` | `false` | Also install into the local XAE library repository. Default false. |
| `overwrite` | `bool` | `false` | Replace an existing file at outputPath. Default false refuses. |
| `encrypt` | `bool` | `false` | Encrypt the compiled library (.compiled-library only; needs software protection on the project). Default false. |

### `twincat_library_signatures`

Read the public API (FBs, functions, types) of the referenced PLC libraries. Use it before calling a library FB instead of guessing its inputs; compiled vendor libraries (Tc2_MC2, Tc3_EventLogger, …) have no readable source. Writes the XML to outputPath (outside the project) and returns per library name, version, distributor and type counts per kind; typeName adds that type's inputs, outputs and constants. Unresolved referenced libraries are named. Changes nothing in the project. FB methods and properties are probably not included.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `library` | `string?` | `null` | Library name or placeholder, e.g. Tc2_MC2. Omit for all referenced libraries. |
| `outputPath` | `string?` | `null` | Path of the XML file to write. Default %LOCALAPPDATA%\ptc-xae-mcp\library-signatures\<solution>.library-signatures.xml. |
| `typeName` | `string?` | `null` | Type whose inputs/outputs/constants are returned inline, case-insensitive, e.g. MC_MoveAbsolute. |
| `plcProjectName` | `string?` | `null` | PLC name or its project name. Empty = first PLC project. |

### `twincat_get_library_parameters`

List the parameter-list overrides of a referenced PLC library placeholder. Such as GVL_PARAMETER constants like NUMBER_OF_ITEMS, as stored in the .plcproj; none = library defaults apply.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `library` | `string` |  | Library or placeholder name, e.g. 'Sample-Library'. |

### `twincat_set_library_parameter`

Set or add one parameter-list override on a referenced PLC library placeholder. Saves and verifies the value in the .plcproj. Returns the updated override list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `library` | `string` |  | Library or placeholder name, e.g. 'Sample-Library'. |
| `listName` | `string` |  | Parameter list name, e.g. 'GVL_PARAMETER'. |
| `key` | `string` |  | Constant name, e.g. 'NUMBER_OF_ITEMS'. |
| `value` | `string` |  | New value, e.g. '101'. |

### `twincat_set_library_version`

Change the pinned version of an existing PLC library reference. Returns the updated reference list.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Library name, e.g. 'Tc2_MC2'. |
| `version` | `string` |  | New version string, e.g. '3.3.73.0' or '*'. |
| `company` | `string?` | `null` | Distributor override (optional). |
| `plcProjectName` | `string?` | `null` |  |

## Lifecycle

### `twincat_open_project`

Open a TwinCAT solution or project in the XAE, launching the XAE if needed. A project file (.tsproj / .tspproj / .hmiproj) gets a temporary .sln. If a .tsproj references .xti files (devices, boxes, axes, PLC projects) that are missing, the open is refused with [Error] listing them, since loading and saving would damage the .tsproj; allowMissingReferences opens anyway with [Warn], and twincat_save_project / twincat_close_project(saveFirst=true) repeat it. The result names the XAE host the TcVersion selects (32-bit below 4026, else 64-bit) and warns when it picks 32-bit although the 64-bit XAE is installed, or when the running XAE is not the selected one.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `slnPath` | `string` |  | Absolute path to the .sln, .tsproj, .tspproj or .hmiproj. |
| `closeCurrentFirst` | `bool` | `false` | Close the open solution first, without saving. Default false. |
| `allowMissingReferences` | `bool` | `false` | Open despite missing .xti files (result starts with [Warn]). Default false refuses. |
| `host` | `string` | `"auto"` | XAE host: 'auto' (default, selected by TcVersion), '64' or '32'. '64' opens an older project in the 64-bit XAE; the .tsproj TcVersion is never rewritten. Refused if that XAE is not installed or one of the other bitness is running. |

### `twincat_save_project`

Save all modified documents and the solution (File > Save All).

_No parameters._

### `twincat_shutdown`

Shut down this MCP server and exit its host application. Returns at once; the process exits ~200 ms later. Exiting kills a running Usermode Runtime (UMR) and aborts an armed IbnLive session (writes the abort bit, drops bEnable). Refuses while a UMR is running unless force=true.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `force` | `bool` | `false` | Shut down even while a UMR instance is running. Default false. |

### `twincat_close_project`

Close the open solution in the XAE; saveFirst=false discards unsaved changes.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `saveFirst` | `bool` | `true` | Save modified documents first. Default true. |

## NcAxes

### `twincat_list_nc_tasks_axes`

List every NC-Task and its axes in the Motion (NC) tree. Read-only; needs an open solution. Returns a summary line and one line per NC-Task and axis with its tree path, for twincat_create_axis / twincat_configure_axis.

_No parameters._

### `twincat_create_nc_task`

Create an NC-Task under the Motion (TINC) root. Needs an open solution. Works only when the project has no NC-Task yet; otherwise it returns guidance to reuse the existing one (twincat_list_nc_tasks_axes + twincat_create_axis). Returns the created tree path as read back. The XAE appends ' SAF' to the name itself ('NC-Task 1' → 'NC-Task 1 SAF'); a trailing ' SAF'/' SVB' in the given name is removed and reported. NOT saved: call twincat_save_project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Base name for the new NC-Task without ' SAF', e.g. 'NC-Task 1'. |

### `twincat_create_axis`

Create an axis under an existing NC-Task. Needs an open solution. Returns the created axis path as read back. NOT saved: call twincat_save_project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ncTaskPath` | `string` |  | Tree path of the NC-Task, from twincat_list_nc_tasks_axes. |
| `axisName` | `string` |  | Name of the new axis, e.g. 'Axis 1'. |

### `twincat_configure_axis`

Change parameters of an existing NC axis; unspecified parameters are kept. Needs an open solution. Pass only the fields to change, as text (numbers/booleans too). NOT saved: call twincat_save_project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `axisPath` | `string` |  | Tree path of the axis, from twincat_list_nc_tasks_axes. |
| `axisType` | `string?` | `null` | Drive class: 'canopen_ds402' (EL72xx/ELM72xx/AX8xxx, CoE) or 'sercos_soe' (AX5xxx, SoE). Sets axis IO type, encoder type and drive type together; TwinCAT regenerates the process image. |
| `unit` | `string?` | `null` | Engineering unit, e.g. 'mm' or 'deg'. |
| `scaleFactorNumerator` | `string?` | `null` | Encoder scaling numerator. |
| `scaleFactorDenominator` | `string?` | `null` | Encoder scaling denominator. |
| `velocityMax` | `string?` | `null` | Maximum velocity. |
| `acceleration` | `string?` | `null` | Acceleration. |
| `deceleration` | `string?` | `null` | Deceleration. |
| `jerk` | `string?` | `null` | Jerk. |
| `encoderInverse` | `string?` | `null` | Encoder inversion 'true'/'false'. |
| `moduloFactor` | `string?` | `null` | Encoder modulo factor. |
| `softMinEnable` | `string?` | `null` | Enable soft minimum position limit 'true'/'false'. |
| `softMinRange` | `string?` | `null` | Soft minimum position limit value. |
| `softMaxEnable` | `string?` | `null` | Enable soft maximum position limit 'true'/'false'. |
| `softMaxRange` | `string?` | `null` | Soft maximum position limit value. |

### `twincat_read_axis_params`

Read the current parameters of an NC axis. Read-only; needs an open solution. Returns a summary, the known fields (same keys as twincat_configure_axis) and the raw axis XML.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `axisPath` | `string` |  | Tree path of the axis, from twincat_list_nc_tasks_axes. |

## PersistentSnapshot

### `twincat_list_ads_symbols`

List the online symbol table of a running PLC or UMR via ADS (no values). For values use twincat_read_ads_variables_batch. Read-only. Returns JSON { success, total, offset, returned, has_more, next_offset, symbols: [{ path, type, size, isPersistent, readOnly, comment }], amsNetId, adsPort } or [Error]. Call again with offset=next_offset while has_more. total is null when the walk stopped at 100000 symbols (narrow root or maxDepth). isPersistent marks PERSISTENT only; RETAIN is NOT flagged over ADS.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `root` | `string?` | `null` | Symbol whose children to list, e.g. 'GVL_Recipe'. Omit for the top level. |
| `maxDepth` | `int?` | `null` | Levels below root to list. Default 1. |
| `persistentOnly` | `bool` | `false` | Only symbols flagged persistent. Default false. |
| `limit` | `int?` | `null` | Symbols per page: default 500, at most 5000 (0 = 5000). |
| `offset` | `int` | `0` | Symbols to skip (next_offset of the previous page). Default 0. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_save`

Save a running PLC's PERSISTENT/RETAIN data to a snapshot file. Read-only on the PLC; writes only the local JSON file (leaf path -> { type, value }). Leaves come from the open solution's compiled .tmc (selectionSource 'tmc'; [Error] if a TMC leaf is missing on the PLC or has another type), else from the runtime's persistent flags ('ads', no RETAIN). Leaves that cannot be captured (e.g. input-mapped, POINTER, PROPERTY) are listed in skipped with the reason. Returns JSON { success, path, leafCount, selectionSource, skipped[], skippedCount, warnings[], amsNetId, adsPort }, not the values.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string?` | `null` | 'persistent', 'retain' or 'both' (default). |
| `roots` | `string[]?` | `null` | Extra symbol paths whose whole subtree is added, e.g. ['GVL_Settings.nRetainCounter']. |
| `selection` | `string?` | `null` | 'auto' (default: TMC if found, else ADS), 'tmc', 'ads' or 'roots' (only roots[]). |
| `tmcPath` | `string?` | `null` | Absolute path of the PLC .tmc. Omit to find it in the open solution. |
| `path` | `string?` | `null` | Target .json file. Default %LOCALAPPDATA%\ptc-xae-mcp\snapshots\<utc>_<netId>.json. Must be inside the solution directory, TEMP, %LOCALAPPDATA%\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_compare`

Compare a snapshot file leaf by leaf with the running PLC or another snapshot. Read-only; with otherPath no PLC is needed. Returns JSON { success, equal, against, equalCount, differenceCount, onlyInSnapshotCount, onlyOnPlcCount, typeMismatchCount, total, offset, returned, has_more, next_offset, differences[{ path, snapshotPath, type, snapshot, live }], onlyInSnapshot[], onlyOnPlc[], typeMismatches[], conflicts[], warnings[] }. limit/offset page the four leaf lists together; call again with offset=next_offset while has_more; *Count gives each list's size. equal = every snapshot leaf matches; onlyOnPlc (remanent leaves not in the snapshot) does not affect it. Integer widths, REAL/LREAL and STRING/WSTRING are compatible; other type changes are mismatches.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `snapshotPath` | `string` |  | Snapshot file written by twincat_persistent_snapshot_save. |
| `otherPath` | `string?` | `null` | Second snapshot file to compare against instead of the PLC. |
| `pathMap` | `string?` | `null` | JSON rename rules for the snapshot paths, e.g. {"GVL_Old.stRecipe":"GVL_New.stRecipe"} (exact path or prefix at a '.'/'[' boundary). |
| `tmcPath` | `string?` | `null` | Absolute path of the PLC .tmc for the live remanent set. Omit to find it. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |
| `limit` | `int?` | `null` | Items per list and page: default 200, at most 5000 (0 = 5000). |
| `offset` | `int` | `0` | Items to skip in every list (next_offset of the previous page). Default 0. |

### `twincat_persistent_snapshot_restore`

Restore a snapshot into the running PLC; DESTRUCTIVE and human-confirmed. A dry run only returns the plan { planHash, plcState, writes[{ path, type, current, snapshot }], skips[{ path, reason }], unchanged } and writes nothing. dryRun=false needs the dry run's planHash, refuses if the plan changed, raises a confirmation popup in the host window (target, PLC RUN/STOP, differences) and returns { restoreId, status: "pending" }. NOTHING is written until a human confirms; no tool can confirm. After confirmation the plan is rechecked (rejected if the PLC changed), only differing leaves are written and every written leaf is read back; poll twincat_persistent_snapshot_restore_status. Type changes, out-of-bounds indices, read-only and input-mapped leaves are skipped. A running program may overwrite a restored value; the read-back shows it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `snapshotPath` | `string` |  | Snapshot file written by twincat_persistent_snapshot_save. |
| `dryRun` | `bool` | `true` | true (default): return the plan only. false: propose the restore for human confirmation. |
| `planHash` | `string?` | `null` | planHash from the dry run. Required when dryRun=false. |
| `pathMap` | `string?` | `null` | JSON rename rules old -> new (see twincat_persistent_snapshot_compare). |
| `include` | `string[]?` | `null` | Restore only these leaf paths. Omit for all. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_persistent_snapshot_restore_status`

Poll the status of a persistent-snapshot restore proposal. Read-only. Returns JSON { restoreId, status, reason, target, plcState, snapshot, createdUtc, expiresUtc, writeCount, report }; status is pending | confirmed | executed | rejected | expired. Once executed, report has { written, failed, skipped, items[{ path, outcome, readBack, message }] } with read-back values.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `restoreId` | `string` |  | restoreId from twincat_persistent_snapshot_restore. |

## PlcControl

### `twincat_plc_start`

Start the PLC runtime (RUN) over ADS. Use after twincat_login mode=download, which leaves the PLC in STOP; unlike twincat_restart_runtime it keeps the downloaded program instead of reloading the old boot project. Reads the state back, so a command without effect is reported as a failure.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | Target AmsNetId. Default: the local NetId. |
| `adsPort` | `int` | `851` | ADS port of the PLC runtime. Default 851 (first PLC). |

### `twincat_plc_stop`

Stop the PLC runtime over ADS; on a real machine this stops the process. Reads the state back and reports the actual runtime state.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | Target AmsNetId. Default: the local NetId. |
| `adsPort` | `int` | `851` | ADS port of the PLC runtime. Default 851 (first PLC). |

### `twincat_activate_boot_project`

Write the current program as the PLC boot project ('Activate Boot Project'). Without it a download lives in RAM only and the next power cycle loads the previous boot project. Works online or offline. dryRun (default) changes nothing and is safe on a running plant. With dryRun=false the result lists the rewritten _Boot files and fails if no boot application (*.app) was produced. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dryRun` | `bool` | `true` | true (default) only probes and changes nothing; false writes the boot project. |

## PlcEntries

### `twincat_plc_list_entries`

List the declaration entries (fields, enum values, variables) of a PLC element. Covers STRUCT/UNION fields, ENUM values, GVL variables and POU/MEMBER variables per section (VAR_INPUT … VAR_GLOBAL, optionally CONSTANT/RETAIN/PERSISTENT). One line per entry: line number, section, name, AT address, type, initial value, comment, attribute pragmas. Entries twincat_plc_edit_entries cannot change safely (several names in one entry, several entries on one line, no ';') carry a note. DUT ALIAS, INTERFACE and ACTION/TRANSITION have no entries.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of a DUT, GVL, POU or MEMBER. |
| `kind` | `string` |  | Kind the node must have: DUT, GVL, POU or MEMBER. |
| `section` | `string?` | `null` | Optional section filter, e.g. VAR_INPUT or 'VAR_GLOBAL PERSISTENT'. |

### `twincat_plc_edit_entries`

Add, change or remove one declaration entry, leaving the rest byte-identical. Works on STRUCT/UNION fields, ENUM values (via value; base type and attributes untouched), GVL variables (NORMAL, PARAMLIST) and POU/MEMBER variables per section; op=add creates a missing section. add: name + type (not for enum values). set: pass only what changes. remove: name only; its attribute lines go with it. Refused with [Error] before the logout, nothing changed: wrong kind, duplicate name, several names in one entry or several entries on one line, DUT ALIAS and INTERFACE, a parameter that does not apply, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by read-back; the result ends with the new 'Revision: …'. Renamed or removed entries break code and ADS callers — follow with twincat_rebuild_plc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of a DUT, GVL, POU or MEMBER. |
| `kind` | `string` |  | Kind the node must have: DUT, GVL, POU or MEMBER. |
| `op` | `string` |  | add, set or remove. |
| `name` | `string` |  | Entry name (for add: the new entry). |
| `section` | `string?` | `null` | Section, e.g. VAR_INPUT, 'VAR_GLOBAL PERSISTENT', 'VAR RETAIN'. Required when ambiguous; add creates a missing one. |
| `type` | `string?` | `null` | Data type, e.g. ARRAY[0..3] OF REAL. Required for add (not for enum values). |
| `value` | `string?` | `null` | ENUM entries only: the value; "" removes it. |
| `initialValue` | `string?` | `null` | Initial value (not for enum values); "" removes it. |
| `address` | `string?` | `null` | AT address, e.g. %I* or %QX0.0 (not for enum values); "" removes it. |
| `comment` | `string?` | `null` | Trailing comment; "" removes it. |
| `afterName` | `string?` | `null` | op=add: insert after this entry instead of at the section end. |
| `newName` | `string?` | `null` | op=set: new entry name. |
| `attributes` | `string[]?` | `null` | Replaces the attribute pragmas; each hide, "attribute 'hide'" or {attribute 'hide'}. null keeps, [] removes them. |
| `expectedRevision` | `string?` | `null` | Revision from twincat_plc_read; a different current revision refuses the edit. Omit to edit unconditionally. |

## PlcInstanceSettings

### `twincat_read_init_symbols`

Read init values of {attribute 'TcInitSymbol'} variables on the PLC instance. They live in the TwinCAT configuration, not in the PLC source. Needs a built PLC project and TwinCAT >= 4024.17.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC; optional when there is exactly one. |

### `twincat_write_init_symbol`

Set one {attribute 'TcInitSymbol'} init value on the PLC instance. The symbol must be listed by twincat_read_init_symbols (the XAE silently ignores others). Verified by read-back; a value the XAE does not store as given (invalid or out of range) is refused and the old value restored. Logs the XAE out first, saves afterwards. Does not activate: effective at the next Activate Configuration.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | Full symbol name as listed by twincat_read_init_symbols, e.g. 'GVL_Params.nSpeed'. |
| `value` | `string` |  | New init value as IEC literal, e.g. '42', '1.5', 'TRUE'. |
| `plcProjectName` | `string?` | `null` | PLC project under TIPC; optional when there is exactly one. |

### `twincat_read_retain_config`

Read the retain configuration (RetainSrc data areas) of the PLC instance. Lists each area's symbols (type, BitSize, BitOffs) and RetainDataMappings if present. Cannot be written; declare retain variables in the source (VAR RETAIN). Needs a built PLC project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC; optional when there is exactly one. |

### `twincat_get_type_file_generation`

Read whether the PLC project generates its .tmc and .tpy type files at build.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project under TIPC; optional when there is exactly one. |

### `twincat_set_type_file_generation`

Switch .tmc/.tpy type-file generation of the PLC project on or off. Coupled: tpy=true also turns tmc on, tmc=false also turns tpy off (tmc=false with tpy=true is refused). Returns the flags read back. Logs the XAE out first, saves afterwards; effective at the next build.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmc` | `bool?` | `null` | Generate the .tmc file; omit = unchanged. |
| `tpy` | `bool?` | `null` | Generate the .tpy file; omit = unchanged. |
| `plcProjectName` | `string?` | `null` | PLC project under TIPC; optional when there is exactly one. |

## PlcObjects

### `twincat_plc_list`

List PLC elements (POU, INTERFACE, DUT, GVL, FOLDER) of the open PLC project. kind filters; parentPath limits to a subtree — under a POU, INTERFACE or PROPERTY it lists the MEMBERs (METHOD, ACTION, PROPERTY, Get/Set), which kind=MEMBER requires. One line per element: kind/subtype, tree path, language. Paged with limit/offset (last line: total, has_more, next_offset).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string?` | `null` | Optional kind filter, e.g. POU, DUT, MEMBER. |
| `parentPath` | `string?` | `null` | Optional caret-separated tree path to list below. Required for kind=MEMBER. |
| `recursive` | `bool` | `true` | False: only direct children of parentPath. Default true. |
| `limit` | `int` | `0` | Max elements; 0 (default) = all. |
| `offset` | `int` | `0` | Elements to skip (next_offset of the previous page). Default 0. |

### `twincat_plc_create`

Create a PLC element (POU, DUT, GVL, member, folder, …) under parentPath. kind/subtype and parent: POU/PROGRAM|FUNCTION|FUNCTION_BLOCK, DUT/STRUCT|ENUM|UNION|ALIAS (ALIAS needs a 'TYPE <name> : <baseType>; END_TYPE' declaration), GVL/NORMAL|PARAMLIST (persistent = a 'VAR_GLOBAL PERSISTENT' section in a NORMAL GVL's declaration), INTERFACE; MEMBER/METHOD|ACTION|PROPERTY|GET|SET under a POU or INTERFACE (GET/SET are named Get/Set; TRANSITION under a POU: ST, implementation only); FOLDER under a folder, the PLC project node, a POU or INTERFACE; TEXTLIST/LOCAL, IMAGEPOOL/LOCAL, UML/CLASSDIAGRAM, VISU/MANAGER|VISUALIZATION, RECIPE/MANAGER under a folder or the PLC project node; VISU/TARGET|WEB under a visualization manager; RECIPE/DEFINITION under a recipe manager; TASKREF/PROGRAM under a PLC task node (name = the PROGRAM). The global text list/image pool, text-list enumerations and trends cannot be created. declaration (not FOLDER/ACTION) and implementation (POU, METHOD, ACTION, TRANSITION, GET, SET; a PROPERTY's code goes into its accessors) fill the new element, verified like twincat_plc_write. A parameter that does not apply is an [Error]. Logs the XAE out first (refuses if that fails). Returns the new tree path. A new GVL, unused FB or function is only compiled once a task's program uses it (or with {attribute 'linkalways'}); verify with twincat_build_plc(expectSymbols=[...]).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `kind` | `string` |  | Element kind (see the tool description), e.g. POU, DUT, GVL, MEMBER, FOLDER. |
| `parentPath` | `string` |  | Caret-separated tree path of the parent (see the tool description per kind). |
| `name` | `string` |  | Name of the new element. |
| `subtype` | `string?` | `null` | Subtype, e.g. FUNCTION_BLOCK, STRUCT, METHOD. Required for POU and MEMBER; DUT defaults to STRUCT, GVL to NORMAL. |
| `declaration` | `string?` | `null` | Optional declaration; TwinCAT's default skeleton when omitted. |
| `returnType` | `string?` | `null` | Return type for FUNCTION, METHOD, PROPERTY. Default BOOL. |
| `implLanguage` | `string?` | `null` | ST, IL, SFC, FBD, CFC or LD (POUs and members). Default ST. |
| `accessors` | `string?` | `null` | PROPERTY only: GET, SET or BOTH (default). |
| `implementation` | `string?` | `null` | Optional body written right after creation: ST text, or XAE's XML for SFC/FBD/CFC/LD. |

### `twincat_plc_read`

Read a PLC element's declaration and implementation by tree path. Only existing sections are returned (DUT/GVL/INTERFACE: no implementation; ACTION/TRANSITION: no declaration), with the language; SFC/FBD/CFC/LD implementations are XAE's native XML. The first line ends with 'Revision: <16 hex digits>' — pass it as expectedRevision to a later write so the write is refused if the element changed meanwhile. paths reads several elements of one kind: each under '=== <path> ===', a failing one as its [Error]; the first line counts read and failed.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string?` | `null` | Caret-separated tree path of the element (as twincat_plc_list shows). Give path or paths. |
| `kind` | `string` | `""` | Kind the node must have, e.g. POU, DUT, GVL, MEMBER. |
| `openInEditor` | `bool` | `false` | Also open the element in the XAE editor, best-effort, behind the user's work unless bringToFront. Not with paths. Default false. |
| `bringToFront` | `bool` | `false` |  |
| `paths` | `string[]?` | `null` | Up to 50 tree paths of the same kind, instead of path. |

### `twincat_plc_write`

Replace the declaration and/or implementation of an existing PLC element. A null section stays unchanged; give each section inline or as a file, not both. Implementation: text for ST/IL; for SFC/FBD/CFC/LD exactly XAE's XML as twincat_plc_read returns it (root <NWL> for LD/FBD, <SFC>, <CFC>). A POU/INTERFACE/GVL/DUT declaration containing a METHOD/PROPERTY/ACTION header is refused (create members with twincat_plc_create). Refused with [Error] before the logout, nothing changed: wrong kind, a section the element lacks (implementation of a DUT/GVL/INTERFACE, declaration of an ACTION), invalid graphical XML, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by an independent read-back; the result ends with the new 'Revision: …'.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element. |
| `kind` | `string` |  | Kind the node must have, e.g. POU, DUT, GVL, MEMBER. |
| `declaration` | `string?` | `null` | New declaration; null = unchanged. Or declarationFile. |
| `implementation` | `string?` | `null` | New implementation; null = unchanged. Or implementationFile. |
| `declarationFile` | `string?` | `null` | File whose contents become the declaration. |
| `implementationFile` | `string?` | `null` | File whose contents become the implementation. |
| `expectedRevision` | `string?` | `null` | Revision from twincat_plc_read; a different current revision refuses the write. Omit to write unconditionally. |

### `twincat_plc_delete`

Delete a PLC element by tree path. Refuses the PLC project root, the PLC instance, References and nodes whose type cannot be read. A FOLDER must be empty unless recursive=true. Logs the XAE out first (refused if that fails); the delete is verified by lookup. Deleting something referenced elsewhere may break the build — check with twincat_build_plc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element. |
| `kind` | `string` |  | Kind the node must have, e.g. POU, DUT, GVL, MEMBER, FOLDER. |
| `recursive` | `bool` | `false` | FOLDER only: true deletes it with everything in it. Default false. |

### `twincat_plc_rename`

Rename a PLC element by tree path; returns the new path. Get/Set accessors cannot be renamed. Logs the XAE out first (refused if that fails). If TwinCAT asks whether to update all references, the tool answers per updateReferences and reports whether the prompt appeared and what was answered. Rebuild afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element. |
| `kind` | `string` |  | Kind the node must have, e.g. POU, DUT, GVL, MEMBER, FOLDER. |
| `newName` | `string` |  | New name. |
| `updateReferences` | `bool` | `false` |  |

### `twincat_plc_move`

Move a PLC element into another folder, keeping its object GUIDs. kind POU, INTERFACE, DUT or GVL: target is a FOLDER of the same PLC project (not inside a POU) or the PLC project node. kind MEMBER (METHOD, ACTION, PROPERTY, TRANSITION): target is a FOLDER inside the same POU/INTERFACE or that POU/INTERFACE itself. The target must exist (create it with twincat_plc_create kind=FOLDER). Works by PLCopen export, delete and re-import (for a MEMBER: of its whole POU/INTERFACE); the XAE is not brought to the front. Refused before the logout, nothing changed: wrong kind, missing, non-folder or current target, name already in the target, member target outside its POU, Get/Set accessor. Logs the XAE out first (refused if that fails). Verified afterwards (new path resolves, old one gone, text and GUIDs unchanged); a failure after the delete re-imports the original and says what happened. Exports are kept under %LOCALAPPDATA%\ptc-xae-mcp\move. Not saved — call twincat_save_project afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element. |
| `kind` | `string` |  | Kind the node must have: POU, INTERFACE, DUT, GVL or MEMBER (checked first). |
| `targetFolderPath` | `string` |  | Caret-separated tree path of the existing target folder or the PLC project node; for MEMBER a folder in the same POU/INTERFACE or that POU/INTERFACE. |

## PlcOnlineStatus

### `twincat_get_plc_online_state`

Check whether the XAE is Online (logged in to a running PLC runtime). The PLC write tools (twincat_plc_write etc.) log out by themselves before writing. Returns JSON { isOnline, message }: true = Online, false = Offline, null = unknown on this TwinCAT version.

_No parameters._

## PlcProjectInfo

### `twincat_plc_project_info`

Read or set the Project Information (Title, Version, …) of a PLC project. Fields (project properties > Common): Title, Version, Company, Author, DefaultNamespace, Description, Released. Without values it lists every field; with values it sets exactly those ("" clears one), verifies by read-back and saves the project. twincat_save_as_library needs a Title (else "'Title' not specified") and normally a Version.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | Name of the PLC (or its project); empty = the first PLC project. |
| `title` | `string?` | `null` | Title of the project/library; required to save it as a library. |
| `version` | `string?` | `null` | Four numbers, e.g. 1.2.0.0; anything else is refused before the write. |
| `company` | `string?` | `null` | Company (distributor of the library). |
| `author` | `string?` | `null` | Author. |
| `defaultNamespace` | `string?` | `null` | Default namespace of the library. |
| `description` | `string?` | `null` | Description. |
| `released` | `bool?` | `null` | Released flag of the library version. |

## PlcProjectLifecycle

### `twincat_plc_project_create`

Create a new PLC project under TIPC in the open solution. The name must not exist under TIPC yet. Logs the XAE out first, saves afterwards. Then build with twincat_build_plc.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | Name of the new PLC project, e.g. 'Plc2'. |
| `template` | `string?` | `null` | 'Standard PLC Template' (default), 'Empty PLC Template', another installed Beckhoff template name, or the absolute path of a custom .plcproj template (copied). |

### `twincat_plc_project_add_existing`

Add an existing PLC project (.plcproj or .tpzip) to the open solution. A .plcproj project is named after its own <Name>; a .tpzip needs mode=COPY and a name. Refuses a name already under TIPC. Logs the XAE out first.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Absolute path of the .plcproj or .tpzip. |
| `mode` | `string` |  | COPY (copy the project folder next to the solution; source unchanged), MOVE (move it there; the source folder is gone) or IN_PLACE (reference it where it is). |
| `name` | `string?` | `null` | Optional for a .plcproj (must equal its own name); required for a .tpzip. |

### `twincat_plc_project_export`

Export one PLC project of the open solution as a .tpzip archive. The target directory must exist; an existing file is overwritten. Verified by the written file. Counterpart: twincat_plc_project_import.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string` |  | Name of the PLC project under TIPC, e.g. 'FixturePlc'. |
| `tpzipPath` | `string` |  | Absolute path of the .tpzip to write. |

### `twincat_plc_project_import`

Import a .tpzip archive as a new PLC project of the open solution. The name must not exist under TIPC yet. Logs the XAE out first, saves afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tpzipPath` | `string` |  | Absolute path of the .tpzip. |
| `name` | `string` |  | Name of the new PLC project. |

### `twincat_solution_save_archive`

Save the whole open solution as a .tszip archive ('Save <solution> as Archive'). Saves all documents first. The target directory must exist. Counterpart: twincat_solution_open_archive.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tszipPath` | `string` |  | Absolute path of the .tszip to write. |

### `twincat_solution_open_archive`

Unpack a .tszip archive as a new solution <destDir>\<name>.sln and open it. Needs a running XAE. An open solution is closed (without saving) only when closeCurrentFirst=true. Verified by the active solution path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tszipPath` | `string` |  | Absolute path of the .tszip. |
| `destDir` | `string` |  | Absolute directory to unpack into (new or empty). |
| `name` | `string` |  | Name of the new solution (without .sln). |
| `closeCurrentFirst` | `bool` | `false` | Close the open solution first, without saving. Default false. |

### `twincat_plc_instance_from_tmc`

Create a PLC instance (I/O image only, no source) from a .tmc file under TIPC. It is named after the .tmc module. Refuses a name already under TIPC. Logs the XAE out first, saves afterwards.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tmcPath` | `string` |  | Absolute path of the .tmc file. |
| `name` | `string?` | `null` | Optional expected instance name; must equal the .tmc module name. |

## PlcSearch

### `twincat_plc_search`

Search declarations and implementations across the open PLC project(s). Covers every POU, METHOD, PROPERTY (Get/Set), ACTION, TRANSITION, INTERFACE, DUT and GVL, with unsaved editor changes. Each hit: kind and tree path, section, line:column (as in twincat_plc_read), the element's revision (expectedRevision for twincat_plc_replace_text / twincat_plc_write) and an excerpt with hit lines marked '>'. Graphical implementations (SFC/FBD/LD/CFC) are not searched and listed as skipped; their declarations are. Paged with limit/offset (last line: total, has_more, next_offset).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pattern` | `string` |  | Text to find; a regular expression when regex=true. |
| `regex` | `bool` | `false` | True: pattern is a .NET regular expression (^ and $ per line). Default false. |
| `caseSensitive` | `bool` | `false` | True: match case exactly. Default false. |
| `wholeWord` | `bool` | `false` | True: skip hits inside longer identifiers. Default false. |
| `scope` | `string?` | `null` | declaration, implementation or both (default). |
| `pathPrefix` | `string?` | `null` | Optional caret-separated tree path: only this element and below (a folder, or a POU with its members). |
| `kind` | `string?` | `null` | Optional kind filter: POU, INTERFACE, DUT, GVL or MEMBER. |
| `contextLines` | `int` | `1` | Context lines before and after each hit, 0–10. Default 1. |
| `limit` | `int?` | `null` | Hits per page: default 100, max 1000 (0 = 1000). |
| `offset` | `int` | `0` | Hits to skip (next_offset of the previous page). Default 0. |

### `twincat_plc_replace_text`

Replace an exact text snippet in a PLC element's declaration or implementation. The rest stays byte-identical (no need to rewrite the section with twincat_plc_write). oldText must occur exactly once (case and whitespace as twincat_plc_read returns them; LF also matches CRLF) unless occurrence picks one. Refused with [Error] before the logout, nothing changed: oldText not found (nearest match shown) or found several times (lines listed), a missing section, a graphical implementation (SFC/FBD/LD/CFC: use twincat_plc_write), a METHOD/PROPERTY/ACTION header in a POU/INTERFACE/GVL/DUT declaration, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by read-back. Returns the changed lines with two lines of context and the new revision.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Caret-separated tree path of the element. |
| `kind` | `string` |  | Kind the node must have: POU, INTERFACE, DUT, GVL or MEMBER. |
| `section` | `string` |  | declaration or implementation. |
| `oldText` | `string` |  | Exact text to replace (may span lines). |
| `newText` | `string` |  | Replacement; "" deletes oldText. |
| `occurrence` | `int?` | `null` | 1-based occurrence when oldText occurs several times; omit to require exactly one. |
| `expectedRevision` | `string?` | `null` | Revision from twincat_plc_read or twincat_plc_search; a different current revision refuses the replacement. |

## PlcTasks

### `twincat_plc_task_create`

Create a real-time task for a PLC and link it to the PLC project. Creates the task under SYSTEM > Tasks, the PLC task reference linked to it and, with program, the call of that PROGRAM. Refused with [Error] before the logout, nothing changed: name not an IEC identifier or already used by a task, cycle time <= 0 or not a multiple of 100 ns, priority outside 1..63 or used by another task under TIRT (listed), unknown PLC project or PROGRAM. Logs the XAE out first. The XAE may adjust cycle time or priority silently: the result shows the read-back values and warns on a difference. If a later step fails, the [Error] names what was created and how to finish or remove it. Saves on success; effective at the next Activate Configuration. Priorities of tasks outside TIRT (NC SAF/SVB) are not checked.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` |  | New task name (IEC identifier), e.g. 'FastTask'; the PLC task reference gets the same name. |
| `cycleTimeMs` | `double` |  | Cycle time in ms, > 0 and a multiple of 0.0001 ms (100 ns), e.g. 10 or 0.5. |
| `priority` | `int?` | `null` | Priority 1..63 (lower = more urgent). Omit for the next free one (highest in use + 1). |
| `plcProjectName` | `string?` | `null` | The PLC by name or project name, e.g. 'PLC'. Optional with exactly one PLC project. |
| `program` | `string?` | `null` | Optional PROGRAM the task calls, e.g. 'MAIN'; must exist in the PLC project. |

## PouMembers

### `twincat_plcopen_export`

Export one or more PLC objects to a PLCopen XML file.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | `string` |  | Absolute path of the XML file to write. |
| `selector` | `string` |  | ';'-separated dotted object names relative to the PLC project, e.g. "POUs.FB_Foo;POUs.MAIN" (a bare name without its container fails). |

### `twincat_plcopen_import`

Import objects from a PLCopen XML file into the PLC project. Logs the XAE out first (refused if that fails).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | `string` |  | Absolute path of the XML file to read. |
| `conflictMode` | `string?` | `null` | On a name conflict: None, Rename, Replace or Skip (default, non-destructive). |

## Receipts

### `twincat_receipt_get`

Show the receipt of a request that carried a requestKey. It names the tool, the start and finish time, the target, the outcome (running, ok, error or unknown) and the stored result. Use it after a timeout: running means the host is still at it, unknown means it never saw the call finish (look at the live state before you run it again with a new key). Returns JSON { requestKey, tool, targetNetId, startedUtc, finishedUtc, outcome, resultText } or [Error] when there is no receipt (never used, older than 7 days, or refused before it ran).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `requestKey` | `string` |  | The requestKey of the call, e.g. the UUID passed to twincat_activate_config. |

## Recovery

### `twincat_login`

Log the XAE in to the PLC (Offline → Online), applying changes per mode. When the running application differs: 'online_change' applies changes without a stop (keeps values); 'download' stops the PLC and transfers the full program (leaves it in STOP; start with twincat_plc_start); 'login_without_change' attaches without transferring code (monitoring only). Answers the login modal and follow-up confirmations. Already online = no-op success. No reachable runtime or project not activated: success=false, isOnlineNow=false. online_change is verified on the PLC (online-change counter read over ADS before and after): onlineChange.verdict 'applied', 'not_needed', 'not_applied' (success then false), 'download' (a fallback download replaced the application) or 'unknown' (counter unreadable); plcCycling false = application not running. Needs a compiled solution open. Returns { success, mode, dialogHandled, isOnlineNow, runState, message, onlineChange? }.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` | `"online_change"` | 'online_change' (default) \| 'download' \| 'login_without_change'. |
| `allowDownloadFallback` | `bool` | `false` | online_change only: if online change is impossible, download instead (stops the running PLC). Default false refuses with [Error]. |
| `adsPort` | `int` | `851` | online_change only: ADS port of the PLC whose counter is read. Default 851 (first PLC). |

### `twincat_logout`

Log the XAE out of the PLC (Online → Offline). No-op when already offline. Returns { wasOnline, isOnlineNow, message }. Call before twincat_clean_solution / twincat_rebuild_solution, which fail while Online. Needs an open solution.

_No parameters._

### `twincat_clean_solution`

Clean the whole solution: remove compiler output and boot-project artefacts. The next build starts from scratch. Returns a message or [Error]. Needs an open solution.

_No parameters._

### `twincat_rebuild_solution`

Rebuild the whole solution (Clean, then Build), like 'Rebuild All'. Compile only: no activation or runtime restart (use twincat_activate_config). Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only objects a task's program reaches are compiled). Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `ignoreStaleFiles` | `bool` | `false` | Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses. |
| `expectSymbols` | `string[]?` | `null` | Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip. |

### `twincat_rebuild_plc`

Rebuild one PLC project (Clean, then Build). Compile only: no activation or runtime restart. Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only objects a task's program reaches are compiled). Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project name (e.g. 'PLC'). Empty = auto-detect. |
| `ignoreStaleFiles` | `bool` | `false` | Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses. |
| `expectSymbols` | `string[]?` | `null` | Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip. |

### `twincat_force_close_xae`

Force-close a stale or crashed XAE (TcXaeShell); its unsaved work is lost. Explicit recovery only; no other tool terminates the XAE. Asks each instance to close and kills those that do not exit in time. Refuses while a dismissable modal (security code / restart / trial license) is open — that XAE is alive; use twincat_dismiss_security_dialog. Use only when twincat_get_status reports a stale/crashed instance or a multi-XAE block you cannot clear otherwise.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pid` | `int?` | `null` | PID of the one TcXaeShell to close (from the twincat_get_status multi-XAE list), leaving the other instances and their unsaved work alone. Omit to close all. |

## RuntimeControl

### `twincat_broadcast_search`

Search the local network for TwinCAT targets via ADS broadcast. Returns a JSON array of { name, netId, ipAddr, version, os }. Call it BEFORE twincat_set_target switches the project to a remote target; if the project already targets a remote system, set the target back to local first. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `searchTarget` | `string?` | `null` | Hostname or IP for a directed search (TC3 4020.10+), e.g. 'CX-12345' or '192.168.1.5'. Omit to broadcast on all interfaces. |

### `twincat_add_route`

Add an ADS route to a remote TwinCAT target. Get the target's netId and IP from twincat_broadcast_search first. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string` |  | AMS NetId of the remote target, e.g. '192.168.1.5.1.1'. |
| `ipAddr` | `string?` | `null` | IP address of the target. ipAddr or hostName is required. |
| `hostName` | `string?` | `null` | Hostname of the target. ipAddr or hostName is required. |
| `userName` | `string?` | `null` | User for a system route (not for project routes). |
| `password` | `string?` | `null` | Password for a system route (not for project routes). |
| `projectRoute` | `bool` | `true` | True (default): project route stored in the .tsproj, no credentials, preferred. False: persistent system route, needs userName/password. |

### `twincat_set_target`

Set the project's target AMS NetId and optionally the platform. Call it BEFORE twincat_activate_config to avoid the platform-mismatch dialog. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string` |  | AMS NetId of the target system, e.g. '192.168.1.5.1.1' or '127.0.0.1.1.1'. |
| `platform` | `string?` | `null` | 'TwinCAT RT (x64)', 'TwinCAT RT (x86)' or 'TwinCAT OS (x64)' (User Mode Runtime). Omit to change only the NetId. |

### `twincat_set_system_mode`

Switch a target's TwinCAT system to Config or Run mode via ADS. Same as the XAE's 'Restart TwinCAT (Config Mode)' / 'Restart TwinCAT System'; waits until the mode is confirmed. A switch stops the PLC on that target. Use it when a running target ignores twincat_activate_config's restart (config, activate, run). Needs no XAE or solution. After a switch to run the PLC on port 851 is checked (up to 15 s); [Warn] if the system runs but the PLC stays stopped.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string` |  | AMS NetId of the target system, e.g. '192.168.250.20.1.1'. Required, no implicit local target: pass the local router's NetId to switch the local system. |
| `mode` | `string` |  | 'config' or 'run'. |
| `timeoutSeconds` | `int?` | `null` | Seconds to wait for the mode to be confirmed. Default 60, clamped to 5..300. |

### `twincat_restart_runtime`

Start or restart the TwinCAT runtime on the project's target. Use after twincat_activate_config: activate generates the boot project, this starts it. Needs an open solution.

_No parameters._

### `twincat_umr_list_instances`

List the registered TwinCAT 3 Usermode Runtime instances on this machine. Instances are folders under %ProgramData%\Beckhoff\TwinCAT\3.1\Runtimes; the names are valid instanceName values for twincat_umr_start. Returns a comma-separated list, or a note if none.

_No parameters._

### `twincat_umr_start`

Start a registered TwinCAT 3 Usermode Runtime instance on the local machine. It starts in Config mode; an init failure returns an error. Does NOT require stopping TcSysSrv. Refused if this session already tracks a UMR process, or if the instance's NetId already answers over ADS (started elsewhere).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `instanceName` | `string?` | `null` | Registered instance name (see twincat_umr_list_instances). Omit for the single instance, else 'UmRT_Default'. |
| `exePath` | `string?` | `null` | Full path to TcSystemServiceUm.exe. Default: the standard installation path. |

### `twincat_umr_set_mode`

Switch the UMR started by twincat_umr_start between Config and Run mode. Config allows downloading a new configuration; Run executes the loaded PLC program. Succeeds only once the mode is confirmed (Run takes about 20 s). After a Run switch the PLC on port 851 is checked; [Warn] if the system runs but the PLC stays stopped. [Error] when the instance's NetId is unknown.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` |  | 'config' or 'run'. |
| `timeoutSeconds` | `int?` | `null` | Seconds to wait for the mode to be confirmed. Default 60, clamped to 5..300. |

### `twincat_umr_stop`

Stop the running User Mode Runtime process. Waits up to 5 s for it to exit. Safe if the UMR is already stopped.

_No parameters._

## Safety

### `twincat_safety_list`

List the TwinCAT Safety project's alias devices, groups and user FBs. Read-only, from the .splcproj and .sds files on disk. Alias devices (name = .sds file name): SDSID, AliasDeviceType Type/SubType, IO name, LinkingMode, folder; groups (.sal); user FBs (.ufb).

_No parameters._

### `twincat_safety_read_io_map`

Read the safe-I/O assignment as three separate fact lists. Read-only. Alias devices (.sds: SDSID, type, IO); channel usage per group (.sal aliasDeviceIoUsage: variable, source/target, sdsId, channel), joined to a device ONLY by sdsId = SDSID; Mappings.xml rows as found, naming a device only when the row carries its SDSID. Nothing is joined by position or guessed.

_No parameters._

### `twincat_safety_read_program`

Dump the FBD safety logic per group/network from the .sal files. Read-only. FB instances (type + name), input/output port sources/targets (connection monikers), FB parameters (discrepancy, Tof off-delays, EDM, restart). Flags [inferred] wiring.

_No parameters._

### `twincat_safety_add_alias_device`

Create a digital alias device: a BOOL between safety logic and standard PLC. EXPERIMENTAL: the XAE reload step is not yet verified against a live TwinSAFE project. Validates the name, allocates an unused SDSID, writes a standard digital .sds (Type 1, Crc 0) next to the group's alias devices, registers it in .splcproj (the .sds is rolled back if that fails), reads it back and reloads the project in the XAE. Refuses an existing name. The CRC stays 0 and is never computed: run 'Verify Complete Safety Project' in the TwinSAFE editor before download. Never downloads or activates the safety project.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `signalName` | `string` |  | Signal name = .sds file name: an IEC identifier with a configured prefix (default bSI_, bSQ_, SI, SQ; PTC_XAE_MCP_SAFETY_PREFIXES overrides). |
| `subType` | `int` | `2` | 2 = '1 Digital Output (Standard)': safety → standard PLC (e.g. bSI_ feedback). 1 = '1 Digital Input (Standard)': standard PLC → safety (e.g. bSQ_ request). Default 2. |
| `group` | `string?` | `null` | Group (.sal name without extension). Required only when the project has more than one group. |

### `twincat_safety_link_alias_device`

Link an alias device's EL1918 connection-image symbol to a standard-PLC symbol. Needs the safety project mapped/built so the connection symbol exists; otherwise a precondition error.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `aliasDevicePath` | `string` |  | Tree path of the alias device's connection-image symbol, e.g. 'TISC^TwinSAFE_PLC^Alias Devices^bSI_DriveReleased'. |
| `plcSymbolPath` | `string` |  | Dot path of the standard-PLC symbol, e.g. 'GVL_Safety.bSI_DriveReleased'. |

## Sbom

### `twincat_ipc_read_inventory`

Read a Beckhoff IPC's inventory from its Device Manager (MDP) over ADS. Read-only: device BTN/BIC and every module (TwinCAT version/build/System ID, installed Beckhoff software, OS, mainboard serial/BIOS, disks, NICs, CPU, memory); unknown modules and tables come as raw hex, never dropped. Returns JSON { success, status (ok | mdpUnavailable | targetUnreachable | mdpNotReady), message, netId, device { btn, bic }, modules [{ moduleType, typeName, name, tables [{ name, entries [{ name, value, raw, indexOffset, adsError }] }], decoded }], warnings }. A target without Device Manager (e.g. a VM or the user-mode runtime) gives status mdpUnavailable, not [Error]; [Error] only for bad arguments. The output contains IP/MAC addresses, route names and the TwinCAT System ID.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | Target IPC AMS NetId, e.g. '192.168.1.10.1.1'. Omit for the open solution's target; with a NetId no open solution is needed. |
| `moduleTypes` | `string[]?` | `null` | Filter on MDP module types, e.g. ['0x0008','0x000A']. Known: 0x0002 NIC, 0x0008 TwinCAT, 0x000A Software, 0x000B CPU, 0x000C Memory, 0x0018 OS, 0x001C Mainboard, 0x001D Disks. Default: all. |
| `includeRaw` | `bool` | `true` | Also give the raw hex of decoded values (undecodable ones always have it). Default true. |
| `timeoutMs` | `int?` | `null` | ADS timeout per read in ms, 100-30000. Default 5000; a timed-out read is retried once. |

### `twincat_ethercat_read_identities`

Read vendor, product, revision and serial of every EtherCAT slave online. Read-only; never changes state or switches to config mode. Joins the master's online data with the configured boxes by fixed address, for all masters (or masterTreePath). Per slave: address, treePath, name, projected {vendorId, productCode, revisionNo}, online {… serialNo}, state/stateName, link/linkFlags, flags identityInvalid, revisionDiffers, productDiffers, vendorDiffers, serialAvailable, missing, extraLink, source. Boxes without online address and online addresses without box are both listed. serialNo is a string (leading zeros kept); serialSource: master, else coe (0x1018:04), else soe:S-0-0432 (AX5000), else for a safety device its effective identifier (coe:0xF980:02, bic:BTN, hostDrive:S-0-0432). Every safety device gets an effective identifier or safetySerialMissing=true with a warning; unreadable identity = safetyIdentityUnknown; safetyIdentityComplete is false in both cases. Each slave has readStatus ok|partial|failed with readErrors; any incomplete read (or failed address/state/link read, then projected addresses are used) makes master and result status partial. A master that does not answer is masterUnreachable (others continue); one without address in its XML is addressNotFound. Result has summary {slaves, slavesRead, slavesPartial, slavesFailed, safetyDevices, safetyWithSerial, safetyUnknown, motors, motorsWithSerial}. Needs an open solution and the configuration active on the target.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `masterTreePath` | `string?` | `null` | Tree path of ONE EtherCAT master, e.g. 'TIID^Device 1 (EtherCAT)'. Omit for all masters. |
| `amsNetId` | `string?` | `null` | Override of the master's AMS NetId (default from its device XML). Only with masterTreePath. |
| `adsPort` | `int?` | `null` | Override of the master's ADS port (master service port 0xFFFF). Only with masterTreePath. |
| `includeCrc` | `bool` | `false` | Also read each slave's CRC error counters (ports A-D). Default false. |
| `includeCoe` | `bool` | `true` | Also read CoE device info of slaves with a mailbox (read-only SDO: 0x1008 name, 0x1009 hardware, 0x100A firmware, 0x1018:04 serial) into coe {status ok\|noMailbox\|notCoe\|error, …}; fills the serial the master reports as 0. Default true. |
| `includeDeviceDetails` | `bool` | `true` | Needs includeCoe. Also read per slave: bic (BTN, CoE 0x10E2:01), safety identity (0xF980) with an effective unique identifier (AX5805/AX5806 also safety.hostDrive), OCT motor/feedback name plate (0x9009/0x9008), and for AX5000 the SoE parameters in soe.channels[] (firmware, hardware, serial, type, safety option, motor name plate; optionCard = a plug-in card such as an encoder card, null if none). Default true. |
| `timeoutMs` | `int?` | `null` | ADS timeout per read in ms, 100-30000. Default 5000; a timed-out read is retried once. |

### `twincat_list_library_repositories`

List the XAE's PLC library repositories, optionally with hashed library files. Read-only. With includeFiles each library reference gets its resolved library (resolution effective | fixed | newest-installed | unresolved) and the file(s) in its repository folder with SHA-256 of the file itself (not a Twinpack content hash); none found = fileFound=false plus searchedPaths. Returns JSON { success, repositories [{ name, folder, folderExists }], references [{ plcProject, placeholder, name, requestedVersion, resolved*, resolution, fileFound, files [{ path, sha256, hashScope, sizeBytes }], searchedPaths }], warnings } or [Error]. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `includeFiles` | `bool` | `false` | Also resolve every library reference to its file(s) and hash them. Default false. |
| `plcProjectPath` | `string?` | `null` | Only this PLC project, by name or tree path (e.g. 'PLC' or 'TIPC^PLC'). Default: all. |

## Schema

### `twincat_get_status`

Get XAE status: IDE connection, active project/PLC, TwinCAT service, policy. The access policy (read / engineer / runtime, blocked and allowed NetIds) is set only in the window or by environment variables, never by a tool.

_No parameters._

### `twincat_get_log`

Get the log shown in the MCP server window. The log grows for the whole host session; only the newest maxChars characters are returned, behind a note of how much was omitted.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `maxChars` | `int` | `20000` | Max log characters returned, newest kept (default 20000); 0 = full log. |

### `twincat_stop`

Stop the currently running TwinCAT operation (like clicking the Stop button).

_No parameters._

### `twincat_validate_schema`

Validate a JSON IO schema file without applying it. Returns Valid/Invalid with the issues found. Checks ioPoints[].symbol (full PLC instance path, e.g. MAIN.aCyl[1].bOut) and refuses schema fields removed in 4.0.0 (the error names the migration).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `schemaPath` | `string` |  | Absolute path to the JSON schema file |
| `targetMasterPath` | `string?` | `null` | Optional brownfield target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'); its format is validated too. |

### `twincat_apply_schema`

Apply a JSON IO schema to the active XAE project. Builds the EtherCAT tree, links IO points that name a PLC symbol and exports; progress shows in the server window. Fail-closed by default: PLC project and EtherCAT master must match exactly (no fallback to the first one) and the target master must be provably empty. An IO point links to the AT %I*/%Q* variable named by ioPoints[].symbol, the full PLC instance path (MAIN.fbCyl.LimitSwitchWorkingPosition, MAIN.aCyl[1].bOut): looked up exactly in the process image, never a same-named member of another instance. A point without symbol only builds hardware. An analog channel is found from the terminal's PDOs (EL3174 channel 1 = 'AI Standard Channel 1^Value', the entry whose width fits the point's type); set ioLeaf only for another entry (a status bit). During the build the XAE's 'Sync Master' and 'Double use of the same Ethernet interface' boxes are answered with OK and the 'unrestored variable links' question with 'Ja' (keep the link information); answered dialogs are listed in the result, any other blocking dialog ends the run with an [Error] naming it.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `schemaPath` | `string` |  | Absolute path to the JSON schema file |
| `plcProject` | `string?` | `null` | PLC project name. Empty = the schema's twincat.plcProjectName. |
| `etherCatMaster` | `string?` | `null` | EtherCAT master name. Empty = the schema's twincat.deviceName. |
| `targetMasterPath` | `string?` | `null` | Brownfield: exact target EtherCAT master tree path (e.g. 'TIID^Device 1 (EtherCAT)'); resolved exactly and forces fail-closed mode. |
| `failClosed` | `bool` | `true` | Default true: exact PLC/master match, empty-check aborts on any uncertainty. false = lenient greenfield resolution (single-PLC / first-master fallback, prefix match on the master name). |
| `autoBuild` | `bool` | `true` | false skips the full solution build so a brownfield apply does not re-bind the existing PLC project; the symbols the schema names must then already be in the process image (twincat_build_plc). Default true. |

## Scope

### `twincat_create_scope_project`

Add a TwinCAT Scope (measurement) project to the open solution. Uses the 'YT Scope Project' template; needs TE130X Scope View installed. Returns the project path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Name of the new measurement project. Default 'MeasurementProject'. |

### `twincat_remove_scope_project`

Remove a Scope (measurement) project node from the open solution. Counterpart of twincat_create_scope_project. Does NOT save the solution or delete files from disk. Idempotent: benign result if no project matches. Call it before closing a solution with a scope project you created, so no dangling node breaks the reopen ('Missing Project Nodes').

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project to remove; must match an existing one. Required when the solution has several (else [Error]). |

### `twincat_add_scope_channel`

Add a channel bound to a PLC symbol to the scope project. Creates chart, axis-group and channel, reusing a chart/axis of the same name, and reuses or creates the DataPool acquisition (same symbol and port). Every field is read back; one that did not land returns [Error]. Returns the channel's tree path.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbol` | `string` |  | PLC symbol path, e.g. 'TIPC^PLC^PLC Projekt^MAIN.rActualValue'. |
| `chartName` | `string?` | `null` | Chart name; reused if it exists. Default: the symbol path. |
| `axisName` | `string?` | `null` | Axis-group name in the chart; reused if it exists. Default: the symbol path. |
| `channelName` | `string?` | `null` | Channel name. Default: the symbol path. |
| `stackedAxis` | `bool` | `false` | True puts the channel on its own Y-axis. Default false. |
| `symbolName` | `string?` | `null` | PLC-side symbol name for the acquisition, if it differs from the one derived from symbol. |
| `targetPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851 (PLC 1). |
| `sampleMode` | `string?` | `null` | Sample mode, e.g. 'Task Sampletime'. Default: the scope's default. |
| `sampleTimeMs` | `int?` | `null` | Sample time in ms. Default: the scope's default. |
| `symbolComment` | `string?` | `null` | Optional comment on the acquisition's symbol binding. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_read_scope_acquisition`

Read the symbol and ADS settings of one scope DataPool acquisition. Returns Symbol Name, Data Type, Sample Mode, Sample Time, Target Port and all raw properties; flags an acquisition with no symbol bound (records nothing). Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `acqSelector` | `string` |  | Acquisition name or 'DataPool^<name>'. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_set_scope_acquisition_symbol`

Set or repair the PLC symbol and sample settings of a scope acquisition. Only supplied fields change; supply at least one. Every field is read back; any that did not land returns [Error] naming what did and did not apply. Returns the read-back values.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `acqSelector` | `string` |  | Acquisition name or 'DataPool^<name>'. |
| `symbolName` | `string?` | `null` | PLC symbol to bind, e.g. 'GVL_HMI.HmiOpened'. |
| `targetPort` | `int?` | `null` | ADS port of the PLC runtime, e.g. 851. |
| `targetSystem` | `string?` | `null` | AMS NetId of the target system to sample from. |
| `sampleMode` | `string?` | `null` | Sample mode, e.g. 'Task Sampletime'. |
| `sampleTimeMs` | `int?` | `null` | Sample time in ms. |
| `timeOffsetS` | `double?` | `null` | Time offset in seconds. |
| `symbolComment` | `string?` | `null` | Comment on the acquisition's symbol binding. |
| `enabled` | `bool?` | `null` | Enable or disable the acquisition. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_set_scope_axis_mode`

Set a scope chart's Y-axis mode to overlaid or stacked. EXPERIMENTAL: not verified on a live TE130x. The value is read back; if the property is missing or differs, returns [Error] listing the chart's property keys.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `chartName` | `string` |  | Tree name of the chart. |
| `mode` | `string` |  | 'overlaid' (one shared Y-axis) or 'stacked' (one Y-axis per channel). |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_set_scope_labels`

Rename a scope chart or axis-group node, or set its titles. Supply at least one of name, chartTitle, axisTitle; a supplied title sets Show Title to Custom. Every value is read back; any that did not land returns [Error] naming what did and did not apply.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `nodeSelector` | `string` |  | Name of the chart or axis-group node. |
| `name` | `string?` | `null` | New node name (rename). |
| `chartTitle` | `string?` | `null` | Title text, only where the node has a title property; TE130x 4026.22 has none on a chart or axis group (a chart shows its name: use `name`), so there it returns [Error]. |
| `axisTitle` | `string?` | `null` | Y-axis title text. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_list_scope_channels`

List all charts, axis-groups, channels and acquisitions of the scope project. One line per node: kind (CHART|AXISGROUP|CHANNEL|ACQUISITION), tree path, bound acquisition (channels) and a flag if the label is still the template default.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_get_scope_status`

Report whether a scope (measurement) project exists in the open solution. Returns its chart/axis-group/channel/acquisition counts. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_export_scope_data`

Export recorded scope data to a file next to the open solution. Returns the written file path; [Error] if no data has been recorded yet.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `format` | `string` | `"CSV"` | CSV (default), BINARY, TDMS, DAT or SVD. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_start_scope_record`

Start a TwinCAT Scope recording. Does not check the current state: call twincat_get_scope_record_state first. [Error] if the start fails or no scope project exists.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_stop_scope_record`

Stop the active TwinCAT Scope recording. The scope then enters 'Reply' state; save the data with twincat_save_scope_data or twincat_export_scope_data. [Error] if the stop fails.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_get_scope_record_state`

Read the TwinCAT Scope recording state: Config, Record or Reply. 'Config' = not recording, 'Record' = recording, 'Reply' = stopped, data available. Call before start/stop. [Error] (never 'Config') when the state cannot be read. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_save_scope_data`

Save recorded TwinCAT Scope data as a native .svdx file. Needs 'Reply' state (recording stopped). Returns the written file path; [Error] if no data has been recorded. For CSV/TDMS/DAT/BINARY use twincat_export_scope_data.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string?` | `null` | Output .svdx path. Default ScopeData.svdx next to the open solution. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_set_scope_record_settings`

Set the scope's record settings (duration, ring buffer, auto-save, sync). Only supplied fields change; supply at least one. EXPERIMENTAL: not verified on a live TE130x; every value is read back and a field that did not land returns [Error] listing the scope root's property keys.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `recordTimeS` | `double?` | `null` | Total recording duration in seconds, > 0. |
| `ringbuffer` | `bool?` | `null` | True = ring-buffer ('Restart Record') mode. |
| `fileStore` | `string?` | `null` | File-store path for recorded data. |
| `recordMode` | `string?` | `null` | NOT writable: read-only in TE130x (follows the trigger configuration). Supplying it returns [Error] for this field; omit it. |
| `autoSaveMode` | `string?` | `null` | 'None', 'SVDX' or 'Export'. |
| `autoSavePath` | `string?` | `null` | Auto-save output path. |
| `filenameMask` | `string?` | `null` | Filename mask for auto-saved files. |
| `syncMode` | `string?` | `null` | 'Soft', 'Medium' or 'Hard'. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_create_scope_trigger_group`

Create a trigger group under the scope project's Trigger node. The Trigger node must exist (never created). EXPERIMENTAL: not verified on a live TE130x. The action is read back; if it did not land, the group is deleted again and [Error] lists its property keys. Add conditions with twincat_add_scope_trigger_set.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `action` | `string` |  | Event fired when all sets of the group are satisfied: 'StopRecord', 'StopDisplay', 'RestartDisplay', 'StartSubsave', 'StopSubsave' or 'EventMarker'. |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_add_scope_trigger_set`

Add a trigger set to an existing scope trigger group. EXPERIMENTAL: not verified on a live TE130x. For a ChannelSet supply threshold, edge and compare. Each condition is read back; any that did not land returns [Error] naming what did and did not apply.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `groupName` | `string` |  | Trigger group name (from twincat_create_scope_trigger_group). |
| `setType` | `string` | `"ChannelSet"` | 'ChannelSet' (default) or 'DirectorySet'. |
| `symbol` | `string?` | `null` | NOT supported: the trigger channel cannot be set through the automation interface. Supplying it returns [Error] for this condition; pick the channel in the Scope editor. |
| `threshold` | `double?` | `null` | Numeric trigger level (ChannelSet only). |
| `edge` | `string?` | `null` | 'Rising', 'Falling' or 'Both' (ChannelSet only). |
| `compare` | `string?` | `null` | 'Greater', 'Less' or 'Equal' (ChannelSet only). |
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

### `twincat_list_scope_triggers`

List the scope project's trigger groups and their trigger sets. Per group: name and action; per set: setType, symbol, threshold, edge, compare. EXPERIMENTAL: not verified on a live TE130x. Read-only.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `projectName` | `string?` | `null` | Measurement project name; required only when the solution has several (else [Error] lists them). |

## ScopeFile

### `twincat_read_scope_file`

Read a saved TwinCAT Scope recording (.svdx) without the XAE. EXPERIMENTAL. Needs TE130x Scope View or TF3300 Scope Server installed. Per channel returns name, symbol, comment, data type, sample time, NetId/port, scale/offset/unit, sample count, first/last time, min/max/mean; includeValues=true adds values in a time window, decimated to min/max per bucket (spikes survive). Times are ms since the recording start; start/end are UTC. path must lie in an allowed directory (%TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or PTC_XAE_MCP_ALLOWED_PATHS). Result: an [Experimental] line, a summary line, then JSON.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Path of the .svdx file, e.g. from twincat_save_scope_data. |
| `channels` | `string?` | `null` | Channel or symbol names for includeValues, comma-separated. Empty = all. |
| `fromMs` | `double?` | `null` | Window start in ms since the recording start. Empty = from the start. |
| `toMs` | `double?` | `null` | Window end in ms since the recording start. Empty = to the end. |
| `maxPoints` | `int` | `ScopeExportCsv.DefaultMaxPoints` | Max points per channel (default 500, at most 5000). |
| `includeValues` | `bool` | `false` | Also return the values, not only the statistics. Default false. |

### `twincat_convert_scope_file`

Convert a saved TwinCAT Scope recording (.svdx) to CSV, TDMS or similar. EXPERIMENTAL; runs without the XAE, needs TE130x Scope View or TF3300 Scope Server installed (.tdms/.dat may need a full licence). An existing target is refused unless overwrite=true. Both paths must lie in an allowed directory (%TEMP%, %LOCALAPPDATA%\ptc-xae-mcp, the TwinCAT installation or PTC_XAE_MCP_ALLOWED_PATHS).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | Path of the .svdx file. |
| `targetPath` | `string` |  | Output file; its extension selects the format, e.g. C:\temp\run1.csv. |
| `overwrite` | `bool` | `false` | Replace an existing target file. Default false. |

## SignalTestbench

### `twincat_ads_wait_until`

Wait until live PLC values meet conditions (ADS notifications). Short pulses are not missed. Read-only. Conditions are texts: '<symbol> <op> <value>' with op == != < <= > >=, or '<symbol> changed' (changed = differs from the value when the wait began, stays true once it happened), e.g. 'MAIN.nStep == 20', 'GVL.bDone == true', 'MAIN.fbCyl.State == E_STATE_RUN' (an ENUM by member name or number). Up to 8 conditions on up to 8 symbols (BOOL, numbers, time types in their raw unit, STRING, ENUM). A condition that already holds ends the wait at once (elapsedMs 0). Returns JSON { success, met, timedOut, elapsedMs, timeoutMs, combine, conditions: [{ condition, met }], values: { symbol: value }, amsNetId, adsPort }; a timeout is met=false with the last values, not an error. [Error] when a symbol is missing, is not a scalar, or the runtime stops while waiting. Changes are seen at the resolution of the PLC task cycle.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `conditions` | `string[]` |  | 1 to 8 conditions, e.g. ['MAIN.nCount > 5', 'MAIN.bDone == true']. |
| `combine` | `string?` | `null` | 'all' (default: every condition holds) or 'any' (one holds). |
| `timeoutMs` | `int?` | `null` | How long to wait in ms: default 10000, at most 300000 (a fixed limit). |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_ads_expect`

Check live PLC values over a time window (ADS notifications). Read-only. mode 'stable': none of the symbols changes for durationMs. mode 'within': the conditions hold at some point inside durationMs (already true counts). mode 'never': the conditions do not hold at any time of the window, the opening state included. Conditions as in twincat_ads_wait_until ('<symbol> <op> <value>' or '<symbol> changed'). A violation ends the check at once. Returns JSON { success, mode, passed, elapsedMs, durationMs, firstViolation: { symbol, atMs, reason, from, to } | null, combine, conditions, values, amsNetId, adsPort }; a failed expectation is passed=false, not an error. [Error] when a symbol is missing or not a scalar, or the runtime stops during the window (a stopped PLC would otherwise look stable). Up to 8 symbols, window at most 300000 ms.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `mode` | `string` |  | 'stable', 'within' or 'never'. |
| `durationMs` | `int` |  | Length of the observation window in ms, at most 300000 (a fixed limit). |
| `conditions` | `string[]?` | `null` | Conditions for 'within' and 'never', e.g. ['MAIN.nStep == 20']. |
| `symbols` | `string[]?` | `null` | Symbol paths for 'stable', e.g. ['GVL.bReady', 'MAIN.nMode']. |
| `combine` | `string?` | `null` | 'all' (default) or 'any', for 'within' and 'never'. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_ads_record`

Record live PLC values briefly (ADS notifications) and report statistics. Read-only on the PLC; writes only a local CSV of the changes. 1 to 32 scalar symbols (BOOL, numbers, time types, STRING, ENUM), durationMs at most 120000, cycleMs 1 to 1000 (the notification cycle). Times are the runtime's own time stamps, so pulse widths and periods are exact to a task cycle. Returns JSON { success, endedBy: completed | changeLimit | fault, durationMs, windowMs, timeBase, changes, csv: { path, rows, bytes }, symbols: [{ symbol, plcType, changes, first, last, min, max, mean (time-weighted), rising, falling, highMs / lowMs / periodMs: { count, min, max, avg } (BOOL: complete pulses only), frequencyHz, changeIntervalMs }] }. At most 200000 changes are kept; the run then ends with endedBy changeLimit. The CSV (t_ms;symbol;value;name, one row per change, opening values at 0) goes to %TEMP%\ptc-xae-mcp\rec unless csvPath is given; recordings there older than 7 days are removed. A pulse shorter than one PLC task cycle is not visible.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbols` | `string[]` |  | 1 to 32 symbol paths, e.g. ['MAIN.bRun', 'GVL.nCycleCount']. |
| `durationMs` | `int?` | `null` | Recording time in ms: default 5000, at most 120000 (a fixed limit). |
| `cycleMs` | `int?` | `null` | Notification cycle in ms, 1 to 1000. Default 1 (every task cycle); a slower cycle only loses changes. |
| `csvPath` | `string?` | `null` | Target .csv file. Default %TEMP%\ptc-xae-mcp\rec\<utc>_<netId>.csv. Must be inside the solution directory, TEMP, %LOCALAPPDATA%\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS. |
| `writeCsv` | `bool` | `true` | Write the CSV (default true). false: statistics only. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

### `twincat_ads_snapshot_compare`

Compare live values of any symbols with a baseline, or capture them. Read-only on the PLC; savePath writes only a local JSON file. A STRUCT or ARRAY symbol is read leaf by leaf; input-mapped and read-only variables are read too. The baseline is baselinePath (a file written by savePath of this tool or by twincat_persistent_snapshot_save) or baseline (JSON object { "<leaf symbol>": <value> }, typed like the PLC symbol). Without symbols, the baseline's leaves are read. savePath alone captures the current values. Returns JSON { success, equal, equalCount, differenceCount, onlyInBaselineCount, notInBaselineCount, typeMismatchCount, missingOnPlcCount, differences: [{ symbol, type, baseline, live }], onlyInBaseline, notInBaseline, typeMismatches, missingOnPlc, skipped, saved: { path, leafCount } | null }; lists show at most 200 entries. Up to 256 symbols and 5000 leaves per call. For the remanent (PERSISTENT / RETAIN) data and its restore use the twincat_persistent_snapshot_* tools.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `symbols` | `string[]?` | `null` | Symbol paths to read (up to 256), e.g. ['GVL_Settings', 'MAIN.nMode']. Omit to read the baseline's leaves. |
| `baselinePath` | `string?` | `null` | Snapshot file to compare with. |
| `baseline` | `string?` | `null` | Inline baseline as JSON, e.g. {"MAIN.nMode": 2, "GVL.bReady": true}. Not together with baselinePath. |
| `savePath` | `string?` | `null` | Write the current values to this .json file (inside the solution directory, TEMP, %LOCALAPPDATA%\ptc-xae-mcp or PTC_XAE_MCP_ALLOWED_PATHS). |
| `tolerance` | `double?` | `null` | Numbers differing by at most this much count as equal. Default 0. |
| `amsNetId` | `string?` | `null` | AMS NetId of the target. Omit for the local runtime. |
| `adsPort` | `int?` | `null` | ADS port of the PLC runtime. Default 851. |

## StaticAnalysis

### `twincat_plc_static_analysis`

Run PLC Static Analysis (TE1200) on the open PLC project and return findings. Covers rules, naming conventions and metrics; each finding: level, rule, file, line, message. Read from the SARIF export (TwinCAT >= 3.1.4026.20, written to %LOCALAPPDATA%\ptc-xae-mcp\static-analysis, outside the project), else from the Error List's SA entries (the result says why). Without a TE1200 license only the free rule subset runs. Compiles like a build, saves nothing. Counts and top rules cover every finding; listed findings are filtered by minLevel and paged with limit/offset (last line: total, has_more, next_offset).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `checkAll` | `bool` | `true` | True (default): also check objects no task calls. False: only objects in use. |
| `settingsFile` | `string?` | `null` | Optional .csa rule-set file loaded before the run. |
| `minLevel` | `string` | `"warning"` | Lowest level listed: error, warning (default) or note. |
| `limit` | `int?` | `null` | Findings per page: default 100, max 1000 (0 = 1000). |
| `offset` | `int` | `0` | Findings to skip (next_offset of the previous page). Default 0. |

## TcUnit

### `twincat_tcunit_results`

Read TcUnit test results from a running PLC over ADS. Waits until the TcUnit runner (GVL_TcUnit.TcUnitRunner) has finished and stored its results, then reads totals, every suite and the cases of failed suites. Does not deploy: build first (twincat_build_plc expectSymbols=['GVL_TcUnit.TcUnitRunner'] proves the runner is compiled in), start the tests with twincat_activate_config(autoConfirmRestart=true) or twincat_login(mode='download'), then call this. Tests do not rerun after an online change; the result then warns with OnlineChangeCnt. Returns JSON with success, verdict (passed | failed | no_tests | unknown), message, totals, suites with their cases, and junitFile when junitPath is given; [Error] when TcUnit is missing or does not finish within timeoutSeconds.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `amsNetId` | `string?` | `null` | AMS NetId of the PLC. Default: the open project's target (twincat_set_target). |
| `adsPort` | `int?` | `null` | ADS port of the PLC application. Default 851. |
| `timeoutSeconds` | `int?` | `null` | Seconds to wait for TcUnit to finish. Default 60, max 600. |
| `includePassed` | `bool` | `false` | Also list passed cases and passed suites' cases (JSON and JUnit file). Default false: only failed and skipped. |
| `junitPath` | `string?` | `null` | Optional JUnit XML file to write the results to, e.g. for CI. |

## VariantManagement

### `twincat_get_variant_status`

Get the active project variant and all available variants and groups. Returns JSON { activeVariant, availableVariants, groups } or [Error]; empty lists when the project has no Variant Management.

_No parameters._

### `twincat_set_active_variant`

Set the active project variant by name, optionally building afterwards. An unknown name returns [Error] and changes nothing; the switch is read back and a failed switch restores the previous variant. Returns a status message (plus build log when building) or [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variantName` | `string` |  | Variant name as defined in the project, e.g. SIMULATION; [GroupName] activates a variant group. |
| `rebuildAfter` | `bool` | `true` | Build the solution after switching. Default true; without a build the change does not take effect. |

### `twincat_variant_create`

Create a project variant, optionally as a member of an existing variant group. A duplicate or invalid name returns [Error] and changes nothing; the variant list is read back and a write that does not land is rolled back. The first variant of a project becomes the active variant. Returns JSON { variant, group, activeVariant, availableVariants, groups, notes } or [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variantName` | `string` |  | Name of the new variant, e.g. Machine or Simulation. Must not contain [ ] ^ < > & quotes or slashes. |
| `group` | `string?` | `null` | Optional name of an existing variant group to add the variant to. |

### `twincat_variant_delete`

Delete a project variant and its memberships in variant groups. The active variant (or a member of the active group) is refused with a hint to switch first; an unknown name returns [Error] and changes nothing. Per-variant settings of tree items that remain are named in the result. Returns JSON { variant, removedFromGroups, availableVariants, groups, notes } or [Error].

| Parameter | Type | Default | Description |
|---|---|---|---|
| `variantName` | `string` |  | Name of the variant to delete, as listed by twincat_get_variant_status. |

### `twincat_variant_set_item`

Set disabled and/or NC axis simulation of a tree item for one project variant. Turns the item's variant-dependent flag on, switches to the variant, writes and reads the value back, switches back to the original variant (also after an error) and saves. Returns JSON { treePath, variant, activeVariant, variants: [{ variant, disabled, axisSimulation }], notes } with the value in every variant, or [Error]. Needs at least one of disabled or axisSimulation.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Tree path of the item, e.g. TIID^Device 1 (I/O device or box) or an NC axis path from twincat_list_nc_tasks_axes. |
| `variant` | `string` |  | Variant the setting belongs to, as listed by twincat_get_variant_status (a single variant, not [Group]). |
| `disabled` | `bool?` | `null` | true disables the item in that variant, false enables it. Omit to leave unchanged. |
| `axisSimulation` | `bool?` | `null` | true makes the NC axis a simulation axis in that variant, false a real one. NC axes only. Omit to leave unchanged. |

## VisibleEditor

### `twincat_open_object`

VISIBLE: show any Solution-Explorer node in the XAE editor or tree. A PLC source object (POU/GVL/DUT/INTERFACE) opens its own editor tab, behind the user's work unless bringToFront=true. A system-manager node (EtherCAT/IO, Motion/NC, TcCOM) or a Scope node is activated in the tree like a double-click (sysman nodes share one editor); that needs the foreground, so without bringToFront=true it is an [Error] and nothing is selected. Write tools do not open the editor; call this before a write to show it. Returns one line (tab opened or node selected) and, with bringToFront, whether the XAE reached the foreground (Windows may refuse; not an error).

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path, e.g. 'TIPC^PLC^PLC Projekt^POUs^MAIN' or 'TIID^Device 1 (EtherCAT)'; for Scope the Solution-Explorer names, '<scope project>^<scope node>'. |
| `bringToFront` | `bool` | `false` |  |

### `twincat_close_object`

Save and close a PLC object's editor tab (counterpart of twincat_open_object). A system-manager node has no own tab: reported no-op, not an error. Returns one line.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `treePath` | `string` |  | Caret-separated tree path, as for twincat_open_object. |

## Visu

### `twincat_visu_list_element_types`

List the visu element types and properties the visu tools support, as JSON. Per type: whether it can be created, its properties (name, kind integer/text/expression/color, meaning) and what is not supported. Needs no XAE.

_No parameters._

### `twincat_visu_read`

Read a PLC visualization (.TcVIS) as structured JSON. Returns name, file, interface (VAR_IN_OUT declaration), extent, revision, whether the tools may write it, and every element with id, type, position, size, texts, colours, colour/invisible/movement/rotation/tap/toggle variables, input actions and a Frame's visualizations and parameters. Unsupported elements show type, id and supported=false and are never changed. Absent properties use XAE's default.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `kind` | `string` | `""` |  |

### `twincat_visu_apply`

Create a PLC visualization or replace its whole page from JSON. Page: { "interface": "VAR_IN_OUT … END_VAR" (optional; kept when omitted), "elements": [ { "type": "Rectangle", "x": 10, "y": 10, "width": 120, "height": 40, "text": "Step %d", "textVariable": "MAIN.fb.nStep", "fillColor": "#C0FFC0", "toggleVariable": "MAIN.bManual" }, { "type": "Frame", "x": 10, "y": 60, "width": 160, "height": 70, "visualization": "VISU_Station", "parameters": { "fb": "MAIN.fbStation1" } }, … ] }. Types: Rectangle, RoundedRectangle, Ellipse, Line, Label, TextField, Button, Frame (properties: twincat_visu_list_element_types). A new element needs x, y, width, height; a Frame also its visualization and a value for every VAR_IN_OUT of that visualization's interface. { "copyFrom": <id>, … } copies an element of these types and applies the other properties. Every existing element is replaced — Polygons and unsupported elements are lost (twincat_visu_edit_elements keeps them). A missing path creates the visualization (parent: a folder or the PLC project node). Element ids are reassigned and listed in the result.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  | The last part is the visualization's name. |
| `kind` | `string` | `""` |  |
| `page` | `string?` | `null` | Page JSON (see the tool description). Or pageFile. |
| `pageFile` | `string?` | `null` | File holding the page JSON. Or page. |
| `expectedRevision` | `string?` | `null` | Revision from twincat_visu_read; a different current revision refuses the write. |

### `twincat_visu_edit_elements`

Add, change or remove single elements of an existing PLC visualization. Every other element, also unsupported ones, stays as it is. operations is a JSON array run in order, ids from twincat_visu_read: { "op": "add", "element": { "type": "Label", "x": 10, "y": 60, "width": 100, "height": 20, "text": "Ready" } }, { "op": "add", "element": { "copyFrom": 4, "x": 300, "parameters": { "fb": "MAIN.fbStation2" } } }, { "op": "update", "id": 3, "set": { "fillColor": "#FF0000", "invisibleVariable": "NOT MAIN.bShow" } } (null resets a property to XAE's default), { "op": "remove", "id": 5 }. A Frame takes "visualization" and "parameters" as in twincat_visu_apply; a Polygon can only be removed. The whole request is validated before anything changes.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `path` | `string` |  |  |
| `kind` | `string` | `""` |  |
| `operations` | `string?` | `null` | JSON array of operations (see the tool description); may be empty when only interface is given. |
| `@interface` | `string?` | `null` | Optional new VAR_IN_OUT interface declaration, e.g. "VAR_IN_OUT\ \tfb : FB_Station;\ END_VAR". |
| `expectedRevision` | `string?` | `null` | Revision from twincat_visu_read; a different current revision refuses the write. |

## Workflow

### `twincat_deploy`

Build, activate and check in one call. Fixed order: build (PLC project, or the whole solution with buildSolution), expectSymbols, target check, twincat_activate_config with a restart of TwinCAT into the new configuration, an ADS check that the target's system reads Run, the PLC runtime reads Run (put into Run over ADS when it does not and startPlc is true), then optionally lifeSign conditions (as twincat_ads_wait_until). The first step that fails ends the run and nothing after it starts: build errors are never activated, and an activation that cannot be shown to have taken effect is never followed by a PLC start. A human is asked once for the whole run, not per step; requestKey makes a retry after a timeout safe. Returns JSON { success, amsNetId, steps: [{ step, ok, durationMs, message }], warnings }; a failure answers '[Error] twincat_deploy stopped at step …' followed by the same JSON with notRun. With restart=false only the registry is written (a warning says the PLC keeps its old code). The target is the open solution's. Needs an open solution.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `plcProjectName` | `string?` | `null` | PLC project name (e.g. 'PLC') for the PLC build. Empty = first .plcproj. Not together with buildSolution. |
| `buildSolution` | `bool` | `false` | Build the whole solution (PLC code and IO mapping) instead of the PLC project only. Default false. |
| `ignoreStaleFiles` | `bool` | `false` | Build despite PLC files changed on disk outside the XAE (as in twincat_build_plc). Default false refuses. |
| `expectSymbols` | `string[]?` | `null` | Names that must exist in the compiled PLC (as in twincat_build_plc), e.g. ['MAIN.fbAxis']. A missing name ends the run before the activation. |
| `restart` | `bool` | `true` | Restart TwinCAT into the new configuration (forcibly restarts the PLC; the activation's dialog watcher confirms the restart prompt). Default true. False: registry only, no deploy. |
| `autoGenerateLicenses` | `bool` | `false` | Answer the trial-license prompt with 'Ja' (as in twincat_activate_config). Default false. |
| `autoConfirmLogin` | `bool` | `false` | Confirm the 'application changed since the last download' login modal (as in twincat_activate_config). Default false. |
| `loginMode` | `string` | `"online_change"` | Answer for that modal: 'online_change' (default) \| 'download' \| 'login_without_change'. |
| `updateBootProject` | `bool` | `true` | On download, tick 'Bootapplikation aktualisieren'. Default true. |
| `allowDownloadFallback` | `bool` | `false` | loginMode='online_change' only: download instead if online change is impossible (stops the PLC). Default false. |
| `runTimeoutSeconds` | `int?` | `null` | Seconds to wait for the system to read Run after the restart. Default 60, clamped to 5..300. |
| `startPlc` | `bool` | `true` | Put the PLC runtime into Run over ADS when it does not read Run after the activation. Default true; false makes that a failed step. |
| `adsPort` | `int` | `851` | ADS port of the PLC runtime for the state check and the life sign. Default 851. |
| `lifeSign` | `string[]?` | `null` | Conditions that prove the PLC program runs, as twincat_ads_wait_until takes them, e.g. ['MAIN.nCycle changed']. Up to 8. Omit to skip. |
| `lifeSignTimeoutMs` | `int` | `10000` | How long the lifeSign conditions may take, in ms. Default 10000, at most 300000. |

### `twincat_release_check`

Read-only release checklist: one traffic light per technical finding. Items: build state (Error List against the newest PLC source), Check all objects, static analysis (last report), libraries installed and pinned, required licenses against the target, boot project (files), PLC runtime state, open XAE dialogs. It never builds, saves or starts anything. What cannot be read without doing so is 'unknown' (never green) and names the tool that would show it: Check all objects and the static analysis compile and save, so only a report left by an earlier run is read. Returns JSON { success, overall, green, yellow, red, unknown, solution, items: [{ id, title, status, summary, details }] }; overall is the worst item (red, then unknown, then yellow). It lists findings, it does not judge the hand-over. Needs an open solution; with an XAE dialog blocking, the reads that need the XAE are unknown.

_No parameters._

## XaeDialogs

### `twincat_xae_dialogs`

List the XAE's open dialogs with id, texts, buttons and a safety class. Also: title, whether it blocks the XAE; class 'known-safe', 'unknown' or 'known-destructive' (confirms a target-system, delete, safety or license action; a person answers those in the XAE). Use it when a call reports the XAE busy or blocked by a dialog; when several block, answer the one with enabled buttons first. Answers while another call holds the XAE; changes nothing.

_No parameters._

### `twincat_xae_dialog_answer`

Press a button of an open XAE dialog listed by twincat_xae_dialogs. Refused for a known-destructive dialog and for a destructive button (activate, restart, download, login, delete, overwrite, …) on any dialog: a person answers those. Reports whether the dialog closed and whether another followed. Works while another call is blocked by the dialog.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `id` | `string` |  | The dialog's id from twincat_xae_dialogs, e.g. '0x1A2B3C'. |
| `button` | `string` |  | Button caption as listed, e.g. 'Neu laden'. |
