using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// hmi-views — create and change files of a TwinCAT HMI project (user controls, contents,
// functions, imported documents) via the HMI Automation Interface.
// Paths are project-relative with '/', e.g. 'UserControls/PtcCamCurve/PtcCamCurve.usercontrol'.
internal sealed partial class TwinCatMcpTools
{
    private const string HmiPathParam =
        "Project-relative path with '/', e.g. 'UserControls/Foo/Foo.usercontrol'. No drive, leading '/' or '..'.";

    [McpServerTool(Name = "twincat_hmi_read_file", ReadOnly = true)]
    [Description("Read the source text of a file in a TwinCAT HMI project. Views, contents, user controls, " +
                 ".usercontrol.json, functions (.js/.function.json), CSS etc. JSON: { path, length, lines, source }.")]
    public string HmiReadFile(
        [Description(HmiPathParam)] string path,
        [Description(HmiProjectParam)] string? hmiProject = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_read_file", new { hmiProject, path }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).ReadFile(hmiProject, path))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_add_item", ReadOnly = false, Destructive = false)]
    [Description("Create a new file in a TwinCAT HMI project from a TE2000 item template. Creates missing " +
                 "folders; refuses an existing file. kind: view, content, usercontrol (+ .usercontrol.json), " +
                 "function_js / function_ts (+ .function.json, namespace = project name), codebehind_js, codebehind_ts, " +
                 "css, html, js, json, ts, text (.txt), xml. html and text start empty, view as the vendor skeleton. " +
                 ConfirmNote + " JSON lists the created paths.")]
    public string HmiAddItem(
        [Description("Template kind (list in the tool description).")] string kind,
        [Description("Target folder, project-relative with '/', e.g. 'UserControls/Foo'. Must be passed; null or " +
                     "empty = project root.")]
        string? folder,
        [Description("File name without extension (the template adds it), e.g. 'Foo'.")] string name,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_add_item", new { hmiProject, kind, folder, name, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .AddItem(hmiProject, folder, name, kind, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_write_file", ReadOnly = false, Destructive = true)]
    [Description("Replace the source text of an existing file in a TwinCAT HMI project. Saves and verifies by " +
                 "read-back. Identical content = 'Nothing to do', no popup. " + ConfirmNote)]
    public string HmiWriteFile(
        [Description(HmiPathParam)] string path,
        [Description("Complete new content. Alternative to sourceFile.")] string? source = null,
        [Description("Absolute path of a local file (max 5 MB) to write. Alternative to source.")] string? sourceFile = null,
        [Description("Keep a JSON file's existing \"$schema\" value (it points into the project's Packages folder). Default true.")] bool keepSchema = true,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_write_file",
            new { hmiProject, path, sourceLength = source?.Length, sourceFile, keepSchema, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .WriteFile(hmiProject, path, source, sourceFile, keepSchema, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_import_file", ReadOnly = false, Destructive = false)]
    [Description("Import an external file (HTML, image, PDF, JS library) into an HMI project. Creates a " +
                 "missing folder; refuses an existing target. If TE2000 reports an error although the file was added, " +
                 "it counts only when the copy on disk is byte-identical (engineError, verifiedOnDisk). For .js/.css " +
                 "the result shows the dependencyFiles entry in Properties/tchmiconfig.json (dependencyType); run " +
                 "twincat_hmi_build afterwards so the live view loads it. " + ConfirmNote)]
    public string HmiImportFile(
        [Description("Absolute path of the file to import.")] string sourcePath,
        [Description("Target folder, project-relative with '/'. Empty = project root.")] string? folder = null,
        [Description("Target file name. Default: source file name.")] string? name = null,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_import_file", new { hmiProject, sourcePath, folder, name, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .ImportFile(hmiProject, sourcePath, folder, name, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_build", ReadOnly = false, Destructive = false)]
    [Description("Build a TwinCAT HMI project so changes reach the server's live view. Needed for new user " +
                 "controls, functions and contents. Changes no source file (no popup). On failure see " +
                 "twincat_get_error_list.")]
    public string HmiBuild(
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description("Solution configuration. Default 'Debug'.")] string? configuration = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_build", new { hmiProject, configuration }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session).Build(hmiProject, configuration))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_apply_bundle", ReadOnly = false, Destructive = true)]
    [Description("Create/write a package of HMI files from a manifest with one confirmation. Manifest JSON: " +
                 "{ \"name\": \"…\", \"entries\": [ { \"path\": \"UserControls/X/X.usercontrol\", \"kind\": " +
                 "\"usercontrol\", \"source\": \"X.usercontrol\" }, { \"path\": \"UserControls/X/X.usercontrol.json\", " +
                 "\"source\": \"X.usercontrol.json\" } ] }. 'source' is relative to the manifest. Existing files are " +
                 "replaced; missing ones are created from 'kind' first (not needed for a file an earlier entry's " +
                 "template creates, e.g. .usercontrol.json, .function.json). JSON files keep the project's \"$schema\". " +
                 "All validated before the popup; entries then run in order and stop at the first error, reporting " +
                 "what was written. " + ConfirmNote)]
    public string HmiApplyBundle(
        [Description("Absolute path of the bundle manifest (JSON).")] string manifestPath,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_apply_bundle", new { hmiProject, manifestPath, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .ApplyBundle(hmiProject, manifestPath, confirmTimeoutSeconds))), cancellationToken);

    [McpServerTool(Name = "twincat_hmi_delete_item", ReadOnly = false, Destructive = true)]
    [Description("Delete a file or folder of a TwinCAT HMI project, from project AND disk. No recycle bin. " +
                 "Refused without popup: project infrastructure (Server, Properties, Packages), the startup view and " +
                 "folders containing it; a missing item is reported without popup. The popup shows file/folder and " +
                 "the item count below it. " + ConfirmNote + " Success only when the item is gone afterwards.")]
    public string HmiDeleteItem(
        [Description(HmiPathParam)] string path,
        [Description(HmiProjectParam)] string? hmiProject = null,
        [Description(ConfirmTimeoutParam)] int confirmTimeoutSeconds = HmiCoordinator.DefaultConfirmTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_hmi_delete_item", new { hmiProject, path, confirmTimeoutSeconds }, () =>
            bridge.Dispatcher.Run(() =>
                HmiResult(new HmiCoordinator(bridge.Dispatcher.Session, host.HmiConfirmer)
                    .DeleteItem(hmiProject, path, confirmTimeoutSeconds))), cancellationToken);
}
