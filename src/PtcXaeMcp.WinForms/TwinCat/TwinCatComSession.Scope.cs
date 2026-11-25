using System.Globalization;

namespace PtcXaeMcp.WinForms.TwinCat;

internal sealed partial class TwinCatComSession
{
#if !TWINCAT
    // ci-twincat-compile-seams (#47): built without TwinCAT (hosted CI). Keep
    // the same public signatures so ScopeCoordinator / MCP tools / unit tests
    // compile and run on a runner with no Beckhoff Measurement AI assembly.
    // Every ScopeCoordinator method wraps the session call in try/catch and
    // surfaces the exception as an "[Error] …" summary, so throwing here is the
    // uniform, honest fallback — no faked scope data.
    public string CreateScopeProject(string? projectName) => throw TwinCatNotBuilt.Exception();
    public string RemoveScopeProject(string? projectName) => throw TwinCatNotBuilt.Exception();
    public string AddScopeChannel(string symbol, string? chartName, string? axisName, string? channelName, bool stackedAxis, string? symbolName, int? targetPort, string? sampleMode, int? sampleTimeMs, string? symbolComment, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string SetScopeAxisMode(string chartName, string mode, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string SetScopeLabels(string nodeSelector, string? name, string? chartTitle, string? axisTitle, string? projectName) => throw TwinCatNotBuilt.Exception();
    public IReadOnlyList<ScopeNodeEntry> ListScopeChannels(string? projectName) => throw TwinCatNotBuilt.Exception();
    public ScopeStatus GetScopeStatus(string? projectName) => throw TwinCatNotBuilt.Exception();
    public string ExportScopeData(string format, string? projectName) => throw TwinCatNotBuilt.Exception();
    public ScopeAcquisitionInfo ReadScopeAcquisition(string acqSelector, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string SetScopeAcquisitionSymbol(string acqSelector, string? symbolName, int? targetPort, string? targetSystem, string? sampleMode, int? sampleTimeMs, double? timeOffsetS, string? symbolComment, bool? enabled, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string StartScopeRecord(string? projectName) => throw TwinCatNotBuilt.Exception();
    public string StopScopeRecord(string? projectName) => throw TwinCatNotBuilt.Exception();
    public ScopeRecordState GetScopeRecordState(string? projectName) => throw TwinCatNotBuilt.Exception();
    public string SaveScopeData(string? path, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string SetScopeRecordSettings(double? recordTimeS, bool? ringbuffer, string? fileStore, string? recordMode, string? autoSaveMode, string? autoSavePath, string? filenameMask, string? syncMode, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string CreateScopeTriggerGroup(string action, string? projectName) => throw TwinCatNotBuilt.Exception();
    public string AddScopeTriggerSet(string groupName, string setType, string? symbol, double? threshold, string? edge, string? compare, string? projectName) => throw TwinCatNotBuilt.Exception();
    public IReadOnlyList<ScopeTriggerGroupEntry> ListScopeTriggers(string? projectName) => throw TwinCatNotBuilt.Exception();
#else
    // ---- Scope measurement (feature/scope-measurement) ----------------------
    //
    // Navigation model — confirmed against Infosys tc3_automationinterface/
    // 498477707 ("Creating and handling TwinCAT Measurement projects"), whose
    // canonical Beckhoff C# snippet is:
    //
    //   Project     measProj = dte.Solution.Projects.Item(1);
    //   ProjectItem scopeRoot = measProj.ProjectItems.Item(1);
    //   IMeasurementScope ims = (IMeasurementScope)scopeRoot.Object;
    //   ims.CreateChild(out object chartObj);          // chartObj is a ProjectItem
    //   ProjectItem piChart = chartObj as ProjectItem;
    //   ((IMeasurementScope)piChart.Object).ChangeName("New Chart");
    //   IMeasurementScope dataPool = (IMeasurementScope)scopeRoot.ProjectItems.Item(1).Object;
    //
    // IMPORTANT: IMeasurementScope is reached via *ProjectItem.Object* (NOT
    // Project.Object), and the tree is walked via EnvDTE *ProjectItem.ProjectItems*.
    // IMeasurementScope has NO ChildCount/Child(i) members — only CreateChild,
    // LookUpChild, ChangeName, the record/export methods and ShowControl. A scope
    // "node" in this file is therefore always an EnvDTE ProjectItem; its scope
    // interface is pi.Object, its children are pi.ProjectItems (1-based), its name
    // is pi.Name, and its labels/binding live in pi.Properties.
    //
    // Tree shape (fixed per the documentation):
    //   Project (.tcmproj)
    //    └─ ProjectItems.Item(1) = scope-config root  (IMeasurementScope)
    //        ├─ DataPool                              → children = Acquisitions
    //        ├─ <Chart>                               → children = AxisGroups
    //        │   └─ <AxisGroup>                       → children = Channels
    //        └─ <Trigger / other set nodes>
    //
    // Assembly path confirmed 2026-05-15:
    //   C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\TE130x-Scope-View\
    //     TwinCAT.Measurement.AutomationInterface.dll
    // Template confirmed 2026-05-15:
    //   C:\Program Files (x86)\Beckhoff\TwinCAT\Functions\
    //     TwinCAT Measurement\Templates\Projects\Empty Measurement Project.tcmproj
    //
    // CreateChild/LookUpChild have an `out object` parameter that C# dynamic
    // dispatch cannot express, so they go through Type.InvokeMember with a
    // ParameterModifier. The returned out object is an EnvDTE ProjectItem.

    // Resolved via TwinCatPaths (issue #157 item 9: registry → %TwinCAT3Dir%
    // → default, single source of truth) instead of a hardcoded path.
    private static readonly string ScopeTemplatePath = TwinCatPaths.GetScopeTemplatePath();

    // Classification context: the documented tree shape is fixed, so a node's
    // kind is determined by its parent's role, refined by name (DataPool) and the
    // presence of the Y-Data.Acquisition property (channels).
    private enum ScopeCtx { Root, Chart, AxisGroup, DataPool }

    // ---- CreateScopeProject -----------------------------------------------

    public string CreateScopeProject(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sln = dte.Solution;

        string slnDir = Path.GetDirectoryName((string)sln.FullName)
            ?? throw new InvalidOperationException("Cannot determine solution directory.");

        string name = string.IsNullOrWhiteSpace(projectName)
            ? "MeasurementProject"
            : projectName!.Trim();

        if (!File.Exists(ScopeTemplatePath))
            throw new InvalidOperationException(
                $"Scope View template not found: {ScopeTemplatePath}. " +
                "Verify TE130X Scope View is installed under Program Files (x86)\\Beckhoff.");

        string destDir = Path.Combine(slnDir, name);

        // AddFromTemplate returns the created EnvDTE.Project (per Infosys
        // 498477707). The "YT Scope Project" project template ships a
        // .tcscopex scope-config document as ProjectItems.Item(1), so the
        // IMeasurementScope root is immediately accessible — both on this
        // returned object and on a later FindMeasurementProject re-resolve.
        // (The "Empty Measurement Project" template creates only an empty
        // container with NO .tcscopex child, so its scope-config root never
        // materialises and every subsequent scope tool fails — see #41.)
        dynamic? proj;
        try { proj = sln.AddFromTemplate(ScopeTemplatePath, destDir, name, false); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            // The late-bound call wraps the XAE's error in a TargetInvocationException whose own
            // message ("an invocation target threw") says nothing — report the innermost cause.
            Exception inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            throw new InvalidOperationException(
                $"dte.Solution.AddFromTemplate failed for scope project '{name}': " +
                $"{inner.GetType().Name} hr=0x{inner.HResult:X8}: {inner.Message}", ex);
        }

        if (proj is null) proj = FindMeasurementProject(dte, name);
        if (proj is null)
            throw new InvalidOperationException(
                $"Scope project '{name}' was added but not found in dte.Solution.Projects.");

        if (GetScopeRootItem(proj) is null)
            throw new InvalidOperationException(
                $"Scope project '{name}' was created but its scope-config root is " +
                "not accessible. The configured template " +
                $"('{Path.GetFileName(ScopeTemplatePath)}') must be a Scope project " +
                "template that contains a .tcscopex configuration.");

        string projPath;
        try { projPath = (string)proj.FullName; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { projPath = Path.Combine(destDir, name + ".tcmproj"); }

        return $"Created scope project '{name}' at {projPath}.";
    }, OpenTimeout);

    // ---- RemoveScopeProject -----------------------------------------------

    // Deterministically unregister a measurement (.tcmproj) project from the
    // in-memory solution. Counterpart to CreateScopeProject; does NOT save the
    // solution and does NOT delete files on disk. Idempotent: returns a benign
    // summary (never throws) when the named project is not present, so callers
    // can use it as unconditional teardown.
    //
    // Why this exists: closing the solution without saving was relied on to
    // "discard" a created scope project, but if the close is blocked (modal
    // save prompt / COM busy) the project stays registered while its backing
    // directory is deleted — every later open then throws E_ABORT with
    // "Missing Project Nodes for Scope Instance ...". Removing the project node
    // first makes teardown order-independent of close success.
    public string RemoveScopeProject(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic sln = dte.Solution;

        string? want = string.IsNullOrWhiteSpace(projectName) ? null : projectName!.Trim();

        int count = 0;
        try { count = (int)sln.Projects.Count; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return "No projects in the open solution; nothing to remove."; }

        var meas = new List<(dynamic Proj, string Name, string Full)>();
        for (int i = 1; i <= count; i++)
        {
            dynamic? proj;
            try { proj = sln.Projects.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (proj is null) continue;

            string fullName = string.Empty;
            try { fullName = (string)proj.FullName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (!fullName.EndsWith(".tcmproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string pName = string.Empty;
            try { pName = (string)proj.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            meas.Add((proj, pName, fullName));
        }

        if (meas.Count == 0)
            return "No TwinCAT Measurement project in the open solution; nothing to remove.";

        (dynamic Proj, string Name, string Full) target;
        if (want is not null)
        {
            var hits = meas.Where(m =>
                string.Equals(m.Name, want, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                return $"No scope project named '{want}' in the open solution; nothing to remove.";
            target = hits[0];
        }
        else if (meas.Count > 1)
        {
            // Never guess which to remove when the solution ships its own scope
            // project (e.g. a customer 'YT Scope Project1') — fail loud (#40).
            throw new InvalidOperationException(
                $"The open solution contains {meas.Count} measurement projects " +
                $"({string.Join(", ", meas.Select(m => $"'{m.Name}'"))}). " +
                "Pass projectName to choose which one to remove.");
        }
        else
        {
            target = meas[0];
        }

        sln.Remove(target.Proj);
        return $"Removed scope project '{target.Name}' from the solution ({target.Full}). " +
               "The solution was not saved.";
    }, OpenTimeout);

    // ---- AddScopeChannel --------------------------------------------------

    public string AddScopeChannel(
        string  symbol,
        string? chartName,
        string? axisName,
        string? channelName,
        bool    stackedAxis,
        string? symbolName,
        int?    targetPort,
        string? sampleMode,
        int?    sampleTimeMs,
        string? symbolComment,
        string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        string chart   = string.IsNullOrWhiteSpace(chartName)   ? symbol : chartName!.Trim();
        string axis    = string.IsNullOrWhiteSpace(axisName)    ? chart  : axisName!.Trim();
        string channel = string.IsNullOrWhiteSpace(channelName) ? symbol : channelName!.Trim();

        // D-19: reuse the DataPool acquisition that already samples this symbol on this port
        // instead of creating a new one per call (every repeat used to orphan the previous one).
        // This also makes a busy-replay of the whole method idempotent: the acquisition is looked up
        // by symbol and chart/axis/channel by name before anything is created.
        string plcSymbol = string.IsNullOrWhiteSpace(symbolName)
            ? DerivePlcSymbolName(symbol)
            : symbolName!.Trim();
        int port = targetPort ?? DefaultPlcPort;

        // #59: a sample time without a sample mode can only mean a fixed one — 'Task Sampletime' takes the
        // time from the PLC task and ignores a written value, which used to fail the whole call.
        string? modeNote = null;
        if (sampleTimeMs is not null && sampleMode is null)
        {
            sampleMode = ScopeSampleMode.Fixed;
            modeNote = $" Sample mode set to '{ScopeSampleMode.Fixed}' because sampleTimeMs was given.";
        }

        // #59: nothing in the Scope tree can be removed through the Automation Interface on TE130x 4026.22
        // (ProjectItem.Delete() and Remove() return without error and change nothing, live 2026-11-24).
        // So a failed call must not have anything to clean up: the DataPool acquisition — the step that can
        // fail on the symbol — is created and read back FIRST, and chart, axis group and channel are only
        // created once it is good. Whatever a failure leaves is listed in the error.
        var created = new List<(string What, string Name, dynamic Node, dynamic Parent)>();
        try
        {
            dynamic dataPool = RequireDataPool(root);
            dynamic? acqNode = FindAcquisitionBySymbol(dataPool, plcSymbol, port);
            bool reused = acqNode is not null;
            if (acqNode is null)
            {
                // The tree path names the symbol for the Scope. For an NC axis ('TINC^…^Axes^Master^ActPosModulo',
                // port 501) the Scope can build something other than an ADS acquisition from such a path, so those
                // use the plain symbol name. Either way the result is checked: an acquisition without Symbol Name /
                // Port / Sample Time cannot be configured.
                bool ncPath = symbol.TrimStart().StartsWith("TINC^", StringComparison.OrdinalIgnoreCase);
                string createName = ncPath ? plcSymbol : symbol;
                acqNode = ScopeCreateChild(dataPool, createName, ScopeAcquisitionType.Ads);
                created.Add(("acquisition", SafeNodeName(acqNode), acqNode, dataPool));
                if (!HasAdsAcquisitionKeys((object)acqNode))
                {
                    if (string.Equals(createName, plcSymbol, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"this TE130x did not make an ADS acquisition for '{plcSymbol}' (port {port}): the new acquisition has no Symbol Name / Port / Sample Time property");
                    acqNode = ScopeCreateChild(dataPool, plcSymbol, ScopeAcquisitionType.Ads);
                    created.Add(("acquisition", SafeNodeName(acqNode), acqNode, dataPool));
                    if (!HasAdsAcquisitionKeys((object)acqNode))
                        throw new InvalidOperationException(
                            $"this TE130x did not make an ADS acquisition for '{plcSymbol}' (port {port}): the new acquisition has no Symbol Name / Port / Sample Time property");
                }
            }

            // Point the acquisition at the real PLC symbol so it actually records. Every field is
            // read back; a field that did not land is an error — an acquisition without its symbol
            // records nothing (keys verified live).
            List<PropWrite> symbolWrites = AfterMutation<List<PropWrite>>(() => ApplyAcquisitionSymbol((object)acqNode!, plcSymbol, port, null,
                sampleMode, sampleTimeMs, null, symbolComment, null));
            ThrowIfNotApplied($"Acquisition for '{plcSymbol}'", symbolWrites);

            // Reuse existing chart / axis via LookUpChild; create if absent.
            dynamic chartNode = ScopeLookUpOrCreate(root,      chart,   ScopeChartType.Yt,           out bool chartNew);
            if (chartNew) created.Add(("chart", SafeNodeName(chartNode), chartNode, root));
            dynamic axisNode  = ScopeLookUpOrCreate(chartNode, axis,    ScopeChartElement.AxisGroup,  out bool axisNew);
            if (axisNew) created.Add(("axis group", SafeNodeName(axisNode), axisNode, chartNode));
            dynamic chanNode  = ScopeLookUpOrCreate(axisNode,  channel, ScopeAxisGroupMember.Channel, out bool chanNew);
            if (chanNew) created.Add(("channel", SafeNodeName(chanNode), chanNode, axisNode));

            // TwinCAT controls the acquisition's tree name (it may dedupe /
            // sanitise the requested name, e.g. "GVL_HMI.HmiOpened" → "HmiOpened (1)").
            // Capture the real name so the result + a follow-up read agree.
            string acqName = AfterMutation(() => ComCall.ReadProp(() => (string)acqNode!.Name, symbol) ?? symbol);

            // Bind the channel to the acquisition. Infosys iterates piChannel.
            // Properties and sets prop.Value on the "Y-Data.Acquisition" entry —
            // the Properties.Item(key) indexer is unreliable here (NRE), so match
            // by iterating like the documented snippet. The bound value is the
            // acquisition's raw COM object (Infosys binds `prop.Value = <acquisition object>`).
            // Bind on the ORIGINAL chanNode (its Properties are populated; a
            // LookUpChild re-resolve returns an empty node). The result says
            // plainly when this TC build rejects the assignment.
            object acqRaw = AfterMutation(() => (object)acqNode!.Object);
            string? bindErr = AfterMutation(() => TrySetChannelAcquisition(chanNode, acqRaw));

            var notes = new List<string>();
            foreach (PropWrite w in AfterMutation<List<PropWrite>>(() => SetScopeLabel((object)chartNode, chart).Concat(SetScopeLabel((object)axisNode, axis)).ToList()))
                if (!w.Applied) notes.Add($"label {w.Label} not applied ({w.Key ?? "no property"}: {w.Error})");

            if (stackedAxis)
            {
                PropWrite st = AfterMutation<PropWrite>(() => ApplyAxisMode((object)chartNode, ScopeAxisMode.Stacked));
                if (!st.Applied) notes.Add($"stacked axis NOT applied ({st.Key ?? "no stacked-axis property"}: {st.Error})");
            }

            string chanPath = $"{chart}^{axis}^{channel}";
            string acqVerb = reused ? "reused existing acquisition" : "created acquisition";
            string baseMsg = $"Added channel '{channel}' under chart '{chart}' / axis '{axis}'; " +
                $"{acqVerb} '{acqName}' (Symbol Name '{plcSymbol}', port {port}; read back). Path: {chanPath}.";
            string bindMsg = bindErr is null
                ? " Channel bound to acquisition."
                : $" NOTE: channel↔acquisition auto-binding was rejected by this TwinCAT build " +
                  $"({bindErr}); bind it in the Scope editor. The acquisition is fully configured.";
            return baseMsg + modeNote + bindMsg + (notes.Count > 0 ? " NOTE: " + string.Join("; ", notes) + "." : string.Empty);
        }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            // The busy case is not handled here: ComBusyRetry replays the whole call, and the
            // lookups above make that idempotent. Anything else is a real failure → clean up.
            throw new InvalidOperationException(ex.Message + RollBackCreated(created), ex);
        }
    }, OpenTimeout);

    // Take back, newest first, what a failed add created, and say what is still in the tree. A node counts as
    // removed only when its parent no longer finds it: Delete() alone proves nothing on this TE130x.
    private static string RollBackCreated(List<(string What, string Name, dynamic Node, dynamic Parent)> created)
    {
        if (created.Count == 0) return " Nothing was created.";
        var removed = new List<string>();
        var left = new List<string>();
        for (int i = created.Count - 1; i >= 0; i--)
        {
            var (what, name, node, parent) = created[i];
            if (TryRemoveScopeNode(parent, node, name)) removed.Add($"{what} '{name}'");
            else left.Add($"{what} '{name}'");
        }
        return (removed.Count > 0 ? $" Rolled back: removed {string.Join(", ", removed)}." : string.Empty) +
               (left.Count > 0
                   ? $" Left in the Scope tree: {string.Join(", ", left)}. This TE130x does not remove nodes through the Automation Interface; " +
                     "remove them in the Scope editor (an acquisition no channel uses records nothing)."
                   : string.Empty);
    }

    // ProjectItem.Delete() on a Scope node returns without error and removes nothing (live 2026-11-24, TE130x 4026.22),
    // so the parent is asked again: true only when the node is really gone.
    private static bool TryRemoveScopeNode(dynamic parent, dynamic node, string name)
    {
        try { node.Delete(); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { return false; }
        return name.Length > 0 && ScopeLookUpChild(parent, name) is null;
    }

    private static string SafeNodeName(dynamic node)
    {
        try { return (string)node.Name ?? string.Empty; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return string.Empty; }
    }

    // An ADS acquisition has Symbol Name, Target Port and Sample Time properties (the live-verified
    // keys ApplyAcquisitionSymbol writes); an acquisition without them cannot be configured.
    private static bool HasAdsAcquisitionKeys(object acq) =>
        HasKey(acq, k => k.Contains("symbolname", StringComparison.Ordinal)) &&
        HasKey(acq, k => k.Contains("port", StringComparison.Ordinal)) &&
        HasKey(acq, k => k.Contains("sampletime", StringComparison.Ordinal));

    // The DataPool acquisition whose Symbol Name equals `plcSymbol` and whose target port equals
    // `port` (when the port is readable), or null.
    private static dynamic? FindAcquisitionBySymbol(dynamic dataPool, string plcSymbol, int port)
    {
        foreach (dynamic child in EnumerateChildren(dataPool))
        {
            var all = new List<ScopeAcquisitionProperty>();
            foreach (dynamic p in AcqProperties(child))
            {
                string pn = SafePropName(p);
                if (pn.Length > 0) all.Add(new ScopeAcquisitionProperty(pn, SafePropValue(p)));
            }
            if (!string.Equals(MatchValue(all, "symbolname")?.Trim(), plcSymbol, StringComparison.OrdinalIgnoreCase))
                continue;
            string? p2 = MatchValue(all, "port");
            if (p2 is null || p2.Trim() == port.ToString(CultureInfo.InvariantCulture))
                return child;
        }
        return null;
    }

    // ---- SetScopeAxisMode -------------------------------------------------

    public string SetScopeAxisMode(string chartName, string mode, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        dynamic? chartNode = ScopeLookUpChild(root, chartName);
        if (chartNode is null)
            throw new InvalidOperationException(
                $"Chart '{chartName}' not found in scope project.");

        // D-6: the stacked-axis key is not documented and could not be verified on the dev VM
        // — so the write is read back and a miss is an error.
        PropWrite w = AfterMutation<PropWrite>(() => ApplyAxisMode((object)chartNode, mode));
        if (!w.Applied)
            throw new InvalidOperationException(
                $"Axis mode '{mode}' was NOT applied to chart '{chartName}': {w.Error}" +
                (w.Key is null ? $". Chart property keys: {string.Join(", ", PropKeys(chartNode))}." : "."));
        return $"Set axis mode '{mode}' on chart '{chartName}' ({w.Key} = {w.ReadBack}, read back).";
    }, OpenTimeout);

    // ---- SetScopeLabels ---------------------------------------------------

    public string SetScopeLabels(
        string  nodeSelector,
        string? name,
        string? chartTitle,
        string? axisTitle,
        string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        // Try a direct child of the scope root first, then one level deeper
        // (axis groups are children of charts).
        dynamic? node = ScopeLookUpChild(root, nodeSelector);
        if (node is null)
        {
            foreach (dynamic chart in EnumerateChildren(root))
            {
                node = ScopeLookUpChild(chart, nodeSelector);
                if (node is not null) break;
            }
        }

        if (node is null)
            throw new InvalidOperationException(
                $"Scope node '{nodeSelector}' not found (searched chart and axis-group level).");

        // D-6: every requested label is written, then read back; anything that did not land is
        // an error that says what did and did not apply (the old code always answered "Set …").
        var writes = new List<PropWrite>();
        if (chartTitle is not null)
        {
            // Issue #201: TE130x (4026.22) has no title-text property on a chart or axis group; the
            // ShowTitleFrom enum there only shows the node's Name. Say so instead of a key miss.
            if (HasKey((object)node, k => k is "commontitle" or "title"))
            {
                writes.Add(WriteProp((object)node, "chartTitle", k => k is "commontitle" or "title", chartTitle));
                writes.Add(WriteProp((object)node, "chartTitle.showTitle", k => k == "commonshowtitle", ScopeShowTitle.True));
            }
            else
                writes.Add(new PropWrite("chartTitle", null, false, null,
                    "this TE130x version has no chart-title property (a chart shows its name: pass `name`; " +
                    "an axis group's 'Common.Show Title' only switches between hidden, shown and the node name)"));
        }
        if (axisTitle is not null)
        {
            // The text goes into "Y-Axis.Title (Y)"; "Y-Axis.Show Title (Y)" is a ShowTitleFrom enum that
            // takes 0..2 only (False/True/Name; live 2026-10-10) — the old "Custom" text never applied.
            writes.Add(WriteProp((object)node, "axisTitle", k => k == "yaxistitley", axisTitle));
            writes.Add(WriteProp((object)node, "axisTitle.showTitle", k => k == "yaxisshowtitley", ScopeShowTitle.True));
        }
        if (name is not null)
            writes.Add(RenameNode(node, name));

        ThrowIfNotApplied($"Labels on '{nodeSelector}'", writes,
            keysHint: () => string.Join(", ", PropKeys(node)));
        return $"Set labels on '{nodeSelector}' (read back): " +
               string.Join(", ", writes.Select(x => $"{x.Label}='{x.ReadBack}'")) + ".";
    }, OpenTimeout);

    // ---- ListScopeChannels ------------------------------------------------

    public IReadOnlyList<ScopeNodeEntry> ListScopeChannels(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();

        dynamic? proj = FindMeasurementProject(dte, projectName);
        if (proj is null) return (IReadOnlyList<ScopeNodeEntry>)Array.Empty<ScopeNodeEntry>();

        dynamic? root = GetScopeRootItem(proj);
        if (root is null)
            throw new InvalidOperationException(
                "Measurement project found but its scope-config root ProjectItem is not accessible.");

        var results = new List<ScopeNodeEntry>();
        WalkScopeTree(root, ScopeCtx.Root, results, string.Empty);
        return results;
    }, OpenTimeout);

    // ---- GetScopeStatus ---------------------------------------------------

    public ScopeStatus GetScopeStatus(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();

        dynamic? proj = FindMeasurementProject(dte, projectName);
        if (proj is null)
            return new ScopeStatus(false, null, 0, 0, 0, 0);

        string projName = string.Empty;
        try { projName = (string)proj.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        dynamic? root;
        try { root = GetScopeRootItem(proj); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { root = null; }
        if (root is null)
            return new ScopeStatus(true, projName, 0, 0, 0, 0);

        var entries = new List<ScopeNodeEntry>();
        WalkScopeTree(root, ScopeCtx.Root, entries, string.Empty);

        return new ScopeStatus(
            ScopeProjectExists:      true,
            ProjectName:             projName,
            ChartCount:              entries.Count(e => e.Kind == ScopeNodeKind.Chart),
            AxisCount:               entries.Count(e => e.Kind == ScopeNodeKind.AxisGroup),
            ChannelCount:            entries.Count(e => e.Kind == ScopeNodeKind.Channel),
            AcquisitionCount:        entries.Count(e => e.Kind == ScopeNodeKind.Acquisition),
            UnboundAcquisitionCount: entries.Count(e => e.Kind == ScopeNodeKind.Acquisition
                                                     && string.IsNullOrWhiteSpace(e.SymbolName)));
    }, OpenTimeout);

    // ---- ExportScopeData --------------------------------------------------

    public string ExportScopeData(string format, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope scope = Ims(root);

        string slnDir = Path.GetDirectoryName((string)dte.Solution.FullName)
            ?? throw new InvalidOperationException("Cannot determine solution directory.");

        string ext = format switch
        {
            "CSV"    => ".csv",
            "BINARY" => ".bin",
            "TDMS"   => ".tdms",
            "DAT"    => ".dat",
            "SVD"    => ".svd",
            _        => throw new InvalidOperationException($"Unknown format: {format}"),
        };

        string outPath = Path.Combine(slnDir, "ScopeData" + ext);

        try
        {
            switch (format)
            {
                case "CSV":    scope.ExportCSV(outPath);    break;
                case "BINARY": scope.ExportBinary(outPath); break;
                case "TDMS":   scope.ExportTDMS(outPath);   break;
                case "DAT":    scope.ExportDAT(outPath);    break;
                case "SVD":    scope.SaveSVD(outPath);      break;
            }
        }
        // D-13: only a message that SAYS "no data" is reported as such. A bare E_FAIL can be
        // anything (locked file, bad path, scope busy) and is reported with its HRESULT.
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            if (SaysNoData(ex))
                throw new InvalidOperationException(
                    "No recorded data — run a recording before exporting.", ex);
            throw new InvalidOperationException(
                $"Export failed ({format}): {DescribeScopeError(ex)}", ex);
        }

        if (!File.Exists(outPath))
            throw new InvalidOperationException(
                $"Export claimed success but output file not found: {outPath}");

        return outPath;
    }, OpenTimeout);

    // ---- scope-acquisition-symbols ----------------------------------------
    //
    // An acquisition's PLC symbol + ADS target + sample settings live as
    // EnvDTE ProjectItem.Properties on the DataPool acquisition node (Infosys
    // 498477707 uses exactly `foreach (Property prop in pi.Properties)`). The
    // internal Property.Name keys are German-locale and NOT documented, so we
    // never hardcode one literal — we enumerate the live Properties and match a
    // normalized token (feedback_browse_never_name_tree). read_scope_acquisition
    // always returns the full raw set so the real keys stay discoverable.

    private const int DefaultPlcPort = 851; // TC3 PLC 1 Runtime

    public ScopeAcquisitionInfo ReadScopeAcquisition(string acqSelector, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        dynamic acq  = RequireAcquisitionItem(root, acqSelector);

        string name = string.Empty;
        try { name = (string)acq.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        var all = new List<ScopeAcquisitionProperty>();
        foreach (dynamic p in AcqProperties(acq))
        {
            string pn = SafePropName(p);
            if (pn.Length == 0) continue;
            all.Add(new ScopeAcquisitionProperty(pn, SafePropValue(p)));
        }

        string? sym  = MatchValue(all, "symbolname");
        return new ScopeAcquisitionInfo(
            Name:         name,
            Path:         $"DataPool^{name}",
            SymbolName:   sym,
            DataType:     MatchValue(all, "datatype"),
            SampleMode:   ScopeSampleMode.InterpretValue(MatchValue(all, "samplemode")),
            SampleTimeMs: MatchValue(all, "sampletime"),
            TargetPort:   MatchValue(all, "port"),
            TargetSystem: MatchValue(all, "targetsystem"),
            HasSymbol:    !string.IsNullOrWhiteSpace(sym),
            AllProperties: all);
    }, OpenTimeout);

    public string SetScopeAcquisitionSymbol(
        string  acqSelector,
        string? symbolName,
        int?    targetPort,
        string? targetSystem,
        string? sampleMode,
        int?    sampleTimeMs,
        double? timeOffsetS,
        string? symbolComment,
        bool?   enabled,
        string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        dynamic acq  = RequireAcquisitionItem(root, acqSelector);

        string nm = string.Empty;
        try { nm = (string)acq.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

        List<PropWrite> writes = AfterMutation<List<PropWrite>>(() => ApplyAcquisitionSymbol((object)acq, symbolName, targetPort, targetSystem,
            sampleMode, sampleTimeMs, timeOffsetS, symbolComment, enabled));

        ThrowIfNotApplied($"Acquisition '{nm}'", writes,
            keysHint: () => "use twincat_read_scope_acquisition to inspect the available property keys");
        return $"Set acquisition '{nm}' (read back): " +
               string.Join(", ", writes.Select(w => $"{w.Label}={w.ReadBack}")) + ".";
    }, OpenTimeout);

    // Find a DataPool acquisition by selector. Accepts the documented
    // "Name" / "DataPool^Name" forms AND the full add_scope_channel `symbol`
    // form (e.g. "TIPC^…^PRG_IntegrationScratch.bScopeTestVar"): TwinCAT names
    // the acquisition by the UNQUALIFIED leaf (GVL_HMI.HmiOpened → "HmiOpened",
    // PRG.bVar → "bVar"), so we match the exact post-'^' token first and, if
    // that misses, fall back to the segment after the last '.' — the same
    // selector the caller passed to add_scope_channel then resolves here.
    private static dynamic RequireAcquisitionItem(dynamic root, string acqSelector)
    {
        string sel = acqSelector.Trim();
        int caret = sel.LastIndexOf('^');
        if (caret >= 0) sel = sel[(caret + 1)..].Trim();

        string? leaf = null;
        int dot = sel.LastIndexOf('.');
        if (dot >= 0 && dot < sel.Length - 1) leaf = sel[(dot + 1)..].Trim();

        dynamic dataPool = RequireDataPool(root);
        var available = new List<string>();
        dynamic? leafMatch = null;
        foreach (dynamic child in EnumerateChildren(dataPool))
        {
            string cn = string.Empty;
            try { cn = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (cn.Length > 0) available.Add(cn);
            if (string.Equals(cn, sel, StringComparison.OrdinalIgnoreCase))
                return child;
            if (leaf is not null && leafMatch is null &&
                string.Equals(cn, leaf, StringComparison.OrdinalIgnoreCase))
                leafMatch = child;
        }
        if (leafMatch is not null) return leafMatch;
        throw new InvalidOperationException(
            $"Acquisition '{sel}' not found in DataPool. Available: " +
            (available.Count == 0 ? "(none)" : string.Join(", ", available)) + ".");
    }

    // Set the supplied symbol/sample fields. Every write is read back (PropWrite.Applied);
    // the keys are the live-verified AdsAcquisition keys,
    // matched by normalised token.
    private static List<PropWrite> ApplyAcquisitionSymbol(
        object acq,
        string? symbolName,
        int?    targetPort,
        string? targetSystem,
        string? sampleMode,
        int?    sampleTimeMs,
        double? timeOffsetS,
        string? symbolComment,
        bool?   enabled)
    {
        var writes = new List<PropWrite>();
        void Try(string token, object? value, string label)
        {
            if (value is null) return;
            writes.Add(WriteProp((object)acq, label, k => k.Contains(token, StringComparison.Ordinal), value));
        }

        if (!string.IsNullOrWhiteSpace(symbolName))
        {
            Try("symbolname",  symbolName, "symbolName");
            Try("symbolbased", true,       "symbolBased");
        }
        Try("port",          targetPort,    "targetPort");
        Try("targetsystem",  targetSystem,  "targetSystem");
        // Sample Mode is a boolean property (spike 2026-05-17): Task
        // Sampletime ⇒ true, Fixed/On-Change ⇒ false.
        if (sampleMode is not null)
            Try("samplemode", ScopeSampleMode.IsTaskSampletime(sampleMode), "sampleMode");
        // In "Task Sampletime" (Sample Mode = True) TE130x ignores a written Sample Time (live
        // 2026-10-06). When the caller did not switch the mode, say why instead of reporting a
        // puzzling read-back mismatch. (Task mode + sampleTimeMs together is refused by the coordinator.)
        if (sampleTimeMs is not null && sampleMode is null && CurrentSampleModeIsTask(acq))
            writes.Add(new PropWrite("sampleTimeMs", "Symbol.Sample Mode", false, null,
                "Sample Mode is 'Task Sampletime', so the sample time comes from the PLC task and a written " +
                "value is ignored — pass sampleMode 'Fixed Sampletime' together with sampleTimeMs"));
        else
            Try("sampletime",    sampleTimeMs,  "sampleTimeMs");
        Try("timeoffset",    timeOffsetS,   "timeOffsetS");
        Try("symbolcomment", symbolComment, "symbolComment");
        Try("enabled",       enabled,       "enabled");
        return writes;
    }

    // Sample Mode is a boolean property: True = "Task Sampletime" (spike 2026-05-17).
    private static bool CurrentSampleModeIsTask(object acqObj)
    {
        dynamic acq = acqObj;
        foreach (dynamic p in AcqProperties(acq))
        {
            if (!NormName(SafePropName(p)).Contains("samplemode", StringComparison.Ordinal)) continue;
            object? v = ComCall.ReadProp<object?>(() => (object?)p.Value, null);
            return v is bool b ? b
                : string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), "True", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static IEnumerable<dynamic> AcqProperties(dynamic acqItem)
    {
        dynamic props;
        try { props = acqItem.Properties; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { yield break; }

        int count;
        try { count = (int)props.Count; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { yield break; }

        for (int i = 1; i <= count; i++)
        {
            dynamic? p = null;
            try { p = props.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (p is not null) yield return p;
        }
    }

    private static string SafePropName(dynamic prop)
    {
        try { return (string)prop.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return string.Empty; }
    }

    private static string SafePropValue(dynamic prop)
    {
        try
        {
            object? v = prop.Value;
            return v?.ToString() ?? string.Empty;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return "<unreadable>"; }
    }

    private static string NormName(string s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string? MatchValue(List<ScopeAcquisitionProperty> all, string token)
    {
        foreach (var p in all)
            if (NormName(p.Key).Contains(token))
                return p.Value;
        return null;
    }

    // Scope Symbol Name is the PLC-side path (MAIN.rValue / GVL_HMI.HmiOpened).
    // The `symbol` arg is usually the automation-tree path
    // (TIPC^PLC^PLC Projekt^MAIN.rValue) — take the segment after the last '^'.
    private static string DerivePlcSymbolName(string symbol)
    {
        string s = (symbol ?? string.Empty).Trim();
        int caret = s.LastIndexOf('^');
        return caret >= 0 ? s[(caret + 1)..].Trim() : s;
    }

    // ---- Project / root resolution ----------------------------------------

    private static dynamic RequireScopeRootItem(dynamic dte, string? projectName)
    {
        dynamic? proj = FindMeasurementProject(dte, projectName);
        if (proj is null)
            throw new InvalidOperationException(
                "No TwinCAT Measurement project in the open solution. " +
                "Call twincat_create_scope_project first.");

        return GetScopeRootItem(proj)
            ?? throw new InvalidOperationException(
                "Measurement project found but its scope-config root ProjectItem " +
                "(project.ProjectItems.Item(1)) is not accessible.");
    }

    // The scope-config root is the first ProjectItem of the .tcmproj project
    // (Infosys: `ProjectItem scopeRoot = measProj.ProjectItems.Item(1)`).
    private static dynamic? GetScopeRootItem(dynamic proj)
    {
        try
        {
            dynamic items = proj.ProjectItems;
            int count = 0;
            try { count = (int)items.Count; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (count < 1) return null;
            dynamic item = items.Item(1);
            return item;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Resolve the measurement (.tcmproj) project to operate on. Returns null
    // only when the solution has NO measurement project (callers treat that as
    // "no scope project"). When a name is given it must match. When no name is
    // given and the solution has MORE THAN ONE measurement project, fail loud
    // (listing them) instead of silently mutating an arbitrary one — a real
    // user opening a project that already ships its own scope project would
    // otherwise have add/set tools hit the wrong project (issue #40).
    private static dynamic? FindMeasurementProject(dynamic dte, string? name)
    {
        dynamic? sln = dte.Solution;
        int count = 0;
        try { count = (int)sln.Projects.Count; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }

        var measProjects = new List<(dynamic Proj, string Name)>();
        for (int i = 1; i <= count; i++)
        {
            dynamic? proj;
            try { proj = sln.Projects.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (proj is null) continue;

            string fullName = string.Empty;
            try { fullName = (string)proj.FullName ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (!fullName.EndsWith(".tcmproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string projName = string.Empty;
            try { projName = (string)proj.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            measProjects.Add((proj, projName));
        }

        if (measProjects.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var mp in measProjects)
                if (string.Equals(mp.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase))
                    return mp.Proj;
            throw new InvalidOperationException(
                $"Measurement project '{name}' not found in the open solution. " +
                $"Available: {string.Join(", ", measProjects.Select(m => $"'{m.Name}'"))}.");
        }

        if (measProjects.Count == 1) return measProjects[0].Proj;

        throw new InvalidOperationException(
            $"The open solution contains {measProjects.Count} measurement projects " +
            $"({string.Join(", ", measProjects.Select(m => $"'{m.Name}'"))}). " +
            "Pass projectName to choose which one to operate on.");
    }

    private static dynamic? FindDataPool(dynamic rootItem)
    {
        foreach (dynamic child in EnumerateChildren(rootItem))
        {
            string childName = string.Empty;
            try { childName = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            // "DataPool" is the stable internal name (not a localized display
            // string) per the Automation Interface documentation.
            if (string.Equals(childName, "DataPool", StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private static dynamic RequireDataPool(dynamic rootItem)
    {
        return FindDataPool(rootItem)
            ?? throw new InvalidOperationException(
                "DataPool node not found in scope project. " +
                "The 'Empty Measurement Project' template must include a DataPool.");
    }

    // ---- ProjectItem helpers ----------------------------------------------

    // The IMeasurementScope for a scope-tree ProjectItem, early-bound via the
    // TE130X interop assembly. Late-bound IDispatch cannot resolve the custom
    // CreateChild/LookUpChild members (DISP_E_UNKNOWNNAME), so the cast is
    // mandatory (Infosys 498477707).
    // NOTE: callers MUST assign the result to a statically-typed
    // IMeasurementScope local (never `var`, never `Ims(x).Method()`):
    // invoking Ims with a dynamic argument makes the call dynamic and the
    // result `dynamic`, which would route interface calls back through the
    // broken IDispatch path. The `object o` hop forces a real static cast
    // (COM QueryInterface) onto the embedded interop interface.
    private static TwinCAT.Measurement.AutomationInterface.IMeasurementScope Ims(dynamic projectItem)
    {
        object o = projectItem.Object;
        return (TwinCAT.Measurement.AutomationInterface.IMeasurementScope)o;
    }

    // Enumerate a node's child ProjectItems (1-based EnvDTE collection).
    private static IEnumerable<dynamic> EnumerateChildren(dynamic projectItem)
    {
        dynamic items;
        try { items = projectItem.ProjectItems; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { yield break; }

        int count;
        try { count = (int)items.Count; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { yield break; }

        for (int i = 1; i <= count; i++)
        {
            dynamic? child = null;
            try { child = items.Item(i); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (child is not null) yield return child;
        }
    }

    // ---- CreateChild / LookUpChild (early-bound IMeasurementScope) ---------

    // IMeasurementScope.CreateChild(out object item, string name, int elementType).
    // The out object is an EnvDTE ProjectItem; we name it via the child's own
    // IMeasurementScope (Infosys: piChart.Object.ChangeName(...)).
    private static dynamic ScopeCreateChild(dynamic parentItem, string name, int elementType)
    {
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope ims = Ims(parentItem);

        int hr;
        object? childObj;
        try { hr = ims.CreateChild(out childObj, name, elementType); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"IMeasurementScope.CreateChild('{name}', {elementType}) failed: {ex.Message}", ex);
        }

        // D-14: a failure HRESULT is a failure even when an object came back.
        if (hr < 0)
            throw new InvalidOperationException(
                $"IMeasurementScope.CreateChild('{name}', {elementType}) failed with hr=0x{hr:X8}.");
        dynamic child = childObj
            ?? throw new InvalidOperationException(
                $"IMeasurementScope.CreateChild returned null for '{name}' (hr=0x{hr:X8}).");

        // The rename is part of the create; its busy retry must not replay the CreateChild.
        AfterMutation<object?>(() =>
        {
            try
            {
                TwinCAT.Measurement.AutomationInterface.IMeasurementScope c = Ims(child);
                c.ChangeName(name);
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { /* best effort; LookUpChild keys off name */ }
            return null;
        });
        return child;
    }

    // IMeasurementScope.LookUpChild(string name, out object childNode) → int.
    // Returns the child ProjectItem, or null when not found.
    private static dynamic? ScopeLookUpChild(dynamic parentItem, string name)
    {
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope ims;
        try { ims = Ims(parentItem); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }

        try
        {
            ims.LookUpChild(name, out object? child);
            return child;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) {
            return null;
        }
    }

    private static dynamic ScopeLookUpOrCreate(dynamic parentItem, string name, int elementType)
        => ScopeLookUpOrCreate(parentItem, name, elementType, out bool _);

    private static dynamic ScopeLookUpOrCreate(dynamic parentItem, string name, int elementType, out bool created)
    {
        dynamic? existing = ScopeLookUpChild(parentItem, name);
        created = existing is null;
        if (existing is not null) return existing;
        return ScopeCreateChild(parentItem, name, elementType);
    }

    // ---- Properties -------------------------------------------------------

    // One property write and what was read back afterwards (issue #153, D-5/D-6): Applied is
    // true only when the value read from a fresh Properties enumeration equals what was written.
    private sealed record PropWrite(string Label, string? Key, bool Applied, string? ReadBack, string? Error);

    private static IEnumerable<string> PropKeys(dynamic node)
    {
        foreach (dynamic p in AcqProperties(node))
        {
            string k = SafePropName(p);
            if (k.Length > 0) yield return k;
        }
    }

    // Assign `value` to the first property whose NORMALISED key satisfies `match`, converting it to
    // the property's current value type (invariant culture), then read the value back.
    private static bool HasKey(object nodeObj, Func<string, bool> match)
    {
        dynamic node = nodeObj;
        foreach (dynamic p in AcqProperties(node))
        {
            string k = SafePropName(p);
            if (k.Length > 0 && match(NormName(k))) return true;
        }
        return false;
    }

    private static PropWrite WriteProp(object nodeObj, string label, Func<string, bool> match, object value)
    {
        dynamic node = nodeObj;
        dynamic? prop = null;
        string? key = null;
        foreach (dynamic p in AcqProperties(node))
        {
            string k = SafePropName(p);
            if (k.Length > 0 && match(NormName(k))) { prop = p; key = k; break; }
        }
        if (prop is null) return new PropWrite(label, null, false, null, "no matching property");

        object? current = ComCall.ReadProp<object?>(() => (object?)prop.Value, null);
        object typed = ScopePropertyValues.CoerceTo(current, value);
        try { prop.Value = typed; }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            try { prop.Value = Convert.ToString(value, CultureInfo.InvariantCulture); }
            catch (Exception ex2) when (ComCall.NotBusy(ex2))
            {
                return new PropWrite(label, key, false, null, $"assignment rejected: {ex2.Message}");
            }
        }

        object? back = ReadPropValue(node, key!);
        string? backText = back is null ? null : Convert.ToString(back, CultureInfo.InvariantCulture);
        bool ok = back is not null && ScopePropertyValues.SameValue(back, value);
        return new PropWrite(label, key, ok, backText,
            ok ? null : $"read back '{backText ?? "<unreadable>"}' after writing '{Convert.ToString(value, CultureInfo.InvariantCulture)}'");
    }

    private static object? ReadPropValue(dynamic node, string key)
    {
        foreach (dynamic p in AcqProperties(node))
            if (string.Equals(SafePropName(p), key, StringComparison.Ordinal))
                return ComCall.ReadProp<object?>(() => (object?)p.Value, null);
        return null;
    }

    private static void ThrowIfNotApplied(string what, IReadOnlyList<PropWrite> writes, Func<string>? keysHint = null)
    {
        var failed = writes.Where(w => !w.Applied).ToList();
        if (failed.Count == 0) return;
        var ok = writes.Where(w => w.Applied).Select(w => $"{w.Label}={w.ReadBack}").ToList();
        string hint = failed.Any(f => f.Key is null) && keysHint is not null ? $" Available keys: {keysHint()}." : string.Empty;
        throw new InvalidOperationException(
            $"{what}: NOT applied — " +
            string.Join("; ", failed.Select(f => $"{f.Label} ({(f.Key is null ? "no matching property" : f.Key)}: {f.Error})")) +
            $". Applied and read back: {(ok.Count == 0 ? "nothing" : string.Join(", ", ok))}.{hint}");
    }

    // Rename via IMeasurementScope.ChangeName and read the ProjectItem name back.
    private static PropWrite RenameNode(object nodeObj, string name)
    {
        dynamic node = nodeObj;
        string? err = null;
        try { TwinCAT.Measurement.AutomationInterface.IMeasurementScope n = Ims(node); n.ChangeName(name); }
        catch (Exception ex) when (ComCall.NotBusy(ex)) { err = ex.Message; }
        string? back = AfterMutation(() => ComCall.ReadProp<string?>(() => (string)node.Name, null));
        bool ok = string.Equals(back, name, StringComparison.Ordinal);
        return new PropWrite("name", "ChangeName", ok, back,
            ok ? null : $"read back '{back ?? "<unreadable>"}'" + (err is null ? string.Empty : $" ({err})"));
    }

    private static bool SaysNoData(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            string m = e.Message ?? string.Empty;
            if (m.Contains("no data", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("keine Daten", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string DescribeScopeError(Exception ex)
    {
        Exception inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return $"{inner.Message} (hr=0x{inner.HResult:X8})";
    }

    // Bind a channel to an acquisition by iterating Properties for the
    // "Y-Data.Acquisition" entry and setting its Value (Infosys pattern).
    // Returns null on success, else a diagnostic of every attempt that failed.
    private static string? TrySetChannelAcquisition(dynamic chanNode, object acqRaw)
    {
        var attempts = new List<string>();
        foreach (dynamic p in AcqProperties(chanNode))
        {
            string pn = SafePropName(p);
            string norm = NormName(pn);
            if (norm != "ydataacquisition" && !norm.EndsWith("acquisition", StringComparison.Ordinal)) continue;

            foreach (var (label, val) in new (string, object?)[]
            {
                ("raw-object",   acqRaw),
                ("ims-cast",     SafeImsCast(acqRaw)),
            })
            {
                if (val is null) continue;
                try { p.Value = val; return null; }
                catch (Exception ex) when (ComCall.NotBusy(ex)) { attempts.Add($"{pn}<-{label}: {ex.Message}"); }
            }
        }
        return attempts.Count == 0
            ? "no Y-Data.Acquisition property on channel"
            : string.Join(" ; ", attempts);
    }

    // Read a channel's bound-acquisition reference by ITERATING Properties for
    // the "Y-Data.Acquisition" entry (Properties.Item(key) is unreliable/NRE on
    // TC3 4026.22 — same workaround as TrySetChannelAcquisition / AcqProperties).
    // On this build the Property.Value marshals as an opaque System.Guid (NOT a
    // COM object, NOT a name; verified live 2026-05-18). A non-empty Guid is a
    // present binding; Guid.Empty / null / unreadable means unbound. The id is
    // intentionally not resolved to the acquisition here — the acquisition node
    // exposes no matching Guid, so there is no reverse map.
    private static Guid? ReadAcquisitionGuid(dynamic chanNode)
    {
        foreach (dynamic p in AcqProperties(chanNode))
        {
            string norm = NormName(SafePropName(p));
            if (norm != "ydataacquisition" && !norm.EndsWith("acquisition", StringComparison.Ordinal)) continue;

            object? v;
            try { v = p.Value; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { continue; }
            if (v is Guid g) return g;
            if (v is string s && Guid.TryParse(s, out var gs)) return gs;
        }
        return null;
    }

    private static object? SafeImsCast(object raw)
    {
        try { return (TwinCAT.Measurement.AutomationInterface.IMeasurementScope)raw; }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return null; }
    }

    // Created nodes must not be left at template defaults ("YT Chart", "Axis Group",
    // "Value Axis"). Returns every write with its read-back so the caller can report misses.
    private static List<PropWrite> SetScopeLabel(object node, string label) =>
    [
        RenameNode(node, label),
        WriteProp((object)node, "name", k => k == "commonname", label),
    ];

    // The stacked-axis property key is NOT documented on Infosys and could not be observed on the
    // dev VM (issue #153): candidate keys by normalised name; a boolean property takes true/false,
    // a text/enum property "Stacked"/"Overlaid". Read back; no candidate → not applied.
    // Live key on TE130x / 4026.22 (issue #201): "Settings.Stacked Axes" (Boolean) → normalised "settingsstackedaxes".
    private static readonly string[] AxisModeKeys = ["settingsstackedaxes", "stackedaxes", "stackedaxis", "yaxismode", "axismode", "commonstackedaxes"];

    private static PropWrite ApplyAxisMode(object chartNodeObj, string mode)
    {
        dynamic chartNode = chartNodeObj;
        bool stacked = mode == ScopeAxisMode.Stacked;
        dynamic? prop = null;
        foreach (dynamic p in AcqProperties(chartNode))
            if (AxisModeKeys.Contains((string)NormName(SafePropName(p)))) { prop = p; break; }
        if (prop is null) return new PropWrite("axisMode", null, false, null, "no stacked-axis property on this chart");

        object? current = ComCall.ReadProp<object?>(() => (object?)prop.Value, null);
        object value = current is bool ? stacked : (stacked ? "Stacked" : "Overlaid");
        string key = SafePropName(prop);
        string normKey = NormName(key);
        return WriteProp((object)chartNode, "axisMode", k => k == normKey, value);
    }

    // ---- Tree walk --------------------------------------------------------

    // Recursive walk over EnvDTE ProjectItems. Kind is determined by the parent
    // context (the documented tree shape is fixed), refined by the DataPool name
    // and the presence of the Y-Data.Acquisition property (channels).
    private static void WalkScopeTree(
        dynamic node, ScopeCtx ctx, List<ScopeNodeEntry> sink, string parentPath)
    {
        foreach (dynamic child in EnumerateChildren(node))
        {
            string name = string.Empty;
            try { name = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            string path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}^{name}";

            bool isDataPool = string.Equals(name, "DataPool", StringComparison.OrdinalIgnoreCase);
            bool hasAcqProp = HasProperty(child, "Y-Data.Acquisition");

            // "Trigger"/"Triggers" is the template's trigger-set node — a direct
            // child of the scope root like DataPool. It is not a chart and is
            // outside the chart/axis/channel model these tools expose; skip it
            // (stable internal name, same rationale as the DataPool special-case).
            if (ctx == ScopeCtx.Root && !isDataPool &&
                (string.Equals(name, "Trigger",  StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "Triggers", StringComparison.OrdinalIgnoreCase)))
                continue;

            string  kind;
            ScopeCtx childCtx;
            switch (ctx)
            {
                case ScopeCtx.Root when isDataPool:
                    kind = ScopeNodeKind.DataPool;  childCtx = ScopeCtx.DataPool; break;
                case ScopeCtx.Root:
                    kind = ScopeNodeKind.Chart;     childCtx = ScopeCtx.Chart;    break;
                case ScopeCtx.Chart:
                    kind = ScopeNodeKind.AxisGroup; childCtx = ScopeCtx.AxisGroup; break;
                case ScopeCtx.AxisGroup:
                    kind = ScopeNodeKind.Channel;   childCtx = ScopeCtx.AxisGroup; break;
                case ScopeCtx.DataPool:
                    kind = ScopeNodeKind.Acquisition; childCtx = ScopeCtx.DataPool; break;
                default:
                    kind = hasAcqProp ? ScopeNodeKind.Channel : ScopeNodeKind.Chart;
                    childCtx = ScopeCtx.AxisGroup; break;
            }

            string? bound   = null;
            string? symName = null;
            if (kind == ScopeNodeKind.Channel)
            {
                // The chart/channel does NOT reference a PLC variable directly —
                // it CONNECTS to a DataPool acquisition, and the acquisition
                // owns the PLC symbol (channel ─Y-Data.Acquisition→ acquisition
                // ─Symbol Name→ PLC). On TC3 4026.22 the Y-Data.Acquisition
                // Property.Value marshals as an opaque Guid reference (verified
                // live 2026-05-18) — NOT a COM object and NOT a name — and the
                // acquisition exposes no matching Guid, so the link cannot be
                // reverse-mapped by id. A non-empty Guid means the channel IS
                // bound; that is the only fact needed to suppress the false
                // "NO SYMBOL" warning (issue #60). The acquisition node is
                // listed and symbol-validated independently.
                Guid? acqId = ReadAcquisitionGuid(child);
                if (acqId is { } id && id != Guid.Empty)
                {
                    // Acquisitions are walked first (DataPool is the first
                    // scope-root child) so they are already in the sink. The
                    // id is not reverse-mappable; resolve the acquisition for
                    // display best-effort: exact name match (the common case —
                    // GUI/add_scope_channel name channel and acquisition alike),
                    // else the sole acquisition. Display only; the binding
                    // itself is already proven by the non-empty Guid.
                    var acqs = sink.Where(e => e.Kind == ScopeNodeKind.Acquisition).ToList();
                    var match = acqs.FirstOrDefault(a =>
                                    NormName(a.Name) == NormName(name))
                                ?? (acqs.Count == 1 ? acqs[0] : null);
                    bound   = match?.Name ?? "(acquisition)";
                    symName = match?.SymbolName;
                }
            }
            else if (kind == ScopeNodeKind.Acquisition)
            {
                var ap = new List<ScopeAcquisitionProperty>();
                foreach (dynamic p in AcqProperties(child))
                {
                    string pn = SafePropName(p);
                    if (pn.Length > 0) ap.Add(new ScopeAcquisitionProperty(pn, SafePropValue(p)));
                }
                symName = MatchValue(ap, "symbolname");
            }

            string? label = null;
            bool atDefault = false;
            try
            {
                label = (string)child.Properties.Item("Common.Name").Value ?? name;
                string[] defaults = ["YT Chart", "Axis Group", "Value Axis"];
                atDefault = defaults.Any(d => string.Equals(label, d, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { label = name; }

            sink.Add(new ScopeNodeEntry(kind, name, path, bound, label, atDefault, symName));

            // Recurse everywhere except channels/acquisitions (leaf nodes).
            if (kind is not (ScopeNodeKind.Channel or ScopeNodeKind.Acquisition))
                WalkScopeTree(child, childCtx, sink, path);
        }
    }

    private static bool HasProperty(dynamic node, string key)
    {
        try
        {
            _ = node.Properties.Item(key);
            return true;
        }
        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { return false; }
    }

    // ---- scope-recording -----------------------------------------------------
    //
    // StartRecord / StopRecord / ScopeState are direct IMeasurementScope methods
    // on the scope-config root item (Infosys te13xx_tc3_scopeview/5961702923).
    // ScopeState returns the TwinCAT.Measurement.AutomationInterface.ScopeViewState
    // enum: Config=0, Record=1, Reply=2 — empirically confirmed on TC3 4026.
    //
    // Record settings live as ProjectItem.Properties on the scope-config root.
    // Keys are German-locale display names (never documented), so we use the same
    // normalised-token matching as ApplyAcquisitionSymbol. First-run with a real
    // XAE will reveal the actual keys via the empty-result guard.
    //
    // Trigger groups are children of the "Trigger"/"Triggers" template node under
    // the scope root (which WalkScopeTree skips). CreateChild on that node creates
    // a group; CreateChild on a group (with TriggerSetType id) creates a set.
    // The trigger-group element-type id and the property names for Action /
    // threshold / edge / symbol binding are empirical — marked with TODO-VERIFY.

    public string StartScopeRecord(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope scope = Ims(root);
        scope.StartRecord();
        return "Recording started.";
    }, OpenTimeout);

    public string StopScopeRecord(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope scope = Ims(root);
        scope.StopRecord();
        return "Recording stopped.";
    }, OpenTimeout);

    public ScopeRecordState GetScopeRecordState(string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope scope = Ims(root);

        // ScopeViewState: Config=0, Record=1, Reply=2.
        // ScopeState is not on IMeasurementScope; access via late-binding.
        // D-18: IMeasurementScope (TE130x AI 3.4.3148) has NO ScopeState member — the late-bound
        // read may fail. A failed or unknown read is an error, never "Config": answering "Config"
        // invited a duplicate StartRecord on a scope that was already recording.
        int stateInt;
        try { stateInt = Convert.ToInt32((object)((dynamic)scope).ScopeState, CultureInfo.InvariantCulture); }
        catch (Exception comEx) when (ComCall.NotBusy(comEx))
        {
            throw new InvalidOperationException(
                $"The scope record state could not be read ({DescribeScopeError(comEx)}); the state is unknown.", comEx);
        }

        string state = stateInt switch
        {
            0 => ScopeViewState.Config,
            1 => ScopeViewState.Record,
            2 => ScopeViewState.Reply,
            _ => throw new InvalidOperationException($"Unknown scope record state value {stateInt}."),
        };
        return new ScopeRecordState(state, state == ScopeViewState.Record, state == ScopeViewState.Reply);
    }, OpenTimeout);

    public string SaveScopeData(string? path, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);
        TwinCAT.Measurement.AutomationInterface.IMeasurementScope scope = Ims(root);

        string outPath = path?.Trim() is { Length: > 0 } p
            ? p
            : Path.Combine(
                Path.GetDirectoryName((string)dte.Solution.FullName)
                    ?? throw new InvalidOperationException("Cannot determine solution directory."),
                "ScopeData.svdx");

        try { scope.SaveSVD(outPath); }
        catch (Exception ex) when (ComCall.NotBusy(ex))
        {
            if (SaysNoData(ex))
                throw new InvalidOperationException(
                    "No recorded data — run a recording (twincat_start_scope_record / twincat_stop_scope_record) before saving.", ex);
            throw new InvalidOperationException($"SaveSVD failed: {DescribeScopeError(ex)}", ex);
        }

        if (!File.Exists(outPath))
            throw new InvalidOperationException(
                $"SaveSVD claimed success but output file not found: {outPath}");

        return outPath;
    }, OpenTimeout);

    public string SetScopeRecordSettings(
        double? recordTimeS,
        bool?   ringbuffer,
        string? fileStore,
        string? recordMode,
        string? autoSaveMode,
        string? autoSavePath,
        string? filenameMask,
        string? syncMode,
        string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        // Keys are NOT verified on this TE130x version (issue #153): matched by normalised token,
        // every write read back, any miss is an error naming what did and did not apply.
        var writes = AfterMutation(() =>
        {
            var w = new List<PropWrite>();
            void TryProp(string token, object? value, string label)
            {
                if (value is null) return;
                w.Add(WriteProp((object)root, label, k => k.Contains(token, StringComparison.Ordinal), value));
            }
            // Record.Record Time is in 100 ns ticks (see ScopeTime) — convert the seconds parameter.
            TryProp("recordtime",   recordTimeS is null ? null : ScopeTime.SecondsToTicks(recordTimeS.Value), "recordTime[100ns]");
            TryProp("ringbuffer",   ringbuffer,   "ringbuffer");
            TryProp("filestore",    fileStore,    "fileStore");
            // The live key is "Record.Start Mode" (normalised "recordstartmode"); "recordmode" never matched.
            // Issue #201: "Record.Start Mode" is ScopeProject.StartMode (RunMode: 2 ClientStart, 4 TriggerStart),
            // declared [ReadOnly(true)] — a write is silently ignored (live 2026-10-10, numeric value too).
            if (recordMode is not null)
                w.Add(new PropWrite("recordMode", "Record.Start Mode", false, null,
                    "read-only in TE130x (RunMode, [ReadOnly]); the start mode follows from the scope's trigger " +
                    "configuration, it cannot be written — omit recordMode"));
            TryProp("autosavemode", autoSaveMode, "autoSaveMode");
            TryProp("autosavepath", autoSavePath, "autoSavePath");
            TryProp("filename",     filenameMask, "filenameMask");
            TryProp("syncmode",     syncMode,     "syncMode");
            return w;
        });

        ThrowIfNotApplied("Record settings", writes, keysHint: () => string.Join(", ", PropKeys(root)));
        return $"Set record settings (read back): {string.Join(", ", writes.Select(w => $"{w.Label}={w.ReadBack}"))}.";
    }, OpenTimeout);

    public string CreateScopeTriggerGroup(string action, string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        // The trigger container is the template's "Trigger"/"Triggers" child of the scope root.
        // It is never created here: CreateChild on the root with element type 0 creates a YT
        // CHART (ChartType.YT = 0), which the old code then used as a trigger container.
        dynamic triggersNode = FindTriggerContainer(root)
            ?? throw new InvalidOperationException(
                "This scope project has no 'Trigger' node. Trigger groups can only be added to a scope " +
                "project whose template contains one.");

        // EXPERIMENTAL (issue #153, D-5): the TE130x Automation Interface (3.4.3148) defines no
        // element-type enum for a trigger GROUP (only TriggerSetType/ConditionType), and the group's
        // property keys could not be observed on the dev VM. 0 is the only id tried; the result is
        // checked, the action is written and read back, and on any miss the group is deleted again.
        string groupName = $"TriggerGroup_{action}";
        dynamic group = ScopeCreateChild(triggersNode, groupName, 0);

        // Live key (TE130x, 2026-10-06): "Trigger Action.Trigger Action" → normalised
        // "triggeractiontriggeraction"; the old exact match ("action"/"triggeraction") never hit it.
        object actionValue = ScopeTriggerAction.ToEventActionId(action) is int id ? id : action;
        var w = AfterMutation(() => WriteProp((object)group, "action",
            k => k is "action" or "triggeraction" || k.EndsWith("triggeraction", StringComparison.Ordinal), actionValue));
        if (!w.Applied)
        {
            string keys = AfterMutation(() => string.Join(", ", PropKeys(group)));
            bool removed = AfterMutation(() => ComCall.Try(() => group.Delete()));
            throw new InvalidOperationException(
                $"Trigger group '{groupName}' was created but its action '{action}' could NOT be applied " +
                $"({w.Error}). The group was {(removed ? "deleted again" : "NOT deleted — remove it in the Scope editor")}. " +
                $"Group property keys: {(keys.Length == 0 ? "(none)" : keys)}.");
        }

        return $"Created trigger group '{groupName}' with action '{w.ReadBack}' (read back). EXPERIMENTAL: element id and keys unverified on this TE130x version.";
    }, OpenTimeout);

    private static dynamic? FindTriggerContainer(dynamic root)
    {
        foreach (dynamic child in EnumerateChildren(root))
        {
            string cn = ComCall.ReadProp(() => (string)child.Name, string.Empty) ?? string.Empty;
            if (string.Equals(cn, "Trigger",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cn, "Triggers", StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    public string AddScopeTriggerSet(
        string  groupName,
        string  setType,
        string? symbol,
        double? threshold,
        string? edge,
        string? compare,
        string? projectName) => ComBusyRetry.Run(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        // Locate the trigger group by name.
        dynamic? group = FindTriggerGroup(root, groupName);
        if (group is null)
            throw new InvalidOperationException(
                $"Trigger group '{groupName}' not found. " +
                "Create it first with twincat_create_scope_trigger_group.");

        // Create a trigger set (TriggerSetType id: ChannelSet=0, DirectorySet=1).
        int setId = ScopeTriggerSetType.ToId(setType);
        string setName = $"TriggerSet_{setType}_{(symbol is not null ? symbol.Split('.').Last() : "1")}";
        dynamic trigSet = ScopeCreateChild(group, setName, setId);

        // EXPERIMENTAL (issue #153, D-5): TriggerSetType ids are vendor-defined (ChannelSet=0,
        // DirectorySet=1 in TwinCAT.Measurement.AutomationInterface); the condition property keys
        // are NOT verified. Each condition is written and read back; any miss → error.
        var writes = AfterMutation(() =>
        {
            var list = new List<PropWrite>();
            void Cond(string token, object? value, string label)
            {
                if (value is null) return;
                list.Add(WriteProp((object)trigSet, label, k => k.Contains(token, StringComparison.Ordinal), value));
            }
            // Live keys (TE130x, 2026-10-06): Common.Release, Common.Threshold, Common.Used Data,
            // Common.Combine, Common.Name — there is no "symbol"/"edge" key on a trigger set.
            void CondAny(object? value, string label, params string[] tokens)
            {
                if (value is null) return;
                list.Add(WriteProp((object)trigSet, label, k => tokens.Any(t => k.Contains(t, StringComparison.Ordinal)), value));
            }
            // Issue #201: "Common.Used Data" is a Guid reference to a DataPool acquisition
            // (ChannelTriggerSet.ReusedAcquisitionOrInterpreterGuid); the acquisition's Guid is not exposed
            // by the Automation Interface, so a symbol cannot be bound here.
            if (symbol is not null)
                list.Add(new PropWrite("symbol", "Common.Used Data", false, null,
                    "the trigger channel is a reference (Guid) to a DataPool acquisition that the Automation " +
                    "Interface does not expose — pick the channel in the Scope editor"));
            Cond("threshold", threshold, "threshold");
            CondAny(edge is null ? null : ScopeTriggerCondition.ToReleaseId(edge) is int rel ? rel : edge,
                "edge", "edge", "release");
            Cond("compare",   compare,   "compare");
            return list;
        });

        if (writes.Any(x => !x.Applied))
        {
            var ok = writes.Where(x => x.Applied).Select(x => $"{x.Label}={x.ReadBack}").ToList();
            var failed = writes.Where(x => !x.Applied).Select(x => $"{x.Label} ({x.Key ?? "no matching property"}: {x.Error})");
            // Name the set's real keys (like the other scope tools do) so a key mismatch is diagnosable.
            string keys = writes.Any(x => x.Key is null)
                ? $" Trigger set property keys: {AfterMutation(() => string.Join(", ", PropKeys(trigSet)))}."
                : string.Empty;
            throw new InvalidOperationException(
                $"Trigger set '{setName}' was created in group '{groupName}', but these conditions could " +
                $"NOT be applied: {string.Join("; ", failed)}. Applied and read back: " +
                $"{(ok.Count == 0 ? "nothing" : string.Join(", ", ok))}. Set the missing conditions in the Scope editor.{keys}");
        }

        var desc = new List<string> { $"setType={setType}" };
        desc.AddRange(writes.Select(x => $"{x.Label}={x.ReadBack}"));
        return $"Added trigger set '{setName}' to group '{groupName}' (read back): {string.Join(", ", desc)}. EXPERIMENTAL: condition keys unverified on this TE130x version.";
    }, OpenTimeout);

    public IReadOnlyList<ScopeTriggerGroupEntry> ListScopeTriggers(string? projectName) =>
        ComBusyRetry.Run<IReadOnlyList<ScopeTriggerGroupEntry>>(() =>
    {
        dynamic dte  = GetActiveTcDte();
        dynamic root = RequireScopeRootItem(dte, projectName);

        // Locate the Triggers container.
        dynamic? triggersNode = null;
        foreach (dynamic child in EnumerateChildren(root))
        {
            string cn = string.Empty;
            try { cn = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (string.Equals(cn, "Trigger",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cn, "Triggers", StringComparison.OrdinalIgnoreCase))
            {
                triggersNode = child; break;
            }
        }
        if (triggersNode is null)
            return Array.Empty<ScopeTriggerGroupEntry>();

        var result = new List<ScopeTriggerGroupEntry>();
        foreach (dynamic group in EnumerateChildren(triggersNode))
        {
            string groupName = string.Empty;
            try { groupName = (string)group.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            // Read the Action property (token-matched).
            string action = string.Empty;
            var groupProps = new List<dynamic>();
            try
            {
                dynamic gp = group.Properties;
                int gc = (int)gp.Count;
                for (int i = 1; i <= gc; i++)
                {
                    try { groupProps.Add(gp.Item(i)); } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                }
                dynamic? ap = groupProps.FirstOrDefault(p =>
                    NormName(SafePropName(p)) is "action" or "triggeraction" or "type");
                if (ap is not null) try { action = ap.Value?.ToString() ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            }
            catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

            var sets = new List<ScopeTriggerSetEntry>();
            foreach (dynamic trigSet in EnumerateChildren(group))
            {
                string setName = string.Empty;
                try { setName = (string)trigSet.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                // Read condition properties.
                string? sym = null, edgeVal = null, cmpVal = null;
                double? thresh = null;
                try
                {
                    dynamic sp = trigSet.Properties;
                    int sc = (int)sp.Count;
                    for (int i = 1; i <= sc; i++)
                    {
                        try
                        {
                            dynamic p = sp.Item(i);
                            string norm = NormName(SafePropName(p));
                            string val  = string.Empty;
                            try { val = Convert.ToString((object?)p.Value, CultureInfo.InvariantCulture) ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                            if (norm.Contains("symbol"))    sym    = val;
                            if (norm.Contains("threshold")) { if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) thresh = d; }
                            if (norm.Contains("edge"))      edgeVal = val;
                            if (norm.Contains("compare"))   cmpVal  = val;
                        }
                        catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    }
                }
                catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }

                // Infer setType from element position (set id not directly readable).
                string setType = setName.Contains("Directory", StringComparison.OrdinalIgnoreCase)
                    ? "DirectorySet" : "ChannelSet";

                sets.Add(new ScopeTriggerSetEntry(setType, sym, thresh, edgeVal, cmpVal));
            }

            result.Add(new ScopeTriggerGroupEntry(groupName, action, sets));
        }
        return result;
    }, OpenTimeout);

    // Find a trigger group by name within the Triggers container.
    private static dynamic? FindTriggerGroup(dynamic root, string groupName)
    {
        foreach (dynamic child in EnumerateChildren(root))
        {
            string cn = string.Empty;
            try { cn = (string)child.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
            if (string.Equals(cn, "Trigger",  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cn, "Triggers", StringComparison.OrdinalIgnoreCase))
            {
                foreach (dynamic group in EnumerateChildren(child))
                {
                    string gn = string.Empty;
                    try { gn = (string)group.Name ?? string.Empty; } catch (Exception comEx) when (ComCall.NotBusy(comEx)) { }
                    if (string.Equals(gn, groupName.Trim(), StringComparison.OrdinalIgnoreCase))
                        return group;
                }
            }
        }
        return null;
    }
#endif
}
