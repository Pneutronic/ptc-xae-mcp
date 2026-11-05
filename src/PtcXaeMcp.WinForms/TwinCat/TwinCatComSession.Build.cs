using System.Runtime.InteropServices;
using EnvDTE80;
using PtcXaeMcp.WinForms.TwinCat.HostSelection;
using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // ---- Build / activate tools (feature/plc-build) -------------------------
    //
    // Build uses DTE.Solution.SolutionBuild. LastBuildInfo==0 is necessary but not
    // sufficient (issue #152 C-10): a PLC build succeeds only with this build's
    // compiler summary line, 0 errors in it and a fresh compile artifact — see
    // BuildTruth.FailureReason. The Error List is classified by ErrorLevel (C-19).
    //
    // vsBuildState values: 1=Done, 2=InProgress, 3=NotStarted
    // vsBuildErrorLevel (EnvDTE80): Low=1 (message), Medium=2 (warning), High=4 (error) — but on
    // TcXaeShell 4026.22 PLC compiler errors also arrive as 2 (issue #178, BuildTruth.ClassifyErrorItem).
    //
    // ActivateConfiguration and CheckAllObjects live on the ITcSysManager COM
    // object (sysMan). Verified empirically on TC3 4026.22: ActivateConfiguration
    // works standalone; CheckAllObjects is called first to surface validation
    // errors before activating (it does not block activation).

    private static readonly TimeSpan BuildPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ErrorReadTimeout = TimeSpan.FromSeconds(30);

    // The build compiles the solution's on-disk/in-memory project model. Any
    // unsaved tree edit made by a *previous* tool call (e.g. a just-written
    // POU body) can otherwise be built stale — the change silently doesn't
    // take, so an injected error fails to surface or a fix fails to apply.
    // Saving first makes build deterministic for every caller (Claude/MCP,
    // GUI, tests) — not just the ones that remember to save. Non-fatal: a
    // save failure must not block a build the user explicitly asked for.
    private static void SaveBeforeBuild(dynamic dte)
    {
        // ONLY File.SaveAll — it flushes dirty editors/project files in place.
        // Do NOT call Solution.SaveAs(FullName): on TC XAE that can detach and
        // reload the solution, leaving the project list momentarily empty so
        // the very next BuildProject reports "No .plcproj project found".
        try { dte.ExecuteCommand("File.SaveAll"); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
    }

    /// <summary>
    /// The ITcSysManager of <paramref name="dte"/>'s open solution, settle-polled like every
    /// session call. For the schema runner, which attaches through
    /// <see cref="AttachForExternalCaller"/> (issue #155 step 7).
    /// </summary>
    internal static object RequireSystemManagerOf(object dte) => RequireSystemManager(dte);

    /// <summary>Main window of the XAE behind <paramref name="dte"/> (dialog-watcher scope, issue #155 step 8).</summary>
    internal static IntPtr XaeMainWindowOf(object dte) => TryGetXaeMainWindow(dte);

    public BuildResult BuildSolution() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveBeforeBuild(dte);
        dynamic build = dte.Solution.SolutionBuild;
        var priorPanes = MeasureOutputPanes(dte);
        DateTime started = DateTime.UtcNow;
        build.Build(true);
        return MaterialiseBuildResult(dte, build, priorPanes, started, SolutionHasPlcProject(dte));
    }, BuildTimeout);

    public BuildResult BuildPlc(string? plcProjectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveBeforeBuild(dte);
        dynamic build = dte.Solution.SolutionBuild;

        // A bare SolutionBuild.BuildProject on the nested .plcproj compiles it in
        // ISOLATION — it skips the solution-level pass that regenerates TwinCAT's
        // implicit code (IO-mapping glue, linked-variable GVL stubs, auto library
        // refs). Without those artifacts the PLC reports spurious unresolved-symbol
        // errors that never occur in a full solution build or a manual XAE build.
        // CheckAllObjects() forces that regeneration first, mirroring what the IDE
        // does when you click "Build" inside the loaded solution context.
        // (Non-fatal: any validation errors are surfaced by the build itself.)
        object? sysObj = TryGetSystemManager(dte);
        if (sysObj is not null)
        {
            try { ((dynamic)sysObj).CheckAllObjects(); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }

        // Right after File.SaveAll / open the solution's project list can be
        // momentarily empty (settle lag), so the .plcproj lookup transiently
        // returns null. Poll briefly before declaring it missing — same family
        // of fix as RequireSystemManager / WaitMemberEnumerable.
        string? uniqueName = null;
        var unDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            uniqueName = plcProjectName is not null
                ? FindProjectUniqueName(dte, plcProjectName)
                : FindFirstPlcProjectUniqueName(dte);
            if (uniqueName is not null || DateTime.UtcNow >= unDeadline) break;
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }

        if (uniqueName is null)
            throw new InvalidOperationException(
                plcProjectName is not null
                    ? $"PLC project '{plcProjectName}' not found in solution."
                    : "No .plcproj project found in solution.");

        // BuildProject requires the SOLUTION-CONTEXT configuration name for this
        // exact project, not the solution's active-configuration display name and
        // not a guessed "Release". A mismatched config silently builds the wrong
        // (or an empty) context. Resolve it from the project's own SolutionContext;
        // only if that lookup fails do we fall back to the active config name.
        string? config = FindSolutionContextConfigName(dte, uniqueName);
        if (config is null)
        {
            try { config = (string)build.ActiveConfiguration.Name; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { config = "Release"; }
        }

        var priorPanes = MeasureOutputPanes(dte);
        DateTime started = DateTime.UtcNow;
        build.BuildProject(config, uniqueName, true);
        return MaterialiseBuildResult(dte, build, priorPanes, started, expectsPlcCompile: true);
    }, BuildTimeout);

    internal static readonly TimeSpan AfterFailureQuiet = TimeSpan.FromSeconds(6);
    internal static readonly TimeSpan AfterFailureMax = TimeSpan.FromSeconds(30);

    /// <summary>Issue #230: E_ACCESSDENIED alone told the caller nothing about what to do.</summary>
    internal static string AccessDeniedHint(Exception ex) =>
        ex.HResult == unchecked((int)0x80070005) || ex is UnauthorizedAccessException
            ? " E_ACCESSDENIED: the XAE is probably not running as administrator, or the target refused the restart. " +
              "Do not confirm a restart prompt by hand — seen live, it stopped the LOCAL TcSysSrv instead of the target " +
              "(only a reboot recovered it). Start the XAE elevated and check that the target answers over ADS first."
            : "";

    public ActivateResult ActivateConfig(
        bool autoConfirmRestart = false,
        bool autoGenerateLicenses = false,
        bool autoConfirmLogin = false,
        LoginMode loginMode = LoginMode.OnlineChange,
        bool updateBootProject = true,
        bool allowDownloadFallback = false,
        TimeSpan? runTimeout = null) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        object? sysObj = RequireSystemManager(dte);
        dynamic sysMan = sysObj;

        // Issue #195: with a restart, success is decided by the target's own system state, so find the
        // target first. This only reads, so a busy XAE here is still safe for the outer retry.
        ActivationTarget? target = autoConfirmRestart ? ResolveActivationTarget(sysObj) : null;
        TimeSpan deadline = runTimeout ?? ActivationWait.DefaultDeadline;

        try { sysMan.CheckAllObjects(); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* non-fatal: errors captured by ErrorList */ }

        // Start the Win32 dialog watcher before ActivateConfiguration() so it
        // is already polling when TC raises modal dialogs (the watcher thread
        // must not touch COM — Win32-only is safe from any thread). It always runs:
        // besides the opted-in confirmations it declines an unrequested trial-licence
        // prompt, which would otherwise block ActivateConfiguration() for good (#150).
        // Scoped to THIS XAE's windows — never the host GUI (#150, C-1…C-4).
        IntPtr xaeMain = TryGetXaeMainWindow(dte);
        var watcher = new Win32DialogAutomation(
            new Win32DialogAutomation.WatcherConfig(
                autoConfirmRestart, autoGenerateLicenses,
                autoConfirmLogin, loginMode, updateBootProject,
                AllowDownloadFallback: allowDownloadFallback,
                XaeMain: xaeMain));
        XaeWindowScope scope = XaeWindowScope.For(xaeMain);
        IReadOnlySet<IntPtr> modalsBefore = ActivationWindows.OpenModalHandles(scope);
        watcher.Start();

        using TargetStateReader? stateReader = target is null ? null : TargetStateReader.Open(target.NetId);
        using var cancelWait = new CancellationTokenSource();
        Task<ActivationVerdict>? wait = null;
        ActivationVerdict? verdict = null;
        string? restartError = null;
        bool restartSkipped = false;
        try
        {
            try
            {
                // ActivateConfiguration() is only "Save To Registry" — per Beckhoff
                // (Infosys 242929035) a following Start/Restart is required to make
                // the configuration physical. Calling activate WITHOUT the restart
                // was the root cause of issue #136: the running PLC kept the old code.
                sysMan.ActivateConfiguration();
                // Issue #195: the watcher already declined or cancelled a dialog (licence prompt, login modal), so
                // the configuration did not activate as asked. Restarting into it would only boot the target into a
                // failure (e.g. "license not found"), and its fatal-error box could outlive this call.
                restartSkipped = stateReader is not null && WatcherProblem(watcher) is not null;
                if (stateReader is not null && !restartSkipped)
                {
                    // Issue #195: watch the target from BEFORE the restart call, on a thread without COM.
                    // If StartRestartTwinCAT() only returns once the runtime is back, the Run -> not Run
                    // -> Run transition happens inside the call. A box raised during the call is caught too.
                    wait = StartActivationWait(stateReader, scope, watcher, modalsBefore, deadline, cancelWait.Token);
                    sysMan.StartRestartTwinCAT();   // physically activate: reboot runtime into the new config + boot project
                }
            }
            catch (Exception ex)
            {
                cancelWait.Cancel();
                ActivationVerdict? early = wait?.GetAwaiter().GetResult();
                // A box the wait found (and closed, when it was OK-only) is why the call failed: report the box.
                if (early is { Kind: ActivationOutcomeKind.Box })
                {
                    verdict = early;
                    restartError = ex.Message;
                }
                else
                {
                    // Issue #230: stop the watcher first — with autoConfirmRestart it would CONFIRM a late restart
                    // prompt — then decline whatever the failed activation still raises.
                    watcher.Stop();
                    var swept = Win32DialogAutomation.SweepAfterFailure(scope, AfterFailureQuiet, AfterFailureMax);
                    string licence = watcher.LicenseProblem is null ? "" : $" Licence dialog: {watcher.LicenseProblem}";
                    string sweep = swept.Count == 0 ? "" : $" Declined after the failure: {string.Join("; ", swept)}.";
                    throw new InvalidOperationException(
                        $"Activate/restart failed: {ex.Message}.{AccessDeniedHint(ex)}{licence}{sweep} {DialogsAnswered(watcher)}", ex);
                }
            }

            if (wait is not null)
                verdict ??= wait.GetAwaiter().GetResult();   // (a) Run, (b) box, (c) deadline — see ActivationWait
            else if (autoGenerateLicenses || autoConfirmLogin)
                Thread.Sleep(2000);   // let the watcher catch a modal raised at the tail of the activation (polls every 150 ms)
        }
        finally
        {
            cancelWait.Cancel();
            watcher.Stop();
        }

        string loginModalHandled = watcher.LoginModalHandled;
        string dialogs = DialogsAnswered(watcher);

        // Issue #195: what the restart led to (a closed box, the last state) stays in the result even when a
        // refused dialog below already decides the failure.
        string restart = restartSkipped
            ? " StartRestartTwinCAT() was not called: the configuration did not activate as asked."
            : verdict is not null && target is not null
                ? $" After the restart: {ActivationWait.Describe(verdict, target, deadline)}" +
                  (verdict.Kind == ActivationOutcomeKind.WatcherProblem ? "." : "")
                : "";

        // A licence dialog that had to be declined/cancelled means the configuration did not
        // activate as asked — never report that as success (#150, C-6).
        if (watcher.LicenseProblem is not null)
            return new ActivateResult(false, $"{watcher.LicenseProblem}{restart} {dialogs}", loginModalHandled);
        if (loginModalHandled == Win32DialogAutomation.RefusedOnlineChange)
            return new ActivateResult(false,
                "The login modal offered no online change for this code change; it was cancelled because " +
                "allowDownloadFallback=false (a download stops the running PLC). Pass allowDownloadFallback=true " +
                $"to accept a full download.{restart} {dialogs}", loginModalHandled);
        if (restartSkipped)   // the watcher's problem decided above; never fall through to a success text
            return new ActivateResult(false, $"The dialog watcher refused a dialog.{restart} {dialogs}", loginModalHandled);

        // Report exactly what happened — do NOT imply a deploy that did not occur
        // (the old code claimed success after ActivateConfiguration() alone, which
        // only writes the registry; #136). A transient online-state probe proved
        // unreliable (it caught a momentary "online" mid-restart), so we no longer
        // gate success on it. Issue #195: nor on StartRestartTwinCAT() returning —
        // only the target's system state reading Run counts.
        if (verdict is not null && target is not null)
        {
            string outcome = ActivationWait.Describe(verdict, target, deadline);
            if (!verdict.Success)
            {
                string call = restartError is null ? "" : $" StartRestartTwinCAT() failed: {restartError}.";
                return new ActivateResult(false, $"{outcome}{call} {dialogs}", loginModalHandled);
            }
            return new ActivateResult(true,
                $"Configuration activated and TwinCAT restarted (ActivateConfiguration + StartRestartTwinCAT): {outcome}. " +
                $"Login modal: {loginModalHandled}. {dialogs}", loginModalHandled);
        }

        string msg = $"Configuration saved to registry only (ActivateConfiguration, no restart). The running PLC keeps the " +
                     $"PREVIOUS code — pass autoConfirmRestart=true to restart, or use twincat_login to deploy to the running PLC. " +
                     $"Login modal: {loginModalHandled}.";
        return new ActivateResult(true, $"{msg} {dialogs}", loginModalHandled);
    }, BuildTimeout);

    // Issue #195: the NetId whose ADS state proves the restart — the project's target NetId, or the local
    // system's NetId for a Local target (the local AMS router's, issue #202). Refused before anything is
    // activated when it is no AMS NetId.
    private static ActivationTarget ResolveActivationTarget(object sysObj)
    {
        string? projectTarget = ComCall.ReadProp(() => (string?)((dynamic)sysObj).GetTargetNetId(), null);
        return ActivationTarget.Resolve(projectTarget, LocalAmsNetIdResolver.Shared.Resolve())
            ?? throw new InvalidOperationException(
                $"The project's target '{projectTarget}' is not an AMS NetId, so a restart could not be confirmed by " +
                "an ADS state read. Nothing was activated. Set the target with twincat_set_target, then activate again.");
    }

    // Issue #195: runs ActivationWait.AwaitRun on its own thread (Win32 and ADS only, no COM) while the
    // dispatcher thread makes the restart call.
    private static Task<ActivationVerdict> StartActivationWait(
        TargetStateReader stateReader, XaeWindowScope scope, Win32DialogAutomation watcher,
        IReadOnlySet<IntPtr> modalsBefore, TimeSpan deadline, CancellationToken cancel)
    {
        var probes = new ActivationProbes(
            stateReader.Read,
            () => ActivationWindows.OpenModals(scope),
            ActivationWindows.CloseOkBox,
            () => WatcherProblem(watcher),
            new StopwatchActivationClock());
        var options = new ActivationWaitOptions(deadline, watcher.Answers, modalsBefore) { Cancellation = cancel };
        return Task.Factory.StartNew(() => ActivationWait.AwaitRun(probes, options),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // Ends the wait early: once the watcher has declined or cancelled a dialog, the activation cannot succeed.
    private static string? WatcherProblem(Win32DialogAutomation watcher) =>
        watcher.LicenseProblem
        ?? (watcher.LoginModalHandled == Win32DialogAutomation.RefusedOnlineChange
            ? "The login modal was cancelled: online change was not possible and allowDownloadFallback=false."
            : watcher.LoginModalHandled == Win32DialogAutomation.RefusedReplaceRunningApp
                ? "The running application must be replaced by a full download; answered Nein because allowDownloadFallback=false."
                : null);

    // What the activation watcher answered — reported so a caller sees every click it made.
    private static string DialogsAnswered(Win32DialogAutomation watcher)
    {
        var a = watcher.Actions.ToArray().Distinct().ToList();
        return a.Count == 0 ? "XAE dialogs answered: none." : $"XAE dialogs answered: {string.Join("; ", a)}.";
    }

    // Issue #55: TIRC^License only knows the license-REQUEST form; the XAE's License editor reads the target's
    // licenses over ADS from the license server (AMS port 30) — see TcLicenseOnline. The target is the project's
    // target system (a Local target resolves to the local router's NetId).
    public LicenseInfoResult GetLicenseInfo()
    {
        string netId = ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            object sysObj = RequireSystemManager(dte)!;
            return ResolveActivationTarget(sysObj).NetId;
        }, BuildTimeout);
        return TcLicenseServer.Read(netId);
    }

    public RequiredLicensesResult ListRequiredLicenses() => ComBusyRetry.Run(() =>
    {
        // TC3 4026.22: TIRC^License ProduceXml returns the "Lizenz Anforderung"
        // submission form (<LicenseRequest>), NOT the manually-selected list.
        // The actual selected licenses live in <System><Licenses><Target><ManualSelect>
        // inside the .tsproj file — read directly from disk.
        dynamic dte = GetActiveTcDte();
        RequireSystemManager(dte); // ensures a solution with system-manager is open

        string slnPath = (string)dte.Solution.FullName;
        string? tsprojPath = TsprojLocator.FindForSolution(slnPath);
        if (tsprojPath is null)
            throw new InvalidOperationException(
                "Cannot locate the .tsproj file beside the solution — ensure the " +
                "project has been saved at least once (twincat_save_project).");

        string tsprojXml = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var licenses = ManualSelectXml.Parse(tsprojXml);
        return new RequiredLicensesResult(licenses, tsprojXml);
    }, BuildTimeout);

    public AddRequiredLicenseResult AddRequiredLicense(string? licenseId, string? orderNo) =>
        ComBusyRetry.Run(() =>
    {
        // Issue #148 (B-3): only a GUID can be a <ManualSelect> value. An order number cannot be
        // resolved to one here, so it is refused instead of being written as "{TC1200}".
        var inputError = ManualSelectXml.ValidateAddInput(licenseId, orderNo);
        if (inputError is not null)
            throw new ArgumentException(inputError);
        licenseId = ManualSelectXml.TryFormatGuid(licenseId)!;
        orderNo = null;

        dynamic dte = GetActiveTcDte();
        RequireSystemManager(dte);

        string slnPath = (string)dte.Solution.FullName;
        string? tsprojPath = TsprojLocator.FindForSolution(slnPath)
            ?? throw new InvalidOperationException(
                "Cannot locate the .tsproj file — ensure the project has been saved first.");

        string tsprojXml = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var current = ManualSelectXml.Parse(tsprojXml);

        if (ManualSelectXml.Contains(current, licenseId, orderNo))
            return new AddRequiredLicenseResult(
                Added: false,
                WasAlreadyPresent: true,
                RequiredLicenses: current,
                Message: "License already present in required list (no change).");

        // Modify .tsproj on disk, then reload so XAE picks up the change.
        // Close without saving to preserve our disk modification (Close(true) would
        // overwrite our edit with XAE's unchanged in-memory state).
        SaveAll();
        // Re-read after the save (it may have rewritten the file), then build and validate the
        // edit completely BEFORE touching the file or closing the solution.
        tsprojXml = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        string modified = ManualSelectXml.Add(tsprojXml, licenseId);
        try { System.Xml.Linq.XDocument.Parse(modified); }
        catch (System.Xml.XmlException xe)
        {
            throw new InvalidOperationException(
                $"Refusing to write the .tsproj: the edited XML would not be well-formed ({xe.Message}).", xe);
        }
        if (!ManualSelectXml.Contains(ManualSelectXml.Parse(modified), licenseId, null))
            throw new InvalidOperationException("Internal error: the edited .tsproj does not contain the new <ManualSelect>.");
        // Close BEFORE writing: an edit while the solution is open races the XAE's file watcher, which then
        // raises "Es wurde eine Dateiänderung erkannt … Neu laden / Ignorieren" and blocks every later call
        // (seen live 2026-10-10 in AddAndRemoveRequiredLicense_RoundTrip). A Close(false) writes nothing,
        // so the edit validated above still applies to the file.
        CloseSolution(saveFirst: false);
        WriteTsprojKeepingBom(tsprojPath, modified);
        Thread.Sleep(1000);
        OpenSolution(slnPath);
        // Poll until XAE has indexed the reloaded project. Busy here is the normal reload lag
        // and must stay local: escaping to the outer retry would replay close/edit/reopen
        // (seen live, issue #149 — a 10-minute close/open loop).
        AfterMutation<object?>(() => { RequireSystemManager(dte); return null; });

        // Read back from disk after the reload: the entry must be there.
        string confirmed = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var after = ManualSelectXml.Parse(confirmed);
        if (!ManualSelectXml.Contains(after, licenseId, null))
            throw new InvalidOperationException(
                $"License {licenseId} is NOT in the .tsproj <ManualSelect> list after the reload — not added.");

        return new AddRequiredLicenseResult(
            Added: true,
            WasAlreadyPresent: false,
            RequiredLicenses: after,
            Message: $"License added to .tsproj <ManualSelect> and project reloaded ({after.Count} total).");
    }, BuildTimeout);

    private static void WriteTsprojKeepingBom(string tsprojPath, string content)
    {
        byte[] head = File.ReadAllBytes(tsprojPath);
        bool hadBom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        File.WriteAllText(tsprojPath, content, new System.Text.UTF8Encoding(hadBom));
    }

    public RemoveRequiredLicenseResult RemoveRequiredLicense(string? licenseId, string? orderNo) =>
        ComBusyRetry.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(licenseId) && string.IsNullOrWhiteSpace(orderNo))
            throw new ArgumentException("Either licenseId or orderNo must be supplied.");

        dynamic dte = GetActiveTcDte();
        RequireSystemManager(dte);

        string slnPath = (string)dte.Solution.FullName;
        string? tsprojPath = TsprojLocator.FindForSolution(slnPath)
            ?? throw new InvalidOperationException(
                "Cannot locate the .tsproj file — ensure the project has been saved first.");

        string tsprojXml = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var current = ManualSelectXml.Parse(tsprojXml);

        if (!ManualSelectXml.Contains(current, licenseId, orderNo))
            return new RemoveRequiredLicenseResult(
                Removed: false,
                WasAbsent: true,
                RequiredLicenses: current,
                Message: "License not found in required list (no change).");

        SaveAll();
        // Re-read after the save (it may have rewritten the file) — the edit must not drop what was just saved.
        tsprojXml = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var (modified, removedCount) = ManualSelectXml.Remove(tsprojXml, licenseId, orderNo);
        try { System.Xml.Linq.XDocument.Parse(modified); }
        catch (System.Xml.XmlException xe)
        {
            throw new InvalidOperationException(
                $"Refusing to write the .tsproj: the edited XML would not be well-formed ({xe.Message}).", xe);
        }

        // Same order as AddRequiredLicense: close first, then write, then reopen (no file-watcher race).
        CloseSolution(saveFirst: false);
        WriteTsprojKeepingBom(tsprojPath, modified);
        Thread.Sleep(1000);
        OpenSolution(slnPath);
        // Poll until XAE has indexed the reloaded project. Busy here is the normal reload lag
        // and must stay local: escaping to the outer retry would replay close/edit/reopen
        // (seen live, issue #149 — a 10-minute close/open loop).
        AfterMutation<object?>(() => { RequireSystemManager(dte); return null; });

        string confirmed = File.ReadAllText(tsprojPath, System.Text.Encoding.UTF8);
        var after = ManualSelectXml.Parse(confirmed);

        return new RemoveRequiredLicenseResult(
            Removed: true,
            WasAbsent: false,
            RequiredLicenses: after,
            Message: $"License removed from .tsproj <ManualSelect> and project reloaded ({removedCount} removed, {after.Count} remaining).");
    }, BuildTimeout);

    public string DismissSecurityDialog()
    {
        var (dismissed, screenshot, created) =
            Win32DialogAutomation.DismissSecurityCodeDialogWithWait();
        if (dismissed)
            return created is null
                ? "Security dialog dismissed: code entered and OK clicked."
                : $"Security dialog dismissed. License file: {created}";
        if (screenshot != null)
            return "Sicherheitscode dialog is open but the code could not be extracted automatically. "
                 + $"Screenshot saved to: {screenshot} — read the code and call twincat_submit_security_code(code).";
        return "No 'Sicherheitscode eingeben' dialog found — nothing dismissed."
             + " Start an activation cycle first, then call this tool while the dialog is open.";
    }

    public string OpenLicenseManagerAndRequestTrial()
    {
        // The button exists only while SYSTEM > License is open in the shared sysman editor; a
        // LookupTreeItem does not open it (live, 2026-10-08). See TrialLicenseButtonFlow.
        var outcome = TrialLicenseButtonFlow.Run(
            Win32DialogAutomation.TryClickTrialLicenseButton,
            OpenLicenseEditor,
            retryWindow: TimeSpan.FromSeconds(5),
            sleep: Thread.Sleep);

        string opened = outcome.OpenEditorNote is null ? "" : $" (License editor: {outcome.OpenEditorNote})";
        return outcome.Clicked
            ? "License Manager opened and 'Aktiviere 7 Tage Testlizenz' button clicked" + opened + ". " +
              "Call twincat_generate_trial_licenses to handle the confirmation and security-code dialogs."
            : "License Manager button not found via Win32 child-window search" + opened + ". " +
              "Open SYSTEM > License manually, click 'Aktiviere 7 Tage Testlizenz', " +
              "then call twincat_generate_trial_licenses.";
    }

    // Opens SYSTEM > License in the shared sysman editor: the localized Solution-Explorer names come
    // from ITcSmTreeItem.Name (never hardcoded), then the row is selected and activated via UIAutomation.
    // Never throws — the note says what happened.
    private string OpenLicenseEditor()
    {
        List<string> display;
        IntPtr hwnd;
        try
        {
            (display, hwnd) = RunOnSta(() =>
            {
                dynamic dte = GetActiveTcDte();
                dynamic sysMan = RequireSystemManager(dte)!;
                var names = new List<string>(2);
                foreach (string path in new[] { "TIRC", "TIRC^License" })
                {
                    string? name = TryGetLocalizedName(sysMan.LookupTreeItem(path));
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidOperationException($"tree item '{path}' has no name");
                    names.Add(name!);
                }
                return (names, (IntPtr)TryGetXaeMainWindow(dte));
            });
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return $"could not resolve SYSTEM > License: {ex.Message}";
        }

        if (hwnd == IntPtr.Zero) return "no XAE main-window handle";
        SolutionExplorerAutomation.TryActivatePath(hwnd, display, out string note);
        return note;
    }

    public (string Message, byte[]? ScreenshotPng) GenerateTrialLicenses() =>
        Win32DialogAutomation.TryGenerateTrialLicenses();

    public string SubmitSecurityCode(string code)
    {
        var (ok, path, note) = Win32DialogAutomation.TrySubmitSecurityCode(code);
        if (!ok)
            return note.StartsWith("No ", StringComparison.Ordinal) ? note : $"[Error] {note}";
        return path is null
            ? $"Security code '{code}' entered and OK clicked (dialog closed)."
            : $"Security code '{code}' entered. License file: {path}";
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { caught = ex; } // com-allow: thread entry — captured and rethrown on the caller thread
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("STA operation timed out after 30 seconds.");
        if (caught is not null) throw caught;
        return result;
    }

    private static bool SolutionHasPlcProject(dynamic dte) => FindFirstPlcProjectUniqueName(dte) is not null;

    private static BuildResult MaterialiseBuildResult(dynamic dte, dynamic build,
        Dictionary<string, OutputPaneDelta.PaneMark>? priorPanes, DateTime buildStartedUtc, bool expectsPlcCompile)
    {
        int lastBuildInfo;
        try { lastBuildInfo = (int)build.LastBuildInfo; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"COM fault reading build status (LastBuildInfo): {ex.Message}", ex);
        }

        // This build's text of EVERY pane: the summary line is taken from whichever pane has it,
        // and the compiler lines decide error vs warning for Error List items (issue #178).
        List<(string Name, string Text)> panes = CollectOutputPanes((object)dte, priorPanes);
        string buildOutput = string.Join("\n", panes.Select(pn => pn.Text));

        // Wrap error-collection in its own retry loop so RPC_E_SERVERCALL_RETRYLATER
        // from the ErrorList COM window doesn't collapse to an empty errors[].
        // IMessageFilter covers the STA/GUI path; this explicit retry covers the
        // MTA/MCP path where CoRegisterMessageFilter is not available.
        List<BuildError> errors = ComBusyRetry.Run<List<BuildError>>(() => CollectFromErrorItems(dte, buildOutput), ErrorReadTimeout);
        if (lastBuildInfo != 0 && !errors.Any(e => e.Level == "error"))
            errors.AddRange(ComBusyRetry.Run<List<BuildError>>(() => CollectFromSolutionBuild(dte, lastBuildInfo), ErrorReadTimeout));

        string log = CollectBuildLog(dte, panes, buildOutput);
        string? summary = BuildTruth.ExtractCompilerSummary(buildOutput);
        BuildArtifactInfo? artifact = FindNewestCompileArtifact(dte);
        string? failure = BuildTruth.FailureReason(lastBuildInfo, summary, artifact, buildStartedUtc, expectsPlcCompile);

        return new BuildResult(failure is null, lastBuildInfo, errors, log, summary, artifact, failure);
    }

    // Error List → BuildError, classified by ErrorLevel plus the compiler line (BuildTruth.ClassifyErrorItem,
    // issue #152 C-19 / #178). An unreadable list becomes one "[Error]" entry instead of an empty list.
    private static List<BuildError> CollectFromErrorItems(dynamic dte, string? compilerOutput)
    {
        List<ErrorListEntry> entries;
        try { entries = ReadErrorListEntries((DTE2)dte); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return [new BuildError("error", $"[Error] COM: ErrorList access failed: {ex.Message}", null, null)];
        }
        return ToBuildErrors(entries, compilerOutput);
    }

    private static List<BuildError> ToBuildErrors(List<ErrorListEntry> entries, string? compilerOutput) =>
        entries
            .Select(e => new BuildError(BuildTruth.ClassifyErrorItem(e.Level, e.Description, compilerOutput), e.Description, e.File, e.Line))
            .ToList();

    // One Error List row as read from EnvDTE80.ErrorItem.
    private sealed record ErrorListEntry(
        string Description, string? File, int? Line, int? Column, string? Project, int? Level);

    // Issue #178: the Error List is read through the TYPED EnvDTE80 interfaces. The previous
    // late-bound read (`dynamic errorItems … errorItems.Count`) returned nothing on TcXaeShell
    // 4026.22 while the window showed three errors (proven live; a late-bound ErrorItems call
    // fails there with E_NOINTERFACE), and its failure path swallowed that into an empty list.
    // The typed read returns the rows. A hidden Error List window is shown first so the read
    // never depends on the user having opened it. Throws when the list cannot be read; a busy
    // XAE rethrows so the caller's ComBusyRetry replays the whole read.
    private static List<ErrorListEntry> ReadErrorListEntries(DTE2 dte)
    {
        ErrorList list = dte.ToolWindows.ErrorList;
        EnsureErrorListWindowShown(dte, list);

        ErrorItems items = list.ErrorItems;
        int count = items.Count;
        var result = new List<ErrorListEntry>(count);
        for (int i = 1; i <= count; i++)
        {
            ErrorItem item;
            try { item = items.Item(i); }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }

            string desc = ReadOrDefault(() => item.Description) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(desc)) continue;
            result.Add(new ErrorListEntry(
                desc,
                ReadOrDefault(() => item.FileName),
                ReadOrDefault<int?>(() => item.Line),
                ReadOrDefault<int?>(() => item.Column),
                ReadOrDefault(() => item.Project),
                ReadOrDefault<int?>(() => (int)item.ErrorLevel)));
        }
        return result;
    }

    // View.ErrorList creates and shows the tool window; only issued when the window is not
    // visible, so an already-open list is read without moving the user's focus.
    private static void EnsureErrorListWindowShown(DTE2 dte, ErrorList list)
    {
        bool visible = false;
        try { visible = list.Parent.Visible; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* unknown — treat as hidden */ }
        if (visible) return;
        try { dte.ExecuteCommand("View.ErrorList"); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* read what the list has */ }
    }

    private static T? ReadOrDefault<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return default; }
    }

    // Combined build logger output: the Visual Studio Output-window build pane
    // (streaming "Build started"/compile progress — where TwinCAT writes the
    // actual failure detail) followed by the full Error List window contents.
    // The Output pane is the primary source and the Error List is supplementary
    // (it used to read as empty on 4026.22 — a late-bound read, fixed in #178).
    private static string CollectBuildLog(dynamic dte, List<(string Name, string Text)> panes, string buildOutput)
    {
        var sb = new System.Text.StringBuilder();

        string outputLog = BestPane(panes);
        if (!string.IsNullOrWhiteSpace(outputLog))
        {
            sb.AppendLine("==== Build output ====");
            sb.AppendLine(outputLog.TrimEnd());
        }

        string errorList = CollectErrorListText(dte, buildOutput);
        if (!string.IsNullOrWhiteSpace(errorList))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("==== Error list ====");
            sb.AppendLine(errorList);
        }

        return BuildTruth.CapBuildLog(sb.ToString().TrimEnd());
    }

    // ---- build truth (backlog item 5) -------------------------------------------
    //
    // "Build succeeded" was reported while the artifacts stayed old, and the log
    // shown was often an ESI echo from an EARLIER run rather than this build's
    // compiler output. Two independent facts settle whether a translation really
    // happened, so both are surfaced as their own fields instead of leaving the
    // caller to grep a log:
    //   * the compiler's own summary line ("N errors, M warnings"), taken from
    //     THIS build's output only (see MeasureOutputPanes / OutputPaneDelta), and
    //   * the youngest compile artifact with its timestamp.
    // Artifact choice matters: PLC.tmc is NOT rewritten on every build (checking
    // it alone is how a stale build can look fresh), whereas
    // _CompileInfo/*.compileinfo is — so the .compileinfo files are the primary
    // evidence and the others are reported for context.
    // The summary extraction itself is BuildTruth.ExtractCompilerSummary (pure, unit-tested).

    private static BuildArtifactInfo? FindNewestCompileArtifact(dynamic dte)
    {
        try
        {
            string slnPath = (string)dte.Solution.FullName;
            string? dir = Path.GetDirectoryName(slnPath);
            if (string.IsNullOrEmpty(dir)) return null;

            // .compileinfo is rewritten on every successful translation; the
            // others are reported only when nothing better exists.
            var patterns = new[] { "*.compileinfo", "*.app", "*.tmc" };
            foreach (var pattern in patterns)
            {
                FileInfo? newest = null;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
                    {
                        var fi = new FileInfo(f);
                        if (newest is null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = fi;
                    }
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                if (newest is not null)
                    return new BuildArtifactInfo(newest.FullName, newest.LastWriteTimeUtc,
                        (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalSeconds);
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return null;
    }

    // Output-window panes accumulate for the whole XAE session — the TwinCAT pane
    // also collects runtime/plant messages that have nothing to do with a build.
    // Returning the whole pane produced a 145 000-character tool result that blew
    // the MCP size limit and buried the actual compiler output under hours of
    // unrelated alarms. So: snapshot each pane's length BEFORE the build and
    // return only what was appended after that mark.
    private static Dictionary<string, OutputPaneDelta.PaneMark> MeasureOutputPanes(dynamic dte)
    {
        var marks = new Dictionary<string, OutputPaneDelta.PaneMark>(StringComparer.Ordinal);
        foreach (var (name, text) in ReadOutputPanes((object)dte))
            marks[name] = OutputPaneDelta.Mark(text);
        return marks;
    }

    // Every Output-window pane as (name, full text). Best effort: an unreadable pane is skipped.
    private static List<(string Name, string Text)> ReadOutputPanes(object dte)
    {
        var result = new List<(string, string)>();
        try
        {
            // Cast to DTE2 so the runtime QIs for the correct interface. Plain dynamic dispatch on
            // a cross-apartment __ComObject cannot resolve ToolWindows (Infosys: via EnvDTE80.DTE2).
            DTE2 dte2 = (DTE2)dte;
            dynamic panes = dte2.ToolWindows.OutputWindow.OutputWindowPanes;
            int paneCount = (int)panes.Count;
            for (int p = 1; p <= paneCount; p++)
            {
                try
                {
                    dynamic pane = panes.Item(p);
                    string name = string.Empty;
                    try { name = (string)pane.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    dynamic td = pane.TextDocument;
                    dynamic ep = td.StartPoint.CreateEditPoint();
                    result.Add((name, (string)ep.GetText(td.EndPoint) ?? string.Empty));
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* best effort — no pane text */ }
        return result;
    }

    // What each pane gained since <paramref name="prior"/> (all of it without a snapshot).
    private static List<(string Name, string Text)> CollectOutputPanes(object dte,
        Dictionary<string, OutputPaneDelta.PaneMark>? prior)
    {
        var result = new List<(string, string)>();
        foreach (var (name, text) in ReadOutputPanes((object)dte))
        {
            OutputPaneDelta.PaneMark? mark = null;
            if (prior is not null) prior.TryGetValue(name, out mark);
            result.Add((name, OutputPaneDelta.Appended(mark, text)));
        }
        return result;
    }

    // The pane shown as "Build output": a build/TwinCAT/PLC pane with the most new text, else the
    // pane with the most new text.
    private static string BestPane(List<(string Name, string Text)> panes)
    {
        (string Name, string Text) best = panes
            .Where(c => c.Name.Contains("Build", StringComparison.OrdinalIgnoreCase)
                     || c.Name.Contains("Erstellen", StringComparison.OrdinalIgnoreCase)
                     || c.Name.Contains("TwinCAT", StringComparison.OrdinalIgnoreCase)
                     || c.Name.Contains("PLC", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Text.Length)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(best.Text))
            best = panes.OrderByDescending(c => c.Text.Length).FirstOrDefault();
        return best.Text ?? string.Empty;
    }

    // Full Error List window contents as a readable log: every item (errors,
    // warnings, and TwinCAT status/license messages), including items with no
    // file location. Per Infosys "Accessing the Error List window of Visual
    // Studio", each ErrorItem exposes Description/FileName/Line/Column/Project.
    private static string CollectErrorListText(dynamic dte, string? compilerOutput)
    {
        List<ErrorListEntry> entries;
        try { entries = ReadErrorListEntries((DTE2)dte); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            return $"[Error] COM: Error List log unavailable: {ex.Message}";
        }

        // Messages accumulate in the Error List since the XAE started (EtherCAT device-description notices by the
        // hundred); listed in full they pushed this build's Output pane out of the capped log. Errors and warnings
        // stay complete, messages are limited like in the build result (BuildCoordinator.AppendErrorListEntries).
        var sb = new System.Text.StringBuilder();
        var listedMessages = new HashSet<string>(StringComparer.Ordinal);
        int omittedMessages = 0;
        foreach (var e in entries)
        {
            string tag = BuildTruth.ClassifyErrorItem(e.Level, e.Description, compilerOutput) switch
            {
                "error" => "ERROR",
                "warning" => "WARN",
                _ => "MSG",
            };
            if (tag == "MSG")
            {
                if (listedMessages.Contains(e.Description)) continue;
                if (listedMessages.Count >= BuildCoordinator.MaxListedMessages) { omittedMessages++; continue; }
                listedMessages.Add(e.Description);
            }

            var loc = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(e.File))
            {
                loc.Append(e.File);
                if (e.Line is > 0) loc.Append(CultureInfo.InvariantCulture, $":{e.Line}");
                if (e.Column is > 0) loc.Append(CultureInfo.InvariantCulture, $",{e.Column}");
            }
            if (!string.IsNullOrEmpty(e.Project))
                loc.Append(loc.Length > 0 ? $" [{e.Project}]" : $"[{e.Project}]");

            sb.AppendLine(loc.Length > 0 ? $"[{tag}] {loc} {e.Description}" : $"[{tag}] {e.Description}");
        }
        if (omittedMessages > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"[MSG] … {omittedMessages} more messages not shown (twincat_get_error_list lists them).");
        return sb.ToString().TrimEnd();
    }

    // Fallback when ErrorList is empty: named project failures from SolutionBuild.
    private static List<BuildError> CollectFromSolutionBuild(dynamic dte, int failedProjects)
    {
        var errors = new List<BuildError>();
        try
        {
            dynamic deps = dte.Solution.SolutionBuild.BuildDependencies;
            int depCount = 0;
            try { depCount = (int)deps.Count; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            for (int i = 1; i <= depCount; i++)
            {
                try
                {
                    dynamic dep = deps.Item(i);
                    string projName = string.Empty;
                    try { projName = (string)dep.Project.Name; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    bool built = true;
                    try { built = (bool)dep.Built; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (!built && !string.IsNullOrEmpty(projName))
                        errors.Add(new BuildError("error", $"Project '{projName}' failed to build.", null, null));
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        if (errors.Count == 0 && failedProjects > 0)
            errors.Add(new BuildError("error", $"Build failed ({failedProjects} project(s)).", null, null));
        return errors;
    }

    private static string? FindFirstPlcProjectUniqueName(dynamic dte) =>
        FindPlcProjectUniqueName(dte, matchName: null);

    private static string? FindProjectUniqueName(dynamic dte, string projectName) =>
        FindPlcProjectUniqueName(dte, matchName: projectName);

    // In a TwinCAT solution, Solution.Projects contains only the .tsproj wrapper —
    // the .plcproj is nested inside and not surfaced by EnvDTE's Projects collection.
    // SolutionBuild.ActiveConfiguration.SolutionContexts enumerates ALL build contexts
    // including nested projects, so it does expose the .plcproj UniqueName.
    private static string? FindPlcProjectUniqueName(dynamic dte, string? matchName)
    {
        try
        {
            dynamic contexts = dte.Solution.SolutionBuild.ActiveConfiguration.SolutionContexts;
            int count = (int)contexts.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic ctx = contexts.Item(i);
                    string uniqueName = string.Empty;
                    try { uniqueName = (string)ctx.ProjectName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                    if (uniqueName.EndsWith(".plcproj", StringComparison.OrdinalIgnoreCase))
                    {
                        if (matchName is null) return uniqueName;
                        // Match by the last path segment (project file name without extension)
                        string segment = Path.GetFileNameWithoutExtension(uniqueName);
                        if (string.Equals(segment, matchName, StringComparison.OrdinalIgnoreCase))
                            return uniqueName;
                    }
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return null;
    }

    // The configuration string BuildProject expects is the per-project
    // SolutionContext config (e.g. "Release|TwinCAT RT (x64)"), NOT the
    // solution's active-configuration display name. Pull it from the matching
    // context so BuildProject targets the real .plcproj build, not an empty one.
    private static string? FindSolutionContextConfigName(dynamic dte, string uniqueName)
    {
        try
        {
            dynamic active = dte.Solution.SolutionBuild.ActiveConfiguration;
            dynamic contexts = active.SolutionContexts;
            int count = (int)contexts.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic ctx = contexts.Item(i);
                    string ctxProject = string.Empty;
                    try { ctxProject = (string)ctx.ProjectName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (!string.Equals(ctxProject, uniqueName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string cfg = string.Empty;
                    try { cfg = (string)ctx.ConfigurationName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    string plat = string.Empty;
                    try { plat = (string)ctx.PlatformName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                    if (!string.IsNullOrEmpty(cfg))
                        return string.IsNullOrEmpty(plat) ? cfg : $"{cfg}|{plat}";
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
            // No per-project context name: fall back to the active config's own
            // name (Configuration|Platform), which BuildProject also accepts.
            try
            {
                string name = (string)active.Name ?? string.Empty;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        return null;
    }

    // ---- xae-error-list ---------------------------------------------------------
    // The clear command is the Error List window's own context-menu entry — on this German XAE
    // "OtherContextMenus.ErrorList.Löschen" (confirmed empirically by enumerating dte.Commands;
    // "TwinCAT.ClearErrorList" is an unrelated command that no-ops the VS Error List). Its NAME is
    // localized, its identity is not: GUID {3B11520B-7E70-4008-A6CF-B60AE84E12B1}, ID 24608
    // (TcXaeShell 4026.22). Issue #150 (C-16): the name is resolved at runtime from that identity
    // (dte.Commands.Item(guid, id).Name), with the known DE/EN names as fallbacks — see
    // ErrorListClearCommand. It acts on the *active* tool window, so we focus the Error List
    // first, then verify by a real pre/post ErrorItems.Count delta and report the TRUTH:
    // Success only if the list actually shrank (or was already empty).
    // An unreadable Error List throws (→ "[Error]"), never reads as "empty" (issue #178). Error vs
    // warning of a level-2 item comes from the compiler line in the Output window (full pane text).
    public IReadOnlyList<BuildError> GetErrorList() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        List<ErrorListEntry> entries = ReadErrorListEntries((DTE2)dte);
        string output = string.Join("\n", ReadOutputPanes((object)dte).Select(pn => pn.Text));
        return (IReadOnlyList<BuildError>)ToBuildErrors(entries, output);
    }, ErrorReadTimeout);

    public ErrorListClearResult ClearErrorList() => ComBusyRetry.Run(() =>
    {
        DTE2 dte;
        try { dte = (DTE2)GetActiveTcDte(); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return new ErrorListClearResult(false, -1, $"ClearErrorList failed: {ex.Message}"); }

        int CountEntries()
        {
            try { return (int)dte.ToolWindows.ErrorList.ErrorItems.Count; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return -1; }
        }

        int before = CountEntries();
        string usedCommand = ErrorListClearCommand.GermanName;

        // Nothing to clear: the Error List window's context-menu "Löschen"
        // item is DISABLED when the list is empty, so issuing the command
        // would throw a COM "Unexpected HRESULT" instead of being a no-op.
        // Short-circuit here — an empty list is a successful clear by intent.
        if (before == 0)
            return new ErrorListClearResult(true, 0, "Error list was already empty.", RemainingCount: 0);

        try
        {
            // Focus the Error List first so the context-menu clear has a
            // target, then issue it. View.ErrorList both ensures the window
            // exists and makes it the active tool window.
            try { dte.ExecuteCommand("View.ErrorList"); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            // Re-read after making the window visible: if the initial count was -1
            // (window wasn't shown yet), it is now readable. Short-circuit again if
            // the list turns out to be empty or still unreadable — the clear command
            // is disabled when empty and would throw an HRESULT error.
            if (before < 0) before = CountEntries();
            if (before <= 0)
                return new ErrorListClearResult(true, 0, "Error list was already empty.", RemainingCount: 0);

            usedCommand = ExecuteFirstAvailable(dte, ErrorListClearCommand.Candidates(ResolveErrorListClearName(dte)));
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new ErrorListClearResult(false, before, $"ClearErrorList failed: {ex.Message}");
        }

        // Re-read the list after the clear so callers reflect the real
        // post-clear state, not just an echoed message.
        int after = CountEntries();

        // Counts unreadable on both sides: can't verify — don't claim success.
        if (before < 0 && after < 0)
            return new ErrorListClearResult(false, -1,
                "Clear command issued but the Error List count could not be read to verify it.");

        if (after == 0 || (before > 0 && after >= 0 && after < before))
            return new ErrorListClearResult(true, before < 0 ? -1 : before - after,
                $"Cleared {(before < 0 ? -1 : before - after)} error list entries (via {usedCommand}).", RemainingCount: after);

        // Command ran without error but nothing was removed — no-op on this
        // host. Report it honestly instead of a false success.
        return new ErrorListClearResult(false, 0,
            $"{usedCommand} ran but {after} entries remain — clear appears unsupported on this XAE host.",
            RemainingCount: after);
    }, BuildTimeout);

    // Localized name of the Error List clear command, from its locale-independent GUID/ID; null
    // when this host does not know the command under that identity.
    private static string? ResolveErrorListClearName(DTE2 dte)
    {
        try { return (string?)dte.Commands.Item(ErrorListClearCommand.Guid, ErrorListClearCommand.Id)?.Name; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Runs the first candidate the shell accepts; a name unknown to this locale throws and the
    // next is tried. Throws with the FIRST failure (the resolved name's — the relevant one) when
    // none ran. Returns the name that ran.
    private static string ExecuteFirstAvailable(DTE2 dte, IReadOnlyList<string> candidates)
    {
        Exception? first = null;
        foreach (string name in candidates)
        {
            try { dte.ExecuteCommand(name); return name; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { first ??= comEx; }
        }
        throw new InvalidOperationException(
            $"no Error List clear command ran (tried {string.Join(", ", candidates)}): {first?.Message}", first);
    }

    // ---- build-recovery: Clean + Rebuild (feature/build-recovery) ------------

    public CleanResult CleanSolution() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveBeforeBuild(dte);
        dynamic build = dte.Solution.SolutionBuild;
        try
        {
            build.Clean(true);
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) {
            return new CleanResult(false, $"Clean failed: {ex.Message}");
        }
        return new CleanResult(true, "Solution cleaned.");
    }, BuildTimeout);

    public RebuildResult RebuildSolution() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveBeforeBuild(dte);
        dynamic build = dte.Solution.SolutionBuild;
        // C-21 (issue #152): snapshot the output panes like a build does — otherwise the log is the
        // whole session's pane history and the compiler summary can be an earlier run's.
        var priorPanes = MeasureOutputPanes(dte);
        DateTime started = DateTime.UtcNow;
        build.Clean(true);
        build.Build(true);
        var br = MaterialiseBuildResult(dte, build, priorPanes, started, SolutionHasPlcProject(dte));
        return new RebuildResult(br.Success, br.FailedProjects, br.Errors, br.Log, br.CompilerSummary, br.Artifact, br.FailureReason);
    }, BuildTimeout);

    public RebuildResult RebuildPlc(string? plcProjectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveBeforeBuild(dte);
        dynamic build = dte.Solution.SolutionBuild;

        // Resolve .plcproj unique name (same settle-lag loop as BuildPlc)
        string? uniqueName = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            uniqueName = plcProjectName is not null
                ? FindProjectUniqueName(dte, plcProjectName)
                : FindFirstPlcProjectUniqueName(dte);
            if (uniqueName is not null || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(TimeSpan.FromMilliseconds(400));
        }

        if (uniqueName is null)
            throw new InvalidOperationException(
                plcProjectName is not null
                    ? $"PLC project '{plcProjectName}' not found."
                    : "No .plcproj found in solution.");

        string? config = FindSolutionContextConfigName(dte, uniqueName);
        if (config is null)
        {
            try { config = (string)build.ActiveConfiguration.Name; }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { config = "Release"; }
        }

        // Clean the PLC project, then rebuild. Compile-only — no Activate/Boot
        // (Boot-Info stage can report failure when the runtime is crashed/dumped,
        // which would mask a successful compile; see 100000).
        var priorPanes = MeasureOutputPanes(dte);
        DateTime started = DateTime.UtcNow;
        build.Clean(true);
        build.BuildProject(config, uniqueName, true);
        var br = MaterialiseBuildResult(dte, build, priorPanes, started, expectsPlcCompile: true);
        return new RebuildResult(br.Success, br.FailedProjects, br.Errors, br.Log, br.CompilerSummary, br.Artifact, br.FailureReason);
    }, BuildTimeout);
}
