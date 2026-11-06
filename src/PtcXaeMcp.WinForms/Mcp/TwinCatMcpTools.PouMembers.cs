using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// PLCopen interchange. Members, deletion and renaming are the generic twincat_plc_* verbs
// (TwinCatMcpTools.PlcObjects.cs).
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plcopen_export", ReadOnly = false, Destructive = true)]
    [Description("Export one or more objects to a PLCopen XML file. selector is a ';'-separated list of dotted object names relative to the PLC project, e.g. \"POUs.FB_Foo;POUs.MAIN\" (a bare name without its container fails). filePath is the absolute path of the XML file to write.")]
    public string PlcOpenExport(
        [Description("Absolute path of the PLCopen XML file to write.")] string filePath,
        [Description("';'-separated list of dotted object names relative to the PLC project, e.g. \"POUs.FB_Foo;POUs.MAIN\" (a bare name without its container fails).")] string selector,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plcopen_export",
            new { filePath, selector }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(filePath, bridge.Dispatcher.Session, "filePath")
                ?? new PouMembersCoordinator(bridge.Dispatcher.Session).PlcOpenExport(filePath, selector)), cancellationToken);

    [McpServerTool(Name = "twincat_plcopen_import", ReadOnly = false, Destructive = true)]
    [Description("Import objects from a PLCopen XML file. conflictMode is None, Rename, Replace, or Skip and defaults to Skip (non-destructive). filePath is the absolute path of the XML file to read.")]
    public string PlcOpenImport(
        [Description("Absolute path of the PLCopen XML file to read.")] string filePath,
        [Description("Conflict resolution: None, Rename, Replace, or Skip. Defaults to Skip (non-destructive).")] string? conflictMode = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plcopen_import",
            new { filePath, conflictMode }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(filePath, bridge.Dispatcher.Session, "filePath")
                ?? new PouMembersCoordinator(bridge.Dispatcher.Session).PlcOpenImport(filePath, conflictMode)), cancellationToken);
}
