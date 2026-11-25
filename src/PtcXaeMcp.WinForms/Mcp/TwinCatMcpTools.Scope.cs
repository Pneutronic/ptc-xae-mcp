using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    // ---- twincat_create_scope_project ---------------------------------------

    [McpServerTool(Name = "twincat_create_scope_project", ReadOnly = false, Destructive = false)]
    [Description("Add a TwinCAT Scope (measurement) project to the open solution. Uses the 'YT Scope Project' template; needs TE130X Scope View installed. Returns the project path.")]
    public string CreateScopeProject(
        [Description("Name of the new measurement project. Default 'MeasurementProject'.")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_create_scope_project", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).CreateScopeProject(projectName)), cancellationToken);

    // ---- twincat_remove_scope_project ---------------------------------------

    [McpServerTool(Name = "twincat_remove_scope_project", ReadOnly = false, Destructive = true)]
    [Description("Remove a Scope (measurement) project node from the open solution. Counterpart of twincat_create_scope_project. Does NOT save the solution or delete files from disk. Idempotent: benign result if no project matches. Call it before closing a solution with a scope project you created, so no dangling node breaks the reopen ('Missing Project Nodes').")]
    public string RemoveScopeProject(
        [Description("Measurement project to remove; must match an existing one. Required when the solution has several (else [Error]).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_remove_scope_project", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).RemoveScopeProject(projectName)), cancellationToken);

    // ---- twincat_add_scope_channel -----------------------------------------

    [McpServerTool(Name = "twincat_add_scope_channel", ReadOnly = false, Destructive = false)]
    [Description("Add a channel bound to a PLC symbol to the scope project. Creates chart, axis-group and channel, reusing a chart/axis of the same name, and reuses or creates the DataPool acquisition (same symbol and port). Every field is read back; one that did not land returns [Error]. The acquisition is made and checked before chart, axis group and channel, so a failed call leaves no half-made channel (the Scope tree cannot be cleaned up through the Automation Interface on TE130x 4026.22; the error lists anything left). NC symbols work too: pass the NC tree path, symbolName 'Axes.<axis>.<variable>' and targetPort 501. Returns the channel's tree path.")]
    public string AddScopeChannel(
        [Description("PLC symbol path, e.g. 'TIPC^PLC^PLC Projekt^MAIN.rActualValue'.")] string  symbol,
        [Description("Chart name; reused if it exists. Default: the symbol path.")] string? chartName     = null,
        [Description("Axis-group name in the chart; reused if it exists. Default: the symbol path.")] string? axisName      = null,
        [Description("Channel name. Default: the symbol path.")] string? channelName   = null,
        [Description("True puts the channel on its own Y-axis. Default false.")] bool    stackedAxis   = false,
        [Description("PLC-side symbol name for the acquisition, if it differs from the one derived from symbol.")] string? symbolName    = null,
        [Description("ADS port of the runtime. Default 851 (PLC 1); 501 for NC symbols.")] int?    targetPort    = null,
        [Description("Sample mode, e.g. 'Task Sampletime'. Default: the scope's default.")] string? sampleMode    = null,
        [Description("Sample time in ms. Without sampleMode this sets 'Fixed Sampletime'. Default: the scope's default.")] int?    sampleTimeMs  = null,
        [Description("Optional comment on the acquisition's symbol binding.")] string? symbolComment = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName   = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_scope_channel",
            new { symbol, chartName, axisName, channelName, stackedAxis, symbolName, targetPort, sampleMode, sampleTimeMs, symbolComment, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .AddScopeChannel(symbol, chartName, axisName, channelName, stackedAxis,
                        symbolName, targetPort, sampleMode, sampleTimeMs, symbolComment, projectName)), cancellationToken);

    // ---- twincat_read_scope_acquisition ------------------------------------

    [McpServerTool(Name = "twincat_read_scope_acquisition", ReadOnly = true)]
    [Description("Read the symbol and ADS settings of one scope DataPool acquisition. Returns Symbol Name, Data Type, Sample Mode, Sample Time, Target Port and all raw properties; flags an acquisition with no symbol bound (records nothing). Read-only.")]
    public string ReadScopeAcquisition(
        [Description("Acquisition name or 'DataPool^<name>'.")] string acqSelector,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_read_scope_acquisition", new { acqSelector, projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, info) = new ScopeCoordinator(bridge.Dispatcher.Session)
                    .ReadScopeAcquisition(acqSelector, projectName);
                if (McpErrors.IsError(summary) || info is null)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Path        : {info.Path}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Symbol Name : {info.SymbolName ?? "<none>"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Data Type   : {info.DataType ?? "<unknown>"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Sample Mode : {info.SampleMode ?? "<unknown>"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Sample Time : {info.SampleTimeMs ?? "<unknown>"} ms");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Target Port : {info.TargetPort ?? "<unknown>"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Target Sys  : {info.TargetSystem ?? "<unknown>"}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  --- all properties ({info.AllProperties.Count}) ---");
                foreach (var p in info.AllProperties)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {p.Key} = {p.Value}");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    // ---- twincat_set_scope_acquisition_symbol ------------------------------

    [McpServerTool(Name = "twincat_set_scope_acquisition_symbol", ReadOnly = false, Destructive = true)]
    [Description("Set or repair the PLC symbol and sample settings of a scope acquisition. Only supplied fields change; supply at least one. Every field is read back; any that did not land returns [Error] naming what did and did not apply. Returns the read-back values.")]
    public string SetScopeAcquisitionSymbol(
        [Description("Acquisition name or 'DataPool^<name>'.")] string  acqSelector,
        [Description("PLC symbol to bind, e.g. 'GVL_HMI.HmiOpened'.")] string? symbolName    = null,
        [Description("ADS port of the PLC runtime, e.g. 851.")] int?    targetPort    = null,
        [Description("AMS NetId of the target system to sample from.")] string? targetSystem  = null,
        [Description("Sample mode, e.g. 'Task Sampletime'.")] string? sampleMode    = null,
        [Description("Sample time in ms.")] int?    sampleTimeMs  = null,
        [Description("Time offset in seconds.")] double? timeOffsetS   = null,
        [Description("Comment on the acquisition's symbol binding.")] string? symbolComment = null,
        [Description("Enable or disable the acquisition.")] bool?   enabled       = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName   = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_scope_acquisition_symbol",
            new { acqSelector, symbolName, targetPort, targetSystem, sampleMode, sampleTimeMs, timeOffsetS, symbolComment, enabled, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .SetScopeAcquisitionSymbol(acqSelector, symbolName, targetPort, targetSystem,
                        sampleMode, sampleTimeMs, timeOffsetS, symbolComment, enabled, projectName)), cancellationToken);

    // ---- twincat_set_scope_axis_mode ----------------------------------------

    [McpServerTool(Name = "twincat_set_scope_axis_mode", ReadOnly = false, Destructive = true)]
    [Description("Set a scope chart's Y-axis mode to overlaid or stacked. EXPERIMENTAL: not verified on a live TE130x. The value is read back; if the property is missing or differs, returns [Error] listing the chart's property keys.")]
    public string SetScopeAxisMode(
        [Description("Tree name of the chart.")] string chartName,
        [Description("'overlaid' (one shared Y-axis) or 'stacked' (one Y-axis per channel).")] string mode,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_scope_axis_mode", new { chartName, mode, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).SetScopeAxisMode(chartName, mode, projectName)), cancellationToken);

    // ---- twincat_set_scope_labels ------------------------------------------

    [McpServerTool(Name = "twincat_set_scope_labels", ReadOnly = false, Destructive = true)]
    [Description("Rename a scope chart or axis-group node, or set its titles. Supply at least one of name, chartTitle, axisTitle; a supplied title sets Show Title to Custom. Every value is read back; any that did not land returns [Error] naming what did and did not apply.")]
    public string SetScopeLabels(
        [Description("Name of the chart or axis-group node.")] string  nodeSelector,
        [Description("New node name (rename).")] string? name       = null,
        [Description("Title text, only where the node has a title property; TE130x 4026.22 has none on a chart or axis group (a chart shows its name: use `name`), so there it returns [Error].")] string? chartTitle = null,
        [Description("Y-axis title text.")] string? axisTitle  = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_scope_labels",
            new { nodeSelector, name, chartTitle, axisTitle, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .SetScopeLabels(nodeSelector, name, chartTitle, axisTitle, projectName)), cancellationToken);

    // ---- twincat_list_scope_channels ----------------------------------------

    [McpServerTool(Name = "twincat_list_scope_channels", ReadOnly = true)]
    [Description("List all charts, axis-groups, channels and acquisitions of the scope project. One line per node: kind (CHART|AXISGROUP|CHANNEL|ACQUISITION), tree path, bound acquisition (channels) and a flag if the label is still the template default.")]
    public string ListScopeChannels(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_scope_channels", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, entries) = new ScopeCoordinator(bridge.Dispatcher.Session).ListScopeChannels(projectName);
                if (McpErrors.IsError(summary) || entries.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var e in entries)
                {
                    string bound   = string.IsNullOrEmpty(e.BoundAcquisition) ? "" : $" → {e.BoundAcquisition}";
                    string warn    = e.AtTemplateDefault ? " [DEFAULT LABEL]" : "";
                    string label   = (e.DisplayLabel is not null && e.DisplayLabel != e.Name)
                                     ? $" (label: {e.DisplayLabel})" : "";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {e.Kind,-11} {e.Path}{bound}{label}{warn}");
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    // ---- twincat_get_scope_status ------------------------------------------

    [McpServerTool(Name = "twincat_get_scope_status", ReadOnly = true)]
    [Description("Report whether a scope (measurement) project exists in the open solution. Returns its chart/axis-group/channel/acquisition counts. Read-only.")]
    public string GetScopeStatus(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_scope_status", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, status) = new ScopeCoordinator(bridge.Dispatcher.Session).GetScopeStatus(projectName);
                if (McpErrors.IsError(summary) || status is null)
                    return summary;

                if (!status.ScopeProjectExists)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Project     : {status.ProjectName}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Charts      : {status.ChartCount}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Axis groups : {status.AxisCount}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Channels    : {status.ChannelCount}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Acquisitions: {status.AcquisitionCount}");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    // ---- twincat_export_scope_data -----------------------------------------

    [McpServerTool(Name = "twincat_export_scope_data", ReadOnly = false, Destructive = true)]
    [Description("Export recorded scope data to a file next to the open solution. Returns the written file path; [Error] if no data has been recorded yet.")]
    public string ExportScopeData(
        [Description("CSV (default), BINARY, TDMS, DAT or SVD.")] string format = "CSV",
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_export_scope_data", new { format, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).ExportScopeData(format, projectName)), cancellationToken);

    // ---- twincat_start_scope_record ----------------------------------------

    [McpServerTool(Name = "twincat_start_scope_record", ReadOnly = false, Destructive = true)]
    [Description("Start a TwinCAT Scope recording. Does not check the current state: call twincat_get_scope_record_state first. [Error] if the start fails or no scope project exists.")]
    public string StartScopeRecord(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_start_scope_record", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).StartScopeRecord(projectName)), cancellationToken);

    // ---- twincat_stop_scope_record -----------------------------------------

    [McpServerTool(Name = "twincat_stop_scope_record", ReadOnly = false, Destructive = true)]
    [Description("Stop the active TwinCAT Scope recording. The scope then enters 'Reply' state; save the data with twincat_save_scope_data or twincat_export_scope_data. [Error] if the stop fails.")]
    public string StopScopeRecord(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_stop_scope_record", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session).StopScopeRecord(projectName)), cancellationToken);

    // ---- twincat_get_scope_record_state ------------------------------------

    [McpServerTool(Name = "twincat_get_scope_record_state", ReadOnly = true)]
    [Description("Read the TwinCAT Scope recording state: Config, Record or Reply. 'Config' = not recording, 'Record' = recording, 'Reply' = stopped, data available. Call before start/stop. [Error] (never 'Config') when the state cannot be read. Read-only.")]
    public string GetScopeRecordState(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_get_scope_record_state", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, state) = new ScopeCoordinator(bridge.Dispatcher.Session)
                    .GetScopeRecordState(projectName);
                if (McpErrors.IsError(summary) || state is null)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  State       : {state.State}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  IsRecording : {state.IsRecording}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  HasData     : {state.HasData}");
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    // ---- twincat_save_scope_data -------------------------------------------

    [McpServerTool(Name = "twincat_save_scope_data", ReadOnly = false, Destructive = true)]
    [Description("Save recorded TwinCAT Scope data as a native .svdx file. Needs 'Reply' state (recording stopped). Returns the written file path; [Error] if no data has been recorded. For CSV/TDMS/DAT/BINARY use twincat_export_scope_data.")]
    public string SaveScopeData(
        [Description("Output .svdx path. Default ScopeData.svdx next to the open solution.")] string? path = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_save_scope_data", new { path, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(path, bridge.Dispatcher.Session, "path")
                ?? new ScopeCoordinator(bridge.Dispatcher.Session).SaveScopeData(path, projectName)), cancellationToken);

    // ---- twincat_set_scope_record_settings ---------------------------------

    [McpServerTool(Name = "twincat_set_scope_record_settings", ReadOnly = false, Destructive = true)]
    [Description("Set the scope's record settings (duration, ring buffer, auto-save, sync). Only supplied fields change; supply at least one. EXPERIMENTAL: not verified on a live TE130x; every value is read back and a field that did not land returns [Error] listing the scope root's property keys.")]
    public string SetScopeRecordSettings(
        [Description("Total recording duration in seconds, > 0.")] double? recordTimeS  = null,
        [Description("True = ring-buffer ('Restart Record') mode.")] bool?   ringbuffer   = null,
        [Description("File-store path for recorded data.")] string? fileStore    = null,
        [Description("NOT writable: read-only in TE130x (follows the trigger configuration). Supplying it returns [Error] for this field; omit it.")] string? recordMode   = null,
        [Description("'None', 'SVDX' or 'Export'.")] string? autoSaveMode = null,
        [Description("Auto-save output path.")] string? autoSavePath = null,
        [Description("Filename mask for auto-saved files.")] string? filenameMask = null,
        [Description("'Soft', 'Medium' or 'Hard'.")] string? syncMode     = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName  = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_set_scope_record_settings",
            new { recordTimeS, ringbuffer, fileStore, recordMode, autoSaveMode, autoSavePath, filenameMask, syncMode, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .SetScopeRecordSettings(recordTimeS, ringbuffer, fileStore, recordMode,
                        autoSaveMode, autoSavePath, filenameMask, syncMode, projectName)), cancellationToken);

    // ---- twincat_create_scope_trigger_group --------------------------------

    [McpServerTool(Name = "twincat_create_scope_trigger_group", ReadOnly = false, Destructive = false)]
    [Description("Create a trigger group under the scope project's Trigger node. The Trigger node must exist (never created). EXPERIMENTAL: not verified on a live TE130x. The action is read back; if it did not land, the group is deleted again and [Error] lists its property keys. Add conditions with twincat_add_scope_trigger_set.")]
    public string CreateScopeTriggerGroup(
        [Description("Event fired when all sets of the group are satisfied: 'StopRecord', 'StopDisplay', 'RestartDisplay', 'StartSubsave', 'StopSubsave' or 'EventMarker'.")] string action,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_create_scope_trigger_group", new { action, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .CreateScopeTriggerGroup(action, projectName)), cancellationToken);

    // ---- twincat_add_scope_trigger_set -------------------------------------

    [McpServerTool(Name = "twincat_add_scope_trigger_set", ReadOnly = false, Destructive = false)]
    [Description("Add a trigger set to an existing scope trigger group. EXPERIMENTAL: not verified on a live TE130x. For a ChannelSet supply threshold, edge and compare. Each condition is read back; any that did not land returns [Error] naming what did and did not apply.")]
    public string AddScopeTriggerSet(
        [Description("Trigger group name (from twincat_create_scope_trigger_group).")] string  groupName,
        [Description("'ChannelSet' (default) or 'DirectorySet'.")] string  setType     = "ChannelSet",
        [Description("NOT supported: the trigger channel cannot be set through the automation interface. Supplying it returns [Error] for this condition; pick the channel in the Scope editor.")] string? symbol      = null,
        [Description("Numeric trigger level (ChannelSet only).")] double? threshold   = null,
        [Description("'Rising', 'Falling' or 'Both' (ChannelSet only).")] string? edge        = null,
        [Description("'Greater', 'Less' or 'Equal' (ChannelSet only).")] string? compare     = null,
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_add_scope_trigger_set",
            new { groupName, setType, symbol, threshold, edge, compare, projectName }, () =>
            bridge.Dispatcher.Run(() =>
                new ScopeCoordinator(bridge.Dispatcher.Session)
                    .AddScopeTriggerSet(groupName, setType, symbol, threshold, edge, compare, projectName)), cancellationToken);

    // ---- twincat_list_scope_triggers ----------------------------------------

    [McpServerTool(Name = "twincat_list_scope_triggers", ReadOnly = true)]
    [Description("List the scope project's trigger groups and their trigger sets. Per group: name and action; per set: setType, symbol, threshold, edge, compare. EXPERIMENTAL: not verified on a live TE130x. Read-only.")]
    public string ListScopeTriggers(
        [Description("Measurement project name; required only when the solution has several (else [Error] lists them).")] string? projectName = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_list_scope_triggers", new { projectName }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var (summary, groups) = new ScopeCoordinator(bridge.Dispatcher.Session)
                    .ListScopeTriggers(projectName);
                if (McpErrors.IsError(summary) || groups.Count == 0)
                    return summary;

                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var g in groups)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  Group '{g.Name}' → action: {g.Action}");
                    foreach (var s in g.Sets)
                    {
                        string cond = new[]
                        {
                            s.Symbol    is not null ? $"symbol={s.Symbol}" : null,
                            s.Threshold is not null ? $"threshold={s.Threshold}" : null,
                            s.Edge      is not null ? $"edge={s.Edge}" : null,
                            s.Compare   is not null ? $"compare={s.Compare}" : null,
                        }.Where(x => x is not null).Aggregate("", (a, b) => a + (a.Length > 0 ? ", " : "") + b);
                        sb.AppendLine(CultureInfo.InvariantCulture, $"    Set [{s.SetType}]{(cond.Length > 0 ? ": " + cond : "")}");
                    }
                }
                return sb.ToString().TrimEnd();
            }), cancellationToken);
}
