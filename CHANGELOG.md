# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Release notes with the full list
of pull requests are on the GitHub Releases page.

Versions up to 2.1.0 were developed in earlier repositories; their issue and
pull-request numbers refer to those.

## [Unreleased]

### Added
- **A call held by an XAE dialog no longer hangs.** While a tool runs, the server looks every 0.5 s for an XAE dialog
  that blocks the XAE and asks something (a button other than Cancel; progress and wait windows do not count). When
  one stays for 3 s, the call answers `[Blocked] XAE dialog '<title>' (id …, <class>; buttons: …)` and names what to do:
  `twincat_xae_dialog_answer` or the XAE, and only a person for a known-destructive dialog. The XAE call itself keeps
  running; until it returns no other call starts, and each one is refused with `[Error]` naming the dialog and the next
  step (`twincat_xae_dialogs` and `twincat_xae_dialog_answer` still work). When the XAE call returns, its result is
  written to the activity log as `<tool> (late result)` and the next call can run. `twincat_xae_dialogs` names the call
  that waits for a dialog. Calls from the window's own buttons behave as before. `PTC_XAE_MCP_DIALOG_WATCH=off`
  switches this off (every call then waits for the XAE as before).
- `ptc_test_hold_xae_dialog` (only with `--diagnostics`): a test hook for the live suite that opens Tools > Options
  inside a COM call, so a call held by an XAE dialog can be tested.

## [3.1.0] - 2026-11-06

### Added
- **Tool profiles per client.** Every tool belongs to one toolset (`meta`, `project`, `build`, `plc`, `ads`,
  `runtime`, `license`, `io`, `hmi`, `scope`, `nc`, `safety`, `ibn`). A client sees the toolsets of its profile:
  `all`, or `core` (`meta`, `project`, `build`, `plc`, `ads`, `runtime`, about half the tools) for clients that cope
  badly with many tools. The profile is chosen by the client's self-reported name (Claude: `all`; Cursor, VS Code,
  Copilot, Windsurf: `core`; any other client: `all`), unless the window (Settings → Tool profile) or
  `PTC_XAE_MCP_TOOLSETS` (`all`, `core` or a list such as `plc,hmi`) sets one for every client. A call to a hidden
  tool is refused with `[Error]` naming its toolset. `twincat_get_client_info` and `/health` show the profile in force.
- **Access policy.** An access level — `read` (only tools that change nothing), `engineer` (also the project:
  PLC code, IO tree, mapping, libraries, build, save) or `runtime` (also target systems: activate, login, PLC
  start/stop, ADS writes, routes, target, system mode, User Mode Runtime, trial licenses) — plus blocked NetIds (no
  runtime action on them, reading stays allowed) and allowed NetIds (runtime actions only on them; `local` = this PC
  and its User Mode Runtimes). Set in the window (Settings, saved to `%LOCALAPPDATA%\ptc-xae-mcp\policy.json`) or
  with `PTC_XAE_MCP_ACCESS`, `PTC_XAE_MCP_BLOCKED_NETIDS`, `PTC_XAE_MCP_ALLOWED_NETIDS`; when both are set the
  stricter wins. No tool can change it, and the file tools cannot reach `policy.json`. It is checked before every
  tool call, also from the tool console; tools above the level are left out of the tool list, and connected clients
  are told to reload it when the window changes the policy. Emergency stops (`twincat_stop`, `twincat_shutdown`,
  `twincat_force_close_xae`, the IBN abort/disarm/release) are never blocked. Without any setting nothing changes
  (`runtime`, no lists). A restricted level is named in the window title; `twincat_get_status` shows the policy.
- `twincat_plc_list(limit, offset)` and `twincat_get_error_list(minLevel, limit, offset)`: page through a large
  project or a long Error List; the last line then gives `total`, `has_more` and `next_offset`. `minLevel` keeps only
  errors or errors and warnings. Without these arguments the output is unchanged.
- `twincat_plc_read(paths)`: reads up to 50 elements of one kind in a single call, each under a `=== <path> ===`
  line; an element that cannot be read is reported as its `[Error]` while the others are still read.

- **MCP prompts**: step-by-step workflows a user picks in the client (in Claude Code as a slash command):
  `plc-change` (find, read, change with revision, build with `expectSymbols`, check), `commission-ethercat`,
  `diagnose-runtime` (reads only), `run-tcunit`, `scope-measure`, `doctor`. A prompt about a toolset the client's
  profile hides is not listed.
- **MCP resources** to pull context into the chat without a tool call (in Claude Code with `@`):
  `ptc-xae://status`, `ptc-xae://plc/elements`, `ptc-xae://plc/element/{path}` (one element's text and revision,
  path URI-encoded), `ptc-xae://build/errors`, `ptc-xae://toolsets`. Each read goes through the matching tool, so
  the access policy, the single-operation lock and the activity log apply.

- `twincat_xae_dialogs` (read-only) lists the dialogs the XAE has open — id, title, texts, buttons, whether it
  blocks the XAE — and classifies each as `known-safe`, `unknown` or `known-destructive` (confirms an activation,
  restart, download, login, delete, safety or license action). `twincat_xae_dialog_answer(id, button)` presses a
  button and reports whether the dialog closed and whether another followed; it refuses a known-destructive dialog
  and a destructive button on any dialog — a person answers those in the XAE. Both use Win32 and UI Automation (so
  also the shell's WPF prompts such as "save changes?"), no COM, and answer while another call is blocked by the
  dialog (verified live with the reload prompt for a file changed on disk and with a "save changes?" prompt stacked
  on a Go To dialog).

### Changed
- The most frequent errors name the next step on a second line `Next: …`: no XAE, no solution open, a PLC path that
  does not exist (with how to find the real, localized path).
- `twincat_plc_read`: `path` and `kind` are no longer marked required in the schema (because of `paths`); a call
  without either path is refused as before.

### Fixed
- A failed `twincat_goto_line` / `twincat_show_step` no longer leaves the XAE modal. When the Go-To dialog did not
  lead to a confirmed cursor, the XAE could re-open the dialog after the verdict, and keys that reached the editor
  could open "Variable deklarieren"; both stayed open and blocked every later call (seen in a live run). For a few
  seconds after a failure every Go-To dialog is now cancelled and any other non-destructive dialog that blocks the
  XAE is closed with its Cancel button; the result names what was cancelled.

## [3.0.0] - 2026-11-06

### Breaking
- **Removed the tools the `twincat_plc_*` verbs replaced** (they answered with a deprecation notice since 2.4.0).
  An MCP client that still calls one gets "unknown tool":

  | Removed | Use instead |
  |---|---|
  | `twincat_list_pous`, `twincat_list_pou_members` | `twincat_plc_list` (`kind`, `parentPath`) |
  | `twincat_read_pou`, `twincat_read_gvl`, `twincat_read_pou_member` | `twincat_plc_read(path, kind)` |
  | `twincat_write_pou_body`, `twincat_write_gvl`, `twincat_write_pou_member` | `twincat_plc_write(path, kind, …)` |
  | `twincat_create_pou`, `twincat_create_dut`, `twincat_create_gvl`, `twincat_create_interface`, `twincat_add_pou_member` | `twincat_plc_create(kind, subtype, parentPath, name, …)` |
  | `twincat_delete_plc_object`, `twincat_rename_plc_object` | `twincat_plc_delete`, `twincat_plc_rename` |
  | `twincat_open_plc_object`, `twincat_close_plc_object` (diagnostics) | `twincat_open_object`, `twincat_close_object` |

- **Removed the parameter aliases** `v1`, `v2`, `offs1`, `offs2` of `twincat_link_variables` /
  `twincat_unlink_variables` (use `variable1TreePath`, `variable2TreePath`, `bitOffset1`, `bitOffset2`) and `netId`
  of `twincat_add_route` / `twincat_set_target` (use `amsNetId`, now required).
- **An argument a tool does not declare is refused** with `[Error]` and the tool's parameters, before the tool runs.
  It used to be dropped without a word. Names compare case-insensitively.

### Added
- `twincat_plc_check_all_objects` (#27): "Check all objects" of a PLC project compiles every object, also functions,
  FBs and GVLs no task's program reaches — `twincat_build_plc` compiles only what a task reaches, so an uncalled
  function with errors builds with 0 errors, and in a library project nothing is checked. Runs through the
  Automation Interface (`ITcPlcIECProject2.CheckAllObjects`) and leaves the XAE behind the user's work; clears the
  Error List first, runs the build preflights and lists the errors with file and line. Only an XAE without that call
  needs the XAE command, which selects the PLC project and brings the XAE to the front: `bringToFront=true`.
- `twincat_plc_search` (read-only): searches the declarations and implementations of every POU, member, INTERFACE,
  DUT and GVL instead of reading element by element — literal or regular expression, case-insensitive by default,
  `wholeWord`, `scope`, `pathPrefix`, `kind`, paged with `limit`/`offset`. Each hit names element, section, line and
  column as `twincat_plc_read` counts them, the element's revision and an excerpt. Graphical implementations are
  listed as skipped.
- `twincat_plc_replace_text`: replaces one exact piece of a declaration or implementation and leaves the rest
  byte-identical. Not found (with where it nearly matched), found several times (with the lines), a stale
  `expectedRevision`, a missing section or a graphical implementation are refused before the automatic logout; the
  result shows the changed lines and the new revision.
- `twincat_plc_move` (#31): moves a POU, INTERFACE, DUT or GVL into another folder of the same PLC project, or a
  METHOD, ACTION, PROPERTY or TRANSITION into a folder inside its POU/INTERFACE, keeping the object GUIDs. The
  element (for a member: its POU) is exported as PLCopen XML, placed in the target folder in the file's project
  structure, deleted and imported into the same PLC project — no clipboard, the XAE stays in the background. A wrong
  kind, a missing or non-folder target, the current folder, a name already in the target and a member target outside
  its POU are refused before the logout. The move is verified by read-back (new path, old path gone, unchanged text
  of the element and every member, unchanged object GUIDs); a failure after the delete re-imports the original into
  its old folder. The exports are kept under `%LOCALAPPDATA%\ptc-xae-mcp\move`.
- `twincat_plc_project_info` (#28): reads the Project Information of a PLC project (Title, Version, Company, Author,
  DefaultNamespace, Description, Released) and sets the fields that are given; a library needs the Title. Every set
  field is read back before the project is saved; a version that is not four numbers is refused before the write.
- `twincat_save_as_library` saves a compiled library (#30): an `outputPath` ending in `.compiled-library` goes
  through `ITcPlcIECProject6.SaveAsLibrary2`, `.library` stays the source library. New `encrypt` (compiled library
  only) and `overwrite`. The result reports the size of the file read back; a refusal for a missing Title names
  `twincat_plc_project_info`.
- `twincat_plc_task_create` (#32): a real-time task under TIRT with cycle time and priority, the PLC task reference
  linked to it and optionally the PROGRAM it calls. A name in use, a priority another task has, a cycle time that is
  not a positive multiple of 100 ns and an unknown PLC or PROGRAM are refused before anything changes; without
  `priority` the next free one is taken. Cycle time, priority and link are read back; a failed later step names
  what was already created.
- `twincat_library_signatures` (read-only): the public API of the referenced libraries — function blocks,
  functions, types and global lists with inputs, outputs and constants — as the XAE serialises it
  (`ITcPlcLibraryManager2.ProduceAllLibrarySignatures`), written to a file outside the project. `typeName` returns
  one type's signature inline, `library` narrows to one library, `plcProjectName` picks the PLC; referenced
  libraries missing from the export are named.
- `twincat_plc_static_analysis`: runs the PLC Static Analysis (TE1200) through the Automation Interface and reports
  the findings compactly — counts per level, the most frequent rules, and level, rule, file, line and message per
  finding (`minLevel`, `limit`/`offset`). Read from the SARIF export (TwinCAT 3.1.4026.20 or later, outside the
  project); an XAE that cannot export SARIF is answered from the Error List's SA entries with the reason.
- `twincat_doctor` (read-only, answers while another call holds the XAE): MCP version, TwinCAT System Service, local
  AMS NetId, TcXaeShell instances, a dialog blocking the XAE, the open solution and the allowed file-path roots, each
  with ok / warn / fail and the next step. Never starts an XAE or a runtime; an XAE that does not answer within 5 s
  is reported.
- `twincat_tcunit_results(junitPath)`: also writes the results as a JUnit XML file for a CI server.
- `twincat_plc_edit_entries(expectedRevision)`: refuses the edit when the element changed since it was read; the
  result ends with the new revision.

### Changed
- `twincat_save_as_library` refuses an `outputPath` with another extension than `.library` or `.compiled-library`,
  and an existing file unless `overwrite=true` (which deletes it first), before anything is saved.
- Tool descriptions and error messages that pointed to a removed tool name the `twincat_plc_*` replacement.

## [2.6.1] - 2026-11-05

### Added
- `twincat_plc_create` takes `implementation` (#22): the body of a new POU, METHOD, ACTION, TRANSITION, GET or SET
  is written right after it was created and read back like `twincat_plc_write`. Before, a body passed along was
  dropped without a word. For an element without a body (DUT, GVL, a PROPERTY itself) it is an `[Error]` before
  anything is created.
- `plcProjectName` on `twincat_list_library_references`, `twincat_check_libraries_installed`,
  `twincat_add_library_reference`, `twincat_remove_library_reference` and `twincat_set_library_version` (#24): the
  PLC (by its name or its project name) whose references are meant. Before, the live path acted on the first PLC
  project and the file path refused a solution with several PLC projects as ambiguous.

### Changed
- **The XAE is no longer brought to the foreground on its own.** `twincat_open_object`,
  `twincat_plc_read(openInEditor)`, `twincat_goto_line` and `twincat_show_step` raised it in front of the user's
  work on every call; they now take `bringToFront` (default false) for when a person asks where something is.
  Without it a PLC object opens behind the user's work. **Callers of `twincat_goto_line`, `twincat_show_step` and
  of `twincat_open_object` on a system-manager or Scope node must pass `bringToFront=true`:** without it these are
  an `[Error]` unless the XAE already is in front, because the XAE comes forward by itself when it shows them
  (selecting such a node in the Solution Explorer, expanding a collapsed node, focusing an editor pane).
- `twincat_plc_rename` of a POU or member (#23) refuses a new name that is already a variable of the element or,
  for a member, of its POU (the XAE accepts it and only the next build fails), and warns when the body still
  assigns the return value to the old name.

### Fixed
- File paths (e.g. `twincat_save_as_library`'s `outputPath`) may lie anywhere in the git repository that holds the
  open solution, not only below the solution's folder (#26): a solution in `<repo>\Source\<project>\` could not
  save its library to `<repo>\Library\`.
- `twincat_add_library_reference(asPlaceholder=true)` adds the placeholder (#25). It failed for every library with
  "Default Placeholder '<name>, * (<distributor>)' could not be found", because the whole resolution was passed
  as the placeholder name.

## [2.6.0] - 2026-11-05

### Added
- `expectSymbols` on `twincat_build_plc`, `twincat_build_solution`, `twincat_rebuild_plc` and
  `twincat_rebuild_solution`: after the build every name is looked up in the PLC project's `.tmc` — a symbol, a
  data type, a program or GVL, or a member path into an instance such as `MAIN.fbAxis.nState` (inherited members
  included). A missing name makes the result an `[Error]` that says why: TwinCAT compiles only what a task's
  program reaches, unless `{attribute 'linkalways'}` is set, so 0 errors do not prove that new code was compiled.
- `twincat_login(mode='online_change')` verifies the change on the PLC: it reads
  `TwinCAT_SystemInfoVarList._AppInfo.OnlineChangeCnt` over ADS before and after the login and the first task's
  cycle counter twice afterwards. `onlineChange.verdict` is `applied`, `not_needed`, `not_applied` (the result is
  then `success=false`), `download` or `unknown`; `onlineChange.plcCycling` says whether the application runs.
- `twincat_plc_read` reports a revision (a hash of declaration and implementation);
  `twincat_plc_write(expectedRevision)` refuses the write, before the automatic logout, when someone else changed
  the element since it was read. The write result names the new revision.
- `twincat_tcunit_results` (read-only, online over ADS): waits until the TcUnit runner has stored its results,
  then returns `verdict` (`passed`, `failed`, `no_tests`, `unknown`), totals, suites and the failed test cases
  with their messages (`includePassed` adds the passed ones). Values are read by variable handle with known types.
  It warns when online changes followed the download, because TcUnit runs its tests only once after the start.

### Changed
- Build results list errors and warnings in full but only the first ten messages, with a note how many more
  the Error List holds. The XAE's Error List keeps every message since its start; hundreds of device-description
  notices had pushed the compiler verdict out of the result.

### Fixed
- `twincat_logout` and `twincat_login` report the state the XAE settles in. When the XAE rejected a call
  while it was still logging in or out, the whole tool was retried: a logout it had just done came back as
  "already offline — no-op" (`wasOnline=false`), and a login could execute the Login command a second time.
  Now only the rejected call is retried, and the result waits until the same login state is read twice in a
  row (the XAE shows both commands disabled or rejects calls for seconds in between). The login dialog
  watcher stays up until then, so confirmations that follow the login dialog are answered too.
- The PLC Login/Logout commands are looked up by their last known name instead of walking every XAE
  command: `twincat_get_plc_online_state` and the logout before every PLC write take well under a second
  instead of several seconds.
- The 32-bit XAE shell is found in the 32-bit registry view.
- `amsNetId="project"` with a Local target (the `.tsproj` has no `TargetNetId`) resolves to the local system
  instead of refusing.

## [2.5.0] - 2026-11-02

### Added
- `twincat_read_ethercat_startup` (read-only, offline): the complete Startup list of EtherCAT boxes as the
  Startup tab shows it — CoE, SoE, EoE and FoE mailbox commands, both the entries TwinCAT generates (fixed, in
  angle brackets in the tab) and the projected ones. `twincat_read_coe_startup` sees only CoE and reports 0
  entries for a SoE drive (Rexroth IndraDrive, Beckhoff AX5000). SoE entries come decoded: IDN in `S-0-0015` /
  `P-0-3231` notation, DriveNo, element, value (2/4/8 data bytes), SoE list header and the IDNs of the AT/MDT
  lists; EoE adds the virtual port (MAC, IP, subnet, gateway, DNS). Without `treePath` every box below every
  EtherCAT master is read and only boxes with entries are listed; `includeFixed` and `protocol` filter.
- `twincat_read_soe_parameter` (read-only, online over ADS): reads SoE parameters (IDNs) live from a SoE drive.
  With `idns` it returns those IDNs at a `driveNo` with the requested elements (value, name, attribute, unit, min,
  max, default, data status); with the attribute the value is interpreted (display format, decimal places, write
  protection). Without `idns` it reads every SoE entry of the box's Startup list from the drive and compares it
  with the project (projected, actual, match; AT/MDT lists without their maximum length). An ADS error of one
  IDN is recorded on that IDN; a drive that does not answer is an error with NetId and port. The SoE element
  parameter of the existing SoE reader is generalised (default unchanged).

### Fixed
- No XAE crash from the remaining tree lookups on a referenced stand-alone PLC project (`.tspproj`), the
  follow-up to 2.4.0 (#14). Every lookup of a path below such a PLC (`TIPC^<plc>^<project>^...`) on the main
  System Manager now walks the tree child by child instead of calling `LookupTreeItem`: the schema apply
  (`twincat_apply_schema`), the GVL writer and the linker, which look paths up themselves, as well as the
  instance settings, the link diagnosis and `twincat_set_device_disabled`. An inline PLC keeps the single
  direct lookup. The library tools were not affected: they look the `References` node up on the stand-alone
  project's own System Manager, where that lookup is safe (verified live on a referenced project).
- `twincat_apply_schema` links the IO of a referenced stand-alone PLC project. Paths below the instance's own
  `<plc> Instance` node (the process image, `PlcTask Inputs`/`Outputs`) are looked up directly again: that node
  lives in the main System Manager, the lookup there is safe, and the image folders are reachable by path only,
  so the walk added in 2.4.0 answered "no image folder found". After the build the apply also reloads the
  stand-alone project's TMC into that instance (`ReloadTmc`), so the new GVL is in its process image before the
  linker looks for it.

### Changed
- `setup\build-winget-manifest.ps1` takes the `ReleaseDate` from the GitHub release (publication date) instead of the day
  it is run, and `-Verify` compares the SHA-256 with the digest GitHub reports for the release asset before writing.
- `Microsoft.Data.Sqlite.Core` 9.0.21 → 9.0.22.

## [2.4.0] - 2026-10-28

### Added
- Opt-in update check in the host window: *Menu → Check for updates…* and, in *Settings…*, *Check for a new release at start* (off by default, `checkForUpdates` in `ui.json`). It sends one `HEAD` request to the GitHub latest-release URL, compares the version in the redirect and reports the result in the run log, the header and the menu (which then opens the release page). Nothing is downloaded or installed, and there is no MCP tool for it. Administrators switch it off for the machine with the policy `HKLM\SOFTWARE\Policies\Pneutronic\ptc-xae-mcp\DisableUpdateCheck=1`; `PTC_XAE_MCP_UPDATE_CHECK=0` does the same for one process. See SECURITY.md, "Update check (opt-in)".
- `setup\build-winget-manifest.ps1` writes the three winget manifests (`Pneutronic.XaeMcp`) for the installer and `.sha256` built by `build-installer.ps1`, ready for a pull request to microsoft/winget-pkgs. Once published, `winget install Pneutronic.XaeMcp` and `winget upgrade Pneutronic.XaeMcp` install and update the app with the hash check done by winget.

### Changed
- README, "No telemetry": the update check is the one documented, opt-in exception to "no network connections of its own".

### Fixed
- The PLC element tools (`twincat_plc_read`, `_write`, `_create`, `_delete`, `_rename`), the POU member tools
  and the editor tools that open an element no longer crash the XAE when the PLC is a referenced stand-alone
  project (`.tspproj`). For such a PLC (its instance node has no `NestedProject`) a path below the instance
  (`TIPC^<plc>^<project>^...`) is walked child by child from `TIPC`, the way `twincat_plc_list` enumerates,
  with the project root taken from the stand-alone project's own System Manager, instead of being passed to
  `LookupTreeItem`, which hung for tens of seconds, answered "PLC tree path not found" and then took
  TcXaeShell.exe down with 0xc0000005 in `TwinCAT System Manager.x64.dll`. An inline PLC keeps the single
  direct lookup, so nothing changes there. Reported and analysed in #9 (GLTR94). The library tools still look
  the `References` node up directly and keep the crash on a referenced project.

## [2.3.0] - 2026-10-27

### Added
- `twincat_open_project` takes `host` (`auto` | `64` | `32`, default `auto`). `64` opens a project whose TcVersion selects the 32-bit XAE in the 64-bit XAE on purpose; the result says which host was chosen and why, and the TcVersion in the `.tsproj` is not rewritten. The call is refused, before anything is opened, when the requested XAE is not installed or an XAE of the other bitness is already running (the MCP never runs two XAEs).
- After a switch to Run, `twincat_set_system_mode` and `twincat_umr_set_mode` also read the state of the PLC runtime on AMS port 851 and give it 15 s to report Run. If it does not, the result still succeeds (the system is in Run) but carries a `[Warn]` naming what the PLC reads and the usual cause (`Autostart Boot Project` off, boot project not started).

### Changed
- `twincat_open_project` names the XAE host (32-bit or 64-bit TcXaeShell) that the project's `TcVersion` selects, and warns when the project resolves to the 32-bit XAE although the 64-bit XAE is installed, or when the running XAE is not the selected one.
- A tool that fails with a binding error on the XAE ("… does not contain a definition for …") now appends the attached XAE host and the usual cause, a solution open in an XAE of the wrong bitness. The NC tools (`twincat_list_nc_tasks_axes`, `twincat_create_nc_task`, `twincat_create_axis`, `twincat_configure_axis`, `twincat_read_axis_params`) use the same error path as the other tools.

### Fixed
- `twincat_get_status` names the XAE that is actually attached instead of the one the project's TcVersion would select. A 4024 project opened on purpose in the 64-bit XAE was reported as "32-bit"; the line now says which shell runs and adds "but TcVersion selects the 32-bit XAE" when they differ.
- `twincat_get_status` reports `XAE: Not active` once the shell has exited. It used to keep the last snapshot ("Active", the old project and host) until the next open or close.

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

[Unreleased]: https://github.com/Pneutronic/ptc-xae-mcp/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v3.1.0
[3.0.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v3.0.0
[2.6.1]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.6.1
[2.6.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.6.0
[2.5.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.5.0
[2.4.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.4.0
[2.3.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.3.0
[2.2.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.2.0
[2.1.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.1.0
[2.0.1]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.0.1
[2.0.0]: https://github.com/Pneutronic/ptc-xae-mcp/releases/tag/v2.0.0
