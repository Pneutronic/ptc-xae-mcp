using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_get_error_list", ReadOnly = true)]
    [Description("Error List: read-only snapshot of the XAE Error List window (the 'Fehlerliste' tool window) — " +
                 "severity, description, file, line per entry. " +
                 "Does NOT trigger a build and does NOT clear anything — unlike twincat_clear_error_list, whose " +
                 "cleared-count was previously the only way to learn how many entries existed. " +
                 "Requires XAE running with a solution open.")]
    public string GetErrorList(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_error_list", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).GetErrorList()), cancellationToken);

    [McpServerTool(Name = "twincat_clear_error_list", ReadOnly = false, Destructive = false)]
    [Description("Clear all entries in the XAE Error List window (the 'Löschen' / 'Clear' toolbar action). " +
                 "Useful before a new build to remove accumulated stale diagnostics that would otherwise " +
                 "obscure the current build output. Returns the cleared-entry count (best-effort; -1 if unavailable) " +
                 "and a status string. Requires XAE running.")]
    public string ClearErrorList(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_clear_error_list", null, () =>
            bridge.Dispatcher.Run(() =>
            {
                var r = bridge.Dispatcher.Session.ClearErrorList();
                return r.Success ? r.Message : $"[Error] {r.Message}";
            }), cancellationToken);

    [McpServerTool(Name = "twincat_build_plc", ReadOnly = false, Destructive = false)]
    [Description("Compile only the PLC project via DTE.Solution.SolutionBuild.BuildProject. " +
                 "Faster than a full solution build for PLC-code-only changes. " +
                 "Returns { success, failedProjects, errors[] } where success is derived from LastBuildInfo==0. " +
                 "Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). Requires XAE running with a solution open.")]
    public string BuildPlc(
        [Description("Name of the PLC project to build (e.g. 'PLC'). " +
                     "Leave empty to auto-detect the first .plcproj in the solution.")] string? plcProjectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_build_plc", new { plcProjectName }, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).BuildPlc(plcProjectName)), cancellationToken);

    [McpServerTool(Name = "twincat_build_solution", ReadOnly = false, Destructive = false)]
    [Description("Trigger a full solution build via DTE.Solution.SolutionBuild.Build. " +
                 "Compiles PLC code and IO mapping. " +
                 "Returns { success, failedProjects, errors[] } where success is derived from LastBuildInfo==0. " +
                 "Library references are checked first: a missing or wrongly versioned library prefixes the result with [Warn] Build result not conclusive (the build still runs). Requires XAE running with a solution open.")]
    public string BuildSolution(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_build_solution", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).BuildSolution()), cancellationToken);

    [McpServerTool(Name = "twincat_activate_config", ReadOnly = false, Destructive = true)]
    [Description("Activate the TwinCAT configuration: ITcSysManager::CheckAllObjects() then ActivateConfiguration(). " +
                 "IMPORTANT: ActivateConfiguration() alone is only 'Save To Registry' — it does NOT physically run the new " +
                 "config, so the PLC keeps the OLD code (root cause of issue #136). Set autoConfirmRestart=true to also call " +
                 "StartRestartTwinCAT() so the runtime reboots into the new config + boot project (this IS the deploy for the " +
                 "'neu aktivieren' path; it forcibly restarts the PLC). Without it, only the registry is written. " +
                 "With autoConfirmRestart the result is 'Activated' ONLY when the target's TwinCAT system reads Run afterwards: " +
                 "an ADS ReadState on port 10000 of the project's target NetId (the local NetId for a Local target) that reads " +
                 "Run once the restart has taken effect (the target was not in Run, or was seen leaving it) and holds it for 1 s, " +
                 "within runTimeoutSeconds (default 60). The restart call returning is not success. " +
                 "An XAE box raised meanwhile (e.g. 'Zielsystem meldet fatalen Fehler' / " +
                 "'target reports fatal error') ends the wait with '[Error] Activation failed: <box text> …'. An OK-only box is " +
                 "closed with its OK button ('(the XAE box was closed)'), so the XAE is never left modal; a box that asks a " +
                 "question is never answered and stays open. A deadline without Run returns an [Error] with the last state read. " +
                 "The target is the project's: call twincat_set_target first to activate on a specific runtime. " +
                 "Set autoGenerateLicenses=true to answer the trial-license prompt with 'Ja'; the following security-code " +
                 "dialog is typed only when its code is machine-readable — on TC 4026.x it is not, so the dialog is captured " +
                 "once and CANCELLED (activation must never hang) and the result is an [Error] that names the three-step flow " +
                 "(twincat_open_license_manager_and_request_trial → twincat_generate_trial_licenses → twincat_submit_security_code). " +
                 "Without autoGenerateLicenses an unexpected trial-license prompt is declined and reported as an [Error]. " +
                 "Dialog automation only ever touches windows of the attached XAE, never other applications. " +
                 "Set autoConfirmLogin=true to auto-confirm the 'Applikation … seit dem letzten Download geändert' login modal " +
                 "if it appears (loginMode = 'online_change' | 'download' | 'login_without_change'; updateBootProject ticks " +
                 "'Bootapplikation aktualisieren'). If online change is not possible for the change, the modal is cancelled and " +
                 "the result is an [Error] unless allowDownloadFallback=true (a download stops the running PLC). To deploy to an ALREADY-RUNNING PLC without a full restart, prefer " +
                 "twincat_login (online-change/download). The system reading Run does not prove the PLC application is up " +
                 "on its own port: verify the new code via an ADS symbol probe (twincat_read_ads_variable with amsNetId). " +
                 "Requires XAE running with a solution open.")]
    public string ActivateConfig(
        [Description("Also call StartRestartTwinCAT() to physically restart TwinCAT into the new config (forcibly restarts the PLC) and confirm the restart dialog. Success then requires the target's system state to read Run (see runTimeoutSeconds). Default false (registry only — no deploy).")] bool autoConfirmRestart = false,
        [Description("Answer the trial-license prompt with 'Ja'. An unreadable security code (TC 4026.x) is never guessed: the dialog is cancelled and the three-step licence flow is named in the [Error]. Default false.")]   bool autoGenerateLicenses = false,
        [Description("Automatically confirm the 'application changed since last download' login modal if it appears. Default false.")] bool autoConfirmLogin = false,
        [Description("Login strategy when the app-changed modal appears: 'online_change' | 'download' | 'login_without_change'. Default 'online_change'.")] string loginMode = "online_change",
        [Description("When confirming via download, also tick 'Bootapplikation aktualisieren' so the new code survives a runtime restart. Default true.")] bool updateBootProject = true,
        [Description("loginMode='online_change' only: when online change is not possible, confirm a full download instead (stops the running PLC). Default false — the modal is cancelled and an [Error] returned.")] bool allowDownloadFallback = false,
        [Description("autoConfirmRestart only: seconds to wait after the restart for the target's TwinCAT system (ADS port 10000) to read Run. Default 60, clamped to 5..300.")] int? runTimeoutSeconds = null,
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
    [Description("Read the licenses of the project's TARGET system from its TwinCAT license server over ADS (AMS port 30) — " +
                 "what the XAE's License editor shows. Returns per license: status (Present / Trial / Expired / Missing / Unknown), " +
                 "order number, name, id, expiry and used/count. The target must answer over ADS (a Local target is this PC). " +
                 "Requires XAE running with a solution open.")]
    public string GetLicenseInfo(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_license_info", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).GetLicenseInfo()), cancellationToken);

    [McpServerTool(Name = "twincat_list_required_licenses", ReadOnly = true)]
    [Description("List the licenses currently marked as required in the TIRC^License node " +
                 "(the 'Manage Licenses' / LicenseRequest section). " +
                 "These are the licenses that will be included in the next trial-license generation. " +
                 "Use twincat_add_required_license to add a license before calling " +
                 "twincat_open_license_manager_and_request_trial. " +
                 "Requires XAE running with a solution open.")]
    public string ListRequiredLicenses(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_required_licenses", null, () =>
            bridge.Dispatcher.Run(() => new BuildCoordinator(bridge.Dispatcher.Session).ListRequiredLicenses()), cancellationToken);

    [McpServerTool(Name = "twincat_add_required_license", ReadOnly = false, Destructive = false)]
    [Description("Mark a license as required in the TIRC^License node so it is included in " +
                 "the next trial-license generation. " +
                 "Provide licenseId as a GUID (e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}') — required; an " +
                 "orderNo alone is rejected because the .tsproj <ManualSelect> list only takes GUIDs. A missing " +
                 "<Licenses><Target> section is created; the entry is read back from the .tsproj after the reload. " +
                 "Idempotent: already-present licenses return success without change. " +
                 "Call twincat_open_license_manager_and_request_trial after to generate the trial. " +
                 "Requires XAE running with a solution open.")]
    public string AddRequiredLicense(
        [Description("License GUID, e.g. '{854588BA-345B-43F2-9354-0CF7AFF76EA3}'. Required.")] string? licenseId = null,
        [Description("Order number, e.g. 'SAMPLE-001'. Not sufficient on its own (cannot be resolved to a GUID).")] string? orderNo = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_required_license", new { licenseId, orderNo }, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).AddRequiredLicense(licenseId, orderNo)), cancellationToken);

    [McpServerTool(Name = "twincat_remove_required_license", ReadOnly = false, Destructive = true)]
    [Description("Remove a license from the required list in the TIRC^License node. " +
                 "Provide licenseId (GUID) or orderNo — at least one is required. " +
                 "Idempotent: absent licenses return success without change. " +
                 "Requires XAE running with a solution open.")]
    public string RemoveRequiredLicense(
        [Description("License GUID to remove. Optional if orderNo is supplied.")] string? licenseId = null,
        [Description("Order number to remove, e.g. 'SAMPLE-001'. Optional if licenseId is supplied.")] string? orderNo = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_required_license", new { licenseId, orderNo }, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).RemoveRequiredLicense(licenseId, orderNo)), cancellationToken);

    [McpServerTool(Name = "twincat_open_license_manager_and_request_trial", ReadOnly = false, Destructive = true)]
    [Description("Navigate to SYSTEM > License in TwinCAT XAE via the COM Automation Interface, " +
                 "then click the 'Aktiviere 7 Tage Testlizenz' / '7 Days Trial License...' button. " +
                 "Uses Win32 child-window search — works for Win32/WinForms License Manager controls. " +
                 "If the button is in a WPF view without a native HWND the click will not land and the " +
                 "response explains what to do manually. " +
                 "After a successful click call twincat_generate_trial_licenses to handle " +
                 "the confirmation and security-code dialogs. " +
                 "Requires XAE running with a solution open.")]
    public string OpenLicenseManagerAndRequestTrial(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_open_license_manager_and_request_trial", null, () =>
            bridge.Dispatcher.Run(() =>
                new BuildCoordinator(bridge.Dispatcher.Session).OpenLicenseManagerAndRequestTrial()), cancellationToken);

    [McpServerTool(Name = "twincat_generate_trial_licenses", ReadOnly = false, Destructive = true)]
    [Description("Handle the 'Some required runtime licenses missing — Generate trial licenses?' dialog. " +
                 "Step 1: clicks 'Ja' (Yes) on the trial-license confirmation dialog. " +
                 "Step 2: waits up to 10 s for the 'Sicherheitscode eingeben' security-code dialog " +
                 "and returns a screenshot of it as an embedded PNG image. " +
                 "Read the displayed 5-character code from the image, then call twincat_submit_security_code(code). " +
                 "Does NOT require a COM session — all interaction is Win32-only. " +
                 "Call twincat_open_license_manager_and_request_trial first if the confirmation dialog " +
                 "is not yet visible.")]
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
    [Description("Enter a known security code into the open 'Sicherheitscode eingeben' dialog and click OK. " +
                 "Use this after twincat_generate_trial_licenses returns an image: " +
                 "read the displayed 5-character code from the embedded PNG, then pass it here. " +
                 "Waits up to 2 s for the post-activation success popup and dismisses it automatically. " +
                 "The dialog must already be visible on screen. " +
                 "Does NOT require a COM session — pure Win32.")]
    public string SubmitSecurityCode(
        [Description("The 5-character code shown in the security-code dialog (e.g. 'bW5Rm').")] string code,
        CancellationToken cancellationToken = default)
        // Dispatcher bypass (issue #150, C-6): pure Win32, traced; allow-listed in McpToolResultContractTests.
        => bridge.Activity.Trace("twincat_submit_security_code", new { code }, () =>
            new BuildCoordinator(bridge.Dispatcher.Session).SubmitSecurityCode(code), cancellationToken);

    [McpServerTool(Name = "twincat_dismiss_security_dialog", ReadOnly = false, Destructive = true)]
    [Description("Locate the open 'Sicherheitscode eingeben' (security-code entry) dialog, " +
                 "read the displayed 5-character code from child window texts, type it into the edit field, and click OK. " +
                 "NOTE: on TC3 4026.22 the code is custom-drawn and NOT extractable from child texts — " +
                 "use twincat_generate_trial_licenses (which takes a screenshot) followed by " +
                 "twincat_submit_security_code(code) instead. " +
                 "Does NOT require a COM session — pure Win32, and it deliberately does not wait for the " +
                 "operation lock: the dialog it answers is what a running activation is blocked on. " +
                 "Requires the dialog to already be visible on screen.")]
    public string DismissSecurityDialog(CancellationToken cancellationToken = default)
        // Dispatcher bypass (issue #150, C-6): pure Win32 — must be callable while activate_config holds the lock.
        => bridge.Activity.Trace("twincat_dismiss_security_dialog", null, () =>
            new BuildCoordinator(bridge.Dispatcher.Session).DismissSecurityDialog(), cancellationToken);
}
