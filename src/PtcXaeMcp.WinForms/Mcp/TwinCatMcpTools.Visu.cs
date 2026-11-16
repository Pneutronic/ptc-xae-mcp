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
        "Caret-separated tree path of the visualization (twincat_plc_list kind=VISU), e.g. 'TIPC^Plc^Plc Project^VISUs^VISU_Main'.";

    private const string VisuKindHelp = "Must be VISU; checked against the node's real type first.";

    private const string VisuWriteRules =
        "Checked before the logout, nothing changed on refusal: kind, request, file format, and that the PLC project has a visualization " +
        "manager (create one with twincat_plc_create kind=VISU subtype=MANAGER). Logs the XAE out first (refused if that fails), closes " +
        "the visualization's editor, deletes the object and imports the new file under the same name (object id kept). The previous file " +
        "is kept under %LOCALAPPDATA%\\ptc-xae-mcp\\visu and re-imported if XAE refuses the new one. A stale expectedRevision refuses the " +
        "write. Not saved — call twincat_save_project afterwards. The result ends with 'Revision: …'.";

    [McpServerTool(Name = "twincat_visu_list_element_types", ReadOnly = true)]
    [Description("List the visu element types and properties the visu tools support, as JSON. Per type: whether it can " +
                 "be created, its properties (name, kind integer/text/expression/color, meaning) and what is not supported. Needs no XAE.")]
    public string VisuListElementTypes(CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_list_element_types", new { }, VisuJson.Catalog, cancellationToken);

    [McpServerTool(Name = "twincat_visu_read", ReadOnly = true)]
    [Description("Read a PLC visualization (.TcVIS) as structured JSON. Returns name, file, interface (VAR_IN_OUT declaration), " +
                 "extent, revision, whether the tools may write it, and every element with id, type, position, size, texts, colours, " +
                 "colour/invisible/movement/rotation/tap/toggle variables, input actions and a Frame's visualizations and parameters. " +
                 "Unsupported elements show type, id and supported=false and are never changed. Absent properties use XAE's default.")]
    public string VisuRead(
        [Description(VisuPathHelp)] string path,
        [Description(VisuKindHelp)] string kind = "",
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_read", new { path, kind }, () =>
            bridge.Dispatcher.Run(() => new VisuCoordinator(bridge.Dispatcher.Session).Read(path, kind)), cancellationToken);

    [McpServerTool(Name = "twincat_visu_apply", ReadOnly = false, Destructive = true)]
    [Description("Create a PLC visualization or replace its whole page from JSON. Page: { \"interface\": \"VAR_IN_OUT … END_VAR\" " +
                 "(optional; kept when omitted), \"elements\": [ { \"type\": \"Rectangle\", \"x\": 10, \"y\": 10, \"width\": 120, \"height\": 40, " +
                 "\"text\": \"Step %d\", \"textVariable\": \"MAIN.fb.nStep\", \"fillColor\": \"#C0FFC0\", \"toggleVariable\": \"MAIN.bManual\" }, " +
                 "{ \"type\": \"Frame\", \"x\": 10, \"y\": 60, \"width\": 160, \"height\": 70, \"visualization\": \"VISU_Station\", " +
                 "\"parameters\": { \"fb\": \"MAIN.fbStation1\" } }, … ] }. Types: Rectangle, RoundedRectangle, Ellipse, Line, Label, " +
                 "TextField, Button, Frame (properties: twincat_visu_list_element_types). A new element needs x, y, width, height; a Frame " +
                 "also its visualization and a value for every VAR_IN_OUT of that visualization's interface. { \"copyFrom\": <id>, … } " +
                 "copies an element of these types and applies the other properties. Every existing element is replaced — Polygons and " +
                 "unsupported elements are lost (twincat_visu_edit_elements keeps them). A missing path creates the visualization (parent: " +
                 "a folder or the PLC project node). Element ids are reassigned and listed in the result. " + VisuWriteRules)]
    public string VisuApply(
        [Description(VisuPathHelp + " The last part is the visualization's name.")] string path,
        [Description(VisuKindHelp)] string kind = "",
        [Description("Page JSON (see the tool description). Or pageFile.")] string? page = null,
        [Description("File holding the page JSON. Or page.")] string? pageFile = null,
        [Description("Revision from twincat_visu_read; a different current revision refuses the write.")] string? expectedRevision = null,
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
    [Description("Add, change or remove single elements of an existing PLC visualization. Every other element, also unsupported " +
                 "ones, stays as it is. operations is a JSON array run in order, ids from twincat_visu_read: " +
                 "{ \"op\": \"add\", \"element\": { \"type\": \"Label\", \"x\": 10, \"y\": 60, \"width\": 100, \"height\": 20, \"text\": \"Ready\" } }, " +
                 "{ \"op\": \"add\", \"element\": { \"copyFrom\": 4, \"x\": 300, \"parameters\": { \"fb\": \"MAIN.fbStation2\" } } }, " +
                 "{ \"op\": \"update\", \"id\": 3, \"set\": { \"fillColor\": \"#FF0000\", \"invisibleVariable\": \"NOT MAIN.bShow\" } } (null resets a " +
                 "property to XAE's default), { \"op\": \"remove\", \"id\": 5 }. A Frame takes \"visualization\" and \"parameters\" as in " +
                 "twincat_visu_apply; a Polygon can only be removed. The whole request is validated before anything changes. " + VisuWriteRules)]
    public string VisuEditElements(
        [Description(VisuPathHelp)] string path,
        [Description(VisuKindHelp)] string kind = "",
        [Description("JSON array of operations (see the tool description); may be empty when only interface is given.")] string? operations = null,
        [Description("Optional new VAR_IN_OUT interface declaration, e.g. \"VAR_IN_OUT\\n\\tfb : FB_Station;\\nEND_VAR\".")] string? @interface = null,
        [Description("Revision from twincat_visu_read; a different current revision refuses the write.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_visu_edit_elements", new { path, kind, operations, @interface, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                new VisuCoordinator(bridge.Dispatcher.Session).Edit(path, kind, operations ?? "[]", @interface, expectedRevision)), cancellationToken);
}
