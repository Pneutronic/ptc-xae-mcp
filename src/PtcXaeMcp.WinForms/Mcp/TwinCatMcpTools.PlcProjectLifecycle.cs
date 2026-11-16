using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

/// <summary>plc-project-lifecycle — MCP-only, no GUI tab.</summary>
internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_project_create", ReadOnly = false, Destructive = false)]
    [Description("Create a new PLC project under TIPC in the open solution. The name must not exist under TIPC yet. Logs the " +
                 "XAE out first, saves afterwards. Then build with twincat_build_plc.")]
    public string PlcProjectCreate(
        [Description("Name of the new PLC project, e.g. 'Plc2'.")] string name,
        [Description("'Standard PLC Template' (default), 'Empty PLC Template', another installed Beckhoff template name, or the absolute path of a custom .plcproj template (copied).")] string? template = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_project_create", new { name, template }, () =>
            bridge.Dispatcher.Run(() =>
                (template is not null && Path.IsPathFullyQualified(template)
                    ? McpPathGuard.CheckRequiredFile(template, bridge.Dispatcher.Session, "template")
                    : null)
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).CreateProject(name, template)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_project_add_existing", ReadOnly = false, Destructive = true)]
    [Description("Add an existing PLC project (.plcproj or .tpzip) to the open solution. A .plcproj project is named after its own " +
                 "<Name>; a .tpzip needs mode=COPY and a name. Refuses a name already under TIPC. Logs the XAE out first.")]
    public string PlcProjectAddExisting(
        [Description("Absolute path of the .plcproj or .tpzip.")] string path,
        [Description("COPY (copy the project folder next to the solution; source unchanged), MOVE (move it there; the source folder is gone) or IN_PLACE (reference it where it is).")] string mode,
        [Description("Optional for a .plcproj (must equal its own name); required for a .tpzip.")] string? name = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_project_add_existing", new { path, mode, name }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(path, bridge.Dispatcher.Session, "path")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).AddExisting(path, mode, name)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_project_export", ReadOnly = false, Destructive = true)]
    [Description("Export one PLC project of the open solution as a .tpzip archive. The target directory must exist; an existing " +
                 "file is overwritten. Verified by the written file. Counterpart: twincat_plc_project_import.")]
    public string PlcProjectExport(
        [Description("Name of the PLC project under TIPC, e.g. 'FixturePlc'.")] string plcProjectName,
        [Description("Absolute path of the .tpzip to write.")] string tpzipPath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_project_export", new { plcProjectName, tpzipPath }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(tpzipPath, bridge.Dispatcher.Session, "tpzipPath")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).Export(plcProjectName, tpzipPath)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_project_import", ReadOnly = false, Destructive = false)]
    [Description("Import a .tpzip archive as a new PLC project of the open solution. The name must not exist under TIPC yet. " +
                 "Logs the XAE out first, saves afterwards.")]
    public string PlcProjectImport(
        [Description("Absolute path of the .tpzip.")] string tpzipPath,
        [Description("Name of the new PLC project.")] string name,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_project_import", new { tpzipPath, name }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(tpzipPath, bridge.Dispatcher.Session, "tpzipPath")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).Import(tpzipPath, name)), cancellationToken);

    [McpServerTool(Name = "twincat_solution_save_archive", ReadOnly = false, Destructive = true)]
    [Description("Save the whole open solution as a .tszip archive ('Save <solution> as Archive'). Saves all documents first. " +
                 "The target directory must exist. Counterpart: twincat_solution_open_archive.")]
    public string SolutionSaveArchive(
        [Description("Absolute path of the .tszip to write.")] string tszipPath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_solution_save_archive", new { tszipPath }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(tszipPath, bridge.Dispatcher.Session, "tszipPath")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).SaveArchive(tszipPath)), cancellationToken);

    [McpServerTool(Name = "twincat_solution_open_archive", ReadOnly = false, Destructive = true)]
    [Description("Unpack a .tszip archive as a new solution <destDir>\\<name>.sln and open it. Needs a running XAE. An open " +
                 "solution is closed (without saving) only when closeCurrentFirst=true. Verified by the active solution path.")]
    public string SolutionOpenArchive(
        [Description("Absolute path of the .tszip.")] string tszipPath,
        [Description("Absolute directory to unpack into (new or empty).")] string destDir,
        [Description("Name of the new solution (without .sln).")] string name,
        [Description("Close the open solution first, without saving. Default false.")] bool closeCurrentFirst = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_solution_open_archive", new { tszipPath, destDir, name, closeCurrentFirst }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(tszipPath, bridge.Dispatcher.Session, "tszipPath")
                ?? McpPathGuard.CheckRequiredFile(destDir, bridge.Dispatcher.Session, "destDir")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).OpenArchive(tszipPath, destDir, name, closeCurrentFirst),
                afterSuccess: () => host.RequestEnvironmentRefresh("After OpenArchive")), cancellationToken);

    [McpServerTool(Name = "twincat_plc_instance_from_tmc", ReadOnly = false, Destructive = false)]
    [Description("Create a PLC instance (I/O image only, no source) from a .tmc file under TIPC. It is named after the .tmc " +
                 "module. Refuses a name already under TIPC. Logs the XAE out first, saves afterwards.")]
    public string PlcInstanceFromTmc(
        [Description("Absolute path of the .tmc file.")] string tmcPath,
        [Description("Optional expected instance name; must equal the .tmc module name.")] string? name = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_instance_from_tmc", new { name, tmcPath }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckRequiredFile(tmcPath, bridge.Dispatcher.Session, "tmcPath")
                ?? new PlcProjectLifecycleCoordinator(bridge.Dispatcher.Session).InstanceFromTmc(name, tmcPath)), cancellationToken);
}
