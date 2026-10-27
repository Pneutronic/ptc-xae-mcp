# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Release notes with the full list
of pull requests are on the GitHub Releases page.

Versions up to 2.1.0 were developed in earlier repositories; their issue and
pull-request numbers refer to those.

## [Unreleased]

### Added
- `twincat_open_project` takes `host` (`auto` | `64` | `32`, default `auto`). `64` opens a project whose TcVersion selects the 32-bit XAE in the 64-bit XAE on purpose; the result says which host was chosen and why, and the TcVersion in the `.tsproj` is not rewritten. The call is refused, before anything is opened, when the requested XAE is not installed or an XAE of the other bitness is already running (the MCP never runs two XAEs).
- After a switch to Run, `twincat_set_system_mode` and `twincat_umr_set_mode` also read the state of the PLC runtime on AMS port 851 and give it 15 s to report Run. If it does not, the result still succeeds (the system is in Run) but carries a `[Warn]` naming what the PLC reads and the usual cause (`Autostart Boot Project` off, boot project not started).

## [2.2.1] - 2026-10-27

### Changed
- `twincat_open_project` names the XAE host (32-bit or 64-bit TcXaeShell) that the project's `TcVersion` selects, and warns when the project resolves to the 32-bit XAE although the 64-bit XAE is installed, or when the running XAE is not the selected one.
- A tool that fails with a binding error on the XAE ("… does not contain a definition for …") now appends the attached XAE host and the usual cause, a solution open in an XAE of the wrong bitness. The NC tools (`twincat_list_nc_tasks_axes`, `twincat_create_nc_task`, `twincat_create_axis`, `twincat_configure_axis`, `twincat_read_axis_params`) use the same error path as the other tools.

## [2.2.0] - 2026-10-23

### Added
- Persistent snapshot: capture, compare and restore the remanent data (PERSISTENT and RETAIN) of a
  running PLC over ADS. New tools `twincat_list_ads_symbols`, `twincat_persistent_snapshot_save`,
  `_compare`, `_restore` and `_restore_status`. The remanent leaves come from the compiled `.tmc` (as the
  compiler resolved them) or from the runtime's `IsPersistent` flags; the snapshot is a flat JSON file
  with a per-leaf type (arrays by declared index). A restore is a dry run by default; with
  `dryRun=false` it only raises a proposal. Nothing is written until a human confirms the new popup in the
  Approvals view, which shows the PLC state (RUN/STOP) and the differences. After the confirm the host
  re-plans against the PLC, writes with ADS sum commands and reads every leaf back. There is no MCP tool
  that confirms.
- `twincat_ipc_read_inventory` reads the Device Manager (MDP over ADS, AMS port 10000, index group 0xF302) of a Beckhoff IPC: BTN/BIC, every module with its tables (TwinCAT version and System ID, installed Beckhoff software, OS, mainboard, disks, NICs, CPU, memory), enumerated dynamically, read-only. A target without Device Manager answers with a structured `mdpUnavailable`.
- `twincat_ethercat_read_identities` reads the online identity (VendorId, ProductCode, RevisionNo, SerialNo), state and link of every EtherCAT slave from the master over ADS and joins it with the projected boxes by fixed address. The master answers on port 0xFFFF of the NetId from the device XML. The master reports serial 0 for most slaves, so by default (`includeCoe`) the CoE device info (name, hardware and firmware version, serial 0x1018:04) is read per slave through the master. With `includeDeviceDetails` (default true, needs `includeCoe`) each slave also reports its BIC/BTN (CoE 0x10E2), its safety identity (CoE 0xF980) with an effective unique safety identifier (0xF980:02, BTN, 0x1018:04, or the host drive serial for AX5805/AX5806; a safety device without any is flagged `safetySerialMissing` and warned about, and `safetyIdentityComplete` turns false), the OCT motor and feedback name plate (CoE 0x9009/0x9008) and, for AX5000 drives, the SoE parameters per channel (firmware, hardware, serial, configured type, safety option, digital motor name plate). The result carries summary counts. All masters under TIID by default; a master that does not answer is a structured `masterUnreachable`. Read-only.
- `twincat_list_library_repositories` lists the XAE library repositories (name, folder). With `includeFiles` every library reference of the PLC project is resolved to a concrete library (effective resolution of the library manager, fixed version, or newest installed) and to its file(s) in the repository, each with its SHA-256. If the library manager does not expose its repositories, the standard managed-library folders are listed instead (`source: filesystem-fallback`, with a warning).
- `twincat_list_library_references` shows, per reference, the concrete library it resolves to (`resolvedName`, `resolvedVersion`, `resolvedDistributor`) and how that was determined (`resolution`: effective, fixed, newest-installed, unresolved). The text output gets a `[resolved: ...]` suffix on each line; the new optional `format: "json"` returns the full records.
- Both inventory tools take `timeoutMs` (default 5000) and repeat a read once on an ADS timeout. Slaves or masters that could not be read completely are reported (`readStatus`, status `partial`) instead of being left out; a safety device whose identity could not be read is `safetyIdentityUnknown`, kept apart from `safetySerialMissing`. EtherCAT bridges such as the EL6695 are reported as `blackChannel`, not as safety devices. A relative master NetId (`0.0.0.0.x.y`) takes the target's first four octets; passive boxes (end caps, the CPU's own E-bus coupler) are marked `passive`.
- `twincat_reload_device_descriptions` makes the XAE re-read the EtherCAT device descriptions (ESI files under `Config\Io\EtherCAT`) by raising its "Reload Device Descriptions" command by GUID/ID, so it works on any XAE language. A vendor ESI placed there is otherwise unknown to the XAE until it is restarted, and `twincat_add_ethercat_box` fails for its type with "Invalid item sub type". A dialog the XAE shows is reported, never answered.
- `twincat_read_scope_file` (experimental) reads a saved scope recording (.svdx) without the XAE through the official `TC3ScopeExportTool.exe` (TE130x Scope View / TF3300 Scope Server), run headless: per channel name, symbol, comment, data type, sample time, NetId/port, scaling, sample count, first/last time and min/max/mean; with `includeValues` the values of selected channels in a time window, decimated to min/max per bucket so spikes survive (`maxPoints`, default 500). Times are ms since the recording start, start/end UTC.
- `twincat_convert_scope_file` (experimental) converts a .svdx into an export file (format from the target extension) with the same tool; an existing target is refused unless `overwrite=true`.

### Changed
- Every build (`twincat_build_plc`, `_rebuild_plc`, `_build_solution`, `_rebuild_solution`) checks the PLC library references first: a missing or wrongly versioned library prefixes the result with `[Warn] Build result not conclusive` (the build still runs).
- A build refuses with `[Error]` when a PLC source file (.TcPOU/.TcGVL/.TcDUT/.TcIO) changed on disk outside the XAE since the project was loaded: the XAE would build its in-memory state, and the build's save could write it over the change. The files are compared with the declaration/implementation texts the XAE holds, so a save from the XAE never counts. `ignoreStaleFiles=true` builds anyway with `[Warn]`; reload with `twincat_open_project(closeCurrentFirst=true)`.
- `twincat_open_project` checks every `File="….xti"` reference (devices, boxes, axes, PLC projects) before the XAE loads anything and refuses a project with missing files, which would open broken and damage the .tsproj when saved. `allowMissingReferences=true` opens with `[Warn]`; save and close-with-save repeat it.
- `twincat_link_variables` names the cause when TwinCAT refuses a link. For a mismatch it reports type, bit size and direction of both variables, with a PDO hint for differing types and a direction hint for two inputs (or two outputs) on the same side; for a missing path it names the path that does not resolve, for `TSM_E_INVALIDITEMTYPE` the path that is no variable. Without readable variable definitions the message stays the plain one, plus the HRESULT name.
- `twincat_add_ethercat_box` says, when it cannot create a box, whether the ESI for that type is on disk (reload the device descriptions, then retry) or absent (place the vendor's ESI first).
- `twincat_get_error_list` lists identical entries (level, text, file, line) once with a `(2× in the Error List)` note and gives the total and the distinct count; its description points alarm diagnosis at `twincat_read_event_logger`.

### Fixed
- `amsNetId="project"` (and every tool that reads the project's TargetNetId) found no target when a `.tsproj` had been opened directly, because the generated solution lives in another folder. The solution's projects are now asked for their `.tsproj` paths as well.

## [2.1.0] - 2026-10-21

### Changed
- The build and the installer no longer ship Microsoft's `envdte`, `envdte80`, `stdole`,
  `Microsoft.VisualStudio.Interop` or `Microsoft.VisualStudio.Imaging.Interop.14.0.DesignTime`
  assemblies. They are licensed for use with Visual Studio only. The host now loads them at
  runtime from the TcXaeShell installation, which every XAE machine already has (64-bit shell
  first, then the 32-bit XAE). `setup\build-installer.ps1` refuses a publish folder that contains
  any of them (#3, #17).
- `twincat_umr_set_mode` switches the UMR over ADS on the instance's NetId (WriteControl on
  port 10000, as `twincat_set_system_mode`) and succeeds only once ReadState confirms the mode,
  about 22 s on a UMR. New optional `timeoutSeconds` (default 60, 5..300). If the instance's NetId
  is unknown, it now returns an `[Error]` and sends nothing (#15, #16).
- IBN Live: ABORT, RELEASE-ALL and Disarm (GUI buttons and `twincat_ibn_abort` /
  `_release_all` / `_disarm`) no longer wait for the dispatcher lock. They now work while a
  build or an activation is running, instead of answering "busy". A coordinator-level I/O gate
  keeps them ordered with the other IBN writes, and the stop paths also write
  `bStepReq:=FALSE` (#1, #14).
- Dependencies: ModelContextProtocol.AspNetCore 1.4.1, Beckhoff.TwinCAT.Ads 7.0.389,
  Microsoft.Data.Sqlite.Core 9.0.21, plus test-only packages (#8).

### Fixed
- `twincat_umr_set_mode` reported "Run" while the system stayed in Config: it only wrote `r`
  to the UMR's stdin (#15, #16).
- `twincat_umr_start` ignored a NetId fixed in the instance's `Start.bat` and always started
  with `-i path`, so the UMR came up with the NetId from its `TcRegistry.xml`. Anything
  addressing the fixed NetId then failed with `TargetMachineNotFound`. The result now names
  the effective NetId (#4, #13).
- IBN Live races closed (#1, #14):
  - An abort during the arm write is no longer undone.
  - A confirmed step request can no longer land after an abort.
  - A heartbeat can no longer re-enable after an abort.
  - The NetId resolved at arm time is reused, so the stop paths never touch COM.
- IBN Live: a proposal belongs to exactly one arm window. Re-arming rejects pending proposals,
  and a confirm checks the proposal's own expiry (#2, #14).
- `docs/mcp-capabilities.md` regenerated; it had missed `twincat_set_system_mode` (#16).

## [2.0.1] - 2026-10-21

First release built from the public repository.

### Added
- `twincat_set_system_mode {amsNetId, mode: config|run, timeoutSeconds?}` switches a target's
  TwinCAT system to Config or Run over ADS (WriteControl Reconfig / Reset on port 10000) and
  reports success only when ReadState confirms the mode. It closes the gap where
  `activate_config` cannot restart a runtime that is already running (#5, #6). Verified on a
  User Mode Runtime: Run → Config → Run in about 22 s each.
- `twincat_hmi_import_file`: when TE2000 reports an error although the file was added (seen for
  `.js`), the import only counts if the copy on disk is byte-identical (`engineError`,
  `verifiedOnDisk`). For `.js` and `.css` the result says how `tchmiconfig.json` lists the file
  in `dependencyFiles` and that `twincat_hmi_build` is needed (#7).
- The CI workflow can be started by hand from the Actions tab (#9).

### Changed
- Dependabot opens at most one grouped pull request per month for NuGet minor/patch, NuGet
  major and GitHub Actions; the CI actions moved to checkout 7, setup-dotnet 6 and
  upload-artifact 7.

### Fixed
- `THIRD-PARTY-NOTICES.md` no longer lists `tools/twincat-validator/`, which is not part of
  this repository; the validator is an optional, separately installed MCP server.

## [2.0.0] - 2026-10-20

Version 2.0 is the first release of the slim host window (introduced in 1.5.0) under the new
PTC brand icon. No MCP tool changed since 1.5.0.

### Changed
- New app icon: "PTC" in grey on white with the red bar over the C, as above the C of the
  PNEUTRONIC word mark (exe, window, installer). `tools/gen_app_icon.ps1` generates
  `favicon.ico` (16–256 px) without dependencies.
- The header logo is larger, and it opens https://pneutronic.ch; the menu has a
  "pneutronic.ch" entry.

### Fixed
- Dark theme (#300): the tab strip, header logo, check boxes, number fields, drop-down lists,
  panel scroll bars and every dialog (including the IBN and HMI confirm popups and their
  title bars) are now dark; before, parts of them stayed white or showed black text.

## 1.5.0 - 2026-10-20

The host window is rebuilt for the open-source release (tracking #275): one header and three
views instead of 16 tabs.

### Added
- **Console view**: run any MCP tool by hand through the real MCP path — search, a form
  generated from the input schema, raw JSON, favourites with a *Getting started* set,
  confirmation before every tool that is not read-only, *Run again* from the Activity view.
- **Approvals view**: IBN Live (arm, ABORT / RELEASE-ALL, countdown, audit log, a TopMost
  confirm popup without a default button) and the history of HMI write approvals; a badge
  on the tab and a header chip while something is pending.
- *Menu*: *Open sample project* (the neutral fixture ships with the app and is copied to
  Documents), *Connect a client…* (`.mcp.json` snippet, `claude mcp add` command, connection
  test), *Settings…* (theme, remembered window position; server settings shown with the
  environment variable that sets each).
- Every tool declares `readOnlyHint` / `destructiveHint` (MCP tool annotations).
- `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1), `SUPPORT.md`, `CHANGELOG.md`, issue and
  pull-request templates, `CODEOWNERS`, Dependabot configuration, a Developer Certificate of
  Origin requirement in `CONTRIBUTING.md`, a safety notice and a no-telemetry statement in
  the README, and `docs/oss-launch.md` with the repository settings for the public launch.
- `THIRD-PARTY-NOTICES.md` now lists the transitive Microsoft interop assemblies,
  `TcHmiAutomation.dll`, the test fixtures' provenance and the development tools under `tools/`.

### Changed
- **IBN Live is armed only in the host window.** `twincat_ibn_arm` is no longer offered to
  MCP clients; it remains test support on a `--diagnostics` host only.
- The MCP server starts on every launch, also on a plain double-click.
- The MCP tools no longer depend on the window (`ServerHost` owns the session, the lock and
  the shared services); `twincat_get_status` reads a status snapshot and no longer reports a
  schema state.
- The in-GUI test runner is gone; single-step and narration are shown only with `--diagnostics`.
- Status colours follow the theme (also after a theme switch); the window opens at a
  comfortable default size and remembers its position.
- `TcHmiAutomation.dll` (Beckhoff TE2000) is no longer copied into the build output or the
  installer. Like the EventLogger interop it is a compile-time reference only and is loaded at
  runtime from the local TE2000 installation (`-p:BundleBeckhoffDlls=true` restores the copy).
- The neutral HMI fixture shows a placeholder image instead of the Beckhoff logo from the
  TE2000 starter template; `tests/Fixtures/create_hmi_fixture.py` writes it.
- `setup/build-installer.ps1` no longer defaults to an internal team share; pass `-ShareDir`
  to compare against earlier builds.
- CI actions are pinned to commit SHAs and the workflow token is read-only.

### Fixed
- On a machine without TE2000, every `twincat_hmi_*` tool now fails with "TwinCAT HMI Engineering
  (TE2000) is not installed on this machine" instead of a bare assembly-load error.
- `TwinCatPaths.GetTwinCatDir` returned the install directory itself when the registry value
  ends with a separator; the TcXaeShell and Scope-template paths were only found through their
  hardcoded fallbacks, and the TE2000 directory not at all.

### Removed
- The tabs Schema, Lifecycle, Libraries, Fieldbus, PLC Code, IO Mapping, Build, ADS Vars,
  Measurement, Runtime, Motion, Safety, Core Dumps and HMI — every function remains an MCP
  tool and can be run from the Console view.
- Stale build logs (`test-baseline.txt`, `test-output.txt`) and the remaining customer and
  machine-specific references in docs, commands and tests.

## 1.4.0 - 2026-10-11

### Added
- `twincat_hmi_enable_runtime_tmc` / `twincat_hmi_disable_runtime_tmc`: server-side `USE_TMC`, so
  `ADS.<rt>.…` symbols map without a running PLC (#161, #258).
- Internal HMI symbols: `twincat_hmi_list_internal_symbols`, `twincat_hmi_add_internal_symbol`,
  `twincat_hmi_remove_internal_symbol`, `twincat_hmi_map_offline_symbol` (#165, #244).
- `twincat_hmi_list_plc_tmc_candidates` (#166, #237) and `twincat_list_ethercat_box_catalog` (#85, #236).
- Neutral HMI fixture for Stage HMI (#242); TMC attach/detach integration stage (#251).

### Changed
- `twincat_get_license_info` reads the target's license server over ADS (#55, #257).
- `twincat_open_project` also accepts `.tsproj` / `.tspproj` / `.hmiproj` (#229).
- `twincat_open_object` opens EtherCAT device editors and Scope views (#124, #255).
- `twincat_get_status` shows the TwinCAT System Service state; ADS tools refuse with a
  "reboot" hint while `TcSysSrv` is stopped (#231, #232).
- `twincat_plc_rename` reports whether callers were renamed (#239, #243, #256).
- `twincat_configure_ethercat_slave` reads the node back after `ConsumeXml` (#152, #235).
- Display name "Pneutronic XAE MCP" and a logo-based application icon (#228, #264).

### Fixed
- `activate_config`: late licence/restart dialogs are declined, `E_ACCESSDENIED` carries a hint (#230, #234).
- Login answers the "running application must be replaced" box (#204, #238).
- Required-license tools close the solution before editing the `.tsproj` (#246).
- Scope: Show-Title enum, read-only record mode, trigger-symbol limits, stacked-axes key (#201, #241).
- HMI: `.js` import result and `dependencyFiles` (#247, #253); `write_file` refuses
  `tchmiconfig.json` (#248); `map_symbol` refuses the engineering-only TMC domain (#160, #252).

## 1.3.1 - 2026-10-09

### Fixed
- `twincat_open_license_manager_and_request_trial` opens SYSTEM > License first (#226).

### Added
- `setup\build-installer.ps1` writes a `.sha256` next to the installer (#225).

## 1.3.0 - 2026-10-08

### Added
- Generic PLC object tools `twincat_plc_*` with a mandatory `kind` (#213, #214); the old
  `*_pou` / `*_gvl` / `*_pou_member` tools are deprecated.
- PLC project lifecycle (#216), PLC instance settings (#217), TwinCAT HMI tools (#196–#198),
  ADS STRING support, PLC start/stop and boot project (#143), version in the GUI (#223).

### Changed
- Central COM layer with busy detection, deadlines and modal-dialog detection (#171).
- Fail-closed offline guard, delete whitelist and read-back after every write (#148, #152, #175, #188).
- XAE dialogs are answered instead of blocking: rename refactoring (#218), save prompt (#219), Go-To (#177).
- The local AmsNetId is read from the AMS router (#221); `activate_config` reports success only
  when the target reads Run (#199).
- Apache-2.0 license; the commercial licensing system and its backend are removed (#173, #194).

## 1.2 - 2026-08-27

### Added
- Editor navigation: `twincat_locate_step`, `twincat_goto_line`, `twincat_show_step` (#139).

[Unreleased]: https://github.com/Pneutronic/ptc-xae-mcp/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.1.0
[2.0.1]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.0.1
[2.0.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.0.0
