# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Release notes with the full list
of pull requests are on the GitHub Releases page.

Versions up to 2.1.0 were developed in earlier repositories; their issue and
pull-request numbers refer to those.

## [Unreleased]

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
