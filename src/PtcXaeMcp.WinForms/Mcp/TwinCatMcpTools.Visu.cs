using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using PtcXaeMcp.WinForms.TwinCat.Visu;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PtcXaeMcp.WinForms;

// PLC visualizations (.TcVIS): read, create/replace a whole page, edit single elements. The content is not reachable
// through the Automation Interface, so the tools read and write the file and let XAE import it (VisuCoordinator).
internal sealed partial class TwinCatMcpTools
{
    private const string VisuPathHelp =
        "Caret-separated tree path of the visualization, e.g. 'TIPC^Plc^Plc Project^VISUs^VISU_Overview' (twincat_plc_list kind=VISU shows them).";

    private const string VisuKindHelp = "Element kind the node must have: VISU. Checked against the node's real type before anything else happens.";

    private const string VisuWriteRules =
        "kind is checked against the node's real type, the request and the file format are checked, and the PLC project must have a " +
        "visualization manager (twincat_plc_create kind=VISU subtype=MANAGER creates one) — all before the automatic logout; a refusal changes " +
        "nothing. Then the open editor of the visualization is closed, the object is deleted and the new file imported under the same name " +
        "(object id kept), and the project file is read back. The previous file is kept under %LOCALAPPDATA%\\ptc-xae-mcp\\visu and imported " +
        "again when XAE refuses the new one. Not saved — twincat_save_project afterwards. With expectedRevision (from twincat_visu_read) the " +
        "write is refused when the file changed since. Elements the tools do not support are kept unchanged. The result ends with 'Revision: …'.";

    [McpServerTool(Name = "twincat_visu_list_element_types", ReadOnly = true)]
    [Description("List the PLC visualization element types and properties the visu tools read and write, as JSON: per type whether it can be " +
                 "created, its properties (name, kind integer/text/expression/color, meaning), and what is not supported. Needs no XAE.")]
    public string VisuListElementTypes(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_list_element_types", new { }, VisuJson.Catalog, cancellationToken);

    [McpServerTool(Name = "twincat_visu_read", ReadOnly = true)]
    [Description("Read a PLC visualization (.TcVIS) structured, as JSON: name, file, interface (the VAR_IN_OUT declaration), extent, revision, " +
                 "whether the tools may write the file, and every element with id, type, position, size, text and text variable, colours, " +
                 "toggle-colour/invisible/movement/rotation/tap variables, tooltip, a Frame's referenced visualizations and parameters, and its " +
                 "input actions. An element the tools do not support appears with type, id and supported=false and is never changed. Properties " +
                 "absent from an element use XAE's default. kind must be VISU.")]
    public string VisuRead(
        [Description(VisuPathHelp)] string path,
        [Description(VisuKindHelp)] string kind = "",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_read", new { path, kind }, () =>
            bridge.Dispatcher.Run(() => new VisuCoordinator(bridge.Dispatcher.Session).Read(path, kind)), cancellationToken);
}
