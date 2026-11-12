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

    [McpServerTool(Name = "twincat_visu_apply", ReadOnly = false, Destructive = true)]
    [Description("Create a PLC visualization or replace its whole page from JSON: { \"interface\": \"VAR_IN_OUT … END_VAR\" (optional; kept when " +
                 "omitted), \"elements\": [ { \"type\": \"Rectangle\", \"x\": 10, \"y\": 10, \"width\": 120, \"height\": 40, \"text\": \"Step %d\", " +
                 "\"textVariable\": \"MAIN.fb.nStep\", \"fillColor\": \"#C0FFC0\" }, … ] }. Types: Rectangle, RoundedRectangle, Ellipse, Line, Label, " +
                 "TextField, Button (twincat_visu_list_element_types lists their properties); a new element needs x, y, width and height. " +
                 "{ \"copyFrom\": <id>, … } copies an element of the page as it was (also a Frame or a Polygon, which cannot be created from scratch) " +
                 "and applies the other properties; a Frame copy takes \"frameParameters\": { \"<parameter>\": \"<expression>\" }. Every existing " +
                 "element is replaced. When the path does not exist, the visualization is created there (parent: a folder or the PLC project node). " +
                 "Element ids are assigned anew and listed in the result. " + VisuWriteRules)]
    public string VisuApply(
        [Description(VisuPathHelp + " The last part is the visualization's name.")] string path,
        [Description(VisuKindHelp)] string kind = "",
        [Description("The page as JSON (see the tool description). Alternative to 'pageFile'.")] string? page = null,
        [Description("Path to a file holding the page JSON. Alternative to 'page'.")] string? pageFile = null,
        [Description("Revision twincat_visu_read reported. When given, a different current revision refuses the write.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_apply", new { path, kind, page, pageFile, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
            {
                var guard = McpPathGuard.CheckOptionalFile(pageFile, bridge.Dispatcher.Session, "pageFile");
                if (guard is not null) return guard;
                var input = SourceTextInput.Resolve("page", page, pageFile);
                if (input.Error is not null) return input.Error;
                if (input.Text is null) return "[Error] page is required: the page JSON inline, or pageFile.";
                return new VisuCoordinator(bridge.Dispatcher.Session).Apply(path, kind, input.Text, expectedRevision)
                       + SourceTextInput.FormatNotes(input);
            }), cancellationToken);

    [McpServerTool(Name = "twincat_visu_edit_elements", ReadOnly = false, Destructive = true)]
    [Description("Add, change or remove single elements of an existing PLC visualization; every other element (also one the tools do not " +
                 "support) stays as it is. operations is a JSON array run in order, ids as twincat_visu_read reports them: " +
                 "{ \"op\": \"add\", \"element\": { \"type\": \"Label\", \"x\": 10, \"y\": 60, \"width\": 100, \"height\": 20, \"text\": \"Ready\" } }, " +
                 "{ \"op\": \"add\", \"element\": { \"copyFrom\": 4, \"x\": 300, \"frameParameters\": { \"fb\": \"MAIN.fbStation2\" } } }, " +
                 "{ \"op\": \"update\", \"id\": 3, \"set\": { \"fillColor\": \"#FF0000\", \"invisibleVariable\": \"NOT MAIN.bShow\" } } (null resets a " +
                 "property to XAE's default), { \"op\": \"remove\", \"id\": 5 }. interface optionally replaces the VAR_IN_OUT declaration. " +
                 "The whole request is validated before anything changes. " + VisuWriteRules)]
    public string VisuEditElements(
        [Description(VisuPathHelp)] string path,
        [Description(VisuKindHelp)] string kind = "",
        [Description("JSON array of operations (see the tool description). May be empty when only interface is given.")] string? operations = null,
        [Description("Optional new interface declaration of the visualization, e.g. \"VAR_IN_OUT\\n\\tfb : FB_Station;\\nEND_VAR\".")] string? @interface = null,
        [Description("Revision twincat_visu_read reported. When given, a different current revision refuses the write.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_edit_elements", new { path, kind, operations, @interface, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new VisuCoordinator(bridge.Dispatcher.Session).Edit(path, kind, operations ?? "[]", @interface, expectedRevision)), cancellationToken);
}
