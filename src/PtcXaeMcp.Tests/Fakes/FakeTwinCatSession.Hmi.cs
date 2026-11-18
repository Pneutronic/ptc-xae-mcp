using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests.Fakes;

/// <summary>The HMI surface of the fake (hmi-server, hmi-views, hmi-ads-tmc).</summary>
internal sealed partial class FakeTwinCatSession
{
    // ---- hmi-server ----------------------------------------------------------

    public Exception? EnsureHmiAutomationAvailableThrows { get; set; }
    public void EnsureHmiAutomationAvailable()
    {
        if (EnsureHmiAutomationAvailableThrows is not null) throw EnsureHmiAutomationAvailableThrows;
    }

    public HmiProjectList HmiProjects { get; set; } =
        new("Beckhoff.TcHmi.1.12", 1, Array.Empty<HmiProjectEntry>(), null);
    public Exception? ListHmiProjectsThrows { get; set; }
    public Dictionary<string, HmiProjectInfo> HmiProjectInfoByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? GetHmiProjectInfoThrows { get; set; }
    /// <summary>Title of the fake "Activate Engineering Server Trial License" window.</summary>
    public string? HmiBlockingDialog { get; set; }

    public HmiProjectList ListHmiProjects()
    {
        CallLog.Add("ListHmiProjects()");
        if (ListHmiProjectsThrows is not null) throw ListHmiProjectsThrows;
        return HmiProjects;
    }

    public HmiProjectInfo GetHmiProjectInfo(string hmiProject, bool includeFiles, int maxDepth)
    {
        CallLog.Add($"GetHmiProjectInfo({hmiProject}, files={includeFiles}, depth={maxDepth})");
        if (GetHmiProjectInfoThrows is not null) throw GetHmiProjectInfoThrows;
        return HmiProjectInfoByName.TryGetValue(hmiProject, out var info)
            ? info
            : throw new InvalidOperationException($"HMI project '{hmiProject}' not found in the open solution.");
    }

    public List<HmiMappedSymbol> HmiMappedSymbols { get; } = new();
    public Exception? ListHmiMappedSymbolsThrows { get; set; }
    /// <summary>symbol → value; a name missing here reads as not mapped.</summary>
    public Dictionary<string, string> HmiSymbolValues { get; } = new(StringComparer.Ordinal);
    /// <summary>symbol → server error text, checked before <see cref="HmiSymbolValues"/>.</summary>
    public Dictionary<string, string> HmiSymbolErrors { get; } = new(StringComparer.Ordinal);
    public Exception? ReadHmiSymbolsThrows { get; set; }

    public IReadOnlyList<HmiMappedSymbol> ListHmiMappedSymbols(string hmiProject, bool refresh)
    {
        CallLog.Add($"ListHmiMappedSymbols({hmiProject}, refresh={refresh})");
        if (ListHmiMappedSymbolsThrows is not null) throw ListHmiMappedSymbolsThrows;
        return HmiMappedSymbols.ToList();
    }

    public IReadOnlyList<HmiSymbolValue> ReadHmiSymbols(string hmiProject, IReadOnlyList<string> symbols)
    {
        CallLog.Add($"ReadHmiSymbols({hmiProject}, [{string.Join(",", symbols)}])");
        if (ReadHmiSymbolsThrows is not null) throw ReadHmiSymbolsThrows;
        return symbols.Select(n =>
                HmiSymbolErrors.TryGetValue(n, out var err) ? new HmiSymbolValue(n, false, null, err)
                : HmiSymbolValues.TryGetValue(n, out var v) ? new HmiSymbolValue(n, true, v, null)
                : new HmiSymbolValue(n, false, null,
                    "server request: Command Error: Code:513, Message: SYMBOL_NOT_MAPPED, Reason: Symbol not mapped"))
            .ToList();
    }

    /// <summary>Result of the next ControlHmiServer call; default: settled with ADS loaded
    /// for start/refresh, nothing loaded for shutdown.</summary>
    public HmiServerControlResult? NextHmiServerControlResult { get; set; }
    public Exception? ControlHmiServerThrows { get; set; }

    public HmiServerControlResult ControlHmiServer(string hmiProject, string action, int timeoutMs)
    {
        CallLog.Add($"ControlHmiServer({hmiProject}, {action}, {timeoutMs})");
        if (ControlHmiServerThrows is not null) throw ControlHmiServerThrows;
        return NextHmiServerControlResult ?? new HmiServerControlResult(action, true, true, null,
            [new HmiServerExtension("ADS", action == "shutdown" ? "Invalid" : "Initialized", action != "shutdown", "14.5.73.0")]);
    }

    /// <summary>Server answer for MapHmiSymbol; Ok=true also adds the mapping unless
    /// <see cref="HmiMapSkipsReadBack"/> simulates a lying server.</summary>
    public HmiOperationResult HmiMapResult { get; set; } = new(true, null);
    public bool HmiMapSkipsReadBack { get; set; }
    public HmiOperationResult HmiUnmapResult { get; set; } = new(true, null);
    public bool HmiUnmapSkipsReadBack { get; set; }

    public HmiOperationResult MapHmiSymbol(string hmiProject, string mapName, string internalName, string domain, string? subSymbolName)
    {
        CallLog.Add($"MapHmiSymbol({hmiProject}, {mapName}, {internalName}, {domain}, sub={subSymbolName ?? "<null>"})");
        if (HmiMapResult.Ok && !HmiMapSkipsReadBack)
        {
            HmiMappedSymbols.Add(new HmiMappedSymbol(mapName, domain, "tchmi:general#/definitions/BOOL", "BOOL", false, false));
            HmiSymbolValues[mapName] = "False"; // the live server now knows it
        }
        return HmiMapResult;
    }

    public HmiOperationResult UnmapHmiSymbol(string hmiProject, string mapName, string domain, bool removeHistorizedData)
    {
        CallLog.Add($"UnmapHmiSymbol({hmiProject}, {mapName}, {domain}, historized={removeHistorizedData})");
        if (HmiUnmapResult.Ok && !HmiUnmapSkipsReadBack)
        {
            HmiMappedSymbols.RemoveAll(s => s.MappedName == mapName);
            HmiSymbolValues.Remove(mapName);
            HmiSymbolErrors.Remove(mapName);
        }
        return HmiUnmapResult;
    }

    public HmiOperationResult HmiWriteResult { get; set; } = new(true, null);
    /// <summary>Simulates a PLC that changes the value right after the write.</summary>
    public string? HmiWriteReadBackOverride { get; set; }

    public HmiOperationResult WriteHmiSymbol(string hmiProject, string symbol, string valueJson)
    {
        CallLog.Add($"WriteHmiSymbol({hmiProject}, {symbol}, {valueJson})");
        if (HmiWriteResult.Ok)
            HmiSymbolValues[symbol] = HmiWriteReadBackOverride ?? RenderLikeTheServer(valueJson);
        return HmiWriteResult;
    }

    // Live, ITcHmiSymbol.Value is .NET-ish text for scalars: true → "True", "x" → x.
    private static string RenderLikeTheServer(string valueJson) => valueJson switch
    {
        "true" => "True",
        "false" => "False",
        _ when valueJson.StartsWith('"') => System.Text.Json.JsonSerializer.Deserialize<string>(valueJson) ?? "",
        _ => valueJson,
    };

    // ---- hmi-ads-tmc: ADS runtimes + attached TMC files -------------------------

    /// <summary>Puts <c>ADS.Config::RUNTIMES</c> into <see cref="HmiSymbolValues"/> — the
    /// symbol the coordinator reads for the live runtime table.</summary>
    public void SetAdsRuntimes(string json) =>
        HmiSymbolValues[HmiCoordinator.AdsRuntimesSymbol] = json;

    /// <summary>
    /// One ADS runtime exactly as the live server answers ADS.Config::RUNTIMES (keys of
    /// TcHmiAds.Schema.json). Composed through JsonSerializer so a Windows path in
    /// <paramref name="tmcFile"/> is escaped the way the server escapes it.
    /// </summary>
    public static string AdsRuntimeJson(string name = "PLC1", string netId = "127.0.0.1.1.1", int port = 851,
        bool useTmc = false, string? tmcFile = null)
    {
        var runtime = new Dictionary<string, object?>
        {
            ["ENABLED"] = true,
            ["NETID"] = netId,
            ["PORT"] = port,
            ["READ_ONLY"] = false,
            ["SYMBOLS"] = new Dictionary<string, object?>(),
            ["USE_WHITELISTING"] = false,
        };
        // A runtime that never had a TMC carries no TMC key at all.
        if (useTmc || tmcFile is not null)
            runtime["TMC"] = new Dictionary<string, object?>
            {
                ["USE_TMC"] = useTmc,
                ["DATA_SYMBOLUPANDDOWNLOAD"] = tmcFile ?? "",
            };
        return System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { [name] = runtime });
    }
    /// <summary>Simulates a TE2000 that reports no exception but never stores the entry.</summary>
    public bool HmiTmcAttachDoesNothing { get; set; }
    /// <summary>Simulates a detach that leaves the entry in place.</summary>
    public bool HmiTmcDetachDoesNothing { get; set; }
    /// <summary>LastError the raw call reports alongside its (meaningless) return value.</summary>
    public string? HmiTmcLastError { get; set; }
    public Exception? AttachHmiTmcFileThrows { get; set; }
    /// <summary>Leaves the saved .hmiproj unparsable after attach/detach, so the read-back
    /// cannot tell what happened (#152 E-5).</summary>
    public bool HmiTmcCorruptsProjectFile { get; set; }

    /// <summary>
    /// The real thing persists the list as the .hmiproj property HmiTmcFiles and the coordinator
    /// reads it back from the SAVED file, so the fake writes that same file. Tests therefore
    /// exercise the real XML/JSON read-back instead of a stand-in.
    /// </summary>
    public HmiOperationResult AttachHmiTmcFile(string hmiProject, string rootNamespace, string tmcFilePath)
    {
        CallLog.Add($"AttachHmiTmcFile({hmiProject}, {rootNamespace}, {tmcFilePath})");
        if (AttachHmiTmcFileThrows is not null) throw AttachHmiTmcFileThrows;
        if (!HmiTmcAttachDoesNothing)
        {
            var entries = HmiTmcEntries(hmiProject);
            entries.Add((rootNamespace, StoredPathFor(tmcFilePath)));
            WriteHmiTmcFiles(hmiProject, entries);
        }
        CorruptHmiProjectFileIfAsked(hmiProject);
        return new HmiOperationResult(true, HmiTmcLastError);
    }

    public HmiOperationResult DetachHmiTmcFile(string hmiProject, string rootNamespace)
    {
        CallLog.Add($"DetachHmiTmcFile({hmiProject}, {rootNamespace})");
        if (!HmiTmcDetachDoesNothing)
            WriteHmiTmcFiles(hmiProject, HmiTmcEntries(hmiProject)
                .Where(e => !string.Equals(e.RootNamespace, rootNamespace, StringComparison.OrdinalIgnoreCase))
                .ToList());
        CorruptHmiProjectFileIfAsked(hmiProject);
        // TE2000 returns false here even on success.
        return new HmiOperationResult(true, HmiTmcLastError);
    }

    // ---- #161: server-side TMC of an ADS runtime ----------------------------------
    // The real server keeps the runtime table that ADS.Config::RUNTIMES reads, so the fake writes the
    // two leaves INTO that same JSON — the coordinator's read-back runs through the real parser.

    /// <summary>Simulates a server that answers both writes with success but changes nothing.</summary>
    public bool HmiRuntimeTmcDoesNothing { get; set; }
    /// <summary>Simulates a server that stores the path, then refuses the USE_TMC write.</summary>
    public bool HmiRuntimeTmcRefusesFlag { get; set; }
    /// <summary>Makes ADS.Config::RUNTIMES unreadable right after the writes (#152 E-5).</summary>
    public bool HmiRuntimeTmcBreaksReadBack { get; set; }
    public Exception? SetHmiRuntimeTmcThrows { get; set; }

    public HmiOperationResult SetHmiRuntimeTmc(string hmiProject, string runtime, string dataSymbolUpAndDownload, bool useTmc,
        bool createTmcObject = false)
    {
        CallLog.Add($"SetHmiRuntimeTmc({hmiProject}, {runtime}, {dataSymbolUpAndDownload}, useTmc={useTmc}" +
                    (createTmcObject ? ", createTmcObject" : "") + ")");
        if (SetHmiRuntimeTmcThrows is not null) throw SetHmiRuntimeTmcThrows;
        try
        {
            if (HmiRuntimeTmcDoesNothing) return new HmiOperationResult(true, null);
            if (createTmcObject)
            {
                UpdateRuntimeTmc(runtime, tmc =>
                {
                    tmc["DATA_SYMBOLUPANDDOWNLOAD"] = dataSymbolUpAndDownload;
                    tmc["USE_TMC"] = useTmc;
                }, create: true);
                return new HmiOperationResult(true, null);
            }
            // Live 2026-11-18: without a TMC object the server refuses the leaf write.
            if (!RuntimeHasTmcObject(runtime))
                return new HmiOperationResult(false,
                    $"ADS.Config::RUNTIMES::{runtime}::TMC::DATA_SYMBOLUPANDDOWNLOAD: Command Error: INVALID_SUBSYMBOL");
            UpdateRuntimeTmc(runtime, tmc => tmc["DATA_SYMBOLUPANDDOWNLOAD"] = dataSymbolUpAndDownload);
            if (HmiRuntimeTmcRefusesFlag)
                return new HmiOperationResult(false,
                    $"ADS.Config::RUNTIMES::{runtime}::TMC::USE_TMC: Command Error: Code:2050 " +
                    "(DATA_SYMBOLUPANDDOWNLOAD was already written)");
            UpdateRuntimeTmc(runtime, tmc => tmc["USE_TMC"] = useTmc);
            return new HmiOperationResult(true, null);
        }
        finally
        {
            if (HmiRuntimeTmcBreaksReadBack)
            {
                HmiSymbolValues.Remove(HmiCoordinator.AdsRuntimesSymbol);
                HmiSymbolErrors[HmiCoordinator.AdsRuntimesSymbol] = "server request: HMI_E_TIMEOUT";
            }
        }
    }

    public HmiOperationResult? RefreshHmiDefinitionsResult { get; set; }

    public HmiOperationResult RefreshHmiDefinitions(string hmiProject, string domain)
    {
        CallLog.Add($"RefreshHmiDefinitions({hmiProject}, {domain})");
        return RefreshHmiDefinitionsResult ?? new HmiOperationResult(true, null);
    }

    private bool RuntimeHasTmcObject(string runtime) =>
        System.Text.Json.Nodes.JsonNode.Parse(HmiSymbolValues[HmiCoordinator.AdsRuntimesSymbol])?[runtime]?["TMC"]
            is System.Text.Json.Nodes.JsonObject;

    private void UpdateRuntimeTmc(string runtime, Action<System.Text.Json.Nodes.JsonObject> change, bool create = false)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(HmiSymbolValues[HmiCoordinator.AdsRuntimesSymbol])!.AsObject();
        var rt = root[runtime]?.AsObject()
                 ?? throw new InvalidOperationException($"Command Error: no runtime '{runtime}'");
        if (rt["TMC"] is not System.Text.Json.Nodes.JsonObject tmc)
        {
            if (!create)
                throw new InvalidOperationException($"Command Error: INVALID_SUBSYMBOL ADS.Config::RUNTIMES::{runtime}::TMC");
            tmc = new System.Text.Json.Nodes.JsonObject();
            rt["TMC"] = tmc;
        }
        change(tmc);
        SetAdsRuntimes(root.ToJsonString());
    }

    // ---- hmi-internal-symbols (#165) -------------------------------------------
    // Like the TMC fake, these write the SAME files TE2000 persists to, so the coordinator's
    // read-back runs through the real parsers.

    /// <summary>Simulates an AddInternalSymbol that throws nothing but never saves.</summary>
    public bool HmiInternalAddDoesNothing { get; set; }
    /// <summary>Simulates a RemoveInternalSymbol that leaves the entry in place.</summary>
    public bool HmiInternalRemoveDoesNothing { get; set; }
    /// <summary>Leaves tchmiconfig.json unparsable after add/remove.</summary>
    public bool HmiInternalCorruptsConfig { get; set; }
    /// <summary>Stores this type instead of the requested one (engine normalising it).</summary>
    public string? HmiInternalStoresType { get; set; }
    public Exception? AddHmiInternalSymbolThrows { get; set; }

    public HmiOperationResult AddHmiInternalSymbol(string hmiProject, string name, string dataType,
        string? defaultValueJson, bool persist, bool readOnly)
    {
        CallLog.Add($"AddHmiInternalSymbol({hmiProject}, {name}, {dataType}, {defaultValueJson ?? "<null>"}, " +
                    $"persist={persist}, readOnly={readOnly})");
        if (AddHmiInternalSymbolThrows is not null) throw AddHmiInternalSymbolThrows;
        if (!HmiInternalAddDoesNothing)
            EditClientConfig(hmiProject, internals =>
                internals[name] = new System.Text.Json.Nodes.JsonObject
                {
                    ["value"] = defaultValueJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(defaultValueJson),
                    ["type"] = HmiInternalStoresType ?? dataType,
                    ["persist"] = persist,
                    ["readonly"] = readOnly,
                });
        if (HmiInternalCorruptsConfig) System.IO.File.WriteAllText(ClientConfigFile(hmiProject), "{ \"symbols\": ");
        return new HmiOperationResult(true, null);
    }

    public HmiOperationResult RemoveHmiInternalSymbol(string hmiProject, string name)
    {
        CallLog.Add($"RemoveHmiInternalSymbol({hmiProject}, {name})");
        if (!HmiInternalRemoveDoesNothing)
            EditClientConfig(hmiProject, internals => internals.Remove(name));
        if (HmiInternalCorruptsConfig) System.IO.File.WriteAllText(ClientConfigFile(hmiProject), "{ \"symbols\": ");
        // TE2000 returns false here ALWAYS (decompiled); the session reports the raw call as made.
        return new HmiOperationResult(true, null);
    }

    public string ClientConfigFile(string hmiProject) =>
        System.IO.Path.Combine(ProjectDirectoryOf(hmiProject), HmiInternalSymbols.ClientConfigRelativePath);

    public string ServerConfigFile(string hmiProject) =>
        System.IO.Path.Combine(ProjectDirectoryOf(hmiProject), HmiInternalSymbols.ServerConfigRelativePath);

    private string ProjectDirectoryOf(string hmiProject) =>
        HmiProjects.Projects.First(p => p.Name.Equals(hmiProject, StringComparison.OrdinalIgnoreCase)).ProjectDirectory;

    private void EditClientConfig(string hmiProject, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        string file = ClientConfigFile(hmiProject);
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(file))!;
        if (root["symbols"] is not System.Text.Json.Nodes.JsonObject symbols)
            root["symbols"] = symbols = new System.Text.Json.Nodes.JsonObject();
        if (symbols["internal"] is not System.Text.Json.Nodes.JsonObject internals)
            symbols["internal"] = internals = new System.Text.Json.Nodes.JsonObject();
        edit(internals);
        System.IO.File.WriteAllText(file, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Server answer for MapHmiOfflineSymbol.</summary>
    public HmiOperationResult HmiOfflineMapResult { get; set; } = new(true, null);
    /// <summary>The server reports success but keeps nothing.</summary>
    public bool HmiOfflineMapDoesNothing { get; set; }
    /// <summary>The live config read of the new symbol fails (file read-back must take over).</summary>
    public bool HmiOfflineMapNoLiveConfig { get; set; }
    /// <summary>The server does not write TcHmiSrv.Config.default.json.</summary>
    public bool HmiOfflineMapNoFile { get; set; }
    /// <summary>The server stores this USEMAPPING instead of the requested one.</summary>
    public bool? HmiOfflineMapStoresUseMapping { get; set; }
    /// <summary>The server stores this DOMAIN instead of TcHmiSrv.</summary>
    public string? HmiOfflineMapStoresDomain { get; set; }

    public HmiOperationResult MapHmiOfflineSymbol(string hmiProject, string mapName, string dataType,
        string? defaultValueJson, bool online, bool persist)
    {
        CallLog.Add($"MapHmiOfflineSymbol({hmiProject}, {mapName}, {dataType}, {defaultValueJson ?? "<null>"}, " +
                    $"online={online}, persist={persist})");
        if (!HmiOfflineMapResult.Ok || HmiOfflineMapDoesNothing) return HmiOfflineMapResult;

        // The shape SrvComm.MapSymbol sends (decompiled): SCHEMA via OfflineSymbolSchema.GetJson.
        System.Text.Json.Nodes.JsonNode? Default() =>
            defaultValueJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(defaultValueJson);
        System.Text.Json.Nodes.JsonObject schema = dataType.StartsWith("tchmi:", StringComparison.Ordinal)
            ? defaultValueJson is null
                ? new() { ["$ref"] = dataType }
                : new() { ["allOf"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["$ref"] = dataType },
                    new System.Text.Json.Nodes.JsonObject { ["default"] = Default() }) }
            : new() { ["type"] = dataType, ["default"] = Default() };
        var options = new System.Text.Json.Nodes.JsonObject();
        if (persist) options["persistedOfflineValue"] = Default();
        var entry = new System.Text.Json.Nodes.JsonObject
        {
            ["ACCESS"] = 3,
            ["DOMAIN"] = HmiOfflineMapStoresDomain ?? HmiInternalSymbols.ServerDomain,
            ["MAPPING"] = "",
            ["OPTIONS"] = options,
            ["SCHEMA"] = schema,
            ["USEMAPPING"] = HmiOfflineMapStoresUseMapping ?? online,
        };

        if (!HmiOfflineMapNoLiveConfig)
            HmiSymbolValues[HmiInternalSymbols.ServerConfigSymbol(mapName)] = entry.ToJsonString();
        HmiSymbolValues[mapName] = defaultValueJson ?? "null"; // the running server now knows it
        if (!HmiOfflineMapNoFile)
        {
            string file = ServerConfigFile(hmiProject);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            string text = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : "";
            var root = string.IsNullOrWhiteSpace(text)
                ? new System.Text.Json.Nodes.JsonObject()
                : (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(text)!;
            if (root["SYMBOLS"] is not System.Text.Json.Nodes.JsonObject symbols)
                root["SYMBOLS"] = symbols = new System.Text.Json.Nodes.JsonObject();
            symbols[mapName] = entry.DeepClone();
            System.IO.File.WriteAllText(file, root.ToJsonString());
        }
        return HmiOfflineMapResult;
    }

    /// <summary>What ListPlcProjectXml returns (#166): one entry per TIPC child.</summary>
    public List<PlcProjectXml> PlcProjectXmls { get; } = new();
    public Exception? ListPlcProjectXmlThrows { get; set; }

    public IReadOnlyList<PlcProjectXml> ListPlcProjectXml()
    {
        CallLog.Add("ListPlcProjectXml()");
        if (ListPlcProjectXmlThrows is not null) throw ListPlcProjectXmlThrows;
        return PlcProjectXmls.ToList();
    }

    private void CorruptHmiProjectFileIfAsked(string hmiProject)
    {
        if (HmiTmcCorruptsProjectFile)
            System.IO.File.WriteAllText(HmiProjectFile(hmiProject), "<Project><PropertyGroup>");
    }

    private string StoredPathFor(string absolutePath) =>
        HmiTmcPaths.StoragePath(absolutePath,
            string.IsNullOrEmpty(ActiveSlnPath) ? null : System.IO.Path.GetDirectoryName(ActiveSlnPath));

    private string HmiProjectFile(string hmiProject)
    {
        HmiProjectEntry entry = HmiProjects.Projects.First(p =>
            p.Name.Equals(hmiProject, StringComparison.OrdinalIgnoreCase));
        return System.IO.Path.Combine(entry.ProjectDirectory, entry.Name + ".hmiproj");
    }

    private List<(string RootNamespace, string FileName)> HmiTmcEntries(string hmiProject)
    {
        var solutionDir = string.IsNullOrEmpty(ActiveSlnPath) ? null : System.IO.Path.GetDirectoryName(ActiveSlnPath);
        return (HmiTmcPaths.ReadAttachments(HmiProjectFile(hmiProject), solutionDir)
                ?? Array.Empty<HmiTmcAttachment>())
            .Select(a => (a.RootNamespace, a.StoredFileName)).ToList();
    }

    /// <summary>Writes the HmiTmcFiles property into the fake project's .hmiproj.</summary>
    private void WriteHmiTmcFiles(string hmiProject, List<(string RootNamespace, string FileName)> entries)
    {
        string file = HmiProjectFile(hmiProject);
        string json = System.Text.Json.JsonSerializer.Serialize(
            entries.Select(e => new { FileName = e.FileName, RootNamespace = e.RootNamespace, PlcName = (string?)null }));
        var doc = System.Xml.Linq.XDocument.Load(file);
        System.Xml.Linq.XNamespace ns = doc.Root!.Name.Namespace;
        var existing = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == HmiTmcPaths.ProjectProperty);
        if (existing is not null) existing.Value = json;
        else
        {
            var group = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup");
            if (group is null)
            {
                group = new System.Xml.Linq.XElement(ns + "PropertyGroup");
                doc.Root.Add(group);
            }
            group.Add(new System.Xml.Linq.XElement(ns + HmiTmcPaths.ProjectProperty, json));
        }
        doc.Save(file);
    }

    // ---- hmi-views: an in-memory project tree -----------------------------------

    public const string FakeProjectSchema = "./../../Packages/Beckhoff.TwinCAT.HMI.Framework.14.3.500/runtimes/native1.12-tchmi/Schema/UserControlConfig.Schema.json";
    /// <summary>path → source. Folders are implied by paths.</summary>
    public Dictionary<string, string> HmiFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HmiOperationResult HmiFileWriteResult { get; set; } = new(true, null);
    /// <summary>Simulates an engine that alters the text on SetSource (read-back must catch it).</summary>
    public Func<string, string>? HmiFileWriteTransform { get; set; }
    public HmiOperationResult HmiBuildResult { get; set; } = new(true, null);
    /// <summary>Paths whose creation fails (simulates AddItem returning null).</summary>
    public HashSet<string> HmiAddItemFails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HmiFileContent ReadHmiFile(string hmiProject, string path)
    {
        CallLog.Add($"ReadHmiFile({hmiProject}, {path})");
        return HmiFiles.TryGetValue(path, out var s)
            ? new HmiFileContent(path, s)
            : throw new InvalidOperationException($"'{path}' not found in HMI project '{hmiProject}'.");
    }

    public bool HmiItemExists(string hmiProject, string path) =>
        HmiFiles.ContainsKey(path) || HmiFiles.Keys.Any(k => k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> AddHmiItem(string hmiProject, string folder, string name, string templateKey)
    {
        CallLog.Add($"AddHmiItem({hmiProject}, {folder}, {name}, {templateKey})");
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var created = new List<string>();
        void Add(string path, string source) { HmiFiles[path] = source; created.Add(path); }
        string ext = templateKey switch
        {
            "UserControl" => ".usercontrol", "Content" => ".content", "View" => ".view",
            "FunctionJavaScript_Hmi" or "CodeBehindJavaScript" or "GeneralBlankJavaScript" => ".js",
            "FunctionTypeScript_Hmi" or "CodeBehindTypeScript" or "GeneralBlankTypeScript" => ".ts",
            "GeneralBlankJson" => ".json", "GeneralBlankCss" => ".css", "GeneralBlankXml" => ".xml",
            "GeneralBlankHtml" => ".html", _ => ".txt",
        };
        if (HmiAddItemFails.Contains(prefix + name + ext))
            throw new InvalidOperationException($"AddItem('{name}', {templateKey}) in '{folder}' failed: fake failure");
        Add(prefix + name + ext, $"<!-- template {templateKey} -->");
        if (templateKey == "UserControl")
            Add(prefix + name + ".usercontrol.json", "{\n  \"$schema\": \"" + FakeProjectSchema + "\",\n  \"parameters\": []\n}");
        if (templateKey is "FunctionJavaScript_Hmi" or "FunctionTypeScript_Hmi")
            Add(prefix + name + ".function.json", "{\n  \"$schema\": \"../.hmiframework/Schema/FunctionDescription.Schema.json\"\n}");
        return created;
    }

    public HmiOperationResult WriteHmiFile(string hmiProject, string path, string source)
    {
        CallLog.Add($"WriteHmiFile({hmiProject}, {path}, {source.Length} chars)");
        if (!HmiFiles.ContainsKey(path)) throw new InvalidOperationException($"'{path}' is not an existing file.");
        if (HmiFileWriteResult.Ok) HmiFiles[path] = HmiFileWriteTransform?.Invoke(source) ?? source;
        return HmiFileWriteResult;
    }

    /// <summary>Simulates #247: TE2000 adds the file but raises this error anyway.</summary>
    public string? HmiImportEngineError { get; set; }
    /// <summary>Simulates a genuine import failure: ImportHmiFile throws with this message.</summary>
    public string? HmiImportFails { get; set; }
    /// <summary>Content the fake engine writes to disk instead of the source (corrupt copy).</summary>
    public string? HmiImportDiskContent { get; set; }

    public HmiImportResult ImportHmiFile(string hmiProject, string folder, string name, string sourcePath)
    {
        CallLog.Add($"ImportHmiFile({hmiProject}, {folder}, {name})");
        if (HmiImportFails is not null) throw new InvalidOperationException(HmiImportFails);
        string path = (folder.Length == 0 ? "" : folder + "/") + name;
        HmiFiles[path] = File.ReadAllText(sourcePath);
        // Like TE2000's AddExistingItem: copy into the project directory when it exists on disk.
        string? dir = HmiProjects?.Projects.FirstOrDefault(p => p.Name == hmiProject)?.ProjectDirectory;
        if (dir is not null && Directory.Exists(dir))
        {
            string target = System.IO.Path.Combine(dir, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (HmiImportDiskContent is null) File.Copy(sourcePath, target, overwrite: true);
            else File.WriteAllText(target, HmiImportDiskContent);
        }
        return new HmiImportResult([path], HmiImportEngineError);
    }

    public HmiOperationResult BuildHmiProject(string hmiProject, string configuration)
    {
        CallLog.Add($"BuildHmiProject({hmiProject}, {configuration})");
        return HmiBuildResult;
    }

    /// <summary>Paths whose DeleteChild the fake engine refuses.</summary>
    public HashSet<string> HmiDeleteFails { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Simulates an engine that reports success but keeps the item.</summary>
    public bool HmiDeleteKeepsItem { get; set; }

    /// <summary>Files the fake XAE has open in an editor tab.</summary>
    public HashSet<string> HmiOpenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsHmiFileOpen(string hmiProject, string path) => HmiOpenFiles.Contains(path);

    public HmiItemInfo? DescribeHmiItem(string hmiProject, string path)
    {
        CallLog.Add($"DescribeHmiItem({hmiProject}, {path})");
        if (HmiFiles.ContainsKey(path)) return new HmiItemInfo(path, true, 0);
        int below = HmiFiles.Keys.Count(k => k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
        return below > 0 ? new HmiItemInfo(path, false, below) : null;
    }

    public HmiOperationResult DeleteHmiItem(string hmiProject, string path)
    {
        CallLog.Add($"DeleteHmiItem({hmiProject}, {path})");
        if (HmiDeleteFails.Contains(path)) return new HmiOperationResult(false, "fake refusal");
        if (!HmiDeleteKeepsItem)
            foreach (string key in HmiFiles.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                                           k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                HmiFiles.Remove(key);
        return new HmiOperationResult(true, null);
    }

    // Not logged: a probe like HasOpenSolution, called on error paths only.
    public string? FindHmiBlockingDialog() => HmiBlockingDialog;
}
