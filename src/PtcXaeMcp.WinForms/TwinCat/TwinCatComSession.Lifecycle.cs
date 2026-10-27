using System.Diagnostics;
using System.Runtime.InteropServices;
using PtcXaeMcp.WinForms.TwinCat.HostSelection;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
    // No TcXaeShell process → nothing to attach to; skip COM (fast, and a stale ROT entry of a
    // dead process cannot answer). Unknown count (null) still tries COM.
    public bool IsConnected =>
        XaeProcessProbe.Count() != 0 &&
        ComBusyRetry.Run(() => GetActiveTcDteOrNull() is not null, ProbeTimeout);

    public bool HasOpenSolution() => ComBusyRetry.Run(() =>
    {
        dynamic? dte = GetActiveTcDteOrNull();
        if (dte is null) return false;
        try { return (bool)dte.Solution.IsOpen; }
        // A busy/rejected COM call right after a structural change (e.g. the
        // project tree reloading after CreateChild) must bubble so
        // ComBusyRetry can retry — swallowing it into "false" would make a
        // momentarily-busy DTE look like "no solution open" and wrongly fail
        // the very next tool's RequireOpenSolution guard.
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return false; }
    }, ProbeTimeout);

    public string? ActiveSolutionPath() => ComBusyRetry.Run<string?>(() =>
    {
        dynamic? dte = GetActiveTcDteOrNull();
        if (dte is null) return null;
        try
        {
            string full = (string)dte.Solution.FullName;
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return null; }
    }, ProbeTimeout);

    public TwinCatEnvironmentStatus Probe() =>
        ComBusyRetry.Run(TwinCatEnvironmentProbe.Probe, ProbeTimeout);

    public async Task ConnectOrLaunchAsync(string? launchWithSlnPath, TimeSpan timeout, CancellationToken ct,
        XaeHostClass? hostOverride = null)
    {
        // Resolve the right host from the project's declared TcVersion when a
        // .tsproj can be located beside the .sln; otherwise stay on the
        // unbiased legacy probe order (worst case == today's behaviour).
        // An explicit host override wins and is strict: only its ProgID is probed.
        IReadOnlyList<string>? probeOrder = null;
        string exePath = XaeExePath;

        var tsproj = TsprojLocator.FindForSolution(launchWithSlnPath);
        if (hostOverride is { } forcedClass)
        {
            var forced = HostResolver.Resolve(forcedClass);
            probeOrder = [forced.ProgId];
            if (File.Exists(forced.ExePath))
                exePath = forced.ExePath;
            else
                throw new InvalidOperationException(
                    $"The {XaeHostReport.Name(forced.ProgId)} was requested but is not installed (expected {forced.ExePath}).");
        }
        else if (tsproj is not null &&
            HostResolver.TryResolveFromTsprojFile(tsproj, out var host, out _))
        {
            probeOrder = HostResolver.AttachProbeOrder(host!.HostClass);
            if (File.Exists(host.ExePath))
                exePath = host.ExePath;
        }

        // Track the deterministic anchor for Q2 (issue #57): the path we were
        // asked to launch/open is the only trustworthy "which project" signal.
        if (!string.IsNullOrWhiteSpace(launchWithSlnPath))
            SetExpectedSolutionPath(launchWithSlnPath);

        if (TryAttachQuietly(probeOrder))
            return;

        // A TcXaeShell may already be up but momentarily absent from the
        // Running Object Table — still booting, busy right after a build, or
        // blocked behind the modal security/license dialog ActivateConfiguration
        // raises. GetActiveTcDteOrNull swallows that miss as "no XAE", and
        // launching a second shell in that window is the recurring "2 XAEs
        // open" bug (feedback_single_xae_constraint). Never start a new shell
        // while a TcXaeShell process exists: poll the ROT until its DTE
        // becomes reachable instead, and fail loudly if it never does.
        // Unknown process count (probe failure) must never read as "none" — that is how a
        // second XAE got launched (issue #149 A-16).
        int? running = XaeProcessProbe.Count();
        if (running is null)
            throw new InvalidOperationException(XaeProcessProbe.LaunchBlockReason(null));
        if (running > 0)
        {
            await WaitForReachableDteAsync(probeOrder, timeout, ct,
                "A TcXaeShell process is already running but its DTE was not " +
                $"reachable within {timeout.TotalSeconds:F0}s (it may be behind a " +
                "modal dialog or a stale/crashed instance — resolve it manually " +
                "rather than risk a second XAE).");
            return;
        }

        if (!File.Exists(exePath))
            throw new InvalidOperationException(
                $"TcXaeShell.exe not found at expected path: {exePath}");

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (!string.IsNullOrWhiteSpace(launchWithSlnPath))
            psi.ArgumentList.Add(launchWithSlnPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start TcXaeShell.exe.");

        await WaitForReachableDteAsync(probeOrder, timeout, ct,
            $"TcXaeShell.exe started but DTE was not reachable within {timeout.TotalSeconds:F0}s.");
    }

    private async Task WaitForReachableDteAsync(
        IReadOnlyList<string>? probeOrder, TimeSpan timeout,
        CancellationToken ct, string timeoutMessage)
    {
        var sw = Stopwatch.StartNew();
        string? modal = null;
        TimeSpan modalSince = TimeSpan.Zero;
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (TryAttachQuietly(probeOrder)) return;

            // A modal XAE dialog (security/license prompt, orphaned Go-To …) will not go away by
            // waiting — name it instead of burning the whole budget.
            string? seen = XaeModalProbe.FindBlockingDialog();
            if (seen is null) modal = null;
            else if (!string.Equals(seen, modal, StringComparison.Ordinal)) { modal = seen; modalSince = sw.Elapsed; }
            else if (sw.Elapsed - modalSince >= TimeSpan.FromSeconds(5))
                throw new XaeBlockedException(ComCall.BlockedByDialogMessage(modal, sw.Elapsed, "attach"), modal, null);

            await Task.Delay(500, ct);
        }
        throw new TimeoutException(timeoutMessage);
    }

    // Attach probe for the connect/launch path: a busy DTE is "not reachable yet" here —
    // the caller waits for it (never launches a second shell while one exists).
    private static bool TryAttachQuietly(IReadOnlyList<string>? probeOrder)
    {
        try { return GetActiveTcDteOrNull(probeOrder) is not null; }
        catch (Exception ex) when (ComCall.IsBusy(ex)) { return false; }
    }

    /// <summary>
    /// Closes the solution without leaving the shell's "save changes?" prompt open.
    /// </summary>
    /// <remarks>
    /// Reported failure: <c>close_project(saveFirst=true)</c> left the XAE's WPF prompt "Sollen die
    /// Änderungen … gespeichert werden?" open and the call blocked ~196 s until it was clicked by hand;
    /// <c>saveFirst=false</c> never prompted. This used to pass <paramref name="saveFirst"/> straight to
    /// <c>Solution.Close(SaveFirst)</c>: <c>false</c> closes without saving (no prompt), while <c>true</c>
    /// lets the shell save, and the shell asks the user whenever it still considers an item dirty.
    /// Now the save is done explicitly first (documents via the object model, then File.SaveAll — the same
    /// path as save_project) and the close runs with <c>Close(false)</c>, which never prompts. Only when the
    /// explicit save left something dirty (or the dirty state cannot be read) does the close keep the shell's
    /// own save (<c>Close(true)</c>), so nothing is silently discarded. In both cases a watcher scoped to
    /// this XAE answers a prompt that still appears: "Speichern" with saveFirst, "Nicht speichern" without.
    /// </remarks>
    public CloseSolutionResult CloseSolution(bool saveFirst)
    {
        IntPtr xaeMain = ComBusyRetry.Run(() => TryGetXaeMainWindow(GetActiveTcDte()), ProbeTimeout);
        using var prompt = new SaveChangesWatcher(XaeWindowScope.For(xaeMain), saveFirst);
        prompt.Start();
        bool shellSave = false;
        try
        {
            ComBusyRetry.Run(() =>
            {
                dynamic dte = GetActiveTcDte();
                shellSave = false;
                if (saveFirst)
                {
                    SaveAllDocumentsAndSolution(dte);
                    shellSave = HasUnsavedChanges(dte);
                }
                dte.Solution.Close(shellSave);
                // No project is expected after a close — drop the anchor so a stale
                // path can't wrongly satisfy the Q2 guard before the next open.
                ClearExpectedSolutionPath();
            }, SaveCloseTimeout);
        }
        catch (Exception ex)
        {
            prompt.Stop();
            if (prompt.Outcome is { } o)
                throw new InvalidOperationException($"{ex.Message} (save prompt: {o})", ex);
            throw;
        }
        prompt.Stop();
        return new CloseSolutionResult(prompt.Outcome, prompt.Failed, shellSave);
    }

    /// <summary>
    /// True when the solution, a top-level project or an open document still reports unsaved changes —
    /// or when that state cannot be read (unknown counts as dirty: the caller then keeps the shell's own
    /// save instead of discarding).
    /// </summary>
    private static bool HasUnsavedChanges(dynamic dte)
    {
        try
        {
            if (!(bool)dte.Solution.Saved) return true;
            dynamic projects = dte.Solution.Projects;
            int pc = (int)projects.Count;
            for (int i = 1; i <= pc; i++)
            {
                dynamic project = projects.Item(i);
                if (!(bool)project.Saved) return true;
            }
            dynamic docs = dte.Documents;
            int dc = (int)docs.Count;
            for (int i = 1; i <= dc; i++)
            {
                dynamic doc = docs.Item(i);
                if (!(bool)doc.Saved) return true;
            }
            return false;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return true; }
    }

    public void OpenSolution(string slnPath) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dte.Solution.Open(slnPath);
        SetExpectedSolutionPath(slnPath);
    }, OpenTimeout);

    public void SaveAll() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        SaveAllDocumentsAndSolution(dte);
    }, SaveCloseTimeout);

    /// <summary>
    /// Saves, then proves it by looking at the FILES — not at the COM model.
    /// </summary>
    /// <remarks>
    /// Reported failure: a POU stayed stale on disk for nine minutes across two
    /// successful-looking save_project calls; only close_plc_object wrote it.
    /// Root cause: this used ONLY <c>dte.ExecuteCommand("File.SaveAll")</c>, a
    /// shell UI command routed through the active window context, which can
    /// silently do nothing. close_plc_object instead calls <c>doc.Save()</c> on
    /// the Document object — which is why it worked. The old read-back verified
    /// through the same in-memory accessors it had just written, so it could not
    /// possibly see a missing flush.
    ///
    /// It also explains why this failed to reproduce on a scratch POU: with no
    /// editor tab open, the Automation-Interface write lands on disk directly,
    /// so the ineffective command did not matter. It only bites for a file that
    /// is OPEN in the editor — exactly the file someone is working on.
    /// </remarks>
    public SaveProjectResult SaveProjectVerified() => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        string? slnPath = null;
        try { slnPath = (string)dte.Solution.FullName; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        // 1. Snapshot every dirty document together with its file timestamp.
        var pending = new List<(dynamic Doc, string Path, DateTime? Before)>();
        try
        {
            dynamic docs = dte.Documents;
            int count = (int)docs.Count;
            for (int i = 1; i <= count; i++)
            {
                dynamic doc;
                try { doc = docs.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
                bool saved = true;
                try { saved = (bool)doc.Saved; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                if (saved) continue;
                string full = string.Empty;
                try { full = (string)doc.FullName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                if (full.Length == 0) continue;
                pending.Add((doc, full, FileMtimeOrNull(full)));
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* Documents unavailable — the command below still runs */ }

        // 2. Save each dirty document through the OBJECT MODEL. This is the call
        //    close_plc_object uses and the one that demonstrably reaches disk.
        var failures = new List<string>();
        foreach (var p in pending)
        {
            try { p.Doc.Save(); }
            catch (Exception ex) when (ComCall.NotBusy(ex)) { failures.Add($"{Path.GetFileName(p.Path)}: {ex.Message}"); }
        }

        // 3. Still run the command form afterwards: it additionally covers the
        //    project/solution files, which are not Documents.
        try { dte.ExecuteCommand("File.SaveAll"); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        // 4. Verify against the files.
        var results = new List<SavedDocument>();
        foreach (var p in pending)
        {
            bool savedNow = false;
            try { savedNow = (bool)p.Doc.Saved; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            results.Add(new SavedDocument(p.Path, savedNow, p.Before, FileMtimeOrNull(p.Path)));
        }

        var notFlushed = results.Where(r => !r.Flushed).ToList();
        bool ok = notFlushed.Count == 0 && failures.Count == 0;

        string message;
        if (results.Count == 0)
        {
            message = slnPath is null
                ? "Saved. No dirty editor documents were open."
                : $"Saved: {slnPath}. No dirty editor documents were open.";
        }
        else if (ok)
        {
            message = $"Saved and verified on disk: {results.Count} document(s) flushed" +
                      (slnPath is null ? "." : $" ({slnPath}).");
        }
        else
        {
            var detail = notFlushed.Select(r =>
                $"{Path.GetFileName(r.Path)} [savedFlag={r.SavedFlagAfter}, fileChanged={r.FileChanged}]");
            message = "[Error] Save did NOT reach the disk for: " + string.Join(", ", detail) +
                      (failures.Count > 0 ? " | save errors: " + string.Join("; ", failures) : "") +
                      ". The file on disk is still the old version — do not build or commit from it.";
        }

        return new SaveProjectResult(ok, slnPath, results, message);
    }, SaveCloseTimeout);

    // Saves open documents via the object model first, then the command form for
    // the project/solution files. Used by every internal SaveAll() caller so they
    // get the reliable path too, not just the save_project tool.
    private static void SaveAllDocumentsAndSolution(dynamic dte)
    {
        try
        {
            dynamic docs = dte.Documents;
            int count = (int)docs.Count;
            for (int i = 1; i <= count; i++)
            {
                try
                {
                    dynamic doc = docs.Item(i);
                    if (!(bool)doc.Saved) doc.Save();
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* one unsaveable document must not block the rest */ }
            }
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
        dte.ExecuteCommand("File.SaveAll");
    }

    private static DateTime? FileMtimeOrNull(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }
}
