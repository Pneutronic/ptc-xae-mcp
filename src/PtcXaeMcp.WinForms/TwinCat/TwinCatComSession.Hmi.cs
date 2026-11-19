using System.Globalization;
using System.Runtime.InteropServices;
#if TCHMI
using TcHmiAutomation;
#endif

namespace PtcXaeMcp.WinForms.TwinCat;

// hmi-server — TwinCAT HMI (TE2000) through its Automation Interface.
// The interface is NOT documented on Infosys (site
// search "TcHmiAutomation"/"ITcHmiServer": 0 results, 2026-09-25); every member
// used here was taken from reflection on the installed TcHmiAutomation.dll and
// proven live from this net10 x64 process against TcXaeShell64:
//   - dte.GetObject("Beckhoff.TcHmi.1.12") returns the ITcHmiAutomation
//     (the id is a string inside TcHmiPackage.dll of TE2000 14.17.22.1),
//   - early-bound casts to ITcHmiProject/ITcHmiItem/ITcHmiError/ITcHmiServer3
//     work out-of-proc; late binding does NOT (an IDispatch interface exposes
//     only its own members, and GetHmiProject(string) binds to the Project
//     overload) — hence the typed reference behind $(TcHmi)/TCHMI.
//   - ReadSymbols with ONE unknown name returns null for the whole batch
//     (LastError "SYMBOL_NOT_MAPPED"); ReadSymbol of an ADS symbol without a
//     reachable PLC returns null (LastError "HMI_ADS_E_SYNC_TIMEOUT").
//   - ITcHmiProjectInformation.ProjectGuid is the project TYPE guid
//     ({FE7A1B72-…}, the .sln project-kind) — deliberately not surfaced.
internal sealed partial class TwinCatComSession
{
    internal const string HmiAutomationId = "Beckhoff.TcHmi.1.12";
    private static readonly TimeSpan HmiTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Attaching parses the whole TMC (a real PLC TMC is ~2 MB / several hundred
    /// symbols) and converts it into the binary AdsSymbolUpAndDownload segment, so it gets
    /// a wider budget than an ordinary server call.</summary>
    private static readonly TimeSpan HmiTmcTimeout = TimeSpan.FromMinutes(5);

    private const int HmiMaxFileNodes = 400;

    public string? FindHmiBlockingDialog()
    {
        try
        {
            return Win32DialogAutomation.TryFindHmiEngineeringLicenseDialog()
                ?? Win32DialogAutomation.TryFindHmiMigrationDialog();
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return null; }
    }

    private const string HmiAutomationDllName = "TcHmiAutomation.dll";

    /// <inheritdoc/>
    public void EnsureHmiAutomationAvailable()
    {
#if TCHMI
        // Same places the runtime looks: next to the exe (a -p:BundleBeckhoffDlls=true build) or the
        // TE2000 bin folder that BeckhoffAssemblyResolver probes (#156 item 4).
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, HmiAutomationDllName))) return;
        IReadOnlyList<string> dirs = TwinCatPaths.GetHmiAutomationDirCandidates();
        if (dirs.Any(d => File.Exists(Path.Combine(d, HmiAutomationDllName)))) return;

        string? twinCatDir = TwinCatPaths.GetTwinCatDir();
        string looked = twinCatDir is null
            ? "no TwinCAT installation was found (registry InstallDir, %TwinCAT3Dir%)"
            : "not under " + Path.Combine(twinCatDir, "Functions", "TE2000-HMI-Engineering", "bin");
        throw new InvalidOperationException(
            $"TwinCAT HMI Engineering (TE2000) is not installed on this machine: {HmiAutomationDllName} is " +
            $"neither next to the host exe nor in the TE2000 installation ({looked}). Install TE2000 to use " +
            "the twincat_hmi_* tools; everything else works without it.");
#else
        throw TwinCatNotBuilt.HmiException();
#endif
    }

#if !TCHMI
    public HmiProjectList ListHmiProjects() => throw TwinCatNotBuilt.HmiException();
    public HmiProjectInfo GetHmiProjectInfo(string hmiProject, bool includeFiles, int maxDepth) => throw TwinCatNotBuilt.HmiException();
    public IReadOnlyList<HmiMappedSymbol> ListHmiMappedSymbols(string hmiProject, bool refresh) => throw TwinCatNotBuilt.HmiException();
    public IReadOnlyList<HmiSymbolValue> ReadHmiSymbols(string hmiProject, IReadOnlyList<string> symbols) => throw TwinCatNotBuilt.HmiException();
    public HmiServerControlResult ControlHmiServer(string hmiProject, string action, int timeoutMs) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult MapHmiSymbol(string hmiProject, string mapName, string internalName, string domain, string? subSymbolName) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult UnmapHmiSymbol(string hmiProject, string mapName, string domain, bool removeHistorizedData) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult WriteHmiSymbol(string hmiProject, string symbol, string valueJson) => throw TwinCatNotBuilt.HmiException();
    public HmiFileContent ReadHmiFile(string hmiProject, string path) => throw TwinCatNotBuilt.HmiException();
    public bool HmiItemExists(string hmiProject, string path) => throw TwinCatNotBuilt.HmiException();
    public IReadOnlyList<string> AddHmiItem(string hmiProject, string folder, string name, string templateKey) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult WriteHmiFile(string hmiProject, string path, string source) => throw TwinCatNotBuilt.HmiException();
    public HmiImportResult ImportHmiFile(string hmiProject, string folder, string name, string sourcePath) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult BuildHmiProject(string hmiProject, string configuration) => throw TwinCatNotBuilt.HmiException();
    public HmiItemInfo? DescribeHmiItem(string hmiProject, string path) => throw TwinCatNotBuilt.HmiException();
    public bool IsHmiFileOpen(string hmiProject, string path) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult DeleteHmiItem(string hmiProject, string path) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult AttachHmiTmcFile(string hmiProject, string rootNamespace, string tmcFilePath) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult DetachHmiTmcFile(string hmiProject, string rootNamespace) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult SetHmiRuntimeTmc(string hmiProject, string runtime, string dataSymbolUpAndDownload, bool useTmc,
        bool createTmcObject = false) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult RefreshHmiDefinitions(string hmiProject, string domain) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult AddHmiInternalSymbol(string hmiProject, string name, string dataType, string? defaultValueJson, bool persist, bool readOnly) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult RemoveHmiInternalSymbol(string hmiProject, string name) => throw TwinCatNotBuilt.HmiException();
    public HmiOperationResult MapHmiOfflineSymbol(string hmiProject, string mapName, string dataType, string? defaultValueJson, bool online, bool persist) => throw TwinCatNotBuilt.HmiException();
    public HmiPublishOutcome PublishHmiProject(string hmiProject, HmiPublishTarget target, TimeSpan timeout) => throw TwinCatNotBuilt.HmiException();
#else
    // ---- hmi-tf2000: publish (members from reflection on TcHmiAutomation.dll, TE2000 14.x) ----
    //   ITcHmiProject.Publish(profileName | ITcHmiProfile, updateUi, ITcHmiPublishCallback), GetProfileInstance(name),
    //   IsPublishRunning(), GetPublishResult() → ITcHmiPublishResult.
    // Live 2026-11-19 (TE2000 14.17, 64-bit XAE):
    //   - Publish(ITcHmiProfile) SAVES the profile to Properties/tchmipublish.config.json (the coordinator restores the
    //     file after every publish).
    //   - ITcHmiProfile.ServerPassword goes unchanged into "encryptedTcHmiServerPassword" (IL of TcHmiProfile.ApplyTo);
    //     the XAE then decrypts it with its own keys. A plain password AND an empty one both fail with "Der Wert darf
    //     nicht NULL sein. Parametername: password" — an in-memory profile never publishes. So only Publish(profileName)
    //     of a profile the engineer saved in the XAE (password encrypted there) is used; the MCP never sees a password.
    //   - NO callback: the XAE keeps an ITcHmiPublishCallback and calls it during LATER HMI builds. Once the host process
    //     that passed it is gone, the next build fails with "Der RPC-Server ist nicht verfügbar (0x800706BA)" and the XAE
    //     then rejects every call (RETRYLATER) until it is killed — reproduced twice. A second Publish(unknown profile,
    //     null) does not replace it. So Publish gets null and the outcome comes from polling plus the target read-back.
    // Publish is not retried after it was issued: only the setup and the polling go through ComBusyRetry.

    /// <summary>How long a publish may take to show IsPublishRunning after Publish returned.</summary>
    private static readonly TimeSpan PublishStartGrace = TimeSpan.FromSeconds(20);

    public HmiPublishOutcome PublishHmiProject(string hmiProject, HmiPublishTarget target, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (started, error) = ComBusyRetry.Run(() =>
        {
            ITcHmiProject project = RequireHmiProject(hmiProject);
            if (project.IsPublishRunning()) return (false, "a publish of this project is already running");
            if (!project.IsValidPublishProfile(target.ProfileName))
                return (false, $"the XAE does not accept publish profile '{target.ProfileName}' as valid");
            bool ok = project.Publish(target.ProfileName, false, null);
            return (ok, ok ? null : HmiLastError(project));
        }, HmiTimeout);
        if (!started) return new HmiPublishOutcome(false, false, null, null, null, error, watch.Elapsed.TotalSeconds);

        // Running → wait until it stops; never seen running → give it PublishStartGrace to start (or to have finished).
        TimeSpan returnedAt = watch.Elapsed;
        bool running = false, seenRunning = false;
        while (watch.Elapsed < timeout)
        {
            running = ComBusyRetry.Run(() => RequireHmiProject(hmiProject).IsPublishRunning(), HmiTimeout);
            seenRunning |= running;
            if (!running && (seenRunning || watch.Elapsed - returnedAt > PublishStartGrace)) break;
            Thread.Sleep(1000);
        }
        var (completed, code, submission, lastError) = ComBusyRetry.Run(() =>
        {
            ITcHmiProject project = RequireHmiProject(hmiProject);
            return project.GetPublishResult() is TcHmiAutomation.Publish.ITcHmiPublishResult r
                ? ((bool?)r.IsCompleted, (int?)r.Result, (int?)r.SubmissionId, HmiLastError(project))
                : (null, null, null, HmiLastError(project));
        }, HmiTimeout);
        return new HmiPublishOutcome(true, running, completed, code, submission, lastError, watch.Elapsed.TotalSeconds);
    }

    // ---- hmi-internal-symbols (#165) — decompiled TE2000 14.17.22.1, NOT verified live ----
    //   - AddInternalSymbol is void and adds without a duplicate check (List.Add), then
    //     SaveResync writes Properties\tchmiconfig.json — possibly asynchronously,
    //   - RemoveInternalSymbol returns false unconditionally,
    //   - MapOfflineSymbol sends AddSymbol {DOMAIN:"TcHmiSrv", MAPPING:internalName,
    //     USEMAPPING:online, SCHEMA:{type|$ref, default}}; false + LastError on a server error.
    // The coordinator pre-checks duplicates and decides by reading the files back. The add runs
    // in a retry of its OWN (same discipline as AddHmiItem): a busy rejection
    // (RPC_E_CALL_REJECTED) means that call was not executed, but a busy error in a later step
    // must never re-run an add that went through — List.Add would store the name twice.

    public HmiOperationResult AddHmiInternalSymbol(string hmiProject, string name, string dataType,
        string? defaultValueJson, bool persist, bool readOnly)
    {
        ITcHmiProject project = ComBusyRetry.Run(() => RequireHmiProject(hmiProject), HmiTimeout);
        ITcHmiInternalSymbol symbol = ComBusyRetry.Run(() =>
            project.GetInternalSymbolInstance(
                name, HmiInternalSymbols.ToComValue(defaultValueJson), dataType, persist, readOnly)
            ?? throw new InvalidOperationException(
                $"GetInternalSymbolInstance('{name}') returned null: {HmiLastError(project) ?? "no error text"}"),
            HmiTimeout);
        ComBusyRetry.Run(() => project.AddInternalSymbol(symbol), HmiTimeout);
        return new HmiOperationResult(true, HmiLastError(project));
    }

    public HmiOperationResult RemoveHmiInternalSymbol(string hmiProject, string name) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiProject project = RequireHmiProject(hmiProject);
            project.RemoveInternalSymbol(name); // always false — see above
            return new HmiOperationResult(true, HmiLastError(project));
        }, HmiTimeout);

    public HmiOperationResult MapHmiOfflineSymbol(string hmiProject, string mapName, string dataType,
        string? defaultValueJson, bool online, bool persist) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            bool ok = server.MapOfflineSymbol(mapName, "", dataType, online, persist,
                HmiInternalSymbols.ToComValue(defaultValueJson));
            return new HmiOperationResult(ok, ok ? null : HmiLastError(server));
        }, HmiTimeout);

    // ---- hmi-views: project files (spike 2026-09-25: LookupChild accepts '/' and '\',
    // AddItem creates dependent files, SetSource/GetSource round-trip, SaveAllFiles) ----

    public HmiFileContent ReadHmiFile(string hmiProject, string path) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiItem? item = ((ITcHmiItem)RequireHmiProject(hmiProject)).LookupChild(path);
            if (item is null)
                throw new InvalidOperationException($"'{path}' not found in HMI project '{hmiProject}'.");
            if (item is not ITcHmiFile file)
                throw new InvalidOperationException($"'{path}' is a folder or not a text file of the HMI project.");
            return new HmiFileContent(path, file.GetSource() ?? "");
        }, HmiTimeout);

    public bool HmiItemExists(string hmiProject, string path) =>
        ComBusyRetry.Run(() => ((ITcHmiItem)RequireHmiProject(hmiProject)).LookupChild(path) is not null, HmiTimeout);

    // Creating steps retry ON THEIR OWN and first look for what an earlier attempt created.
    // Stage-HMI run 2026-09-25: AddItem, the child enumeration and SaveAllFiles shared one
    // ComBusyRetry lambda; a busy error after a successful AddItem re-ran AddItem on the same
    // name and the XAE blocked on its modal "A file with the same name … already exists. Do you
    // want to overwrite it?" (answering No leaves "<name> - Copy.content").
    public IReadOnlyList<string> AddHmiItem(string hmiProject, string folder, string name, string templateKey)
    {
        if (!Enum.TryParse(templateKey, out HmiTemplates template))
            throw new InvalidOperationException($"Unknown HMI template '{templateKey}'.");
        string expected = name + HmiTemplatesUtils.GetFileExtension(template);
        ITcHmiItem parent = ComBusyRetry.Run(() => EnsureHmiFolder(RequireHmiProject(hmiProject), folder), HmiTimeout);
        ITcHmiItem created = ComBusyRetry.Run(() =>
            parent.LookupChild(expected) ?? parent.AddItem(name, template)
            ?? throw new InvalidOperationException(
                $"AddItem('{name}', {templateKey}) in '{folder}' failed: {HmiLastError(parent) ?? "no error text"}"), HmiTimeout);
        IReadOnlyList<string> paths = ComBusyRetry.Run(() =>
        {
            var list = new List<string> { CleanHmiPath(created.PathName) };
            foreach (ITcHmiItem child in created.Childs ?? Array.Empty<ITcHmiItem>())
                if (child is not null) list.Add(CleanHmiPath(child.PathName));
            return (IReadOnlyList<string>)list;
        }, HmiTimeout);
        ComBusyRetry.Run(() => RequireHmiAutomation().SaveAllFiles(), HmiTimeout);
        return paths;
    }

    public HmiOperationResult WriteHmiFile(string hmiProject, string path, string source) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiItem? item = ((ITcHmiItem)RequireHmiProject(hmiProject)).LookupChild(path);
            if (item is not ITcHmiFile file)
                throw new InvalidOperationException($"'{path}' is not an existing file of HMI project '{hmiProject}'.");
            bool ok = file.SetSource(source);
            if (ok) RequireHmiAutomation().SaveAllFiles();
            return new HmiOperationResult(ok, ok ? null : HmiLastError(file));
        }, HmiTimeout);

    public HmiImportResult ImportHmiFile(string hmiProject, string folder, string name, string sourcePath)
    {
        // Same retry discipline as AddHmiItem: never add the same file twice.
        ITcHmiItem parent = ComBusyRetry.Run(() => EnsureHmiFolder(RequireHmiProject(hmiProject), folder), HmiTimeout);
        string? engineError = null;
        ITcHmiItem created = ComBusyRetry.Run(() =>
        {
            if (parent.LookupChild(name) is { } existing) return existing;
            try
            {
                return parent.AddExistingItem(name, sourcePath)
                    ?? throw new InvalidOperationException(
                        $"AddExistingItem('{name}') in '{folder}' failed: {HmiLastError(parent) ?? "no error text"}");
            }
            // Issue #247: for a .js file TE2000 adds the item, then fails on a WPF-owned object ("Der aufrufende
            // Thread kann nicht auf dieses Objekt zugreifen…") — our out-of-proc call is not on the XAE's UI
            // thread. Utilities.AddExistingItem (TcHmiProjectBase, decompiled) catches every exception into
            // LastError + null. The project tree is the read-back: when the item is there, the import landed.
            // The message goes up so the coordinator can check the copy on disk before calling it a success.
            // A genuine failure leaves no item and keeps the error.
            catch (Exception ex) when (ComCall.NotBusy(ex) && parent.LookupChild(name) is { } landed)
            {
                engineError = ex.Message;
                return landed;
            }
        }, HmiTimeout);
        string path = ComBusyRetry.Run(() => CleanHmiPath(created.PathName), HmiTimeout);
        ComBusyRetry.Run(() => RequireHmiAutomation().SaveAllFiles(), HmiTimeout);
        // Issue #247: a .js only becomes a runtime dependency (tchmiconfig.json "dependencyFiles") when TE2000 saves
        // its CONFIG, which recomputes that list from the project files; SaveAllFiles does not. Re-applying the
        // current websocket interval is a no-op change that triggers exactly that save (TcHmiConfig SaveResync).
        if (name.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            ComBusyRetry.Run(() =>
            {
                ITcHmiProject project = RequireHmiProject(hmiProject);
                int interval = Convert.ToInt32(project.GetConfigValue(ConfigFields.WebsocketIntervalTime), CultureInfo.InvariantCulture);
                project.ChangeWebsocketIntervalTime(interval);
            }, HmiTimeout);
        return new HmiImportResult([path], engineError);
    }

    public HmiOperationResult BuildHmiProject(string hmiProject, string configuration) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiProject project = RequireHmiProject(hmiProject);
            bool ok = project.Build(configuration, false);
            return new HmiOperationResult(ok, ok ? null : HmiLastError(project));
        }, TimeSpan.FromMinutes(10));

    public bool IsHmiFileOpen(string hmiProject, string path) =>
        ComBusyRetry.Run(() => ((ITcHmiItem)RequireHmiProject(hmiProject)).LookupChild(path) is ITcHmiFile file && file.IsOpen(),
            HmiTimeout);

    public HmiItemInfo? DescribeHmiItem(string hmiProject, string path) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiItem? item = ((ITcHmiItem)RequireHmiProject(hmiProject)).LookupChild(path);
            return item is null ? null : new HmiItemInfo(path, item is ITcHmiFile, CountDescendants(item, 10_000));
        }, HmiTimeout);

    public HmiOperationResult DeleteHmiItem(string hmiProject, string path)
    {
        ITcHmiProject project = ComBusyRetry.Run(() => RequireHmiProject(hmiProject), HmiTimeout);
        var root = (ITcHmiItem)project;
        // A retry after a DeleteChild that went through finds nothing left: done, not a second delete.
        bool ok = ComBusyRetry.Run(() => root.LookupChild(path) is null || root.DeleteChild(path), HmiTimeout);
        ComBusyRetry.Run(() => RequireHmiAutomation().SaveAllFiles(), HmiTimeout);
        return new HmiOperationResult(ok, ok ? null : HmiLastError(project));
    }

    // Items below a folder, capped — the popup only needs an order of magnitude.
    private static int CountDescendants(ITcHmiItem item, int cap)
    {
        int count = 0;
        var pending = new Stack<ITcHmiItem>();
        pending.Push(item);
        while (pending.Count > 0 && count < cap)
            foreach (ITcHmiItem child in pending.Pop().Childs ?? Array.Empty<ITcHmiItem>())
            {
                if (child is null) continue;
                count++;
                pending.Push(child);
            }
        return count;
    }

    // Walks/creates the folder chain below the project root ("" = root).
    private static ITcHmiItem EnsureHmiFolder(ITcHmiProject project, string folder)
    {
        ITcHmiItem current = project;
        if (string.IsNullOrEmpty(folder)) return current;
        foreach (string segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            ITcHmiItem? next = current.LookupChild(segment) ?? current.AddFolder(segment);
            current = next ?? throw new InvalidOperationException(
                $"Folder '{segment}' could not be created: {HmiLastError(current) ?? "no error text"}");
        }
        return current;
    }

    private static string CleanHmiPath(string? pathName) => (pathName ?? "").Replace('\\', '/').TrimEnd('/');

    public HmiOperationResult WriteHmiSymbol(string hmiProject, string symbol, string valueJson) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            bool ok = server.WriteSymbolJson(symbol, valueJson);
            return new HmiOperationResult(ok, ok ? null : HmiLastError(server));
        }, HmiTimeout);

    // Raw mutating calls. A busy rejection (RPC_E_CALL_REJECTED) means the call was
    // NOT executed, so ComBusyRetry re-issuing it cannot double-apply.
    public HmiOperationResult MapHmiSymbol(string hmiProject, string mapName, string internalName, string domain, string? subSymbolName) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            bool ok = string.IsNullOrEmpty(subSymbolName)
                ? server.MapSymbol(mapName, internalName, domain)
                : ((ITcHmiServer3)server).MapSymbol(mapName, internalName, subSymbolName, domain);
            return new HmiOperationResult(ok, ok ? null : HmiLastError(server));
        }, HmiTimeout);

    public HmiOperationResult UnmapHmiSymbol(string hmiProject, string mapName, string domain, bool removeHistorizedData) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            bool ok = server.UnMapSymbol(mapName, domain, removeHistorizedData);
            return new HmiOperationResult(ok, ok ? null : HmiLastError(server));
        }, HmiTimeout);

    // ---- hmi-ads-tmc: offline symbols from a PLC project's TMC file ----------
    // Verified by decompiling TE2000 14.17.22.1 (TcHmiProjectMiddleware,
    // TcHmiServerSymbolRootLazy.AttachTmcFile / TcHmiSymbolProviderLazy.DetachTmc):
    //   - the list is a project property, HmiTmcFiles in the .hmiproj, so it needs a save,
    //   - AttachTmcFile returns TRUE unless it throws; a real error is shown as a modal
    //     message box in the XAE (MessageBox.WarningWinMain) — the coordinator's pre-checks
    //     cover both error cases the implementation can produce so that box stays unreachable,
    //   - DetachTmcFile returns FALSE unconditionally, after removing the entry.
    // Both are therefore reported as "raw call went through"; the truth is the read-back.
    // Re-issuing after a busy rejection is safe: attach refuses a duplicate, detach is a
    // filter over the list.

    public HmiOperationResult AttachHmiTmcFile(string hmiProject, string rootNamespace, string tmcFilePath)
    {
        string? lastError = ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            server.AttachTmcFile(rootNamespace, tmcFilePath);
            return HmiLastError(server);
        }, HmiTmcTimeout);
        ComBusyRetry.Run(() => RequireHmiAutomation().SaveAllFiles(), HmiTimeout);
        return new HmiOperationResult(true, lastError);
    }

    public HmiOperationResult DetachHmiTmcFile(string hmiProject, string rootNamespace)
    {
        string? lastError = ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            server.DetachTmcFile(rootNamespace);
            return HmiLastError(server);
        }, HmiTimeout);
        ComBusyRetry.Run(() => RequireHmiAutomation().SaveAllFiles(), HmiTimeout);
        return new HmiOperationResult(true, lastError);
    }

    // ---- #161: server-side TMC of an ADS runtime — decompiled TE2000 14.17.22.1, verified live 2026-11-18 ----
    //   TcHmiServerCommunication.AttachTmcFile sends exactly these two writes, path first, in one
    //   request; DetachTmcFile the same with "" / false. WriteSymbolJson sends a generic write of the
    //   named symbol (SrvComm.WriteSymbolValue, option Replace — irrelevant for a scalar leaf) and
    //   returns false with LastError when the server answers with an error. Each write retries on its
    //   own: re-issuing a write that a busy XAE rejected writes the same value, so it cannot double-apply.

    //   A runtime without a TMC object refuses both leaf writes (live: INVALID_SUBSYMBOL for …::TMC); writing
    //   the object as a whole was accepted and read back (engineering server 24.0.7.0).
    public HmiOperationResult SetHmiRuntimeTmc(string hmiProject, string runtime, string dataSymbolUpAndDownload, bool useTmc,
        bool createTmcObject = false)
    {
        if (createTmcObject)
        {
            string objectSymbol = HmiRuntimeTmc.TmcObjectSymbol(runtime);
            string objectJson = HmiRuntimeTmc.TmcObjectJson(dataSymbolUpAndDownload, useTmc);
            string? objectError = ComBusyRetry.Run(() =>
            {
                ITcHmiServer server = RequireHmiServer(hmiProject);
                return server.WriteSymbolJson(objectSymbol, objectJson) ? null : HmiLastError(server) ?? "no error text";
            }, HmiTimeout);
            return objectError is null
                ? new HmiOperationResult(true, null)
                : new HmiOperationResult(false, $"{objectSymbol}: {objectError}");
        }

        string pathSymbol = HmiRuntimeTmc.ConfigSymbol(runtime, HmiRuntimeTmc.DataSymbolUpAndDownload);
        string flagSymbol = HmiRuntimeTmc.ConfigSymbol(runtime, HmiRuntimeTmc.UseTmc);
        string pathJson = System.Text.Json.JsonSerializer.Serialize(dataSymbolUpAndDownload);
        string flagJson = useTmc ? "true" : "false";

        string? pathError = ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            return server.WriteSymbolJson(pathSymbol, pathJson) ? null : HmiLastError(server) ?? "no error text";
        }, HmiTimeout);
        if (pathError is not null)
            return new HmiOperationResult(false, $"{pathSymbol}: {pathError}");

        string? flagError = ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            return server.WriteSymbolJson(flagSymbol, flagJson) ? null : HmiLastError(server) ?? "no error text";
        }, HmiTimeout);
        return flagError is null
            ? new HmiOperationResult(true, null)
            : new HmiOperationResult(false, $"{flagSymbol}: {flagError} ({HmiRuntimeTmc.DataSymbolUpAndDownload} was already written)");
    }

    // hmi-offline-types: TcHmiServerCommunication.RefreshDefinitions sends a write of the function symbol
    // RefreshDefinitions with the domain as value. Re-issuing after a busy rejection only refreshes again.
    public HmiOperationResult RefreshHmiDefinitions(string hmiProject, string domain) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            bool ok = server.WriteSymbolJson("RefreshDefinitions", System.Text.Json.JsonSerializer.Serialize(domain));
            return new HmiOperationResult(ok, ok ? null : HmiLastError(server));
        }, HmiTimeout);

    public HmiServerControlResult ControlHmiServer(string hmiProject, string action, int timeoutMs) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            switch (action)
            {
                case "start": server.Start(); break;
                case "shutdown": server.Shutdown(); break;
                case "refresh_symbols": server.RefreshSymbols(); break;
                default: throw new InvalidOperationException($"Unknown HMI server action '{action}'.");
            }

            // The three calls are void; poll the extension states for the effect.
            Func<IReadOnlyList<HmiServerExtension>, bool> settled = action switch
            {
                "start" => exts => exts.Any(e => e.Loaded),
                "shutdown" => exts => !exts.Any(e => e.Loaded),
                _ => _ => true,
            };
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs));
            IReadOnlyList<HmiServerExtension> extensions = TryReadHmiExtensions(server);
            while (!settled(extensions) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(500);
                extensions = TryReadHmiExtensions(server);
            }
            return new HmiServerControlResult(action, true, settled(extensions), null, extensions);
        }, HmiTimeout + TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs)));

    // A server that is shutting down may refuse the query; that reads as
    // "nothing loaded" rather than a failure of the control call itself.
    private static IReadOnlyList<HmiServerExtension> TryReadHmiExtensions(ITcHmiServer server)
    {
        try { return ReadHmiExtensions(server); }
        catch (COMException ex) when (ComCall.NotBusy(ex)) { return Array.Empty<HmiServerExtension>(); }
    }

    public IReadOnlyList<HmiMappedSymbol> ListHmiMappedSymbols(string hmiProject, bool refresh) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);
            var list = new List<HmiMappedSymbol>();
            foreach (ITcHmiMappedSymbol s in server.GetMappedSymbols(refresh) ?? Array.Empty<ITcHmiMappedSymbol>())
            {
                if (s is null) continue;
                list.Add(ToRecord(s));
            }
            return (IReadOnlyList<HmiMappedSymbol>)list;
        }, HmiTimeout);

    public IReadOnlyList<HmiSymbolValue> ReadHmiSymbols(string hmiProject, IReadOnlyList<string> symbols) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiServer server = RequireHmiServer(hmiProject);

            // Batch first. Verified live: ONE unknown name makes ReadSymbols return
            // null for the whole batch (LastError "SYMBOL_NOT_MAPPED"), so anything
            // short of a complete answer falls back to one ReadSymbol per name.
            ITcHmiSymbol[]? batch = null;
            try { batch = server.ReadSymbols(symbols.ToArray()); }
            catch (COMException ex) when (ComCall.NotBusy(ex)) { batch = null; }

            if (batch is not null && batch.Length == symbols.Count && batch.All(s => s is not null))
            {
                var byName = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (ITcHmiSymbol s in batch)
                    byName[s.SymbolName ?? ""] = s.Value;
                if (symbols.All(byName.ContainsKey))
                    return (IReadOnlyList<HmiSymbolValue>)symbols
                        .Select(n => new HmiSymbolValue(n, true, byName[n], null)).ToList();
            }

            var values = new List<HmiSymbolValue>(symbols.Count);
            foreach (string name in symbols)
            {
                ITcHmiSymbol? s = null;
                string? error = null;
                try { s = server.ReadSymbol(name); }
                catch (COMException ex) when (ComCall.NotBusy(ex)) { error = HmiCoordinator.CondenseServerError(ex.Message); }
                if (s is null)
                    values.Add(new HmiSymbolValue(name, false, null,
                        error ?? HmiLastError(server) ?? "ReadSymbol returned no value."));
                else
                    values.Add(new HmiSymbolValue(name, true, s.Value, null));
            }
            return values;
        }, HmiTimeout);

    private static ITcHmiServer RequireHmiServer(string hmiProject) =>
        RequireHmiProject(hmiProject).GetServerInterface()
        ?? throw new InvalidOperationException($"HMI project '{hmiProject}' returned no server interface.");

    private static HmiMappedSymbol ToRecord(ITcHmiMappedSymbol s) =>
        new(s.MappedName ?? "", s.Domain ?? "", NullIfEmpty(s.Type), NullIfEmpty(s.DataTypeDisplayName),
            s.ReadOnly, s.Hidden);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public HmiProjectList ListHmiProjects() =>
        ComBusyRetry.Run(() =>
        {
            dynamic dte = GetActiveTcDte();
            ITcHmiAutomation? hmi = TryGetHmiAutomation(dte, out string? attachError);
            if (hmi is null)
            {
                // Without an .hmiproj the TE2000 package need not be loaded at all,
                // so a failing GetObject is then an empty answer, not an error.
                if (!SolutionContainsHmiProject(dte))
                    return new HmiProjectList(HmiAutomationId, 0, Array.Empty<HmiProjectEntry>(), FindHmiBlockingDialog());
                throw new InvalidOperationException(attachError);
            }

            var projects = new List<HmiProjectEntry>();
            foreach (ITcHmiProject p in hmi.GetHmiProjects() ?? Array.Empty<ITcHmiProject>())
            {
                if (p is null) continue;
                ITcHmiProjectInformation info = p.GetProjectInformation();
                projects.Add(new HmiProjectEntry(
                    info?.ProjectName ?? "",
                    info?.ProjectDirectory ?? "",
                    SafeHmiBool(() => p.IsReady()),
                    SafeHmiBool(() => p.IsPublishRunning())));
            }
            return new HmiProjectList(HmiAutomationId, SafeHmiVersion(hmi), projects, FindHmiBlockingDialog());
        }, HmiTimeout);

    public HmiProjectInfo GetHmiProjectInfo(string hmiProject, bool includeFiles, int maxDepth) =>
        ComBusyRetry.Run(() =>
        {
            ITcHmiProject p = RequireHmiProject(hmiProject);
            ITcHmiProjectInformation info = p.GetProjectInformation();

            var config = new SortedDictionary<string, string?>(StringComparer.Ordinal);
            foreach (ConfigFields field in Enum.GetValues<ConfigFields>())
            {
                string? value;
                try { value = Convert.ToString(p.GetConfigValue(field), CultureInfo.InvariantCulture); }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { value = null; }
                config[field.ToString()] = value;
            }

            IReadOnlyList<HmiServerExtension> extensions = ReadHmiExtensions(p.GetServerInterface());

            IReadOnlyList<HmiFileNode>? files = null;
            bool truncated = false;
            if (includeFiles)
            {
                int budget = HmiMaxFileNodes;
                files = ReadHmiTree(p, 1, Math.Clamp(maxDepth, 1, 6), ref budget);
                truncated = budget <= 0;
            }

            return new HmiProjectInfo(
                info?.ProjectName ?? hmiProject,
                info?.ProjectDirectory ?? "",
                SafeHmiBool(() => p.IsReady()),
                SafeHmiBool(() => p.IsPublishRunning()),
                config,
                extensions,
                files,
                truncated);
        }, HmiTimeout);

    // ---- helpers -------------------------------------------------------------

    private static ITcHmiAutomation? TryGetHmiAutomation(dynamic dte, out string? error)
    {
        error = null;
        object? raw;
        try { raw = dte.GetObject(HmiAutomationId); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            error = $"dte.GetObject(\"{HmiAutomationId}\") failed: {ex.Message} " +
                    "Is TwinCAT HMI (TE2000) installed and the HMI project loaded?";
            return null;
        }

        if (raw is ITcHmiAutomation direct) return direct;

        // Unofficial Beckhoff sample (benhar-dev/tchmi-automation-interface-samples,
        // Utilities.GetAutomationInterface): some versions hand back the EnvDTE.Project
        // whose .Object is the automation interface.
        try
        {
            object? inner = ((dynamic)raw!).Object;
            if (inner is ITcHmiAutomation viaProject) return viaProject;
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { /* fall through to the error below */ }

        error = $"dte.GetObject(\"{HmiAutomationId}\") returned " +
                $"{(raw is null ? "null" : raw.GetType().Name)}, not ITcHmiAutomation.";
        return null;
    }

    private static ITcHmiAutomation RequireHmiAutomation()
    {
        dynamic dte = GetActiveTcDte();
        return TryGetHmiAutomation(dte, out string? error)
            ?? throw new InvalidOperationException(error);
    }

    // Resolves by ITcHmiProjectInformation.ProjectName — the same name
    // ListHmiProjects reports — instead of GetHmiProject(string), whose key is
    // not documented.
    private static ITcHmiProject RequireHmiProject(string hmiProject)
    {
        ITcHmiAutomation hmi = RequireHmiAutomation();
        foreach (ITcHmiProject p in hmi.GetHmiProjects() ?? Array.Empty<ITcHmiProject>())
        {
            if (p is null) continue;
            string? name = p.GetProjectInformation()?.ProjectName;
            if (string.Equals(name, hmiProject, StringComparison.OrdinalIgnoreCase))
                return p;
        }
        throw new InvalidOperationException($"HMI project '{hmiProject}' not found in the open solution.");
    }

    private static bool SolutionContainsHmiProject(dynamic dte)
    {
        try { return ContainsHmiProject(dte.Solution.Projects, depth: 0); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return false; }
    }

    // Walks top-level projects and solution folders (their sub-projects hang off
    // ProjectItems[i].SubProject).
    private static bool ContainsHmiProject(dynamic projects, int depth)
    {
        if (projects is null || depth > 3) return false;
        int count = (int)projects.Count;
        for (int i = 1; i <= count; i++)
        {
            dynamic project = projects.Item(i);
            if (project is null) continue;
            string fullName = "";
            try { fullName = (string)project.FullName ?? ""; } catch (Exception ex) when (ComCall.NotBusy(ex)) { }
            if (fullName.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase)) return true;

            dynamic? items = null;
            try { items = project.ProjectItems; } catch (Exception ex) when (ComCall.NotBusy(ex)) { }
            if (items is null) continue;
            int itemCount = 0;
            try { itemCount = (int)items.Count; } catch (Exception ex) when (ComCall.NotBusy(ex)) { }
            for (int j = 1; j <= itemCount; j++)
            {
                dynamic? sub = null;
                try { sub = items.Item(j).SubProject; } catch (Exception ex) when (ComCall.NotBusy(ex)) { }
                if (sub is null) continue;
                string subName = "";
                try { subName = (string)sub.FullName ?? ""; } catch (Exception ex) when (ComCall.NotBusy(ex)) { }
                if (subName.EndsWith(".hmiproj", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static IReadOnlyList<HmiServerExtension> ReadHmiExtensions(ITcHmiServer? server)
    {
        if (server is null) return Array.Empty<HmiServerExtension>();
        var list = new List<HmiServerExtension>();
        foreach (ITcHmiServerExtension e in server.GetServerExtensions(true) ?? Array.Empty<ITcHmiServerExtension>())
        {
            if (e is null) continue;
            list.Add(new HmiServerExtension(e.DomainName ?? "", e.State ?? "", e.Loaded, e.Version));
        }
        return list;
    }

    private static List<HmiFileNode> ReadHmiTree(ITcHmiItem item, int depth, int maxDepth, ref int budget)
    {
        var nodes = new List<HmiFileNode>();
        foreach (ITcHmiItem child in item.Childs ?? Array.Empty<ITcHmiItem>())
        {
            if (child is null) continue;
            if (budget <= 0) break;
            budget--;
            int childCount = (int)child.ChildCount;
            IReadOnlyList<HmiFileNode>? children = depth < maxDepth && childCount > 0
                ? ReadHmiTree(child, depth + 1, maxDepth, ref budget)
                : null;
            nodes.Add(new HmiFileNode(child.Name ?? "", child.PathName ?? "", childCount, children));
        }
        return nodes;
    }

    private static bool SafeHmiBool(Func<bool> read)
    {
        try { return read(); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return false; }
    }

    private static int SafeHmiVersion(ITcHmiAutomation hmi)
    {
        try { return hmi.Version; }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return 0; }
    }

    /// <summary>ITcHmiError.LastError of any automation object, or null. Only meaningful
    /// right after a call that returned false/null — the value is not reset on success.</summary>
    private static string? HmiLastError(object? automationObject)
    {
        if (automationObject is not ITcHmiError err) return null;
        try
        {
            string? text = err.LastError;
            return string.IsNullOrWhiteSpace(text) ? null : HmiCoordinator.CondenseServerError(text);
        }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return null; }
    }
#endif
}
