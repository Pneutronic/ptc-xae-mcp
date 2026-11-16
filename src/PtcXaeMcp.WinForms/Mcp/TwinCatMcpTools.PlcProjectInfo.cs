using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

internal sealed partial class TwinCatMcpTools
{
    [McpServerTool(Name = "twincat_plc_project_info", ReadOnly = false, Destructive = false)]
    [Description("Read or set the Project Information (Title, Version, …) of a PLC project. Fields (project properties > Common): " +
                 "Title, Version, Company, Author, DefaultNamespace, Description, Released. Without values it lists every field; with " +
                 "values it sets exactly those (\"\" clears one), verifies by read-back and saves the project. twincat_save_as_library " +
                 "needs a Title (else \"'Title' not specified\") and normally a Version.")]
    public string PlcProjectInfo(
        [Description("Name of the PLC (or its project); empty = the first PLC project.")] string? plcProjectName = null,
        [Description("Title of the project/library; required to save it as a library.")] string? title = null,
        [Description("Four numbers, e.g. 1.2.0.0; anything else is refused before the write.")] string? version = null,
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
