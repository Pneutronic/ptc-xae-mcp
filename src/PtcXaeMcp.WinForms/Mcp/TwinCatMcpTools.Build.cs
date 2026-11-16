using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_error_list", ReadOnly = true)]
    [Description("Read the XAE Error List: severity, text, file and line per entry. " +
                 "Does not build or clear (that is twincat_clear_error_list). Identical entries are listed once with a " +
                 "'(2× in the Error List)' note; the first line gives total and distinct count. " +
                 "The list also shows runtime EventLogger events, sometimes twice; for alarm diagnosis use " +
                 "twincat_read_event_logger. When paging, the last line gives total, has_more and next_offset. " +
                 "Needs an open solution.")]
    public string GetErrorList(
        [Description("Lowest severity kept: 'error', 'warning' or 'message'. Default: all.")] string? minLevel = null,
        [Description("Max distinct entries; 0 (default) = all.")] int limit = 0,
        [Description("Entries to skip (next_offset of the previous page). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_error_list", new { minLevel, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).GetErrorList(minLevel, limit, offset)), cancellationToken);

    [McpServerTool(Name = "twincat_clear_error_list", ReadOnly = false, Destructive = false)]
    [Description("Clear all entries of the XAE Error List, for instance before a build. " +
                 "Returns the cleared count (-1 if unknown). Needs a running XAE.")]
    public string ClearErrorList(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_clear_error_list", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var r = bridge.Dispatcher.Session.ClearErrorList();
                return r.Success ? r.Message : $"[Error] {r.Message}";
            }), cancellationToken);

    [McpServerTool(Name = "twincat_build_plc", ReadOnly = false, Destructive = false)]
    [Description("Compile only the PLC project, faster than a full solution build. " +
                 "Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong " +
                 "version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the " +
                 "XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could " +
                 "overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. " +
                 "With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only " +
                 "objects a task's program reaches are compiled). Needs an open solution.")]
    public string BuildPlc(
        [Description("PLC project name (e.g. 'PLC'). Empty = first .plcproj.")] string? plcProjectName = null,
        [Description("Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip.")] string[]? expectSymbols = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_build_plc", new { plcProjectName, ignoreStaleFiles, expectSymbols }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).BuildPlc(plcProjectName, ignoreStaleFiles, expectSymbols)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_check_all_objects", ReadOnly = false, Destructive = false)]
    [Description("Run 'Check all objects': compile every PLC object, also ones no task reaches. " +
                 "twincat_build_plc compiles only what a task reaches, so errors in uncalled code (or a whole library project) " +
                 "go unnoticed; use this to verify a library or code not called yet. Clears the Error List first. Same preflights " +
                 "as twincat_build_plc (stale files refuse, library references warn). An XAE lacking the automation call needs " +
                 "the menu command, which brings the XAE to the front: only with bringToFront=true, else [Error]; the result " +
                 "says which way ran. Result: 'Check all objects: no errors' or '[Error] Check all objects failed.' with the " +
                 "errors and file/line. Needs an open solution.")]
    public string PlcCheckAllObjects(
        [Description("PLC name (e.g. 'PLC') or its project name. Empty = first PLC project.")] string? plcProjectName = null,
        [Description("Check despite PLC files changed on disk outside the XAE (checks the in-memory state; result starts with [Warn]). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Allow the XAE to come to the front if the menu-command fallback is needed. Default false.")] bool bringToFront = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_check_all_objects", new { plcProjectName, ignoreStaleFiles, bringToFront }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).CheckAllObjects(plcProjectName, ignoreStaleFiles, bringToFront)), cancellationToken);

    [McpServerTool(Name = "twincat_build_solution", ReadOnly = false, Destructive = false)]
    [Description("Build the whole solution: PLC code and IO mapping. " +
                 "Returns { success, failedProjects, errors[] }. Library references are checked first (missing or wrong " +
                 "version: '[Warn] Build result not conclusive', the build still runs). PLC files changed on disk outside the " +
                 "XAE since loading refuse the build with [Error] (it would compile the stale in-memory state and saving could " +
                 "overwrite the change): reload with twincat_open_project(closeCurrentFirst=true) or set ignoreStaleFiles. " +
                 "With expectSymbols a name missing from the compiled PLC makes the result an [Error] that says why (only " +
                 "objects a task's program reaches are compiled). Needs an open solution.")]
    public string BuildSolution(
        [Description("Build despite PLC files changed on disk outside the XAE (builds the in-memory state; result starts with [Warn]). Default false refuses.")] bool ignoreStaleFiles = false,
        [Description("Names that must exist in the compiled PLC, checked case-insensitively in its .tmc, e.g. ['MAIN.fbAxis', 'GVL_Recipe.nCount', 'ST_Recipe']: symbol paths (also members), data types, or a program/GVL name. Functions and FB types without an instance never appear. Omit to skip.")] string[]? expectSymbols = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_build_solution", new { ignoreStaleFiles, expectSymbols }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).BuildSolution(ignoreStaleFiles, expectSymbols)), cancellationToken);

    [McpServerTool(Name = "twincat_activate_config", ReadOnly = false, Destructive = true)]
    [Description("Activate the TwinCAT configuration on the project's target. " +
                 "Call twincat_set_target first to choose another runtime. Without autoConfirmRestart only the registry is " +
                 "written: nothing is deployed and the PLC keeps its OLD code. With it TwinCAT restarts into the new config and " +
                 "boot project (forcibly restarting the PLC); the result is 'Activated' only when the target's system state " +
                 "(ADS port 10000) reads Run after the restart and holds it 1 s within runTimeoutSeconds, else an [Error] with " +
                 "the last state. An XAE error box meanwhile (e.g. 'Zielsystem meldet fatalen Fehler') ends the wait with " +
                 "'[Error] Activation failed: <box text>'; OK-only boxes are closed with OK, boxes that ask a question are never " +
                 "answered. A trial-license prompt is declined with [Error] unless autoGenerateLicenses. Only windows of the " +
                 "attached XAE are touched. To deploy to an already running PLC without a restart prefer twincat_login. Run " +
                 "does not prove the PLC application is up: verify with twincat_read_ads_variable (amsNetId). " +
                 "Needs an open solution.")]
    public string ActivateConfig(
        [Description("Restart TwinCAT into the new config (forcibly restarts the PLC; confirms the restart dialog). Default false = registry only, no deploy.")] bool autoConfirmRestart = false,
        [Description("Answer the trial-license prompt with 'Ja'. An unreadable security code (TC 4026.x) is never guessed: the dialog is cancelled and the [Error] names the flow twincat_open_license_manager_and_request_trial → twincat_generate_trial_licenses → twincat_submit_security_code. Default false.")]   bool autoGenerateLicenses = false,
        [Description("Confirm the 'Applikation … seit dem letzten Download geändert' login modal if it appears. Default false.")] bool autoConfirmLogin = false,
        [Description("Answer for that modal: 'online_change' (default) | 'download' | 'login_without_change'.")] string loginMode = "online_change",
        [Description("On download, tick 'Bootapplikation aktualisieren' so the code survives a runtime restart. Default true.")] bool updateBootProject = true,
        [Description("loginMode='online_change' only: if online change is impossible, download instead (stops the running PLC). Default false cancels the modal with [Error].")] bool allowDownloadFallback = false,
        [Description("autoConfirmRestart only: seconds to wait for Run. Default 60, clamped to 5..300.")] int? runTimeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_activate_config",
            new { autoConfirmRestart, autoGenerateLicenses, autoConfirmLogin, loginMode, updateBootProject, allowDownloadFallback, runTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
            {
                LoginMode mode = loginMode switch
                {
                    "online_change"        => LoginMode.OnlineChange,
                    "download"             => LoginMode.Download,
                    "login_without_change" => LoginMode.LoginWithoutChange,
                    _ => throw new ArgumentException(
                        $"Unknown loginMode '{loginMode}'. " +
                        "Valid values: online_change, download, login_without_change."),
                };
                return new BuildCoordinator(bridge.Dispatcher.Session).ActivateConfig(
                    autoConfirmRestart, autoGenerateLicenses, autoConfirmLogin, mode, updateBootProject, allowDownloadFallback,
                    runTimeoutSeconds);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_get_license_info", ReadOnly = true)]
    [Description("Read the licenses of the project's target system over ADS (AMS port 30). " +
                 "As the License editor shows them: status (Present / Trial / Expired / Missing / Unknown), order number, name, " +
                 "id, expiry, used/count. The target must answer over ADS (Local = this PC). Needs an open solution.")]
    public string GetLicenseInfo(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_license_info", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).GetLicenseInfo()), cancellationToken);

    [McpServerTool(Name = "twincat_list_required_licenses", ReadOnly = true)]
    [Description("List the licenses marked as required for the next trial-license generation. " +
                 "Add one with twincat_add_required_license before twincat_open_license_manager_and_request_trial. " +
                 "Needs an open solution.")]
    public string ListRequiredLicenses(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_required_licenses", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).ListRequiredLicenses()), cancellationToken);

    [McpServerTool(Name = "twincat_add_required_license", ReadOnly = false, Destructive = false)]
    [Description("Mark a license as required so the next trial-license generation includes it. " +
                 "licenseId is required; an orderNo alone is rejected. Writes the .tsproj license list (created if missing) and " +
                 "reads it back. Idempotent. Then call twincat_open_license_manager_and_request_trial. Needs an open solution.")]
    public string AddRequiredLicense(
        [Description("License GUID, e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}'. Required.")] string? licenseId = null,
        [Description("Order number, e.g. 'SAMPLE-001'. Not sufficient on its own.")] string? orderNo = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_required_license", new { licenseId, orderNo }, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).AddRequiredLicense(licenseId, orderNo)), cancellationToken);

    [McpServerTool(Name = "twincat_remove_required_license", ReadOnly = false, Destructive = true)]
    [Description("Remove a license from the required-license list. " +
                 "Give licenseId or orderNo. Idempotent. Needs an open solution.")]
    public string RemoveRequiredLicense(
        [Description("License GUID. Optional if orderNo is given.")] string? licenseId = null,
        [Description("Order number, e.g. 'SAMPLE-001'. Optional if licenseId is given.")] string? orderNo = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_required_license", new { licenseId, orderNo }, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).RemoveRequiredLicense(licenseId, orderNo)), cancellationToken);

    [McpServerTool(Name = "twincat_open_license_manager_and_request_trial", ReadOnly = false, Destructive = true)]
    [Description("Open SYSTEM > License in the XAE and click the 7-day trial license button. " +
                 "If the click cannot land (WPF view), the result says what to do by hand. Then call " +
                 "twincat_generate_trial_licenses for the confirmation and security-code dialogs. Needs an open solution.")]
    public string OpenLicenseManagerAndRequestTrial(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_open_license_manager_and_request_trial", null, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).OpenLicenseManagerAndRequestTrial()), cancellationToken);

    [McpServerTool(Name = "twincat_generate_trial_licenses", ReadOnly = false, Destructive = true)]
    [Description("Answer the trial-license prompt with 'Ja' and screenshot the security code. " +
                 "Waits up to 10 s for the 'Sicherheitscode eingeben' dialog and returns it as a PNG image; read the " +
                 "5-character code and call twincat_submit_security_code(code). Needs no open solution. If the prompt is not " +
                 "visible yet, call twincat_open_license_manager_and_request_trial first.")]
    public IEnumerable<ContentBlock> GenerateTrialLicenses(CancellationToken cancellationToken = default)
    {
        // Dispatcher bypass (issue #150, C-6): pure Win32, traced; allow-listed in McpToolResultContractTests.
        byte[]? png = null;
        var msg = bridge.Activity.Trace("twincat_generate_trial_licenses", null, () =>
        {
            var (m, p) = new BuildCoordinator(bridge.Dispatcher.Session).GenerateTrialLicenses();
            png = p;
            return m;
        }, cancellationToken);
        var blocks = new List<ContentBlock> { new TextContentBlock { Text = msg } };
        if (png is not null)
            blocks.Add(ImageContentBlock.FromBytes(png, "image/png"));
        return blocks;
    }

    [McpServerTool(Name = "twincat_submit_security_code", ReadOnly = false, Destructive = true)]
    [Description("Enter the trial-license security code into its open dialog and click OK. " +
                 "The 'Sicherheitscode eingeben' dialog must be visible; take the code from the twincat_generate_trial_licenses " +
                 "image. Closes the success popup that follows (waits up to 2 s). Needs no open solution.")]
    public string SubmitSecurityCode(
        [Description("The 5-character code, e.g. 'bW5Rm'.")] string code,
        CancellationToken cancellationToken = default)
        // Dispatcher bypass (issue #150, C-6): pure Win32, traced; allow-listed in McpToolResultContractTests.
        => bridge.Activity.Trace("twincat_submit_security_code", new { code }, () =>
            new BuildCoordinator(bridge.Dispatcher.Session).SubmitSecurityCode(code), cancellationToken);

    [McpServerTool(Name = "twincat_dismiss_security_dialog", ReadOnly = false, Destructive = true)]
    [Description("Read, type and confirm the code of the open security-code dialog. " +
                 "Works only when the 'Sicherheitscode eingeben' dialog shows the code as window text; on TC3 4026.22 it is " +
                 "drawn and cannot be read — use twincat_generate_trial_licenses, then twincat_submit_security_code. Does not " +
                 "wait for other calls, so it works while an activation is blocked on the dialog. The dialog must be visible.")]
    public string DismissSecurityDialog(CancellationToken cancellationToken = default)
        // Dispatcher bypass (issue #150, C-6): pure Win32 — must be callable while activate_config holds the lock.
        => bridge.Activity.Trace("twincat_dismiss_security_dialog", null, () =>
            new BuildCoordinator(bridge.Dispatcher.Session).DismissSecurityDialog(), cancellationToken);
}
