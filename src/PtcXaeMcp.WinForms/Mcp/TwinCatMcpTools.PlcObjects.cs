using PtcXaeMcp.WinForms.Security;
using PtcXaeMcp.WinForms.TwinCat;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Globalization;

namespace PtcXaeMcp.WinForms;

// feature/plc-objects: one schema for every manipulation of every PLC element — generic verbs plus a
// mandatory `kind` that is checked against the node's real ItemType.
// Reading verbs (list, read) and writing verbs (create, write, delete, rename) stay separate tools:
// McpServerTests.NonMutatingTools decides per tool name whether a test may call it against an open solution.
internal sealed partial class TwinCatMcpTools
{
    private const string PlcKindHelp =
        "kind: POU | INTERFACE | DUT | GVL | MEMBER | FOLDER, or TEXTLIST | IMAGEPOOL | VISU | RECIPE | UML | TASKREF " +
        "(list/create/rename/delete only; read/write/entries answer [Error]). With a path, kind must equal the node's real type, " +
        "checked before anything else ([Error] otherwise). Libraries: twincat_*_library_* tools.";

    [McpServerTool(Name = "twincat_plc_list", ReadOnly = true)]
    [Description("List PLC elements (POU, INTERFACE, DUT, GVL, FOLDER) of the open PLC project. " +
                 "kind filters; parentPath limits to a subtree — under a POU, INTERFACE or PROPERTY it lists the MEMBERs " +
                 "(METHOD, ACTION, PROPERTY, Get/Set), which kind=MEMBER requires. One line per element: kind/subtype, tree path, " +
                 "language. Paged with limit/offset (last line: total, has_more, next_offset). " +
                 PlcKindHelp)]
    public string PlcList(
        [Description("Optional kind filter, e.g. POU, DUT, MEMBER.")] string? kind = null,
        [Description("Optional caret-separated tree path to list below. Required for kind=MEMBER.")] string? parentPath = null,
        [Description("False: only direct children of parentPath. Default true.")] bool recursive = true,
        [Description("Max elements; 0 (default) = all.")] int limit = 0,
        [Description("Elements to skip (next_offset of the previous page). Default 0.")] int offset = 0,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_list", new { kind, parentPath, recursive, limit, offset }, () =>
            Mcp.McpPaging.Validate(limit, offset) ?? bridge.Dispatcher.Run(() =>
            {
                var (summary, entries) = new PlcObjectCoordinator(bridge.Dispatcher.Session).List(kind, parentPath, recursive);
                if (McpErrors.IsError(summary) || entries.Count == 0)
                    return summary;

                var page = Mcp.McpPaging.Apply(entries, limit, offset);
                var sb = new StringBuilder();
                sb.AppendLine(summary);
                foreach (var e in page.Items)
                {
                    string what = e.Subtype is null ? e.Kind : $"{e.Kind}/{e.Subtype}";
                    string lang = string.IsNullOrEmpty(e.Language) ? "" : $" [{e.Language}]";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {what,-24} {e.Path}{lang}");
                }
                if (page.Paged) sb.AppendLine(page.Footer());
                return sb.ToString().TrimEnd();
            }), cancellationToken);

    [McpServerTool(Name = "twincat_plc_create", ReadOnly = false, Destructive = false)]
    [Description("Create a PLC element (POU, DUT, GVL, member, folder, …) under parentPath. kind/subtype and parent: " +
                 "POU/PROGRAM|FUNCTION|FUNCTION_BLOCK, DUT/STRUCT|ENUM|UNION|ALIAS (ALIAS needs a 'TYPE <name> : <baseType>; END_TYPE' " +
                 "declaration), GVL/NORMAL|PARAMLIST (persistent = a 'VAR_GLOBAL PERSISTENT' section in a NORMAL GVL's declaration), INTERFACE; " +
                 "MEMBER/METHOD|ACTION|PROPERTY|GET|SET under a POU or INTERFACE (GET/SET are named Get/Set; TRANSITION under a POU: ST, " +
                 "implementation only); FOLDER under a folder, the PLC project node, a POU or INTERFACE; TEXTLIST/LOCAL, IMAGEPOOL/LOCAL, " +
                 "UML/CLASSDIAGRAM, VISU/MANAGER|VISUALIZATION, RECIPE/MANAGER under a folder or the PLC project node; VISU/TARGET|WEB under a " +
                 "visualization manager; RECIPE/DEFINITION under a recipe manager; TASKREF/PROGRAM under a PLC task node (name = the PROGRAM). " +
                 "The global text list/image pool, text-list enumerations and trends cannot be created. declaration (not FOLDER/ACTION) and " +
                 "implementation (POU, METHOD, ACTION, TRANSITION, GET, SET; a PROPERTY's code goes into its accessors) fill the new element, " +
                 "verified like twincat_plc_write. A parameter that does not apply is an [Error]. Logs the XAE out first (refuses if that fails). " +
                 "Returns the new tree path. A new GVL, unused FB or function is only compiled once a task's program uses it (or with " +
                 "{attribute 'linkalways'}); verify with twincat_build_plc(expectSymbols=[...]). " + PlcKindHelp)]
    public string PlcCreate(
        [Description("Element kind (see the tool description), e.g. POU, DUT, GVL, MEMBER, FOLDER.")] string kind,
        [Description("Caret-separated tree path of the parent (see the tool description per kind).")] string parentPath,
        [Description("Name of the new element.")] string name,
        [Description("Subtype, e.g. FUNCTION_BLOCK, STRUCT, METHOD. Required for POU and MEMBER; DUT defaults to STRUCT, GVL to NORMAL.")] string? subtype = null,
        [Description("Optional declaration; TwinCAT's default skeleton when omitted.")] string? declaration = null,
        [Description("Return type for FUNCTION, METHOD, PROPERTY. Default BOOL.")] string? returnType = null,
        [Description("ST, IL, SFC, FBD, CFC or LD (POUs and members). Default ST.")] string? implLanguage = null,
        [Description("PROPERTY only: GET, SET or BOTH (default).")] string? accessors = null,
        [Description("Optional body written right after creation: ST text, or XAE's XML for SFC/FBD/CFC/LD.")] string? implementation = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_create",
            new { kind, subtype, parentPath, name, declaration, returnType, implLanguage, accessors, implementation }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .Create(kind, subtype, parentPath, name, declaration, returnType, implLanguage, accessors, implementation)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_read", ReadOnly = true)]
    [Description("Read a PLC element's declaration and implementation by tree path. Only existing sections are returned " +
                 "(DUT/GVL/INTERFACE: no implementation; ACTION/TRANSITION: no declaration), with the language; SFC/FBD/CFC/LD implementations " +
                 "are XAE's native XML. The first line ends with 'Revision: <16 hex digits>' — pass it as expectedRevision to a later write " +
                 "so the write is refused if the element changed meanwhile. paths reads several elements of one kind: each under " +
                 "'=== <path> ===', a failing one as its [Error]; the first line counts read and failed. " + PlcKindHelp)]
    public string PlcRead(
        [Description("Caret-separated tree path of the element (as twincat_plc_list shows). Give path or paths.")] string? path = null,
        [Description("Kind the node must have, e.g. POU, DUT, GVL, MEMBER.")] string kind = "",
        [Description("Also open the element in the XAE editor, best-effort, behind the user's work unless bringToFront. Not with paths. Default false.")] bool openInEditor = false,
        [Description(BringToFrontHelp)] bool bringToFront = false,
        [Description("Up to 50 tree paths of the same kind, instead of path.")] string[]? paths = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_read", new { path, kind, openInEditor, bringToFront, paths }, () =>
        {
            bool one = !string.IsNullOrWhiteSpace(path);
            bool many = paths is { Length: > 0 };
            if (one == many)
                return "[Error] Give either path (one element) or paths (several elements), not both and not neither.";
            if (string.IsNullOrWhiteSpace(kind))
                return "[Error] kind is required: POU, INTERFACE, DUT, GVL, MEMBER or FOLDER.";
            if (many && paths!.Length > MaxBatchPaths)
                return $"[Error] paths takes at most {MaxBatchPaths} elements (got {paths.Length}); read them in several calls.";
            if (many && openInEditor)
                return "[Error] openInEditor works with path only, not with paths.";

            return bridge.Dispatcher.Run(() =>
            {
                var coordinator = new PlcObjectCoordinator(bridge.Dispatcher.Session);
                if (one)
                {
                    if (openInEditor)
                    {
                        try { bridge.Dispatcher.Session.OpenPlcObject(path!, bringToFront); } catch (Exception ex) when (!ComCall.IsFinal(ex)) { } // a blocked XAE fails the tool now, not a second time in the read
                    }
                    return ReadOne(coordinator, path!, kind);
                }

                var sb = new StringBuilder();
                int failed = 0;
                var parts = new List<string>();
                foreach (var p in paths!)
                {
                    string text;
                    // One element that throws must not cost the others; a blocked XAE (final) still ends the call.
                    try { text = ReadOne(coordinator, p, kind); }
                    catch (Exception ex) when (!ComCall.IsFinal(ex)) { text = McpErrors.EnsureError(ComCall.Describe(ex)); }
                    if (McpErrors.IsError(text)) failed++;
                    parts.Add($"=== {p} ===\n{text}");
                }
                if (failed == paths.Length)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"[Error] None of the {paths.Length} elements could be read.");
                else
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Read {paths.Length - failed} of {paths.Length} elements ({failed} failed).");
                foreach (var part in parts) sb.AppendLine(part);
                return sb.ToString().TrimEnd();
            });
        }, cancellationToken);

    /// <summary>The most elements one <c>twincat_plc_read(paths)</c> call reads.</summary>
    internal const int MaxBatchPaths = 50;

    private static string ReadOne(PlcObjectCoordinator coordinator, string path, string kind)
    {
        var (summary, content) = coordinator.Read(path, kind);
        if (McpErrors.IsError(summary) || content is null)
            return summary;

        var sb = new StringBuilder();
        sb.AppendLine(summary);
        if (content.HasDeclaration)
        {
            sb.AppendLine("--- Declaration ---");
            sb.AppendLine(content.Declaration);
        }
        if (content.HasImplementation)
        {
            sb.AppendLine("--- Implementation ---");
            sb.AppendLine(content.Implementation);
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "twincat_plc_write", ReadOnly = false, Destructive = true)]
    [Description("Replace the declaration and/or implementation of an existing PLC element. A null section stays unchanged; " +
                 "give each section inline or as a file, not both. Implementation: text for ST/IL; for SFC/FBD/CFC/LD exactly XAE's XML " +
                 "as twincat_plc_read returns it (root <NWL> for LD/FBD, <SFC>, <CFC>). A POU/INTERFACE/GVL/DUT declaration containing a " +
                 "METHOD/PROPERTY/ACTION header is refused (create members with twincat_plc_create). Refused with [Error] before the logout, " +
                 "nothing changed: wrong kind, a section the element lacks (implementation of a DUT/GVL/INTERFACE, declaration of an ACTION), " +
                 "invalid graphical XML, a stale expectedRevision. Logs the XAE out first (refused if that fails); verified by an independent " +
                 "read-back; the result ends with the new 'Revision: …'. " + PlcKindHelp)]
    public string PlcWrite(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Kind the node must have, e.g. POU, DUT, GVL, MEMBER.")] string kind,
        [Description("New declaration; null = unchanged. Or declarationFile.")] string? declaration = null,
        [Description("New implementation; null = unchanged. Or implementationFile.")] string? implementation = null,
        [Description("File whose contents become the declaration.")] string? declarationFile = null,
        [Description("File whose contents become the implementation.")] string? implementationFile = null,
        [Description("Revision from twincat_plc_read; a different current revision refuses the write. Omit to write unconditionally.")] string? expectedRevision = null,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_write",
            new { path, kind, declaration, implementation, declarationFile, implementationFile, expectedRevision }, () =>
            bridge.Dispatcher.Run(() =>
                McpPathGuard.CheckOptionalFile(declarationFile, bridge.Dispatcher.Session, "declarationFile")
                ?? McpPathGuard.CheckOptionalFile(implementationFile, bridge.Dispatcher.Session, "implementationFile")
                ?? new PlcObjectCoordinator(bridge.Dispatcher.Session)
                    .Write(path, kind, declaration, implementation, declarationFile, implementationFile, expectedRevision)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_delete", ReadOnly = false, Destructive = true)]
    [Description("Delete a PLC element by tree path. Refuses the PLC project root, the PLC instance, References and nodes " +
                 "whose type cannot be read. A FOLDER must be empty unless recursive=true. Logs the XAE out first (refused if that fails); " +
                 "the delete is verified by lookup. Deleting something referenced elsewhere may break the build — check with " +
                 "twincat_build_plc. " + PlcKindHelp)]
    public string PlcDelete(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Kind the node must have, e.g. POU, DUT, GVL, MEMBER, FOLDER.")] string kind,
        [Description("FOLDER only: true deletes it with everything in it. Default false.")] bool recursive = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_delete", new { path, kind, recursive }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).Delete(path, kind, recursive)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_rename", ReadOnly = false, Destructive = true)]
    [Description("Rename a PLC element by tree path; returns the new path. Get/Set accessors cannot be renamed. Logs the XAE " +
                 "out first (refused if that fails). If TwinCAT asks whether to update all references, the tool answers per updateReferences " +
                 "and reports whether the prompt appeared and what was answered. Rebuild afterwards. " + PlcKindHelp)]
    public string PlcRename(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Kind the node must have, e.g. POU, DUT, GVL, MEMBER, FOLDER.")] string kind,
        [Description("New name.")] string newName,
        [Description(UpdateReferencesHelp)] bool updateReferences = false,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_rename", new { path, kind, newName, updateReferences }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).Rename(path, kind, newName, updateReferences)), cancellationToken);

    [McpServerTool(Name = "twincat_plc_move", ReadOnly = false, Destructive = true)]
    [Description("Move a PLC element into another folder, keeping its object GUIDs. kind POU, INTERFACE, DUT or GVL: target is " +
                 "a FOLDER of the same PLC project (not inside a POU) or the PLC project node. kind MEMBER (METHOD, ACTION, PROPERTY, " +
                 "TRANSITION): target is a FOLDER inside the same POU/INTERFACE or that POU/INTERFACE itself. The target must exist (create " +
                 "it with twincat_plc_create kind=FOLDER). Works by PLCopen export, delete and re-import (for a MEMBER: of its whole " +
                 "POU/INTERFACE); the XAE is not brought to the front. Refused before the logout, nothing changed: wrong kind, missing, " +
                 "non-folder or current target, name already in the target, member target outside its POU, Get/Set accessor. Logs the XAE " +
                 "out first (refused if that fails). Verified afterwards (new path resolves, old one gone, text and GUIDs unchanged); a " +
                 "failure after the delete re-imports the original and says what happened. Exports are kept under " +
                 "%LOCALAPPDATA%\\ptc-xae-mcp\\move. Not saved — call twincat_save_project afterwards.")]
    public string PlcMove(
        [Description("Caret-separated tree path of the element.")] string path,
        [Description("Kind the node must have: POU, INTERFACE, DUT, GVL or MEMBER (checked first).")] string kind,
        [Description("Caret-separated tree path of the existing target folder or the PLC project node; for MEMBER a folder in the same POU/INTERFACE or that POU/INTERFACE.")] string targetFolderPath,
        CancellationToken cancellationToken = default)
        => bridge.Activity.Trace("twincat_plc_move", new { path, kind, targetFolderPath }, () =>
            bridge.Dispatcher.Run(() =>
                new PlcObjectCoordinator(bridge.Dispatcher.Session).Move(path, kind, targetFolderPath)), cancellationToken);

    internal const string UpdateReferencesHelp =
        "Answer to TwinCAT's prompt to update all references, if shown: false (default) = \"Nein\", only the element is renamed " +
        "(callers keep the old name); true = \"Ja\", references in the project are rewritten.";
}
