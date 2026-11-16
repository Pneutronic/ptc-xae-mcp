using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_coredump_list", ReadOnly = true)]
    [Description(
        "List the TwinCAT PLC core dumps of a Usermode Runtime instance. " +
        "File-based; needs no XAE or running runtime. Per dump: full path, write time, size, kind " +
        "(AutoException = runtime trap, Manual = engineer-made, CallStackOnly = no variable values) " +
        "and whether the .compileinfo needed to interpret it is present.")]
    public string CoreDumpList(
        [Description("UMR instance name, e.g. 'UmRT_Template'. Omit for all registered instances.")]
        string? instanceName = null,
        [Description("ADS port to filter by, e.g. 851. Omit for all ports.")]
        int? port = null,
        [Description("Full path of the PLC project folder (containing the .plcproj). " +
                     "Required to find manual dumps and the _CompileInfo folder.")]
        string? projectFolder = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_coredump_list",
            new { instanceName, port, projectFolder }, () =>
            bridge.Dispatcher.Run(() =>
                bridge.Dispatcher.Session.CoreDumpList(instanceName, port, projectFolder)), cancellationToken);

    [McpServerTool(Name = "twincat_coredump_analyze", ReadOnly = true)]
    [Description(
        "Analyze a TwinCAT PLC core dump file. Needs no running runtime. " +
        "Classifies the dump kind; computes the hang gap (last app-log timestamp vs dump write time: " +
        "a large gap means the PLC stalled, not trapped); extracts embedded state strings (app-log ring buffer, " +
        "last state-machine steps); lists active unconfirmed events from LoggedEvents.db and CurrentEventMessages.xml.")]
    public string CoreDumpAnalyze(
        [Description("Full path of the .core file. Omit to pick the newest dump (by instanceName/port).")]
        string? dumpPath = null,
        [Description("UMR instance name for picking the newest dump.")]
        string? instanceName = null,
        [Description("ADS port filter for picking the newest dump.")]
        int? port = null,
        [Description("Full path of the PLC project folder (with .plcproj, _CompileInfo, LoggedEvents.db, " +
                     "CurrentEventMessages.xml); needed for manual dumps and event data.")]
        string? projectFolder = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_coredump_analyze",
            new { dumpPath, instanceName, port, projectFolder }, () =>
            bridge.Dispatcher.Run(() =>
                bridge.Dispatcher.Session.CoreDumpAnalyze(dumpPath, instanceName, port, projectFolder)), cancellationToken);

    [McpServerTool(Name = "twincat_coredump_read_callstack", ReadOnly = true)]
    [Description(
        "Read the call stack of the core dump loaded in the XAE (best effort). " +
        "Returns POU and instance path per frame; the top-most is the exception location. " +
        "FRAGILE: the dump must already be loaded by hand (PLC > Core Dump > Load core dump); this tool " +
        "cannot load it. Returns an error when no dump is loaded or the XAE is unreachable; never invents a stack.")]
    public string CoreDumpReadCallStack(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_coredump_read_callstack", null, () =>
            bridge.Dispatcher.Run(() =>
                bridge.Dispatcher.Session.CoreDumpReadCallStack()), cancellationToken);
}
