using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_project_info", ReadOnly = false, Destructive = false)]
    [Description("Read or set the Project Information of a PLC project (the XAE's project properties > Common): Title, Version, Company, " +
                 "Author, DefaultNamespace, Description, Released. Without values it only reads and lists every field. With values it sets " +
                 "exactly those (an empty string clears a field), leaves the others unchanged, verifies them by read-back and saves the " +
                 "project. A library needs at least the Title — twincat_save_as_library fails with \"'Title' not specified\" without it — " +
                 "and normally a Version (four numbers, e.g. 1.0.0.0; anything else is refused before the write). plcProjectName picks " +
                 "one PLC of several.")]
    public string PlcProjectInfo(
        [Description("Name of the PLC (or its project). Leave empty for the first PLC project.")] string? plcProjectName = null,
        [Description("Title of the project/library. Required to save it as a library.")] string? title = null,
        [Description("Version as four numbers, e.g. 1.2.0.0.")] string? version = null,
        [Description("Company (distributor of the library).")] string? company = null,
        [Description("Author.")] string? author = null,
        [Description("Default namespace of the library.")] string? defaultNamespace = null,
        [Description("Description.")] string? description = null,
        [Description("Released flag of the library version.")] bool? released = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_project_info",
            new { plcProjectName, title, version, company, author, defaultNamespace, description, released }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcProjectInfoCoordinator(bridge.Dispatcher.Session)
                    .Run(plcProjectName, title, version, company, author, defaultNamespace, description, released)), cancellationToken);
}
